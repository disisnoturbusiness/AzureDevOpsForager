using System;
using System.Threading.Tasks;
using Microsoft.Data.SqlClient;

namespace AzureDevOpsForager.Core.Services.Search;

/// <summary>
/// Read side of the vector-search stack backed by SQL Server 2025's native vector engine.
/// The code corpus is embedded with the configured model (bge-code-v1 at 1536 dimensions on the
/// hosted path; local e5-large-v2 at 1024) and stored in a VECTOR(n) column sized by
/// Config.EmbeddingDimension, so similarity ranking runs inside the database via VECTOR_SEARCH
/// (DiskANN index, TOP_N) using cosine distance rather than being pulled back to the client.
/// This class holds the connection details for that store and exposes lightweight health/stats
/// lookups against it. Implements IDisposable so callers can use it inside a using block, even
/// though the current surface keeps no unmanaged handles open between calls.
/// </summary>
public class SqlVectorService : IDisposable
{
   #region Data Members

   /// <summary>
   /// Connection string for the SQL Server instance that hosts the embedded code corpus.
   /// Captured once at construction; every query opens its own short-lived connection from it
   /// so the service stays safe to share without holding a connection open across calls.
   /// </summary>
   private readonly string _connectionString;

   /// <summary>
   /// Guards against running the dispose logic more than once when a caller (or a using block)
   /// invokes Dispose repeatedly.
   /// </summary>
   private bool _disposed;

   #endregion Data Members

   #region Constructor

   /// <summary>
   /// Creates the service against a specific SQL connection string, or falls back to the
   /// application-wide configured one when none is supplied. The fallback lets most callers
   /// construct the service with no arguments while tests can point it at another database.
   /// </summary>
   /// <param name="connectionString">
   /// Explicit connection string to use; when null, the shared Config.SqlConnectionString is used.
   /// </param>
   public SqlVectorService( string connectionString = null )
   {
      _connectionString = connectionString ?? Config.SqlConnectionString;
   }

   #endregion Constructor

   #region Public Methods

   /// <summary>
   /// Reports health for the code-chunk collection: how many chunks actually carry an embedding, a
   /// status string, and a human-readable detail line. Callers use this as the "can this thing serve
   /// a semantic search?" probe.
   /// <para>
   /// This deliberately verifies more than a row count. An earlier version ran
   /// <c>SELECT COUNT(*) FROM dbo.CodeChunks</c> and returned a hard-coded "Green", which meant the
   /// probe reported a healthy, fully populated vector store even when the Embedding column was
   /// entirely NULL or the DiskANN index did not exist — the two conditions most likely to make
   /// VECTOR_SEARCH silently return nothing. A health check that cannot fail is not a health check,
   /// and it actively misleads during exactly the outage it exists to detect.
   /// </para>
   /// <para>
   /// PointCount is therefore the count of rows with a non-NULL Embedding — the number of vectors
   /// that can actually be searched — not the raw row count.
   /// </para>
   /// Any failure (connection, query) is surfaced as a zero count with an "Error" status rather than
   /// throwing, so a health check never takes down the caller.
   /// </summary>
   /// <returns>
   /// A tuple of the searchable vector count, a status string ("Green" fully healthy, "Yellow"
   /// degraded but partly usable, "Red" cannot serve vector search, "Error" the probe itself failed),
   /// and a detail string explaining a non-Green status.
   /// </returns>
   public async Task<(long PointCount, string Status, string Detail)> GetCollectionInfoAsync()
   {
      try
      {
         using var connection = AzureDevOpsForager.Core.Services.Utilities.SqlResilience.CreateConnection(_connectionString );
         await connection.OpenAsync();

         // Total rows vs rows that actually carry a vector. COUNT_BIG(Embedding) skips NULLs, so a
         // corpus that was chunked but never embedded shows up as a gap between the two.
         long totalRows;
         long embeddedRows;
         using( var command = new SqlCommand(
            "SELECT COUNT_BIG(*), COUNT_BIG(Embedding) FROM dbo.CodeChunks", connection ) )
         using( var reader = await command.ExecuteReaderAsync() )
         {
            if( !await reader.ReadAsync() )
               return (0, "Red", "dbo.CodeChunks returned no count row." );

            totalRows = reader.GetInt64( 0 );
            embeddedRows = reader.GetInt64( 1 );
         }

         // Probe the DiskANN index separately: sys.vector_indexes does not exist on every engine
         // version, and a missing catalog view must not take down the whole health check.
         var hasVectorIndex = false;
         string indexVersion = null;
         try
         {
            using var indexCommand = new SqlCommand(
               "SELECT TOP (1) JSON_VALUE(build_parameters, '$.Version') " +
               "FROM sys.vector_indexes WHERE object_id = OBJECT_ID('dbo.CodeChunks')", connection );
            var indexResult = await indexCommand.ExecuteScalarAsync();
            if( indexResult != null )
            {
               hasVectorIndex = true;
               indexVersion = indexResult == DBNull.Value ? "unknown" : indexResult.ToString();
            }
         }
         catch( Exception indexException )
         {
            Logger.Warn( $"Could not read sys.vector_indexes: {indexException.Message}", "SqlVector" );
         }

         if( totalRows == 0 )
            return (0, "Red", "dbo.CodeChunks is empty — nothing has been indexed." );

         if( embeddedRows == 0 )
            return (0, "Red", $"{totalRows} chunks are stored but none have an embedding — run the indexer." );

         if( embeddedRows < totalRows )
            return (embeddedRows, "Yellow",
               $"{embeddedRows} of {totalRows} chunks have an embedding; {totalRows - embeddedRows} are missing one." );

         if( !hasVectorIndex )
            return (embeddedRows, "Yellow",
               $"{embeddedRows} chunks are embedded but no vector index exists on dbo.CodeChunks — VECTOR_SEARCH will fall back to an exact scan or fail." );

         return (embeddedRows, "Green", $"{embeddedRows} vectors, DiskANN index version {indexVersion}." );
      }
      catch( Exception exception )
      {
         Logger.Error( "Error getting vector store stats", "SqlVector", exception );
         return (0, "Error", exception.Message);
      }
   }

   /// <summary>
   /// Implements IDisposable. The service does not currently keep long-lived resources open
   /// (each query owns its connection), so this only flips the disposed flag once and is safe to
   /// call more than once. It exists to honor the using-block contract and to give a single place
   /// to release resources should any be added later.
   /// </summary>
   public void Dispose()
   {
      if( _disposed ) return;
      _disposed = true;
   }

   #endregion Public Methods
}
