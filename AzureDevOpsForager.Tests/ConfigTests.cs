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

   /// <summary>
   /// Writes a one-key config, loads it, and returns the resulting MaxVectorDistance, restoring the
   /// static afterwards so nothing leaks between assertions.
   /// </summary>
   private static double LoadMaxVectorDistance( string raw, double startingValue )
   {
      var orig = Config.MaxVectorDistance;
      var path = Path.Combine( Path.GetTempPath(), $"adf_mvd_{Guid.NewGuid():N}.json" );
      try
      {
         Config.MaxVectorDistance = startingValue;
         File.WriteAllText( path, JsonConvert.SerializeObject( new Dictionary<string, string> { ["MaxVectorDistance"] = raw } ) );
         Config.LoadFromFile( path );
         return Config.MaxVectorDistance;
      }
      finally
      {
         Config.MaxVectorDistance = orig;
         if ( File.Exists( path ) ) File.Delete( path );
      }
   }

   private static double LoadMinRerankScore( string raw, double startingValue )
   {
      var orig = Config.MinRerankScore;
      var path = Path.Combine( Path.GetTempPath(), $"adf_mrs_{Guid.NewGuid():N}.json" );
      try
      {
         Config.MinRerankScore = startingValue;
         File.WriteAllText( path, JsonConvert.SerializeObject( new Dictionary<string, string> { ["MinRerankScore"] = raw } ) );
         Config.LoadFromFile( path );
         return Config.MinRerankScore;
      }
      finally
      {
         Config.MinRerankScore = orig;
         if ( File.Exists( path ) ) File.Delete( path );
      }
   }

   [Theory]
   [InlineData( "0" )]        // admits only vectors identical to the query — empties the vector leg
   [InlineData( "-0.5" )]     // meaningless for a distance
   [InlineData( "2.5" )]      // above the cosine-distance range
   public void LoadFromFile_MaxVectorDistanceOutOfRange_IsRejectedAndPriorValueKept( string raw )
   {
      // Cosine distance is bounded to [0, 2] and a zero ceiling silently discards every candidate with
      // no error — precisely the failure that made search degrade to full-text unnoticed. A bad value
      // must not be accepted just because it parses.
      Assert.Equal( 1.0d, LoadMaxVectorDistance( raw, startingValue: 1.0 ), precision: 10 );
   }

   [Theory]
   [InlineData( "1.0" )]
   [InlineData( "0.75" )]
   [InlineData( "2" )]        // the top of the range is legal
   public void LoadFromFile_MaxVectorDistanceInRange_IsApplied( string raw )
   {
      var expected = double.Parse( raw, System.Globalization.CultureInfo.InvariantCulture );
      Assert.Equal( expected, LoadMaxVectorDistance( raw, startingValue: 1.0 ), precision: 10 );
    }

   [Theory]
   [InlineData( "-0.1" )]
   [InlineData( "1.5" )]
   public void LoadFromFile_MinRerankScoreOutOfRange_IsRejectedAndPriorValueKept( string raw )
   {
      Assert.Equal( 0.001d, LoadMinRerankScore( raw, startingValue: 0.001 ), precision: 10 );
   }

   [Theory]
   [InlineData( "0" )]        // legal: disables the gate entirely
   [InlineData( "0.05" )]
   [InlineData( "1" )]
   public void LoadFromFile_MinRerankScoreInRange_IsApplied( string raw )
   {
      var expected = double.Parse( raw, System.Globalization.CultureInfo.InvariantCulture );
      Assert.Equal( expected, LoadMinRerankScore( raw, startingValue: 0.001 ), precision: 10 );
   }

   [Fact]
   public void Defaults_AreTheMeasuredValues()
   {
      // Guards the two constants this project got wrong once already. MaxVectorDistance must sit clear
      // of the observed 0.67-0.90 distance band, and MinRerankScore inside the gap between off-topic
      // (<=0.0013) and on-topic (>=0.14) rerank scores. A future edit that reverts either should fail
      // here rather than silently degrading search quality.
      var freshMax = LoadMaxVectorDistance( "not-a-number", startingValue: Config.MaxVectorDistance );
      Assert.True( freshMax >= 0.95, $"MaxVectorDistance default {freshMax} is inside the measured distance band" );
      Assert.True( Config.MinRerankScore > 0 && Config.MinRerankScore < 0.14,
         $"MinRerankScore default {Config.MinRerankScore} is outside the off-topic/on-topic gap" );
   }
}
