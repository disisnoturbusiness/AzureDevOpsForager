using System;

namespace AzureDevOpsForager.Core.Services.Storage
{
   /// <summary>
   /// Turns the raw fields captured on the connection form (server name, database name, the
   /// "use Windows authentication" checkbox, and an optional SQL login/password) into a single
   /// ADO.NET connection string.
   ///
   /// The class also centralizes one piece of domain knowledge the UI needs: Azure SQL Database
   /// does not support Windows/integrated authentication, so whenever the target server is an
   /// Azure logical server the form has to fall back to a SQL login. Keeping that rule here means
   /// the form and the builder can never disagree about which auth mode is legal for a given server.
   /// </summary>
   public static class ConnectionStringBuilder
   {
      #region Data Members

      /// <summary>
      /// Host-name fragment that identifies an Azure SQL Database logical server. Every Azure SQL
      /// endpoint lives under the "database.windows.net" domain, so a case-insensitive substring
      /// match against this token is a reliable, cheap way to distinguish cloud servers from on-prem
      /// SQL Server instances without a network round-trip.
      /// </summary>
      private const string AzureServerHostToken = "database.windows.net";

      /// <summary>
      /// Encryption clause used when talking to Azure SQL. Azure presents certificates signed by a
      /// trusted public CA, so we require encryption AND validate the server certificate (no bypass).
      /// </summary>
      private const string AzureEncryptionSettings = "Encrypt=True;TrustServerCertificate=False;";

      /// <summary>
      /// Encryption clause used for on-premises SQL Server. These instances typically use
      /// self-signed development certificates, so we still require encryption but trust the
      /// certificate rather than failing the handshake on an untrusted chain.
      /// </summary>
      private const string OnPremEncryptionSettings = "Encrypt=True;TrustServerCertificate=True;";

      #endregion

      #region Public Methods

      /// <summary>
      /// Reports whether the supplied server name refers to an Azure SQL Database logical server.
      /// This drives the form's auth logic: Azure servers cannot use Windows authentication.
      /// </summary>
      /// <param name="server">The server / host name entered on the connection form.</param>
      /// <returns>
      /// True when <paramref name="server"/> is non-empty and contains the Azure SQL host token;
      /// otherwise false (treated as on-premises SQL Server).
      /// </returns>
      public static bool IsAzure( string server ) =>
         !string.IsNullOrEmpty( server ) &&
         server.IndexOf( AzureServerHostToken, StringComparison.OrdinalIgnoreCase ) >= 0;

      /// <summary>
      /// Indicates whether the form must force SQL authentication (show the User/Password fields and
      /// disable the Windows-auth option) for the given server. Because Azure SQL has no support for
      /// integrated auth, this is currently equivalent to <see cref="IsAzure"/>; it exists as a
      /// separate, intent-revealing method so the UI expresses the requirement rather than the cause.
      /// </summary>
      /// <param name="server">The server / host name entered on the connection form.</param>
      /// <returns>True when only SQL authentication is valid for this server.</returns>
      public static bool RequiresSqlAuth( string server ) => IsAzure( server );

      /// <summary>
      /// Builds the ADO.NET connection string for the requested server and database.
      ///
      /// Windows (trusted) authentication is honored only for on-premises servers when the caller
      /// asks for it. For Azure servers, or whenever the Windows-auth flag is off, the method emits
      /// a SQL-login connection string using the supplied user and password. The encryption clause
      /// is chosen to match the server type (validated certs for Azure, trusted self-signed certs
      /// for on-prem) so the resulting string connects cleanly in both environments.
      /// </summary>
      /// <param name="server">Target server / host name.</param>
      /// <param name="database">Initial catalog (database) to connect to.</param>
      /// <param name="useWindowsAuth">
      /// When true, request trusted (integrated) auth; honored only when the server is on-premises.
      /// </param>
      /// <param name="user">SQL login name; used when SQL authentication is selected.</param>
      /// <param name="password">SQL login password; used when SQL authentication is selected.</param>
      /// <returns>A ready-to-use ADO.NET connection string.</returns>
      public static string Build( string server, string database, bool useWindowsAuth, string user = null, string password = null )
      {
         var isAzureServer = IsAzure( server );

         // Trusted auth is only legal on-prem, so honor the checkbox only when the server isn't Azure.
         if( useWindowsAuth && !isAzureServer )
            return BuildWindowsAuthConnectionString( server, database );

         return BuildSqlAuthConnectionString( server, database, user, password, isAzureServer );
      }

      #endregion

      #region Private Methods

      /// <summary>
      /// Composes a trusted-connection (Windows/integrated auth) string for an on-premises server.
      /// </summary>
      private static string BuildWindowsAuthConnectionString( string server, string database ) =>
         $"Server={server};Database={database};Trusted_Connection=True;TrustServerCertificate=True;";

      /// <summary>
      /// Composes a SQL-login connection string, selecting the encryption clause that matches the
      /// server type. This path is mandatory for Azure and is also used on-prem when the caller has
      /// turned Windows authentication off.
      /// </summary>
      private static string BuildSqlAuthConnectionString( string server, string database, string user, string password, bool isAzureServer )
      {
         var encryptionSettings = isAzureServer ? AzureEncryptionSettings : OnPremEncryptionSettings;
         return $"Server={server};Database={database};User ID={user};Password={password};{encryptionSettings}";
      }

      #endregion
   }
}
