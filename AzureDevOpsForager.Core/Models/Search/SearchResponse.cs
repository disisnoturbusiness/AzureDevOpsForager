using System.Collections.Generic;

namespace AzureDevOpsForager.Core.Models.Search;

/// <summary>
/// Data-transfer object returned by the hybrid search pipeline (vector similarity fused with
/// full-text ranking, optionally cross-encoder reranked). The collection shape mirrors the
/// ChromaDB response contract: the OUTER list is the batch of queries and the INNER list is the
/// per-result payload for that query. In practice this service issues one query at a time, so each
/// outer list holds a single inner list, but the nested shape is kept so callers built against the
/// Chroma convention consume it unchanged. The three parallel collections (Ids, Documents,
/// Metadatas) are index-aligned: element N of each describes the same hit.
/// </summary>
public class SearchResponse
{
   #region Data Members

   /// <summary>
   /// Identifiers for each hit, in ranked order. For this codebase the identifier is the file path
   /// of the matched source document. Outer list = per-query batch; inner list = the ranked hits
   /// for that query.
   /// </summary>
   public List<List<string>> Ids { get; set; } = new();

   /// <summary>
   /// The document text for each hit, index-aligned with <see cref="Ids"/>. The top hit is returned
   /// in full while lower-ranked hits are truncated to keep the payload small, so downstream callers
   /// get the most relevant content verbatim without shipping every match at full length.
   /// </summary>
   public List<List<string>> Documents { get; set; } = new();

   /// <summary>
   /// Per-hit metadata bags (string-keyed) index-aligned with <see cref="Ids"/>. Carries the
   /// supplementary fields surfaced from the index, for example the rerank score written by the
   /// optional cross-encoder stage, so consumers can display or sort on them without a second lookup.
   /// </summary>
   public List<List<Dictionary<string, string>>> Metadatas { get; set; } = new();

   /// <summary>
   /// Populated with a message when the search failed; left null on success. Lets callers surface a
   /// failure to the user without the pipeline throwing across the service boundary (fail-soft).
   /// </summary>
   public string Error { get; set; }

   #endregion
}
