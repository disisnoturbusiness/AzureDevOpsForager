using AzureDevOpsForager.Core.Services.Reranking;
using AzureDevOpsForager.Core.Services.Search;
using Xunit;

namespace AzureDevOpsForager.Tests;

/// <summary>
/// Covers <see cref="HybridSearchService.ApplyRelevanceGate"/> — the cross-encoder score floor that
/// decides how many results a query gets back.
///
/// This is the control that lets an unanswerable question return nothing instead of a full page of
/// confident-looking noise, so the cases that matter most here are the boundary ones: everything
/// filtered (must yield empty, NOT a silent fallback to the unfiltered list) and a floor of zero
/// (must be a no-op). Both are easy for a refactor to get subtly wrong in a way no user would
/// notice quickly, since the failure is extra results rather than an error.
/// </summary>
public class RelevanceGateTests
{
   /// <summary>Builds a row shaped like the ones FetchFusedRowsAsync produces, keyed by a marker path.</summary>
   private static (string FilePath, string Content, Dictionary<string, string> Meta) Row( string path ) =>
      (path, $"content of {path}", new Dictionary<string, string> { ["_file_path"] = path });

   private static List<(string FilePath, string Content, Dictionary<string, string> Meta)> Rows( params string[] paths ) =>
      paths.Select( Row ).ToList();

   [Fact]
   public void BelowFloorAreDropped_AboveFloorSurvive()
   {
      var rows = Rows( "a.cs", "b.cs", "c.cs" );
      var reranked = new List<RerankerResult>
      {
         new( 0, 0.90 ),      // clearly relevant
         new( 1, 0.00012 ),   // noise
         new( 2, 0.05 ),      // relevant
      };

      var (ordered, dropped) = HybridSearchService.ApplyRelevanceGate( reranked, rows, 0.001 );

      Assert.Equal( 1, dropped );
      Assert.Equal( new[] { "a.cs", "c.cs" }, ordered.Select( r => r.FilePath ) );
   }

   [Fact]
   public void EverythingBelowFloor_ReturnsEmptyRatherThanFallingBack()
   {
      // The unanswerable-question case. Returning the input rows here would silently defeat the
      // whole point of the gate, so this asserts the empty result explicitly.
      var rows = Rows( "a.cs", "b.cs" );
      var reranked = new List<RerankerResult> { new( 0, 0.0 ), new( 1, 0.00001 ) };

      var (ordered, dropped) = HybridSearchService.ApplyRelevanceGate( reranked, rows, 0.001 );

      Assert.Empty( ordered );
      Assert.Equal( 2, dropped );
   }

   [Fact]
   public void ZeroFloor_KeepsEverything()
   {
      var rows = Rows( "a.cs", "b.cs" );
      var reranked = new List<RerankerResult> { new( 0, 0.0 ), new( 1, 0.0 ) };

      var (ordered, dropped) = HybridSearchService.ApplyRelevanceGate( reranked, rows, 0.0 );

      Assert.Equal( 2, ordered.Count );
      Assert.Equal( 0, dropped );
   }

   [Fact]
   public void FloorIsInclusive_ScoreExactlyAtFloorSurvives()
   {
      var rows = Rows( "a.cs" );
      var reranked = new List<RerankerResult> { new( 0, 0.001 ) };

      var (ordered, dropped) = HybridSearchService.ApplyRelevanceGate( reranked, rows, 0.001 );

      Assert.Single( ordered );
      Assert.Equal( 0, dropped );
   }

   [Fact]
   public void OutputFollowsRerankOrder_NotRetrievalOrder()
   {
      var rows = Rows( "first.cs", "second.cs", "third.cs" );
      var reranked = new List<RerankerResult> { new( 2, 0.9 ), new( 0, 0.5 ), new( 1, 0.2 ) };

      var (ordered, _) = HybridSearchService.ApplyRelevanceGate( reranked, rows, 0.001 );

      Assert.Equal( new[] { "third.cs", "first.cs", "second.cs" }, ordered.Select( r => r.FilePath ) );
   }

   [Fact]
   public void EachSurvivingRowIsStampedWithItsScore()
   {
      var rows = Rows( "a.cs" );
      var reranked = new List<RerankerResult> { new( 0, 0.66764 ) };

      var (ordered, _) = HybridSearchService.ApplyRelevanceGate( reranked, rows, 0.001 );

      Assert.Equal( "0.66764", ordered[0].Meta["rerank_score"] );
   }

   [Fact]
   public void OutOfRangeIndices_AreSkippedWithoutCountingAsDrops()
   {
      // A reranker contract violation, not a relevance decision. Counting these as drops would make
      // the caller treat "the reranker misbehaved" as "we deliberately filtered everything", which
      // suppresses the fail-soft fallback.
      var rows = Rows( "a.cs" );
      var reranked = new List<RerankerResult> { new( 5, 0.9 ), new( -1, 0.9 ) };

      var (ordered, dropped) = HybridSearchService.ApplyRelevanceGate( reranked, rows, 0.001 );

      Assert.Empty( ordered );
      Assert.Equal( 0, dropped );
   }

   [Fact]
   public void NoRerankerResults_YieldsNothingDropped()
   {
      // Distinguishes "reranker returned nothing" (caller should fall back to RRF order) from
      // "everything was filtered" (caller should return empty).
      var rows = Rows( "a.cs", "b.cs" );

      var (ordered, dropped) = HybridSearchService.ApplyRelevanceGate( new List<RerankerResult>(), rows, 0.001 );

      Assert.Empty( ordered );
      Assert.Equal( 0, dropped );
   }
}
