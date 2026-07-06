using System;
using System.Collections.Generic;
using System.IO;
using AzureDevOpsForager.Core.Models.Embedding;

namespace AzureDevOpsForager.Core.Services.Embedding;

/// <summary>
/// A lightweight BERT-style WordPiece tokenizer used to prepare text for sentence-transformer
/// embedding models. It converts free-form text (work-item titles, descriptions, comments, etc.)
/// into the numeric token-id sequence and attention mask that the downstream ONNX model consumes.
/// The vocabulary is loaded once from a plain-text vocab.txt file where the line number of each
/// token is its id, matching the format published alongside Hugging Face BERT models. Keeping the
/// tokenizer in-process (rather than calling out to a Python runtime) is what lets the forager
/// generate embeddings entirely inside the .NET application.
/// </summary>
public class Tokenizer
{
   #region Data Members

   /// <summary>
   /// Maps each vocabulary token to its integer id. Populated from vocab.txt where the zero-based
   /// line index is the id, so lookups here mirror exactly what the trained model was built against.
   /// Uses ordinal comparison because vocabulary matching must be byte-exact, never culture-aware.
   /// </summary>
   private readonly Dictionary<string, int> _vocab;

   /// <summary>
   /// Id of the "[CLS]" classification token that every BERT sequence begins with. Its final
   /// hidden state is what sentence-transformer models pool into the sentence embedding.
   /// </summary>
   private readonly int _clsId;

   /// <summary>
   /// Id of the "[SEP]" separator token appended to the end of the sequence to mark its boundary.
   /// </summary>
   private readonly int _sepId;

   /// <summary>
   /// Id of the "[UNK]" unknown token substituted whenever a word (or subword) has no vocabulary
   /// entry, ensuring every input still produces a valid token sequence.
   /// </summary>
   private readonly int _unkId;

   #endregion Data Members

   #region Constructor

   /// <summary>
   /// Loads the vocabulary from the given vocab.txt file and resolves the four special-token ids.
   /// Each line in the file is one token; its line number becomes its id. The special tokens fall
   /// back to the well-known BERT defaults (CLS=101, SEP=102, UNK=100) if the file happens
   /// not to list them, so the tokenizer is usable even against a slightly non-standard vocab.
   /// </summary>
   /// <param name="vocabPath">Path to the vocab.txt file shipped with the embedding model.</param>
   public Tokenizer( string vocabPath )
   {
      _vocab = new Dictionary<string, int>( StringComparer.Ordinal );

      var lines = File.ReadAllLines( vocabPath );
      for( int i = 0; i < lines.Length; i++ )
      {
         _vocab[lines[i]] = i;
      }

      _clsId = ResolveTokenId( "[CLS]", 101 );
      _sepId = ResolveTokenId( "[SEP]", 102 );
      _unkId = ResolveTokenId( "[UNK]", 100 );
   }

   #endregion Constructor

   #region Public Methods

   /// <summary>
   /// Tokenizes the supplied text into the fixed-width input-id and attention-mask arrays the ONNX
   /// model expects. Text is lower-cased and split on whitespace, each word is resolved through
   /// WordPiece (whole word first, then greedy longest-match subwords prefixed with "##"), the
   /// sequence is bracketed with [CLS] and [SEP], and the result is padded or truncated to exactly
   /// <paramref name="maxLength"/>. Truncation is enforced while building so the sequence never
   /// overflows and always leaves room for the closing [SEP].
   /// </summary>
   /// <param name="text">Raw text to tokenize.</param>
   /// <param name="maxLength">Fixed sequence length of the output tensors (including [CLS] and [SEP]).</param>
   /// <returns>The token ids and matching attention mask, both exactly <paramref name="maxLength"/> long.</returns>
   public TokenizerResult Tokenize( string text, int maxLength )
   {
      var tokens = new List<int> { _clsId };

      var words = text.ToLowerInvariant().Split( new[] { ' ', '\t', '\n', '\r' }, StringSplitOptions.RemoveEmptyEntries );

      foreach( var word in words )
      {
         // Leave at least one slot free for the trailing [SEP] token.
         if( tokens.Count >= maxLength - 1 )
            break;

         AddWordTokens( word, tokens, maxLength );
      }

      tokens.Add( _sepId );

      return ToTokenizerResult( tokens );
   }

   #endregion Public Methods

   #region Private Methods

   /// <summary>
   /// Looks up a special token's id, returning the given fallback when the vocabulary omits it.
   /// </summary>
   /// <param name="token">The special token text, e.g. "[CLS]".</param>
   /// <param name="fallback">The standard BERT id to use when the token is absent.</param>
   private int ResolveTokenId( string token, int fallback )
   {
      return _vocab.TryGetValue( token, out var id ) ? id : fallback;
   }

   /// <summary>
   /// Resolves a single word to one or more token ids and appends them to <paramref name="tokens"/>.
   /// The whole word is tried first for a direct vocabulary hit; failing that, WordPiece breaks it
   /// into subwords. The sequence-length cap is respected so appended tokens never overrun the tensor.
   /// </summary>
   /// <param name="word">The lower-cased word to tokenize.</param>
   /// <param name="tokens">The token-id list being accumulated for the sequence.</param>
   /// <param name="maxLength">Fixed sequence length; one slot is reserved for the trailing [SEP].</param>
   private void AddWordTokens( string word, List<int> tokens, int maxLength )
   {
      if( _vocab.TryGetValue( word, out var wordId ) )
      {
         tokens.Add( wordId );
         return;
      }

      AddWordPieceTokens( word, tokens, maxLength );
   }

   /// <summary>
   /// Applies greedy longest-match WordPiece to a word the vocabulary has no whole-word entry for.
   /// Starting from the front of the remaining text it takes the longest prefix (up to 20 chars)
   /// that exists in the vocabulary, records that subword, and continues on the rest. Every subword
   /// after the first carries the "##" continuation marker. If no prefix matches, the whole word is
   /// emitted as a single [UNK] token so the sequence stays valid.
   /// </summary>
   /// <param name="word">The lower-cased word with no direct vocabulary match.</param>
   /// <param name="tokens">The token-id list being accumulated for the sequence.</param>
   /// <param name="maxLength">Fixed sequence length; one slot is reserved for the trailing [SEP].</param>
   private void AddWordPieceTokens( string word, List<int> tokens, int maxLength )
   {
      var remaining = word;
      bool isFirstPiece = true;

      while( remaining.Length > 0 && tokens.Count < maxLength - 1 )
      {
         string continuationPrefix = isFirstPiece ? "" : "##";
         bool matched = false;

         // Greedily prefer the longest subword that is in the vocabulary.
         for( int length = Math.Min( remaining.Length, 20 ); length > 0; length-- )
         {
            var piece = continuationPrefix + remaining.Substring( 0, length );
            if( _vocab.TryGetValue( piece, out var pieceId ) )
            {
               tokens.Add( pieceId );
               remaining = remaining.Substring( length );
               matched = true;
               isFirstPiece = false;
               break;
            }
         }

         // No subword matched: fall back to a single unknown token and stop splitting this word.
         if( !matched )
         {
            tokens.Add( _unkId );
            break;
         }
      }
   }

   /// <summary>
   /// Copies the accumulated token ids into input-id and attention-mask arrays sized to the actual
   /// token count. The model is always run one sequence at a time, so there is no batch to align and
   /// therefore no need to pad: every position is a real token and gets an attention-mask value of 1.
   /// This keeps each forward pass proportional to the real text length instead of always paying for
   /// the full <paramref name="tokens"/> being stretched to the model's maximum window. The sequence
   /// is already truncated to the maximum length while being built in <see cref="Tokenize"/>, so it
   /// never overflows the model here.
   /// </summary>
   /// <param name="tokens">The complete token-id sequence, already bracketed with [CLS] and [SEP] and already truncated to the model's maximum length.</param>
   /// <returns>The input ids and an all-ones attention mask, both exactly as long as <paramref name="tokens"/>.</returns>
   private TokenizerResult ToTokenizerResult( List<int> tokens )
   {
      var inputIds = new long[tokens.Count];
      var attentionMask = new long[tokens.Count];

      for( int i = 0; i < tokens.Count; i++ )
      {
         inputIds[i] = tokens[i];
         attentionMask[i] = 1;
      }

      return new TokenizerResult
      {
         InputIds = inputIds,
         AttentionMask = attentionMask
      };
   }

   #endregion Private Methods
}
