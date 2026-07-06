using System.Collections.Generic;
using System.Linq;
using System.Threading.Tasks;
using AzureDevOpsForager.Core.Services.Integration;

namespace AzureDevOpsForager.Core.Services.Sources;
/// <summary>
/// Reads indexable source files out of an Azure DevOps Git repository and presents them to
/// the indexer through the neutral <see cref="ISourceProvider"/> contract. This is one of the
/// interchangeable backends (alongside TFVC and GitHub) so the indexing pipeline
/// never has to know which kind of repository it is harvesting from.
///
/// It deliberately reuses the same <see cref="AzureDevOpsService"/> instance as the TFVC adapter,
/// which means the same organization URL and PAT credentials are shared; only the REST route family
/// differs (the /git/ endpoints instead of the /tfvc/ ones). All the real HTTP work lives in that
/// service, so this class is a thin translation layer that maps Git items into the shape the
/// indexer expects and applies the caller's include/exclude filtering.
/// </summary>
public class GitSourceProvider : ISourceProvider
{
   #region Data Members

   /// <summary>
   /// Shared Azure DevOps REST client that performs the actual authenticated HTTP calls. Reused
   /// across source providers so credentials (org URL + PAT) are configured in exactly one place.
   /// </summary>
   private readonly AzureDevOpsService _azure;

   /// <summary>
   /// Identifier of the Git repository to read from. Passed straight through to the REST calls;
   /// Azure DevOps accepts either the repository's GUID or its name here.
   /// </summary>
   private readonly string _repositoryId;

   /// <summary>
   /// Branch to read from, or null to let the service fall back to the repository's default branch.
   /// The null case is surfaced as "default" in <see cref="SourceDescription"/> for operator clarity.
   /// </summary>
   private readonly string _branch;

   /// <summary>
   /// Include/exclude glob rules that decide which discovered files are actually indexable. Always
   /// non-null after construction (an empty options object is substituted when the caller omits it)
   /// so the enumeration path can call it unconditionally.
   /// </summary>
   private readonly SourceFilterOptions _filter;

   #endregion

   #region Constructor

   /// <summary>
   /// Builds a Git source provider bound to one repository (and optionally one branch). The filter
   /// is defaulted to an empty <see cref="SourceFilterOptions"/> when not supplied, which makes the
   /// provider include everything rather than silently dropping files against a null rule set.
   /// </summary>
   /// <param name="azure">Shared Azure DevOps REST client used for every call.</param>
   /// <param name="repositoryId">Repository GUID or name to harvest.</param>
   /// <param name="branch">Branch to read; null uses the repository default branch.</param>
   /// <param name="filter">Include/exclude rules; null means "no filtering" (include all files).</param>
   public GitSourceProvider( AzureDevOpsService azure, string repositoryId, string branch = null, SourceFilterOptions filter = null )
   {
      _azure = azure;
      _repositoryId = repositoryId;
      _branch = branch;
      _filter = filter ?? new SourceFilterOptions();
   }

   #endregion

   #region Public Methods

   /// <summary>
   /// Human-readable label describing which repository and branch this run is pointed at. When no
   /// branch was specified it reads "default" so logs and UI stay unambiguous rather than showing a
   /// blank branch segment.
   /// </summary>
   public string SourceDescription =>
      $"Azure DevOps (Git: {_repositoryId}@{( string.IsNullOrEmpty( _branch ) ? "default" : _branch )})";

   /// <summary>
   /// Enumerates every indexable file in the repository. The raw Git tree is fetched from Azure DevOps,
   /// folders are dropped (only blobs are real files), each item is projected into the provider-neutral
   /// <see cref="SourceFileInfo"/> record, and finally the caller's include/exclude filter is applied so
   /// the indexer receives an already-vetted list.
   ///
   /// Path handling is deliberate: the raw Git path is kept verbatim as the native path used to fetch
   /// content later, while the relative path is normalized to forward slashes with any leading slash
   /// trimmed so it can serve as the canonical cross-provider identity for filtering and storage.
   /// </summary>
   public async Task<List<SourceFileInfo>> GetAllFilesAsync()
   {
      var items = await _azure.GetAllGitItemsAsync( _repositoryId, _branch );
      return items
         .Where( item => item.IsBlob )
         .Select( item => new SourceFileInfo
         {
            NativePath = item.Path,
            RelativePath = ( item.Path ?? "" ).Replace( '\\', '/' ).TrimStart( '/' ),
            ChangeDate = null
         } )
         .Where( file => _filter.ShouldInclude( file.RelativePath ) )
         .ToList();
   }

   /// <summary>
   /// Fetches the current text content of a single file on demand. Delegates to the shared service
   /// using the file's native (unnormalized) path, which is the form the Git REST endpoint expects.
   /// Kept as a per-file call so the indexer can stream content lazily instead of loading the whole
   /// repository into memory.
   /// </summary>
   public Task<string> GetFileContentAsync( SourceFileInfo file )
      => _azure.GetGitFileContentAsync( _repositoryId, file.NativePath, _branch );

   /// <summary>
   /// Best-effort provenance lookup returning the most recent author and change date for a file.
   /// Delegates to the shared service by native path; the result is advisory only, since Git history
   /// may be unavailable or approximate for a given item.
   /// </summary>
   public Task<(string author, string date)> GetBasicMetadataAsync( SourceFileInfo file )
      => _azure.GetGitFileMetadataAsync( _repositoryId, file.NativePath, _branch );

   #endregion
}
