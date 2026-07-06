using System.Collections.Generic;

namespace AzureDevOpsForager.Core.Services.Embedding
{
   /// <summary>
   /// Abstraction over "turn text into a 1024-dim vector" so the query + passage embedding path can be
   /// satisfied either in-process by the local ONNX <see cref="EmbeddingService"/> or remotely by
   /// <see cref="HuggingFaceEmbedder"/>. Both implementations apply the E5 "query: " / "passage: "
   /// prefixes and return unit-length vectors, so their outputs are interchangeable and the cosine-based
   /// ranking stays valid no matter which one is wired in.
   /// </summary>
   public interface IEmbedder
   {
      /// <summary>Embeds a search query (adds the E5 "query: " prefix). Returns a unit-length 1024-dim vector.</summary>
      float[] EmbedQuery( string text );

      /// <summary>Embeds a passage / code chunk (adds the E5 "passage: " prefix). Returns a unit-length 1024-dim vector.</summary>
      float[] EmbedPassage( string text );

      /// <summary>Embeds many queries; equivalent to calling <see cref="EmbedQuery"/> per item.</summary>
      List<float[]> EmbedQueryBatch( IReadOnlyList<string> texts );

      /// <summary>Embeds many passages; equivalent to calling <see cref="EmbedPassage"/> per item.</summary>
      List<float[]> EmbedPassageBatch( IReadOnlyList<string> texts );
   }
}
