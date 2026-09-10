using System.Collections.Generic;

namespace AzureDevOpsForager.Eval.Models;

/// <summary>
/// One labelled question in the golden set: the text a user would actually type, plus the
/// chunks a human decided are genuinely relevant answers to it.
///
/// This is the ground truth the whole harness rests on. Every metric the runner reports is
/// ultimately "how many of these did the search put near the top", so the honesty of a tuning
/// result is capped by the honesty of these labels. Why label by hand rather than infer: the
/// only thing that makes a retrieval change defensible is a judgement made BEFORE the config
/// was swept, by someone who knows the codebase. Labels derived from a previous run's output
/// would just re-certify whatever the search already did.
/// </summary>
public class GoldenQuery
{
   #region Data Members

   /// <summary>
   /// Stable short identifier for this query, used as the join key in the per-query CSV so a
   /// single question can be tracked across every configuration in a sweep. Kept human-readable
   /// (for example "rrf-weights" or "reranker-truncation") so a regression is legible at a glance.
   /// </summary>
   public string Id { get; set; }

   /// <summary>
   /// The question exactly as a user would type it. Deliberately stored verbatim, including any
   /// sloppy casing or missing punctuation, because the search has to survive real input rather
   /// than a cleaned-up version of it.
   /// </summary>
   public string Question { get; set; }

   /// <summary>
   /// The chunks a human judged relevant to <see cref="Question"/>, each carrying its own grade.
   /// A query with an empty list is treated as a labelling error rather than a query with no
   /// answer, because "nothing is relevant" cannot be distinguished from "nobody finished
   /// labelling this one" and silently scoring the second as the first would inflate every metric.
   /// </summary>
   public List<RelevantItem> Relevant { get; set; } = new List<RelevantItem>();

   /// <summary>
   /// Free-text note explaining why these items were graded the way they were. Not read by any
   /// metric; it exists so that a label can be argued with six months later instead of being
   /// taken on faith.
   /// </summary>
   public string Notes { get; set; }

   /// <summary>
   /// Optional tag used to group queries into families (for example "navigational" versus
   /// "conceptual") so the report can show that a config change helped one kind of question
   /// while hurting another. An overall average routinely hides exactly that trade.
   /// </summary>
   public string Category { get; set; }

   #endregion Data Members
}

/// <summary>
/// A single human-graded relevant chunk for a golden query: where it lives and how relevant it is.
///
/// Grades are graded rather than binary because retrieval quality is not binary. The file that
/// literally answers the question and a file that merely mentions the same subject are both
/// "relevant" under a yes/no label, which makes nDCG blind to the difference between an excellent
/// ranking and a mediocre one.
/// </summary>
public class RelevantItem
{
   #region Data Members

   /// <summary>
   /// Path of the file holding the relevant chunk. Matched leniently by the judge (case-insensitive,
   /// separator-normalized, suffix-based) so the same golden set works whether the index stores
   /// absolute paths, repo-relative paths, or a server-side rewrite of either.
   /// </summary>
   public string FilePath { get; set; }

   /// <summary>
   /// Optional name of the specific chunk (method, property, type) that answers the question. When
   /// set, the judge requires it to match as well as the path, which is what makes the harness able
   /// to tell "found the right file" apart from "found the right method". Leave null to accept any
   /// chunk from the file.
   /// </summary>
   public string ChunkName { get; set; }

   /// <summary>
   /// Human relevance grade on a 0-3 scale: 3 answers the question outright, 2 is strongly relevant,
   /// 1 is related background, 0 is not relevant. Feeds the gain term in nDCG; binary metrics
   /// (recall, precision, MRR) treat anything at or above <see cref="MinimumBinaryGrade"/> as a hit.
   /// </summary>
   public int Grade { get; set; } = 3;

   /// <summary>
   /// The lowest grade still counted as a hit by the binary metrics. Set at 1 so that background
   /// material counts as found, while an explicit 0 (labelled and rejected) never does. Exposed as a
   /// constant rather than inlined so the threshold is one decision in one place.
   /// </summary>
   public const int MinimumBinaryGrade = 1;

   /// <summary>
   /// Highest grade the scale allows. Used to validate loaded labels, because a stray 5 in a JSON
   /// file would silently dominate every nDCG gain it appears in.
   /// </summary>
   public const int MaximumGrade = 3;

   #endregion Data Members
}
