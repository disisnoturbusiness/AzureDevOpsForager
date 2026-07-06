using System.Collections.Generic;
using System.Threading.Tasks;

namespace AzureDevOpsForager.Core.Services.Embedding;
/// <summary>
/// Abstraction over "turn text into a 1024-dim vector" so the query + passage embedding path can be
/// satisfied either in-process by the local ONNX <see cref="EmbeddingService"/> or remotely by
/// <see cref="HuggingFaceEmbedder"/>. Both implementations apply the E5 "query: " / "passage: "
/// prefixes and return unit-length vectors, so their outputs are interchangeable and the cosine-based
/// ranking stays valid no matter which one is wired in.
///
/// Both a synchronous and an asynchronous form of each operation are exposed. Server request handlers
/// (an inherently async, thread-pool-bound context) should prefer the *Async members so a remote HF
/// embed does not block an ASP.NET request thread on network I/O via GetAwaiter().GetResult(); the
/// synchronous members remain for the local ONNX path and simple call sites. The local implementation
/// runs in-process, so its async members complete synchronously over already-computed results.
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

   /// <summary>
   /// Async form of <see cref="EmbedQuery"/>. On the remote HF implementation this awaits the network
   /// call instead of blocking a thread; on the local ONNX implementation it completes synchronously.
   /// </summary>
   Task<float[]> EmbedQueryAsync( string text );

   /// <summary>
   /// Async form of <see cref="EmbedPassage"/>. On the remote HF implementation this awaits the network
   /// call instead of blocking a thread; on the local ONNX implementation it completes synchronously.
   /// </summary>
   Task<float[]> EmbedPassageAsync( string text );

   /// <summary>Async form of <see cref="EmbedQueryBatch"/>; awaits remote calls, completes synchronously locally.</summary>
   Task<List<float[]>> EmbedQueryBatchAsync( IReadOnlyList<string> texts );

   /// <summary>Async form of <see cref="EmbedPassageBatch"/>; awaits remote calls, completes synchronously locally.</summary>
   Task<List<float[]>> EmbedPassageBatchAsync( IReadOnlyList<string> texts );
}
