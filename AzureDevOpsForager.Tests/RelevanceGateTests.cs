using AzureDevOpsForager.Core.Services.Reranking;
using AzureDevOpsForager.Core.Services.Search;
using Xunit;

namespace AzureDevOpsForager.Tests;

/// <summary>
/// Covers <see cref="HybridSearchService.ApplyRelevanceGate"/> — the two-part gate that decides how many
/// results a query gets back: a RELATIVE floor (a fraction of the best score in the set) plus a
/// degenerate-top guard (if the best score is ~0, nothing is returned).
///
/// The relative design exists because an absolute floor is a property of one model's calibration. A floor
/// tuned against Qwen3-Reranker-4B filtered out every result the moment the endpoint was pointed at
/// Qwen3-Reranker-0.6B — the smaller model scores on a different scale, nothing errored, and every search
/// silently returned nothing. <see cref="ScoreScaleIsIrrelevant_SameShapeSurvivesAnyCalibration"/> is the
/// regression test for that, and it is the most important one in this file.
/// </summary>
public class RelevanceGateTests
{
   private const double Ratio = 0.1;
   private const double TopFloor = 0.000001;

   private static (string FilePath, string Content, Dictionary<string, string> Meta) Row( string path ) =>
      (path, $"content of {path}", new Dictionary<string, string> { ["_file_path"] = path });

   private static List<(string FilePath, string Content, Dictionary<string, string> Meta)> Rows( params string[] paths ) =>
      paths.Select( Row ).ToList();

   /// <summary>Builds reranker output for scores in descending order, mapped to rows 0..n-1.</summary>
   private static List<RerankerResult> Scores( params double[] scores ) =>
      scores.Select( ( s, i ) => new RerankerResult( i, s ) ).ToList();

   [Fact]
   public void ScoreScaleIsIrrelevant_SameShapeSurvivesAnyCalibration()
   {
      // THE REGRESSION TEST. Identical relative shape, three wildly different absolute scales — the sort
      // of difference that separates one reranker from another. All three must return the same count.
      // Under the old absolute floor the second and third would have returned nothing at all.
      var big    = HybridSearchService.ApplyRelevanceGate( Scores( 0.90, 0.50, 0.20, 0.02 ), Rows( "a", "b", "c", "d" ), Ratio, TopFloor );
      var small  = HybridSearchService.ApplyRelevanceGate( Scores( 0.0090, 0.0050, 0.0020, 0.0002 ), Rows( "a", "b", "c", "d" ), Ratio, TopFloor );
      var tiny   = HybridSearchService.ApplyRelevanceGate( Scores( 0.000090, 0.000050, 0.000020, 0.000002 ), Rows( "a", "b", "c", "d" ), Ratio, TopFloor );

      Assert.Equal( 3, big.Ordered.Count );
      Assert.Equal( 3, small.Ordered.Count );
      Assert.Equal( 3, tiny.Ordered.Count );
   }

   [Fact]
   public void AllScoresNearZero_ReturnsNothing()
   {
      // The unanswerable question. A pure ratio would keep everything here, because every score sits
      // within any fraction of a top score that is itself zero — hence the degenerate-top guard.
      var (ordered, dropped) = HybridSearchService.ApplyRelevanceGate(
         Scores( 0.0, 0.0, 0.0, 0.0, 0.0 ), Rows( "a", "b", "c", "d", "e" ), Ratio, TopFloor );

      Assert.Empty( ordered );
      Assert.Equal( 5, dropped );
   }

   [Fact]
   public void OneStrongHitAmongNoise_KeepsOnlyTheStrongOne()
   {
      // Measured shape of "how many bits in a byte": one weak-but-clear best, then nothing near it.
      var (ordered, dropped) = HybridSearchService.ApplyRelevanceGate(
         Scores( 0.00134, 0.00012, 0.00007, 0.00001, 0.0 ), Rows( "a", "b", "c", "d", "e" ), Ratio, TopFloor );

      Assert.Single( ordered );
      Assert.Equal( "a", ordered[0].FilePath );
      Assert.Equal( 4, dropped );
   }

   [Fact]
   public void GradualDistribution_KeepsAll()
   {
      // Measured shape of "how do we send email" — a real answer with several supporting hits.
      var (ordered, dropped) = HybridSearchService.ApplyRelevanceGate(
         Scores( 0.66764, 0.37515, 0.22165, 0.21776, 0.1391 ), Rows( "a", "b", "c", "d", "e" ), Ratio, TopFloor );

      Assert.Equal( 5, ordered.Count );
      Assert.Equal( 0, dropped );
   }

   [Fact]
   public void StrongTopWithTrailingNoise_DropsTheTail()
   {
      // Measured shape of "PaymentMethod": exact hit, two related, then two irrelevant.
      var (ordered, dropped) = HybridSearchService.ApplyRelevanceGate(
         Scores( 0.99009, 0.40979, 0.19795, 0.00308, 0.00117 ), Rows( "a", "b", "c", "d", "e" ), Ratio, TopFloor );

      Assert.Equal( 3, ordered.Count );
      Assert.Equal( 2, dropped );
   }

   [Fact]
   public void ZeroRatio_KeepsEverythingAboveTheDegenerateGuard()
   {
      var (ordered, dropped) = HybridSearchService.ApplyRelevanceGate(
         Scores( 0.9, 0.001, 0.0 ), Rows( "a", "b", "c" ), 0.0, TopFloor );

      Assert.Equal( 3, ordered.Count );
      Assert.Equal( 0, dropped );
   }

   [Fact]
   public void FloorIsInclusive_ScoreExactlyAtTheRatioSurvives()
   {
      var (ordered, _) = HybridSearchService.ApplyRelevanceGate(
         Scores( 1.0, 0.1 ), Rows( "a", "b" ), Ratio, TopFloor );

      Assert.Equal( 2, ordered.Count );
   }

   [Fact]
   public void OutputFollowsRerankOrder_NotRetrievalOrder()
   {
      var reranked = new List<RerankerResult> { new( 2, 0.9 ), new( 0, 0.5 ), new( 1, 0.2 ) };

      var (ordered, _) = HybridSearchService.ApplyRelevanceGate(
         reranked, Rows( "first", "second", "third" ), Ratio, TopFloor );

      Assert.Equal( new[] { "third", "first", "second" }, ordered.Select( r => r.FilePath ) );
   }

   [Fact]
   public void EachSurvivingRowIsStampedWithItsScore()
   {
      var (ordered, _) = HybridSearchService.ApplyRelevanceGate(
         Scores( 0.66764 ), Rows( "a" ), Ratio, TopFloor );

      Assert.Equal( "0.66764", ordered[0].Meta["rerank_score"] );
   }

   [Fact]
   public void OutOfRangeIndices_AreSkippedAndDoNotSetTheTopScore()
   {
      // A bogus index carrying a huge score must not become the reference point — that would drag the
      // floor up and wrongly filter the genuine results measured against it.
      var reranked = new List<RerankerResult> { new( 99, 1.0 ), new( 0, 0.5 ), new( 1, 0.1 ) };

      var (ordered, dropped) = HybridSearchService.ApplyRelevanceGate(
         reranked, Rows( "a", "b" ), Ratio, TopFloor );

      Assert.Equal( 2, ordered.Count );
      Assert.Equal( 0, dropped );
   }

   [Fact]
   public void NoRerankerResults_YieldsNothingDropped()
   {
      // Distinguishes "reranker returned nothing" (caller falls back to RRF order) from "everything was
      // filtered" (caller returns empty). Both produce no rows, only Dropped tells them apart.
      var (ordered, dropped) = HybridSearchService.ApplyRelevanceGate(
         new List<RerankerResult>(), Rows( "a", "b" ), Ratio, TopFloor );

      Assert.Empty( ordered );
      Assert.Equal( 0, dropped );
   }

   [Fact]
   public void MisorderedRerankerOutput_StillUsesTheTrueMaximumAsReference()
   {
      // The contract says descending, but taking First() rather than Max() would make a misbehaving
      // reranker cause silent over-filtering instead of merely a wrong order.
      var reranked = new List<RerankerResult> { new( 0, 0.05 ), new( 1, 0.90 ) };

      var (ordered, dropped) = HybridSearchService.ApplyRelevanceGate(
         reranked, Rows( "a", "b" ), Ratio, TopFloor );

      Assert.Single( ordered );
      Assert.Equal( "b", ordered[0].FilePath );
      Assert.Equal( 1, dropped );
   }
}
