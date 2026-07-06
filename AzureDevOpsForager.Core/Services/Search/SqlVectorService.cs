using System;
using System.Threading.Tasks;
using Microsoft.Data.SqlClient;

namespace AzureDevOpsForager.Core.Services.Search;

/// <summary>
/// Read side of the vector-search stack backed by SQL Server 2025's native vector engine.
/// The code corpus is embedded with the E5-large-v2 model (1024 dimensions) and stored in a
/// VECTOR(1024) column, so similarity ranking runs inside the database via VECTOR_SEARCH
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
   /// Reports basic health for the code-chunk collection: how many chunks are currently stored
   /// and a coarse status string. Callers use this as a cheap "is the vector store populated and
   /// reachable?" probe before running searches. Any failure (connection, query) is swallowed and
   /// surfaced as a zero count with an "Error" status rather than throwing, so a health check never
   /// takes down the caller.
   /// </summary>
   /// <returns>
   /// A tuple of the row count in dbo.CodeChunks and a status string: "Green" on success,
   /// "Error" when the lookup failed.
   /// </returns>
   public async Task<(long PointCount, string Status)> GetCollectionInfoAsync()
   {
      try
      {
         using var connection = AzureDevOpsForager.Core.Services.Utilities.SqlResilience.CreateConnection(_connectionString );
         await connection.OpenAsync();

         // Count the stored chunks; the presence of rows is the signal the corpus is loaded.
         using var command = new SqlCommand( "SELECT COUNT(*) FROM dbo.CodeChunks", connection );
         var count = (int)await command.ExecuteScalarAsync();

         return (count, "Green");
      }
      catch( Exception exception )
      {
         Console.WriteLine( $"[SQL VECTOR] Error getting stats: {exception.Message}" );
         return (0, "Error");
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
