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
/// Remote <see cref="IReranker"/> backed by a Hugging Face Inference Endpoint serving Qwen3-Reranker-0.6B in
/// its sequence-classification form (tomaarsen/Qwen3-Reranker-0.6B-seq-cls) on a vLLM container. It POSTs a
/// Jina-style {"model","query","documents"} request to the endpoint's /rerank route and reads back the
/// scored results, mapping each returned index back to the candidate's OriginalIndex.
///
/// Qwen3-Reranker is instruction-aware and scores a (query, document) pair through its chat template, so
/// the query side carries the template prefix plus "&lt;Instruct&gt;/&lt;Query&gt;" markers (the task text
/// comes from <see cref="Config.RerankerInstruction"/>) and each document carries the "&lt;Document&gt;"
/// marker plus the template suffix — concatenated by the server they form the exact prompt the model was
/// trained on. The parser accepts both the vLLM/Jina response shape {"results":[{index,relevance_score}]}
/// and the older TEI shape [{index,score}], so either serving stack works.
///
/// Fail-soft per the interface contract: any error returns the candidates in their original retrieval
/// order, truncated to topK, and never throws (except honoring cancellation). Lets ranking run with zero
/// local ONNX (no reranker model loaded in-process).
/// </summary>
public class HuggingFaceReranker : IReranker
{
   #region Data Members

   /// <summary>
   /// Qwen3-Reranker chat-template prefix: the system turn framing the yes/no relevance judgment, opening
   /// the user turn. Sent at the start of the query side of every pair, per the model card.
   /// </summary>
   private const string PromptPrefix =
      "<|im_start|>system\nJudge whether the Document meets the requirements based on the Query and the Instruct provided. Note that the answer can only be \"yes\" or \"no\".<|im_end|>\n<|im_start|>user\n";

   /// <summary>
   /// Qwen3-Reranker chat-template suffix: closes the user turn and opens the (empty-thinking) assistant
   /// turn the classifier head scores. Appended after every document, per the model card.
   /// </summary>
   private const string PromptSuffix = "<|im_end|>\n<|im_start|>assistant\n<think>\n\n</think>\n\n";

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
   /// Rescores the candidates via the hosted cross-encoder and returns the top-K by descending score. On any
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
         var wrappedQuery = PromptPrefix + $"<Instruct>: {Config.RerankerInstruction}\n<Query>: {query ?? ""}\n";
         var documents = candidates.Select( candidate => $"<Document>: {candidate.Preview ?? ""}" + PromptSuffix ).ToList();
         var payload = JsonConvert.SerializeObject( new
         {
            model = Config.RerankerModelName,
            query = wrappedQuery,
            documents,
            top_n = candidates.Count
         } );
         var body = await PostWithWarmupRetryAsync( payload, cancellationToken );

         var scored = ParseScores( body, candidates );
         if( scored.Count == 0 )
         {
            Report( $"endpoint returned no usable scores for model '{Config.RerankerModelName}'" );
            return FallbackOriginalOrder( candidates, topK );
         }

         return scored.OrderByDescending( result => result.Score ).Take( topK ).ToList();
      }
      catch( OperationCanceledException )
      {
         throw;
      }
      catch( Exception exception )
      {
         // Reported rather than swallowed. Degrading to retrieval order is the right behaviour, but doing
         // it silently means a persistent misconfiguration looks identical to the reranker simply being
         // unimpressed by the results. The model name is included because it is sent in the request body
         // and must match what the endpoint actually serves — pointing HuggingFaceRerankUrl at a different
         // endpoint without also changing RerankerModelName makes vLLM reject every call with
         // "The model `...` does not exist", which is exactly how this fallback started firing constantly.
         Report( $"{exception.GetType().Name}: {exception.Message} (model '{Config.RerankerModelName}')" );
         return FallbackOriginalOrder( candidates, topK );
      }
   }

   #endregion

   #region Private Methods

   /// <summary>
   /// Parses either rerank response shape into results mapped back to the candidates' original indexes:
   /// vLLM/Jina {"results":[{ "index", "relevance_score" }]} or TEI [{ "index", "score" }].
   /// </summary>
   private static List<RerankerResult> ParseScores( string body, IReadOnlyList<RerankerCandidate> candidates )
   {
      var root = JToken.Parse( body );
      var items = root as JArray ?? root["results"] as JArray;

      var scored = new List<RerankerResult>();
      if( items == null )
         return scored;

      foreach( var item in items )
      {
         var textIndex = item["index"]?.Value<int>() ?? -1;
         var score = ( item["relevance_score"] ?? item["score"] )?.Value<double>() ?? 0.0;
         if( textIndex >= 0 && textIndex < candidates.Count )
            scored.Add( new RerankerResult( candidates[textIndex].OriginalIndex, score ) );
      }
      return scored;
   }

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

   /// <summary>
   /// Writes a rerank degradation notice to both the console and the log file. Console because that is
   /// what surfaces in a hosted platform's log stream; the log file because that is what survives a
   /// restart. Rate-limiting is deliberately omitted: a reranker that is failing on every request should
   /// be noisy, since the visible symptom otherwise is only slightly worse ordering.
   /// </summary>
   private static void Report( string detail )
   {
      var message = $"[RERANK] Falling back to retrieval order — {detail}";
      Console.WriteLine( message );
      Logger.Warn( message, "Rerank" );
   }

   /// <summary>
   /// Fail-soft result: the candidates in their original retrieval order, truncated to topK, carrying
   /// high descending pseudo-scores rather than zeros.
   /// <para>
   /// The scores matter as much as the order. This previously emitted 0.0 for every candidate, which is
   /// indistinguishable from the reranker having genuinely judged everything irrelevant — so a downstream
   /// relevance gate reads a reranker OUTAGE as "this corpus has no answer" and returns nothing for every
   /// query. That is the opposite of failing soft, and it is what happened when the endpoint began
   /// rejecting requests: search silently went from working to returning nothing at all, with the
   /// reranker's own fallback causing it.
   /// </para>
   /// <para>
   /// Emitting descending values just under 1.0 preserves the retrieval order, keeps the relative spacing
   /// tight enough that no ratio-based gate discards anything, and matches BgeReranker's fallback so the
   /// local and hosted paths degrade identically.
   /// </para>
   /// </summary>
   private static IReadOnlyList<RerankerResult> FallbackOriginalOrder( IReadOnlyList<RerankerCandidate> candidates, int topK )
   {
      return candidates.Take( topK )
         .Select( ( candidate, i ) => new RerankerResult( candidate.OriginalIndex, 1.0 - ( i * 0.001 ) ) )
         .ToList();
   }

   #endregion
}
