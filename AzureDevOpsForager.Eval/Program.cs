using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Threading.Tasks;
using AzureDevOpsForager.Core;
using AzureDevOpsForager.Eval.Models;
using AzureDevOpsForager.Eval.Services;
using Newtonsoft.Json;

namespace AzureDevOpsForager.Eval;

/// <summary>
/// Command-line entry point for the retrieval quality harness.
///
/// The harness answers one question the search code cannot answer about itself: did that change to
/// the weights, the distance ceiling, the reranker, or the embedding model actually make retrieval
/// better, and what did it cost in latency. It runs a hand-labelled golden set against every
/// configuration in a sweep and writes the comparison out as CSV, JSON and Markdown.
/// </summary>
public static class Program
{
   #region Data Members

   /// <summary>Default cutoff used for every @K metric when the caller does not choose one.</summary>
   private const int DefaultTopK = 10;

   /// <summary>Exit code returned when arguments or input files are unusable.</summary>
   private const int ExitFailure = 1;

   /// <summary>Exit code returned on a completed run.</summary>
   private const int ExitSuccess = 0;

   #endregion Data Members

   #region Public Methods

   /// <summary>
   /// Parses arguments, loads configuration and the golden set, runs the sweep, and writes reports.
   /// Argument and input errors are reported as messages and a non-zero exit code rather than as
   /// stack traces, since this is run from a prompt far more often than from a debugger.
   /// </summary>
   /// <param name="args">Command-line arguments; see <see cref="PrintUsage"/> for the accepted flags.</param>
   /// <returns>Zero on a completed run, non-zero when the run could not start.</returns>
   public static async Task<int> Main( string[] args )
   {
      var options = ParseArguments( args );
      if( options == null )
      {
         PrintUsage();
         return ExitFailure;
      }

      LoadForagerConfiguration( options.ConfigPath );

      List<GoldenQuery> queries;
      SweepSpec spec;
      try
      {
         queries = new GoldenSetLoader().Load( options.GoldenPath );
         spec = LoadSweepSpec( options.SweepPath );
      }
      catch( Exception exception )
      {
         Console.Error.WriteLine( exception.Message );
         return ExitFailure;
      }

      var configurations = new SweepPlanner().Plan( spec );
      Console.WriteLine( $"Golden queries: {queries.Count}   Configurations: {configurations.Count}   K: {options.TopK}" );

      var summaries = await RunSweepAsync( configurations, queries, options.TopK );

      var written = new ReportWriter().WriteAll( options.OutputDirectory, summaries, options.BaselineName );
      Console.WriteLine();
      foreach( var path in written )
         Console.WriteLine( $"Wrote {path}" );

      return ExitSuccess;
   }

   #endregion Public Methods

   #region Private Methods

   /// <summary>
   /// Runs every configuration in order, reporting progress as it goes because a full sweep is a
   /// minutes-to-hours job and a silent console is indistinguishable from a hang.
   /// </summary>
   /// <param name="configurations">The configurations to measure.</param>
   /// <param name="queries">The golden set.</param>
   /// <param name="topK">The cutoff for the @K metrics.</param>
   /// <returns>One scorecard per configuration, in run order.</returns>
   private static async Task<List<RunSummary>> RunSweepAsync(
      List<RunConfiguration> configurations, List<GoldenQuery> queries, int topK )
   {
      var runner = new EvalRunner();
      var summaries = new List<RunSummary>();
      var stopwatch = Stopwatch.StartNew();

      for( int i = 0; i < configurations.Count; i++ )
      {
         Console.Write( $"[{i + 1}/{configurations.Count}] {configurations[i].Name} ... " );

         var summary = await runner.RunAsync( configurations[i], queries, topK );
         summaries.Add( summary );

         if( summary.QueriesRun == 0 )
            Console.WriteLine( "skipped" );
         else
            Console.WriteLine(
               $"nDCG {summary.MeanNdcgAtK:0.000}  recall {summary.MeanRecallAtK:0.000}  " +
               $"p95 {summary.LatencyP95Milliseconds:0} ms" +
               ( summary.QueriesFailed > 0 ? $"  ({summary.QueriesFailed} failed)" : "" ) );
      }

      stopwatch.Stop();
      Console.WriteLine( $"Sweep finished in {stopwatch.Elapsed.TotalMinutes:0.0} minutes." );

      return summaries;
   }

   /// <summary>
   /// Loads the forager's own configuration so the harness reads the same connection string, model
   /// paths and endpoints the server would. Failures are tolerated because a machine may legitimately
   /// carry only environment-variable configuration.
   /// </summary>
   /// <param name="configPath">Explicit config.json path, or null to use the one beside the executable.</param>
   private static void LoadForagerConfiguration( string configPath )
   {
      var resolved = string.IsNullOrWhiteSpace( configPath )
         ? Path.Combine( AppContext.BaseDirectory, "config.json" )
         : configPath;

      try { Config.LoadFromFile( resolved ); } catch { }
      try { Config.LoadUserOverrides(); } catch { }
      try { Config.EnsureDirectories(); } catch { }
   }

   /// <summary>
   /// Loads the sweep specification, treating an absent path as "measure the current defaults only".
   /// </summary>
   /// <param name="sweepPath">Path to the sweep JSON, or null.</param>
   /// <returns>The parsed specification, or an empty one.</returns>
   /// <exception cref="InvalidDataException">The file exists but is not valid sweep JSON.</exception>
   private static SweepSpec LoadSweepSpec( string sweepPath )
   {
      if( string.IsNullOrWhiteSpace( sweepPath ) )
         return new SweepSpec();

      if( !File.Exists( sweepPath ) )
         throw new FileNotFoundException( $"Sweep spec not found: {sweepPath}" );

      try
      {
         return JsonConvert.DeserializeObject<SweepSpec>( File.ReadAllText( sweepPath ) ) ?? new SweepSpec();
      }
      catch( JsonException exception )
      {
         throw new InvalidDataException( $"Sweep spec is not valid JSON: {exception.Message}", exception );
      }
   }

   /// <summary>
   /// Parses the command-line flags into an options object, returning null when a required flag is
   /// missing or a value will not parse.
   /// </summary>
   /// <param name="args">The raw arguments.</param>
   /// <returns>The parsed options, or null when the arguments are unusable.</returns>
   private static EvalOptions ParseArguments( string[] args )
   {
      var options = new EvalOptions { TopK = DefaultTopK };

      for( int i = 0; i < args.Length; i++ )
      {
         var flag = args[i];
         var value = i + 1 < args.Length ? args[i + 1] : null;

         switch( flag )
         {
            case "--golden": options.GoldenPath = value; i++; break;
            case "--sweep": options.SweepPath = value; i++; break;
            case "--out": options.OutputDirectory = value; i++; break;
            case "--baseline": options.BaselineName = value; i++; break;
            case "--config": options.ConfigPath = value; i++; break;
            case "--top-k":
               if( !int.TryParse( value, out var topK ) || topK <= 0 )
                  return null;
               options.TopK = topK;
               i++;
               break;
            default:
               Console.Error.WriteLine( $"Unrecognized argument: {flag}" );
               return null;
         }
      }

      if( string.IsNullOrWhiteSpace( options.GoldenPath ) )
         return null;

      if( string.IsNullOrWhiteSpace( options.OutputDirectory ) )
         options.OutputDirectory = Path.Combine( AppContext.BaseDirectory, "eval-output" );

      return options;
   }

   /// <summary>
   /// Prints the accepted flags with a worked example, shown whenever argument parsing fails.
   /// </summary>
   private static void PrintUsage()
   {
      Console.WriteLine( "AzureDevOpsForager.Eval - retrieval quality harness" );
      Console.WriteLine();
      Console.WriteLine( "  --golden <path>    Golden query set JSON. Required." );
      Console.WriteLine( "  --sweep <path>     Sweep spec JSON. Omit to measure current settings only." );
      Console.WriteLine( "  --out <dir>        Output directory. Defaults to ./eval-output." );
      Console.WriteLine( "  --top-k <n>        Cutoff K for the @K metrics. Defaults to " + DefaultTopK + "." );
      Console.WriteLine( "  --baseline <name>  Config name to measure deltas against. Defaults to the first run." );
      Console.WriteLine( "  --config <path>    Forager config.json. Defaults to the one beside this exe." );
      Console.WriteLine();
      Console.WriteLine( "Example:" );
      Console.WriteLine( "  AzureDevOpsForager.Eval --golden golden-queries.json --sweep sweep.json --out .\\eval-output" );
   }

   #endregion Private Methods
}

/// <summary>
/// The parsed command-line options for one harness run. A plain carrier so that argument parsing
/// stays separable from the run itself.
/// </summary>
public class EvalOptions
{
   #region Data Members

   /// <summary>Path to the golden query set JSON.</summary>
   public string GoldenPath { get; set; }

   /// <summary>Path to the sweep specification JSON, or null to measure current settings only.</summary>
   public string SweepPath { get; set; }

   /// <summary>Directory the report files are written into.</summary>
   public string OutputDirectory { get; set; }

   /// <summary>Name of the configuration deltas are measured against, or null for the first run.</summary>
   public string BaselineName { get; set; }

   /// <summary>Path to the forager's config.json, or null to use the one beside the executable.</summary>
   public string ConfigPath { get; set; }

   /// <summary>Cutoff K applied to every @K metric.</summary>
   public int TopK { get; set; }

   #endregion Data Members
}
