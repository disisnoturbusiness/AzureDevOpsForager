using System;
using System.IO;
using System.Windows.Forms;
using AzureDevOpsForager.Core;

namespace AzureDevOpsForagerGroq.WinForms
{
   /// <summary>
   /// Process entry point for the Groq flavour of the Azure DevOps Forager WinForms client.
   /// This class does nothing more than bootstrap the application: it loads configuration,
   /// initialises the WinForms runtime, and hands control to the main chat form. It is a
   /// static host class (no instances are ever created), which is the conventional shape
   /// for a WinForms <c>Main</c> launcher.
   /// </summary>
   internal static class Groq
   {
      #region Private Methods

      /// <summary>
      /// The main entry point for the application.
      /// </summary>
      /// <remarks>
      /// Configuration is layered so that a self-hoster can override the shipped defaults
      /// (which point at the hosted demo) without editing the binaries. First the on-disk
      /// <c>config.json</c> next to the executable is applied, then any per-user overrides
      /// on top of it. Both loads are wrapped in swallow-everything try/catch blocks on
      /// purpose: a missing or malformed config file should degrade to the built-in
      /// defaults rather than prevent the application from starting.
      /// </remarks>
      [STAThread]
      private static void Main()
      {
         //Application.SetHighDpiMode( HighDpiMode.SystemAware );

         // Apply shipped defaults first, then let per-user settings (e.g. a custom Server URL) win.
         try { Config.LoadFromFile( Path.Combine( AppContext.BaseDirectory, "config.json" ) ); } catch { }
         try { Config.LoadUserOverrides(); } catch { }

         // If the user built a local index (shared config has a DB), point at the local Server and start it
         // if it isn't already running — no prompt. Makes a self-hosted index searchable the moment the client
         // opens; a no-op when only the hosted demo is configured.
         try { AzureDevOpsForager.Core.Services.Utilities.LocalServerLauncher.EnsureLocalServerRunning(); } catch { }

         Application.EnableVisualStyles();
         Application.SetCompatibleTextRenderingDefault( false );
         Application.Run( new GroqMainForm() );
      }

      #endregion Private Methods
   }
}
