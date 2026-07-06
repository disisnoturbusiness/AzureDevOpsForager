using System;
using System.Collections.Generic;
using System.Net.Http;
using System.Text;
using System.Threading.Tasks;
using Newtonsoft.Json;
using Newtonsoft.Json.Linq;

namespace AzureDevOpsForager.Core.Services.Embedding;
/// <summary>
/// Remote <see cref="IEmbedder"/> backed by a Hugging Face Inference Endpoint serving e5-large-v2.
/// Instead of loading the ~1.3 GB ONNX model in-process, it POSTs {"inputs":"passage: ..."} with a
/// bearer token to the endpoint and reads back the flat 1024-float vector. It applies the same E5
/// "query: " / "passage: " prefixes as the local service and L2-normalizes the result, so its vectors
/// are interchangeable with the local model's and cosine ranking stays valid. This is what lets the
/// Server and Indexer run with zero local ONNX.
/// </summary>
public class HuggingFaceEmbedder : IEmbedder, IDisposable
{
   #region Data Members

   /// <summary>Shared client pre-loaded with the bearer Authorization header and a generous timeout.</summary>
   private readonly HttpClient _httpClient;

   /// <summary>The HF endpoint URL that returns an embedding for a single "inputs" string.</summary>
   private readonly string _endpointUrl;

   #endregion

   #region Constructor

   /// <summary>Creates an embedder bound to a HF endpoint URL and bearer token.</summary>
   public HuggingFaceEmbedder( string endpointUrl, string token )
   {
      _endpointUrl = endpointUrl?.TrimEnd( '/' );
      _httpClient = new HttpClient { Timeout = TimeSpan.FromMinutes( 3 ) };
      if( !string.IsNullOrWhiteSpace( token ) )
         _httpClient.DefaultRequestHeaders.Add( "Authorization", "Bearer " + token );
   }

   #endregion

   #region Public Methods (IEmbedder)

   /// <summary>
   /// Embeds a search query by POSTing "query: {text}" to the HF endpoint and returning the
   /// unit-length 1024-dim vector. Synchronous convenience wrapper over <see cref="EmbedQueryAsync"/>;
   /// it blocks the calling thread on the network round-trip, so prefer the async form on request
   /// threads (the server hot path uses the async members for exactly this reason).
   /// </summary>
   public float[] EmbedQuery( string text ) => EmbedAsync( "query: " + text ).GetAwaiter().GetResult();

   /// <summary>
   /// Embeds a passage / code chunk by POSTing "passage: {text}" to the HF endpoint and returning the
   /// unit-length 1024-dim vector. Synchronous convenience wrapper over <see cref="EmbedPassageAsync"/>;
   /// it blocks the calling thread on the network round-trip, so prefer the async form on request threads.
   /// </summary>
   public float[] EmbedPassage( string text ) => EmbedAsync( "passage: " + text ).GetAwaiter().GetResult();

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

   /// <summary>Async passage embed for the Indexer's parallel loop (adds the "passage: " prefix).</summary>
   public Task<float[]> EmbedPassageAsync( string text ) => EmbedAsync( "passage: " + text );

   /// <summary>Async query embed (adds the "query: " prefix).</summary>
   public Task<float[]> EmbedQueryAsync( string text ) => EmbedAsync( "query: " + text );

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

   /// <summary>POSTs {"inputs": prefixedText} to the endpoint (with warm-up retry), parses the vector, and L2-normalizes it.</summary>
   private async Task<float[]> EmbedAsync( string prefixedText )
   {
      if( string.IsNullOrWhiteSpace( prefixedText ) )
         return new float[Config.EmbeddingDimension];

      var payload = JsonConvert.SerializeObject( new { inputs = prefixedText } );
      var body = await PostWithWarmupRetryAsync( payload );

      var vector = ParseVector( body );
      NormalizeInPlace( vector );
      return vector;
   }

   /// <summary>
   /// POSTs the payload, retrying the transient statuses a scale-to-zero HF endpoint returns while its GPU
   /// spins up (503 loading, 429 rate, 409 conflict, other 5xx). Backs off (2s..10s) for up to ~5 minutes so
   /// a cold endpoint warms rather than failing every chunk; a real error (e.g. 401/400) throws immediately.
   /// </summary>
   private async Task<string> PostWithWarmupRetryAsync( string payload )
   {
      const int maxAttempts = 30;
      for( int attempt = 1; ; attempt++ )
      {
         using var content = new StringContent( payload, Encoding.UTF8, "application/json" );
         using var response = await _httpClient.PostAsync( _endpointUrl, content );
         if( response.IsSuccessStatusCode )
            return await response.Content.ReadAsStringAsync();

         var status = (int)response.StatusCode;
         var transient = status == 503 || status == 429 || status == 409 || status == 500 || status == 502 || status == 504;
         if( !transient || attempt >= maxAttempts )
            response.EnsureSuccessStatusCode();   // throw with the real status

         await Task.Delay( TimeSpan.FromSeconds( Math.Min( 10, attempt * 2 ) ) );
      }
   }

   /// <summary>Parses the HF response into a float[], tolerating a flat [..] or a nested [[..]] array.</summary>
   private static float[] ParseVector( string body )
   {
      var array = JToken.Parse( body ) as JArray;
      if( array != null && array.Count > 0 && array[0] is JArray )
         array = (JArray)array[0];
      if( array == null )
         return new float[Config.EmbeddingDimension];

      var vector = new float[array.Count];
      for( int i = 0; i < array.Count; i++ )
         vector[i] = array[i].Value<float>();
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
