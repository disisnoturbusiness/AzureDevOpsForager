using AzureDevOpsForager.Core;
using AzureDevOpsForager.Core.Models.API;
using AzureDevOpsForager.Core.Models.Search;
using AzureDevOpsForager.Core.Services.Chat;
using AzureDevOpsForager.Core.Services.Embedding;
using AzureDevOpsForager.Core.Services.Reranking;
using AzureDevOpsForager.Core.Services.Search;
using AzureDevOpsForager.Core.Services.Storage;
using Microsoft.Data.SqlClient;
using Microsoft.Extensions.Hosting.WindowsServices;
using System.Diagnostics;
using System.Security.Principal;

namespace AzureDevOpsForager.Server;

/// <summary>
/// Entry point and HTTP host for the AzureDevOpsForager search server, an ASP.NET Core
/// minimal-API application. The same executable runs either as a Windows Service (for
/// unattended, always-on hosting) or as an interactive console app (for local debugging).
/// Search is backed by SQL Server 2025 using its native VECTOR type and DiskANN index,
/// so semantic + full-text hybrid search happens inside the database rather than a
/// separate vector store.
/// </summary>
public class Server
{
   #region Public Methods

   /// <summary>
   /// Process entry point. Prints a startup banner, loads configuration, optionally handles
   /// the one-shot "--set-groq-key" secret-setup mode (and exits), then builds and runs the
   /// web host. The host serves the browser UI from wwwroot and exposes the JSON search API.
   /// </summary>
   /// <param name="args">Raw command-line arguments; also inspected for "--set-groq-key".</param>
   public static void Main( string[] args )
   {
      // Detect the hosting mode up front so both the banner and host wiring agree on it.
      var isService = WindowsServiceHelpers.IsWindowsService();

      PrintStartupBanner( isService );
      LoadConfiguration();

      // Secret-setup mode is terminal: if the user asked to store a secret (--set-secret / --set-groq-key), do it and stop.
      if( TryHandleSetSecret( args ) )
         return;

      // Service-setup mode is also terminal: "--install" / "--uninstall" register/remove the Windows Service.
      if( TryHandleServiceCommand( args ) )
         return;

      LogEffectiveSearchConfiguration();

      var app = BuildApplication( args, isService );

      RecordSiteVisits( app );  // must precede the static-file middleware, which short-circuits "/"

      app.UseDefaultFiles();   // serve wwwroot/index.html at /
      app.UseStaticFiles();    // serve the self-contained web UI from wwwroot

      SetupEndpoints( app );

      Console.WriteLine();
      Console.WriteLine( $"[SERVER] Listening on http://0.0.0.0:{Config.Port}" );
      Console.WriteLine( "[SERVER] Ready for requests!" );
      Console.WriteLine();

      RegisterSqlWarmup( app );

      app.Run();
   }

   #endregion

   #region Private Methods

   /// <summary>
   /// Records one row in dbo.SiteVisits per page load: the time and the caller's IP.
   /// <para>
   /// Deliberately hooked to the DOCUMENT request rather than to the API. A visit is an arrival, not an
   /// action — someone who runs twenty searches is one visitor, and putting this on /query would both
   /// bury that and mix per-arrival facts into the per-action usage table.
   /// </para>
   /// <para>
   /// Runs before UseDefaultFiles/UseStaticFiles because those short-circuit the pipeline as soon as they
   /// match "/", so anything registered after them never sees the request that matters. Only "/" and an
   /// explicit "/index.html" count, which keeps css/js/favicon fetches from each logging a visit.
   /// </para>
   /// <para>
   /// The real caller is in X-Forwarded-For: behind App Service's load balancer, RemoteIpAddress is the
   /// balancer, so reading it alone would record the same infrastructure address for every visitor.
   /// </para>
   /// </summary>
   /// <param name="app">The web application to insert the middleware into, ahead of static files.</param>
   private static void RecordSiteVisits( WebApplication app )
   {
      app.Use( async ( context, next ) =>
      {
         var path = context.Request.Path.Value ?? "";
         var isDocument = path == "/" || path.Equals( "/index.html", StringComparison.OrdinalIgnoreCase );

         if( isDocument && HttpMethods.IsGet( context.Request.Method ) )
         {
            var forwarded = context.Request.Headers["X-Forwarded-For"].ToString();
            var clientIp = string.IsNullOrWhiteSpace( forwarded )
               ? context.Connection.RemoteIpAddress?.ToString()
               : forwarded;

            UsageTelemetry.RecordVisit( clientIp );
         }

         await next();
      } );
   }

   /// <summary>
   /// Writes the boxed startup banner to the console: product name, hosting mode, port, and
   /// the resolved SQL Server / database. Parsing the connection string here keeps credentials
   /// out of the banner while still showing operators which instance they are pointed at.
   /// </summary>
   /// <param name="isService">True when hosted as a Windows Service, false for console mode.</param>
   private static void PrintStartupBanner( bool isService )
   {
      Console.WriteLine( "=" + new string( '=', 60 ) );
      Console.WriteLine( "  AzureDevOpsForager Search Server - SQL Server 2025 Edition" );
      Console.WriteLine( "=" + new string( '=', 60 ) );
      Console.WriteLine( $"  Mode: {( isService ? "Windows Service" : "Console" )}" );
      Console.WriteLine( $"  Port: {Config.Port}" );

      var connectionInfo = new SqlConnectionStringBuilder( Config.SqlConnectionString );
      Console.WriteLine( $"  DB:   Server={connectionInfo.DataSource};Database={connectionInfo.InitialCatalog}" );
      Console.WriteLine();
   }

   /// <summary>
   /// Loads configuration in precedence order: the per-exe config.json next to the binary,
   /// then shared per-user overrides (model path, chosen DB) which win over config.json, and
   /// finally ensures any directories the app relies on exist.
   /// </summary>
   private static void LoadConfiguration()
   {
      var configPath = Path.Combine( AppDomain.CurrentDomain.BaseDirectory, "config.json" );
      Config.LoadFromFile( configPath );
      Config.LoadUserOverrides();   // shared per-user overrides (model path, chosen DB) win over the per-exe config.json
      Config.EnsureDirectories();
   }

   /// <summary>
   /// Prints the retrieval settings actually in force after all three config layers have been applied.
   /// <para>
   /// This exists because the values that decide whether search works at all arrive from a config.json
   /// that is deliberately not in source control, so reading the repo tells you nothing about what a
   /// given deployment is really running. A mis-set MaxVectorDistance silently empties the vector leg
   /// and a mis-set rerank gate silently empties the result set — neither raises an error, so without
   /// this line the only symptom is quietly worse results. One line at boot makes the effective
   /// configuration a matter of record instead of an archaeology exercise.
   /// </para>
   /// Secrets and connection strings are deliberately excluded: this goes to a log that may be shipped
   /// or pasted, and none of these values are sensitive on their own.
   /// </summary>
   private static void LogEffectiveSearchConfiguration()
   {
      var embedSource = Config.HuggingFaceEnabled ? "HuggingFace endpoint"
         : File.Exists( Config.OnnxModelPath ) ? "local ONNX"
         : "none (full-text only)";

      var line = $"[CONFIG] EmbeddingDimension={Config.EmbeddingDimension} embedder={embedSource} " +
                 $"MaxVectorDistance={Config.MaxVectorDistance} MinRerankScoreRatio={Config.MinRerankScoreRatio} MinRerankTopScore={Config.MinRerankTopScore} MinVectorOnly={Config.MinVectorOnlyRerankScore} " +
                 $"reranker={( Config.RerankerEnabled ? "on" : "off" )} rerankModel={Config.RerankerModelName} rerankApi={Config.RerankerApiFormat} RerankerInputSize={Config.RerankerInputSize} " +
                 $"RRF(v/c/f)={Config.RrfVectorWeight}/{Config.RrfChunkFtsWeight}/{Config.RrfFileFtsWeight} " +
                 $"MinFtsRank={Config.MinFtsRank}";

      Console.WriteLine( line );
      Logger.Info( line, "Config" );
   }

   /// <summary>
   /// Handles the one-time secret-setup commands: "Server --set-secret NAME VALUE" (generic) or the
   /// back-compat "Server --set-groq-key gsk_xxx". When present, stores the value in the consolidated,
   /// AES-encrypted secrets.enc beside the binary and reports success. The app later decrypts each secret
   /// whenever the matching environment variable is not set.
   /// </summary>
   /// <param name="args">Command-line arguments to scan for the flag and its value(s).</param>
   /// <returns>True if a set-secret flag was handled (caller should exit); false to continue startup.</returns>
   private static bool TryHandleSetSecret( string[] args )
   {
      // Generic form: --set-secret <NAME> <VALUE> (e.g. HF_TOKEN, GROQ_API_KEY).
      var genericIndex = Array.IndexOf( args, "--set-secret" );
      if( genericIndex >= 0 && genericIndex + 2 < args.Length )
      {
         WriteSecret( args[genericIndex + 1], args[genericIndex + 2] );
         return true;
      }

      // Back-compat alias: --set-groq-key <VALUE> stores the Groq key under GROQ_API_KEY.
      var groqAliasIndex = Array.IndexOf( args, "--set-groq-key" );
      if( groqAliasIndex >= 0 && groqAliasIndex + 1 < args.Length )
      {
         WriteSecret( "GROQ_API_KEY", args[groqAliasIndex + 1] );
         return true;
      }

      return false;
   }

   /// <summary>
   /// Stores one named secret into the consolidated, AES-encrypted secrets.enc beside the binary, merging it
   /// with any secrets already present. The app decrypts and uses it whenever the matching environment
   /// variable is not set.
   /// </summary>
   /// <param name="name">Secret name (e.g. HF_TOKEN, GROQ_API_KEY).</param>
   /// <param name="value">The clear secret value to protect.</param>
   private static void WriteSecret( string name, string value )
   {
      AzureDevOpsForager.Core.Services.Utilities.SecretStore.Set( name, value );
      Console.WriteLine( $"Stored secret '{name}' in the encrypted secrets.enc (next to the binary)." );
      Console.WriteLine( $"The app decrypts + uses it whenever the {name} environment variable is not set." );
   }

   /// <summary>
   /// Handles the one-shot service commands: "Server --install" registers this exe as an auto-start Windows
   /// Service (and starts it); "Server --uninstall" stops and removes it. Both need elevation: if not already
   /// admin, it tries to relaunch elevated (UAC); when that is blocked (locked-down machines), it prints the
   /// exact command an administrator can run instead and points at install-service.cmd. Windows-only.
   /// </summary>
   /// <param name="args">Command-line arguments to scan for --install / --uninstall.</param>
   /// <returns>True if a service command was handled (caller should exit); false to continue startup.</returns>
   private static bool TryHandleServiceCommand( string[] args )
   {
      bool install = Array.IndexOf( args, "--install" ) >= 0;
      bool uninstall = Array.IndexOf( args, "--uninstall" ) >= 0;
      if( !install && !uninstall )
         return false;

      if( !OperatingSystem.IsWindows() )
      {
         Console.WriteLine( "--install / --uninstall are Windows-only (the service host is a Windows Service)." );
         return true;
      }

      const string serviceName = "AzureDevOpsForagerServer";
      var exePath = Environment.ProcessPath ?? "";

      if( !IsElevated() )
      {
         Console.WriteLine( "Installing a Windows Service requires administrator rights." );
         if( TryRelaunchElevated( args ) )
            return true;   // an elevated copy is doing the work (or the user is being prompted)

         // Locked-down environment: can't self-elevate. Tell the user exactly what to hand their admin.
         PrintManualServiceInstructions( install, serviceName, exePath );
         return true;
      }

      if( install )
      {
         RunSc( $"create {serviceName} binPath= \"{exePath}\" start= auto DisplayName= \"Azure DevOps Forager Search Server\"" );
         RunSc( $"description {serviceName} \"Serves semantic + full-text code search over your indexed database.\"" );
         RunSc( $"start {serviceName}" );
         Console.WriteLine( $"[OK] Service '{serviceName}' installed and started (auto-start on boot)." );
      }
      else
      {
         RunSc( $"stop {serviceName}" );
         RunSc( $"delete {serviceName}" );
         Console.WriteLine( $"[OK] Service '{serviceName}' stopped and removed." );
      }
      return true;
   }

   /// <summary>True when the current process is running with administrator rights (Windows only).</summary>
   private static bool IsElevated()
   {
      if( !OperatingSystem.IsWindows() ) return false;
      using( var identity = WindowsIdentity.GetCurrent() )
         return new WindowsPrincipal( identity ).IsInRole( WindowsBuiltInRole.Administrator );
   }

   /// <summary>
   /// Attempts to relaunch this exe with the same service flag under a UAC elevation prompt. Returns true if
   /// the elevated process was launched (or the user is being prompted); false if elevation was refused or
   /// blocked — common on locked-down corporate machines.
   /// </summary>
   private static bool TryRelaunchElevated( string[] args )
   {
      try
      {
         var startInfo = new ProcessStartInfo
         {
            FileName = Environment.ProcessPath,
            Arguments = string.Join( " ", args ),
            UseShellExecute = true,
            Verb = "runas"
         };
         Process.Start( startInfo );
         Console.WriteLine( "Continuing in an elevated window (approve the UAC prompt)..." );
         return true;
      }
      catch
      {
         return false;   // UAC denied, or elevation not permitted on this machine
      }
   }

   /// <summary>Runs sc.exe with the given arguments and echoes its output (Windows service control).</summary>
   private static void RunSc( string arguments )
   {
      try
      {
         var startInfo = new ProcessStartInfo
         {
            FileName = "sc.exe",
            Arguments = arguments,
            UseShellExecute = false,
            RedirectStandardOutput = true,
            RedirectStandardError = true
         };
         using( var process = Process.Start( startInfo ) )
         {
            if( process == null ) return;
            var output = process.StandardOutput.ReadToEnd().Trim();
            var error = process.StandardError.ReadToEnd().Trim();
            if( output.Length > 0 ) Console.WriteLine( output );
            if( error.Length > 0 ) Console.WriteLine( error );
            process.WaitForExit();
         }
      }
      catch( Exception exception )
      {
         Console.WriteLine( $"sc {arguments} failed: {exception.Message}" );
      }
   }

   /// <summary>
   /// Prints copy-paste instructions for an administrator to install/uninstall the service by hand — for
   /// locked-down machines where self-elevation is blocked and the user must ask their admin to run it.
   /// </summary>
   private static void PrintManualServiceInstructions( bool install, string serviceName, string exePath )
   {
      Console.WriteLine();
      Console.WriteLine( "This machine won't let the program self-elevate. Have an administrator do one of these:" );
      Console.WriteLine();
      Console.WriteLine( "  EASIEST: right-click  install-service.cmd  (next to this exe)  ->  \"Run as administrator\"." );
      Console.WriteLine();
      Console.WriteLine( "  OR, in an elevated (Run as administrator) command prompt, run:" );
      if( install )
      {
         Console.WriteLine( $"    sc create {serviceName} binPath= \"{exePath}\" start= auto DisplayName= \"Azure DevOps Forager Search Server\"" );
         Console.WriteLine( $"    sc start {serviceName}" );
      }
      else
      {
         Console.WriteLine( $"    sc stop {serviceName}" );
         Console.WriteLine( $"    sc delete {serviceName}" );
      }
      Console.WriteLine();
   }

   /// <summary>
   /// Builds the web host: applies Windows Service hosting when applicable, binds Kestrel to
   /// the configured port, registers all search/AI services in the DI container, and returns
   /// the built <see cref="WebApplication"/> ready for endpoint mapping.
   /// </summary>
   /// <param name="args">Command-line arguments passed through to the host builder.</param>
   /// <param name="isService">True to enable Windows Service lifetime integration.</param>
   private static WebApplication BuildApplication( string[] args, bool isService )
   {
      var builder = WebApplication.CreateBuilder( args );

      if( isService )
      {
         builder.Host.UseWindowsService();
      }

      // Bind Kestrel to the configured port and keep the server anonymous in responses.
      builder.WebHost.ConfigureKestrel( options =>
      {
         options.ListenAnyIP( Config.Port );
         options.AddServerHeader = false;   // don't advertise "Server: Kestrel" in responses
      } );

      RegisterServices( builder );
      return builder.Build();
   }

   /// <summary>
   /// Registers the search pipeline services as singletons. The full-text service is opened
   /// eagerly at construction; the embedding and reranker services are conditional on their
   /// ONNX models being present (and, for the reranker, enabled), so the server still runs in
   /// a reduced mode when a model is missing. The Groq LLM provider is always registered but
   /// reports itself unconfigured without an API key, letting /chat degrade gracefully.
   /// </summary>
   /// <param name="builder">The host builder whose service collection is populated.</param>
   private static void RegisterServices( WebApplicationBuilder builder )
   {
      builder.Services.AddSingleton<SqlFtsService>( serviceProvider =>
      {
         var ftsService = new SqlFtsService();
         ftsService.Open();
         return ftsService;
      } );

      builder.Services.AddSingleton<SqlVectorService>();

      // Embedding source (IEmbedder): prefer the HF endpoint when configured (zero local ONNX); otherwise
      // fall back to the local ONNX EmbeddingService when its model file is present. With neither, search
      // runs full-text-only.
      if( Config.HuggingFaceEnabled )
      {
         builder.Services.AddSingleton<IEmbedder>( serviceProvider => new HuggingFaceEmbedder( Config.HuggingFaceEmbedUrl, Config.HuggingFaceToken ) );
         Console.WriteLine( "[EMBED] Hugging Face endpoint (no local ONNX)" );
      }
      else if( File.Exists( Config.OnnxModelPath ) )
      {
         builder.Services.AddSingleton<IEmbedder>( serviceProvider => new EmbeddingService() );
         Console.WriteLine( "[EMBED] Local ONNX model" );
      }
      else
      {
         Console.WriteLine( "[EMBED] Disabled (full-text-only search)" );
      }

      // Cross-encoder reranker (optional second stage): prefer the HF endpoint when configured, else the
      // local ONNX BgeReranker when its model is present and reranking is enabled.
      if( Config.RerankerEnabled && Config.HuggingFaceEnabled && !string.IsNullOrWhiteSpace( Config.HuggingFaceRerankUrl ) )
      {
         builder.Services.AddSingleton<IReranker>( serviceProvider => new HuggingFaceReranker( Config.HuggingFaceRerankUrl, Config.HuggingFaceToken ) );
         Console.WriteLine( "[RERANK] Hugging Face endpoint (no local ONNX)" );
      }
      else if( Config.RerankerEnabled && File.Exists( Config.RerankerModelPath ) )
      {
         builder.Services.AddSingleton<IReranker>( serviceProvider => new BgeReranker() );
         Console.WriteLine( "[RERANK] Local ONNX model" );
      }
      else
      {
         Console.WriteLine( "[RERANK] Disabled (RRF fusion only)" );
      }

      // LLM provider for /chat (Groq). IsConfigured=false without GROQ_API_KEY, so /chat degrades gracefully.
      builder.Services.AddSingleton<ILLMProvider>( serviceProvider => new GroqProvider() );

      builder.Services.AddSingleton<HybridSearchService>();
   }

   /// <summary>
   /// Registers a callback that runs after the host signals RUNNING to the Service Control
   /// Manager. It warms up the SQL full-text connection and logs the indexed file count, so
   /// the first real request does not pay the connection cost and operators can confirm the
   /// index is populated.
   /// </summary>
   /// <param name="app">The running web application whose lifetime events are hooked.</param>
   private static void RegisterSqlWarmup( WebApplication app )
   {
      app.Lifetime.ApplicationStarted.Register( () =>
      {
         var ftsService = app.Services.GetRequiredService<SqlFtsService>();
         Console.WriteLine( $"[SQL FTS] Connected to the code database" );
         Console.WriteLine( $"[SQL FTS] Files indexed: {ftsService.GetFileCount():N0}" );

         // The Server has historically only ever READ schema the Indexer created, so the usage table --
         // the one thing the Server itself writes -- would otherwise never exist here. Idempotent and
         // non-fatal: an index that cannot record its own usage is still a working index.
         _ = Task.Run( async () =>
         {
            try
            {
               await SchemaInitializer.EnsureUsageTableAsync( Config.SqlConnectionString );
               Console.WriteLine( "[TELEMETRY] dbo.UsageEvents ready." );
            }
            catch( Exception exception )
            {
               Console.WriteLine( $"[TELEMETRY] usage table unavailable ({exception.GetType().Name}); usage will not be recorded." );
            }
         } );
      } );
   }

   // There used to be a RegisterHfWarmup here that woke the embed and rerank endpoints on
   // ApplicationStarted, so the first visitor after a restart would not pay their cold start.
   //
   // It was removed because it fires on EVERY app start, and on this deployment the app restarts
   // on every push to main and on every app-settings change. Each of those woke two A10Gs that
   // then stayed billable for the full 30-minute idle window — around $1 per deploy, paid on days
   // when the only person touching the site was the one deploying it. Eight deploys in a day is
   // most of a day's GPU budget spent warming endpoints for nobody.
   //
   // The two warm-ups that remain are the ones tied to an actual person: the browser fires one on
   // the visitor's first keystroke, and SearchAsync calls StartRerankerWarmup so the rerank cold
   // start overlaps the embed cold start rather than following it. A restart with no visitor now
   // costs nothing, which is the common case.

   /// <summary>
   /// Wires up every HTTP endpoint on the application. Grouped by concern (health, search,
   /// chat, and info) into focused helpers so each endpoint's request/response shape stays
   /// easy to follow.
   /// </summary>
   /// <param name="app">The web application to map endpoints onto.</param>
   private static void SetupEndpoints( WebApplication app )
   {
      MapHealthEndpoint( app );
      MapSearchEndpoints( app );
      MapChatEndpoints( app );
      MapInfoEndpoints( app );
   }

   /// <summary>
   /// Maps GET /health, which returns the hybrid search service's self-reported health
   /// (index availability and file counts) as JSON.
   /// </summary>
   /// <param name="app">The web application to map the endpoint onto.</param>
   private static void MapHealthEndpoint( WebApplication app )
   {
      app.MapGet( "/health", async ( HybridSearchService search ) =>
      {
         var health = await search.GetHealthAsync();
         return Results.Json( health );
      } );
   }

   /// <summary>
   /// Maps the search-oriented endpoints: POST /query (hybrid RRF search over the question),
   /// POST /embed (turn text into a vector using the server's ONNX model), and
   /// POST /search_by_filename (pattern match on file name).
   /// </summary>
   /// <param name="app">The web application to map the endpoints onto.</param>
   private static void MapSearchEndpoints( WebApplication app )
   {
      // Main search endpoint
      app.MapPost( "/query", async ( SearchRequest request, HybridSearchService search ) =>
      {
         Console.WriteLine( $"[QUERY] {request.Question}" );

         var stopwatch = Stopwatch.StartNew();
         var response = await search.SearchAsync( request );
         stopwatch.Stop();

         var resultCount = response.Ids?.FirstOrDefault()?.Count ?? 0;

         if( !string.IsNullOrEmpty( response.Error ) )
         {
            Console.WriteLine( $"[ERROR] {response.Error}" );
         }
         else
         {
            Console.WriteLine( $"[RESULT] Found {resultCount} results" );

            // Which leg won is worth a column: it is the cheapest available signal that the vector path is
            // actually contributing. An all-FullText week means the headline feature has quietly died again.
            var topSource = response.Metadatas?.FirstOrDefault()?.FirstOrDefault()
               ?.TryGetValue( "match_source", out var source ) == true ? source : null;

            UsageTelemetry.RecordQuery( "search", request.Question, resultCount, stopwatch.ElapsedMilliseconds, topSource: topSource );
         }

         return Results.Json( response );
      } );

      // Embedding service: turn text into a vector using the server's ONNX model, so the indexer
      // (and anyone building an index) can embed remotely instead of shipping/loading a local model.
      app.MapPost( "/embed", async ( EmbedRequest request, IServiceProvider serviceProvider ) =>
      {
         var embedder = serviceProvider.GetService<IEmbedder>();
         if( embedder == null )
            return Results.Json( new { error = "Embedding not available on this server." }, statusCode: 503 );

         // Await the async embed so a remote HF round-trip does not block this ASP.NET request thread
         // on GetAwaiter().GetResult(); the local ONNX path completes synchronously either way.
         var text = request?.Text ?? "";
         var vector = string.Equals( request?.Kind, "query", StringComparison.OrdinalIgnoreCase )
            ? await embedder.EmbedQueryAsync( text )
            : await embedder.EmbedPassageAsync( text );
         return Results.Json( new { vector } );
      } );

      // Batched embedding: same model, many texts in one request and one forward pass. The indexer uses
      // this to embed a file's chunks together instead of one HTTP round-trip and one model run per chunk.
      app.MapPost( "/embed_batch", async ( EmbedBatchRequest request, IServiceProvider serviceProvider ) =>
      {
         var embedder = serviceProvider.GetService<IEmbedder>();
         if( embedder == null )
            return Results.Json( new { error = "Embedding not available on this server." }, statusCode: 503 );

         // Await the async batch so remote HF round-trips do not block this ASP.NET request thread;
         // the local ONNX path completes synchronously either way.
         var texts = request?.Texts ?? System.Array.Empty<string>();
         var vectors = string.Equals( request?.Kind, "query", StringComparison.OrdinalIgnoreCase )
            ? await embedder.EmbedQueryBatchAsync( texts )
            : await embedder.EmbedPassageBatchAsync( texts );
         return Results.Json( new { vectors } );
      } );

      // Search by filename
      app.MapPost( "/search_by_filename", ( FilenameRequest request, HybridSearchService search ) =>
      {
         Console.WriteLine( $"[FILENAME] {request.Filename}" );

         var response = search.SearchByFilename( request.Filename, request.NResults );

         return Results.Json( response );
      } );
   }

   /// <summary>
   /// Maps the chat endpoints: POST /chat (retrieval-augmented Q&amp;A grounded in code) and
   /// POST /chat/feedback (thumbs up/down appended to a local log).
   /// </summary>
   /// <param name="app">The web application to map the endpoints onto.</param>
   private static void MapChatEndpoints( WebApplication app )
   {
      // Chat: in-process search -> code context -> Groq answer (no external service).
      app.MapPost( "/chat", async ( SearchRequest request, HybridSearchService search, ILLMProvider llmProvider ) =>
      {
         Console.WriteLine( $"[CHAT] {request.Question}" );
         if( !llmProvider.IsConfigured )
            return Results.Json( new { answer = "Chat is not configured — set the GROQ_API_KEY environment variable on the server.", sources = Array.Empty<string>() } );

         var stopwatch = Stopwatch.StartNew();
         var searchResults = await search.SearchAsync( new SearchRequest { Question = request.Question, NResults = 8, ModuleFilter = request.ModuleFilter } );
         var contextBuilder = new System.Text.StringBuilder();
         var sources = new List<string>();
         var documents = searchResults.Documents?.FirstOrDefault();
         var ids = searchResults.Ids?.FirstOrDefault();
         if( documents != null && ids != null )
            for( int i = 0; i < documents.Count && i < ids.Count; i++ )
            {
               contextBuilder.AppendLine( $"// File: {ids[i]}" ).AppendLine( documents[i] ).AppendLine();
               if( !sources.Contains( ids[i] ) ) sources.Add( ids[i] );
            }

         // No retrieved code means nothing to ground an answer in, and a model handed an empty context
         // answers from its training weights instead of saying so — see GroundingGuard for why that is the
         // one failure this endpoint must not have. Short-circuit before the completion rather than after.
         var context = contextBuilder.ToString();
         if( !GroundingGuard.HasGrounding( context ) )
         {
            Console.WriteLine( $"[CHAT] No grounding for \"{request.Question}\" — answering without a model call." );
            stopwatch.Stop();

            // Recorded with Grounded=0 rather than skipped. How often the corpus cannot answer a question
            // people actually ask is the single most useful thing this table can tell you: a run of these
            // is either a gap worth indexing or a gate that has drifted too tight.
            UsageTelemetry.RecordQuery( "ask", request.Question, 0, stopwatch.ElapsedMilliseconds, grounded: false );

            return Results.Json( new
            {
               answer = GroundingGuard.NoGroundingAnswer,
               sources,
               results = new { ids = searchResults.Ids, documents = searchResults.Documents, metadatas = searchResults.Metadatas }
            } );
         }

         var answer = await llmProvider.AskAsync( request.Question, context );
         stopwatch.Stop();
         UsageTelemetry.RecordQuery( "ask", request.Question, sources.Count, stopwatch.ElapsedMilliseconds, grounded: true );

         // Return the retrieved hits in full, not just their file paths. The client renders an answer's
         // sources with the same card component the search results use — chunk name, type, match source,
         // scores, line range and the code itself — and none of that is reconstructable from a path.
         // "sources" is retained for older clients and because it is the deduped file-level view; the
         // chunk-level detail lives in "results", where two chunks from one file stay separate rather
         // than collapsing into a single entry.
         return Results.Json( new
         {
            answer,
            sources,
            results = new
            {
               ids = searchResults.Ids,
               documents = searchResults.Documents,
               metadatas = searchResults.Metadatas
            }
         } );
      } );

      // Chat feedback (thumbs up/down) appended to a local log.
      app.MapPost( "/chat/feedback", ( ChatFeedback feedback ) =>
      {
         try
         {
            // Was File.AppendAllText to a relative path. On App Service Linux the container filesystem is
            // recreated on every restart and deploy, so the feature appeared to collect feedback and in
            // fact discarded it. Goes to the database the app already has open.
            UsageTelemetry.RecordFeedback( feedback.Helpful, feedback.Question );
         }
         catch { }
         return Results.Json( new { ok = true } );
      } );
   }

   /// <summary>
   /// Maps the informational endpoints used by the UI and operators: GET /systems (file-type
   /// facet counts), GET /collections (index stats), and GET /status (plain-text status page).
   /// </summary>
   /// <param name="app">The web application to map the endpoints onto.</param>
   private static void MapInfoEndpoints( WebApplication app )
   {
      // Facet list for the UI filter: distinct FileType values + counts (generic; empty if no index yet).
      app.MapGet( "/systems", async () =>
      {
         var list = new List<object>();
         try
         {
            using var connection = new SqlConnection( Config.SqlConnectionString );
            await connection.OpenAsync();
            using var command = new SqlCommand( "SELECT ISNULL(NULLIF(FileType,''),'(none)'), COUNT(*) FROM dbo.CodeFiles GROUP BY FileType ORDER BY COUNT(*) DESC", connection );
            using var reader = await command.ExecuteReaderAsync();
            while( await reader.ReadAsync() ) list.Add( new { name = reader.GetString( 0 ), count = reader.GetInt32( 1 ) } );
         }
         catch { }
         return Results.Json( list );
      } );

      // Collections info
      app.MapGet( "/collections", async ( HybridSearchService search ) =>
      {
         var health = await search.GetHealthAsync();
         return Results.Json( new
         {
            collections = new[]
            {
               new
               {
                  name = "the code database",
                  count = health.FtsFileCount
               }
            }
         } );
      } );

      // Plain-text status page (the browser UI is served from wwwroot at /).
      app.MapGet( "/status", () =>
      {
         return Results.Text( @"
Azure DevOps Forager - Search Server (SQL Server 2025 / Azure SQL + DiskANN)
================================================================

Endpoints:
  GET  /                   - Web UI (browser search + chat)
  GET  /status             - This text status page
  POST /query              - Hybrid RRF search (question, n_results)
  POST /chat               - Ask a question; answer grounded in retrieved code (Groq)
  POST /chat/feedback      - Thumbs up/down on a chat answer
  POST /search_by_filename - Search by filename pattern
  POST /embed              - Embed a single text (one vector)
  POST /embed_batch        - Embed many texts in one call (one vector each)
  GET  /systems            - Distinct file-type facet counts
  GET  /health             - Health check
  GET  /collections        - Index stats

Backend: SQL Server 2025 / Azure SQL native VECTOR + DiskANN, RRF fusion, Qwen3-Reranker-0.6B cross-encoder rerank.
Embeddings: bge-code-v1 (code-specialized, 1536-dim) via Hugging Face endpoint, or local ONNX e5-large-v2 (1024-dim).
Status: Running
", "text/plain" );
      } );
   }

   #endregion
}

/// <summary>Thumbs up/down feedback on a chat answer.</summary>
public record ChatFeedback( string Question, string Answer, bool Helpful );

/// <summary>Text to embed via /embed. Kind = "passage" (default) or "query".</summary>
public record EmbedRequest( string Text, string Kind );

/// <summary>Texts to embed via /embed_batch in a single forward pass. Kind = "passage" (default) or "query".</summary>
public record EmbedBatchRequest( string[] Texts, string Kind );
