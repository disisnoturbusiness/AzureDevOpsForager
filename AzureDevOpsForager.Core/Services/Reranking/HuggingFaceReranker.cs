using System;
using System.Collections.Generic;
using System.Linq;
using System.Net.Http;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using Newtonsoft.Json;
using Newtonsoft.Json.Linq;

namespace AzureDevOpsForager.Core.Services.Reranking;
/// <summary>
/// Remote <see cref="IReranker"/> backed by a Hugging Face Inference Endpoint serving bge-reranker-v2-m3.
/// It POSTs {"query", "texts":[previews]} with a bearer token to the endpoint's /rerank route and reads
/// back [{index, score}], mapping each returned index back to the candidate's OriginalIndex. Fail-soft
/// per the interface contract: any error returns the candidates in their original retrieval order,
/// truncated to topK, and never throws (except honoring cancellation). Lets ranking run with zero local
/// ONNX (no bge model loaded in-process).
/// </summary>
public class HuggingFaceReranker : IReranker
{
   #region Data Members

   /// <summary>Shared client pre-loaded with the bearer Authorization header and a request timeout.</summary>
   private readonly HttpClient _httpClient;

   /// <summary>The endpoint's /rerank route (base URL + "/rerank").</summary>
   private readonly string _rerankUrl;

   #endregion

   #region Constructor

   /// <summary>Creates a reranker bound to a HF endpoint URL and bearer token; appends the /rerank route.</summary>
   public HuggingFaceReranker( string endpointUrl, string token )
   {
      _rerankUrl = ( endpointUrl?.TrimEnd( '/' ) ?? "" ) + "/rerank";
      _httpClient = new HttpClient { Timeout = TimeSpan.FromMinutes( 2 ) };
      if( !string.IsNullOrWhiteSpace( token ) )
         _httpClient.DefaultRequestHeaders.Add( "Authorization", "Bearer " + token );
   }

   #endregion

   #region Public Methods

   /// <summary>
   /// Rescores the candidates via the HF cross-encoder and returns the top-K by descending score. On any
   /// failure it returns the input order truncated to topK (fail-soft); cancellation is honored.
   /// </summary>
   public async Task<IReadOnlyList<RerankerResult>> RerankAsync(
      string query, IReadOnlyList<RerankerCandidate> candidates, int topK, CancellationToken cancellationToken = default )
   {
      if( candidates == null || candidates.Count == 0 )
         return new List<RerankerResult>();
      if( candidates.Count == 1 )
         return new List<RerankerResult> { new RerankerResult( candidates[0].OriginalIndex, 1.0 ) };

      try
      {
         var texts = candidates.Select( candidate => candidate.Preview ?? "" ).ToList();
         var payload = JsonConvert.SerializeObject( new { query = query ?? "", texts } );
         var body = await PostWithWarmupRetryAsync( payload, cancellationToken );

         // Response shape: [{ "index": <position in texts>, "score": <relevance> }, ...]
         var scored = new List<RerankerResult>();
         foreach( var item in JArray.Parse( body ) )
         {
            var textIndex = item["index"]?.Value<int>() ?? -1;
            var score = item["score"]?.Value<double>() ?? 0.0;
            if( textIndex >= 0 && textIndex < candidates.Count )
               scored.Add( new RerankerResult( candidates[textIndex].OriginalIndex, score ) );
         }

         if( scored.Count == 0 )
            return FallbackOriginalOrder( candidates, topK );

         return scored.OrderByDescending( result => result.Score ).Take( topK ).ToList();
      }
      catch( OperationCanceledException )
      {
         throw;
      }
      catch
      {
         return FallbackOriginalOrder( candidates, topK );
      }
   }

   #endregion

   #region Private Methods

   /// <summary>
   /// POSTs the payload to /rerank, retrying the transient statuses a scale-to-zero endpoint returns while
   /// its GPU spins up (503/429/409/5xx). Backs off (2s..10s) for up to ~5 minutes; a real error throws.
   /// </summary>
   private async Task<string> PostWithWarmupRetryAsync( string payload, CancellationToken cancellationToken )
   {
      const int maxAttempts = 30;
      for( int attempt = 1; ; attempt++ )
      {
         using var content = new StringContent( payload, Encoding.UTF8, "application/json" );
         using var response = await _httpClient.PostAsync( _rerankUrl, content, cancellationToken );
         if( response.IsSuccessStatusCode )
            return await response.Content.ReadAsStringAsync();

         var status = (int)response.StatusCode;
         var transient = status == 503 || status == 429 || status == 409 || status == 500 || status == 502 || status == 504;
         if( !transient || attempt >= maxAttempts )
            response.EnsureSuccessStatusCode();

         await Task.Delay( TimeSpan.FromSeconds( Math.Min( 10, attempt * 2 ) ), cancellationToken );
      }
   }

   /// <summary>Fail-soft result: the candidates in their original retrieval order, truncated to topK.</summary>
   private static IReadOnlyList<RerankerResult> FallbackOriginalOrder( IReadOnlyList<RerankerCandidate> candidates, int topK )
   {
      return candidates.Take( topK ).Select( candidate => new RerankerResult( candidate.OriginalIndex, 0.0 ) ).ToList();
   }

   #endregion
}
