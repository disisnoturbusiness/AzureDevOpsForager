using System.Collections.Generic;

namespace AzureDevOpsForager.Core.Models.Search;

/// <summary>
/// A single hit returned by a vector similarity search against SQL Server's native
/// VECTOR type (queried via the DiskANN approximate-nearest-neighbor index).
///
/// Each result ties an indexed file back to how closely its stored embedding matched
/// the query embedding, along with whatever descriptive metadata was persisted at
/// index time. It is a plain data-transfer object: the search layer populates it and
/// callers (ranking, display, downstream reranking) read from it. No behavior lives here.
/// </summary>
public class VectorResult
{
   #region Data Members

   /// <summary>
   /// Path of the source file this hit points back to. This is the human-meaningful
   /// identifier for the match, used to open, display, or further process the file
   /// that produced the matching embedding.
   /// </summary>
   public string FilePath
   {
      get; set;
   }

   /// <summary>
   /// Similarity score for this hit, as reported by the vector distance/similarity
   /// computation. Higher values indicate a closer match to the query embedding, so
   /// this is the primary key callers sort on when ranking results.
   /// </summary>
   public float Score
   {
      get; set;
   }

   /// <summary>
   /// Metadata payload captured for this file when it was indexed, kept as free-form
   /// key/value pairs (for example language, symbol names, or repository context).
   /// It carries whatever descriptive attributes the indexer chose to store so callers
   /// can enrich or filter results without a second lookup.
   /// </summary>
   public Dictionary<string, string> Payload
   {
      get; set;
   }

   #endregion
}
