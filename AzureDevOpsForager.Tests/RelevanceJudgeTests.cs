using System.Collections.Generic;
using AzureDevOpsForager.Eval.Models;
using AzureDevOpsForager.Eval.Services;
using Xunit;

namespace AzureDevOpsForager.Tests;

/// <summary>
/// Tests for the relevance judge, which decides whether a returned search result is one of the
/// answers a human labelled relevant.
///
/// These matter more than their size suggests: the judge sits upstream of every metric, so a
/// matching bug does not produce an obviously wrong number, it produces a plausible one. The
/// near-miss cases (a labelled file name that is a substring of a different file name) are the
/// specific way this class can silently inflate an entire report.
/// </summary>
public class RelevanceJudgeTests
{
   #region Private Methods

   /// <summary>
   /// Builds a single-label golden query for the tests to judge against.
   /// </summary>
   /// <param name="filePath">The labelled file path.</param>
   /// <param name="chunkName">Optional labelled chunk name.</param>
   /// <param name="grade">The label's grade.</param>
   /// <returns>A golden query carrying exactly that one label.</returns>
   private static GoldenQuery QueryWith( string filePath, string? chunkName = null, int grade = 3 )
   {
      return new GoldenQuery
      {
         Id = "q1",
         Question = "test question",
         Relevant = new List<RelevantItem>
         {
            new RelevantItem { FilePath = filePath, ChunkName = chunkName, Grade = grade },
         },
      };
   }

   #endregion Private Methods

   #region Public Methods

   /// <summary>Verifies an exactly equal path is matched and earns the label's grade.</summary>
   [Fact]
   public void Judge_ExactPath_Matches()
   {
      var judgement = new RelevanceJudge().Judge( QueryWith( "src/Core/BasketService.cs" ), "src/Core/BasketService.cs", null );

      Assert.Equal( 3, judgement.Grade );
      Assert.Equal( 0, judgement.GoldenItemIndex );
   }

   /// <summary>
   /// Verifies separator style and casing do not affect matching, so the same golden set works
   /// against a Windows-rooted index and a POSIX-rooted one.
   /// </summary>
   [Fact]
   public void Judge_DifferentSeparatorAndCase_Matches()
   {
      var judgement = new RelevanceJudge().Judge( QueryWith( "src/Core/BasketService.cs" ), @"SRC\CORE\BasketService.cs", null );

      Assert.Equal( 3, judgement.Grade );
   }

   /// <summary>
   /// Verifies a repo-relative label matches an absolute indexed path, which is the case that makes
   /// a golden set survive a change in how the indexer records paths.
   /// </summary>
   [Fact]
   public void Judge_RelativeLabelAgainstAbsolutePath_Matches()
   {
      var judgement = new RelevanceJudge().Judge(
         QueryWith( "src/Core/BasketService.cs" ), @"C:\repos\eShop\src\Core\BasketService.cs", null );

      Assert.Equal( 3, judgement.Grade );
   }

   /// <summary>
   /// Verifies the suffix match respects segment boundaries. Without this, "Foo.cs" would match
   /// "MyFoo.cs" and every score in the report would drift upward for the best possible reason:
   /// the harness marking wrong answers correct.
   /// </summary>
   [Fact]
   public void Judge_SuffixThatIsNotASegment_DoesNotMatch()
   {
      var judgement = new RelevanceJudge().Judge( QueryWith( "src/Core/Service.cs" ), "src/Core/BasketService.cs", null );

      Assert.Equal( 0, judgement.Grade );
      Assert.Equal( -1, judgement.GoldenItemIndex );
   }

   /// <summary>Verifies a labelled chunk name narrows the match from the file to the member.</summary>
   [Fact]
   public void Judge_ChunkNameLabelled_RequiresChunkToMatch()
   {
      var query = QueryWith( "src/Core/BasketService.cs", "AddItemToBasket" );
      var judge = new RelevanceJudge();

      Assert.Equal( 3, judge.Judge( query, "src/Core/BasketService.cs", "AddItemToBasket" ).Grade );
      Assert.Equal( 0, judge.Judge( query, "src/Core/BasketService.cs", "DeleteBasket" ).Grade );
   }

   /// <summary>Verifies an unlabelled chunk name accepts any chunk from the labelled file.</summary>
   [Fact]
   public void Judge_ChunkNameNotLabelled_AcceptsAnyChunk()
   {
      var judgement = new RelevanceJudge().Judge( QueryWith( "src/Core/BasketService.cs" ), "src/Core/BasketService.cs", "Anything" );

      Assert.Equal( 3, judgement.Grade );
   }

   /// <summary>
   /// Verifies that when a result satisfies several labels, the highest grade wins, so a result that
   /// genuinely is the best answer is not scored as the weaker label it also happens to match.
   /// </summary>
   [Fact]
   public void Judge_MultipleMatchingLabels_TakesHighestGrade()
   {
      var query = new GoldenQuery
      {
         Id = "q1",
         Question = "test",
         Relevant = new List<RelevantItem>
         {
            new RelevantItem { FilePath = "src/Core/BasketService.cs", Grade = 1 },
            new RelevantItem { FilePath = "src/Core/BasketService.cs", ChunkName = "AddItemToBasket", Grade = 3 },
         },
      };

      var judgement = new RelevanceJudge().Judge( query, "src/Core/BasketService.cs", "AddItemToBasket" );

      Assert.Equal( 3, judgement.Grade );
      Assert.Equal( 1, judgement.GoldenItemIndex );
   }

   /// <summary>Verifies the ranked overload preserves rank order and tolerates a missing chunk-name list.</summary>
   [Fact]
   public void JudgeRanked_PreservesOrderAndToleratesMissingChunkNames()
   {
      var query = QueryWith( "src/Core/BasketService.cs" );
      var paths = new List<string> { "src/Core/Other.cs", "src/Core/BasketService.cs" };

      var judgements = new RelevanceJudge().JudgeRanked( query, paths, null );

      Assert.Equal( 2, judgements.Count );
      Assert.Equal( 0, judgements[0].Grade );
      Assert.Equal( 3, judgements[1].Grade );
   }

   #endregion Public Methods
}
