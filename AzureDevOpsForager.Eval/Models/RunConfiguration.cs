using System.Collections.Generic;
using System.Text;

namespace AzureDevOpsForager.Eval.Models;

/// <summary>
/// One fully-specified point in the tuning space: every knob the harness varies, pinned to a
/// single value. The runner applies one of these to the static <c>Config</c>, runs the whole
/// golden set against it, and records the metrics that come back.
///
/// Why a value object rather than mutating Config directly and hoping: a sweep only means
/// something if each measurement can be tied back to the exact settings that produced it. Holding
/// the settings as data lets them be written into the CSV alongside the numbers, so a result is
/// reproducible instead of merely observed.
/// </summary>
public class RunConfiguration
{
   #region Data Members

   /// <summary>
   /// Human-readable label for this configuration, generated from the knob values so two rows in
   /// the report can never collide or be confused for one another.
   /// </summary>
   public string Name { get; set; }

   /// <summary>Weight applied to the vector leg's reciprocal-rank contribution inside dbo.SearchCode.</summary>
   public int RrfVectorWeight { get; set; }

   /// <summary>Weight applied to the chunk-level full-text leg's reciprocal-rank contribution.</summary>
   public int RrfChunkFtsWeight { get; set; }

   /// <summary>Weight applied to the file-level full-text leg's reciprocal-rank contribution.</summary>
   public int RrfFileFtsWeight { get; set; }

   /// <summary>
   /// Minimum SQL full-text RANK a row must reach before it is allowed into the fusion. Raising it
   /// trades recall for precision by discarding weak keyword noise before it can occupy a slot.
   /// </summary>
   public int MinFtsRank { get; set; }

   /// <summary>
   /// Cosine distance ceiling above which a vector candidate is discarded. The single most
   /// dangerous knob in the set: put it below the embedding model's real distance floor and the
   /// vector leg silently returns nothing, leaving a search that looks healthy but is full-text only.
   /// </summary>
   public double MaxVectorDistance { get; set; }

   /// <summary>Whether the second-stage cross-encoder rerank runs at all.</summary>
   public bool RerankerEnabled { get; set; }

   /// <summary>
   /// Size of the candidate pool over-fetched from the fusion when reranking is on. Bigger pools
   /// give the cross-encoder more chance to rescue a good result the first stage ranked low, and
   /// cost latency roughly linearly, which is exactly the trade this harness exists to measure.
   /// </summary>
   public int RerankerInputSize { get; set; }

   /// <summary>
   /// Which embedding backend produces the query vector: <see cref="BackendLocal"/> or
   /// <see cref="BackendHosted"/>. Note that this cannot be swept freely against a fixed index,
   /// because the query vector must come from the same model as the stored vectors; the runner
   /// enforces that.
   /// </summary>
   public string Embedder { get; set; }

   /// <summary>
   /// Which cross-encoder backend rescores the shortlist: <see cref="BackendLocal"/>,
   /// <see cref="BackendHosted"/>, or <see cref="BackendNone"/>.
   /// </summary>
   public string Reranker { get; set; }

   /// <summary>Identifier for the locally hosted ONNX embedding or reranking model.</summary>
   public const string BackendLocal = "local";

   /// <summary>Identifier for the remotely hosted Hugging Face inference endpoint.</summary>
   public const string BackendHosted = "hosted";

   /// <summary>Identifier meaning "do not run this stage at all".</summary>
   public const string BackendNone = "none";

   #endregion Data Members

   #region Public Methods

   /// <summary>
   /// Builds the deterministic display name for this configuration from its knob values, so the
   /// same settings always produce the same label across runs and the report can be diffed.
   /// Called by the planner once the knobs are assigned.
   /// </summary>
   /// <returns>A compact label such as "v60-c30-f30-fts10-d2.00-rr(hosted,30)-emb(hosted)".</returns>
   public string BuildName()
   {
      var builder = new StringBuilder();
      builder.Append( "v" ).Append( RrfVectorWeight );
      builder.Append( "-c" ).Append( RrfChunkFtsWeight );
      builder.Append( "-f" ).Append( RrfFileFtsWeight );
      builder.Append( "-fts" ).Append( MinFtsRank );
      builder.Append( "-d" ).Append( MaxVectorDistance.ToString( "0.00" ) );
      builder.Append( RerankerEnabled ? $"-rr({Reranker},{RerankerInputSize})" : "-rr(off)" );
      builder.Append( "-emb(" ).Append( Embedder ).Append( ")" );

      return builder.ToString();
   }

   /// <summary>
   /// Produces the ordered column values describing this configuration, used to write the knob
   /// columns of the results CSV. Kept beside <see cref="ConfigurationColumnHeaders"/> so the
   /// headers and the values cannot drift apart.
   /// </summary>
   /// <returns>Knob values as strings, in the same order as the headers.</returns>
   public List<string> ToColumnValues()
   {
      return new List<string>
      {
         Name,
         RrfVectorWeight.ToString(),
         RrfChunkFtsWeight.ToString(),
         RrfFileFtsWeight.ToString(),
         MinFtsRank.ToString(),
         MaxVectorDistance.ToString( "0.####" ),
         RerankerEnabled ? "true" : "false",
         RerankerInputSize.ToString(),
         Embedder,
         Reranker,
      };
   }

   /// <summary>
   /// The CSV headers matching <see cref="ToColumnValues"/>. Static because the header row is a
   /// property of the format rather than of any one configuration.
   /// </summary>
   /// <returns>Column headers describing the swept knobs.</returns>
   public static List<string> ConfigurationColumnHeaders()
   {
      return new List<string>
      {
         "config", "rrf_vector_w", "rrf_chunk_fts_w", "rrf_file_fts_w",
         "min_fts_rank", "max_vector_distance", "reranker_enabled",
         "reranker_input_size", "embedder", "reranker",
      };
   }

   #endregion Public Methods
}
