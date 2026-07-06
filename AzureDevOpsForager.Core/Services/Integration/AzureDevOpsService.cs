using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Linq;
using System.Net.Http;
using System.Net.Http.Headers;
using System.Text;
using System.Threading.Tasks;
using AzureDevOpsForager.Core.Models.AzureDevOps;
using Newtonsoft.Json;

namespace AzureDevOpsForager.Core.Services.Integration;

/// <summary>
/// Integration surface for reading source out of Azure DevOps, covering both the
/// legacy TFVC version-control model and the modern Git model. The indexer uses this
/// to enumerate the files under a scope, pull the latest text of each one, and read
/// enough author/date provenance to attribute code to a person and a point in time.
///
/// Authentication is a Personal Access Token supplied as HTTP Basic credentials, which
/// is the pattern Azure DevOps expects for token-based access to its REST API. All calls
/// go through a single shared <see cref="HttpClient"/> so connection pooling and the
/// configured auth header are reused across the many parallel fetches the indexer issues.
/// </summary>
public class AzureDevOpsService : IDisposable
{
   #region Data Members

   /// <summary>Shared HTTP client carrying the Basic auth header and JSON accept header for every request.</summary>
   private readonly HttpClient _http;

   /// <summary>Organization base URL (e.g. https://dev.azure.com/your-org) with any trailing slash removed.</summary>
   private readonly string _baseUrl;

   /// <summary>Azure DevOps project name that scopes every REST route.</summary>
   private readonly string _project;

   /// <summary>TFVC root folder with the leading "$/" stripped, used to build full TFVC paths and to relativize them.</summary>
   private readonly string _tfvcRoot;

   /// <summary>REST API version pinned on every request so the response shapes stay stable.</summary>
   private readonly string _apiVersion = "6.0";

   /// <summary>Guards against double-disposal of the shared <see cref="HttpClient"/>.</summary>
   private bool _disposed;

   /// <summary>
   /// Per-path changeset history cache. The indexer fetches files in parallel, so this is a
   /// concurrent dictionary; caching avoids re-hitting the history endpoint for a path we
   /// already resolved during this session.
   /// </summary>
   private readonly ConcurrentDictionary<string, List<ChangesetInfo>> _historyCache = new();

   /// <summary>
   /// Per-key file-content cache spanning both TFVC and Git reads (the key encodes which source
   /// and which version). Latest content rarely changes mid-run, so a session cache saves redundant
   /// downloads when the same file is requested more than once.
   /// </summary>
   private readonly ConcurrentDictionary<string, string> _contentCache = new();

   #endregion Data Members

   #region Constructor

   /// <summary>
   /// Builds the service and prepares the shared HTTP client with PAT-based Basic authentication.
   /// The URL and TFVC root are normalized here (trailing/leading slashes and the "$/" prefix removed)
   /// so the rest of the class can compose paths without re-checking those edge cases.
   /// </summary>
   /// <param name="azureUrl">Base URL like https://dev.azure.com/your-org.</param>
   /// <param name="pat">Personal Access Token used as the password half of Basic auth.</param>
   /// <param name="project">Project name, e.g. MyProject.</param>
   /// <param name="tfvcRoot">TFVC root path (with or without the leading "$/"); normalized on the way in.</param>
   public AzureDevOpsService( string azureUrl, string pat, string project, string tfvcRoot )
   {
      _baseUrl = ( azureUrl ?? string.Empty ).TrimEnd( '/' );
      _project = project;
      _tfvcRoot = ( tfvcRoot ?? string.Empty ).TrimStart( '$', '/' ).TrimEnd( '/' );

      _http = new HttpClient();

      // Azure DevOps expects the PAT as Basic auth with an empty username, i.e. ":PAT" base64-encoded.
      var authBytes = Encoding.ASCII.GetBytes( $":{pat}" );
      var authBase64 = Convert.ToBase64String( authBytes );
      _http.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue( "Basic", authBase64 );
      _http.DefaultRequestHeaders.Accept.Add( new MediaTypeWithQualityHeaderValue( "application/json" ) );
      _http.Timeout = TimeSpan.FromSeconds( 60 );
   }

   #endregion Constructor

   #region Public Methods

   /// <summary>
   /// Reads basic provenance for a TFVC file: the original author and the date it was first added.
   /// The history endpoint returns changesets newest-first, so the oldest entry (the last one) is
   /// treated as the "add" event. Any failure is swallowed and reported as empty strings so a single
   /// unreadable file never breaks a bulk indexing run.
   /// </summary>
   /// <param name="relativePath">File path relative to the configured TFVC root.</param>
   /// <returns>A tuple of (author, addDate); both empty when history is unavailable.</returns>
   public async Task<(string author, string addDate)> GetBasicMetadataAsync( string relativePath )
   {
      try
      {
         var tfvcPath = ToTfvcPath( relativePath );
         var history = await GetHistoryAsync( tfvcPath );

         if( history != null && history.Count > 0 )
         {
            var oldest = history.Last(); // History is newest-first, so the tail is the original add.
            return ( oldest.Author, oldest.Date );
         }
      }
      catch { }

      return ( "", "" );
   }

   /// <summary>
   /// Lists every item (files and folders) recursively under a TFVC scope. When no scope is given it
   /// defaults to the configured root. Errors are logged and returned as an empty list rather than
   /// thrown, keeping enumeration resilient during large crawls.
   /// </summary>
   /// <param name="scopePath">Optional TFVC scope path; defaults to "$/{root}".</param>
   public async Task<List<TfvcItem>> GetAllItemsAsync( string scopePath = null )
   {
      scopePath ??= $"$/{_tfvcRoot}";

      var url = $"{_baseUrl}/{_project}/_apis/tfvc/items" +
                $"?scopePath={Uri.EscapeDataString( scopePath )}" +
                $"&recursionLevel=Full" +
                $"&api-version={_apiVersion}";

      try
      {
         var response = await HttpRetry.GetWithRetryAsync( _http, url, "[AZURE]" );
         if( !response.IsSuccessStatusCode )
         {
            Console.WriteLine( $"[AZURE ERROR] GetAllItems failed: {response.StatusCode}" );
            return new List<TfvcItem>();
         }

         var json = await response.Content.ReadAsStringAsync();
         var data = JsonConvert.DeserializeObject<TfvcItemsResponse>( json );

         return data?.Value?.ToList() ?? new List<TfvcItem>();
      }
      catch( Exception exception )
      {
         Console.WriteLine( $"[AZURE ERROR] GetAllItems: {exception.Message}" );
         return new List<TfvcItem>();
      }
   }

   /// <summary>
   /// Fetches the latest text content of a TFVC file (no changeset pin, so it reads tip). The result
   /// is cached per path for the session; a failed read returns null so the caller can skip the file.
   /// </summary>
   /// <param name="tfvcPath">Full TFVC path, e.g. "$/Root/Folder/File.cs".</param>
   public async Task<string> GetLatestFileContentAsync( string tfvcPath )
   {
      var cacheKey = $"{tfvcPath}@latest";
      if( _contentCache.TryGetValue( cacheKey, out var cached ) )
         return cached;

      var url = $"{_baseUrl}/{_project}/_apis/tfvc/items" +
                $"?path={Uri.EscapeDataString( tfvcPath )}" +
                $"&$format=text" +
                $"&api-version={_apiVersion}";

      try
      {
         var response = await HttpRetry.GetWithRetryAsync( _http, url, "[AZURE]" );
         if( !response.IsSuccessStatusCode )
            return null;

         var content = await response.Content.ReadAsStringAsync();
         _contentCache[cacheKey] = content;
         return content;
      }
      catch
      {
         return null;
      }
   }

   /// <summary>
   /// Turns a full TFVC path back into a path relative to the configured root by stripping the
   /// "$/{root}/" prefix. If the prefix is not present (comparison is case-insensitive) the input is
   /// returned unchanged, which keeps the method safe to call on already-relative paths.
   /// </summary>
   public string ToRelativePath( string tfvcPath )
   {
      var prefix = $"$/{_tfvcRoot}/";
      return tfvcPath.StartsWith( prefix, StringComparison.OrdinalIgnoreCase )
         ? tfvcPath.Substring( prefix.Length )
         : tfvcPath;
   }

   /// <summary>
   /// Lists every item recursively in an Azure DevOps Git repository. This reuses the same
   /// organization URL and PAT as the TFVC calls but targets the "/git/" route family. A branch may
   /// be supplied to read a specific version; when omitted the repository default is used. Errors are
   /// logged and returned as an empty list.
   /// </summary>
   /// <param name="repositoryId">Git repository id or name.</param>
   /// <param name="branch">Optional branch name; when null the repo default branch is read.</param>
   public async Task<List<GitItem>> GetAllGitItemsAsync( string repositoryId, string branch = null )
   {
      var url = $"{_baseUrl}/{_project}/_apis/git/repositories/{Uri.EscapeDataString( repositoryId )}/items" +
                $"?recursionLevel=Full" +
                ( string.IsNullOrEmpty( branch ) ? "" : $"&versionDescriptor.version={Uri.EscapeDataString( branch )}&versionDescriptor.versionType=branch" ) +
                $"&api-version={_apiVersion}";

      try
      {
         var response = await HttpRetry.GetWithRetryAsync( _http, url, "[AZURE]" );
         if( !response.IsSuccessStatusCode )
         {
            Console.WriteLine( $"[GIT ERROR] GetAllGitItems failed: {response.StatusCode}" );
            return new List<GitItem>();
         }

         var json = await response.Content.ReadAsStringAsync();
         var data = JsonConvert.DeserializeObject<GitItemsResponse>( json );
         return data?.Value?.ToList() ?? new List<GitItem>();
      }
      catch( Exception exception )
      {
         Console.WriteLine( $"[GIT ERROR] GetAllGitItems: {exception.Message}" );
         return new List<GitItem>();
      }
   }

   /// <summary>
   /// Fetches the latest text content of a file from an Azure DevOps Git repository, optionally at a
   /// specific branch. The cache key encodes repo, branch, and path so TFVC and Git entries never
   /// collide in the shared content cache. A failed read returns null.
   /// </summary>
   /// <param name="repositoryId">Git repository id or name.</param>
   /// <param name="path">Path within the repository.</param>
   /// <param name="branch">Optional branch name; when null the repo default branch is read.</param>
   public async Task<string> GetGitFileContentAsync( string repositoryId, string path, string branch = null )
   {
      var cacheKey = $"git:{repositoryId}:{branch}:{path}";
      if( _contentCache.TryGetValue( cacheKey, out var cached ) )
         return cached;

      var url = $"{_baseUrl}/{_project}/_apis/git/repositories/{Uri.EscapeDataString( repositoryId )}/items" +
                $"?path={Uri.EscapeDataString( path )}" +
                $"&download=true&$format=text" +
                ( string.IsNullOrEmpty( branch ) ? "" : $"&versionDescriptor.version={Uri.EscapeDataString( branch )}&versionDescriptor.versionType=branch" ) +
                $"&api-version={_apiVersion}";

      try
      {
         var response = await HttpRetry.GetWithRetryAsync( _http, url, "[AZURE]" );
         if( !response.IsSuccessStatusCode )
            return null;

         var content = await response.Content.ReadAsStringAsync();
         _contentCache[cacheKey] = content;
         return content;
      }
      catch
      {
         return null;
      }
   }

   /// <summary>
   /// Reads basic provenance for a Git file: the author and date of its most recent commit. Only the
   /// top commit is requested ($top=1) since that is all the "last changed" attribution needs. Prefers
   /// the author identity over the committer identity, falling back between the two for each field.
   /// Any failure yields empty strings so bulk indexing keeps moving.
   /// </summary>
   /// <param name="repositoryId">Git repository id or name.</param>
   /// <param name="path">Path within the repository.</param>
   /// <param name="branch">Optional branch name; when null the repo default branch is read.</param>
   /// <returns>A tuple of (author, date); both empty when no commit is found or the call fails.</returns>
   public async Task<(string author, string date)> GetGitFileMetadataAsync( string repositoryId, string path, string branch = null )
   {
      var url = $"{_baseUrl}/{_project}/_apis/git/repositories/{Uri.EscapeDataString( repositoryId )}/commits" +
                $"?searchCriteria.itemPath={Uri.EscapeDataString( path )}" +
                $"&searchCriteria.$top=1" +
                ( string.IsNullOrEmpty( branch ) ? "" : $"&searchCriteria.itemVersion.version={Uri.EscapeDataString( branch )}&searchCriteria.itemVersion.versionType=branch" ) +
                $"&api-version={_apiVersion}";

      try
      {
         var response = await HttpRetry.GetWithRetryAsync( _http, url, "[AZURE]" );
         if( !response.IsSuccessStatusCode )
            return ( "", "" );

         var json = await response.Content.ReadAsStringAsync();
         var data = JsonConvert.DeserializeObject<GitCommitsResponse>( json );
         var commit = data?.Value?.FirstOrDefault();
         if( commit == null )
            return ( "", "" );

         var author = ExtractAuthorName( commit.Author?.Name ?? commit.Committer?.Name ?? "Unknown" );
         var date = ( commit.Author?.Date ?? commit.Committer?.Date )?.ToString( "yyyy-MM-dd" ) ?? "";
         return ( author, date );
      }
      catch
      {
         return ( "", "" );
      }
   }

   /// <summary>
   /// Disposes the shared <see cref="HttpClient"/>. This satisfies <see cref="IDisposable"/>; the
   /// disposal guard makes repeated calls safe, which matters because callers using <c>using</c> and
   /// explicit shutdown paths can both reach it.
   /// </summary>
   public void Dispose()
   {
      if( _disposed )
         return;

      _http?.Dispose();
      _disposed = true;
   }

   #endregion Public Methods

   #region Private Methods

   /// <summary>
   /// Retrieves the changeset history for a TFVC file, newest-first, and caches it for the session.
   /// Each raw changeset is projected into a lightweight <see cref="ChangesetInfo"/>, resolving the
   /// author from either the checked-in author or the "checked in by" identity and formatting the date
   /// as yyyy-MM-dd. Failures return null and are logged rather than thrown.
   /// </summary>
   /// <param name="tfvcPath">Full TFVC path to read history for.</param>
   /// <param name="maxResults">Upper bound on changesets requested (default 500).</param>
   private async Task<List<ChangesetInfo>> GetHistoryAsync( string tfvcPath, int maxResults = 500 )
   {
      if( _historyCache.TryGetValue( tfvcPath, out var cached ) )
         return cached;

      var url = $"{_baseUrl}/{_project}/_apis/tfvc/changesets" +
                $"?searchCriteria.itemPath={Uri.EscapeDataString( tfvcPath )}" +
                $"&$top={maxResults}" +
                $"&api-version={_apiVersion}";

      try
      {
         var response = await HttpRetry.GetWithRetryAsync( _http, url, "[AZURE]" );
         if( !response.IsSuccessStatusCode )
            return null;

         var json = await response.Content.ReadAsStringAsync();
         var data = JsonConvert.DeserializeObject<ChangesetResponse>( json );

         var history = new List<ChangesetInfo>();
         foreach( var changeset in data?.Value ?? Array.Empty<ChangesetItem>() )
         {
            history.Add( new ChangesetInfo
            {
               ChangesetId = changeset.ChangesetId.ToString(),
               Author = ExtractAuthorName( changeset.Author?.DisplayName ?? changeset.CheckedInBy?.DisplayName ?? "Unknown" ),
               Date = changeset.CreatedDate?.ToString( "yyyy-MM-dd" ) ?? "",
               Comment = changeset.Comment ?? ""
            } );
         }

         _historyCache[tfvcPath] = history;
         return history;
      }
      catch( Exception exception )
      {
         Console.WriteLine( $"[AZURE HISTORY ERROR] {exception.Message}" );
         return null;
      }
   }

   /// <summary>
   /// Composes a full TFVC path ("$/{root}/...") from a normalized relative path. Backslashes are
   /// converted to forward slashes first so Windows-style inputs map onto TFVC's slash convention.
   /// </summary>
   private string ToTfvcPath( string relativePath )
   {
      var normalized = relativePath.Replace( '\\', '/' );
      return $"$/{_tfvcRoot}/{normalized}";
   }

   /// <summary>
   /// Extracts just the human name from a version-control display name, dropping any trailing email in
   /// angle brackets (e.g. "Jane Doe &lt;jane@...&gt;" becomes "Jane Doe"). Empty input maps to
   /// "Unknown" so downstream attribution always has a value.
   /// </summary>
   private string ExtractAuthorName( string displayName )
   {
      if( string.IsNullOrEmpty( displayName ) )
         return "Unknown";

      var emailIndex = displayName.IndexOf( '<' );
      if( emailIndex > 0 )
         return displayName.Substring( 0, emailIndex ).Trim();

      return displayName.Trim();
   }

   #endregion Private Methods
}

#region Internal JSON Response Models

/// <summary>Envelope for the TFVC changesets endpoint response.</summary>
internal class ChangesetResponse
{
   public ChangesetItem[] Value { get; set; }
}

/// <summary>A single TFVC changeset as returned by the API, before projection into <see cref="ChangesetInfo"/>.</summary>
internal class ChangesetItem
{
   public int ChangesetId { get; set; }
   public AuthorInfo Author { get; set; }
   public AuthorInfo CheckedInBy { get; set; }
   public DateTime? CreatedDate { get; set; }
   public string Comment { get; set; }
}

/// <summary>Identity block carrying just the display name used to derive an author.</summary>
internal class AuthorInfo
{
   public string DisplayName { get; set; }
}

/// <summary>Envelope for the TFVC items endpoint response.</summary>
internal class TfvcItemsResponse
{
   public TfvcItem[] Value { get; set; }
}

/// <summary>Represents a TFVC item (file or folder) from the Azure DevOps API.</summary>
public class TfvcItem
{
   /// <summary>Full TFVC path of the item.</summary>
   [JsonProperty( "path" )]
   public string Path { get; set; }

   /// <summary>True when the item is a folder rather than a file.</summary>
   [JsonProperty( "isFolder" )]
   public bool IsFolder { get; set; }

   /// <summary>Size of the item in bytes.</summary>
   [JsonProperty( "size" )]
   public long Size { get; set; }

   /// <summary>Last change date reported by the server, when available.</summary>
   [JsonProperty( "changeDate" )]
   public DateTime? ChangeDate { get; set; }

   /// <summary>API URL for the item.</summary>
   [JsonProperty( "url" )]
   public string Url { get; set; }
}

/// <summary>Envelope for the Git items endpoint response.</summary>
internal class GitItemsResponse
{
   public GitItem[] Value { get; set; }
}

/// <summary>Represents a Git item (blob or tree) from the Azure DevOps Git API.</summary>
public class GitItem
{
   /// <summary>Path of the item within the repository.</summary>
   [JsonProperty( "path" )]
   public string Path { get; set; }

   /// <summary>Git object type as reported by the API, typically "blob" or "tree".</summary>
   [JsonProperty( "gitObjectType" )]
   public string GitObjectType { get; set; }

   /// <summary>Git object id (SHA) of the item.</summary>
   [JsonProperty( "objectId" )]
   public string ObjectId { get; set; }

   /// <summary>True when the item is a folder rather than a file.</summary>
   [JsonProperty( "isFolder" )]
   public bool IsFolder { get; set; }

   /// <summary>
   /// True when this item is a file blob. Treats an explicit "blob" type as a blob, and also treats a
   /// missing type as a blob when the item is not a folder, so files are still recognized when the API
   /// omits the object type.
   /// </summary>
   public bool IsBlob =>
      string.Equals( GitObjectType, "blob", StringComparison.OrdinalIgnoreCase ) ||
      ( string.IsNullOrEmpty( GitObjectType ) && !IsFolder );
}

/// <summary>Envelope for the Git commits endpoint response.</summary>
internal class GitCommitsResponse
{
   public GitCommit[] Value { get; set; }
}

/// <summary>A single Git commit carrying the author and committer identities used for provenance.</summary>
internal class GitCommit
{
   [JsonProperty( "author" )]
   public GitUserDate Author { get; set; }

   [JsonProperty( "committer" )]
   public GitUserDate Committer { get; set; }

   [JsonProperty( "comment" )]
   public string Comment { get; set; }
}

/// <summary>Git identity plus timestamp (author or committer) as returned by the commits endpoint.</summary>
internal class GitUserDate
{
   [JsonProperty( "name" )]
   public string Name { get; set; }

   [JsonProperty( "email" )]
   public string Email { get; set; }

   [JsonProperty( "date" )]
   public DateTime? Date { get; set; }
}

#endregion Internal JSON Response Models
