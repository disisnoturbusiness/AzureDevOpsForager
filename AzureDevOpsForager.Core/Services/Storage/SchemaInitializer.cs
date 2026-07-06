using System;
using System.Threading.Tasks;
using Microsoft.Data.SqlClient;

namespace AzureDevOpsForager.Core.Services.Storage
{
   /// <summary>
   /// Auto-provisions the code-search schema in the user's chosen database. This is the single place that
   /// knows how the vector index store is shaped, so the desktop indexer never has to embed raw DDL.
   ///
   /// The main entry points, in the order a typical session hits them:
   ///   - TestConnectionAsync : verify the connection works.
   ///   - EnsureSchemaAsync   : idempotent create-if-missing of tables + full-text + b-tree indexes
   ///                           (runs on connect; NEVER drops data).
   ///   - HasContentAsync     : true if either table already holds rows (gate for the wipe warnings).
   ///   - ResetAsync          : DESTRUCTIVE wipe, only call after the user confirms twice.
   ///
   /// The DiskANN vector index is intentionally NOT created here. It needs >=100 non-NULL vectors, so the
   /// indexer builds it after the first load. All DDL here mirrors Schema/CreateSchema.sql.
   /// </summary>
   public static class SchemaInitializer
   {
      #region Data Members

      /// <summary>
      /// Live CodeFiles table: create only when missing so a reconnect never drops existing data. Built from
      /// the shared <see cref="CodeFilesTable"/> body with an empty suffix (the live, un-suffixed name).
      /// </summary>
      private static string CodeFilesDdl => "\nIF OBJECT_ID('dbo.CodeFiles','U') IS NULL" + CodeFilesTable( "" );

      /// <summary>
      /// Live CodeChunks table: create only when missing. Shares its body with the staging table via
      /// <see cref="CodeChunksTable"/> so the two definitions cannot drift and silently break the swap.
      /// </summary>
      private static string CodeChunksDdl => "\nIF OBJECT_ID('dbo.CodeChunks','U') IS NULL" + CodeChunksTable( "" );

      /// <summary>
      /// Supporting b-tree indexes on CodeChunks: the FK lookup column and the ChunkType filter column. Both
      /// are guarded with IF NOT EXISTS so this can run on every connect.
      /// </summary>
      private const string IndexesDdl = @"
IF NOT EXISTS (SELECT 1 FROM sys.indexes WHERE name='IX_CodeChunks_CodeFileId' AND object_id=OBJECT_ID('dbo.CodeChunks'))
   CREATE NONCLUSTERED INDEX IX_CodeChunks_CodeFileId ON dbo.CodeChunks (CodeFileId);
IF NOT EXISTS (SELECT 1 FROM sys.indexes WHERE name='IX_CodeChunks_ChunkType' AND object_id=OBJECT_ID('dbo.CodeChunks'))
   CREATE NONCLUSTERED INDEX IX_CodeChunks_ChunkType ON dbo.CodeChunks (ChunkType);";

      /// <summary>
      /// Full-text index over the file-level metadata columns (content plus every extracted code facet). This
      /// backs the file-FTS signal in the hybrid search. Keyed on the CodeFiles primary key.
      /// </summary>
      private const string CodeFilesFtsDdl = @"
CREATE FULLTEXT INDEX ON dbo.CodeFiles
(
   Content, ClassName, ClassNames, BaseClass, Namespace, Interfaces,
   PropertyNames, MethodNames, Properties, Constructors,
   OverriddenMethods, AbstractMethods, VirtualMethods, AsyncMethods,
   EnumValues, Attributes, Constants, GenericTypes, Usings,
   CommitMessages, WorkItemTitles, WorkItemTags, AllAuthors
)
KEY INDEX PK_CodeFiles ON CODEINDEX_FTC WITH (CHANGE_TRACKING AUTO);";

      /// <summary>
      /// Full-text index over the chunk-level columns. This backs the chunk-FTS signal in the hybrid search,
      /// which matches at method/class granularity rather than whole-file. Keyed on the CodeChunks primary key.
      /// </summary>
      private const string CodeChunksFtsDdl = @"
CREATE FULLTEXT INDEX ON dbo.CodeChunks
(
   ChunkContent, ChunkKey, ChunkName, ClassName, Namespace, Signature, ParentContext
)
KEY INDEX PK_CodeChunks ON CODEINDEX_FTC WITH (CHANGE_TRACKING AUTO);";

      /// <summary>
      /// The hybrid search stored procedure. It fuses three ranked signals (DiskANN vector similarity,
      /// chunk-level full-text, file-level full-text) with Reciprocal Rank Fusion in a single round-trip, so
      /// the client gets one already-scored result set. Declared CREATE OR ALTER so re-running is idempotent.
      /// </summary>
      private const string SearchCodeProcDdl = @"
CREATE OR ALTER PROCEDURE dbo.SearchCode
   @SearchText     NVARCHAR(4000),
   @QueryVector    VECTOR(1024),
   @TopN           INT = 20,
   @ChunkType      NVARCHAR(50) = NULL,
   @VectorWeight   INT = 60,
   @ChunkFtsWeight INT = 30,
   @FileFtsWeight  INT = 30,
   @MinFtsRank     INT = 10,
   @MaxDistance    FLOAT = 0.5
AS
BEGIN
   SET NOCOUNT ON;
   DECLARE @SafeText NVARCHAR(4000) = LEFT(
      REPLACE(REPLACE(REPLACE(REPLACE(REPLACE(REPLACE(
         @SearchText, '''', ' '), '""', ' '), '&', ' '), '|', ' '), '~', ' '), '!', ' '), 4000);

   DECLARE @VectorResults TABLE (ChunkId INT, Distance FLOAT, VectorRank INT);
   INSERT INTO @VectorResults (ChunkId, Distance, VectorRank)
   SELECT ranked.ChunkId, ranked.Distance,
          ROW_NUMBER() OVER (ORDER BY ranked.Distance ASC, ranked.ChunkId ASC)
   FROM (
      SELECT c.Id AS ChunkId, vs.distance AS Distance
      FROM VECTOR_SEARCH(TABLE = dbo.CodeChunks AS c, COLUMN = Embedding,
           SIMILAR_TO = @QueryVector, METRIC = 'cosine', TOP_N = 200) AS vs
      WHERE (@ChunkType IS NULL OR c.ChunkType = @ChunkType) AND vs.distance <= @MaxDistance
   ) ranked;

   DECLARE @ChunkFts TABLE (ChunkId INT, FtsRank INT, ChunkFtsRank INT);
   IF LEN(LTRIM(@SafeText)) > 0
      INSERT INTO @ChunkFts (ChunkId, FtsRank, ChunkFtsRank)
      SELECT ranked.ChunkId, ranked.FtsRank,
             ROW_NUMBER() OVER (ORDER BY ranked.FtsRank DESC, ranked.ChunkId ASC)
      FROM (
         SELECT ft.[KEY] AS ChunkId, ft.[RANK] AS FtsRank
         FROM FREETEXTTABLE(dbo.CodeChunks,
              (ChunkContent, ChunkName, ChunkKey, ClassName, [Namespace], Signature, ParentContext), @SafeText) ft
         INNER JOIN dbo.CodeChunks c ON ft.[KEY] = c.Id
         WHERE ft.[RANK] >= @MinFtsRank AND (@ChunkType IS NULL OR c.ChunkType = @ChunkType)
      ) ranked;

   DECLARE @FileFts TABLE (FileId INT, FtsRank INT, FileFtsRank INT);
   IF LEN(LTRIM(@SafeText)) > 0
      INSERT INTO @FileFts (FileId, FtsRank, FileFtsRank)
      SELECT ranked.FileId, ranked.FtsRank,
             ROW_NUMBER() OVER (ORDER BY ranked.FtsRank DESC, ranked.FileId ASC)
      FROM (
         SELECT ft.[KEY] AS FileId, ft.[RANK] AS FtsRank
         FROM FREETEXTTABLE(dbo.CodeFiles, *, @SafeText) ft
         WHERE ft.[RANK] >= @MinFtsRank
      ) ranked;

   ;WITH AllChunks AS (
      SELECT ChunkId FROM @VectorResults
      UNION SELECT ChunkId FROM @ChunkFts
      UNION SELECT c.Id FROM dbo.CodeChunks c INNER JOIN @FileFts ff ON c.CodeFileId = ff.FileId
   ),
   Scores AS (
      SELECT ac.ChunkId,
         CASE WHEN vr.ChunkId IS NOT NULL THEN @VectorWeight   * (1.0 / (60 + vr.VectorRank))   ELSE 0 END AS VectorRRF,
         CASE WHEN cf.ChunkId IS NOT NULL THEN @ChunkFtsWeight * (1.0 / (60 + cf.ChunkFtsRank)) ELSE 0 END AS ChunkFtsRRF,
         CASE WHEN ff.FileId  IS NOT NULL THEN @FileFtsWeight  * (1.0 / (60 + ff.FileFtsRank))  ELSE 0 END AS FileFtsRRF,
         vr.Distance
      FROM AllChunks ac
      LEFT JOIN @VectorResults vr ON ac.ChunkId = vr.ChunkId
      LEFT JOIN @ChunkFts cf      ON ac.ChunkId = cf.ChunkId
      LEFT JOIN dbo.CodeChunks cx ON ac.ChunkId = cx.Id
      LEFT JOIN @FileFts ff       ON cx.CodeFileId = ff.FileId
   )
   SELECT TOP (@TopN)
      s.ChunkId, c.CodeFileId AS FileId, f.FilePath, f.ClassName,
      c.ChunkType, c.ChunkName, c.ChunkContent, c.StartLine, c.EndLine,
      c.[Namespace] AS ChunkNamespace, c.Signature, c.ParentContext, f.Author, f.BaseClass,
      s.VectorRRF + s.ChunkFtsRRF + s.FileFtsRRF AS Score,
      s.VectorRRF, s.ChunkFtsRRF, s.FileFtsRRF, s.Distance,
      CASE
         WHEN s.VectorRRF > 0 AND (s.ChunkFtsRRF > 0 OR s.FileFtsRRF > 0) THEN 'Hybrid'
         WHEN s.VectorRRF > 0 THEN 'Vector'
         ELSE 'FullText'
      END AS MatchSource
   FROM Scores s
   INNER JOIN dbo.CodeChunks c ON s.ChunkId = c.Id
   INNER JOIN dbo.CodeFiles  f ON c.CodeFileId = f.Id
   ORDER BY (s.VectorRRF + s.ChunkFtsRRF + s.FileFtsRRF) DESC, s.ChunkId ASC;
END";

      /// <summary>
      /// Fresh, empty *_Staging tables built from the SAME table bodies as live (only the "_Staging" suffix
      /// differs), so any column added to live automatically appears in staging too. The full reindex writes
      /// here; <see cref="SwapStagingToLiveAsync"/> later renames the _Staging-named constraints/indexes to the
      /// live names. Any prior staging tables are dropped first so each rebuild starts clean.
      /// </summary>
      private static string StagingTablesDdl => $@"
IF OBJECT_ID('dbo.CodeChunks_Staging','U') IS NOT NULL DROP TABLE dbo.CodeChunks_Staging;
IF OBJECT_ID('dbo.CodeFiles_Staging','U')  IS NOT NULL DROP TABLE dbo.CodeFiles_Staging;
{CodeFilesTable( "_Staging" )}
{CodeChunksTable( "_Staging" )}
CREATE NONCLUSTERED INDEX IX_CodeChunks_Staging_CodeFileId ON dbo.CodeChunks_Staging (CodeFileId);
CREATE NONCLUSTERED INDEX IX_CodeChunks_Staging_ChunkType ON dbo.CodeChunks_Staging (ChunkType);";

      #endregion Data Members

      #region Public Methods

      /// <summary>
      /// Cheap reachability probe: try to open the connection and report success as a bool rather than throwing.
      /// Used by the UI to decide whether to fall back to a master-scoped connection and create the database.
      /// </summary>
      public static async Task<bool> TestConnectionAsync( string connectionString )
      {
         try
         {
            using var connection = new SqlConnection( connectionString );
            await connection.OpenAsync();
            return true;
         }
         catch
         {
            // Any failure (bad server, missing DB, auth) means "not usable"; the caller decides what to do next.
            return false;
         }
      }

      /// <summary>
      /// True if the named database exists. Pass a server/master-scoped connection string, since the target DB
      /// may not exist yet. DB_ID returns NULL for an unknown name, which we surface as false.
      /// </summary>
      public static async Task<bool> DatabaseExistsAsync( string serverConnectionString, string database )
      {
         using var connection = new SqlConnection( serverConnectionString );
         await connection.OpenAsync();
         using var command = new SqlCommand( "SELECT DB_ID(@db)", connection );
         command.Parameters.AddWithValue( "@db", database );
         var databaseId = await command.ExecuteScalarAsync();
         return databaseId != null && databaseId != DBNull.Value;
      }

      /// <summary>
      /// Create the database if it does not already exist. Pass a server/master-scoped connection string. The
      /// name is escaped two different ways because it appears both as an identifier and as a string literal.
      /// </summary>
      public static async Task CreateDatabaseAsync( string serverConnectionString, string database )
      {
         // Bracket-quote for the identifier position (double any closing bracket); single-quote-escape for the
         // DB_ID(N'...') literal position. The two escapings are not interchangeable.
         var quotedIdentifier = "[" + database.Replace( "]", "]]" ) + "]";
         var stringLiteral = database.Replace( "'", "''" );
         using var connection = new SqlConnection( serverConnectionString );
         await connection.OpenAsync();
         using var command = new SqlCommand( $"IF DB_ID(N'{stringLiteral}') IS NULL CREATE DATABASE {quotedIdentifier};", connection ) { CommandTimeout = 120 };
         await command.ExecuteNonQueryAsync();
      }

      /// <summary>
      /// Drop pooled connections so a stale handle isn't reused. Call this right after creating a database, so
      /// the next connection actually hits the new DB instead of a pooled connection bound to the old state.
      /// </summary>
      public static void ClearConnectionPools() => SqlConnection.ClearAllPools();

      /// <summary>
      /// Preflight the target database for a vector rebuild BEFORE the long-running index build, so we fail in
      /// seconds instead of after minutes of work. Confirms the VECTOR type works, that the compat level is
      /// high enough (on-prem), enables PREVIEW_FEATURES if needed, and smoke-tests a DiskANN index. Throws a
      /// clear exception on a hard failure; only warns (does not throw) when DiskANN itself is unavailable.
      /// </summary>
      public static async Task ValidateVectorCapabilitiesAsync( string connectionString )
      {
         using var connection = new SqlConnection( connectionString );
         await connection.OpenAsync();
         using var command = new SqlCommand { Connection = connection, CommandTimeout = 60 };

         bool isAzureSql = await DetectAzureSqlAsync( command );
         await RequireVectorTypeAsync( command, isAzureSql );
         await RequireCompatibilityLevelAsync( command, connection, isAzureSql );
         await EnablePreviewFeaturesBestEffortAsync( command, isAzureSql );
         await SmokeTestDiskAnnIndexAsync( command );
      }

      /// <summary>
      /// True if dbo.CodeFiles or dbo.CodeChunks already contains rows. Gates the destructive-wipe warnings in
      /// the UI: if there's existing content, the user has to confirm before a reset overwrites it.
      /// </summary>
      public static async Task<bool> HasContentAsync( string connectionString )
      {
         using var connection = new SqlConnection( connectionString );
         await connection.OpenAsync();
         using var command = new SqlCommand( @"
            SELECT
               CASE WHEN OBJECT_ID('dbo.CodeFiles','U')  IS NOT NULL THEN (SELECT COUNT(*) FROM dbo.CodeFiles)  ELSE 0 END +
               CASE WHEN OBJECT_ID('dbo.CodeChunks','U') IS NOT NULL THEN (SELECT COUNT(*) FROM dbo.CodeChunks) ELSE 0 END;", connection );
         var totalRowCount = await command.ExecuteScalarAsync();
         return totalRowCount != null && totalRowCount != DBNull.Value && Convert.ToInt64( totalRowCount ) > 0;
      }

      /// <summary>
      /// Idempotent create-if-missing of the whole schema (tables, b-tree indexes, full-text catalog + indexes,
      /// and the search proc). Safe to run on every connect; it never drops data. The DiskANN vector index is
      /// deliberately left out here, since it needs a populated table.
      /// </summary>
      public static async Task EnsureSchemaAsync( string connectionString )
      {
         using var connection = new SqlConnection( connectionString );
         await connection.OpenAsync();

         // On-prem SQL 2025 needs this for the (later) vector index; it's a no-op/unsupported on Azure, so swallow.
         await TryExecAsync( connection, "ALTER DATABASE SCOPED CONFIGURATION SET PREVIEW_FEATURES = ON;" );

         await ExecAsync( connection, CodeFilesDdl );
         await ExecAsync( connection, CodeChunksDdl );
         await ExecAsync( connection, IndexesDdl );

         // Full-text needs a default catalog before any full-text index can bind to it.
         if( !await ExistsAsync( connection, "SELECT 1 FROM sys.fulltext_catalogs WHERE name = 'CODEINDEX_FTC'" ) )
            await ExecAsync( connection, "CREATE FULLTEXT CATALOG CODEINDEX_FTC AS DEFAULT;" );

         if( !await ExistsAsync( connection, "SELECT 1 FROM sys.fulltext_indexes WHERE object_id = OBJECT_ID('dbo.CodeFiles')" ) )
            await ExecAsync( connection, CodeFilesFtsDdl );

         if( !await ExistsAsync( connection, "SELECT 1 FROM sys.fulltext_indexes WHERE object_id = OBJECT_ID('dbo.CodeChunks')" ) )
            await ExecAsync( connection, CodeChunksFtsDdl );

         // Hybrid RRF search proc (vector + chunk-FTS + file-FTS fusion). CREATE OR ALTER makes this idempotent.
         await TryExecAsync( connection, SearchCodeProcDdl );
      }

      /// <summary>
      /// DESTRUCTIVE: drop the vector index (if present) and delete all rows from both tables. This empties the
      /// index store without dropping the tables themselves. Confirm twice with the user before calling.
      /// </summary>
      public static async Task ResetAsync( string connectionString )
      {
         using var connection = new SqlConnection( connectionString );
         await connection.OpenAsync();
         await TryExecAsync( connection, "IF EXISTS (SELECT 1 FROM sys.indexes WHERE name = 'IX_CodeChunks_Embedding' AND object_id = OBJECT_ID('dbo.CodeChunks')) DROP INDEX IX_CodeChunks_Embedding ON dbo.CodeChunks;" );
         // Chunks first: the FK to CodeFiles cascades, but deleting the child side first keeps the intent explicit.
         await TryExecAsync( connection, "IF OBJECT_ID('dbo.CodeChunks','U') IS NOT NULL DELETE FROM dbo.CodeChunks;" );
         await TryExecAsync( connection, "IF OBJECT_ID('dbo.CodeFiles','U')  IS NOT NULL DELETE FROM dbo.CodeFiles;" );
      }

      /// <summary>
      /// Create fresh, empty *_Staging tables (dropping any prior staging first). The full reindex writes into
      /// these; the later swap promotes them to live. Keeping the rebuild off the live tables is what makes the
      /// reindex zero-downtime.
      /// </summary>
      public static async Task EnsureStagingTablesAsync( string connectionString )
      {
         using var connection = new SqlConnection( connectionString );
         await connection.OpenAsync();
         await ExecAsync( connection, StagingTablesDdl );
      }

      /// <summary>
      /// Row count of a table, or 0 if it doesn't exist. Used as the swap completion guard: the swap only runs
      /// once staging holds the expected number of rows, so a half-finished rebuild can't be promoted.
      /// </summary>
      public static async Task<long> RowCountAsync( string connectionString, string table )
      {
         using var connection = new SqlConnection( connectionString );
         await connection.OpenAsync();
         using var command = new SqlCommand( $"IF OBJECT_ID('dbo.{table}','U') IS NULL SELECT CAST(0 AS bigint) ELSE SELECT COUNT_BIG(*) FROM dbo.{table};", connection );
         var rowCount = await command.ExecuteScalarAsync();
         return rowCount == null || rowCount == DBNull.Value ? 0 : Convert.ToInt64( rowCount );
      }

      /// <summary>
      /// Zero-downtime promote: atomically swap the *_Staging tables into live via sp_rename. It drops the
      /// dependent objects (full-text indexes + search proc bound to the live object_ids), renames live to _Old
      /// and staging to live, drops _Old, renames the staging constraints/indexes onto the live names, then
      /// recreates the DiskANN vector index + full-text indexes + the SearchCode proc. Live tables stay
      /// queryable right up to the rename, and a build that crashes before this call never touches live.
      /// </summary>
      public static async Task SwapStagingToLiveAsync( string connectionString )
      {
         using var connection = new SqlConnection( connectionString );
         await connection.OpenAsync();

         await TryExecAsync( connection, "ALTER DATABASE SCOPED CONFIGURATION SET PREVIEW_FEATURES = ON;" );

         await DropLiveDependentObjectsAsync( connection );
         await RenameStagingIntoLiveAsync( connection );
         await RenameStagingConstraintsToLiveNamesAsync( connection );
         await RecreateLiveSearchObjectsAsync( connection );
      }

      #endregion Public Methods

      #region Private Methods

      /// <summary>
      /// Detect Azure SQL Database, which is evergreen and ALWAYS reports EngineEdition 5 plus a legacy
      /// "12.0.x" version string regardless of the features it actually supports. That makes a version-number
      /// gate meaningless there, so downstream checks branch on this flag instead.
      /// </summary>
      private static async Task<bool> DetectAzureSqlAsync( SqlCommand command )
      {
         command.CommandText = "SELECT SERVERPROPERTY('ProductVersion'), SERVERPROPERTY('Edition'), SERVERPROPERTY('EngineEdition')";
         int engineEdition = 0;
         using( var reader = await command.ExecuteReaderAsync() )
            if( await reader.ReadAsync() )
               engineEdition = Convert.ToInt32( reader.GetValue( 2 ) ?? 0 );
         return engineEdition == 5;   // 5 = Azure SQL Database
      }

      /// <summary>
      /// HARD requirement: prove the VECTOR type actually works by casting a literal, rather than trusting the
      /// version number. This is correct for both on-prem SQL 2025 and Azure SQL (which reports 12.0 but does
      /// support it). Re-reads the platform identity only to build a friendly error message on failure.
      /// </summary>
      private static async Task RequireVectorTypeAsync( SqlCommand command, bool isAzureSql )
      {
         try
         {
            command.CommandText = "DECLARE @v VECTOR(3) = CAST('[1,2,3]' AS VECTOR(3)); SELECT 1;";
            await command.ExecuteScalarAsync();
         }
         catch( SqlException exception )
         {
            string platformDescription = isAzureSql ? "This Azure SQL Database" : await DescribeServerAsync( command );
            throw new InvalidOperationException(
               $"{platformDescription} does not support the VECTOR type. Requires SQL Server 2025+ or an Azure SQL Database with vector support. SQL error: {exception.Message}", exception );
         }
      }

      /// <summary>
      /// Build a human-readable "SQL Server {version} ({edition})" string for error messages, reading the
      /// product version and edition back from the server. Best-effort; falls back to a generic label.
      /// </summary>
      private static async Task<string> DescribeServerAsync( SqlCommand command )
      {
         command.CommandText = "SELECT SERVERPROPERTY('ProductVersion'), SERVERPROPERTY('Edition')";
         string version = null, edition = null;
         using( var reader = await command.ExecuteReaderAsync() )
            if( await reader.ReadAsync() )
            {
               version = reader.GetValue( 0 )?.ToString();
               edition = reader.GetValue( 1 )?.ToString();
            }
         return $"SQL Server {version} ({edition})";
      }

      /// <summary>
      /// On-prem only: require database compatibility level 170+, which the VECTOR features need. Azure manages
      /// compat itself and the VECTOR probe already proved it works there, so this check is skipped on Azure.
      /// </summary>
      private static async Task RequireCompatibilityLevelAsync( SqlCommand command, SqlConnection connection, bool isAzureSql )
      {
         if( isAzureSql )
            return;

         command.CommandText = "SELECT compatibility_level FROM sys.databases WHERE name = DB_NAME()";
         int compatibilityLevel = Convert.ToInt32( await command.ExecuteScalarAsync() ?? 0 );
         if( compatibilityLevel < 170 )
            throw new InvalidOperationException( $"Database compatibility level {compatibilityLevel} — VECTOR features require 170+. Run: ALTER DATABASE [{connection.Database}] SET COMPATIBILITY_LEVEL = 170;" );
      }

      /// <summary>
      /// Turn on PREVIEW_FEATURES when it's off. On-prem SQL 2025 needs this for the DiskANN index; Azure SQL
      /// doesn't use this scoped config and setting it can error, so the whole thing is best-effort and swallows.
      /// </summary>
      private static async Task EnablePreviewFeaturesBestEffortAsync( SqlCommand command, bool isAzureSql )
      {
         try
         {
            command.CommandText = "SELECT CAST(value AS INT) FROM sys.database_scoped_configurations WHERE name = 'PREVIEW_FEATURES'";
            var previewFeaturesValue = await command.ExecuteScalarAsync();
            bool alreadyOn = previewFeaturesValue != null && Convert.ToInt32( previewFeaturesValue ) == 1;
            if( !alreadyOn )
            {
               command.CommandText = "ALTER DATABASE SCOPED CONFIGURATION SET PREVIEW_FEATURES = ON";
               await command.ExecuteNonQueryAsync();
            }
         }
         catch { /* not applicable on this platform */ }
      }

      /// <summary>
      /// Best-effort DiskANN smoke test on a throwaway table. The DiskANN vector index is preview/region-gated
      /// on Azure; if it's unavailable the indexer still works via exact VECTOR_DISTANCE search, so this WARNS
      /// rather than failing the run. Always tries to clean up the temp table even on failure.
      /// </summary>
      private static async Task SmokeTestDiskAnnIndexAsync( SqlCommand command )
      {
         const string smokeTable = "_VectorSmokeTest";
         command.CommandText = $@"
            SET QUOTED_IDENTIFIER ON;
            DROP TABLE IF EXISTS dbo.[{smokeTable}];
            CREATE TABLE dbo.[{smokeTable}] (Id INT IDENTITY(1,1) CONSTRAINT PK_{smokeTable} PRIMARY KEY, Embedding VECTOR(1024) NOT NULL);
            INSERT INTO dbo.[{smokeTable}] (Embedding)
               SELECT CAST('[' + STRING_AGG(CAST(0.0 AS VARCHAR(5)), ',') + ']' AS VECTOR(1024)) FROM (SELECT TOP 1024 1 AS n FROM sys.all_objects) x;
            CREATE VECTOR INDEX IX_{smokeTable}_Embedding ON dbo.[{smokeTable}](Embedding) WITH (metric='cosine', type='diskann');
            DROP TABLE dbo.[{smokeTable}];";
         try
         {
            await command.ExecuteNonQueryAsync();
            Console.WriteLine( "         Vector capability: OK (VECTOR type + DiskANN index)." );
         }
         catch( SqlException exception )
         {
            // The CREATE may have failed mid-batch, leaving the temp table behind; drop it so a retry is clean.
            try { command.CommandText = $"DROP TABLE IF EXISTS dbo.[{smokeTable}]"; await command.ExecuteNonQueryAsync(); } catch { }
            Console.WriteLine( $"[WARN] DiskANN vector index not available here (search will fall back to exact distance): {exception.Message}" );
         }
      }

      /// <summary>
      /// Swap step 1-2: drop the objects bound to the live table object_ids (both full-text indexes and the
      /// search proc), then drop the live DiskANN vector index. These must go before the tables can be renamed.
      /// </summary>
      private static async Task DropLiveDependentObjectsAsync( SqlConnection connection )
      {
         await ExecAsync( connection, @"
            IF EXISTS (SELECT 1 FROM sys.fulltext_indexes WHERE object_id = OBJECT_ID('dbo.CodeChunks')) DROP FULLTEXT INDEX ON dbo.CodeChunks;
            IF EXISTS (SELECT 1 FROM sys.fulltext_indexes WHERE object_id = OBJECT_ID('dbo.CodeFiles'))  DROP FULLTEXT INDEX ON dbo.CodeFiles;
            DROP PROCEDURE IF EXISTS dbo.SearchCode;" );

         await TryExecAsync( connection, "IF EXISTS (SELECT 1 FROM sys.indexes WHERE name='IX_CodeChunks_Embedding' AND object_id=OBJECT_ID('dbo.CodeChunks')) DROP INDEX IX_CodeChunks_Embedding ON dbo.CodeChunks;" );
      }

      /// <summary>
      /// Swap step 3-5: rename live to _Old, staging to live, then drop _Old. Dropping _Old frees the live
      /// constraint names so the staging constraints can be renamed onto them in the next step.
      /// </summary>
      private static async Task RenameStagingIntoLiveAsync( SqlConnection connection )
      {
         // live -> _Old
         await ExecAsync( connection, @"
            IF OBJECT_ID('dbo.CodeChunks','U') IS NOT NULL EXEC sp_rename 'dbo.CodeChunks', 'CodeChunks_Old';
            IF OBJECT_ID('dbo.CodeFiles','U')  IS NOT NULL EXEC sp_rename 'dbo.CodeFiles',  'CodeFiles_Old';" );

         // staging -> live
         await ExecAsync( connection, @"
            EXEC sp_rename 'dbo.CodeFiles_Staging',  'CodeFiles';
            EXEC sp_rename 'dbo.CodeChunks_Staging', 'CodeChunks';" );

         // drop _Old (frees the live constraint names so staging constraints can take them)
         await ExecAsync( connection, @"
            DROP TABLE IF EXISTS dbo.CodeChunks_Old;
            DROP TABLE IF EXISTS dbo.CodeFiles_Old;" );
      }

      /// <summary>
      /// Swap step 6: rename every staging-suffixed constraint, key, FK, and index onto its live name. Without
      /// this the promoted tables would keep their _Staging constraint names and drift from the live schema.
      /// </summary>
      private static async Task RenameStagingConstraintsToLiveNamesAsync( SqlConnection connection )
      {
         await ExecAsync( connection, @"
            IF EXISTS (SELECT 1 FROM sys.key_constraints WHERE name='PK_CodeFiles_Staging')  EXEC sp_rename 'dbo.PK_CodeFiles_Staging',  'PK_CodeFiles',  'OBJECT';
            IF EXISTS (SELECT 1 FROM sys.key_constraints WHERE name='PK_CodeChunks_Staging') EXEC sp_rename 'dbo.PK_CodeChunks_Staging', 'PK_CodeChunks', 'OBJECT';
            IF EXISTS (SELECT 1 FROM sys.foreign_keys WHERE name='FK_CodeChunks_Staging_CodeFiles') EXEC sp_rename 'dbo.FK_CodeChunks_Staging_CodeFiles', 'FK_CodeChunks_CodeFiles', 'OBJECT';
            IF EXISTS (SELECT 1 FROM sys.indexes WHERE name='UQ_CodeFiles_Staging_FilePath'  AND object_id=OBJECT_ID('dbo.CodeFiles'))  EXEC sp_rename 'dbo.CodeFiles.UQ_CodeFiles_Staging_FilePath',  'UQ_CodeFiles_FilePath',  'INDEX';
            IF EXISTS (SELECT 1 FROM sys.indexes WHERE name='UQ_CodeChunks_Staging_ChunkKey' AND object_id=OBJECT_ID('dbo.CodeChunks')) EXEC sp_rename 'dbo.CodeChunks.UQ_CodeChunks_Staging_ChunkKey', 'UQ_CodeChunks_ChunkKey', 'INDEX';
            IF EXISTS (SELECT 1 FROM sys.indexes WHERE name='IX_CodeChunks_Staging_CodeFileId' AND object_id=OBJECT_ID('dbo.CodeChunks')) EXEC sp_rename 'dbo.CodeChunks.IX_CodeChunks_Staging_CodeFileId', 'IX_CodeChunks_CodeFileId', 'INDEX';
            IF EXISTS (SELECT 1 FROM sys.indexes WHERE name='IX_CodeChunks_Staging_ChunkType'  AND object_id=OBJECT_ID('dbo.CodeChunks')) EXEC sp_rename 'dbo.CodeChunks.IX_CodeChunks_Staging_ChunkType',  'IX_CodeChunks_ChunkType',  'INDEX';
            IF EXISTS (SELECT 1 FROM sys.default_constraints WHERE name='DF_CodeFiles_Staging_ModifiedDate') EXEC sp_rename 'dbo.DF_CodeFiles_Staging_ModifiedDate', 'DF_CodeFiles_ModifiedDate', 'OBJECT';" );
      }

      /// <summary>
      /// Swap step 7-9: rebuild the search-serving objects on the freshly-promoted live tables. Recreates the
      /// DiskANN vector index (non-fatal if DiskANN is unavailable), ensures the full-text catalog + both
      /// full-text indexes, and re-creates the RRF search proc.
      /// </summary>
      private static async Task RecreateLiveSearchObjectsAsync( SqlConnection connection )
      {
         await TryExecAsync( connection, @"
            IF NOT EXISTS (SELECT 1 FROM sys.indexes WHERE name='IX_CodeChunks_Embedding' AND object_id=OBJECT_ID('dbo.CodeChunks'))
               CREATE VECTOR INDEX IX_CodeChunks_Embedding ON dbo.CodeChunks(Embedding) WITH (metric='cosine', type='diskann');" );

         if( !await ExistsAsync( connection, "SELECT 1 FROM sys.fulltext_catalogs WHERE name = 'CODEINDEX_FTC'" ) )
            await ExecAsync( connection, "CREATE FULLTEXT CATALOG CODEINDEX_FTC AS DEFAULT;" );
         await ExecAsync( connection, CodeFilesFtsDdl );
         await ExecAsync( connection, CodeChunksFtsDdl );
         await TryExecAsync( connection, SearchCodeProcDdl );
      }

      /// <summary>Run a DDL/DML batch as a non-query with a generous 120s timeout (index/table DDL can be slow).</summary>
      private static async Task ExecAsync( SqlConnection connection, string sql )
      {
         using var command = new SqlCommand( sql, connection ) { CommandTimeout = 120 };
         await command.ExecuteNonQueryAsync();
      }

      /// <summary>
      /// Run a batch but swallow any exception. Used for steps that are idempotent and best-effort (e.g. the
      /// preview-features toggle, or dropping an index that may not exist), where a failure is expected and fine.
      /// </summary>
      private static async Task TryExecAsync( SqlConnection connection, string sql )
      {
         try { await ExecAsync( connection, sql ); } catch { /* idempotent best-effort */ }
      }

      /// <summary>Return true when the given EXISTS-style probe query returns any non-null scalar (i.e. a row).</summary>
      private static async Task<bool> ExistsAsync( SqlConnection connection, string existsSql )
      {
         using var command = new SqlCommand( existsSql, connection );
         var probeResult = await command.ExecuteScalarAsync();
         return probeResult != null && probeResult != DBNull.Value;
      }

      /// <summary>
      /// The single CodeFiles table body, shared by both the live DDL and the _Staging DDL (suffix = "" for
      /// live, "_Staging" for the reindex staging table). Sharing one body means the two can never drift apart
      /// and silently break the staging swap. The suffix flows into the table name and every constraint name.
      /// </summary>
      private static string CodeFilesTable( string suffix ) => $@"
CREATE TABLE dbo.CodeFiles{suffix}
(
   -- Identity / keys
   Id                INT IDENTITY(1,1) NOT NULL,
   -- File identity
   FilePath          NVARCHAR(500)     NOT NULL,
   FileType          NVARCHAR(50)      NULL,
   -- VCS / blame metadata
   Author            NVARCHAR(100)     NULL,
   FileAddDate       NVARCHAR(MAX)     NULL,
   AllAuthors        NVARCHAR(1000)    NULL,
   CommitMessages    NVARCHAR(MAX)     NULL,
   WorkItemTitles    NVARCHAR(MAX)     NULL,
   WorkItemTags      NVARCHAR(MAX)     NULL,
   -- Content
   Content           NVARCHAR(MAX)     NULL,
   -- Extracted code metadata
   Namespace         NVARCHAR(500)     NULL,
   ClassName         NVARCHAR(255)     NULL,
   ClassNames        NVARCHAR(4000)    NULL,
   ClassModifiers    NVARCHAR(100)     NULL,
   BaseClass         NVARCHAR(500)     NULL,
   Interfaces        NVARCHAR(500)     NULL,
   PropertyNames     NVARCHAR(MAX)     NULL,
   Properties        NVARCHAR(MAX)     NULL,
   MethodNames       NVARCHAR(MAX)     NULL,
   Constructors      NVARCHAR(MAX)     NULL,
   OverriddenMethods NVARCHAR(2000)    NULL,
   AbstractMethods   NVARCHAR(2000)    NULL,
   VirtualMethods    NVARCHAR(2000)    NULL,
   AsyncMethods      NVARCHAR(MAX)     NULL,
   EnumValues        NVARCHAR(MAX)     NULL,
   Constants         NVARCHAR(4000)    NULL,
   StaticFields      NVARCHAR(2000)    NULL,
   Attributes        NVARCHAR(2000)    NULL,
   Events            NVARCHAR(2000)    NULL,
   Delegates         NVARCHAR(1000)    NULL,
   GenericTypes      NVARCHAR(MAX)     NULL,
   ReferencedTypes   NVARCHAR(MAX)     NULL,
   Dictionaries      NVARCHAR(2000)    NULL,
   SqlOperations     NVARCHAR(100)     NULL,
   Usings            NVARCHAR(4000)    NULL,
   Regions           NVARCHAR(2000)    NULL,
   -- Audit
   ModifiedDate      DATETIME2(7)      NOT NULL CONSTRAINT DF_CodeFiles{suffix}_ModifiedDate DEFAULT (GETDATE()),
   CONSTRAINT PK_CodeFiles{suffix} PRIMARY KEY CLUSTERED (Id),
   CONSTRAINT UQ_CodeFiles{suffix}_FilePath UNIQUE (FilePath)
);";

      /// <summary>
      /// The single CodeChunks table body, shared by live + _Staging just like <see cref="CodeFilesTable"/>.
      /// The suffix drives the table name, every constraint name, AND the FK's referenced CodeFiles table (so
      /// the staging chunks reference CodeFiles_Staging, not live).
      /// </summary>
      private static string CodeChunksTable( string suffix ) => $@"
CREATE TABLE dbo.CodeChunks{suffix}
(
   Id            INT IDENTITY(1,1) NOT NULL,
   CodeFileId    INT               NOT NULL,
   ChunkKey      NVARCHAR(500)     NOT NULL,
   ChunkType     NVARCHAR(50)      NOT NULL,
   ChunkName     NVARCHAR(200)     NOT NULL,
   StartLine     INT               NOT NULL,
   EndLine       INT               NOT NULL,
   ChunkContent  NVARCHAR(MAX)     NOT NULL,
   Embedding     VECTOR(1024)      NULL,
   Namespace     NVARCHAR(500)     NULL,
   ClassName     NVARCHAR(200)     NULL,
   Signature     NVARCHAR(MAX)     NULL,
   ParentContext NVARCHAR(MAX)     NULL,
   CONSTRAINT PK_CodeChunks{suffix} PRIMARY KEY CLUSTERED (Id),
   CONSTRAINT FK_CodeChunks{suffix}_CodeFiles FOREIGN KEY (CodeFileId) REFERENCES dbo.CodeFiles{suffix}(Id) ON DELETE CASCADE,
   CONSTRAINT UQ_CodeChunks{suffix}_ChunkKey UNIQUE (ChunkKey)
);";

      #endregion Private Methods
   }
}
