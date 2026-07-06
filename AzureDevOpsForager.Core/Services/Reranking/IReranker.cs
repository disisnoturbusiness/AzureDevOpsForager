using System.Collections.Generic;
using System.Threading;
using System.Threading.Tasks;

namespace AzureDevOpsForager.Core.Services.Reranking;
/// <summary>
/// Second-stage relevance scorer built on a cross-encoder model. The first-stage retrieval
/// (vector similarity, full-text search, and Reciprocal Rank Fusion) scores the query and each
/// document independently, which is fast but blind to how well a specific chunk actually answers
/// the specific query. A reranker reads the (query, chunk) pair together in a single pass, so it
/// can judge true relevance and reorder the shortlist accordingly.
///
/// Implementations MUST be fail-soft: on any failure (model load error, timeout, cancellation)
/// they return the candidates in their original retrieval order, truncated to topK, and never
/// throw. Reranking is an optional quality boost, so a broken reranker must degrade gracefully to
/// plain first-stage results rather than take down the whole search.
/// </summary>
public interface IReranker
{
   /// <summary>
   /// Rescores the supplied candidates against the query and returns the best topK, ordered by
   /// descending relevance. On any failure the implementation falls back to the input order,
   /// truncated to topK, so callers can treat the result as always-present.
   /// </summary>
   /// <param name="query">The user's search query, read jointly with each candidate's preview.</param>
   /// <param name="candidates">The first-stage shortlist to be rescored.</param>
   /// <param name="topK">Maximum number of results to return.</param>
   /// <param name="cancellationToken">Cancels the (potentially slow) model inference.</param>
   Task<IReadOnlyList<RerankerResult>> RerankAsync(
      string query,
      IReadOnlyList<RerankerCandidate> candidates,
      int topK,
      CancellationToken cancellationToken = default );
}

/// <summary>
/// A single input to the reranker: one first-stage hit that is a candidate for rescoring. It
/// carries the candidate's position in the original retrieval list (so the result can be mapped
/// back to the full hit record the caller holds) plus the preview text the cross-encoder reads.
/// </summary>
public sealed class RerankerCandidate
{
   #region Data Members

   /// <summary>
   /// The candidate's zero-based position in the first-stage retrieval list. The reranker only
   /// sees preview text, so this index is how a <see cref="RerankerResult"/> is tied back to the
   /// caller's full hit record after reordering.
   /// </summary>
   public int OriginalIndex { get; }

   /// <summary>
   /// The chunk preview text fed to the cross-encoder. This is the document side of the
   /// (query, chunk) pair the model scores for relevance.
   /// </summary>
   public string Preview { get; }

   #endregion

   #region Constructor

   /// <summary>
   /// Creates a candidate pairing an original retrieval position with the preview text to score.
   /// </summary>
   /// <param name="originalIndex">Zero-based position in the first-stage retrieval list.</param>
   /// <param name="preview">The chunk preview text the cross-encoder will read.</param>
   public RerankerCandidate( int originalIndex, string preview )
   {
      OriginalIndex = originalIndex;
      Preview = preview;
   }

   #endregion
}

/// <summary>
/// A single output of the reranker: a pointer back to the input candidate plus the fresh
/// relevance score the cross-encoder assigned. Results are returned ordered by descending score,
/// and <see cref="OriginalIndex"/> lets the caller reunite each score with its full hit record.
/// </summary>
public sealed class RerankerResult
{
   #region Data Members

   /// <summary>
   /// The <see cref="RerankerCandidate.OriginalIndex"/> this score belongs to. Because reranking
   /// reorders candidates, this back-pointer is how the caller reassociates a score with the
   /// original hit it came from.
   /// </summary>
   public int OriginalIndex { get; }

   /// <summary>
   /// The cross-encoder relevance score for this candidate. Higher means more relevant; results
   /// are sorted by this value in descending order.
   /// </summary>
   public double Score { get; }

   #endregion

   #region Constructor

   /// <summary>
   /// Creates a reranked result binding an original candidate index to its new relevance score.
   /// </summary>
   /// <param name="originalIndex">The candidate index this score corresponds to.</param>
   /// <param name="score">The cross-encoder relevance score; higher is more relevant.</param>
   public RerankerResult( int originalIndex, double score )
   {
      OriginalIndex = originalIndex;
      Score = score;
   }

   #endregion
}
