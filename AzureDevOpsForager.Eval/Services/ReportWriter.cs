using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Text;
using AzureDevOpsForager.Eval.Models;
using Newtonsoft.Json;

namespace AzureDevOpsForager.Eval.Services;

/// <summary>
/// Writes the run's results to disk in the four shapes a tuning session actually needs: a summary
/// CSV for sorting, a per-query CSV for finding what regressed, a JSON dump for anything the CSVs
/// cannot express, and a Markdown report meant to be read by a human or pasted onto a page.
///
/// The Markdown report deliberately leads with the baseline delta rather than the raw scores. An
/// absolute nDCG of 0.71 means very little on its own; "+0.06 against the shipping defaults, at
/// +180ms p95" is the sentence that decides whether a change ships.
/// </summary>
public class ReportWriter
{
   #region Data Members

   /// <summary>Filename for the one-row-per-configuration summary.</summary>
   private const string SummaryCsvName = "results.csv";

   /// <summary>Filename for the one-row-per-configuration-per-query detail.</summary>
   private const string PerQueryCsvName = "per-query.csv";

   /// <summary>Filename for the full machine-readable dump.</summary>
   private const string JsonName = "results.json";

   /// <summary>Filename for the human-readable ranked report.</summary>
   private const string MarkdownName = "report.md";

   /// <summary>
   /// Smallest nDCG difference treated as a real improvement in the report's verdict line. Set at
   /// one point of nDCG because anything under that, on a golden set of a few dozen queries, is
   /// comfortably inside the noise a single relabelled item would produce.
   /// </summary>
   private const double MeaningfulNdcgDelta = 0.01;

   #endregion Data Members

   #region Public Methods

   /// <summary>
   /// Writes all four output files into the given directory, creating it when needed.
   /// </summary>
   /// <param name="outputDirectory">Directory to write the reports into.</param>
   /// <param name="summaries">Every configuration's scorecard, in run order.</param>
   /// <param name="baselineName">Name of the configuration to measure deltas against, or null to use the first.</param>
   /// <returns>The paths of the files written.</returns>
   public List<string> WriteAll( string outputDirectory, List<RunSummary> summaries, string baselineName )
   {
      Directory.CreateDirectory( outputDirectory );

      var summaryPath = Path.Combine( outputDirectory, SummaryCsvName );
      var perQueryPath = Path.Combine( outputDirectory, PerQueryCsvName );
      var jsonPath = Path.Combine( outputDirectory, JsonName );
      var markdownPath = Path.Combine( outputDirectory, MarkdownName );

      File.WriteAllText( summaryPath, BuildSummaryCsv( summaries ) );
      File.WriteAllText( perQueryPath, BuildPerQueryCsv( summaries ) );
      File.WriteAllText( jsonPath, JsonConvert.SerializeObject( summaries, Formatting.Indented ) );
      File.WriteAllText( markdownPath, BuildMarkdown( summaries, ResolveBaseline( summaries, baselineName ) ) );

      return new List<string> { summaryPath, perQueryPath, jsonPath, markdownPath };
   }

   #endregion Public Methods

   #region Private Methods

   /// <summary>
   /// Builds the summary CSV: the swept knob values followed by the quality and latency numbers,
   /// one row per configuration.
   /// </summary>
   /// <param name="summaries">The scorecards to write.</param>
   /// <returns>The complete CSV text.</returns>
   private static string BuildSummaryCsv( List<RunSummary> summaries )
   {
      var builder = new StringBuilder();

      var headers = new List<string>( RunConfiguration.ConfigurationColumnHeaders() );
      headers.AddRange( new[]
      {
         "top_k", "mean_ndcg", "mean_recall", "mean_precision", "mean_mrr", "success_rate",
         "p50_ms", "p95_ms", "max_ms", "queries_run", "queries_failed", "vector_backed_results",
      } );
      builder.AppendLine( string.Join( ",", headers.Select( Escape ) ) );

      foreach( var summary in summaries )
      {
         var values = new List<string>( summary.Configuration.ToColumnValues() );
         values.AddRange( new[]
         {
            summary.TopK.ToString( CultureInfo.InvariantCulture ),
            Number( summary.MeanNdcgAtK ), Number( summary.MeanRecallAtK ),
            Number( summary.MeanPrecisionAtK ), Number( summary.MeanReciprocalRank ),
            Number( summary.SuccessRate ),
            Number( summary.LatencyP50Milliseconds, "0.0" ), Number( summary.LatencyP95Milliseconds, "0.0" ),
            Number( summary.LatencyMaxMilliseconds, "0.0" ),
            summary.QueriesRun.ToString( CultureInfo.InvariantCulture ),
            summary.QueriesFailed.ToString( CultureInfo.InvariantCulture ),
            summary.TotalVectorBackedResults.ToString( CultureInfo.InvariantCulture ),
         } );
         builder.AppendLine( string.Join( ",", values.Select( Escape ) ) );
      }

      return builder.ToString();
   }

   /// <summary>
   /// Builds the per-query CSV. This is the file that answers "what did that change actually break",
   /// which the summary means can never show.
   /// </summary>
   /// <param name="summaries">The scorecards to write.</param>
   /// <returns>The complete CSV text.</returns>
   private static string BuildPerQueryCsv( List<RunSummary> summaries )
   {
      var builder = new StringBuilder();
      builder.AppendLine( "config,query_id,category,question,ndcg,recall,precision,mrr,latency_ms,vector_backed,error" );

      foreach( var summary in summaries )
      {
         foreach( var outcome in summary.Outcomes )
         {
            var values = new[]
            {
               summary.Configuration.Name, outcome.QueryId, outcome.Category ?? "", outcome.Question,
               Number( outcome.NdcgAtK ), Number( outcome.RecallAtK ), Number( outcome.PrecisionAtK ),
               Number( outcome.ReciprocalRank ), Number( outcome.LatencyMilliseconds, "0.0" ),
               outcome.VectorBackedResults.ToString( CultureInfo.InvariantCulture ), outcome.Error ?? "",
            };
            builder.AppendLine( string.Join( ",", values.Select( Escape ) ) );
         }
      }

      return builder.ToString();
   }

   /// <summary>
   /// Builds the human-readable report: a ranked table with deltas against the baseline, followed by
   /// the warnings that matter more than the ranking itself.
   /// </summary>
   /// <param name="summaries">The scorecards to report on.</param>
   /// <param name="baseline">The configuration the deltas are measured against.</param>
   /// <returns>The complete Markdown text.</returns>
   private static string BuildMarkdown( List<RunSummary> summaries, RunSummary baseline )
   {
      var builder = new StringBuilder();
      var ranked = summaries.OrderByDescending( s => s.MeanNdcgAtK ).ToList();
      var topK = summaries.Count > 0 ? summaries[0].TopK : 0;

      builder.AppendLine( "# Retrieval quality sweep" );
      builder.AppendLine();
      builder.AppendLine( $"- Configurations measured: **{summaries.Count}**" );
      builder.AppendLine( $"- Golden queries per configuration: **{baseline?.Outcomes.Count ?? 0}**" );
      builder.AppendLine( $"- Cutoff: **K = {topK}**" );
      builder.AppendLine( $"- Baseline: `{baseline?.Configuration.Name ?? "none"}`" );
      builder.AppendLine();

      builder.AppendLine( "| # | Config | nDCG@K | ΔnDCG | Recall@K | MRR | Success | p50 ms | p95 ms | Δp95 | Failed |" );
      builder.AppendLine( "|---|---|---|---|---|---|---|---|---|---|---|" );

      for( int i = 0; i < ranked.Count; i++ )
         builder.AppendLine( BuildMarkdownRow( i + 1, ranked[i], baseline ) );

      builder.AppendLine();
      builder.Append( BuildVerdict( ranked, baseline ) );
      builder.Append( BuildWarnings( summaries ) );

      return builder.ToString();
   }

   /// <summary>
   /// Formats a single ranked row, including the deltas that make the row interpretable.
   /// </summary>
   /// <param name="rank">1-based rank by nDCG.</param>
   /// <param name="summary">The scorecard for this row.</param>
   /// <param name="baseline">The baseline to compare against, possibly null.</param>
   /// <returns>The Markdown table row.</returns>
   private static string BuildMarkdownRow( int rank, RunSummary summary, RunSummary baseline )
   {
      var ndcgDelta = baseline == null ? 0.0 : summary.MeanNdcgAtK - baseline.MeanNdcgAtK;
      var latencyDelta = baseline == null ? 0.0 : summary.LatencyP95Milliseconds - baseline.LatencyP95Milliseconds;
      var isBaseline = baseline != null && summary.Configuration.Name == baseline.Configuration.Name;
      var name = isBaseline ? $"`{summary.Configuration.Name}` _(baseline)_" : $"`{summary.Configuration.Name}`";

      return $"| {rank} | {name} | {Number( summary.MeanNdcgAtK )} | {Signed( ndcgDelta )} | " +
             $"{Number( summary.MeanRecallAtK )} | {Number( summary.MeanReciprocalRank )} | " +
             $"{Number( summary.SuccessRate )} | {Number( summary.LatencyP50Milliseconds, "0" )} | " +
             $"{Number( summary.LatencyP95Milliseconds, "0" )} | {Signed( latencyDelta, "0" )} | {summary.QueriesFailed} |";
   }

   /// <summary>
   /// Writes the plain-language verdict: whether the winner genuinely beat the baseline, and what it
   /// cost in latency. Stated explicitly because a ranked table invites reading the top row as a win
   /// even when the margin is noise.
   /// </summary>
   /// <param name="ranked">Configurations ordered best-first by nDCG.</param>
   /// <param name="baseline">The baseline to compare against, possibly null.</param>
   /// <returns>The verdict section.</returns>
   private static string BuildVerdict( List<RunSummary> ranked, RunSummary baseline )
   {
      if( ranked.Count == 0 || baseline == null )
         return string.Empty;

      var builder = new StringBuilder();
      builder.AppendLine( "## Verdict" );
      builder.AppendLine();

      // Nothing ran, so every score is a zero produced by absence rather than by measurement.
      // Ranking those zeros would name a "winner" and read as though the baseline had been
      // defended, which is the most misleading sentence this report could print.
      var measured = ranked.Where( s => s.QueriesRun > 0 ).ToList();
      if( measured.Count == 0 )
      {
         builder.AppendLine( "**Nothing ran.** No configuration completed a query, so there is no result to compare. See the warnings below." );
         builder.AppendLine();
         return builder.ToString();
      }

      var winner = measured[0];
      var delta = winner.MeanNdcgAtK - baseline.MeanNdcgAtK;
      var latencyDelta = winner.LatencyP95Milliseconds - baseline.LatencyP95Milliseconds;

      if( baseline.QueriesRun == 0 )
         builder.AppendLine(
            $"**No usable baseline.** `{baseline.Configuration.Name}` did not run, so the deltas in the table above are " +
            $"measured against zero and mean nothing. Best measured config was `{winner.Configuration.Name}` " +
            $"at {Number( winner.MeanNdcgAtK )} nDCG@{winner.TopK}." );
      else if( winner.Configuration.Name == baseline.Configuration.Name )
         builder.AppendLine( "**The baseline won.** Nothing in this sweep beat the settings already shipping." );
      else if( delta < MeaningfulNdcgDelta )
         builder.AppendLine(
            $"**No meaningful winner.** Best config `{winner.Configuration.Name}` leads the baseline by only " +
            $"{Signed( delta )} nDCG, under the {MeaningfulNdcgDelta:0.00} threshold this report treats as noise. Keep the baseline." );
      else
         builder.AppendLine(
            $"**`{winner.Configuration.Name}` beats the baseline** by {Signed( delta )} nDCG@{winner.TopK}, " +
            $"at {Signed( latencyDelta, "0" )} ms p95." );

      builder.AppendLine();
      return builder.ToString();
   }

   /// <summary>
   /// Appends the warnings that override the ranking: configurations that never ran, and
   /// configurations whose vector leg returned nothing and were therefore measuring full-text search
   /// under a hybrid label.
   /// </summary>
   /// <param name="summaries">Every scorecard from the run.</param>
   /// <returns>The warnings section, or an empty string when there is nothing to warn about.</returns>
   private static string BuildWarnings( List<RunSummary> summaries )
   {
      var deadVector = summaries.Where( s => s.QueriesRun > 0 && s.TotalVectorBackedResults == 0 ).ToList();
      var skipped = summaries.Where( s => s.QueriesRun == 0 ).ToList();

      if( deadVector.Count == 0 && skipped.Count == 0 )
         return string.Empty;

      var builder = new StringBuilder();
      builder.AppendLine( "## Warnings" );
      builder.AppendLine();

      foreach( var summary in deadVector )
         builder.AppendLine(
            $"- `{summary.Configuration.Name}` returned **zero vector-backed results**. Its scores describe " +
            $"full-text search, not hybrid search. Most likely the distance ceiling " +
            $"({summary.Configuration.MaxVectorDistance:0.00}) sits below the embedding model's real distance floor." );

      foreach( var summary in skipped )
         builder.AppendLine(
            $"- `{summary.Configuration.Name}` did not run: {summary.Outcomes.FirstOrDefault()?.Error ?? "unknown reason"}" );

      builder.AppendLine();
      return builder.ToString();
   }

   /// <summary>
   /// Picks the configuration deltas are measured against: the one named on the command line when it
   /// exists, otherwise the first configuration run, which the planner emits from the current
   /// shipping defaults.
   /// </summary>
   /// <param name="summaries">Every scorecard from the run.</param>
   /// <param name="baselineName">Requested baseline configuration name, or null.</param>
   /// <returns>The baseline scorecard, or null when there are no scorecards.</returns>
   private static RunSummary ResolveBaseline( List<RunSummary> summaries, string baselineName )
   {
      if( summaries.Count == 0 )
         return null;

      if( !string.IsNullOrWhiteSpace( baselineName ) )
      {
         var named = summaries.FirstOrDefault(
            s => string.Equals( s.Configuration.Name, baselineName, StringComparison.OrdinalIgnoreCase ) );
         if( named != null )
            return named;
      }

      return summaries[0];
   }

   /// <summary>Formats a metric for output with a fixed number of decimals and invariant culture.</summary>
   /// <param name="value">The value to format.</param>
   /// <param name="format">Numeric format string; defaults to three decimals.</param>
   /// <returns>The formatted number.</returns>
   private static string Number( double value, string format = "0.000" )
   {
      return value.ToString( format, CultureInfo.InvariantCulture );
   }

   /// <summary>
   /// Formats a delta with an explicit sign, so an improvement and a regression are distinguishable
   /// at a glance rather than by squinting at two adjacent columns.
   /// </summary>
   /// <param name="value">The delta to format.</param>
   /// <param name="format">Numeric format string; defaults to three decimals.</param>
   /// <returns>The signed, formatted delta.</returns>
   private static string Signed( double value, string format = "0.000" )
   {
      var text = Math.Abs( value ).ToString( format, CultureInfo.InvariantCulture );
      if( value > 0 )
         return "+" + text;

      return value < 0 ? "-" + text : text;
   }

   /// <summary>
   /// Escapes a CSV field, quoting it when it contains a delimiter, quote, or newline so that a
   /// question containing a comma cannot silently shift every column to its right.
   /// </summary>
   /// <param name="value">The raw field value.</param>
   /// <returns>The CSV-safe field.</returns>
   private static string Escape( string value )
   {
      value = value ?? string.Empty;

      if( value.IndexOfAny( new[] { ',', '"', '\r', '\n' } ) < 0 )
         return value;

      return "\"" + value.Replace( "\"", "\"\"" ) + "\"";
   }

   #endregion Private Methods
}
