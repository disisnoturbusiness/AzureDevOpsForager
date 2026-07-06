using System;

namespace AzureDevOpsForager.Core.Misc;
/// <summary>
/// Application-wide static holder for the small set of settings that every
/// AzureDevOpsForager component needs to reach before any per-instance
/// configuration exists (Azure DevOps connection details and the global
/// debug switch). Values here are read once at process start from the
/// environment so the connection secrets never have to be hard-coded, and
/// they act as the seed defaults that <c>Config</c> later exposes as
/// mutable, user-editable properties.
/// </summary>
public class Global
{
   #region Data Members

   /// <summary>
   /// Base URL of the Azure DevOps organization to forage against.
   /// Sourced from the <c>AZDO_URL</c> environment variable so the endpoint
   /// can be supplied by the launch environment or CI rather than baked into
   /// source. Falls back to an empty string, which downstream code treats as
   /// "Azure integration disabled." Consumed as the initial value of
   /// <c>Config.AzureUrl</c>.
   /// </summary>
   public static readonly string AzureDevOpsUrl = Environment.GetEnvironmentVariable( "AZDO_URL" ) ?? "";

   /// <summary>
   /// Personal Access Token used to authenticate against Azure DevOps.
   /// Sourced from the <c>AZDO_PAT</c> environment variable to keep the
   /// secret out of source control and off disk. Defaults to an empty string
   /// when unset. Consumed as the initial value of <c>Config.AzurePAT</c>.
   /// </summary>
   public static readonly string AzureDevOpsPat = Environment.GetEnvironmentVariable( "AZDO_PAT" ) ?? "";

   /// <summary>
   /// Master switch for diagnostic logging across the whole application.
   /// The centralized <c>Logger</c> checks this flag first and short-circuits
   /// every log call when it is off, which keeps the normal run quiet and
   /// avoids the scattered Console.WriteLine calls this replaced. Off by
   /// default so production runs stay silent unless logging is opted into.
   /// </summary>
   public static bool DebugLogging = false;

   #endregion
}
