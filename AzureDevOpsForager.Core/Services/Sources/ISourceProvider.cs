using System;
using System.Collections.Generic;
using System.Threading.Tasks;

namespace AzureDevOpsForager.Core.Services.Sources;
/// <summary>
/// Abstraction over a code source that the indexer harvests files from. Concrete
/// implementations wrap Azure DevOps TFVC, Azure DevOps Git, and GitHub.
/// The indexer is written against this contract rather than any one provider so that
/// adding a new backend (or swapping one out) never ripples into the indexing pipeline.
/// </summary>
public interface ISourceProvider
{
   /// <summary>
   /// Human-readable description of where this source reads from (for example, a repo
   /// URL or a local folder path). Surfaced in logs and UI so an operator can tell at a
   /// glance which backend a given indexing run was pointed at.
   /// </summary>
   string SourceDescription { get; }

   /// <summary>
   /// Enumerates every file the provider considers indexable. The returned list is
   /// already filtered by the provider's include/exclude globs, so callers can treat
   /// each entry as a file that should be fetched and indexed without re-filtering.
   /// </summary>
   Task<List<SourceFileInfo>> GetAllFilesAsync();

   /// <summary>
   /// Fetches the latest text content for a single file. Kept separate from
   /// enumeration so the indexer can stream content on demand rather than loading every
   /// file into memory up front, which matters for large repositories.
   /// </summary>
   Task<string> GetFileContentAsync( SourceFileInfo file );

   /// <summary>
   /// Returns the most-recent author and change date for a file as a best-effort
   /// provenance lookup. Providers that cannot cheaply resolve history may return empty
   /// or approximate values, so callers must treat this metadata as advisory only.
   /// </summary>
   Task<(string author, string date)> GetBasicMetadataAsync( SourceFileInfo file );
}

/// <summary>
/// A single file discovered by an <see cref="ISourceProvider"/>. Acts as the neutral
/// hand-off record between a source backend and the indexer: it carries both the
/// normalized path used for filtering and storage and the provider-native path needed
/// to fetch content, so the two concerns never have to agree on a single path format.
/// </summary>
public class SourceFileInfo
{
   #region Data Members

   /// <summary>
   /// Normalized forward-slash path relative to the source root. This is the canonical
   /// identity of the file across providers and is what glob filtering and stored index
   /// records key on, regardless of the backend's native path conventions.
   /// </summary>
   public string RelativePath { get; set; }

   /// <summary>
   /// Provider-native path used to actually fetch content (TFVC uses the "$/..." server
   /// path form, Git uses a "/..." repo-relative form). Retained alongside
   /// <see cref="RelativePath"/> so the indexer can request content without knowing which
   /// backend produced this record.
   /// </summary>
   public string NativePath { get; set; }

   /// <summary>
   /// Last-change date if the source exposes one; null when the backend does not report
   /// per-file timestamps. Nullable by design so a missing value stays distinguishable
   /// from a genuine epoch/default date.
   /// </summary>
   public DateTime? ChangeDate { get; set; }

   #endregion
}
