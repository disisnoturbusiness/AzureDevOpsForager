using System;
using System.Collections.Generic;
using Microsoft.Data.SqlClient;

namespace AzureDevOpsForager.Core.Services.Utilities
{
   /// <summary>
   /// Factory for <see cref="SqlConnection"/>s that automatically retry through transient faults — most
   /// importantly the serverless "database is resuming" error (40613) that a PAUSED Azure SQL database throws
   /// while it wakes up. Without this, hitting a paused serverless DB surfaces as a hung request / HTTP 504;
   /// with it, the connection is retried (exponential backoff, up to ~2 minutes) until the database is online.
   /// A safe no-op for an always-on SQL Server 2025 / local instance — nothing transient ever fires.
   /// </summary>
   public static class SqlResilience
   {
      private static readonly object _lock = new object();
      private static SqlRetryLogicBaseProvider _provider;

      /// <summary>
      /// The shared retry provider: exponential backoff (3s..15s) up to 12 tries (~2 min total), triggered by
      /// the serverless-resume error plus the usual Azure SQL transient/throttling faults. Each retry logs a
      /// line so operators can see the wait; clients surface their own "waking up" notice to the user.
      /// </summary>
      private static SqlRetryLogicBaseProvider Provider()
      {
         if( _provider != null ) return _provider;
         lock( _lock )
         {
            if( _provider == null )
            {
               var options = new SqlRetryLogicOption
               {
                  NumberOfTries = 12,
                  DeltaTime = TimeSpan.FromSeconds( 3 ),
                  MaxTimeInterval = TimeSpan.FromSeconds( 15 ),
                  // 40613 = serverless "database not currently available / resuming"; the rest are the usual
                  // Azure SQL transient + throttling + connection faults, plus client timeout (-2).
                  TransientErrors = new List<int>
                  {
                     40613, 40197, 40501, 49918, 49919, 49920, 4060, 4221, 40143, 40166,
                     233, 10928, 10929, 10053, 10054, 10060, 258, 64, 20, -2
                  }
               };

               var provider = SqlConfigurableRetryFactory.CreateExponentialRetryProvider( options );
               provider.Retrying += ( sender, args ) =>
                  Console.WriteLine( $"[SQL] Database not ready (attempt {args.RetryCount}) — waiting for the serverless DB to resume..." );
               _provider = provider;
            }
         }
         return _provider;
      }

      /// <summary>
      /// Creates a <see cref="SqlConnection"/> whose Open/OpenAsync (and command execution) retries through the
      /// serverless resume and transient faults. Drop-in replacement for <c>new SqlConnection(connString)</c>.
      /// </summary>
      public static SqlConnection CreateConnection( string connectionString )
      {
         return new SqlConnection( connectionString ) { RetryLogicProvider = Provider() };
      }
   }
}
