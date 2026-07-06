using System.Collections.Generic;

namespace AzureDevOpsForager.Core.Models.Search;

/// <summary>
/// A single result after the full-text (FTS) and vector search paths have been merged into
/// one hybrid ranking. A given file can surface from either path or both, so this object
/// carries the raw score from each path alongside the blended <see cref="FinalScore"/> that
/// the ranking layer computes. Keeping both source scores (rather than only the blend) lets
/// callers explain why a file ranked where it did and tune the blending weights without
/// re-running either search. It is a plain data-transfer object: the search pipeline fills it
/// in and downstream consumers (ranking, display, reranking) read from it.
/// </summary>
public class CombinedResult
{
   #region Data Members

   /// <summary>
   /// Path of the source file this result points back to. It also acts as the natural key for
   /// de-duplicating a file that was returned by both the full-text and vector paths, so the
   /// two per-path scores can be folded onto a single combined entry.
   /// </summary>
   public string FilePath
   {
      get; set;
   }

   /// <summary>
   /// The indexed textual content of the matched file, carried through so callers can render a
   /// snippet or preview without having to re-read the file from disk.
   /// </summary>
   public string Content
   {
      get; set;
   }

   /// <summary>
   /// Descriptive metadata captured for the file at index time, kept as free-form key/value
   /// pairs (for example symbol names, file type, or repository context). Held here so results
   /// can be enriched or filtered without a second lookup.
   /// </summary>
   public Dictionary<string, string> Metadata
   {
      get; set;
   }

   /// <summary>
   /// The BM25 relevance score from the full-text path. Zero when this file did not surface
   /// from full-text search. Retained separately from the blend so the keyword contribution to
   /// the final ranking stays visible and tunable.
   /// </summary>
   public double FtsRank
   {
      get; set;
   }

   /// <summary>
   /// The similarity score from the vector path (higher means a closer embedding match). Zero
   /// when this file did not surface from vector search. Kept alongside <see cref="FtsRank"/>
   /// so both source signals remain inspectable after blending.
   /// </summary>
   public float VectorScore
   {
      get; set;
   }

   /// <summary>
   /// The blended hybrid score derived from <see cref="FtsRank"/> and <see cref="VectorScore"/>
   /// by the ranking layer. This is the value callers sort on to order the combined result set.
   /// </summary>
   public double FinalScore
   {
      get; set;
   }

   #endregion
}
