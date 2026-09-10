using System.Collections.Generic;
using System.Linq;
using AzureDevOpsForager.Core;
using AzureDevOpsForager.Eval.Models;

namespace AzureDevOpsForager.Eval.Services;

/// <summary>
/// Expands a sweep specification into the concrete list of configurations to run, taking the
/// cartesian product of the candidate values for each knob.
///
/// Any knob the spec leaves empty is pinned to whatever is currently in <see cref="Config"/>, so a
/// spec that varies one knob measures that knob against the shipping defaults instead of against an
/// arbitrary set of other changes. That is the difference between an experiment and a coincidence.
/// </summary>
public class SweepPlanner
{
   #region Public Methods

   /// <summary>
   /// Builds the configurations described by the spec. Configurations come back in a stable order so
   /// that two runs of the same spec produce comparable reports, and the list is capped when the
   /// spec asks for a ceiling.
   /// </summary>
   /// <param name="spec">The sweep specification, or null to produce a single baseline configuration.</param>
   /// <returns>The configurations to run, each with its display name already assigned.</returns>
   public List<RunConfiguration> Plan( SweepSpec spec )
   {
      spec = spec ?? new SweepSpec();

      var vectorWeights = OrDefault( spec.RrfVectorWeight, Config.RrfVectorWeight );
      var chunkWeights = OrDefault( spec.RrfChunkFtsWeight, Config.RrfChunkFtsWeight );
      var fileWeights = OrDefault( spec.RrfFileFtsWeight, Config.RrfFileFtsWeight );
      var minRanks = OrDefault( spec.MinFtsRank, Config.MinFtsRank );
      var distances = OrDefault( spec.MaxVectorDistance, Config.MaxVectorDistance );
      var rerankFlags = OrDefault( spec.RerankerEnabled, Config.RerankerEnabled );
      var rerankSizes = OrDefault( spec.RerankerInputSize, Config.RerankerInputSize );
      var embedders = OrDefault( spec.Embedder, DefaultEmbedderBackend() );
      var rerankers = OrDefault( spec.Reranker, DefaultRerankerBackend() );

      var configurations = new List<RunConfiguration>();

      foreach( var vectorWeight in vectorWeights )
         foreach( var chunkWeight in chunkWeights )
            foreach( var fileWeight in fileWeights )
               foreach( var minRank in minRanks )
                  foreach( var distance in distances )
                     foreach( var embedder in embedders )
                        foreach( var rerankEnabled in rerankFlags )
                           configurations.AddRange( ExpandRerankVariants(
                              vectorWeight, chunkWeight, fileWeight, minRank, distance,
                              embedder, rerankEnabled, rerankSizes, rerankers ) );

      var deduplicated = Deduplicate( configurations );

      if( spec.MaxConfigurations > 0 && deduplicated.Count > spec.MaxConfigurations )
         return deduplicated.Take( spec.MaxConfigurations ).ToList();

      return deduplicated;
   }

   #endregion Public Methods

   #region Private Methods

   /// <summary>
   /// Produces the rerank-related variants for one otherwise-fixed configuration. When reranking is
   /// off, the pool size and backend are irrelevant, so this emits exactly one configuration rather
   /// than a family of identical runs that would differ only in fields nothing reads.
   /// </summary>
   /// <param name="vectorWeight">Vector leg RRF weight for these variants.</param>
   /// <param name="chunkWeight">Chunk full-text RRF weight for these variants.</param>
   /// <param name="fileWeight">File full-text RRF weight for these variants.</param>
   /// <param name="minRank">Minimum full-text rank for these variants.</param>
   /// <param name="distance">Vector distance ceiling for these variants.</param>
   /// <param name="embedder">Embedding backend for these variants.</param>
   /// <param name="rerankEnabled">Whether the rerank stage runs.</param>
   /// <param name="rerankSizes">Candidate rerank pool sizes.</param>
   /// <param name="rerankers">Candidate rerank backends.</param>
   /// <returns>The configurations for this combination.</returns>
   private List<RunConfiguration> ExpandRerankVariants(
      int vectorWeight, int chunkWeight, int fileWeight, int minRank, double distance,
      string embedder, bool rerankEnabled, List<int> rerankSizes, List<string> rerankers )
   {
      var variants = new List<RunConfiguration>();

      var sizes = rerankEnabled ? rerankSizes : new List<int> { Config.RerankerInputSize };
      var backends = rerankEnabled ? rerankers : new List<string> { RunConfiguration.BackendNone };

      foreach( var size in sizes )
      {
         foreach( var backend in backends )
         {
            // A "none" backend and an off switch describe the same run; normalize so the report
            // does not carry two rows that are the same experiment under different labels.
            var enabled = rerankEnabled && backend != RunConfiguration.BackendNone;

            var configuration = new RunConfiguration
            {
               RrfVectorWeight = vectorWeight,
               RrfChunkFtsWeight = chunkWeight,
               RrfFileFtsWeight = fileWeight,
               MinFtsRank = minRank,
               MaxVectorDistance = distance,
               RerankerEnabled = enabled,
               RerankerInputSize = size,
               Embedder = embedder,
               Reranker = enabled ? backend : RunConfiguration.BackendNone,
            };
            configuration.Name = configuration.BuildName();
            variants.Add( configuration );
         }
      }

      return variants;
   }

   /// <summary>
   /// Removes configurations whose generated names collide, which happens whenever normalization
   /// collapses two spec combinations onto the same actual experiment.
   /// </summary>
   /// <param name="configurations">The raw expanded configurations.</param>
   /// <returns>The configurations with duplicates removed, order preserved.</returns>
   private static List<RunConfiguration> Deduplicate( List<RunConfiguration> configurations )
   {
      var seen = new HashSet<string>();
      var unique = new List<RunConfiguration>();

      foreach( var configuration in configurations )
         if( seen.Add( configuration.Name ) )
            unique.Add( configuration );

      return unique;
   }

   /// <summary>
   /// Returns the spec's candidate list when it has entries, or a single-element list holding the
   /// current default. This is what lets a one-knob spec stay a one-knob experiment.
   /// </summary>
   /// <typeparam name="T">The knob's value type.</typeparam>
   /// <param name="values">Candidate values from the spec; may be null or empty.</param>
   /// <param name="fallback">The value currently configured, used when no candidates were given.</param>
   /// <returns>The list of values to iterate for this knob.</returns>
   private static List<T> OrDefault<T>( List<T> values, T fallback )
   {
      if( values == null || values.Count == 0 )
         return new List<T> { fallback };

      return values;
   }

   /// <summary>
   /// Picks the embedding backend that matches how the server would wire itself right now, so an
   /// unspecified sweep measures the deployed behaviour rather than a hypothetical one.
   /// </summary>
   /// <returns>"hosted" when a Hugging Face endpoint is configured, otherwise "local".</returns>
   private static string DefaultEmbedderBackend()
   {
      return Config.HuggingFaceEnabled ? RunConfiguration.BackendHosted : RunConfiguration.BackendLocal;
   }

   /// <summary>
   /// Picks the rerank backend matching the server's current wiring, mirroring the same precedence:
   /// hosted endpoint first, then a local ONNX model, then no rerank at all.
   /// </summary>
   /// <returns>The rerank backend identifier the server would select today.</returns>
   private static string DefaultRerankerBackend()
   {
      if( !Config.RerankerEnabled )
         return RunConfiguration.BackendNone;

      if( Config.HuggingFaceEnabled && !string.IsNullOrWhiteSpace( Config.HuggingFaceRerankUrl ) )
         return RunConfiguration.BackendHosted;

      return RunConfiguration.BackendLocal;
   }

   #endregion Private Methods
}
