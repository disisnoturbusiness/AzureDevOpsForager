namespace AzureDevOpsForager.Core.Models.API;

/// <summary>
/// Data transfer object returned by the search service's health-check endpoint.
/// It carries a single at-a-glance snapshot of how the two backing search stores are doing:
/// the full-text-search (FTS) index and the vector (embedding) store. Callers use it to decide
/// whether the search subsystem is ready to serve queries, and the counts give operators a quick
/// sense of how much content is currently indexed in each store.
/// </summary>
public class HealthResponse
{
   #region Data Members

   /// <summary>
   /// Overall health status of the search service (for example "healthy" or "unhealthy").
   /// This is the top-level verdict a caller checks first before trusting the rest of the snapshot.
   /// </summary>
   public string Status
   {
      get; set;
   }

   /// <summary>
   /// Number of files currently indexed in the full-text-search store.
   /// Serves as a rough measure of FTS coverage; a value of zero usually means nothing has been
   /// indexed yet (or the index was reset), which is a useful operational signal.
   /// </summary>
   public int FtsFileCount
   {
      get; set;
   }

   /// <summary>
   /// Number of points (embedding vectors) currently stored in the vector database.
   /// A single file can produce many vectors (one per chunk), so this count is typically larger
   /// than <see cref="FtsFileCount"/>. It is a long because vector stores can grow well past int range.
   /// </summary>
   public long VectorPointCount
   {
      get; set;
   }

   /// <summary>
   /// Health status reported specifically by the vector store, kept separate from the overall
   /// <see cref="Status"/> so callers can tell which backend is degraded when the two disagree.
   /// </summary>
   public string VectorStatus
   {
      get; set;
   }

   /// <summary>
   /// Human-readable error detail populated when the health check itself failed or a store reported
   /// a problem. Expected to be empty or null on a clean, healthy response.
   /// </summary>
   public string Error
   {
      get; set;
   }

   #endregion
}
