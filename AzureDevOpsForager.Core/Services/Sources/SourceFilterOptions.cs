using System.Collections.Generic;
using System.Linq;
using System.Text.RegularExpressions;

namespace AzureDevOpsForager.Core.Services.Sources;
/// <summary>
/// Config-driven include/exclude glob filtering for source files. This replaces the old
/// hard-coded folder whitelist so the indexer can run against any repository layout: the
/// caller supplies whatever include/exclude patterns fit the repo instead of relying on a
/// fixed set of folder names.
///
/// The filter is the gate that decides which files the indexer bothers to fetch and store.
/// Its two rules are deliberately simple so they can be reasoned about from config alone:
/// excludes always win over includes, and an empty include list means "include everything"
/// (still subject to excludes).
/// </summary>
public class SourceFilterOptions
{
   #region Data Members

   /// <summary>
   /// Glob patterns for files the indexer should pull in. Defaults to every C# file
   /// (<c>**/*.cs</c>) because that is the only content the code-search index cares about.
   /// An empty list is treated as "include everything" (see <see cref="ShouldInclude"/>).
   /// </summary>
   public List<string> IncludeGlobs { get; set; } = new List<string> { "**/*.cs" };

   /// <summary>
   /// Glob patterns for files to drop regardless of the include list. Defaults to build
   /// output (<c>**/bin/**</c> and <c>**/obj/**</c>) since compiled artifacts are noise in a
   /// source index. Excludes are evaluated first and take precedence over includes.
   /// </summary>
   public List<string> ExcludeGlobs { get; set; } = new List<string> { "**/bin/**", "**/obj/**" };

   #endregion

   #region Public Methods

   /// <summary>
   /// Decides whether a single repository file should be indexed, based on the configured
   /// include/exclude globs. The relative path is normalized first (backslashes to forward
   /// slashes, leading slash trimmed) so Windows-style paths from the indexer match the
   /// forward-slash globs.
   ///
   /// Rule order matters: an exclude match short-circuits to <c>false</c> even if the path
   /// would otherwise be included, and an empty include list means everything not excluded
   /// is kept.
   /// </summary>
   /// <param name="relativePath">
   /// The file path relative to the repo root. May be null or use either slash style.
   /// </param>
   /// <returns><c>true</c> if the file should be indexed; otherwise <c>false</c>.</returns>
   public bool ShouldInclude( string relativePath )
   {
      var normalizedPath = NormalizePath( relativePath );

      // Excludes win: if any exclude glob matches, the file is dropped immediately.
      if( ExcludeGlobs != null && ExcludeGlobs.Any( excludeGlob => GlobMatch( normalizedPath, excludeGlob ) ) )
         return false;

      // No include patterns configured means "include everything" (excludes already applied).
      if( IncludeGlobs == null || IncludeGlobs.Count == 0 )
         return true;

      // Otherwise the file must match at least one include glob to be kept.
      return IncludeGlobs.Any( includeGlob => GlobMatch( normalizedPath, includeGlob ) );
   }

   #endregion

   #region Private Methods

   /// <summary>
   /// Normalizes a raw path into the canonical form the glob matcher expects: null becomes
   /// an empty string, backslashes are converted to forward slashes, and any leading slash
   /// is trimmed so a repo-root file matches patterns like <c>**/*.cs</c>.
   /// </summary>
   private static string NormalizePath( string relativePath )
   {
      return ( relativePath ?? "" ).Replace( '\\', '/' ).TrimStart( '/' );
   }

   /// <summary>
   /// Translates a simple glob (supporting <c>**</c>, <c>*</c>, and <c>?</c>) into a regular
   /// expression and tests the path against it, case-insensitively. This is what lets plain
   /// config strings act as path filters without pulling in a full globbing library.
   /// </summary>
   /// <param name="path">The already-normalized (forward-slash) path to test.</param>
   /// <param name="glob">The glob pattern to match against.</param>
   /// <returns><c>true</c> if the path matches the glob.</returns>
   private static bool GlobMatch( string path, string glob )
   {
      if( string.IsNullOrEmpty( glob ) )
         return false;

      // Escape the glob first so literal regex metacharacters in the pattern are inert, then
      // reintroduce the glob wildcards as regex fragments. Order matters: the longer "**/"
      // and "**" tokens must be handled before the single "*" they contain.
      //   **/ -> (.*/)?  a full optional directory prefix (also matches zero folders)
      //   **  -> .*      any run of characters, crossing slashes
      //   *   -> [^/]*   any run within a single path segment
      //   ?   -> .       any single character
      var pattern = "^" + Regex.Escape( glob.Replace( '\\', '/' ) )
         .Replace( @"\*\*/", "(.*/)?" )
         .Replace( @"\*\*", ".*" )
         .Replace( @"\*", "[^/]*" )
         .Replace( @"\?", "." ) + "$";

      return Regex.IsMatch( path, pattern, RegexOptions.IgnoreCase );
   }

   #endregion
}
