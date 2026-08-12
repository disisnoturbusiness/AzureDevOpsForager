using System;
using System.Threading.Tasks;
using Microsoft.Data.SqlClient;

namespace AzureDevOpsForager.Core.Services.Storage;

/// <summary>
/// Records what the demo is actually being used for, into dbo.UsageEvents.
/// <para>
/// The motivating problem is that this app previously had no way to answer "has anyone used this?".
/// Console output goes to a log stream with a 30-minute window, the file logger writes to a container
/// filesystem that is recreated on every deploy, and the thumbs up/down feedback appended to a
/// relative-path file that was discarded on the next restart. All three look like telemetry and none of
/// them survive the afternoon.
/// </para>
/// <para>
/// Every method here is FIRE-AND-FORGET and swallows its own exceptions. Telemetry must never slow a
/// search, and it must never be the reason a search fails: the database is serverless with auto-pause,
/// so a write can arrive at a resuming instance, and that is a fine reason to lose a telemetry row and
/// an unacceptable reason to lose a user's query. Nothing downstream reads the return value.
/// </para>
/// <para>
/// Records no client identifier of any kind — no IP, no user agent, no session. See the DDL in
/// <see cref="SchemaInitializer"/> for why.
/// </para>
/// </summary>
public static class UsageTelemetry
{
   #region Data Members

   /// <summary>
   /// The heartbeat's query text. A browser tab left open POSTs this every 10 minutes to keep the
   /// scale-to-zero endpoints warm, which would otherwise become the most popular search on the site and
   /// make every usage number meaningless. Filtered here rather than at the caller so there is exactly
   /// one place to change if the heartbeat's payload ever does.
   /// </summary>
   private const string HeartbeatQuery = "warmup keepalive";

   /// <summary>Matches the Question column width; longer questions are truncated rather than dropped.</summary>
   private const int MaxQuestionLength = 400;

   #endregion

   #region Public Methods

   /// <summary>
   /// Records a search or an answer. <paramref name="grounded"/> and <paramref name="topSource"/> are
   /// optional and only meaningful for "ask" and "search" respectively.
   /// </summary>
   /// <param name="eventType">"search" or "ask".</param>
   /// <param name="question">The visitor's query text.</param>
   /// <param name="resultCount">How many results were returned after the relevance gate.</param>
   /// <param name="durationMs">Wall-clock time for the request.</param>
   /// <param name="grounded">For "ask": whether retrieval produced anything to ground the answer in.</param>
   /// <param name="topSource">For "search": the match_source of the top hit (Hybrid / FullText / Vector).</param>
   public static void RecordQuery( string eventType, string question, int resultCount, long durationMs,
                                   bool? grounded = null, string topSource = null )
   {
      if( IsSynthetic( question ) )
         return;

      Fire( async connection =>
      {
         using var command = new SqlCommand(
            @"INSERT INTO dbo.UsageEvents (EventType, Question, ResultCount, DurationMs, Grounded, TopSource)
              VALUES (@type, @question, @count, @duration, @grounded, @source);", connection );

         command.Parameters.AddWithValue( "@type", eventType ?? "search" );
         command.Parameters.AddWithValue( "@question", Trim( question ) );
         command.Parameters.AddWithValue( "@count", resultCount );
         command.Parameters.AddWithValue( "@duration", (int)Math.Min( durationMs, int.MaxValue ) );
         command.Parameters.AddWithValue( "@grounded", (object)grounded ?? DBNull.Value );
         command.Parameters.AddWithValue( "@source", (object)topSource ?? DBNull.Value );

         await command.ExecuteNonQueryAsync();
      } );
   }

   /// <summary>Records a thumbs up/down on an answer.</summary>
   /// <param name="helpful">True for thumbs up.</param>
   /// <param name="question">The question the verdict applies to.</param>
   public static void RecordFeedback( bool helpful, string question )
   {
      Fire( async connection =>
      {
         using var command = new SqlCommand(
            @"INSERT INTO dbo.UsageEvents (EventType, Question, Verdict)
              VALUES ('feedback', @question, @verdict);", connection );

         command.Parameters.AddWithValue( "@question", Trim( question ) );
         command.Parameters.AddWithValue( "@verdict", helpful ? "UP" : "DOWN" );

         await command.ExecuteNonQueryAsync();
      } );
   }

   #endregion

   #region Private Methods

   /// <summary>
   /// True when the query came from the keep-warm heartbeat rather than a person. Synthetic traffic in the
   /// usage table would not merely add noise — the heartbeat fires on a timer and a visitor does not, so it
   /// would dominate the counts and invert the answer to "is anyone using this".
   /// </summary>
   private static bool IsSynthetic( string question )
   {
      return string.Equals( ( question ?? "" ).Trim(), HeartbeatQuery, StringComparison.OrdinalIgnoreCase );
   }

   /// <summary>Truncates to the column width and normalises null to an empty string.</summary>
   private static string Trim( string question )
   {
      var text = ( question ?? "" ).Trim();
      return text.Length > MaxQuestionLength ? text.Substring( 0, MaxQuestionLength ) : text;
   }

   /// <summary>
   /// Runs a write on a background task with its own connection, swallowing everything. Deliberately not
   /// awaited by callers: the request has already been served by the time this matters, and the only thing
   /// a telemetry failure should ever cost is the telemetry.
   /// </summary>
   private static void Fire( Func<SqlConnection, Task> write )
   {
      _ = Task.Run( async () =>
      {
         try
         {
            using var connection = new SqlConnection( Config.SqlConnectionString );
            await connection.OpenAsync();
            await write( connection );
         }
         catch( Exception exception )
         {
            // Logged, not raised. Silent loss would make the telemetry itself untrustworthy — "no rows"
            // has to be distinguishable from "the writer is broken" — but it stays off the request path.
            Logger.Warn( $"usage telemetry write failed: {exception.GetType().Name}: {exception.Message}", "Telemetry" );
         }
      } );
   }

   #endregion
}
