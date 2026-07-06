using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Threading.Tasks;
using AzureDevOpsForager.Core.Services.Integration;

namespace AzureDevOpsForager.Core.Services.Sources
{
   /// <summary>
   /// An <see cref="ISourceProvider"/> that harvests files from a GitHub repository.
   /// Rather than fetching files one at a time (which quickly exhausts GitHub's per-file
   /// API rate limit on any real-sized repo), this provider downloads the whole repository
   /// as a single zipball, extracts it to a local working directory, and then reads files
   /// straight off disk. That trades a burst of API calls for exactly one download request.
   /// Callers narrow what gets harvested with the include/exclude globs carried on
   /// <see cref="SourceFilterOptions"/> (for example, include only "src/**/*.cs").
   /// </summary>
   public class GitHubSourceProvider : ISourceProvider
   {
      #region Data Members

      /// <summary>
      /// Integration service that talks to GitHub. Owns URL parsing plus the zipball
      /// download-and-extract, so this provider stays a thin adapter over it.
      /// </summary>
      private readonly GitHubService _gitHubService;

      /// <summary>Repository owner (the user or organization), parsed from the supplied repo URL.</summary>
      private readonly string _owner;

      /// <summary>Repository name, parsed from the supplied repo URL.</summary>
      private readonly string _repo;

      /// <summary>
      /// Branch to read from. Null or empty means "use the repository's default branch",
      /// which the download step resolves on GitHub's side.
      /// </summary>
      private readonly string _branch;

      /// <summary>
      /// Include/exclude glob rules applied to each discovered file's relative path.
      /// Populated with an empty (allow-all) instance when the caller passes none.
      /// </summary>
      private readonly SourceFilterOptions _filter;

      /// <summary>
      /// Absolute path to the local directory the zipball was extracted into. Assigned during
      /// <see cref="GetAllFilesAsync"/> and used as the root for relative-path computation.
      /// </summary>
      private string _localRoot;

      #endregion

      #region Constructor

      /// <summary>
      /// Builds a provider pointed at a single repository. The owner and repo name are parsed
      /// up front from <paramref name="repoUrl"/> so any malformed URL surfaces immediately
      /// rather than at download time. When no filter is supplied a default allow-all filter
      /// is used, meaning every extracted file is considered indexable.
      /// </summary>
      /// <param name="gh">GitHub integration service used to parse the URL and download the repo.</param>
      /// <param name="repoUrl">Full GitHub repository URL to harvest from.</param>
      /// <param name="branch">Optional branch name; null/empty selects the default branch.</param>
      /// <param name="filter">Optional include/exclude globs; defaults to allow-all.</param>
      public GitHubSourceProvider( GitHubService gh, string repoUrl, string branch = null, SourceFilterOptions filter = null )
      {
         _gitHubService = gh;
         ( _owner, _repo ) = GitHubService.ParseRepoUrl( repoUrl );
         _branch = branch;
         _filter = filter ?? new SourceFilterOptions();
      }

      #endregion

      #region Public Methods

      /// <summary>
      /// Human-readable label of exactly which repo and branch this run reads from, in the
      /// form "GitHub: owner/repo@branch". Falls back to "@default" when no branch was given
      /// so logs and UI still convey that the default branch is in play.
      /// </summary>
      public string SourceDescription =>
         $"GitHub: {_owner}/{_repo}@{( string.IsNullOrEmpty( _branch ) ? "default" : _branch )}";

      /// <summary>
      /// Downloads and extracts the repository, then enumerates every extracted file and
      /// returns the ones the filter accepts. Native paths are kept as-is for later content
      /// reads, while relative paths are computed against the extract root and normalized to
      /// forward slashes so glob matching behaves the same regardless of the host OS.
      /// </summary>
      public async Task<List<SourceFileInfo>> GetAllFilesAsync()
      {
         _localRoot = await _gitHubService.DownloadAndExtractAsync( _owner, _repo, _branch );

         // +1 skips the trailing separator so the relative path does not start with a slash.
         var rootPrefixLength = _localRoot.Length + 1;

         return Directory.EnumerateFiles( _localRoot, "*", SearchOption.AllDirectories )
            .Select( filePath => new SourceFileInfo
            {
               NativePath = filePath,
               RelativePath = filePath.Substring( rootPrefixLength ).Replace( '\\', '/' ),
               ChangeDate = null
            } )
            .Where( file => _filter.ShouldInclude( file.RelativePath ) )
            .ToList();
      }

      /// <summary>
      /// Reads a single file's text straight off the local extract. Any read failure (a
      /// missing file, a lock, an unreadable byte sequence) is swallowed and reported as a
      /// null result so one bad file never aborts the whole indexing run.
      /// </summary>
      public Task<string> GetFileContentAsync( SourceFileInfo file )
      {
         try { return Task.FromResult( File.ReadAllText( file.NativePath ) ); }
         catch { return Task.FromResult<string>( null ); }
      }

      /// <summary>
      /// Provenance lookup for a file. The zipball extract carries no per-file author or
      /// commit history, so this provider deliberately returns empty values; callers treat
      /// this metadata as advisory and tolerate the blanks.
      /// </summary>
      public Task<(string author, string date)> GetBasicMetadataAsync( SourceFileInfo file )
         => Task.FromResult( ( "", "" ) );

      #endregion
   }
}
