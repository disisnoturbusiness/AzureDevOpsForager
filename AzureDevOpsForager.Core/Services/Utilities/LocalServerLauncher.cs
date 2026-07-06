using System;
using System.Diagnostics;
using System.IO;
using System.Net.Http;

namespace AzureDevOpsForager.Core.Services.Utilities
{
   /// <summary>
   /// Makes a self-hosted index "just work" from a client. When the shared user config has a local database
   /// wired in (SqlConnectionString — set at the end of a reindex), the client should search THAT database,
   /// not the hosted demo. So: point the client at a local Server, and if no local Server is answering, start
   /// one — with no prompt. The Server reads the same shared user config, so it serves the same database
   /// automatically. Everything here is best-effort/fail-soft: a client must never crash (or block for long)
   /// because it couldn't autostart a server.
   /// </summary>
   public static class LocalServerLauncher
   {
      /// <summary>Server executable file name, as produced by the Server project.</summary>
      private const string ServerExeName = "AzureDevOpsForager.Server.exe";

      /// <summary>
      /// If a local DB is configured, route the client at the local Server (localhost) and — when nothing is
      /// listening there yet — launch the Server console pointed at that DB. Silent and best-effort. Does
      /// nothing when no local DB is wired (the client then keeps talking to whatever ServerUrl was set, i.e.
      /// the hosted demo).
      /// </summary>
      public static void EnsureLocalServerRunning()
      {
         try
         {
            // No local DB wired => nothing to do; leave ServerUrl pointing at the configured (demo) server.
            if( string.IsNullOrWhiteSpace( Config.SqlConnectionString ) )
               return;

            // A local index exists, so the client should search THAT — point it at the local Server.
            var localUrl = "http://localhost:" + Config.Port;
            Config.ServerUrl = localUrl;

            // Already up? Then there's nothing to start.
            if( IsServerResponding( localUrl ) )
               return;

            // Find the Server exe and start it (console mode). No window juggling, no prompt.
            var serverExe = ResolveServerExe();
            if( serverExe == null )
               return;

            var startInfo = new ProcessStartInfo
            {
               FileName = serverExe,
               WorkingDirectory = Path.GetDirectoryName( serverExe ),
               UseShellExecute = true
            };
            Process.Start( startInfo );
         }
         catch
         {
            // Best-effort: never let an autostart failure stop the client from opening.
         }
      }

      /// <summary>Quick health probe: true if something answers on the local Server's /health within ~1.5s.</summary>
      private static bool IsServerResponding( string baseUrl )
      {
         try
         {
            using( var client = new HttpClient { Timeout = TimeSpan.FromMilliseconds( 1500 ) } )
            {
               var response = client.GetAsync( baseUrl.TrimEnd( '/' ) + "/health" ).GetAwaiter().GetResult();
               return response.IsSuccessStatusCode;
            }
         }
         catch
         {
            return false;
         }
      }

      /// <summary>
      /// Locates the Server executable: an explicit <see cref="Config.ServerExePath"/> wins; otherwise probe
      /// the common packaged layouts next to the client (same folder, or a "server" subfolder). Returns null
      /// when it can't be found — the client is still routed at the local URL, it just can't autostart.
      /// </summary>
      private static string ResolveServerExe()
      {
         if( !string.IsNullOrWhiteSpace( Config.ServerExePath ) && File.Exists( Config.ServerExePath ) )
            return Config.ServerExePath;

         var baseDir = AppContext.BaseDirectory;
         var candidates = new[]
         {
            Path.Combine( baseDir, ServerExeName ),
            Path.Combine( baseDir, "server", ServerExeName ),
            Path.Combine( baseDir, "Server", ServerExeName ),
         };

         foreach( var candidate in candidates )
            if( File.Exists( candidate ) )
               return candidate;

         return null;
      }
   }
}
