using System.Collections.Generic;

namespace AzureDevOpsForager.Eval.Models;

/// <summary>
/// What happened when one golden query was run against one configuration: the ranked result list
/// the search returned, the per-query metrics computed from it, and how long it took.
///
/// Kept per-query rather than only aggregated because an average is where regressions go to hide.
/// A config change that lifts the mean while destroying three specific queries is a bad change,
/// and the only way to see that is to keep the individual rows.
/// </summary>
public class QueryOutcome
{
   #region Data Members

   /// <summary>Identifier of the golden query this outcome belongs to.</summary>
   public string QueryId { get; set; }

   /// <summary>The question text, duplicated here so the per-query CSV is readable on its own.</summary>
   public string Question { get; set; }

   /// <summary>Optional grouping tag copied from the golden query, for per-category breakdowns.</summary>
   public string Category { get; set; }

   /// <summary>
   /// Graded relevance of each returned result, in rank order: index 0 is the top hit. A zero means
   /// the result at that rank was not in the golden set. This single array is what every metric for
   /// this query is computed from.
   /// </summary>
   public List<int> RankedGrades { get; set; } = new List<int>();

   /// <summary>Fraction of the query's relevant items that appeared anywhere in the top K.</summary>
   public double RecallAtK { get; set; }

   /// <summary>Fraction of the top K results that were relevant.</summary>
   public double PrecisionAtK { get; set; }

   /// <summary>
   /// Reciprocal of the rank of the first relevant result, or zero if none appeared. Answers
   /// "how far down did the user have to read", which is the metric that tracks felt quality most
   /// closely on navigational questions.
   /// </summary>
   public double ReciprocalRank { get; set; }

   /// <summary>
   /// Normalized discounted cumulative gain over the top K, using the human grades as gains. The
   /// only metric here that rewards putting the BEST answer above a merely acceptable one.
   /// </summary>
   public double NdcgAtK { get; set; }

   /// <summary>Wall-clock milliseconds for the whole search call, including embedding and any rerank.</summary>
   public double LatencyMilliseconds { get; set; }

   /// <summary>
   /// How many of the returned results were attributed to the vector leg by the fusion proc's
   /// MatchSource column. Recorded because a config whose vector leg has silently gone empty can
   /// still post respectable scores on keyword-friendly queries, and this is the column that
   /// exposes it.
   /// </summary>
   public int VectorBackedResults { get; set; }

   /// <summary>Error text when this query failed outright, or null on success.</summary>
   public string Error { get; set; }

   #endregion Data Members
}

/// <summary>
/// The aggregate scorecard for one configuration across the entire golden set: mean metrics,
/// latency percentiles, and the counts needed to tell a clean run from a partly-failed one.
///
/// This is the row that lands in results.csv and the report table, and the object a human actually
/// compares when deciding whether a knob change earned its keep.
/// </summary>
public class RunSummary
{
   #region Data Members

   /// <summary>The configuration these numbers were produced by.</summary>
   public RunConfiguration Configuration { get; set; }

   /// <summary>The K used for every cutoff metric in this summary.</summary>
   public int TopK { get; set; }

   /// <summary>Mean recall@K across all successfully executed queries.</summary>
   public double MeanRecallAtK { get; set; }

   /// <summary>Mean precision@K across all successfully executed queries.</summary>
   public double MeanPrecisionAtK { get; set; }

   /// <summary>Mean reciprocal rank across all successfully executed queries.</summary>
   public double MeanReciprocalRank { get; set; }

   /// <summary>Mean nDCG@K across all successfully executed queries. The headline quality number.</summary>
   public double MeanNdcgAtK { get; set; }

   /// <summary>
   /// Fraction of queries that returned at least one relevant result in the top K. Blunter than
   /// nDCG and easier to explain out loud, which makes it the right number for a demo page.
   /// </summary>
   public double SuccessRate { get; set; }

   /// <summary>Median search latency in milliseconds.</summary>
   public double LatencyP50Milliseconds { get; set; }

   /// <summary>95th-percentile search latency in milliseconds, the number that governs felt speed.</summary>
   public double LatencyP95Milliseconds { get; set; }

   /// <summary>Slowest single query in milliseconds.</summary>
   public double LatencyMaxMilliseconds { get; set; }

   /// <summary>Count of queries that ran without error.</summary>
   public int QueriesRun { get; set; }

   /// <summary>
   /// Count of queries that threw. Surfaced next to the metrics because means computed over a
   /// shrinking denominator can drift upward while the system gets worse.
   /// </summary>
   public int QueriesFailed { get; set; }

   /// <summary>
   /// Total results across the run that the fusion attributed to the vector leg. Zero here means
   /// the run measured full-text search wearing a hybrid label, regardless of how good the scores look.
   /// </summary>
   public int TotalVectorBackedResults { get; set; }

   /// <summary>The individual query outcomes behind these aggregates.</summary>
   public List<QueryOutcome> Outcomes { get; set; } = new List<QueryOutcome>();

   #endregion Data Members
}
