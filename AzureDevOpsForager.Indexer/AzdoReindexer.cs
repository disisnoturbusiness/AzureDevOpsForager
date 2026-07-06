using System;
using System.IO;
using System.Windows.Forms;
using AzureDevOpsForager.Core;

namespace AzureDevOpsForager.Indexer;

/// <summary>
/// Process entry point for the Indexer desktop tool. The Indexer is a single WinForm that lets an operator
/// pick a source (Azure DevOps TFVC, Git, or GitHub), point at a target SQL Server / Azure SQL database,
/// and build the code-search vector index that the rest of the Forager toolset queries.
///
/// This class does nothing but bootstrap: it seeds configuration, applies the standard WinForms rendering
/// setup, then hands control to <see cref="IndexerForm"/> for the rest of the session.
/// </summary>
internal static class Program
{
   #region Private Methods

   /// <summary>
   /// Application entry point. Loads configuration in precedence order, initializes the WinForms runtime,
   /// and runs the main indexer form as the message loop's root window. Marked <see cref="STAThreadAttribute"/>
   /// because WinForms (and the clipboard / common dialogs it uses) require a single-threaded apartment.
   /// </summary>
   [STAThread]
   private static void Main()
   {
      LoadConfiguration();

      Application.EnableVisualStyles();
      Application.SetCompatibleTextRenderingDefault( false );
      Application.Run( new IndexerForm() );
   }

   /// <summary>
   /// Loads configuration so the form opens pre-filled. Two layers are applied in precedence order: first the
   /// per-exe config.json sitting next to the executable, then the shared per-user overrides on top. The
   /// per-user layer wins so a value the operator previously chose (a model path, a target database) follows
   /// them across every Forager exe. Each load is wrapped so a missing or malformed file never blocks startup;
   /// the defaults simply stay in effect.
   /// </summary>
   private static void LoadConfiguration()
   {
      try { Config.LoadFromFile( Path.Combine( AppContext.BaseDirectory, "config.json" ) ); } catch { }
      try { Config.LoadUserOverrides(); } catch { }
   }

   #endregion
}
