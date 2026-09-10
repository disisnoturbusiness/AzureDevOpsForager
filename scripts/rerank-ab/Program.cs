using System;
using System.Collections.Generic;
using System.Data.SqlClient;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Text;
using System.Text.Json;
using System.Threading.Tasks;
using AzureDevOpsForager.Core;
using AzureDevOpsForager.Core.Services.Embedding;
using AzureDevOpsForager.Core.Services.Reranking;

/// <summary>
/// Head-to-head reranker comparison. A cross-encoder is a pure function of (query, documents), so
/// handing BOTH models the identical candidate list removes the embedder, the index and the fusion
/// from the comparison entirely. Nothing differs but the reranker.
///
/// Stages are separate and each writes its own JSON so the slow local pass can never cost the
/// perishable hosted one: candidates -> hosted (GPU, endpoint is warm) -> local (CPU, minutes).
/// </summary>
internal static class Program
{
   private const string OutDir = @"C:\Temp\ForClaude\AzureDevOpsForager";

   private sealed class Cand
   {
      public int Index { get; set; }
      public string File { get; set; }
      public string Content { get; set; }
   }

   private sealed class QSet
   {
      public string Question { get; set; }
      public string Expected { get; set; }
      public string Kind { get; set; }
      public List<Cand> Candidates { get; set; }
      public int FirstStageRank { get; set; }
   }

   private static readonly (string Q, string A, string Kind)[] Questions =
   {
      ("what stops someone paying with nothing in their cart",              "EmptyBasketOnCheckoutException.cs", "semantic"),
      ("what happens to a guest's items when they sign in",                 "TransferBasket.cs",                 "semantic"),
      ("how does the site show a temporary popup message",                  "ToastComponent.cs",                 "semantic"),
      ("how does it decide a picture upload is allowed",                    "ImageValidators.cs",                "semantic"),
      ("what keeps repeated lookups from hitting the database every time",  "CacheHelpers.cs",                   "semantic"),
      ("what fills the store with starting products",                       "CatalogContextSeed.cs",             "semantic"),
      ("what tells us the storefront is still reachable",                   "HomePageHealthCheck.cs",            "semantic"),
      ("what turns an unhandled failure into a clean api response",         "ExceptionMiddleware.cs",            "semantic"),
      ("how is a bearer credential produced after sign in",                 "IdentityTokenClaimService.cs",      "semantic"),
      ("where does a customer see everything they bought before",           "GetMyOrdersHandler.cs",             "semantic"),
      ("how is a long product list broken up for display",                  "CatalogFilterPaginatedSpecification.cs", "semantic"),
      ("what runs when a shopper ends their session",                       "Logout.cshtml.cs",                  "semantic"),
      ("where does someone create a new login",                             "Register.cshtml.cs",                "semantic"),
      ("how does the browser client know who is currently signed in",       "CustomAuthStateProvider.cs",        "semantic"),
      ("OrderBuilder",        "OrderBuilder.cs",        "identifier"),
      ("ExceptionMiddleware", "ExceptionMiddleware.cs", "identifier"),
      ("CacheHelpers",        "CacheHelpers.cs",        "identifier"),
      ("BasketQueryService",  "BasketQueryService.cs",  "identifier"),
   };

   private static async Task<int> Main( string[] args )
   {
      var stage = args.Length > 0 ? args[0] : "all";
      var configPath = args.Length > 1 ? args[1] : @"C:\Temp\ForClaude\AzureDevOpsForager\config.local-e5.json";
      Config.LoadFromFile( configPath );

      // A suffix keeps profiles apart: "" is the hybrid shortlist, "-fts" the full-text-only one.
      var suffix     = Environment.GetEnvironmentVariable( "RERANKAB_SUFFIX" ) ?? "";
      var candPath   = Path.Combine( OutDir, $"rerank-candidates{suffix}.json" );
      var hostedPath = Path.Combine( OutDir, $"rerank-hosted{suffix}.json" );
      var localPath  = Path.Combine( OutDir, $"rerank-local{suffix}.json" );

      if( stage == "candidates" || stage == "all" )
         BuildCandidates( candPath );

      if( stage == "hosted" || stage == "all" )
         await ScoreAsync( candPath, hostedPath, "HOSTED", BuildHostedReranker() );

      if( stage == "local" || stage == "all" )
         await ScoreAsync( candPath, localPath, "LOCAL", new BgeReranker() );

      if( stage == "report" || stage == "all" )
         Report( candPath, hostedPath, localPath );

      return 0;
   }

   /// <summary>Pulls the same top-N shortlist the server would rerank, once, for every question.</summary>
   private static void BuildCandidates( string path )
   {
      const int pool = 30;
      // RERANKAB_VW=0 removes the vector leg entirely, leaving a pure full-text shortlist. That
      // answers whether the embedder earns its place in the pipeline at all, since a cross-encoder
      // never reads a vector and only needs candidates from somewhere.
      var vw = int.TryParse( Environment.GetEnvironmentVariable( "RERANKAB_VW" ), out var vwEnv ) ? vwEnv : 60;
      Console.WriteLine( $"first-stage vector weight = {vw}" );
      var embedder = new EmbeddingService();
      var sets = new List<QSet>();

      using var connection = new SqlConnection( Config.AzdoVectorConnectionString );
      connection.Open();

      foreach( var (question, expected, kind) in Questions )
      {
         var vector = embedder.EmbedQuery( question );
         var json = "[" + string.Join( ",", vector.Select( v => v.ToString( "R", CultureInfo.InvariantCulture ) ) ) + "]";

         using var command = new SqlCommand( $@"
DECLARE @qv VECTOR({Config.EmbeddingDimension}) = CAST(@vecjson AS VECTOR({Config.EmbeddingDimension}));
EXEC dbo.SearchCode @SearchText=@txt, @QueryVector=@qv, @TopN=@top, @ChunkType=NULL,
                    @VectorWeight=@vw, @ChunkFtsWeight=30, @FileFtsWeight=30,
                    @MinFtsRank=10, @MaxDistance=2.0;", connection );
         command.CommandTimeout = 180;
         command.Parameters.AddWithValue( "@vecjson", json );
         command.Parameters.AddWithValue( "@txt", question );
         command.Parameters.AddWithValue( "@top", pool );
         command.Parameters.AddWithValue( "@vw", vw );

         var candidates = new List<Cand>();
         using( var reader = command.ExecuteReader() )
         {
            var i = 0;
            while( reader.Read() )
            {
               var full = (string)reader["FilePath"];
               candidates.Add( new Cand
               {
                  Index = i++,
                  File = full.Split( '\\', '/' ).Last(),
                  Content = reader["ChunkContent"] as string ?? ""
               } );
            }
         }

         var first = candidates.FindIndex( c => c.File == expected );
         sets.Add( new QSet
         {
            Question = question, Expected = expected, Kind = kind,
            Candidates = candidates, FirstStageRank = first < 0 ? -1 : first + 1
         } );
         Console.WriteLine( $"  {expected,-44} candidates={candidates.Count,-3} first-stage rank={( first < 0 ? "none" : ( first + 1 ).ToString() )}" );
      }

      File.WriteAllText( path, JsonSerializer.Serialize( sets, new JsonSerializerOptions { WriteIndented = false } ) );
      Console.WriteLine( $"candidates -> {path}" );
   }

   private static IReranker BuildHostedReranker()
   {
      var url = Environment.GetEnvironmentVariable( "HF_RERANK_URL" );
      var token = Environment.GetEnvironmentVariable( "HF_TOKEN" );
      if( string.IsNullOrWhiteSpace( url ) || string.IsNullOrWhiteSpace( token ) )
         throw new InvalidOperationException( "HF_RERANK_URL and HF_TOKEN must be set for the hosted stage." );
      return new HuggingFaceReranker( url, token );
   }

   /// <summary>Scores every question's shortlist with one reranker and records the full ordering.</summary>
   private static async Task ScoreAsync( string candPath, string outPath, string label, IReranker reranker )
   {
      var sets = JsonSerializer.Deserialize<List<QSet>>( File.ReadAllText( candPath ) );
      var results = new List<object>();
      var stopwatch = System.Diagnostics.Stopwatch.StartNew();

      foreach( var set in sets )
      {
         var candidates = set.Candidates
            .Select( c => new RerankerCandidate( c.Index, c.Content ) )
            .ToList();

         var t0 = stopwatch.ElapsedMilliseconds;
         var scored = await reranker.RerankAsync( set.Question, candidates, candidates.Count );
         var ms = stopwatch.ElapsedMilliseconds - t0;

         // Reranked order, de-duplicated to file level: several chunks of one file are one answer.
         var files = new List<string>();
         foreach( var result in scored )
         {
            var file = set.Candidates.First( c => c.Index == result.OriginalIndex ).File;
            if( !files.Contains( file ) ) files.Add( file );
         }

         var rank = files.IndexOf( set.Expected );
         results.Add( new
         {
            set.Question, set.Expected, set.Kind, set.FirstStageRank,
            Rank = rank < 0 ? -1 : rank + 1,
            Ms = ms,
            Top5 = files.Take( 5 ).ToList()
         } );
         Console.WriteLine( $"  [{label}] {set.Expected,-44} first={set.FirstStageRank,-4} after={( rank < 0 ? "none" : ( rank + 1 ).ToString() ),-5} {ms} ms" );
      }

      File.WriteAllText( outPath, JsonSerializer.Serialize( results, new JsonSerializerOptions { WriteIndented = true } ) );
      Console.WriteLine( $"{label} -> {outPath}" );
   }

   private static void Report( string candPath, string hostedPath, string localPath )
   {
      if( !File.Exists( hostedPath ) || !File.Exists( localPath ) )
      {
         Console.WriteLine( "report: need both hosted and local score files" );
         return;
      }

      using var hosted = JsonDocument.Parse( File.ReadAllText( hostedPath ) );
      using var local = JsonDocument.Parse( File.ReadAllText( localPath ) );
      var h = hosted.RootElement.EnumerateArray().ToList();
      var l = local.RootElement.EnumerateArray().ToList();

      Console.WriteLine();
      Console.WriteLine( "{0,-44} {1,6} {2,8} {3,8}", "expected file", "first", "bge-m3", "qwen0.6" );
      Console.WriteLine( new string( '-', 72 ) );

      int lWin = 0, hWin = 0, tie = 0;
      double lMrr = 0, hMrr = 0, fMrr = 0;
      int n = 0;

      for( var i = 0; i < h.Count && i < l.Count; i++ )
      {
         var first = h[i].GetProperty( "FirstStageRank" ).GetInt32();
         var hr = h[i].GetProperty( "Rank" ).GetInt32();
         var lr = l[i].GetProperty( "Rank" ).GetInt32();
         var name = h[i].GetProperty( "Expected" ).GetString();

         if( first < 1 ) continue;   // shortlist never held the answer; the reranker cannot be blamed
         n++;
         fMrr += 1.0 / first;
         if( lr >= 1 ) lMrr += 1.0 / lr;
         if( hr >= 1 ) hMrr += 1.0 / hr;

         var verdict = "";
         if( lr == hr ) { tie++; verdict = "tie"; }
         else if( lr >= 1 && ( hr < 1 || lr < hr ) ) { lWin++; verdict = "bge-m3"; }
         else { hWin++; verdict = "qwen0.6"; }

         Console.WriteLine( "{0,-44} {1,6} {2,8} {3,8}   {4}", name, first,
            lr < 1 ? "none" : lr.ToString(), hr < 1 ? "none" : hr.ToString(), verdict );
      }

      Console.WriteLine( new string( '-', 72 ) );
      Console.WriteLine( $"scored on {n} questions whose shortlist actually contained the answer" );
      Console.WriteLine( $"bge-reranker-v2-m3 wins {lWin}   Qwen3-Reranker-0.6B wins {hWin}   tie {tie}" );
      var f  = n > 0 ? fMrr / n : 0;
      var lm = n > 0 ? lMrr / n : 0;
      var hm = n > 0 ? hMrr / n : 0;
      Console.WriteLine();
      Console.WriteLine( $"MRR  first stage, no rerank  {f:N3}" );
      Console.WriteLine( $"MRR  bge-reranker-v2-m3      {lm:N3}   {( lm / f - 1 ) * 100:N0}% over first stage" );
      Console.WriteLine( $"MRR  Qwen3-Reranker-0.6B     {hm:N3}   {( hm / f - 1 ) * 100:N0}% over first stage" );
   }
}
