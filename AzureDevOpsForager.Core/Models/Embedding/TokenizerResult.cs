namespace AzureDevOpsForager.Core.Models.Embedding;

/// <summary>
/// Carries the numeric output of BERT-style tokenization for a single piece of text,
/// packaged in exactly the shape the downstream ONNX embedding model expects.
/// A tokenizer turns raw words into token identifiers plus a mask that tells the model
/// which positions are real content versus padding, so this type is the hand-off contract
/// between the text-preparation step and the model-inference step of the embedding pipeline.
/// </summary>
public class TokenizerResult
{
   #region Data Members

   /// <summary>
   /// The sequence of vocabulary token identifiers produced from the input text.
   /// Each entry maps a token to its integer id in the BERT vocabulary; this array is fed
   /// directly to the ONNX model as the primary input tensor that it embeds.
   /// </summary>
   public long[] InputIds
   {
      get; set;
   }

   /// <summary>
   /// The attention mask paired one-to-one with <see cref="InputIds"/>.
   /// A value of 1 marks a real token the model should attend to, and 0 marks padding
   /// that should be ignored, which lets a batch of variable-length inputs share a fixed
   /// tensor width without letting the filler positions influence the resulting embedding.
   /// </summary>
   public long[] AttentionMask
   {
      get; set;
   }

   #endregion
}
