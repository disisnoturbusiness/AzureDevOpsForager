using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Threading.Tasks;
using AzureDevOpsForager.Core;
using AzureDevOpsForager.Core.Models.Search;
using AzureDevOpsForager.Core.Services.Embedding;
using AzureDevOpsForager.Core.Services.Reranking;
using AzureDevOpsForager.Core.Services.Search;
using AzureDevOpsForager.Eval.Models;

namespace AzureDevOpsForager.Eval.Services;

/// <summary>
/// Runs the golden set against one configuration at a time and collects the measurements.
///
/// Two things here exist purely to stop the harness from lying. The first is the preflight: a
/// configuration is proved to work on a single query before thirty more are spent on it, so a
/// dimension mismatch or a dead connection is reported as a skipped configuration instead of
/// thirty identical failures averaged into a suspiciously round score. The second is the warm-up:
/// the first call of a run pays for model load and connection setup, and folding that one-off cost
/// into a p95 makes every configuration look slower than it is, with the first one measured
/// punished hardest.
/// </summary>
public class EvalRunner
{
   #region Data Members

   /// <summary>Query used to warm caches and force model loading before any timing is recorded.</summary>
   private const string WarmupQuestion = "how does hybrid search fuse results";

   /// <summary>Judges returned results against the golden labels.</summary>
   private readonly RelevanceJudge _judge = new RelevanceJudge();

   /// <summary>Computes per-query and aggregate metrics.</summary>
   private readonly MetricsCalculator _metrics = new MetricsCalculator();

   #endregion Data Members

   #region Public Methods

   /// <summary>
   /// Runs every golden query against one configuration and returns its scorecard. Applies the
   /// configuration to the static <see cref="Config"/> first, since that is what the search path and
   /// the fusion proc read their knobs from.
   /// </summary>
   /// <param name="configuration">The configuration to measure.</param>
   /// <param name="queries">The golden set to run.</param>
   /// <param name="topK">Number of results to request and to score at.</param>
   /// <returns>The aggregate scorecard, or a summary marking every query failed when preflight fails.</returns>
   public async Task<RunSummary> RunAsync( RunConfiguration configuration, List<GoldenQuery> queries, int topK )
   {
      ApplyConfiguration( configuration );

      IEmbedder embedder = null;
      IReranker reranker = null;

      try
      {
         embedder = BuildEmbedder( configuration );
         AlignEmbeddingDimension( embedder );
         reranker = BuildReranker( configuration );
      }
      catch( Exception exception )
      {
         return SkippedSummary( configuration, queries, topK, $"backend unavailable: {exception.Message}" );
      }

      using( var search = new HybridSearchService( null, null, embedder, reranker ) )
      {
         var preflightError = await PreflightAsync( search, queries[0], topK );
         if( preflightError != null )
            return SkippedSummary( configuration, queries, topK, preflightError );

         var outcomes = new List<QueryOutcome>();
         foreach( var query in queries )
            outcomes.Add( await RunSingleAsync( search, query, topK ) );

         return _metrics.Summarize( configuration, outcomes, topK );
      }
   }

   #endregion Public Methods

   #region Private Methods

   /// <summary>
   /// Copies a configuration's knob values onto the static <see cref="Config"/>. This is the only
   /// place the harness mutates global state, so the blast radius of the sweep is one method.
   /// </summary>
   /// <param name="configuration">The configuration whose values become the active settings.</param>
   private static void ApplyConfiguration( RunConfiguration configuration )
   {
      Config.RrfVectorWeight = configuration.RrfVectorWeight;
      Config.RrfChunkFtsWeight = configuration.RrfChunkFtsWeight;
      Config.RrfFileFtsWeight = configuration.RrfFileFtsWeight;
      Config.MinFtsRank = configuration.MinFtsRank;
      Config.MaxVectorDistance = configuration.MaxVectorDistance;
      Config.RerankerEnabled = configuration.RerankerEnabled;
      Config.RerankerInputSize = configuration.RerankerInputSize;
   }

   /// <summary>
   /// Creates the embedding backend named by the configuration, mirroring how the server wires
   /// itself so the harness measures the deployed path rather than a lookalike.
   /// </summary>
   /// <param name="configuration">The configuration naming the backend.</param>
   /// <returns>The embedder to use for this run.</returns>
   /// <exception cref="InvalidOperationException">The named backend is not configured on this machine.</exception>
   private static IEmbedder BuildEmbedder( RunConfiguration configuration )
   {
      if( configuration.Embedder == RunConfiguration.BackendHosted )
      {
         if( !Config.HuggingFaceEnabled )
            throw new InvalidOperationException( "hosted embedder requested but HuggingFace endpoint/token is not configured" );

         return new HuggingFaceEmbedder( Config.HuggingFaceEmbedUrl, Config.HuggingFaceToken );
      }

      if( !File.Exists( Config.OnnxModelPath ) )
         throw new InvalidOperationException( $"local embedder requested but model is missing: {Config.OnnxModelPath}" );

      return new EmbeddingService();
   }

   /// <summary>
   /// Creates the rerank backend named by the configuration, or null when the configuration turns
   /// reranking off. A null reranker is the supported "no second stage" signal downstream.
   /// </summary>
   /// <param name="configuration">The configuration naming the backend.</param>
   /// <returns>The reranker to use, or null for no rerank stage.</returns>
   /// <exception cref="InvalidOperationException">The named backend is not configured on this machine.</exception>
   private static IReranker BuildReranker( RunConfiguration configuration )
   {
      if( !configuration.RerankerEnabled || configuration.Reranker == RunConfiguration.BackendNone )
         return null;

      if( configuration.Reranker == RunConfiguration.BackendHosted )
      {
         if( !Config.HuggingFaceEnabled || string.IsNullOrWhiteSpace( Config.HuggingFaceRerankUrl ) )
            throw new InvalidOperationException( "hosted reranker requested but HuggingFace rerank endpoint/token is not configured" );

         return new HuggingFaceReranker( Config.HuggingFaceRerankUrl, Config.HuggingFaceToken );
      }

      if( !File.Exists( Config.RerankerModelPath ) )
         throw new InvalidOperationException( $"local reranker requested but model is missing: {Config.RerankerModelPath}" );

      return new BgeReranker();
   }

   /// <summary>
   /// Probes the embedder for the width of the vectors it actually emits and points
   /// <see cref="Config.EmbeddingDimension"/> at it, so the VECTOR cast in the fusion proc is at
   /// least well-formed. Whether the INDEX accepts that width is a separate question, deliberately
   /// left to the preflight: a mismatch there is a "you must reindex" answer, and it should be
   /// reported as such rather than silently papered over here.
   /// </summary>
   /// <param name="embedder">The embedder about to be used.</param>
   private static void AlignEmbeddingDimension( IEmbedder embedder )
   {
      var probe = embedder.EmbedQuery( "dimension probe" );
      if( probe == null || probe.Length == 0 )
         throw new InvalidOperationException( "embedder returned an empty vector for the dimension probe" );

      Config.EmbeddingDimension = probe.Length;
   }

   /// <summary>
   /// Proves a configuration can actually search before spending the full golden set on it, and
   /// warms the model and connection so the first timed query is not paying one-off costs.
   /// </summary>
   /// <param name="search">The search service under test.</param>
   /// <param name="probeQuery">Any golden query, used as a realistic preflight question.</param>
   /// <param name="topK">Result count to request.</param>
   /// <returns>Null when the configuration is usable, otherwise a description of why it is not.</returns>
   private static async Task<string> PreflightAsync( HybridSearchService search, GoldenQuery probeQuery, int topK )
   {
      try
      {
         await search.SearchAsync( new SearchRequest { Question = WarmupQuestion, NResults = topK } );

         var response = await search.SearchAsync( new SearchRequest { Question = probeQuery.Question, NResults = topK } );
         if( !string.IsNullOrWhiteSpace( response.Error ) )
            return $"preflight search failed: {response.Error}";

         return null;
      }
      catch( Exception exception )
      {
         return $"preflight search threw: {exception.Message}";
      }
   }

   /// <summary>
   /// Runs one golden query, times it, and scores the ranked results it returned.
   /// </summary>
   /// <param name="search">The search service under test.</param>
   /// <param name="query">The golden query to run.</param>
   /// <param name="topK">Result count to request and score at.</param>
   /// <returns>The scored outcome, carrying an error message when the query failed.</returns>
   private async Task<QueryOutcome> RunSingleAsync( HybridSearchService search, GoldenQuery query, int topK )
   {
      var stopwatch = Stopwatch.StartNew();

      try
      {
         var response = await search.SearchAsync( new SearchRequest { Question = query.Question, NResults = topK } );
         stopwatch.Stop();

         if( !string.IsNullOrWhiteSpace( response.Error ) )
            return FailedOutcome( query, stopwatch.Elapsed.TotalMilliseconds, response.Error );

         var metadata = FirstList( response.Metadatas );
         var paths = metadata.Select( m => Value( m, "_file_path" ) ).ToList();
         var chunkNames = metadata.Select( m => Value( m, "chunk_name" ) ).ToList();

         var judgements = _judge.JudgeRanked( query, paths, chunkNames );
         var outcome = _metrics.Score( query, judgements, topK );
         outcome.LatencyMilliseconds = stopwatch.Elapsed.TotalMilliseconds;
         outcome.VectorBackedResults = metadata.Count( m => Value( m, "match_source" ) != "FullText" );

         return outcome;
      }
      catch( Exception exception )
      {
         stopwatch.Stop();
         return FailedOutcome( query, stopwatch.Elapsed.TotalMilliseconds, exception.Message );
      }
   }

   /// <summary>
   /// Builds the outcome recorded when a query could not be scored, keeping the metrics at zero and
   /// the reason attached so the failure survives into the report instead of vanishing into a mean.
   /// </summary>
   /// <param name="query">The query that failed.</param>
   /// <param name="latencyMilliseconds">How long it ran before failing.</param>
   /// <param name="error">Why it failed.</param>
   /// <returns>The failed outcome.</returns>
   private static QueryOutcome FailedOutcome( GoldenQuery query, double latencyMilliseconds, string error )
   {
      return new QueryOutcome
      {
         QueryId = query.Id,
         Question = query.Question,
         Category = query.Category,
         LatencyMilliseconds = latencyMilliseconds,
         Error = error,
      };
   }

   /// <summary>
   /// Builds the scorecard for a configuration that never ran, marking every query failed with the
   /// same reason. Reported rather than dropped, because a silently missing row in a sweep reads as
   /// "not interesting" when it actually means "not measured".
   /// </summary>
   /// <param name="configuration">The configuration that was skipped.</param>
   /// <param name="queries">The golden set that would have been run.</param>
   /// <param name="topK">The cutoff that would have been used.</param>
   /// <param name="reason">Why the configuration could not run.</param>
   /// <returns>A summary whose failure count equals the golden set size.</returns>
   private RunSummary SkippedSummary( RunConfiguration configuration, List<GoldenQuery> queries, int topK, string reason )
   {
      var outcomes = queries.Select( q => FailedOutcome( q, 0.0, reason ) ).ToList();
      return _metrics.Summarize( configuration, outcomes, topK );
   }

   /// <summary>
   /// Reads the first (and only) result list out of the search response's nested list shape, which
   /// exists to mirror the multi-query API contract even though the harness asks one at a time.
   /// </summary>
   /// <param name="nested">The response's nested metadata list.</param>
   /// <returns>The first inner list, or an empty list when the response carried none.</returns>
   private static List<Dictionary<string, string>> FirstList( List<List<Dictionary<string, string>>> nested )
   {
      if( nested == null || nested.Count == 0 || nested[0] == null )
         return new List<Dictionary<string, string>>();

      return nested[0];
   }

   /// <summary>
   /// Reads a metadata value, returning an empty string when the key is absent so downstream string
   /// comparisons never have to null-check.
   /// </summary>
   /// <param name="metadata">The result's metadata dictionary.</param>
   /// <param name="key">The key to read.</param>
   /// <returns>The value, or an empty string.</returns>
   private static string Value( Dictionary<string, string> metadata, string key )
   {
      if( metadata == null || !metadata.TryGetValue( key, out var value ) )
         return string.Empty;

      return value ?? string.Empty;
   }

   #endregion Private Methods
}
