namespace AzureDevOpsForager.Core.Models.Search;

/// <summary>
/// Parameters for a hybrid (keyword + vector) search over the indexed Azure DevOps content.
/// This is the request body deserialized from POSTs to the /query and /chat endpoints; the
/// same shape drives both the raw search results view and the chat flow, where it is used to
/// gather context passages before the answer is generated.
/// </summary>
public class SearchRequest
{
   #region Data Members

   /// <summary>
   /// The user's natural-language search text. Feeds both the full-text (keyword) search and,
   /// when embeddings are available, the vector query, so it is the primary signal for ranking.
   /// </summary>
   public string Question
   {
      get; set;
   }

   /// <summary>
   /// Restricts results to a single module, or "All" (the default) to search across every module.
   /// Acts as a scoping filter on the full-text search rather than a ranking factor.
   /// </summary>
   public string ModuleFilter { get; set; } = "All";

   /// <summary>
   /// The maximum number of results to return (top-N). Defaults to 5. Downstream search widens
   /// its candidate pool beyond this number for reranking, then caps the final list back to it.
   /// </summary>
   public int NResults { get; set; } = 5;

   #endregion
}
