using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.ML.OnnxRuntime;
using Microsoft.ML.OnnxRuntime.Tensors;
using Microsoft.ML.Tokenizers;

namespace AzureDevOpsForager.Core.Services.Reranking;
/// <summary>
/// Cross-encoder reranker backed by BAAI/bge-reranker-v2-m3 (an XLM-RoBERTa-large model). It loads
/// an ONNX export of the model together with the SentencePiece BPE vocabulary from the folder that
/// contains <see cref="Config.RerankerModelPath"/>.
///
/// For each (query, chunk) pair the reranker tokenizes both sides with SentencePiece, applies the
/// fairseq +1 token-id offset, and assembles the XLM-R pair sequence (&lt;s&gt; q &lt;/s&gt;&lt;/s&gt;
/// d &lt;/s&gt;). All pairs for one query are stacked into a single batched inference, and the model's
/// relevance logit for each pair becomes that candidate's score.
///
/// The class is deliberately fail-soft: if the model or vocabulary cannot be loaded, or an inference
/// throws, it returns the candidates in their original retrieval order rather than propagating the
/// error. Reranking is an optional quality boost, so a broken reranker must never take down search.
/// </summary>
public sealed class BgeReranker : IReranker, IDisposable
{
   #region Data Members

   /// <summary>Token id for the XLM-R beginning-of-sequence marker (&lt;s&gt;).</summary>
   private const int XlmRBosId = 0;

   /// <summary>Token id for the XLM-R padding marker (&lt;pad&gt;), used to fill short rows in a batch.</summary>
   private const int XlmRPadId = 1;

   /// <summary>Token id for the XLM-R end-of-sequence / separator marker (&lt;/s&gt;).</summary>
   private const int XlmREosId = 2;

   /// <summary>
   /// Offset added to every SentencePiece piece id to map it into the XLM-R model's vocabulary.
   /// XLM-R (via fairseq) reserves the low ids for special tokens, so a raw SentencePiece id of N
   /// corresponds to model id N+1.
   /// </summary>
   private const int FairseqOffset = 1;

   /// <summary>
   /// Maximum token count the model accepts per sequence. Longer (query, document) pairs are
   /// truncated to fit; see <see cref="ScoreBatch"/> for the truncation budget logic.
   /// </summary>
   private const int MaxSequenceLength = 512;

   /// <summary>
   /// How long to wait after a failed initialization before trying to load the model again. This
   /// prevents a missing or broken model file from hammering disk and logs on every search.
   /// </summary>
   private static readonly TimeSpan RetryInterval = TimeSpan.FromMinutes( 5 );

   /// <summary>
   /// Explicit path to the ONNX model file, or null to fall back to <see cref="Config.RerankerModelPath"/>.
   /// Captured at construction and resolved lazily on first use.
   /// </summary>
   private readonly string _modelPath;

   /// <summary>
   /// Guards the one-time lazy initialization so concurrent searches don't try to load the model
   /// twice. Only one caller performs the load; the rest wait and then observe the result.
   /// </summary>
   private readonly SemaphoreSlim _initLock = new SemaphoreSlim( 1, 1 );

   /// <summary>The loaded ONNX inference session; null until a successful init, and after disposal.</summary>
   private InferenceSession _session;

   /// <summary>The SentencePiece tokenizer that produces piece ids for the query and documents.</summary>
   private SentencePieceTokenizer _sentencePieceTokenizer;

   /// <summary>True once the model and tokenizer have loaded successfully at least once.</summary>
   private bool _initialized;

   /// <summary>True when the last init attempt failed; combined with the retry interval to throttle retries.</summary>
   private bool _initFailed;

   /// <summary>Timestamp of the most recent init attempt, used to enforce <see cref="RetryInterval"/>.</summary>
   private DateTime _lastInitAttempt = DateTime.MinValue;

   /// <summary>True once <see cref="Dispose"/> has run, so disposal is idempotent.</summary>
   private bool _disposed;

   #endregion

   #region Constructor

   /// <summary>
   /// Creates a reranker. The model is not loaded here; loading happens lazily on the first
   /// <see cref="RerankAsync"/> call so that constructing the service is cheap and cannot fail.
   /// </summary>
   /// <param name="modelPath">
   /// Optional explicit path to the ONNX model file. When null (the default), the path is resolved
   /// from <see cref="Config.RerankerModelPath"/> at load time.
   /// </param>
   public BgeReranker( string modelPath = null )
   {
      _modelPath = modelPath;   // null means resolve from Config.RerankerModelPath later
   }

   #endregion

   #region Public Methods

   /// <summary>
   /// Whether the model and tokenizer are loaded and ready to score. Callers can use this to decide
   /// whether reranking will actually run, though <see cref="RerankAsync"/> is safe to call either
   /// way because it falls back to the original order when the model is unavailable.
   /// </summary>
   public bool IsModelLoaded =>
      _initialized && !_initFailed && _session != null && _sentencePieceTokenizer != null;

   /// <summary>
   /// Rescores the candidates against the query with the cross-encoder and returns the best topK,
   /// ordered by descending relevance. This is the <see cref="IReranker"/> implementation; it is
   /// fail-soft and never throws for model or inference problems, only for genuine cancellation.
   /// </summary>
   /// <param name="query">The user's search query, read jointly with each candidate's preview.</param>
   /// <param name="candidates">The first-stage shortlist to rescore.</param>
   /// <param name="topK">Maximum number of results to return; non-positive means "all candidates".</param>
   /// <param name="cancellationToken">Cancels the potentially slow model inference.</param>
   public async Task<IReadOnlyList<RerankerResult>> RerankAsync(
      string query, IReadOnlyList<RerankerCandidate> candidates, int topK, CancellationToken cancellationToken = default )
   {
      // Trivial shortlists don't need the model: nothing to score, or a single obvious winner.
      if( candidates.Count == 0 ) return Array.Empty<RerankerResult>();
      if( topK <= 0 ) topK = candidates.Count;
      if( candidates.Count == 1 ) return new[] { new RerankerResult( candidates[0].OriginalIndex, 1.0 ) };

      await EnsureInitializedAsync( cancellationToken ).ConfigureAwait( false );
      if( !IsModelLoaded )
         return FallbackOriginalOrder( candidates, topK );

      try
      {
         cancellationToken.ThrowIfCancellationRequested();

         var documentPreviews = new string[candidates.Count];
         for( int i = 0; i < candidates.Count; i++ )
            documentPreviews[i] = candidates[i].Preview ?? string.Empty;

         var scores = ScoreBatch( query, documentPreviews );
         return candidates
            .Select( ( candidate, i ) => new RerankerResult( candidate.OriginalIndex, scores[i] ) )
            .OrderByDescending( result => result.Score )
            .Take( topK )
            .ToList();
      }
      catch( OperationCanceledException ) when( cancellationToken.IsCancellationRequested )
      {
         // Cancellation is a real caller intent, not a model failure; let it propagate.
         throw;
      }
      catch( Exception exception )
      {
         // Any other failure degrades gracefully to plain first-stage order.
         Console.WriteLine( $"[RERANK] rerank failed, using retrieval order: {exception.Message}" );
         return FallbackOriginalOrder( candidates, topK );
      }
   }

   /// <summary>
   /// Releases the ONNX session and the init lock. Idempotent: safe to call more than once. This
   /// satisfies <see cref="IDisposable"/> so the reranker can own the unmanaged inference session.
   /// </summary>
   public void Dispose()
   {
      if( _disposed ) return;
      _session?.Dispose();
      _initLock.Dispose();
      _disposed = true;
   }

   #endregion

   #region Private Methods

   /// <summary>
   /// Scores every (query, document) pair in one batched inference. All pairs are packed into a
   /// single [batch, maxLen] tensor, padded to the longest row, and the model returns one relevance
   /// logit per pair which becomes that candidate's score.
   /// </summary>
   /// <param name="query">The query text, tokenized once and reused for every pair.</param>
   /// <param name="documents">The document previews, one per candidate.</param>
   /// <returns>One relevance score per document, in the same order as <paramref name="documents"/>.</returns>
   private double[] ScoreBatch( string query, IReadOnlyList<string> documents )
   {
      var queryModelIds = ToOffsetArray(
         _sentencePieceTokenizer.EncodeToIds( query, considerPreTokenization: true, considerNormalization: true ) );

      var pairs = BuildTokenPairs( queryModelIds, documents, out int maxSequenceLength );
      return RunInference( pairs, maxSequenceLength );
   }

   /// <summary>
   /// Tokenizes each document and pairs it with the query, truncating any pair that would exceed
   /// <see cref="MaxSequenceLength"/>. The document side is trimmed first; only if the query alone
   /// is already too long is the query trimmed as well (always leaving room for at least one
   /// document token). Also reports the longest resulting sequence so the caller can size the batch.
   /// </summary>
   /// <param name="queryModelIds">The query's model token ids, shared across all pairs.</param>
   /// <param name="documents">The document previews to tokenize and pair.</param>
   /// <param name="maxSequenceLength">Receives the longest total token count across all pairs.</param>
   /// <returns>One tuple per document holding its (possibly trimmed) query ids, document ids, and total length.</returns>
   private ( long[] Query, long[] Document, int Total )[] BuildTokenPairs(
      long[] queryModelIds, IReadOnlyList<string> documents, out int maxSequenceLength )
   {
      // Fixed structural tokens per XLM-R pair sequence: <s>, </s>, </s>, </s>.
      const int fixedTokens = 4;

      var pairs = new ( long[] Query, long[] Document, int Total )[documents.Count];
      maxSequenceLength = 0;

      for( int i = 0; i < documents.Count; i++ )
      {
         var documentModelIds = ToOffsetArray(
            _sentencePieceTokenizer.EncodeToIds( documents[i], considerPreTokenization: true, considerNormalization: true ) );
         var pairQueryIds = queryModelIds;
         int total = fixedTokens + pairQueryIds.Length + documentModelIds.Length;

         if( total > MaxSequenceLength )
         {
            int documentBudget = MaxSequenceLength - fixedTokens - pairQueryIds.Length;
            if( documentBudget < 1 )
            {
               // The query alone overflows: trim it, reserving one slot for a document token.
               int queryBudget = MaxSequenceLength - fixedTokens - 1;
               if( queryBudget < 1 ) queryBudget = 1;
               pairQueryIds = Head( pairQueryIds, queryBudget );
               documentBudget = 1;
            }
            documentModelIds = Head( documentModelIds, Math.Min( documentBudget, documentModelIds.Length ) );
            total = fixedTokens + pairQueryIds.Length + documentModelIds.Length;
         }

         pairs[i] = ( pairQueryIds, documentModelIds, total );
         if( total > maxSequenceLength ) maxSequenceLength = total;
      }

      return pairs;
   }

   /// <summary>
   /// Builds the padded input tensors from the token pairs, runs the ONNX session, and reads the
   /// per-row relevance logit (column 0) as each candidate's score. Rows shorter than the batch
   /// width are padded with <see cref="XlmRPadId"/> and masked off via the attention mask.
   /// </summary>
   /// <param name="pairs">The tokenized (query, document) pairs to score.</param>
   /// <param name="maxSequenceLength">The batch width every row is padded to.</param>
   /// <returns>One relevance score per pair, in input order.</returns>
   private double[] RunInference( ( long[] Query, long[] Document, int Total )[] pairs, int maxSequenceLength )
   {
      int batchSize = pairs.Length;
      var inputIds = new long[batchSize * maxSequenceLength];
      var attentionMask = new long[batchSize * maxSequenceLength];

      // Pre-fill every slot with padding; real tokens overwrite the leading slots of each row.
      for( int i = 0; i < inputIds.Length; i++ ) inputIds[i] = XlmRPadId;

      for( int batchRow = 0; batchRow < batchSize; batchRow++ )
      {
         var pair = pairs[batchRow];
         int rowOffset = batchRow * maxSequenceLength;
         int tokenIndex = 0;

         // Assemble the XLM-R pair sequence: <s> query </s></s> document </s>.
         inputIds[rowOffset + tokenIndex++] = XlmRBosId;
         foreach( var id in pair.Query ) inputIds[rowOffset + tokenIndex++] = id;
         inputIds[rowOffset + tokenIndex++] = XlmREosId;
         inputIds[rowOffset + tokenIndex++] = XlmREosId;
         foreach( var id in pair.Document ) inputIds[rowOffset + tokenIndex++] = id;
         inputIds[rowOffset + tokenIndex++] = XlmREosId;

         // Mask covers only the real tokens; the padded tail stays zero.
         for( int i = 0; i < pair.Total; i++ ) attentionMask[rowOffset + i] = 1;
      }

      var inputIdsTensor = new DenseTensor<long>( inputIds, new[] { batchSize, maxSequenceLength } );
      var attentionMaskTensor = new DenseTensor<long>( attentionMask, new[] { batchSize, maxSequenceLength } );
      var inputs = new List<NamedOnnxValue>
      {
         NamedOnnxValue.CreateFromTensor( "input_ids", inputIdsTensor ),
         NamedOnnxValue.CreateFromTensor( "attention_mask", attentionMaskTensor )
      };

      using( var outputs = _session.Run( inputs ) )
      {
         var logits = outputs.First().AsTensor<float>();
         var scores = new double[batchSize];
         for( int batchRow = 0; batchRow < batchSize; batchRow++ ) scores[batchRow] = logits[batchRow, 0];
         return scores;
      }
   }

   /// <summary>
   /// Returns the first <paramref name="count"/> elements of the array, or the array itself when it
   /// is already that short. Used to truncate token sequences to the model's length budget.
   /// </summary>
   private static long[] Head( long[] source, int count ) => count >= source.Length ? source : source.Take( count ).ToArray();

   /// <summary>
   /// Converts raw SentencePiece piece ids into XLM-R model ids by adding <see cref="FairseqOffset"/>
   /// to each, widening to long for the ONNX tensor. See <see cref="FairseqOffset"/> for why the
   /// offset exists.
   /// </summary>
   private static long[] ToOffsetArray( IReadOnlyList<int> ids )
   {
      var result = new long[ids.Count];
      for( int i = 0; i < ids.Count; i++ ) result[i] = ids[i] + FairseqOffset;
      return result;
   }

   /// <summary>
   /// Lazily loads the ONNX model and SentencePiece vocabulary on first use, under a lock so only
   /// one caller loads while others wait. Every failure path is fail-soft: it logs, marks the
   /// attempt failed, and returns so the caller degrades to the original retrieval order. Failed
   /// attempts are retried no more often than <see cref="RetryInterval"/>.
   /// </summary>
   /// <param name="cancellationToken">Cancels the wait for the init lock.</param>
   private async Task EnsureInitializedAsync( CancellationToken cancellationToken )
   {
      if( _initialized && !_initFailed ) return;
      if( _initFailed && DateTime.UtcNow - _lastInitAttempt < RetryInterval ) return;

      await _initLock.WaitAsync( cancellationToken ).ConfigureAwait( false );
      try
      {
         // Re-check under the lock: another caller may have finished (or recently failed) while we waited.
         if( _initialized && !_initFailed ) return;
         if( _initFailed && DateTime.UtcNow - _lastInitAttempt < RetryInterval ) return;
         _lastInitAttempt = DateTime.UtcNow;

         var modelPath = string.IsNullOrWhiteSpace( _modelPath ) ? Config.RerankerModelPath : _modelPath;
         if( !TryResolveModelPaths( modelPath, out string sentencePieceModelPath ) )
         {
            _initFailed = true;
            return;
         }

         _session = new InferenceSession( modelPath );
         using( var sentencePieceStream = File.OpenRead( sentencePieceModelPath ) )
            _sentencePieceTokenizer = SentencePieceTokenizer.Create( sentencePieceStream );

         _initialized = true;
         _initFailed = false;
         Console.WriteLine( $"[RERANK] loaded bge reranker: {modelPath}" );
      }
      catch( Exception exception )
      {
         Console.WriteLine( $"[RERANK] init failed, reranker disabled: {exception.Message}" );
         _session?.Dispose();
         _session = null;
         _sentencePieceTokenizer = null;
         _initFailed = true;
      }
      finally
      {
         _initLock.Release();
      }
   }

   /// <summary>
   /// Validates that the model file exists and that its sibling SentencePiece vocabulary file is
   /// present, logging a specific reason for each failure. On success it reports the resolved
   /// vocabulary path; on any missing input it returns false so the caller can disable reranking.
   /// </summary>
   /// <param name="modelPath">The resolved ONNX model path (may be null/empty if unconfigured).</param>
   /// <param name="sentencePieceModelPath">Receives the path to sentencepiece.bpe.model on success.</param>
   /// <returns>True when both the model and the vocabulary file exist; otherwise false.</returns>
   private static bool TryResolveModelPaths( string modelPath, out string sentencePieceModelPath )
   {
      sentencePieceModelPath = null;

      if( string.IsNullOrWhiteSpace( modelPath ) )
      {
         Console.WriteLine( "[RERANK] RerankerModelPath not set — reranker disabled (RRF-only)." );
         return false;
      }
      if( !File.Exists( modelPath ) )
      {
         Console.WriteLine( $"[RERANK] model not found at '{modelPath}' — reranker disabled." );
         return false;
      }

      sentencePieceModelPath = Path.Combine( Path.GetDirectoryName( modelPath ) ?? string.Empty, "sentencepiece.bpe.model" );
      if( !File.Exists( sentencePieceModelPath ) )
      {
         Console.WriteLine( $"[RERANK] sentencepiece.bpe.model not found at '{sentencePieceModelPath}' — reranker disabled." );
         return false;
      }

      return true;
   }

   /// <summary>
   /// The fail-soft result: returns the first topK candidates in their original retrieval order,
   /// with gently descending synthetic scores so downstream sorting keeps that order stable. Used
   /// whenever the model is unavailable or an inference fails.
   /// </summary>
   private static IReadOnlyList<RerankerResult> FallbackOriginalOrder( IReadOnlyList<RerankerCandidate> candidates, int topK )
      => candidates.Take( topK ).Select( ( candidate, i ) => new RerankerResult( candidate.OriginalIndex, 1.0 - ( i * 0.001 ) ) ).ToList();

   #endregion
}
