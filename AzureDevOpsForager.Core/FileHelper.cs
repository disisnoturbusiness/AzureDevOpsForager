using System;
using System.IO;
using Newtonsoft.Json;

namespace AzureDevOpsForager.Core
{
   /// <summary>
   /// Centralized file I/O gateway used by every AzureDevOpsForager component that needs to
   /// persist or load data from disk (cached API payloads, run manifests, serialized settings, etc.).
   /// Wraps the raw <see cref="System.IO.File"/> primitives so that callers get uniform error
   /// handling and logging instead of ad-hoc try/catch blocks scattered across the codebase.
   /// Why this exists: before this helper, direct File.WriteAllText / File.ReadAllText calls were
   /// sprinkled throughout the project, each with its own (or no) error handling. Funneling them
   /// through one place means a single failure convention: log the problem via <see cref="Logger"/>
   /// and return a sentinel (false / null) rather than letting an IOException escape to the caller.
   /// </summary>
   public static class FileHelper
   {
      #region Public Methods

      /// <summary>
      /// Writes the given text to a file, first creating the target directory if it does not yet exist.
      /// Used for persisting plain-text artifacts (logs, exported reports, raw JSON strings).
      /// Any I/O failure is logged and reported as a false return rather than thrown, so callers can
      /// treat a failed write as a soft error in a larger pipeline.
      /// </summary>
      /// <param name="filePath">Full path of the file to write; its parent directory is auto-created.</param>
      /// <param name="content">The text content to write, overwriting any existing file.</param>
      /// <param name="category">Log category tag, letting log output be filtered by subsystem.</param>
      /// <returns>True when the write succeeds, false when an exception was caught and logged.</returns>
      public static bool WriteText( string filePath, string content, string category = "FileIO" )
      {
         try
         {
            EnsureParentDirectory( filePath );
            File.WriteAllText( filePath, content );
            return true;
         }
         catch( Exception exception )
         {
            Logger.Error( $"Failed to write file: {filePath}", category, exception );
            return false;
         }
      }

      /// <summary>
      /// Reads the entire contents of a text file. Returns null (not an exception) when the file is
      /// missing, which lets callers distinguish "no cached data yet" from a genuine read failure
      /// without branching on File.Exists themselves.
      /// </summary>
      /// <param name="filePath">Full path of the file to read.</param>
      /// <param name="category">Log category tag used if a read error must be logged.</param>
      /// <returns>The file's text, or null if the file is absent or an error was caught and logged.</returns>
      public static string ReadText( string filePath, string category = "FileIO" )
      {
         try
         {
            if( !File.Exists( filePath ) )
            {
               return null;
            }
            return File.ReadAllText( filePath );
         }
         catch( Exception exception )
         {
            Logger.Error( $"Failed to read file: {filePath}", category, exception );
            return null;
         }
      }

      /// <summary>
      /// Appends text to a file, creating the target directory (and the file itself) if needed.
      /// Handy for line-oriented, grow-over-time outputs such as run logs where each call adds to
      /// the tail rather than replacing the whole file. Failures are logged and surfaced as false.
      /// </summary>
      /// <param name="filePath">Full path of the file to append to; its parent directory is auto-created.</param>
      /// <param name="content">The text to append at the end of the file.</param>
      /// <param name="category">Log category tag for any error logging.</param>
      /// <returns>True on success, false when an exception was caught and logged.</returns>
      public static bool AppendText( string filePath, string content, string category = "FileIO" )
      {
         try
         {
            EnsureParentDirectory( filePath );
            File.AppendAllText( filePath, content );
            return true;
         }
         catch( Exception exception )
         {
            Logger.Error( $"Failed to append to file: {filePath}", category, exception );
            return false;
         }
      }

      /// <summary>
      /// Serializes an object to JSON and writes it to disk. This is the standard way the app
      /// persists structured state (DTOs, settings, cached API responses) so it can be reloaded
      /// later via <see cref="ReadJson{T}"/>. Serialization and the underlying write are both
      /// guarded; on any failure the error is logged and false is returned.
      /// </summary>
      /// <typeparam name="T">Type of the object being serialized.</typeparam>
      /// <param name="filePath">Destination file path for the JSON.</param>
      /// <param name="value">The object graph to serialize.</param>
      /// <param name="formatted">
      /// When true (default) the JSON is written with indentation for human readability; when false
      /// it is emitted compactly to save space.
      /// </param>
      /// <param name="category">Log category tag for error logging.</param>
      /// <returns>True on success, false when serialization or the write failed (and was logged).</returns>
      public static bool WriteJson<T>( string filePath, T value, bool formatted = true, string category = "FileIO" )
      {
         try
         {
            var json = JsonConvert.SerializeObject( value, formatted ? Formatting.Indented : Formatting.None );
            return WriteText( filePath, json, category );
         }
         catch( Exception exception )
         {
            Logger.Error( $"Failed to write JSON to file: {filePath}", category, exception );
            return false;
         }
      }

      /// <summary>
      /// Reads a JSON file from disk and deserializes it back into a strongly-typed object; the
      /// counterpart to <see cref="WriteJson{T}"/> for reloading persisted state. Returns null when
      /// the file is missing or empty (nothing to load) or when deserialization throws.
      /// </summary>
      /// <typeparam name="T">Reference type to deserialize the JSON into.</typeparam>
      /// <param name="filePath">Path of the JSON file to read.</param>
      /// <param name="category">Log category tag for error logging.</param>
      /// <returns>The deserialized object, or null if the file was absent/empty or an error was logged.</returns>
      public static T ReadJson<T>( string filePath, string category = "FileIO" ) where T : class
      {
         try
         {
            var json = ReadText( filePath, category );
            if( string.IsNullOrEmpty( json ) )
            {
               return null;
            }
            return JsonConvert.DeserializeObject<T>( json );
         }
         catch( Exception exception )
         {
            Logger.Error( $"Failed to read JSON from file: {filePath}", category, exception );
            return null;
         }
      }

      /// <summary>
      /// Deletes a file if it is present. A missing file is treated as success (nothing to do), so
      /// callers can call this idempotently during cleanup without checking existence first.
      /// </summary>
      /// <param name="filePath">Path of the file to remove.</param>
      /// <param name="category">Log category tag for error logging.</param>
      /// <returns>True if the file was deleted or already absent; false only when deletion threw and was logged.</returns>
      public static bool DeleteFile( string filePath, string category = "FileIO" )
      {
         try
         {
            if( File.Exists( filePath ) )
            {
               File.Delete( filePath );
            }
            return true;
         }
         catch( Exception exception )
         {
            Logger.Error( $"Failed to delete file: {filePath}", category, exception );
            return false;
         }
      }

      /// <summary>
      /// Reports whether a file exists. A thin, allocation-free convenience wrapper so callers can
      /// go through the same helper surface as the rest of their file operations.
      /// </summary>
      /// <param name="filePath">Path to test.</param>
      /// <returns>True if the file exists, otherwise false.</returns>
      public static bool Exists( string filePath )
      {
         return File.Exists( filePath );
      }

      /// <summary>
      /// Ensures a directory exists, creating it (and any missing parents) if necessary. Unlike the
      /// internal parent-directory guard used by the write methods, this is a public, self-contained
      /// operation whose own failures are caught and logged rather than propagated, so it fits the
      /// same soft-failure contract as the other public helpers.
      /// </summary>
      /// <param name="directory">The directory path to guarantee exists.</param>
      /// <param name="category">Log category tag for error logging.</param>
      /// <returns>True if the directory exists (or was created); false when creation threw and was logged.</returns>
      public static bool EnsureDirectory( string directory, string category = "FileIO" )
      {
         try
         {
            if( !Directory.Exists( directory ) )
            {
               Directory.CreateDirectory( directory );
            }
            return true;
         }
         catch( Exception exception )
         {
            Logger.Error( $"Failed to create directory: {directory}", category, exception );
            return false;
         }
      }

      #endregion

      #region Private Methods

      /// <summary>
      /// Creates the parent directory of a target file path when it is missing. Shared by
      /// <see cref="WriteText"/> and <see cref="AppendText"/> so the "make sure the folder is there
      /// before writing" logic lives in exactly one place. Deliberately lets any failure propagate:
      /// the calling method's own try/catch is what logs the error and returns the false sentinel,
      /// so this helper stays a pure precondition step.
      /// </summary>
      /// <param name="filePath">The file path whose parent directory should be ensured.</param>
      private static void EnsureParentDirectory( string filePath )
      {
         var directory = Path.GetDirectoryName( filePath );
         if( !string.IsNullOrEmpty( directory ) && !Directory.Exists( directory ) )
         {
            Directory.CreateDirectory( directory );
         }
      }

      #endregion
   }
}
