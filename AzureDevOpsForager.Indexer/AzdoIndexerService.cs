using AzureDevOpsForager.Indexer.Indexing;
using AzureDevOpsForager.Core;
using AzureDevOpsForager.Core.Services.Integration;
using AzureDevOpsForager.Core.Services.Sources;
using AzureDevOpsForager.Core.Services.Storage;
using AzureDevOpsForager.Core.Services.Embedding;
using Microsoft.Data.SqlClient;
using Newtonsoft.Json;
using System.Threading;

namespace AzureDevOpsForager.Indexer;

/// <summary>
/// Indexes a source codebase into the vector store.
/// Source is an <see cref="ISourceProvider"/> (Azure DevOps TFVC or Git, selected by config);
/// metadata + chunking are Roslyn-based and language-generic (no domain/vendor concepts).
/// </summary>
public class AzdoIndexerService : IDisposable
{
   #region Data Members

   /// <summary>Connection string to the vector store, read once from config at construction.</summary>
   private readonly string _connectionString;

   /// <summary>In-process embedding model, populated only when a local model path is configured (uncapped self-host).</summary>
   private EmbeddingService _localEmbed;

   /// <summary>Remote Hugging Face embedder, populated when a HF endpoint + token are configured (zero local ONNX).</summary>
   private HuggingFaceEmbedder _hfEmbedder;

   /// <summary>Background warm-up call that wakes the scale-to-zero HF endpoint at run start, so the GPU is
   /// already spun up by the time the embed step begins; awaited (not skipped) before embedding starts.</summary>
   private Task _hfWarmup;

   /// <summary>Shared HTTP client for remote embedding via the hosted /embed service (shared demo, capped).</summary>
   private System.Net.Http.HttpClient _embedHttp;

   /// <summary>Fully-qualified /embed endpoint; also serves as the "remote embedding configured" marker.</summary>
   private string _embedUrl;

   /// <summary>Max concurrent /embed calls on the REMOTE path. Tracks the hosted server's capacity, not the client's
   /// core count — a strong client at ProcessorCount-1 would otherwise swamp a small server and make it thrash.</summary>
   private const int RemoteEmbedMaxConcurrency = 4;

   /// <summary>
   /// Minimum fraction of the listed files that must land in staging before a full reindex is allowed to
   /// promote (swap staging → live). A run that stages fewer than this is treated as partial and aborted so a
   /// good live index is never clobbered by a bad build.
   /// </summary>
   private const double PromotionThresholdRatio = 0.95;

   /// <summary>
   /// Per-chunk character cap applied before embedding. A single chunk longer than this is truncated so one
   /// oversized chunk cannot blow the embedding model's token budget.
   /// </summary>
   private const int MaxChunkChars = 5000;

   /// <summary>
   /// Number of times the source file-listing call is retried when it returns zero files. The Azure DevOps
   /// listing API is occasionally flaky and returns an empty set transiently, so a few retries smooth it over.
   /// </summary>
   private const int ListingMaxAttempts = 4;

   /// <summary>
   /// Base backoff in milliseconds between empty-listing retries. Multiplied by the attempt number for a linear
   /// backoff (attempt 1 waits one unit, attempt 2 waits two, and so on).
   /// </summary>
   private const int ListingRetryBackoffMs = 2000;

   /// <summary>Azure DevOps client, created when the source type is TFVC or Azure-hosted Git.</summary>
   private AzureDevOpsService _azure;

   /// <summary>GitHub client, created when the source type is GitHub.</summary>
   private GitHubService _github;

   /// <summary>The active source provider that lists and reads files (TFVC, Git, or GitHub).</summary>
   private ISourceProvider _source;

   /// <summary>Table-name suffix that redirects writes: "" targets the live tables, "_Staging" targets the staging tables during a full build.</summary>
   private string _tableSuffix = "";

   /// <summary>
   /// Optional UI hook invoked (on the caller's thread) when a hosted-embedding run exceeds
   /// <see cref="Config.HostedEmbeddingFileCap"/>. Return true to proceed with the top-N files, false to cancel.
   /// Null (headless) proceeds with the cap.
   /// </summary>
   public Func<int, bool> OnHostedCapExceeded;

   #endregion Data Members

   #region Constructor

   /// <summary>Creates the service and captures the vector-store connection string from config.</summary>
   public AzdoIndexerService()
   {
      _connectionString = Config.AzdoVectorConnectionString;
   }

   #endregion Constructor

   #region Public Methods

   /// <summary>
   /// Full reindex. Rebuilds the entire index into staging tables, then atomically swaps staging into live.
   /// Runs a preflight capability check, honors the hosted-embedding file cap, and refuses to promote a
   /// partial build (under 95% staged) so a good live index is never clobbered by a bad run.
   /// </summary>
   public async Task RunMonthlyAsync( CancellationToken cancellationToken = default )
   {
      var runTimer = System.Diagnostics.Stopwatch.StartNew();
      Console.WriteLine( "[FULL] Starting full reindex..." );
      Console.WriteLine();

      Console.WriteLine( "[STEP 0] Preflight: validating SQL vector capability..." );
      await SchemaInitializer.ValidateVectorCapabilitiesAsync( _connectionString );

      Console.WriteLine( "[STEP 1] Creating fresh staging tables..." );
      await SchemaInitializer.EnsureStagingTablesAsync( _connectionString );

      Console.WriteLine( "[STEP 2] Initializing services..." );
      InitializeServices();

      Console.WriteLine( $"[STEP 3] Listing files from {_source.SourceDescription}..." );
      var files = await ListFilesWithRetryAsync();
      Console.WriteLine( $"         Found {files.Count:N0} indexable files" );

      if( files.Count == 0 )
      {
         Console.WriteLine( "[ABORT] No files found — live index left untouched (not swapping)." );
         return;
      }

      files = ApplyHostedEmbeddingCap( files );
      cancellationToken.ThrowIfCancellationRequested();

      Console.WriteLine( "[STEP 4] Chunking + embedding files into staging..." );
      _tableSuffix = "_Staging";
      try { await IndexFilesAsync( files, isFullReindex: true, cancellationToken ); }
      finally { _tableSuffix = ""; }

      // Completion guard: never promote a partial build over a good live index.
      var stagedCount = await SchemaInitializer.RowCountAsync( _connectionString, "CodeFiles_Staging" );
      if( stagedCount < (long)( files.Count * PromotionThresholdRatio ) )
      {
         Console.WriteLine( $"[ABORT] Only {stagedCount:N0}/{files.Count:N0} files staged (< 95%) — live index left untouched (not swapping)." );
         return;
      }

      Console.WriteLine( $"[STEP 5] Swapping staging → live ({stagedCount:N0} files) + rebuilding indexes..." );
      await SchemaInitializer.SwapStagingToLiveAsync( _connectionString );

      SaveLastRunTime();

      runTimer.Stop();
      Console.WriteLine();
      Console.WriteLine( $"[COMPLETE] Indexed {stagedCount:N0} files (promoted live via staging swap) in {FormatElapsed( runTimer.Elapsed )}" );
   }

   /// <summary>Releases the local ONNX embedding session and the remote HTTP client, whichever was used.</summary>
   public void Dispose()
   {
      _localEmbed?.Dispose();
      _hfEmbedder?.Dispose();
      _embedHttp?.Dispose();
   }

   #endregion Public Methods

   #region Private Methods

   /// <summary>
   /// Enforces the fair-use file cap for the shared hosted embedding service. A local model is uncapped and
   /// passes through untouched. When the cap is exceeded the UI hook decides whether to proceed with the
   /// first N files or cancel the whole run.
   /// </summary>
   private List<SourceFileInfo> ApplyHostedEmbeddingCap( List<SourceFileInfo> files )
   {
      if( _localEmbed != null || files.Count <= Config.HostedEmbeddingFileCap )
         return files;

      bool proceedCapped = OnHostedCapExceeded?.Invoke( files.Count ) ?? true;
      if( !proceedCapped )
         throw new OperationCanceledException();

      Console.WriteLine( $"[LIMIT] Embedding only the first {Config.HostedEmbeddingFileCap:N0} of {files.Count:N0} files (hosted embedding cap). Install a local model to embed everything." );
      return files.Take( Config.HostedEmbeddingFileCap ).ToList();
   }

   /// <summary>List source files, retrying on a transient empty result (the AzDO listing API is occasionally flaky).</summary>
   private async Task<List<SourceFileInfo>> ListFilesWithRetryAsync()
   {
      for( int attempt = 1; attempt <= ListingMaxAttempts; attempt++ )
      {
         var files = await _source.GetAllFilesAsync();
         if( files.Count > 0 ) return files;
         Console.WriteLine( $"[WARN] Listing returned 0 files (attempt {attempt}/{ListingMaxAttempts}) — retrying..." );
         await Task.Delay( ListingRetryBackoffMs * attempt );
      }
      Console.WriteLine( "[WARN] Listing still empty after retries." );
      return new List<SourceFileInfo>();
   }

   /// <summary>
   /// Wires up the source provider and embedding source from config. Source selection is GitHub, Azure Git,
   /// or (default) TFVC. Embedding prefers a configured LOCAL model (self-host, uncapped); if none is set it
   /// falls back to the hosted /embed service, and if neither is configured embedding is disabled.
   /// </summary>
   private void InitializeServices()
   {
      var filter = BuildSourceFilter();
      _source = BuildSourceProvider( filter );
      Console.WriteLine( $"         Source: {_source.SourceDescription}" );

      ConfigureEmbeddingSource();

      var vectorDbBuilder = new SqlConnectionStringBuilder( _connectionString );
      Console.WriteLine( $"         Vector DB: Server={vectorDbBuilder.DataSource};Database={vectorDbBuilder.InitialCatalog};" );
   }

   /// <summary>Parse the semicolon-delimited include/exclude glob config into a trimmed, non-empty filter set.</summary>
   private static SourceFilterOptions BuildSourceFilter()
   {
      return new SourceFilterOptions
      {
         IncludeGlobs = SplitGlobs( Config.IncludeGlobs ),
         ExcludeGlobs = SplitGlobs( Config.ExcludeGlobs )
      };
   }

   /// <summary>Split a semicolon-delimited glob string into a list of trimmed, non-empty entries.</summary>
   private static List<string> SplitGlobs( string globs )
   {
      return ( globs ?? "" )
         .Split( new[] { ';' }, StringSplitOptions.RemoveEmptyEntries )
         .Select( x => x.Trim() )
         .Where( x => x.Length > 0 )
         .ToList();
   }

   /// <summary>Construct the concrete source provider (GitHub, Azure Git, or TFVC) implied by config.</summary>
   private ISourceProvider BuildSourceProvider( SourceFilterOptions filter )
   {
      if( string.Equals( Config.SourceType, "github", StringComparison.OrdinalIgnoreCase ) )
      {
         _github = new GitHubService( Config.GitHubToken );
         return new GitHubSourceProvider( _github, Config.GitHubRepoUrl, Config.GitBranch, filter );
      }

      _azure = new AzureDevOpsService(
         Config.AzureUrl,
         Config.AzurePAT,
         Config.AzureProject,
         Config.AzureTfvcRoot );

      if( string.Equals( Config.SourceType, "git", StringComparison.OrdinalIgnoreCase ) )
         return new GitSourceProvider( _azure, Config.GitRepository, Config.GitBranch, filter );

      return new TfvcSourceProvider( _azure, filter );
   }

   /// <summary>
   /// Select the embedding source: a configured LOCAL model wins (self-host, uncapped); otherwise the hosted
   /// /embed service (shared demo, capped). Recipients only ever set a model PATH, never a URL, so the hosted
   /// URL is never printed to the indexer log.
   /// </summary>
   private void ConfigureEmbeddingSource()
   {
      if( Config.IsLocalModelConfigured )
      {
         _localEmbed = new EmbeddingService( Config.OnnxModelPath );
         Console.WriteLine( $"         Embeddings: LOCAL model ({Path.GetFileName( Config.OnnxModelPath )})" );
      }
      else if( Config.HuggingFaceEnabled )
      {
         _hfEmbedder = new HuggingFaceEmbedder( Config.HuggingFaceEmbedUrl, Config.HuggingFaceToken );
         Console.WriteLine( "         Embeddings: HUGGING FACE endpoint" );
         // The HF endpoint is scale-to-zero, so send the warm-up heartbeat NOW — the moment services come up,
         // before file listing — so its GPU spins up while the rest of setup runs. STEP 4 waits on this.
         var warmupStart = DateTime.Now;
         Console.WriteLine( "         Sending warm-up heartbeat to Hugging Face endpoint (waking scale-to-zero GPU)..." );
         _hfWarmup = Task.Run( async () =>
         {
            try
            {
               await _hfEmbedder.EmbedPassageAsync( "warmup" );
               Console.WriteLine( $"         Server heartbeat received — endpoint warm ({( DateTime.Now - warmupStart ).TotalSeconds:F0}s)" );
            }
            catch( Exception warmupException )
            {
               // Non-fatal: the per-chunk embeds retry cold starts on their own; this just logs the miss.
               Console.WriteLine( $"         Warm-up heartbeat failed ({warmupException.GetType().Name}); embed step will retry per chunk." );
            }
         } );
      }
      else if( !string.IsNullOrWhiteSpace( Config.EmbeddingServiceUrl ) )
      {
         _embedHttp = new System.Net.Http.HttpClient { Timeout = TimeSpan.FromMinutes( 3 ) };
         var embedBase = Config.EmbeddingServiceUrl.TrimEnd( '/' );
         _embedUrl = embedBase + "/embed";
         Console.WriteLine( "         Embeddings: REMOTE (hosted service)" );
      }
      else
      {
         Console.WriteLine( "         Embeddings: DISABLED (no local model and no embedding service configured)" );
      }
   }

   /// <summary>
   /// Fetches, Roslyn-parses, embeds, and writes every file in parallel. Each worker uses its own pooled
   /// SqlConnection so DB writes parallelize (SQL Server handles concurrent staging inserts; no vector index
   /// exists during a staging build). Fetch failures and chunk/embedding failures are counted and logged
   /// without aborting the run.
   /// </summary>
   private async Task IndexFilesAsync( List<SourceFileInfo> files, bool isFullReindex, CancellationToken cancellationToken = default )
   {
      int processedCount = 0, fetchErrorCount = 0;
      var chunkErrorCount = new System.Runtime.CompilerServices.StrongBox<int>( 0 );
      var startTime = DateTime.Now;
      var errorLogPath = Path.Combine( AppDomain.CurrentDomain.BaseDirectory, "chunkerrors.txt" );
      try { if( File.Exists( errorLogPath ) ) File.Delete( errorLogPath ); } catch { }
      var errorLogLock = new object();

      // Two bottlenecks, two settings. LOCAL embedding runs on THIS machine, so parallelize across its cores.
      // REMOTE embedding runs on the hosted server, so cap concurrency to the server's capacity regardless of how
      // many cores this client has — otherwise a strong client swamps a small server and throughput decays.
      var degreeOfParallelism = _localEmbed != null
         ? Math.Max( 2, Environment.ProcessorCount - 1 )
         : RemoteEmbedMaxConcurrency;
      var options = new ParallelOptions { MaxDegreeOfParallelism = degreeOfParallelism, CancellationToken = cancellationToken };
      // Progress cadence: fine for small runs (so a sub-500-file build still shows an ETA), coarser at scale.
      int progressEvery = files.Count <= 200 ? 25 : ( files.Count <= 2000 ? 100 : 500 );
      Console.WriteLine( $"         Embedding {files.Count:N0} files with degree-of-parallelism = {degreeOfParallelism}" );

      // The warm-up heartbeat was fired at service init (STEP 2). If it hasn't come back by now, say so —
      // otherwise the run looks frozen here — then wait for it before starting the session. The lambda swallows
      // its own errors, so this await never throws; it just gates the loop until the endpoint answers.
      if( _hfWarmup != null )
      {
         if( !_hfWarmup.IsCompleted )
            Console.WriteLine( "         Waiting for server heartbeat to start session..." );
         await _hfWarmup;
      }

      await Parallel.ForEachAsync( files, options, async ( file, token ) =>
      {
         try
         {
            var content = await _source.GetFileContentAsync( file );
            if( string.IsNullOrEmpty( content ) )
            {
               if( Interlocked.Increment( ref fetchErrorCount ) <= 5 ) Console.WriteLine( $"[WARN] No content: {file.RelativePath}" );
               return;
            }

            var metadata = RoslynMetadataExtractor.Extract( content );
            var ( author, addDate ) = await _source.GetBasicMetadataAsync( file );

            using var connection = new SqlConnection( _connectionString );
            await connection.OpenAsync( token );

            var codeFileId = UpsertCodeFile( connection, file.RelativePath, content, metadata, author, addDate, isFullReindex );

            if( ( _localEmbed != null || _hfEmbedder != null || _embedUrl != null ) && codeFileId > 0 )
               await ChunkAndEmbedFileAsync( connection, codeFileId, file, content, isFullReindex, errorLogPath, errorLogLock, chunkErrorCount );

            ReportProgress( Interlocked.Increment( ref processedCount ), files.Count, startTime, progressEvery );
         }
         catch( OperationCanceledException ) { throw; }
         catch( Exception exception )
         {
            if( Interlocked.Increment( ref fetchErrorCount ) <= 5 ) Console.WriteLine( $"[WARN] Error indexing {file.RelativePath}: {exception.Message}" );
         }
      } );

      if( fetchErrorCount > 5 ) Console.WriteLine( $"[WARN] ... and {fetchErrorCount - 5} more fetch errors" );
      if( chunkErrorCount.Value > 0 ) Console.WriteLine( $"[WARN] {chunkErrorCount.Value} total chunk/embedding errors" );
      Console.WriteLine( $"         Embedded {processedCount:N0} files" );
   }

   /// <summary>
   /// Roslyn-chunks a single file, embeds each chunk (capped at <see cref="MaxChunkChars"/> chars per chunk), and inserts it.
   /// Any failure here is isolated per file: it is counted, logged to the error file, and swallowed so one
   /// bad file never aborts the whole parallel run.
   /// </summary>
   private async Task ChunkAndEmbedFileAsync( SqlConnection connection, int codeFileId, SourceFileInfo file, string content, bool isFullReindex, string errorLogPath, object errorLogLock, System.Runtime.CompilerServices.StrongBox<int> chunkErrorCount )
   {
      try
      {
         // Single-embed (fastest measured: ~3 min local vs ~4.5 min batched — batching pads to the batch's
         // longest chunk and loses on this workload, so we embed one chunk per call).
         var chunks = new RoslynChunker().ChunkFile( file.RelativePath, content );
         foreach( var chunk in chunks )
         {
            var chunkContent = chunk.Content.Length > MaxChunkChars ? chunk.Content.Substring( 0, MaxChunkChars ) : chunk.Content;
            var embedding = await EmbedPassageSingleAsync( chunkContent );
            InsertCodeChunk( connection, codeFileId, chunk, chunkContent, embedding, isFullReindex );
         }
      }
      catch( Exception chunkException )
      {
         if( Interlocked.Increment( ref chunkErrorCount.Value ) <= 10 )
            Console.WriteLine( $"[CHUNK ERROR] {file.RelativePath}: {chunkException.GetType().Name}: {chunkException.Message}" );
         try { lock( errorLogLock ) { File.AppendAllText( errorLogPath, $"[{DateTime.Now:HH:mm:ss}] {file.RelativePath} | {chunkException.GetType().Name} | {chunkException.Message}{Environment.NewLine}" ); } } catch { }
      }
   }

   /// <summary>Write a throttled progress line with a live throughput rate and ETA at the configured cadence.</summary>
   private static void ReportProgress( int processedCount, int totalFiles, DateTime startTime, int progressEvery )
   {
      if( processedCount != 25 && processedCount % progressEvery != 0 ) return;

      var elapsed = DateTime.Now - startTime;
      var rate = processedCount / Math.Max( 0.01, elapsed.TotalMinutes );
      var remaining = ( totalFiles - processedCount ) / Math.Max( 1.0, rate );
      var eta = remaining >= 1 ? $"~{remaining:F0} min remaining" : "~<1 min remaining";
      Console.WriteLine( $"         Processed {processedCount:N0}/{totalFiles:N0} ({rate:F0}/min, {eta})" );
   }

   /// <summary>
   /// Embed a single passage via the plain /embed endpoint (one text, one vector) — used for the pre-sweep
   /// isolation test, since the original server exposes /embed but not /embed_batch.
   /// </summary>
   private async Task<float[]> EmbedPassageSingleAsync( string text )
   {
      if( _localEmbed != null )
         return _localEmbed.EmbedPassage( text );

      if( _hfEmbedder != null )
         return await _hfEmbedder.EmbedPassageAsync( text );

      var requestBody = new System.Net.Http.StringContent(
         JsonConvert.SerializeObject( new { text, kind = "passage" } ),
         System.Text.Encoding.UTF8, "application/json" );
      using var response = await _embedHttp.PostAsync( _embedUrl, requestBody );
      response.EnsureSuccessStatusCode();
      var json = await response.Content.ReadAsStringAsync();
      var vectorArray = Newtonsoft.Json.Linq.JObject.Parse( json )["vector"] as Newtonsoft.Json.Linq.JArray;
      return vectorArray?.Select( token => (float)token ).ToArray() ?? new float[0];
   }

   /// <summary>Delete a staging/live row by a single key column (daily-mode replace). Table + column are code literals, not user input.</summary>
   private void DeleteByKey( SqlConnection connection, string table, string keyColumn, object keyValue )
   {
      using var deleteCommand = new SqlCommand( $"DELETE FROM dbo.{table}{_tableSuffix} WHERE {keyColumn} = @key", connection );
      deleteCommand.Parameters.AddWithValue( "@key", keyValue );
      deleteCommand.ExecuteNonQuery();
   }

   /// <summary>Insert a dbo.CodeFiles row; returns its Id for the chunk FK. Daily mode deletes-by-path first.</summary>
   private int UpsertCodeFile( SqlConnection connection, string filePath, string content, FileMetadata metadata, string author, string addDate, bool isInsert )
   {
      if( !isInsert )
         DeleteByKey( connection, "CodeFiles", "FilePath", filePath );

      var sql = $@"
         INSERT INTO dbo.CodeFiles{_tableSuffix} (
            FilePath, Content, FileType, Namespace, ClassName, BaseClass, Interfaces, ClassModifiers, ClassNames,
            PropertyNames, MethodNames, Properties, Constructors, OverriddenMethods, AbstractMethods, VirtualMethods, AsyncMethods,
            EnumValues, Usings, Attributes, Regions, Constants, StaticFields, SqlOperations, Dictionaries, GenericTypes,
            Events, Delegates, ReferencedTypes,
            Author, FileAddDate, AllAuthors, CommitMessages, WorkItemTitles, WorkItemTags
         )
         OUTPUT INSERTED.Id
         VALUES (
            @file_path, @content, @file_type, @namespace, @class_name, @base_class, @interfaces, @class_modifiers, @class_names,
            @property_names, @method_names, @properties, @constructors, @overridden_methods, @abstract_methods, @virtual_methods, @async_methods,
            @enum_values, @usings, @attributes, @regions, @constants, @static_fields, @sql_operations, @dictionaries, @generic_types,
            @events, @delegates, @referenced_types,
            @author, @file_add_date, @all_authors, @commit_messages, @work_item_titles, @work_item_tags
         )";

      using var command = new SqlCommand( sql, connection );
      command.Parameters.AddWithValue( "@file_path", filePath );
      command.Parameters.AddWithValue( "@content", content ?? "" );
      command.Parameters.AddWithValue( "@file_type", metadata.FileType ?? "" );
      command.Parameters.AddWithValue( "@namespace", metadata.Namespace ?? "" );
      command.Parameters.AddWithValue( "@class_name", metadata.ClassName ?? "" );
      command.Parameters.AddWithValue( "@base_class", metadata.BaseClass ?? "" );
      command.Parameters.AddWithValue( "@interfaces", metadata.Interfaces ?? "" );
      command.Parameters.AddWithValue( "@class_modifiers", metadata.ClassModifiers ?? "" );
      command.Parameters.AddWithValue( "@class_names", metadata.ClassNames ?? "" );
      command.Parameters.AddWithValue( "@property_names", metadata.PropertyNames ?? "" );
      command.Parameters.AddWithValue( "@method_names", metadata.MethodNames ?? "" );
      command.Parameters.AddWithValue( "@properties", metadata.Properties ?? "" );
      command.Parameters.AddWithValue( "@constructors", metadata.Constructors ?? "" );
      command.Parameters.AddWithValue( "@overridden_methods", metadata.OverriddenMethods ?? "" );
      command.Parameters.AddWithValue( "@abstract_methods", metadata.AbstractMethods ?? "" );
      command.Parameters.AddWithValue( "@virtual_methods", metadata.VirtualMethods ?? "" );
      command.Parameters.AddWithValue( "@async_methods", metadata.AsyncMethods ?? "" );
      command.Parameters.AddWithValue( "@enum_values", metadata.EnumValues ?? "" );
      command.Parameters.AddWithValue( "@usings", metadata.Usings ?? "" );
      command.Parameters.AddWithValue( "@attributes", metadata.Attributes ?? "" );
      command.Parameters.AddWithValue( "@regions", metadata.Regions ?? "" );
      command.Parameters.AddWithValue( "@constants", metadata.Constants ?? "" );
      command.Parameters.AddWithValue( "@static_fields", metadata.StaticFields ?? "" );
      command.Parameters.AddWithValue( "@sql_operations", metadata.SqlOperations ?? "" );
      command.Parameters.AddWithValue( "@dictionaries", metadata.Dictionaries ?? "" );
      command.Parameters.AddWithValue( "@generic_types", metadata.GenericTypes ?? "" );
      command.Parameters.AddWithValue( "@events", metadata.Events ?? "" );
      command.Parameters.AddWithValue( "@delegates", metadata.Delegates ?? "" );
      command.Parameters.AddWithValue( "@referenced_types", metadata.ReferencedTypes ?? "" );
      command.Parameters.AddWithValue( "@author", author ?? "" );
      command.Parameters.AddWithValue( "@file_add_date", addDate ?? "" );
      // Rich VCS enrichment (multi-author / commit messages / work items) is a follow-up; empty for now.
      command.Parameters.AddWithValue( "@all_authors", "" );
      command.Parameters.AddWithValue( "@commit_messages", "" );
      command.Parameters.AddWithValue( "@work_item_titles", "" );
      command.Parameters.AddWithValue( "@work_item_tags", "" );

      return (int)command.ExecuteScalar();
   }

   /// <summary>Insert a chunk + its vector embedding into dbo.CodeChunks (with Roslyn context columns).</summary>
   private void InsertCodeChunk( SqlConnection connection, int codeFileId, CodeChunkDto chunk, string chunkContent, float[] embedding, bool isInsert )
   {
      var chunkKey = CapKey( chunk.GetId(), 500 );   // ChunkKey is NVARCHAR(500) UNIQUE
      var vectorJson = System.Text.Json.JsonSerializer.Serialize( embedding );

      if( !isInsert )
         DeleteByKey( connection, "CodeChunks", "ChunkKey", chunkKey );

      var sql = $@"
         INSERT INTO dbo.CodeChunks{_tableSuffix} (CodeFileId, ChunkKey, ChunkType, ChunkName, StartLine, EndLine, ChunkContent, Embedding, Namespace, ClassName, Signature, ParentContext)
         VALUES (@codeFileId, @chunkKey, @chunkType, @chunkName, @startLine, @endLine, @chunkContent, CAST(@embedding AS VECTOR(1024)), @namespace, @className, @signature, @parentContext)";

      using var command = new SqlCommand( sql, connection );
      command.Parameters.AddWithValue( "@codeFileId", codeFileId );
      command.Parameters.AddWithValue( "@chunkKey", chunkKey );
      command.Parameters.AddWithValue( "@chunkType", chunk.ChunkType ?? "" );
      command.Parameters.AddWithValue( "@chunkName", (object)Cap( chunk.ChunkName, 200 ) ?? DBNull.Value );
      command.Parameters.AddWithValue( "@startLine", chunk.StartLine );
      command.Parameters.AddWithValue( "@endLine", chunk.EndLine );
      command.Parameters.AddWithValue( "@chunkContent", (object)chunkContent ?? DBNull.Value );
      command.Parameters.AddWithValue( "@embedding", vectorJson );
      command.Parameters.AddWithValue( "@namespace", (object)Cap( chunk.Namespace, 500 ) ?? DBNull.Value );
      command.Parameters.AddWithValue( "@className", (object)Cap( chunk.ClassName, 200 ) ?? DBNull.Value );
      command.Parameters.AddWithValue( "@signature", (object)chunk.Signature ?? DBNull.Value );
      command.Parameters.AddWithValue( "@parentContext", (object)chunk.ParentContext ?? DBNull.Value );

      command.ExecuteNonQuery();
   }

   /// <summary>Truncate a value to fit its column.</summary>
   private static string Cap( string value, int max )
      => string.IsNullOrEmpty( value ) || value.Length <= max ? value : value.Substring( 0, max );

   /// <summary>Cap a UNIQUE key to max chars; if truncated, append a stable hash so it stays unique.</summary>
   private static string CapKey( string value, int max )
   {
      if( string.IsNullOrEmpty( value ) || value.Length <= max ) return value;
      var hash = StableHash( value );
      return value.Substring( 0, max - hash.Length - 1 ) + "#" + hash;
   }

   /// <summary>Deterministic FNV-1a hash (8 hex chars), stable across runs so daily delete-by-key still matches.</summary>
   private static string StableHash( string value )
   {
      unchecked
      {
         uint hash = 2166136261;
         foreach( var character in value ) { hash ^= character; hash *= 16777619; }
         return hash.ToString( "x8" );
      }
   }

   /// <summary>Format an elapsed span compactly for the completion line (e.g. "37s", "2m 18s", "1h 04m 12s").</summary>
   private static string FormatElapsed( TimeSpan elapsed )
      => elapsed.TotalHours >= 1 ? $"{(int)elapsed.TotalHours}h {elapsed.Minutes:D2}m {elapsed.Seconds:D2}s"
       : elapsed.TotalMinutes >= 1 ? $"{elapsed.Minutes}m {elapsed.Seconds:D2}s"
       : $"{elapsed.Seconds}s";

   /// <summary>Persist the current time (Unix seconds) so the daily incremental run knows the last full-index cutoff.</summary>
   private void SaveLastRunTime()
   {
      var lastRunFile = Path.Combine( AppDomain.CurrentDomain.BaseDirectory, "azdo_last_update.json" );
      var data = new Dictionary<string, double> { { "last_run", DateTimeOffset.Now.ToUnixTimeSeconds() } };
      File.WriteAllText( lastRunFile, JsonConvert.SerializeObject( data ) );
   }

   #endregion Private Methods
}
