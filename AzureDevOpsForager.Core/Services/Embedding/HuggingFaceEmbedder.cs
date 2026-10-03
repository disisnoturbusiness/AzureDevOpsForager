using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Net.Http;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using Newtonsoft.Json;
using Newtonsoft.Json.Linq;

namespace AzureDevOpsForager.Core.Services.Embedding;
/// <summary>
/// Remote <see cref="IEmbedder"/> backed by a Hugging Face Inference Endpoint serving BAAI/bge-code-v1,
/// a code-specialized 1536-dim embedder (Qwen2.5-Coder backbone, 32k context) served by TEI. Instead of
/// loading a multi-GB model in-process, it POSTs {"inputs":"..."} with a bearer token to the endpoint and
/// reads back the flat float vector. Per the bge-code-v1 model card, queries are wrapped in the model's
/// "&lt;instruct&gt;{task}\n&lt;query&gt;{text}" prompt (the task comes from
/// <see cref="Config.EmbeddingQueryInstruction"/>) while documents/passages are embedded raw. Results are
/// L2-normalized so cosine ranking stays valid. This is what lets the Server and Indexer run with zero
/// local ONNX.
///
/// Query embeds run under a time budget (<see cref="DefaultQueryBudget"/>); passage embeds do not. A
/// query embed sits on a visitor's search request, which the hosting front end abandons at about 230
/// seconds, and the search already falls back to full-text-only when the embed throws. A passage embed
/// sits in the indexer, where waiting out a long cold start is the whole point of the warm-up retry.
/// </summary>
public class HuggingFaceEmbedder : IEmbedder, IDisposable
{
   #region Data Members

   /// <summary>
   /// The most wall-clock time one QUERY embed may spend on the endpoint, counting every attempt, the time
   /// each request is in flight, and every backoff sleep between them.
   /// <para>
   /// The query embed is the first thing a visitor's search waits on, and Azure App Service abandons the
   /// request at roughly 230 seconds with a 504. The unbounded warm-up loop (30 attempts, 3-minute timeout
   /// each) could outlive that on its own, which is the same failure that took search down on 2026-10-02
   /// through the reranker. When this budget runs out the embed throws, and HybridSearchService serves a
   /// full-text-only result instead.
   /// </para>
   /// <para>
   /// 90 seconds is about three times the 30.9 s embedder cold start recorded in HybridSearchService (an
   /// earlier embedder; the current model's cold start has not been measured separately), so an ordinary
   /// wake should still complete inside it, and it leaves the rest of the search deadline for SQL and the
   /// rerank.
   /// The request that hits the budget has already triggered the wake, so the next search gets vectors.
   /// </para>
   /// </summary>
   private static readonly TimeSpan DefaultQueryBudget = TimeSpan.FromSeconds( 90 );

   /// <summary>
   /// The longest a single query-embed attempt may wait for a response before it is abandoned and counted
   /// as a transient failure. Never longer than what remains of <see cref="DefaultQueryBudget"/>.
   /// </summary>
   private static readonly TimeSpan DefaultQueryAttemptTimeout = TimeSpan.FromSeconds( 30 );

   /// <summary>
   /// Shared client pre-loaded with the bearer Authorization header and a generous timeout. The 3-minute
   /// timeout is what bounds a single passage-embed attempt; query attempts carry a shorter deadline of
   /// their own through a CancellationTokenSource, which fires first.
   /// </summary>
   private readonly HttpClient _httpClient;

   /// <summary>The HF endpoint URL that returns an embedding for a single "inputs" string.</summary>
   private readonly string _endpointUrl;

   /// <summary>Total time budget for one query embed; see <see cref="DefaultQueryBudget"/>.</summary>
   private readonly TimeSpan _queryBudget;

   /// <summary>Per-attempt response deadline for a query embed; see <see cref="DefaultQueryAttemptTimeout"/>.</summary>
   private readonly TimeSpan _queryAttemptTimeout;

   #endregion

   #region Constructor

   /// <summary>
   /// Creates an embedder bound to a HF endpoint URL and bearer token, using the production query limits
   /// <see cref="DefaultQueryBudget"/> and <see cref="DefaultQueryAttemptTimeout"/>.
   /// </summary>
   public HuggingFaceEmbedder( string endpointUrl, string token )
      : this( endpointUrl, token, new HttpClientHandler(), DefaultQueryBudget, DefaultQueryAttemptTimeout )
   {
   }

   /// <summary>
   /// Creates an embedder over a caller-supplied message handler and explicit query time limits. This is
   /// the seam the unit tests use to stand in a fake endpoint (one that returns 503 forever, or never
   /// answers at all) and to prove the budget in seconds rather than minutes. The client takes ownership
   /// of the handler and disposes it with itself.
   /// </summary>
   /// <param name="endpointUrl">The endpoint URL that returns an embedding for one "inputs" string.</param>
   /// <param name="token">The bearer token, or null/blank to send no Authorization header.</param>
   /// <param name="handler">The HTTP message handler every request is sent through.</param>
   /// <param name="queryBudget">The most wall-clock time one query embed may spend across all attempts.</param>
   /// <param name="queryAttemptTimeout">The longest one query-embed attempt may wait for a response.</param>
   public HuggingFaceEmbedder( string endpointUrl, string token, HttpMessageHandler handler, TimeSpan queryBudget, TimeSpan queryAttemptTimeout )
   {
      _endpointUrl = endpointUrl?.TrimEnd( '/' );
      _queryBudget = queryBudget;
      _queryAttemptTimeout = queryAttemptTimeout;
      _httpClient = new HttpClient( handler ) { Timeout = TimeSpan.FromMinutes( 3 ) };
      if( !string.IsNullOrWhiteSpace( token ) )
         _httpClient.DefaultRequestHeaders.Add( "Authorization", "Bearer " + token );
   }

   #endregion

   #region Public Methods (IEmbedder)

   /// <summary>
   /// Embeds a search query by POSTing the bge-code-v1 instruction-wrapped prompt to the HF endpoint and
   /// returning the unit-length vector. Synchronous convenience wrapper over <see cref="EmbedQueryAsync"/>;
   /// it blocks the calling thread on the network round-trip, so prefer the async form on request
   /// threads (the server hot path uses the async members for exactly this reason).
   /// </summary>
   public float[] EmbedQuery( string text ) => EmbedQueryAsync( text ).GetAwaiter().GetResult();

   /// <summary>
   /// Embeds a passage / code chunk by POSTing the raw text to the HF endpoint and returning the
   /// unit-length vector (bge-code-v1 documents take no instruction prefix). Synchronous convenience
   /// wrapper over <see cref="EmbedPassageAsync"/>; it blocks the calling thread on the network
   /// round-trip, so prefer the async form on request threads.
   /// </summary>
   public float[] EmbedPassage( string text ) => EmbedPassageAsync( text ).GetAwaiter().GetResult();

   /// <summary>
   /// Embeds many queries by calling <see cref="EmbedQuery"/> per item (the HF endpoint takes one
   /// "inputs" string per request, so there is no single-round-trip batch form here). Blocks the
   /// calling thread on each round-trip; prefer <see cref="EmbedQueryBatchAsync"/> on request threads.
   /// </summary>
   public List<float[]> EmbedQueryBatch( IReadOnlyList<string> texts )
   {
      var result = new List<float[]>( texts.Count );
      foreach( var text in texts ) result.Add( EmbedQuery( text ) );
      return result;
   }

   /// <summary>
   /// Embeds many passages by calling <see cref="EmbedPassage"/> per item (the HF endpoint takes one
   /// "inputs" string per request, so there is no single-round-trip batch form here). Blocks the
   /// calling thread on each round-trip; prefer <see cref="EmbedPassageBatchAsync"/> on request threads.
   /// </summary>
   public List<float[]> EmbedPassageBatch( IReadOnlyList<string> texts )
   {
      var result = new List<float[]>( texts.Count );
      foreach( var text in texts ) result.Add( EmbedPassage( text ) );
      return result;
   }

   #endregion

   #region Async (IEmbedder + Indexer's async embed loop)

   /// <summary>
   /// Async passage embed for the Indexer's parallel loop (documents are embedded raw, per the model card).
   /// No time budget: the indexer would rather wait out a cold start than skip chunks.
   /// </summary>
   public Task<float[]> EmbedPassageAsync( string text ) => EmbedAsync( text, null );

   /// <summary>
   /// Async query embed (wraps the text in the bge-code-v1 "&lt;instruct&gt;/&lt;query&gt;" prompt). Bounded
   /// by the query time budget; running out throws a TimeoutException naming the last failure.
   /// </summary>
   public Task<float[]> EmbedQueryAsync( string text ) =>
      string.IsNullOrWhiteSpace( text )
         ? Task.FromResult( new float[Config.EmbeddingDimension] )
         : EmbedAsync( $"<instruct>{Config.EmbeddingQueryInstruction}\n<query>{text}", _queryBudget );

   /// <summary>
   /// Async form of <see cref="EmbedQueryBatch"/>: awaits each query embed in turn so a request thread
   /// is never blocked on the network. The endpoint has no single-round-trip batch form, so this still
   /// issues one call per text, just without a synchronous wait.
   /// </summary>
   public async Task<List<float[]>> EmbedQueryBatchAsync( IReadOnlyList<string> texts )
   {
      var result = new List<float[]>( texts.Count );
      foreach( var text in texts ) result.Add( await EmbedQueryAsync( text ) );
      return result;
   }

   /// <summary>
   /// Async form of <see cref="EmbedPassageBatch"/>: awaits each passage embed in turn so a request
   /// thread is never blocked on the network. One call per text, without a synchronous wait.
   /// </summary>
   public async Task<List<float[]>> EmbedPassageBatchAsync( IReadOnlyList<string> texts )
   {
      var result = new List<float[]>( texts.Count );
      foreach( var text in texts ) result.Add( await EmbedPassageAsync( text ) );
      return result;
   }

   #endregion

   #region IDisposable

   private bool _disposed;

   /// <summary>Disposes the shared HttpClient. Safe to call more than once.</summary>
   public void Dispose()
   {
      if( _disposed ) return;
      _httpClient?.Dispose();
      _disposed = true;
   }

   #endregion

   #region Private Methods

   /// <summary>
   /// POSTs {"inputs": text} to the endpoint (with warm-up retry), parses the vector, and L2-normalizes it.
   /// </summary>
   /// <param name="text">The text to embed, already wrapped in the query prompt when it is a query.</param>
   /// <param name="budget">The total time budget for the warm-up retry, or null for none (passages).</param>
   private async Task<float[]> EmbedAsync( string text, TimeSpan? budget )
   {
      if( string.IsNullOrWhiteSpace( text ) )
         return new float[Config.EmbeddingDimension];

      // truncate:true lets TEI clip inputs beyond the model's context window instead of erroring; with
      // bge-code-v1's 32k window a Roslyn chunk should never actually hit it, so this is a safety net.
      var payload = JsonConvert.SerializeObject( new { inputs = text, truncate = true } );
      var body = await PostWithWarmupRetryAsync( payload, budget );

      var vector = ParseVector( body );
      NormalizeInPlace( vector );
      return vector;
   }

   /// <summary>
   /// POSTs the payload, retrying the transient statuses a scale-to-zero HF endpoint returns while its GPU
   /// spins up (503 loading, 429 rate, 409 conflict, other 5xx). Backs off (2s..10s) for up to 30 attempts
   /// (~5 minutes) so a cold endpoint warms rather than failing every chunk; a real error (e.g. 401/400)
   /// throws an HttpRequestException immediately, as does running out of attempts.
   /// <para>
   /// With a budget (the query path) the loop is also bounded in wall-clock time, measured on a Stopwatch
   /// that runs across in-flight requests and backoff sleeps alike. Each attempt's deadline is the
   /// per-attempt timeout or whatever budget remains, whichever is sooner; an attempt that gets no
   /// response in time is retried like a 503; and the loop stops rather than sleep past the budget.
   /// Running out throws a TimeoutException carrying the last failure seen.
   /// </para>
   /// </summary>
   /// <param name="payload">The serialized JSON request body.</param>
   /// <param name="budget">The total time budget, or null for the unbounded passage path.</param>
   /// <returns>The successful response body.</returns>
   private async Task<string> PostWithWarmupRetryAsync( string payload, TimeSpan? budget )
   {
      const int maxAttempts = 30;
      var elapsed = Stopwatch.StartNew();
      var lastFailure = "no attempt was made";
      var attempt = 0;
      while( true )
      {
         var attemptTimeout = Timeout.InfiniteTimeSpan;
         if( budget.HasValue )
         {
            var remaining = budget.Value - elapsed.Elapsed;
            if( remaining <= TimeSpan.Zero )
               break;
            attemptTimeout = remaining < _queryAttemptTimeout ? remaining : _queryAttemptTimeout;
         }

         attempt++;
         var outcome = await PostOnceAsync( payload, attemptTimeout );
         if( outcome.Body != null )
            return outcome.Body;
         if( !outcome.Transient || attempt >= maxAttempts )
            throw new HttpRequestException( outcome.Failure );

         lastFailure = outcome.Failure;
         var backoff = TimeSpan.FromSeconds( Math.Min( 10, attempt * 2 ) );
         if( budget.HasValue && elapsed.Elapsed + backoff >= budget.Value )
            break;

         await Task.Delay( backoff );
      }

      throw new TimeoutException(
         $"Query embed gave up after {attempt} attempt(s) in {elapsed.Elapsed.TotalSeconds:F0}s " +
         $"(budget {budget.GetValueOrDefault().TotalSeconds:F0}s); last failure: {lastFailure}" );
   }

   /// <summary>
   /// Makes one POST to the endpoint under its own deadline. Returns the body on success; otherwise
   /// whether the failure is worth retrying, plus a description of it.
   /// <para>
   /// Only this attempt's own deadline is caught, and a hung request is classed as transient, the same as
   /// a 503: an endpoint stuck waiting for hardware looks exactly like that from outside. With an infinite
   /// deadline (the passage path) the HttpClient's 3-minute timeout still applies and propagates as it
   /// always has.
   /// </para>
   /// </summary>
   /// <param name="payload">The serialized JSON request body.</param>
   /// <param name="timeout">How long this attempt may wait for a response, or infinite.</param>
   /// <returns>The response body (null on failure), whether a failure is transient, and its description.</returns>
   private async Task<(string Body, bool Transient, string Failure)> PostOnceAsync( string payload, TimeSpan timeout )
   {
      using var attemptCancellation = new CancellationTokenSource();
      attemptCancellation.CancelAfter( timeout );
      try
      {
         using var content = new StringContent( payload, Encoding.UTF8, "application/json" );
         using var response = await _httpClient.PostAsync( _endpointUrl, content, attemptCancellation.Token );
         if( response.IsSuccessStatusCode )
            return ( await response.Content.ReadAsStringAsync(), false, null );

         var status = (int)response.StatusCode;
         var transient = status == 503 || status == 429 || status == 409 || status == 500 || status == 502 || status == 504;
         return ( null, transient, $"{status} ({response.ReasonPhrase}) from {_endpointUrl}" );
      }
      catch( OperationCanceledException ) when( attemptCancellation.IsCancellationRequested )
      {
         return ( null, true, $"no response within {timeout.TotalSeconds:F0}s from {_endpointUrl}" );
      }
   }

   /// <summary>Parses the HF response into a float[], tolerating a flat [..] or a nested [[..]] array.</summary>
   private static float[] ParseVector( string body )
   {
      var array = JToken.Parse( body ) as JArray;
      if( array != null && array.Count > 0 && array[0] is JArray )
         array = (JArray)array[0];

      // Throw rather than hand back a zero vector. An all-zero embedding is not a degraded result, it
      // is a meaningless one: it survives NormalizeInPlace untouched (magnitude 0 short-circuits), then
      // produces garbage cosine distances against every stored chunk, so the caller silently returns
      // nonsense instead of reporting that the endpoint answered with something unparseable.
      if( array == null )
         throw new InvalidOperationException(
            $"Embedding endpoint returned a payload that is not a JSON array: {( body != null && body.Length > 200 ? body.Substring( 0, 200 ) + "..." : body )}" );

      var vector = new float[array.Count];
      for( int i = 0; i < array.Count; i++ )
         vector[i] = array[i].Value<float>();

      // A dimension mismatch here means the configured model and EmbeddingDimension disagree. Caught at
      // the source it is one clear message; left alone it surfaces much later as a CAST failure to
      // VECTOR(n) inside the search proc, or worse, as a quietly empty vector leg.
      if( vector.Length != Config.EmbeddingDimension )
         throw new InvalidOperationException(
            $"Embedding endpoint returned {vector.Length} dimensions but EmbeddingDimension is {Config.EmbeddingDimension}. " +
            "Update EmbeddingDimension to match the model and run a full reindex." );

      return vector;
   }

   /// <summary>Scales a vector to unit length so cosine distance behaves (idempotent if already unit).</summary>
   private static void NormalizeInPlace( float[] vector )
   {
      double sumSquares = 0;
      for( int i = 0; i < vector.Length; i++ ) sumSquares += (double)vector[i] * vector[i];
      var magnitude = Math.Sqrt( sumSquares );
      if( magnitude <= 0 ) return;
      for( int i = 0; i < vector.Length; i++ ) vector[i] = (float)( vector[i] / magnitude );
   }

   #endregion
}
