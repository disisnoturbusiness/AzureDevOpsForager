using AzureDevOpsForager.Core;
using Newtonsoft.Json;
using Xunit;

namespace AzureDevOpsForager.Tests;

/// <summary>
/// Covers <see cref="Config.LoadFromFile"/> — the JSON-to-static-config loader.
/// Every value in the file is a string; the loader is responsible for parsing the
/// int/bool/double-typed statics (Port, RerankerEnabled, MaxVectorDistance) and applying
/// the string-typed ones verbatim.
///
/// IMPORTANT: <see cref="Config"/> is a static class with mutable statics. All Config tests
/// live in this single class (xUnit serializes test methods within a class, so they never
/// run concurrently), and every static this class mutates is snapshotted and restored in a
/// finally block so no state leaks into other tests.
/// </summary>
public class ConfigTests
{
   [Fact]
   public void LoadFromFile_ParsesTypedValuesAndAppliesStrings()
   {
      // Snapshot every static we touch so we can restore it afterward.
      var origSqlConn = Config.SqlConnectionString;
      var origPort = Config.Port;
      var origReranker = Config.RerankerEnabled;
      var origMaxDist = Config.MaxVectorDistance;
      var origServerUrl = Config.ServerUrl;

      var configPath = Path.Combine( Path.GetTempPath(), $"adf_config_test_{Guid.NewGuid():N}.json" );

      try
      {
         // Everything on disk is a string — this is exactly the shape config.sample.json ships.
         var values = new Dictionary<string, string>
         {
            ["SqlConnectionString"] = "Server=.;Database=Forager;Trusted_Connection=True;",
            ["Port"] = "9001",
            ["RerankerEnabled"] = "false",
            ["MaxVectorDistance"] = "0.42",
            ["ServerUrl"] = "http://demo.example.com:8000"
         };
         File.WriteAllText( configPath, JsonConvert.SerializeObject( values ) );

         Config.LoadFromFile( configPath );

         // String-typed statics applied verbatim.
         Assert.Equal( "Server=.;Database=Forager;Trusted_Connection=True;", Config.SqlConnectionString );
         Assert.Equal( "http://demo.example.com:8000", Config.ServerUrl );

         // Typed statics parsed from their string forms.
         Assert.Equal( 9001, Config.Port );
         Assert.False( Config.RerankerEnabled );
         Assert.Equal( 0.42d, Config.MaxVectorDistance, precision: 10 );
      }
      finally
      {
         Config.SqlConnectionString = origSqlConn;
         Config.Port = origPort;
         Config.RerankerEnabled = origReranker;
         Config.MaxVectorDistance = origMaxDist;
         Config.ServerUrl = origServerUrl;

         if ( File.Exists( configPath ) )
            File.Delete( configPath );
      }
   }

   [Fact]
   public void LoadFromFile_MissingFile_LeavesStaticsUntouched()
   {
      var origPort = Config.Port;
      var missingPath = Path.Combine( Path.GetTempPath(), $"adf_config_missing_{Guid.NewGuid():N}.json" );

      // The loader no-ops when the file doesn't exist — the static keeps its prior value.
      Config.LoadFromFile( missingPath );

      Assert.Equal( origPort, Config.Port );
   }
}
