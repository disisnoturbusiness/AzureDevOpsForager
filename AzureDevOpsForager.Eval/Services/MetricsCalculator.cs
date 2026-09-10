using System;
using System.Collections.Generic;
using System.Linq;
using AzureDevOpsForager.Eval.Models;

namespace AzureDevOpsForager.Eval.Services;

/// <summary>
/// Turns a graded, ranked result list into the numbers that decide whether a configuration change
/// was an improvement: recall@K, precision@K, MRR, nDCG@K, and latency percentiles.
///
/// Four quality metrics rather than one because they disagree in useful ways. Recall asks whether
/// the answers were found at all, precision asks what fraction of the page was worth reading, MRR
/// asks how far the user had to scroll, and nDCG is the only one that notices when a merely
/// acceptable answer outranks the best one. A change that lifts recall while sinking MRR is a real
/// trade-off, and a single headline number would hide it.
/// </summary>
public class MetricsCalculator
{
   #region Data Members

   /// <summary>
   /// Rank offset used in the logarithmic discount. Standard DCG discounts rank i (1-based) by
   /// log2(i + 1), so the top result is divided by log2(2) = 1 and is therefore undiscounted.
   /// </summary>
   private const int DiscountRankOffset = 1;

   #endregion Data Members

   #region Public Methods

   /// <summary>
   /// Computes every per-query metric for one query's graded results and packages them into an
   /// outcome row. The caller supplies the already-judged ranking so that judging and scoring stay
   /// separately testable.
   /// </summary>
   /// <param name="query">The golden query being scored, supplying the full label set for recall.</param>
   /// <param name="judgements">The graded results in rank order; index 0 is the top hit.</param>
   /// <param name="topK">Cutoff K for the @K metrics.</param>
   /// <returns>The populated outcome, minus the fields the runner fills in (latency, errors).</returns>
   public QueryOutcome Score( GoldenQuery query, IReadOnlyList<RankedJudgement> judgements, int topK )
   {
      var considered = Take( judgements, topK );

      var outcome = new QueryOutcome
      {
         QueryId = query.Id,
         Question = query.Question,
         Category = query.Category,
         RankedGrades = considered.Select( j => j.Grade ).ToList(),
         RecallAtK = RecallAtK( query, considered ),
         PrecisionAtK = PrecisionAtK( considered, topK ),
         ReciprocalRank = ReciprocalRank( considered ),
         NdcgAtK = NdcgAtK( query, considered, topK ),
      };

      return outcome;
   }

   /// <summary>
   /// Fraction of the query's labelled items that the search surfaced within the cutoff. Coverage is
   /// counted per distinct labelled item, so returning the same good file repeatedly earns credit once.
   /// </summary>
   /// <param name="query">The golden query supplying the denominator of labelled items.</param>
   /// <param name="judgements">The graded results within the cutoff.</param>
   /// <returns>Recall in the range 0..1; zero when the query has no countable labels.</returns>
   public double RecallAtK( GoldenQuery query, IReadOnlyList<RankedJudgement> judgements )
   {
      var totalRelevant = CountableLabels( query );
      if( totalRelevant == 0 )
         return 0.0;

      var covered = new HashSet<int>();
      foreach( var judgement in judgements )
         if( judgement.GoldenItemIndex >= 0 && judgement.Grade >= RelevantItem.MinimumBinaryGrade )
            covered.Add( judgement.GoldenItemIndex );

      return (double)covered.Count / totalRelevant;
   }

   /// <summary>
   /// Fraction of the K result slots that were relevant. Divided by K rather than by the number of
   /// results actually returned, so that a configuration which quietly returns three results instead
   /// of ten is penalised for the empty slots rather than flattered by them.
   /// </summary>
   /// <param name="judgements">The graded results within the cutoff.</param>
   /// <param name="topK">The cutoff K forming the denominator.</param>
   /// <returns>Precision in the range 0..1.</returns>
   public double PrecisionAtK( IReadOnlyList<RankedJudgement> judgements, int topK )
   {
      if( topK <= 0 )
         return 0.0;

      var hits = judgements.Count( j => j.Grade >= RelevantItem.MinimumBinaryGrade );
      return (double)hits / topK;
   }

   /// <summary>
   /// Reciprocal of the 1-based rank of the first relevant result, or zero when none was relevant.
   /// The metric that tracks "did the answer land at the top" most directly.
   /// </summary>
   /// <param name="judgements">The graded results within the cutoff, in rank order.</param>
   /// <returns>The reciprocal rank in the range 0..1.</returns>
   public double ReciprocalRank( IReadOnlyList<RankedJudgement> judgements )
   {
      for( int i = 0; i < judgements.Count; i++ )
         if( judgements[i].Grade >= RelevantItem.MinimumBinaryGrade )
            return 1.0 / ( i + 1 );

      return 0.0;
   }

   /// <summary>
   /// Normalized discounted cumulative gain at K, using exponential gain on the human grades so a
   /// grade-3 answer is worth substantially more than a grade-1 one, and normalizing against the
   /// best ordering the labels permit so scores are comparable across queries with different numbers
   /// of relevant items.
   /// </summary>
   /// <param name="query">The golden query supplying the ideal ranking.</param>
   /// <param name="judgements">The graded results within the cutoff, in rank order.</param>
   /// <param name="topK">Cutoff K applied to both the actual and the ideal ranking.</param>
   /// <returns>nDCG in the range 0..1; zero when no ideal gain exists.</returns>
   public double NdcgAtK( GoldenQuery query, IReadOnlyList<RankedJudgement> judgements, int topK )
   {
      var actualGrades = judgements.Select( j => j.Grade ).ToList();

      var idealGrades = ( query.Relevant ?? new List<RelevantItem>() )
         .Select( r => r.Grade )
         .OrderByDescending( g => g )
         .Take( topK )
         .ToList();

      var idealGain = DiscountedCumulativeGain( idealGrades );
      if( idealGain <= 0.0 )
         return 0.0;

      return DiscountedCumulativeGain( actualGrades ) / idealGain;
   }

   /// <summary>
   /// Aggregates per-query outcomes into the summary row for one configuration, including latency
   /// percentiles. Failed queries are excluded from the quality means but counted separately, so a
   /// run that got better by crashing on its hard queries is visible rather than flattering.
   /// </summary>
   /// <param name="configuration">The configuration these outcomes were produced under.</param>
   /// <param name="outcomes">Every per-query outcome from the run, successes and failures alike.</param>
   /// <param name="topK">The cutoff K the outcomes were scored at.</param>
   /// <returns>The aggregate scorecard for the configuration.</returns>
   public RunSummary Summarize( RunConfiguration configuration, List<QueryOutcome> outcomes, int topK )
   {
      var succeeded = outcomes.Where( o => o.Error == null ).ToList();
      var latencies = succeeded.Select( o => o.LatencyMilliseconds ).OrderBy( ms => ms ).ToList();

      return new RunSummary
      {
         Configuration = configuration,
         TopK = topK,
         QueriesRun = succeeded.Count,
         QueriesFailed = outcomes.Count - succeeded.Count,
         MeanRecallAtK = Mean( succeeded.Select( o => o.RecallAtK ) ),
         MeanPrecisionAtK = Mean( succeeded.Select( o => o.PrecisionAtK ) ),
         MeanReciprocalRank = Mean( succeeded.Select( o => o.ReciprocalRank ) ),
         MeanNdcgAtK = Mean( succeeded.Select( o => o.NdcgAtK ) ),
         SuccessRate = Mean( succeeded.Select( o => o.ReciprocalRank > 0.0 ? 1.0 : 0.0 ) ),
         LatencyP50Milliseconds = Percentile( latencies, 50 ),
         LatencyP95Milliseconds = Percentile( latencies, 95 ),
         LatencyMaxMilliseconds = latencies.Count == 0 ? 0.0 : latencies[latencies.Count - 1],
         TotalVectorBackedResults = succeeded.Sum( o => o.VectorBackedResults ),
         Outcomes = outcomes,
      };
   }

   /// <summary>
   /// Returns the value at the given percentile of an already-sorted sample using nearest-rank,
   /// which needs no interpolation and therefore always reports a latency that was actually observed.
   /// </summary>
   /// <param name="sortedValues">Sample values sorted ascending.</param>
   /// <param name="percentile">The percentile to read, 0-100.</param>
   /// <returns>The observed value at that percentile, or zero for an empty sample.</returns>
   public double Percentile( IReadOnlyList<double> sortedValues, double percentile )
   {
      if( sortedValues == null || sortedValues.Count == 0 )
         return 0.0;

      var rank = (int)Math.Ceiling( percentile / 100.0 * sortedValues.Count );
      var index = Math.Min( Math.Max( rank - 1, 0 ), sortedValues.Count - 1 );

      return sortedValues[index];
   }

   #endregion Public Methods

   #region Private Methods

   /// <summary>
   /// Sums the exponential-gain, log-discounted contribution of each graded position, which is the
   /// standard DCG formulation: (2^grade - 1) / log2(rank + 1).
   /// </summary>
   /// <param name="grades">Grades in rank order.</param>
   /// <returns>The discounted cumulative gain.</returns>
   private static double DiscountedCumulativeGain( IReadOnlyList<int> grades )
   {
      double gain = 0.0;

      for( int i = 0; i < grades.Count; i++ )
      {
         if( grades[i] <= 0 )
            continue;

         gain += ( Math.Pow( 2, grades[i] ) - 1 ) / Math.Log( i + 1 + DiscountRankOffset, 2 );
      }

      return gain;
   }

   /// <summary>
   /// Counts the labelled items that the binary metrics are allowed to treat as findable, which
   /// excludes explicit grade-0 labels recorded as "looked relevant, is not".
   /// </summary>
   /// <param name="query">The query whose labels are counted.</param>
   /// <returns>The number of countable relevant items.</returns>
   private static int CountableLabels( GoldenQuery query )
   {
      if( query.Relevant == null )
         return 0;

      return query.Relevant.Count( r => r.Grade >= RelevantItem.MinimumBinaryGrade );
   }

   /// <summary>
   /// Takes the first K judgements, tolerating a result list shorter than K rather than padding it,
   /// because a missing result and an irrelevant one are different failures.
   /// </summary>
   /// <param name="judgements">The full judged ranking.</param>
   /// <param name="topK">The cutoff to apply.</param>
   /// <returns>At most K judgements, in rank order.</returns>
   private static IReadOnlyList<RankedJudgement> Take( IReadOnlyList<RankedJudgement> judgements, int topK )
   {
      if( judgements == null )
         return new List<RankedJudgement>();

      return judgements.Take( Math.Max( topK, 0 ) ).ToList();
   }

   /// <summary>
   /// Arithmetic mean that yields zero for an empty sample, so a configuration where every query
   /// failed reports zero rather than throwing or producing NaN in the CSV.
   /// </summary>
   /// <param name="values">The sample to average.</param>
   /// <returns>The mean, or zero when the sample is empty.</returns>
   private static double Mean( IEnumerable<double> values )
   {
      var materialized = values.ToList();
      return materialized.Count == 0 ? 0.0 : materialized.Average();
   }

   #endregion Private Methods
}
