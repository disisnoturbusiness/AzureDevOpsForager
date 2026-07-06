namespace AzureDevOpsForager.Core.Models.API;

/// <summary>
/// Request payload for the /search_by_filename API endpoint. Callers use this to look up
/// indexed source files by their name (or a fragment of it) rather than by full-text or
/// semantic content, which is handy when a user already knows roughly what a file is called
/// and just wants to jump to it. The properties map directly to the JSON body the endpoint
/// deserializes, so any rename here would break the public API contract.
/// </summary>
public class FilenameRequest
{
   #region Data Members

   /// <summary>
   /// The filename (or partial filename) to match against the indexed files. This is the
   /// primary search term for the filename lookup; the search layer decides how strictly it
   /// is matched (exact, prefix, or contains) so we keep it as a free-form string here.
   /// </summary>
   public string Filename
   {
      get; set;
   }

   /// <summary>
   /// Maximum number of matching files to return, capping the result set so a broad filename
   /// fragment does not flood the caller. Defaults to 5, which keeps the typical "find the
   /// file I mean" response small and fast while still surfacing close alternatives.
   /// </summary>
   public int NResults { get; set; } = 5;

   #endregion
}
