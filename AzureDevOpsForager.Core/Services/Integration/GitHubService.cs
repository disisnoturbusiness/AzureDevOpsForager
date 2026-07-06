using System;
using System.IO;
using System.IO.Compression;
using System.Net.Http;
using System.Net.Http.Headers;
using System.Threading.Tasks;
using Newtonsoft.Json;

namespace AzureDevOpsForager.Core.Services.Integration;
/// <summary>
/// Thin client over the GitHub REST API (api.github.com) used by the forager's indexing pipeline.
/// Rather than walk the tree and pull each file (which burns through GitHub's per-file rate limit),
/// indexing grabs the whole repository as a single zipball in one request and then reads the files
/// back off the local extract. An access token is optional: public repos work unauthenticated, but a
/// Personal Access Token (PAT) is required to reach private repositories.
/// </summary>
public class GitHubService : IDisposable
{
   #region Data Members

   /// <summary>
   /// Root URL for every GitHub REST call. Kept as a constant so the endpoint strings that
   /// concatenate onto it (repo metadata, zipball download) all point at the same host.
   /// </summary>
   private const string ApiBase = "https://api.github.com";

   /// <summary>
   /// The single long-lived HttpClient used for all requests. Its default headers (User-Agent,
   /// Accept, optional Bearer auth) and the extended timeout are configured once in the constructor.
   /// </summary>
   private readonly HttpClient _http;

   /// <summary>
   /// Guards <see cref="Dispose"/> so the underlying HttpClient is only torn down once, even if
   /// Dispose is called more than once (idempotent disposal).
   /// </summary>
   private bool _disposed;

   #endregion

   #region Constructor

   /// <summary>
   /// Builds the client and pins the request headers that GitHub expects for the lifetime of the
   /// instance. The User-Agent is mandatory (GitHub rejects requests without one); the Accept header
   /// opts into the current JSON media type; and when a token is supplied it is sent as a Bearer
   /// credential so private repos and higher rate limits become available. The timeout is stretched
   /// to ten minutes because a full-repository zipball can be large and slow to transfer.
   /// </summary>
   /// <param name="token">
   /// Optional GitHub Personal Access Token. Leave null/empty for anonymous access to public repos.
   /// </param>
   public GitHubService( string token = null )
   {
      _http = new HttpClient();
      _http.DefaultRequestHeaders.UserAgent.ParseAdd( "AzureDevOpsForager/1.0" );  // GitHub rejects requests with no UA
      _http.DefaultRequestHeaders.Accept.Add( new MediaTypeWithQualityHeaderValue( "application/vnd.github+json" ) );
      if( !string.IsNullOrEmpty( token ) )
         _http.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue( "Bearer", token );
      _http.Timeout = TimeSpan.FromMinutes( 10 );   // a whole-repo zipball can be large
   }

   #endregion

   #region Public Methods

   /// <summary>
   /// Normalizes the many shapes a user might paste for a repository reference into a plain
   /// (owner, repo) pair. Accepts full HTTPS/HTTP web URLs, bare "owner/repo" slugs, and SSH-style
   /// "git@github.com:owner/repo" remotes, tolerating a trailing ".git" or slash. Returns a pair of
   /// empty strings when the input can't be resolved, so callers can treat that as "not a repo".
   /// </summary>
   /// <param name="url">The raw repository reference in any of the supported forms.</param>
   /// <returns>The parsed owner and repository name, or ("", "") if parsing fails.</returns>
   public static (string owner, string repo) ParseRepoUrl( string url )
   {
      if( string.IsNullOrWhiteSpace( url ) ) return ("", "");

      // Strip scheme and rewrite the SSH remote form into the same shape as a web path so the
      // segment split below can treat every supported input identically.
      var normalizedUrl = url.Trim()
         .Replace( "https://", "" ).Replace( "http://", "" )
         .Replace( "git@github.com:", "github.com/" )
         .TrimEnd( '/' );

      var segments = normalizedUrl.Split( new[] { '/' }, StringSplitOptions.RemoveEmptyEntries );

      // A bare "owner/repo" slug has no host segment; a full URL leads with "github.com". Skip the
      // host segment when present so the owner/repo pair always sits at the same relative offset.
      var startIndex = ( segments.Length > 0 && segments[0].IndexOf( "github.com", StringComparison.OrdinalIgnoreCase ) >= 0 ) ? 1 : 0;
      if( segments.Length >= startIndex + 2 )
      {
         var owner = segments[startIndex];
         var repo = segments[startIndex + 1];

         // A cloned remote often carries a ".git" suffix; drop it to get the plain repo name.
         if( repo.EndsWith( ".git", StringComparison.OrdinalIgnoreCase ) )
            repo = repo.Substring( 0, repo.Length - 4 );
         return (owner, repo);
      }
      return ("", "");
   }

   /// <summary>
   /// Looks up the repository's default branch (e.g. "main" or "master") via the repo metadata
   /// endpoint. This is what indexing falls back to when the caller doesn't pin an explicit branch.
   /// Any failure (network error, missing repo, unexpected payload) is swallowed and "main" is
   /// returned, since it is the most common default and lets the pipeline proceed optimistically.
   /// </summary>
   /// <param name="owner">The repository owner (user or organization).</param>
   /// <param name="repo">The repository name.</param>
   /// <returns>The default branch name, or "main" if it cannot be determined.</returns>
   public async Task<string> GetDefaultBranchAsync( string owner, string repo )
   {
      try
      {
         var json = await GetStringAsync( $"{ApiBase}/repos/{owner}/{repo}" );
         return JsonConvert.DeserializeObject<GitHubRepo>( json )?.DefaultBranch ?? "main";
      }
      catch
      {
         return "main";
      }
   }

   /// <summary>
   /// Downloads the repository as a single zipball and extracts it to a temp folder, returning the
   /// local path that holds the repo's files. When no branch is given, the default branch is resolved
   /// first. GitHub wraps everything inside a single "owner-repo-&lt;sha&gt;" folder, so the extract's
   /// lone child directory is returned as the effective repo root when it exists.
   /// </summary>
   /// <param name="owner">The repository owner (user or organization).</param>
   /// <param name="repo">The repository name.</param>
   /// <param name="branch">The branch to fetch; falls back to the default branch when empty.</param>
   /// <returns>The local folder containing the extracted repository files.</returns>
   public async Task<string> DownloadAndExtractAsync( string owner, string repo, string branch )
   {
      var refName = string.IsNullOrEmpty( branch ) ? await GetDefaultBranchAsync( owner, repo ) : branch;
      var url = $"{ApiBase}/repos/{owner}/{repo}/zipball/{Uri.EscapeDataString( refName )}";

      var response = await HttpRetry.GetWithRetryAsync( _http, url, "[GITHUB]" );
      response.EnsureSuccessStatusCode();
      var zipBytes = await response.Content.ReadAsByteArrayAsync();

      var tempDir = CreateCleanTempDir( owner, repo, refName );
      ExtractZipToDirectory( zipBytes, tempDir );

      // The zipball nests everything under a single "owner-repo-<sha>" folder; hand that back
      // directly when it's the only child so callers see the true repo root.
      var subDirectories = Directory.GetDirectories( tempDir );
      return subDirectories.Length == 1 ? subDirectories[0] : tempDir;
   }

   /// <summary>
   /// Releases the underlying HttpClient. Satisfies <see cref="IDisposable"/> and is safe to call
   /// repeatedly; the <see cref="_disposed"/> guard makes every call after the first a no-op.
   /// </summary>
   public void Dispose()
   {
      if( _disposed ) return;
      _http?.Dispose();
      _disposed = true;
   }

   #endregion

   #region Private Methods

   /// <summary>
   /// Issues a GET (with the shared retry policy) and returns the response body as a string, throwing
   /// on any non-success status. Used for the small JSON metadata calls, as opposed to the binary
   /// zipball download.
   /// </summary>
   /// <param name="url">The absolute URL to fetch.</param>
   /// <returns>The response body decoded as a string.</returns>
   private async Task<string> GetStringAsync( string url )
   {
      var response = await HttpRetry.GetWithRetryAsync( _http, url, "[GITHUB]" );
      response.EnsureSuccessStatusCode();
      return await response.Content.ReadAsStringAsync();
   }

   /// <summary>
   /// Builds a deterministic, filesystem-safe temp folder name for this owner/repo/branch and ensures
   /// it starts empty. Any stale extract from a previous run is deleted first (best effort) so a fresh
   /// download never mixes with leftover files.
   /// </summary>
   /// <param name="owner">The repository owner, used in the folder name.</param>
   /// <param name="repo">The repository name, used in the folder name.</param>
   /// <param name="refName">The branch/ref name, used in the folder name.</param>
   /// <returns>The path to a freshly created, empty temp directory.</returns>
   private static string CreateCleanTempDir( string owner, string repo, string refName )
   {
      // Replace path separators so a branch like "feature/x" can't escape into subfolders.
      var tempDir = Path.Combine( Path.GetTempPath(), $"adf_gh_{owner}_{repo}_{refName}".Replace( '/', '_' ).Replace( '\\', '_' ) );
      if( Directory.Exists( tempDir ) )
         try { Directory.Delete( tempDir, true ); } catch { }   // best effort; a lock shouldn't abort the download
      Directory.CreateDirectory( tempDir );
      return tempDir;
   }

   /// <summary>
   /// Extracts every file entry from an in-memory zip into <paramref name="targetDir"/>, recreating
   /// the archive's folder structure. Directory marker entries (those with an empty Name) are skipped
   /// since the parent folders are created on demand for each file.
   /// </summary>
   /// <param name="zipBytes">The raw zipball bytes downloaded from GitHub.</param>
   /// <param name="targetDir">The destination folder to extract into.</param>
   private static void ExtractZipToDirectory( byte[] zipBytes, string targetDir )
   {
      using( var memoryStream = new MemoryStream( zipBytes ) )
      using( var zip = new ZipArchive( memoryStream, ZipArchiveMode.Read ) )
      {
         // Full path of the target dir with a trailing separator, so a prefix check reliably tells
         // an in-tree destination apart from a sibling whose name merely starts the same way.
         var targetRoot = Path.GetFullPath( targetDir );
         var targetPrefix = targetRoot.EndsWith( Path.DirectorySeparatorChar.ToString() )
            ? targetRoot
            : targetRoot + Path.DirectorySeparatorChar;

         foreach( var entry in zip.Entries )
         {
            if( string.IsNullOrEmpty( entry.Name ) ) continue;   // directory marker, nothing to write
            var destination = Path.GetFullPath( Path.Combine( targetDir, entry.FullName ) );

            // Zip-Slip guard: a crafted entry name (e.g. "../../evil") can resolve outside targetDir.
            // Reject any entry whose fully-resolved destination escapes the extract root.
            if( !destination.StartsWith( targetPrefix, StringComparison.Ordinal ) )
               throw new IOException( $"Zip entry '{entry.FullName}' would extract outside the target directory." );

            var destinationDir = Path.GetDirectoryName( destination );
            if( destinationDir != null ) Directory.CreateDirectory( destinationDir );
            using var entryStream = entry.Open();
            using var fileStream = File.Create( destination );
            entryStream.CopyTo( fileStream );
         }
      }
   }

   #endregion
}

/// <summary>
/// Minimal deserialization target for the GitHub repo metadata endpoint. Only the single field the
/// forager needs (the default branch) is mapped; everything else in the payload is ignored.
/// </summary>
internal class GitHubRepo
{
   /// <summary>The repository's default branch, mapped from the API's "default_branch" JSON field.</summary>
   [JsonProperty( "default_branch" )] public string DefaultBranch { get; set; }
}
