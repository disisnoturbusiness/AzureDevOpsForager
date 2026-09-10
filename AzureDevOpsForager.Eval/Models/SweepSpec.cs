using System.Collections.Generic;

namespace AzureDevOpsForager.Eval.Models;

/// <summary>
/// The tuning space to explore, as loaded from sweep.json: one list of candidate values per knob.
/// The planner takes the cartesian product of these lists to produce the configurations to run.
///
/// Expressed as lists rather than ranges on purpose. Retrieval knobs are not smoothly continuous in
/// their effect and a stepped range invites sweeping fifty near-identical points that cost real
/// minutes each; naming the handful of values worth testing forces the choice to be deliberate.
/// Any list left empty falls back to the value currently in <c>Config</c>, so a spec can vary one
/// knob without having to restate the rest.
/// </summary>
public class SweepSpec
{
   #region Data Members

   /// <summary>Candidate values for the vector leg's RRF weight. Empty means "use the current Config value".</summary>
   public List<int> RrfVectorWeight { get; set; } = new List<int>();

   /// <summary>Candidate values for the chunk-level full-text RRF weight.</summary>
   public List<int> RrfChunkFtsWeight { get; set; } = new List<int>();

   /// <summary>Candidate values for the file-level full-text RRF weight.</summary>
   public List<int> RrfFileFtsWeight { get; set; } = new List<int>();

   /// <summary>Candidate values for the minimum full-text RANK admitted to the fusion.</summary>
   public List<int> MinFtsRank { get; set; } = new List<int>();

   /// <summary>Candidate values for the cosine distance ceiling on vector candidates.</summary>
   public List<double> MaxVectorDistance { get; set; } = new List<double>();

   /// <summary>Candidate on/off values for the second-stage cross-encoder rerank.</summary>
   public List<bool> RerankerEnabled { get; set; } = new List<bool>();

   /// <summary>Candidate sizes for the over-fetched candidate pool handed to the reranker.</summary>
   public List<int> RerankerInputSize { get; set; } = new List<int>();

   /// <summary>
   /// Candidate embedding backends ("local" or "hosted"). Sweeping this against a single index is
   /// only valid when both backends emit the same dimension as the stored vectors; the runner
   /// checks and skips configurations that would compare a query vector against foreign geometry.
   /// </summary>
   public List<string> Embedder { get; set; } = new List<string>();

   /// <summary>Candidate reranker backends ("local", "hosted", or "none").</summary>
   public List<string> Reranker { get; set; } = new List<string>();

   /// <summary>
   /// Optional ceiling on how many configurations to run, applied after the product is built.
   /// A safety valve: four knobs with three values each is already eighty-one full passes over the
   /// golden set, which is an overnight job rather than the ten minutes it looks like on paper.
   /// Zero or negative means no limit.
   /// </summary>
   public int MaxConfigurations { get; set; }

   #endregion Data Members
}
