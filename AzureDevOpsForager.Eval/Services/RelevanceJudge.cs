using System;
using System.Collections.Generic;
using AzureDevOpsForager.Eval.Models;

namespace AzureDevOpsForager.Eval.Services;

/// <summary>
/// A single returned result after it has been compared against the golden labels: what grade it
/// earned, and which specific labelled item it satisfied.
///
/// The back-pointer matters as much as the grade. Without it, two results that both match the same
/// labelled item would each count toward recall, and a search that returned the same good file five
/// times would score as though it had found five different answers.
/// </summary>
public class RankedJudgement
{
   #region Data Members

   /// <summary>The human grade this result earned, or zero when it matched nothing in the golden set.</summary>
   public int Grade { get; set; }

   /// <summary>
   /// Index of the labelled item this result satisfied, or -1 when it matched nothing. Used to
   /// deduplicate coverage when computing recall.
   /// </summary>
   public int GoldenItemIndex { get; set; } = -1;

   #endregion Data Members
}

/// <summary>
/// Decides whether a returned search result is one of the answers a human labelled as relevant.
///
/// The matching is deliberately lenient about path shape and strict about identity. Lenient,
/// because the same chunk legitimately appears as an absolute path, a repo-relative path, or a
/// server-rewritten path depending on how the index was built, and a golden set that only works
/// against one of those is a golden set that rots. Strict, because a suffix comparison that ignores
/// segment boundaries would happily match "Foo.cs" against "MyFoo.cs" and quietly inflate every
/// score in the report.
/// </summary>
public class RelevanceJudge
{
   #region Data Members

   /// <summary>
   /// Directory separator every path is normalized to before comparison, so Windows and POSIX
   /// spellings of the same path compare equal.
   /// </summary>
   private const char CanonicalSeparator = '/';

   #endregion Data Members

   #region Public Methods

   /// <summary>
   /// Grades one returned result against a query's labels, returning the best match found.
   /// When a result satisfies more than one labelled item, the highest grade wins, because the
   /// result genuinely is the better answer and scoring it as the weaker one would understate the
   /// ranking.
   /// </summary>
   /// <param name="query">The golden query supplying the labels.</param>
   /// <param name="resultFilePath">File path of the returned result, as reported by the search.</param>
   /// <param name="resultChunkName">Chunk name of the returned result, or null when unknown.</param>
   /// <returns>The grade earned and the index of the labelled item satisfied, or grade 0 / index -1.</returns>
   public RankedJudgement Judge( GoldenQuery query, string resultFilePath, string resultChunkName )
   {
      var best = new RankedJudgement();

      if( query?.Relevant == null || string.IsNullOrWhiteSpace( resultFilePath ) )
         return best;

      for( int i = 0; i < query.Relevant.Count; i++ )
      {
         var item = query.Relevant[i];

         if( !PathsMatch( resultFilePath, item.FilePath ) )
            continue;

         // A labelled chunk name narrows the claim from "the right file" to "the right member".
         // When the label leaves it null, any chunk from the file is accepted.
         if( !string.IsNullOrWhiteSpace( item.ChunkName )
             && !string.Equals( item.ChunkName, resultChunkName, StringComparison.OrdinalIgnoreCase ) )
            continue;

         if( best.GoldenItemIndex < 0 || item.Grade > best.Grade )
         {
            best.Grade = item.Grade;
            best.GoldenItemIndex = i;
         }
      }

      return best;
   }

   /// <summary>
   /// Grades an entire ranked result list in one pass, preserving rank order. This is the shape the
   /// metrics calculator consumes.
   /// </summary>
   /// <param name="query">The golden query supplying the labels.</param>
   /// <param name="resultFilePaths">Returned file paths, in rank order (index 0 is the top hit).</param>
   /// <param name="resultChunkNames">Returned chunk names in the same order; may be null or shorter.</param>
   /// <returns>One judgement per returned result, in rank order.</returns>
   public List<RankedJudgement> JudgeRanked(
      GoldenQuery query, IReadOnlyList<string> resultFilePaths, IReadOnlyList<string> resultChunkNames )
   {
      var judgements = new List<RankedJudgement>();
      if( resultFilePaths == null )
         return judgements;

      for( int rank = 0; rank < resultFilePaths.Count; rank++ )
      {
         var chunkName = resultChunkNames != null && rank < resultChunkNames.Count ? resultChunkNames[rank] : null;
         judgements.Add( Judge( query, resultFilePaths[rank], chunkName ) );
      }

      return judgements;
   }

   #endregion Public Methods

   #region Private Methods

   /// <summary>
   /// Decides whether two file paths refer to the same file, tolerating separator style, casing,
   /// and one path being a deeper-rooted spelling of the other.
   /// </summary>
   /// <param name="first">One path to compare.</param>
   /// <param name="second">The other path to compare.</param>
   /// <returns>True when the paths denote the same file.</returns>
   private static bool PathsMatch( string first, string second )
   {
      if( string.IsNullOrWhiteSpace( first ) || string.IsNullOrWhiteSpace( second ) )
         return false;

      var left = Normalize( first );
      var right = Normalize( second );

      if( string.Equals( left, right, StringComparison.Ordinal ) )
         return true;

      return IsPathSuffix( left, right ) || IsPathSuffix( right, left );
   }

   /// <summary>
   /// Tests whether the shorter path is a whole-segment tail of the longer one, which is what makes
   /// a repo-relative label match an absolute indexed path. The segment-boundary check is the part
   /// that stops "src/Foo.cs" from matching "src/MyFoo.cs".
   /// </summary>
   /// <param name="longer">The candidate longer path, already normalized.</param>
   /// <param name="shorter">The candidate tail, already normalized.</param>
   /// <returns>True when <paramref name="shorter"/> is a segment-aligned suffix of <paramref name="longer"/>.</returns>
   private static bool IsPathSuffix( string longer, string shorter )
   {
      if( longer.Length <= shorter.Length )
         return false;

      if( !longer.EndsWith( shorter, StringComparison.Ordinal ) )
         return false;

      return longer[longer.Length - shorter.Length - 1] == CanonicalSeparator;
   }

   /// <summary>
   /// Reduces a path to its comparison form: backslashes folded to the canonical separator, casing
   /// dropped, and any leading separator removed so rooted and unrooted spellings align.
   /// </summary>
   /// <param name="path">The raw path to normalize.</param>
   /// <returns>The normalized path used for comparison.</returns>
   private static string Normalize( string path )
   {
      return path.Trim()
         .Replace( '\\', CanonicalSeparator )
         .TrimStart( CanonicalSeparator )
         .ToLowerInvariant();
   }

   #endregion Private Methods
}
