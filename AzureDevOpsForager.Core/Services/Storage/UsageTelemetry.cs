using System;
using System.Net;
using System.Threading.Tasks;
using Microsoft.Data.SqlClient;

namespace AzureDevOpsForager.Core.Services.Storage;

/// <summary>
/// What one index build did: counts, timings, and how it ended. Filled in by the indexer as the run
/// progresses and handed to <see cref="UsageTelemetry.RecordIndexRunAsync"/> once, at the end.
/// <para>
/// A plain mutable bag on purpose. The values become known at different stages — the source description
/// at service init, the listed count at STEP 3, the error counts at STEP 4, the staged count and outcome
/// at STEP 5 — and threading thirteen out-parameters back up through the pipeline to build one immutable
/// object at the bottom would be worse than letting the orchestrator fill this in as it goes.
/// </para>
/// </summary>
public sealed class IndexRunRecord
{
   /// <summary>When the build started, UTC.</summary>
   public DateTime StartedUtc { get; set; }

   /// <summary>Wall-clock duration of the whole run in milliseconds, including a run that aborted early.</summary>
   public int DurationMs { get; set; }

   /// <summary>Promoted | NoFilesListed | BelowThreshold | Cancelled | Failed.</summary>
   public string Outcome { get; set; }

   /// <summary>Which source the files came from, as the provider describes itself.</summary>
   public string SourceDescription { get; set; }

   /// <summary>How many indexable files the source listed, after the hosted-embedding cap.</summary>
   public int FilesListed { get; set; }

   /// <summary>How many rows actually landed in staging. Compared against <see cref="FilesListed"/> by the promotion guard.</summary>
   public long FilesStaged { get; set; }

   /// <summary>Files that could not be fetched or threw before chunking.</summary>
   public int FetchErrors { get; set; }

   /// <summary>Chunks that could not be embedded or written.</summary>
   public int ChunkErrors { get; set; }

   /// <summary>Degree of parallelism the run chose, which differs for local versus remote embedding.</summary>
   public int Parallelism { get; set; }

   /// <summary>local-onnx | huggingface | http | none.</summary>
   public string EmbeddingBackend { get; set; }

   /// <summary>The vector width the corpus was written at. A change here invalidates the whole index.</summary>
   public int EmbeddingDimension { get; set; }

   /// <summary>Set only when the run threw; type name and message, truncated.</summary>
   public string ErrorMessage { get; set; }
}

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
/// dbo.UsageEvents records no client identifier of any kind — no IP, no user agent, no session. See
/// the DDL in <see cref="SchemaInitializer"/> for why. dbo.SiteVisits is the deliberate exception and
/// does store the caller's address; see <see cref="RecordVisit(string)"/> for that reasoning.
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

   /// <summary>
   /// Records one site visit: the time, and the client IP as given. Called once per page load, never per
   /// search — a visitor who runs twenty searches is one visit, not twenty.
   /// <para>
   /// The address is stored verbatim rather than hashed or truncated, because the whole point is to be
   /// able to resolve it at read time. Application Insights already collects requests for this site and
   /// zeroes client_IP to 0.0.0.0 before storing, keeping only a city guess — which on this deployment
   /// resolved a Michigan visitor to Detroit, four hours from where they actually were. Geo derived from
   /// an IP is not a substitute for the IP.
   /// </para>
   /// </summary>
   /// <param name="clientIp">Client address, already extracted from X-Forwarded-For by the caller.</param>
   public static void RecordVisit( string clientIp, string path = null )
   {
      var address = NormalizeIp( clientIp );

      if( IsSyntheticVisit( address ) )
         return;

      // Recorded because arrivals are no longer all the same arrival. A writeup exists to pull search
      // traffic, and a single undifferentiated visit count cannot say whether it did.
      var landedOn = Cap( path, 200 );

      Fire( async connection =>
      {
         using var command = new SqlCommand(
            "INSERT INTO dbo.SiteVisits (ClientIp, Path) VALUES (@ip, @path);", connection );

         command.Parameters.AddWithValue( "@ip", address );
         command.Parameters.AddWithValue( "@path", (object)landedOn ?? DBNull.Value );

         await command.ExecuteNonQueryAsync();
      } );
   }

   /// <summary>
   /// Takes the client address out of an X-Forwarded-For value. App Service puts the real caller first and
   /// appends a source port ("203.0.113.7:51234"), and proxies in front may add further hops after a comma,
   /// so the first entry is the one that matters. IPv6 is left intact — it contains colons of its own, so
   /// the port is only stripped when exactly one colon is present.
   /// </summary>
   private static string NormalizeIp( string clientIp )
   {
      var text = ( clientIp ?? "" ).Trim();
      if( text.Length == 0 )
         return null;

      var firstHop = text.Split( ',' )[0].Trim();
      var colons = firstHop.Split( ':' ).Length - 1;
      if( colons == 1 )
         firstHop = firstHop.Substring( 0, firstHop.IndexOf( ':' ) );

      return firstHop.Length > 45 ? firstHop.Substring( 0, 45 ) : firstHop;
   }

   /// <summary>
   /// Records one finished index build — promoted, aborted, or failed.
   /// <para>
   /// Awaited rather than fire-and-forget, which is the one deliberate departure from the rest of this
   /// class. A search runs inside a request that outlives the write; a build is the last thing its process
   /// does, so a background write would race process exit and lose precisely the rows worth having.
   /// Exceptions are still swallowed — telemetry must never turn a good build into a failed one.
   /// </para>
   /// <para>
   /// Takes the connection string explicitly instead of reading <c>Config.SqlConnectionString</c>, because
   /// the indexer is routinely pointed at a different database than the server. A local rebuild filing its
   /// run record into the live demo's table would be worse than not recording it at all.
   /// </para>
   /// </summary>
   /// <param name="connectionString">The database the build wrote to, not necessarily the server's.</param>
   /// <param name="run">The run to record. Ignored when null.</param>
   public static async Task RecordIndexRunAsync( string connectionString, IndexRunRecord run )
   {
      if( run == null || string.IsNullOrWhiteSpace( connectionString ) )
         return;

      try
      {
         using var connection = new SqlConnection( connectionString );
         await connection.OpenAsync();

         using var command = new SqlCommand(
            @"INSERT INTO dbo.IndexRuns
                 (StartedUtc, DurationMs, Outcome, SourceDescription, FilesListed, FilesStaged,
                  FetchErrors, ChunkErrors, Parallelism, EmbeddingBackend, EmbeddingDimension, ErrorMessage)
              VALUES
                 (@startedUtc, @durationMs, @outcome, @source, @filesListed, @filesStaged,
                  @fetchErrors, @chunkErrors, @parallelism, @backend, @dimension, @error);", connection );

         command.Parameters.AddWithValue( "@startedUtc", run.StartedUtc );
         command.Parameters.AddWithValue( "@durationMs", run.DurationMs );
         command.Parameters.AddWithValue( "@outcome", Cap( run.Outcome, 24 ) ?? "Unknown" );
         command.Parameters.AddWithValue( "@source", (object)Cap( run.SourceDescription, 200 ) ?? DBNull.Value );
         command.Parameters.AddWithValue( "@filesListed", run.FilesListed );
         command.Parameters.AddWithValue( "@filesStaged", run.FilesStaged );
         command.Parameters.AddWithValue( "@fetchErrors", run.FetchErrors );
         command.Parameters.AddWithValue( "@chunkErrors", run.ChunkErrors );
         command.Parameters.AddWithValue( "@parallelism", run.Parallelism );
         command.Parameters.AddWithValue( "@backend", (object)Cap( run.EmbeddingBackend, 16 ) ?? DBNull.Value );
         command.Parameters.AddWithValue( "@dimension", run.EmbeddingDimension );
         command.Parameters.AddWithValue( "@error", (object)Cap( run.ErrorMessage, 400 ) ?? DBNull.Value );

         await command.ExecuteNonQueryAsync();
      }
      catch( Exception exception )
      {
         Logger.Warn( $"index-run telemetry write failed: {exception.GetType().Name}: {exception.Message}", "Telemetry" );
      }
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

   /// <summary>
   /// True when an arrival came from infrastructure rather than a person.
   /// <para>
   /// Always On is enabled on this deployment, so App Service GETs "/" from inside the container roughly
   /// every five minutes, around the clock. Those requests arrive over loopback with no X-Forwarded-For
   /// and are indistinguishable from a page load at the middleware. The platform health probe on the
   /// link-local range behaves the same way.
   /// </para>
   /// <para>
   /// This is the rule <see cref="IsSynthetic(string)"/> already applies to the usage table, and its
   /// absence here did exactly what that method's remarks predict. Measured 2026-09-28: the timer was
   /// 13,540 of 14,068 rows in dbo.SiteVisits — 96% — against roughly 520 real arrivals across seven
   /// weeks. Every visit count taken off that table was the timer, not an audience. Rows already written
   /// are not removed; read them with a loopback filter.
   /// </para>
   /// </summary>
   /// <param name="clientIp">The normalised address, as it would be written to the row.</param>
   private static bool IsSyntheticVisit( string clientIp )
   {
      var text = ( clientIp ?? "" ).Trim();

      // "::ffff:127.0.0.1" is the same loopback wearing an IPv6 hat. Stripped textually because
      // netstandard2.0 has neither IsIPv4MappedToIPv6 nor MapToIPv4.
      if( text.StartsWith( "::ffff:", StringComparison.OrdinalIgnoreCase ) )
         text = text.Substring( 7 );

      // An address that will not parse cannot be attributed to a visitor, so it is not counted as one.
      if( !IPAddress.TryParse( text, out var address ) )
         return true;

      if( IPAddress.IsLoopback( address ) || address.IsIPv6LinkLocal )
         return true;

      // 169.254.0.0/16 — IPv4 link-local, where the App Service health probe lives.
      var bytes = address.GetAddressBytes();
      return bytes.Length == 4 && bytes[0] == 169 && bytes[1] == 254;
   }

   /// <summary>Truncates to a column width, mapping null and whitespace to null so the row stores NULL.</summary>
   private static string Cap( string text, int maxLength )
   {
      var trimmed = ( text ?? "" ).Trim();
      if( trimmed.Length == 0 )
         return null;

      return trimmed.Length > maxLength ? trimmed.Substring( 0, maxLength ) : trimmed;
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
