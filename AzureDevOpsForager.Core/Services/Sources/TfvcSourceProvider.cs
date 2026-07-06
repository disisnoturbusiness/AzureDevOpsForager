using System.Collections.Generic;
using System.Linq;
using System.Threading.Tasks;
using AzureDevOpsForager.Core.Services.Integration;

namespace AzureDevOpsForager.Core.Services.Sources;
/// <summary>
/// Source backend that harvests files from an Azure DevOps TFVC (Team Foundation Version
/// Control) repository. This is the flagship adapter: it satisfies the neutral
/// <see cref="ISourceProvider"/> contract by delegating the actual server calls to an
/// <see cref="AzureDevOpsService"/>, which owns the TFVC root and connection details.
///
/// The value this class adds on top of the raw service is shaping: it filters out folders,
/// projects each server item into the indexer's neutral <see cref="SourceFileInfo"/> record,
/// and applies the configured include/exclude globs so the indexer downstream never has to
/// know it is talking to TFVC specifically.
/// </summary>
public class TfvcSourceProvider : ISourceProvider
{
   #region Data Members

   /// <summary>
   /// The Azure DevOps client that performs the real TFVC calls (enumeration, content
   /// fetch, history lookup). All backend-specific knowledge lives here; this provider only
   /// orchestrates and reshapes what it returns.
   /// </summary>
   private readonly AzureDevOpsService _azure;

   /// <summary>
   /// Include/exclude glob rules applied to each discovered file's relative path. Guarantees
   /// callers of <see cref="GetAllFilesAsync"/> receive an already-filtered list, honoring the
   /// contract that entries need no further filtering before indexing.
   /// </summary>
   private readonly SourceFilterOptions _filter;

   #endregion

   #region Constructor

   /// <summary>
   /// Builds a TFVC source provider over an existing Azure DevOps client. The filter is
   /// optional; when none is supplied we fall back to a default <see cref="SourceFilterOptions"/>
   /// (which includes everything) so the provider is always safe to call without extra setup.
   /// </summary>
   /// <param name="azure">Configured Azure DevOps client that holds the TFVC root and credentials.</param>
   /// <param name="filter">Optional glob filter; a permissive default is used when null.</param>
   public TfvcSourceProvider( AzureDevOpsService azure, SourceFilterOptions filter = null )
   {
      _azure = azure;
      _filter = filter ?? new SourceFilterOptions();
   }

   #endregion

   #region Public Methods

   /// <summary>
   /// Human-readable label identifying this backend as Azure DevOps TFVC. Shown in logs and
   /// UI so an operator can tell which source an indexing run was pointed at.
   /// </summary>
   public string SourceDescription => "Azure DevOps (TFVC)";

   /// <summary>
   /// Enumerates every indexable file in the TFVC repository. Pulls the full item list from
   /// the server, drops folder entries (only leaf files are indexable), maps each remaining
   /// item into a neutral <see cref="SourceFileInfo"/>, then keeps only the paths the
   /// configured filter accepts.
   ///
   /// Note the two path forms carried per file: the TFVC server path is preserved as
   /// <see cref="SourceFileInfo.NativePath"/> for later content fetches, while a
   /// forward-slash relative path becomes the canonical identity used for filtering and
   /// storage. Backslashes are normalized to forward slashes so paths compare consistently
   /// regardless of TFVC's native separator.
   /// </summary>
   public async Task<List<SourceFileInfo>> GetAllFilesAsync()
   {
      var items = await _azure.GetAllItemsAsync();
      return items
         .Where( item => !item.IsFolder )
         .Select( item => new SourceFileInfo
         {
            NativePath = item.Path,
            RelativePath = _azure.ToRelativePath( item.Path ).Replace( '\\', '/' ),
            ChangeDate = item.ChangeDate
         } )
         .Where( fileInfo => _filter.ShouldInclude( fileInfo.RelativePath ) )
         .ToList();
   }

   /// <summary>
   /// Fetches the latest text content for a single file, keyed off its TFVC server path.
   /// Content is pulled on demand (rather than during enumeration) so large repositories do
   /// not have to be loaded into memory all at once.
   /// </summary>
   /// <param name="file">The file record to fetch, whose native path locates it on the server.</param>
   public Task<string> GetFileContentAsync( SourceFileInfo file )
      => _azure.GetLatestFileContentAsync( file.NativePath );

   /// <summary>
   /// Returns best-effort provenance (most-recent author and change date) for a file. This
   /// lookup keys off the relative path and is advisory only, per the
   /// <see cref="ISourceProvider"/> contract; callers must tolerate empty or approximate values.
   /// </summary>
   /// <param name="file">The file record whose recent-change metadata is requested.</param>
   public Task<(string author, string date)> GetBasicMetadataAsync( SourceFileInfo file )
      => _azure.GetBasicMetadataAsync( file.RelativePath );

   #endregion
}
