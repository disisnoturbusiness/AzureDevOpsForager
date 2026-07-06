namespace AzureDevOpsForager.Core.Models.AzureDevOps;

/// <summary>
/// A flattened, presentation-ready view of a single Azure DevOps TFVC changeset (a check-in).
/// The Azure DevOps REST API returns a richer, deeply nested changeset payload; this model
/// captures just the four fields the Forager needs for provenance display and history caching,
/// with everything reduced to strings so the values can be rendered directly without further
/// formatting. Instances are produced when raw changeset items are projected during a history
/// fetch (author names are simplified, and the date is pre-formatted to "yyyy-MM-dd").
/// </summary>
public class ChangesetInfo
{
   #region Data Members

   /// <summary>
   /// The changeset's numeric identifier, held as a string. Azure DevOps issues an integer
   /// changeset number per check-in; it is stored here as text because the value is only ever
   /// displayed and used as a cache/lookup key, never used in arithmetic.
   /// </summary>
   public string ChangesetId
   {
      get; set;
   }

   /// <summary>
   /// The simplified display name of the person who made the check-in. This is a cleaned-up
   /// author name (the raw payload's display name, with any account/domain noise stripped by
   /// the history projection), falling back to "Unknown" when no author could be determined.
   /// </summary>
   public string Author
   {
      get; set;
   }

   /// <summary>
   /// The check-in date, pre-formatted as an ISO-style "yyyy-MM-dd" string. Kept as text (not a
   /// DateTime) so it renders consistently and sorts lexically; an empty string indicates the
   /// source changeset had no creation date.
   /// </summary>
   public string Date
   {
      get; set;
   }

   /// <summary>
   /// The check-in comment supplied by the author. Defaults to an empty string rather than null
   /// when the changeset carried no comment, so consumers can bind to it without null checks.
   /// </summary>
   public string Comment
   {
      get; set;
   }

   #endregion Data Members
}
