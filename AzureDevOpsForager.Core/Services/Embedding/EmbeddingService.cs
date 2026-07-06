using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using AzureDevOpsForager.Core.Models.Embedding;
using Microsoft.ML.OnnxRuntime;
using Microsoft.ML.OnnxRuntime.Tensors;

namespace AzureDevOpsForager.Core.Services.Embedding;

/// <summary>
/// Turns arbitrary text into a fixed-length numeric vector (an "embedding") that captures its
/// meaning, so that semantically similar text ends up close together in vector space. This is the
/// backbone of the forager's semantic search: both the code chunks we index and the questions users
/// ask get pushed through this same service, and we then compare the resulting vectors to find
/// relevant matches.
///
/// The heavy lifting is done locally by an ONNX model (E5-large-v2) via ONNX Runtime. Running the
/// model in-process is a deliberate design choice: it means no Python dependency, no external API,
/// and no per-call cost, which keeps the give-away trivial for a self-hoster to stand up.
///
/// E5-large-v2 has a few contracts that differ from the older all-mpnet-base-v2 model and that the
/// rest of this class encodes:
/// - It emits 1024-dimensional vectors (mpnet emitted 768).
/// - It accepts up to 512 tokens per input (mpnet accepted 384).
/// - It expects input text to carry a "query: " or "passage: " prefix so it knows which side of a
///   search pair the text represents; see <see cref="EmbedQuery"/> / <see cref="EmbedPassage"/>.
/// - Its graph takes three inputs (input_ids + attention_mask + token_type_ids) rather than two.
/// </summary>
public class EmbeddingService : IDisposable, IEmbedder
{
   #region Data Members

   /// <summary>
   /// The loaded ONNX Runtime inference session that actually executes the embedding model. Created
   /// once in the constructor and reused for every call, since loading the model is expensive.
   /// </summary>
   private readonly InferenceSession _session;

   /// <summary>
   /// Converts raw text into the token id / attention mask arrays the model expects. Paired with the
   /// same vocabulary the model was trained on, so tokenization stays consistent with the weights.
   /// </summary>
   private readonly Tokenizer _tokenizer;

   /// <summary>
   /// Maximum number of tokens fed to the model in a single call. E5-large-v2 tops out at 512;
   /// anything longer is truncated by the tokenizer so the model's input shape stays valid.
   /// </summary>
   private readonly int _maxLength = 512;

   /// <summary>
   /// Whether the loaded model's graph declares a "token_type_ids" input. E5 models require it (all
   /// zeros for our single-sequence inputs) while mpnet-style models do not, so we detect it once at
   /// load time and only supply the tensor when the model asks for it.
   /// </summary>
   private readonly bool _usesTokenTypeIds;

   /// <summary>
   /// Guards against double-disposal so <see cref="Dispose"/> is safe to call more than once.
   /// </summary>
   private bool _disposed;

   #endregion Data Members

   #region Constructor

   /// <summary>
   /// Loads the ONNX embedding model and its tokenizer into memory and prepares the reusable
   /// inference session. Both paths default to the platform-configured locations in
   /// <see cref="Config"/> so callers normally construct this with no arguments; the parameters
   /// exist mainly to point at an alternate model during testing.
   /// </summary>
   /// <param name="modelPath">Path to the .onnx model file. Falls back to <see cref="Config.OnnxModelPath"/> when null.</param>
   /// <param name="vocabPath">Path to the tokenizer vocabulary. Falls back to <see cref="Config.TokenizerPath"/> when null.</param>
   public EmbeddingService( string modelPath = null, string vocabPath = null )
   {
      modelPath ??= Config.OnnxModelPath;
      vocabPath ??= Config.TokenizerPath;

      // Fail loudly at construction rather than on the first embed call, so a missing model asset is
      // an obvious startup error instead of a confusing runtime one deep inside a search.
      if( !File.Exists( modelPath ) )
         throw new FileNotFoundException( $"ONNX model not found: {modelPath}" );

      if( !File.Exists( vocabPath ) )
         throw new FileNotFoundException( $"Tokenizer vocab not found: {vocabPath}" );

      var sessionOptions = new SessionOptions();
      sessionOptions.GraphOptimizationLevel = GraphOptimizationLevel.ORT_ENABLE_ALL;
      _session = new InferenceSession( modelPath, sessionOptions );

      _tokenizer = new Tokenizer( vocabPath );

      // Probe the model's declared inputs once. E5 exposes token_type_ids; mpnet does not.
      _usesTokenTypeIds = _session.InputMetadata.ContainsKey( "token_type_ids" );

      Console.WriteLine( $"[EMBEDDINGS] Loaded model: {Path.GetFileName( modelPath )}" );
      Console.WriteLine( $"[EMBEDDINGS] Output dimension: {Config.EmbeddingDimension}" );
      Console.WriteLine( $"[EMBEDDINGS] Max tokens: {_maxLength}" );
      Console.WriteLine( $"[EMBEDDINGS] Uses token_type_ids: {_usesTokenTypeIds}" );
   }

   #endregion Constructor

   #region Public Methods

   /// <summary>
   /// Embeds a user's search question. E5 models are trained on asymmetric query/passage pairs, so
   /// the "query: " prefix tells the model this text is the question side of the pair; using it (and
   /// the matching passage prefix on indexed content) is what makes retrieval accurate.
   /// </summary>
   public float[] EmbedQuery( string text )
   {
      return Embed( "query: " + text );
   }

   /// <summary>
   /// Embeds a passage (a code chunk being indexed). The "passage: " prefix marks this as the
   /// document side of an E5 query/passage pair, mirroring <see cref="EmbedQuery"/>.
   /// </summary>
   public float[] EmbedPassage( string text )
   {
      return Embed( "passage: " + text );
   }

   /// <summary>
   /// Embeds text exactly as given, with no query/passage prefix. Prefer
   /// <see cref="EmbedQuery"/> / <see cref="EmbedPassage"/> for real search work; this raw entry
   /// point exists for callers that have already prefixed their text or genuinely want no prefix.
   ///
   /// The flow is: tokenize the text, wrap the token arrays as ONNX tensors, run the model, then
   /// mean-pool the per-token output into a single sentence vector.
   /// </summary>
   public float[] Embed( string text )
   {
      // Empty input has no meaning to embed; return a zero vector of the expected width so callers
      // (and any vector store) still get a correctly shaped result.
      if( string.IsNullOrWhiteSpace( text ) )
         return new float[Config.EmbeddingDimension];

      var tokens = _tokenizer.Tokenize( text, _maxLength );
      var modelInputs = BuildModelInputs( tokens );

      using var outputs = _session.Run( modelInputs );

      // The model returns per-token hidden states; the first (and only) output is the token-level
      // embedding tensor we pool down into one vector.
      var tokenEmbeddings = outputs.First().AsTensor<float>();

      return MeanPooling( tokenEmbeddings, tokens.AttentionMask );
   }

   /// <summary>
   /// Embeds many passages in a single batched forward pass. Each text is prefixed "passage: " to match
   /// <see cref="EmbedPassage"/>, then all are tokenized, padded to the longest sequence IN THIS BATCH
   /// (not the model maximum) and run through the model once. One run over N stacked sequences — plus a
   /// single network round-trip when this is reached over the hosted service — is far cheaper than
   /// embedding each chunk separately, which is what makes bulk indexing practical. Per-row attention
   /// masks mean each returned vector matches what <see cref="EmbedPassage"/> would produce alone.
   /// </summary>
   /// <param name="texts">The raw passage texts to embed together as one batch.</param>
   public List<float[]> EmbedPassageBatch( IReadOnlyList<string> texts )
   {
      return EmbedBatchInternal( texts, "passage: " );
   }

   /// <summary>
   /// Embeds many search queries in a single batched forward pass, mirroring <see cref="EmbedPassageBatch"/>
   /// but with the "query: " prefix E5 expects on the question side of a query/passage pair.
   /// </summary>
   /// <param name="texts">The raw query texts to embed together as one batch.</param>
   public List<float[]> EmbedQueryBatch( IReadOnlyList<string> texts )
   {
      return EmbedBatchInternal( texts, "query: " );
   }

   /// <summary>
   /// Measures how similar two embedding vectors are using cosine similarity, i.e. the cosine of the
   /// angle between them. Returns a value in roughly [-1, 1] where 1 means identical direction (most
   /// similar) and 0 means unrelated; this is the score search ranking is built on. Returns 0 when
   /// either vector has zero magnitude, since the angle is undefined in that case.
   /// </summary>
   /// <param name="vectorA">First embedding vector.</param>
   /// <param name="vectorB">Second embedding vector; must be the same length as <paramref name="vectorA"/>.</param>
   public static float CosineSimilarity( float[] vectorA, float[] vectorB )
   {
      if( vectorA.Length != vectorB.Length )
         throw new ArgumentException( "Vectors must have same length" );

      float dotProduct = 0;
      float magnitudeA = 0;
      float magnitudeB = 0;

      // Accumulate the dot product and each vector's squared magnitude in a single pass.
      for( int i = 0; i < vectorA.Length; i++ )
      {
         dotProduct += vectorA[i] * vectorB[i];
         magnitudeA += vectorA[i] * vectorA[i];
         magnitudeB += vectorB[i] * vectorB[i];
      }

      if( magnitudeA == 0 || magnitudeB == 0 )
         return 0;

      return dotProduct / ( (float)Math.Sqrt( magnitudeA ) * (float)Math.Sqrt( magnitudeB ) );
   }

   /// <summary>
   /// Releases the ONNX inference session. Implements <see cref="IDisposable"/> so the model's
   /// unmanaged resources are freed deterministically; guarded so repeated calls are harmless.
   /// </summary>
   public void Dispose()
   {
      if( _disposed )
         return;

      _session?.Dispose();
      _disposed = true;
   }

   #endregion Public Methods

   #region Private Methods

   /// <summary>
   /// Wraps the tokenizer's output arrays in the named ONNX tensors the model's graph expects. Always
   /// supplies input_ids and attention_mask; additionally supplies an all-zero token_type_ids tensor
   /// when <see cref="_usesTokenTypeIds"/> indicates the model (E5) requires it. Every tensor is
   /// shaped [1, sequenceLength] because we run one sequence at a time.
   /// </summary>
   private List<NamedOnnxValue> BuildModelInputs( TokenizerResult tokens )
   {
      int sequenceLength = tokens.InputIds.Length;

      var inputIds = new DenseTensor<long>( tokens.InputIds, new[] { 1, sequenceLength } );
      var attentionMask = new DenseTensor<long>( tokens.AttentionMask, new[] { 1, sequenceLength } );

      var inputs = new List<NamedOnnxValue>
      {
         NamedOnnxValue.CreateFromTensor( "input_ids", inputIds ),
         NamedOnnxValue.CreateFromTensor( "attention_mask", attentionMask )
      };

      // Single-sequence input means every token belongs to segment 0, hence an all-zero tensor.
      if( _usesTokenTypeIds )
      {
         var tokenTypeIds = new DenseTensor<long>( new long[sequenceLength], new[] { 1, sequenceLength } );
         inputs.Add( NamedOnnxValue.CreateFromTensor( "token_type_ids", tokenTypeIds ) );
      }

      return inputs;
   }

   /// <summary>
   /// Collapses the model's per-token embeddings into a single sentence vector by averaging across
   /// tokens, then L2-normalizes the result.
   ///
   /// Only real tokens contribute to the average: the attention mask is 1 for actual tokens and 0 for
   /// padding, so masked positions are skipped and the sum is divided by the count of real tokens.
   /// L2-normalizing at the end means every returned vector is unit length, which makes downstream
   /// cosine-similarity comparisons well behaved.
   /// </summary>
   /// <param name="tokenEmbeddings">Model output shaped [batch, sequenceLength, hiddenSize].</param>
   /// <param name="attentionMask">Per-token mask (1 = real token, 0 = padding) for the pooled sequence.</param>
   private float[] MeanPooling( Tensor<float> tokenEmbeddings, long[] attentionMask )
   {
      var dimensions = tokenEmbeddings.Dimensions.ToArray();
      int sequenceLength = dimensions[1];
      int hiddenSize = dimensions[2];

      var pooled = new float[hiddenSize];
      float realTokenCount = attentionMask.Sum();

      // No unmasked tokens (e.g. all padding) leaves nothing to average; return the zero vector.
      if( realTokenCount == 0 )
         return pooled;

      // For each embedding dimension, average that dimension's value over the real tokens only.
      for( int hiddenIndex = 0; hiddenIndex < hiddenSize; hiddenIndex++ )
      {
         float dimensionSum = 0;
         for( int sequenceIndex = 0; sequenceIndex < sequenceLength; sequenceIndex++ )
         {
            if( attentionMask[sequenceIndex] == 1 )
            {
               dimensionSum += tokenEmbeddings[0, sequenceIndex, hiddenIndex];
            }
         }
         pooled[hiddenIndex] = dimensionSum / realTokenCount;
      }

      NormalizeInPlace( pooled );
      return pooled;
   }

   /// <summary>
   /// L2-normalizes a vector in place so it has unit length, leaving it untouched if its magnitude is
   /// zero (nothing to scale). Keeping this separate lets the pooling logic stay focused on averaging.
   /// </summary>
   private static void NormalizeInPlace( float[] vector )
   {
      float magnitude = (float)Math.Sqrt( vector.Sum( component => component * component ) );
      if( magnitude > 0 )
      {
         for( int i = 0; i < vector.Length; i++ )
            vector[i] /= magnitude;
      }
   }

   /// <summary>
   /// Shared batched-inference core for <see cref="EmbedPassageBatch"/> / <see cref="EmbedQueryBatch"/>.
   /// Tokenizes every (prefixed) text to its own natural length, pads each up to the longest sequence in
   /// this batch, stacks them into [batch, sequenceLength] tensors, and runs the model exactly once.
   /// Whitespace-only inputs are embedded as a single space so the batch shape stays valid.
   /// </summary>
   /// <param name="texts">Raw texts to embed together in one forward pass.</param>
   /// <param name="prefix">The E5 side marker prepended to each text ("passage: " or "query: ").</param>
   private List<float[]> EmbedBatchInternal( IReadOnlyList<string> texts, string prefix )
   {
      if( texts == null || texts.Count == 0 )
         return new List<float[]>();

      // Tokenize each text to its own length first; the batch's padded width is the longest of these.
      var tokenized = new TokenizerResult[texts.Count];
      int sequenceLength = 1;
      for( int i = 0; i < texts.Count; i++ )
      {
         var raw = string.IsNullOrWhiteSpace( texts[i] ) ? " " : prefix + texts[i];
         tokenized[i] = _tokenizer.Tokenize( raw, _maxLength );
         if( tokenized[i].InputIds.Length > sequenceLength )
            sequenceLength = tokenized[i].InputIds.Length;
      }

      int batchSize = texts.Count;
      var inputIds = new long[batchSize * sequenceLength];
      var attentionMask = new long[batchSize * sequenceLength];
      for( int row = 0; row < batchSize; row++ )
      {
         int rowOffset = row * sequenceLength;
         var ids = tokenized[row].InputIds;
         var mask = tokenized[row].AttentionMask;
         for( int column = 0; column < ids.Length; column++ )
         {
            inputIds[rowOffset + column] = ids[column];
            attentionMask[rowOffset + column] = mask[column];
         }
         // Any remaining columns in this row stay 0 (pad id) with mask 0, i.e. ignored padding.
      }

      var shape = new[] { batchSize, sequenceLength };
      var inputs = new List<NamedOnnxValue>
      {
         NamedOnnxValue.CreateFromTensor( "input_ids", new DenseTensor<long>( inputIds, shape ) ),
         NamedOnnxValue.CreateFromTensor( "attention_mask", new DenseTensor<long>( attentionMask, shape ) )
      };
      if( _usesTokenTypeIds )
         inputs.Add( NamedOnnxValue.CreateFromTensor( "token_type_ids", new DenseTensor<long>( new long[batchSize * sequenceLength], shape ) ) );

      using var outputs = _session.Run( inputs );
      var tokenEmbeddings = outputs.First().AsTensor<float>();

      var results = new List<float[]>( batchSize );
      for( int row = 0; row < batchSize; row++ )
         results.Add( MeanPoolRow( tokenEmbeddings, attentionMask, row, sequenceLength ) );
      return results;
   }

   /// <summary>
   /// Mean-pools a single row of a batched [batch, sequenceLength, hiddenSize] output into one vector and
   /// L2-normalizes it — the batched analogue of <see cref="MeanPooling"/>. Only positions whose mask
   /// value is 1 (the row's real tokens) contribute, so batch padding is excluded.
   /// </summary>
   /// <param name="tokenEmbeddings">Batched model output, shaped [batch, sequenceLength, hiddenSize].</param>
   /// <param name="flatAttentionMask">Row-major [batch * sequenceLength] mask; 1 = real token, 0 = padding.</param>
   /// <param name="row">Index of the row (sequence) to pool.</param>
   /// <param name="sequenceLength">Padded sequence length shared by every row in the batch.</param>
   private float[] MeanPoolRow( Tensor<float> tokenEmbeddings, long[] flatAttentionMask, int row, int sequenceLength )
   {
      int hiddenSize = tokenEmbeddings.Dimensions[2];
      var pooled = new float[hiddenSize];
      int maskOffset = row * sequenceLength;

      float realTokenCount = 0;
      for( int sequenceIndex = 0; sequenceIndex < sequenceLength; sequenceIndex++ )
         realTokenCount += flatAttentionMask[maskOffset + sequenceIndex];
      if( realTokenCount == 0 )
         return pooled;

      for( int hiddenIndex = 0; hiddenIndex < hiddenSize; hiddenIndex++ )
      {
         float dimensionSum = 0;
         for( int sequenceIndex = 0; sequenceIndex < sequenceLength; sequenceIndex++ )
            if( flatAttentionMask[maskOffset + sequenceIndex] == 1 )
               dimensionSum += tokenEmbeddings[row, sequenceIndex, hiddenIndex];
         pooled[hiddenIndex] = dimensionSum / realTokenCount;
      }

      NormalizeInPlace( pooled );
      return pooled;
   }

   #endregion Private Methods
}
