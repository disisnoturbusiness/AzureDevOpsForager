using System;
using System.IO;
using AzureDevOpsForager.Core.Misc;

namespace AzureDevOpsForager.Core
{
   /// <summary>
   /// Centralized, file-based logging facility shared by every AzureDevOpsForager component.
   /// The class exists so that diagnostic output is written to one predictable location under
   /// the user's local app-data folder instead of being scattered across ad-hoc Console.WriteLine
   /// calls. Informational and warning messages are gated behind the Global.DebugLogging switch so
   /// that a normal (non-debug) run stays quiet, while errors are always persisted because they
   /// matter regardless of the debug setting.
   /// </summary>
   public static class Logger
   {
      #region Data Members

      /// <summary>
      /// Synchronization gate for the log file write. Because the logger is static and can be
      /// called from multiple threads at once, all file access is serialized through this lock so
      /// that two callers never interleave their AppendAllText calls into the same daily log file.
      /// </summary>
      private static readonly object _lockObj = new object();

      #endregion

      #region Public Methods

      /// <summary>
      /// Records an informational message. This is the routine "what happened" breadcrumb trail and
      /// is therefore suppressed entirely unless Global.DebugLogging is turned on, keeping production
      /// logs from filling with noise.
      /// </summary>
      /// <param name="message">The human-readable text to record.</param>
      /// <param name="category">A grouping tag (subsystem name) used to sort log lines; defaults to "General".</param>
      public static void Info( string message, string category = "General" )
      {
         if( !Global.DebugLogging )
            return;
         Log( "INFO", category, message );
      }

      /// <summary>
      /// Records a warning: something unexpected but recoverable. Like Info, warnings are only
      /// written when debug logging is enabled, since a healthy run should not need them.
      /// </summary>
      /// <param name="message">The human-readable text to record.</param>
      /// <param name="category">A grouping tag (subsystem name) used to sort log lines; defaults to "General".</param>
      public static void Warn( string message, string category = "General" )
      {
         if( !Global.DebugLogging )
            return;
         Log( "WARN", category, message );
      }

      /// <summary>
      /// Records an error. Unlike Info and Warn this always writes, even when debug logging is off,
      /// because failures need to be captured for later diagnosis. When an exception is supplied its
      /// message and stack trace are appended to the caller's message so the full failure context
      /// lands in a single log line.
      /// </summary>
      /// <param name="message">The human-readable description of what failed.</param>
      /// <param name="category">A grouping tag (subsystem name) used to sort log lines; defaults to "General".</param>
      /// <param name="exception">The exception behind the failure, if any; its message and stack trace are folded into the entry.</param>
      public static void Error( string message, string category = "General", Exception exception = null )
      {
         var fullMessage = exception != null ? $"{message}: {exception.Message}\n{exception.StackTrace}" : message;
         Log( "ERROR", category, fullMessage );
      }

      /// <summary>
      /// The single low-level write path that all the severity-specific helpers funnel into. It
      /// stamps the entry with the current local time, formats it as
      /// "[timestamp] [level] [category] message", and appends it to today's log file under
      /// %LocalAppData%\AzureDevOpsForager\logs. The daily file name rolls over automatically since
      /// it embeds the current date. Any I/O failure is swallowed on purpose: logging is a
      /// diagnostic side-channel and must never take the application down.
      /// </summary>
      /// <param name="level">Severity label such as INFO, WARN, or ERROR.</param>
      /// <param name="category">A grouping tag (subsystem name) used to sort log lines.</param>
      /// <param name="message">The already-composed message body to write.</param>
      public static void Log( string level, string category, string message )
      {
         var timestamp = DateTime.Now.ToString( "yyyy-MM-dd HH:mm:ss" );
         var logMessage = $"[{timestamp}] [{level}] [{category}] {message}";

         try
         {
            // Serialize file access so concurrent callers don't corrupt the daily log file.
            lock( _lockObj )
            {
               var logDir = Path.Combine( Config.LocalAppDataRoot, "logs" );
               if( !Directory.Exists( logDir ) )
               {
                  Directory.CreateDirectory( logDir );
               }

               // One file per calendar day; the yyyyMMdd stamp makes it roll over on its own.
               var logFile = Path.Combine( logDir, $"forager_{DateTime.Now:yyyyMMdd}.log" );
               File.AppendAllText( logFile, logMessage + Environment.NewLine );
            }
         }
         catch
         {
            // Silent fail: logging shouldn't break the app.
         }
      }

      #endregion
   }
}
