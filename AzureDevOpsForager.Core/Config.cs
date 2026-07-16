using System.Collections.Generic;
using System.IO;
using AzureDevOpsForager.Core.Misc;

namespace AzureDevOpsForager.Core;

/// <summary>
/// Single source of truth for every runtime setting the AzureDevOpsForager tool-chain needs: on-disk paths,
/// SQL connection strings, embedding/reranker model locations, hosted-service URLs, and the Azure DevOps /
/// Git / GitHub source selection. All members are static so the three cooperating executables (the API
/// Server, the Indexer, and the desktop/web clients) read the same in-process values without passing a
/// settings object around. Defaults are seeded here, then layered over at startup by an optional per-exe
/// config.json and finally by a shared per-user override file so a choice the Indexer makes for the user
/// (a model path, a "use this DB") is automatically picked up by the Server and clients.
/// </summary>
public static class Config
{
   #region Data Members

   /// <summary>
   /// Root directory under which all AzureDevOpsForager working data lives. Created on demand by
   /// <see cref="EnsureDirectories"/> and overridable through config.json so a self-hoster can relocate it.
   /// </summary>
   public static string DataRoot { get; set; } = @"C:\AzureDevOpsForager";

   /// <summary>
   /// Filesystem path to the sentence-embedding ONNX model (E5-large-v2, 1024-dimensional output — the
   /// lightweight local option; the hosted demo embeds with the code-specialized bge-code-v1 instead).
   /// This is the model the Indexer and Server load to turn code chunks and queries into vectors; when it
   /// is present a self-hoster embeds locally instead of calling the hosted service. Running this local
   /// model requires setting <see cref="EmbeddingDimension"/> to 1024 to match its output.
   /// </summary>
   public static string OnnxModelPath { get; set; } = @"\models\e5-large-v2\e5-large-v2.onnx";

   /// <summary>
   /// Path to the tokenizer vocabulary (vocab.txt), always resolved alongside <see cref="OnnxModelPath"/> so
   /// the two files stay together no matter where the model is relocated. Derived, not independently settable.
   /// </summary>
   public static string TokenizerPath => Path.Combine( Path.GetDirectoryName( OnnxModelPath ), "vocab.txt" );

   /// <summary>
   /// Local source-code directory to index when indexing from disk rather than from Azure DevOps / Git / GitHub.
   /// Empty by default; the active source is governed by <see cref="SourceType"/>.
   /// </summary>
   public static string SourceDir { get; set; } = @"";

   /// <summary>
   /// Path to the on-disk cache of "known answers", the responses a user gave a thumbs-up to, kept under the
   /// per-user data root so accepted answers survive across runs and can be replayed.
   /// </summary>
   public static string KnownAnswersPath { get; set; } = System.IO.Path.Combine( LocalAppDataRoot, "known_answers.json" );

   /// <summary>
   /// Public download location for the embedding-model bundle (e5-large-v2 ONNX + vocab), used by the
   /// Indexer's "Download" wizard so a self-hoster never needs Python or a manual model export. Points at the
   /// give-away's GitHub Release asset; override via config.json if you mirror the model somewhere else.
   /// </summary>
   public static string ModelDownloadUrl { get; set; } = "https://adforagerdl74790.blob.core.windows.net/models/onyx.zip";

   /// <summary>
   /// Per-user data root under %LOCALAPPDATA% shared by every component: the single place the app-data folder
   /// name lives, so paths like <see cref="KnownAnswersPath"/> and <see cref="SharedUserConfigPath"/> stay consistent.
   /// </summary>
   public static string LocalAppDataRoot => Path.Combine(
      System.Environment.GetFolderPath( System.Environment.SpecialFolder.LocalApplicationData ), "AzureDevOpsForager" );

   /// <summary>
   /// Per-user override file shared by ALL apps (Server, Indexer, desktop client), layered on top of each
   /// exe's own config.json. This is where the Indexer persists a chosen model path or "use this DB" so the
   /// local Server and clients pick them up automatically, with no manual file editing by the user.
   /// </summary>
   public static string SharedUserConfigPath => Path.Combine( LocalAppDataRoot, "config.json" );

   /// <summary>
   /// True when a usable local embedding model is configured, meaning its .onnx file actually exists on disk.
   /// Callers use this to decide between local embedding and the hosted embedding service.
   /// </summary>
   public static bool IsLocalModelConfigured =>
      !string.IsNullOrWhiteSpace( OnnxModelPath ) && File.Exists( OnnxModelPath );

   /// <summary>
   /// TCP port the API Server listens on.
   /// </summary>
   public static int Port { get; set; } = 8000;

   /// <summary>
   /// Network interface the API Server binds to; 0.0.0.0 means all interfaces.
   /// </summary>
   public static string Host { get; set; } = "0.0.0.0";

   /// <summary>
   /// Full base URL of the locally-hosted API, composed from <see cref="Host"/> and <see cref="Port"/>.
   /// </summary>
   public static string BaseUrl => $"http://{Host}:{Port}";

   /// <summary>
   /// URL of the Forager Server that the thin clients (web UI + desktop chat) call for /query and /chat.
   /// Defaults to the hosted demo server (zero-config client); override via config.json to point at your own Server.
   /// </summary>
   public static string ServerUrl { get; set; } = "https://azuredevops.aidataforager.com";

   /// <summary>Optional explicit path to the Server executable, letting a client auto-start a local Server
   /// when a local index is configured. Blank =&gt; the launcher probes next to the client. Set per-deployment.</summary>
   public static string ServerExePath { get; set; } = "";

   /// <summary>
   /// URL of the hosted embedding service (a running Server's /embed endpoint). The indexer embeds every chunk
   /// remotely through this service when no local model is configured. Defaults to the hosted demo Server;
   /// override via config.json for a self-hosted Server.
   /// </summary>
   public static string EmbeddingServiceUrl { get; set; } = "https://azuredevopsforager.azurewebsites.net";

   /// <summary>
   /// Hugging Face Inference Endpoint URL for embeddings (BAAI/bge-code-v1, a code-specialized 1536-dim
   /// embedder served by TEI). When set together with a token, the Server and Indexer embed via HTTP to
   /// this endpoint instead of loading the local ONNX model. Blank = disabled. Not a secret (the token
   /// protects it); set per-deployment via config.json or the HUGGINGFACE_EMBED_URL environment variable
   /// (config.json wins when both are present).
   /// </summary>
   public static string HuggingFaceEmbedUrl { get; set; } =
      System.Environment.GetEnvironmentVariable( "HUGGINGFACE_EMBED_URL" ) ?? "";

   /// <summary>
   /// Hugging Face Inference Endpoint URL for reranking (Qwen3-Reranker-4B in its sequence-classification
   /// form, served by vLLM). The client appends the "/rerank" route (Jina-compatible API). When set with a
   /// token, ranking runs remotely instead of via the local ONNX reranker. Also settable via the
   /// HUGGINGFACE_RERANK_URL environment variable (config.json wins when both are set).
   /// </summary>
   public static string HuggingFaceRerankUrl { get; set; } =
      System.Environment.GetEnvironmentVariable( "HUGGINGFACE_RERANK_URL" ) ?? "";

   /// <summary>
   /// Instruction sent on the query side of a bge-code-v1 embedding request, using the model's
   /// "&lt;instruct&gt;{task}\n&lt;query&gt;{text}" prompt format. Documents/passages are embedded raw (no
   /// instruction), per the model card. Only the hosted (Hugging Face) embed path uses this; the local E5
   /// model keeps its own "query: " / "passage: " prefixes.
   /// </summary>
   public static string EmbeddingQueryInstruction { get; set; } =
      "Given a code search query, retrieve relevant code that answers the query.";

   /// <summary>
   /// Task instruction baked into every Qwen3-Reranker scoring prompt (the model card warns that scoring
   /// without an instruction costs 1-5% accuracy, so a code-search-specific one is supplied by default).
   /// Only the hosted (Hugging Face) rerank path uses this; the local bge reranker takes no instruction.
   /// </summary>
   public static string RerankerInstruction { get; set; } =
      "Given a code search query, retrieve relevant code chunks that answer the query.";

   /// <summary>
   /// Model name sent in the hosted rerank request body. vLLM's OpenAI-compatible /rerank route expects
   /// the served model's name; this default matches the deployed sequence-classification conversion.
   /// </summary>
   public static string RerankerModelName { get; set; } = "tomaarsen/Qwen3-Reranker-4B-seq-cls";

   /// <summary>
   /// The Hugging Face API token, resolved from the HF_TOKEN environment variable or the encrypted
   /// secrets.enc. Never stored in source or plain config. Null/empty means HF is not authorized.
   /// </summary>
   public static string HuggingFaceToken => AzureDevOpsForager.Core.Services.Utilities.SecretStore.Get( "HF_TOKEN" );

   /// <summary>
   /// True when both a HF embed URL and a token are present — the gate for routing embeddings/ranking to HF.
   /// </summary>
   public static bool HuggingFaceEnabled => !string.IsNullOrWhiteSpace( HuggingFaceEmbedUrl ) && !string.IsNullOrWhiteSpace( HuggingFaceToken );

   /// <summary>
   /// Max files a single indexing run may push through the hosted (shared) embedding service before the
   /// Indexer warns and offers to index only the top N. Protects the shared demo /embed from very large jobs;
   /// ignored entirely when a local model is configured (self-hosters embed locally, uncapped).
   /// </summary>
   public static int HostedEmbeddingFileCap { get; set; } = 1000;

   /// <summary>
   /// Maximum number of characters kept per file when building content for embedding, a rough guard so a single
   /// large file cannot blow the model's token budget.
   /// </summary>
   public static int MaxContentLength { get; set; } = 1500;

   /// <summary>
   /// Reciprocal-rank-fusion weight applied to the vector-search ranking within the dbo.SearchCode fusion.
   /// </summary>
   public static int RrfVectorWeight { get; set; } = 60;

   /// <summary>
   /// Reciprocal-rank-fusion weight applied to the chunk-level full-text-search ranking within dbo.SearchCode.
   /// </summary>
   public static int RrfChunkFtsWeight { get; set; } = 30;

   /// <summary>
   /// Reciprocal-rank-fusion weight applied to the file-level full-text-search ranking within dbo.SearchCode.
   /// </summary>
   public static int RrfFileFtsWeight { get; set; } = 30;

   /// <summary>
   /// Minimum FREETEXT rank a full-text-search hit must clear before it is allowed into the fusion set, filtering
   /// out weak lexical matches.
   /// </summary>
   public static int MinFtsRank { get; set; } = 10;

   /// <summary>
   /// Maximum cosine distance (0..2) a vector candidate may have to enter fusion; larger distances are too
   /// dissimilar to be worth ranking.
   /// </summary>
   public static double MaxVectorDistance { get; set; } = 0.5;

   /// <summary>
   /// Path to the bge-reranker-v2-m3 cross-encoder ONNX model (its sentencepiece.bpe.model is expected
   /// alongside). A blank value disables reranking entirely.
   /// </summary>
   public static string RerankerModelPath { get; set; } = @"models\bge-reranker-v2-m3-onnx\model.onnx";

   /// <summary>
   /// Enables the cross-encoder reranker second stage that re-scores the fused candidates for relevance.
   /// </summary>
   public static bool RerankerEnabled { get; set; } = true;

   /// <summary>
   /// Size of the candidate pool handed to the reranker: the search over-fetches from RRF up to this many
   /// results, reranks them, then caps back down to the requested result count.
   /// </summary>
   public static int RerankerInputSize { get; set; } = 30;

   /// <summary>
   /// SQL Server connection string for the code database the Server reads from when answering queries. Left
   /// blank by default and supplied through config.json / user overrides.
   /// </summary>
   public static string SqlConnectionString { get; set; } =
      AzureDevOpsForager.Core.Services.Utilities.SecretStore.Get( "SQL_CONNECTION_STRING" ) ?? "";

   /// <summary>
   /// SQL Server connection string for the vector-index database the Indexer writes embeddings into. Left blank
   /// by default and supplied through config.json / user overrides.
   /// </summary>
   public static string AzdoVectorConnectionString { get; set; } =
      AzureDevOpsForager.Core.Services.Utilities.SecretStore.Get( "AZDO_VECTOR_CONNECTION_STRING" ) ?? "";

   /// <summary>
   /// Dimensionality of the embedding vectors stored in the index; must match the embedding model in use
   /// (bge-code-v1 = 1536, the hosted default; the lightweight local E5-large-v2 = 1024). This value flows
   /// into the VECTOR(n) column DDL, the DiskANN index, and the SearchCode procedure's parameter, so
   /// changing the embedding model means changing this AND running a full reindex.
   /// </summary>
   public static int EmbeddingDimension { get; set; } = 1536;

   /// <summary>
   /// Azure DevOps organization URL, e.g. https://dev.azure.com/your-org. Seeded from the environment via
   /// <see cref="Global.AzureDevOpsUrl"/>; leaving it empty disables the Azure integration.
   /// </summary>
   public static string AzureUrl { get; set; } = Global.AzureDevOpsUrl;

   /// <summary>
   /// Azure DevOps Personal Access Token used to authenticate against the organization. Seeded from the
   /// environment via <see cref="Global.AzureDevOpsPat"/> so the secret is not baked into source.
   /// </summary>
   public static string AzurePAT { get; set; } = Global.AzureDevOpsPat;

   /// <summary>
   /// Azure DevOps project name (e.g. MyProject) within the organization.
   /// </summary>
   public static string AzureProject { get; set; } = "";

   /// <summary>
   /// TFVC root path to index, given without the leading "$/".
   /// </summary>
   public static string AzureTfvcRoot { get; set; } = "";

   /// <summary>
   /// True when Azure DevOps integration is usable, meaning both an org URL and a PAT have been supplied.
   /// </summary>
   public static bool AzureEnabled => !string.IsNullOrEmpty( AzureUrl ) && !string.IsNullOrEmpty( AzurePAT );

   /// <summary>
   /// Which source the Indexer reads from: "tfvc" (Azure DevOps TFVC, the default), "git" (Azure DevOps Git),
   /// or "github". Selects which of the source-specific settings below take effect.
   /// </summary>
   public static string SourceType { get; set; } = "tfvc";

   /// <summary>
   /// Git repository name or id, used when <see cref="SourceType"/> is "git".
   /// </summary>
   public static string GitRepository { get; set; } = "";

   /// <summary>
   /// Branch to index for the "git" / "github" sources; an empty value means the repository's default branch.
   /// </summary>
   public static string GitBranch { get; set; } = "";

   /// <summary>
   /// GitHub repository URL (e.g. https://github.com/owner/repo), used when <see cref="SourceType"/> is "github".
   /// Defaults to the public demo repo and is overridable.
   /// </summary>
   public static string GitHubRepoUrl { get; set; } = "https://github.com/dotnet-architecture/eShopOnWeb";

   /// <summary>
   /// Optional GitHub token; a blank value indexes a public repo at the unauthenticated rate limit.
   /// </summary>
   public static string GitHubToken { get; set; } = "";

   /// <summary>
   /// Semicolon-separated include globs deciding which files are indexed. Defaults to all C# files.
   /// </summary>
   public static string IncludeGlobs { get; set; } = "**/*.cs";

   /// <summary>
   /// Semicolon-separated exclude globs deciding which files are skipped. Defaults to build output folders.
   /// </summary>
   public static string ExcludeGlobs { get; set; } = "**/bin/**;**/obj/**";

   #endregion Data Members

   #region Public Methods

   /// <summary>
   /// Ensures the working directories the app needs exist, creating the data root and the model's containing
   /// folder. Each create is wrapped so a failure on one (e.g. a permission or path issue) does not stop the other.
   /// </summary>
   public static void EnsureDirectories()
   {
      try { Directory.CreateDirectory( DataRoot ); } catch { }
      try { Directory.CreateDirectory( Path.GetDirectoryName( OnnxModelPath ) ); } catch { }
   }

   /// <summary>
   /// Loads settings from a JSON config file (a flat string-to-string map) and applies each recognized key over
   /// the current defaults. Missing file, malformed JSON, or a null map are all treated as "nothing to override"
   /// so startup never fails on a bad config; any parse error simply leaves the defaults in place.
   /// </summary>
   /// <param name="configPath">Path to the config.json to read; ignored silently if it does not exist.</param>
   public static void LoadFromFile( string configPath )
   {
      if( !File.Exists( configPath ) )
         return;

      try
      {
         var json = File.ReadAllText( configPath );
         var config = Newtonsoft.Json.JsonConvert.DeserializeObject<Dictionary<string, string>>( json );

         if( config == null )
            return;

         ApplyPathAndModelSettings( config );
         ApplyServerSettings( config );
         ApplyAzureSettings( config );
         ApplySourceSelectionSettings( config );
         ApplySearchTuningSettings( config );
      }
      catch
      {
         // Bad file or JSON: keep whatever defaults are already in effect.
      }
   }

   /// <summary>
   /// Loads the shared per-user override file (<see cref="SharedUserConfigPath"/>) on top of whatever the
   /// per-exe config.json already set. Every app calls this at startup so a value the Indexer wrote for the
   /// user (a chosen model path, a "use this DB" choice) takes precedence and is seen across all the exes.
   /// </summary>
   public static void LoadUserOverrides() => LoadFromFile( SharedUserConfigPath );

   /// <summary>
   /// Persists a single key into the shared per-user override file, creating it (and its folder) if needed and
   /// preserving any other keys already there. This is how the Indexer "sets it for you, poof, overridden"
   /// without the user ever hand-editing a config file. Write failures are swallowed since the override layer is
   /// only a convenience.
   /// </summary>
   /// <param name="key">The override key to set (must match a key <see cref="LoadFromFile"/> recognizes to take effect).</param>
   /// <param name="value">The value to store for that key.</param>
   public static void SaveUserOverride( string key, string value )
   {
      try
      {
         var path = SharedUserConfigPath;
         Directory.CreateDirectory( Path.GetDirectoryName( path ) );

         // Merge into any existing overrides so we never clobber a previously-saved key (e.g. model path + DB).
         var map = new Dictionary<string, string>();
         if( File.Exists( path ) )
         {
            var existing = Newtonsoft.Json.JsonConvert.DeserializeObject<Dictionary<string, string>>( File.ReadAllText( path ) );
            if( existing != null ) map = existing;
         }

         map[key] = value;
         File.WriteAllText( path, Newtonsoft.Json.JsonConvert.SerializeObject( map, Newtonsoft.Json.Formatting.Indented ) );
      }
      catch
      {
         // Non-fatal: the override layer is a convenience; a write failure just means the value isn't persisted.
      }
   }

   #endregion Public Methods

   #region Private Methods

   /// <summary>
   /// Applies the path, model, and hosted-embedding keys from a parsed config map onto the corresponding
   /// properties. Only keys present in the map change anything, leaving everything else at its default.
   /// </summary>
   /// <param name="config">The parsed config map to read override values from.</param>
   private static void ApplyPathAndModelSettings( Dictionary<string, string> config )
   {
      if( config.TryGetValue( "DataRoot", out var dataRoot ) )
         DataRoot = dataRoot;
      if( config.TryGetValue( "SourceDir", out var sourceDir ) )
         SourceDir = sourceDir;
      if( config.TryGetValue( "OnnxModelPath", out var modelPath ) )
         OnnxModelPath = modelPath;
      if( config.TryGetValue( "ModelDownloadUrl", out var modelDownloadUrl ) )
         ModelDownloadUrl = modelDownloadUrl;
      if( config.TryGetValue( "EmbeddingDimension", out var embeddingDimensionText ) && int.TryParse( embeddingDimensionText, out var embeddingDimension ) )
         EmbeddingDimension = embeddingDimension;
      if( config.TryGetValue( "EmbeddingQueryInstruction", out var embeddingQueryInstruction ) && !string.IsNullOrWhiteSpace( embeddingQueryInstruction ) )
         EmbeddingQueryInstruction = embeddingQueryInstruction;
      if( config.TryGetValue( "HostedEmbeddingFileCap", out var fileCapText ) && int.TryParse( fileCapText, out var fileCap ) )
         HostedEmbeddingFileCap = fileCap;
   }

   /// <summary>
   /// Applies the server, connection-string, and hosted-service-URL keys from a parsed config map. Numeric keys
   /// are only applied when they parse, so a garbled value falls back to the existing default rather than throwing.
   /// </summary>
   /// <param name="config">The parsed config map to read override values from.</param>
   private static void ApplyServerSettings( Dictionary<string, string> config )
   {
      if( config.TryGetValue( "Port", out var portText ) && int.TryParse( portText, out var port ) )
         Port = port;
      if( config.TryGetValue( "SqlConnectionString", out var sqlConnectionString ) && !string.IsNullOrWhiteSpace( sqlConnectionString ) )
         SqlConnectionString = sqlConnectionString;
      if( config.TryGetValue( "AzdoVectorConnectionString", out var vectorConnectionString ) && !string.IsNullOrWhiteSpace( vectorConnectionString ) )
         AzdoVectorConnectionString = vectorConnectionString;
      if( config.TryGetValue( "ServerUrl", out var serverUrl ) )
         ServerUrl = serverUrl;
      if( config.TryGetValue( "EmbeddingServiceUrl", out var embeddingServiceUrl ) )
         EmbeddingServiceUrl = embeddingServiceUrl;
      if( config.TryGetValue( "HuggingFaceEmbedUrl", out var huggingFaceEmbedUrl ) )
         HuggingFaceEmbedUrl = huggingFaceEmbedUrl;
      if( config.TryGetValue( "HuggingFaceRerankUrl", out var huggingFaceRerankUrl ) )
         HuggingFaceRerankUrl = huggingFaceRerankUrl;
      if( config.TryGetValue( "ServerExePath", out var serverExePath ) )
         ServerExePath = serverExePath;
   }

   /// <summary>
   /// Applies the Azure DevOps connection keys (org URL, PAT, project, TFVC root) from a parsed config map,
   /// overriding the environment-seeded defaults when present.
   /// </summary>
   /// <param name="config">The parsed config map to read override values from.</param>
   private static void ApplyAzureSettings( Dictionary<string, string> config )
   {
      if( config.TryGetValue( "AzureUrl", out var azureUrl ) )
         AzureUrl = azureUrl;
      if( config.TryGetValue( "AzurePAT", out var azurePat ) )
         AzurePAT = azurePat;
      if( config.TryGetValue( "AzureProject", out var azureProject ) )
         AzureProject = azureProject;
      if( config.TryGetValue( "AzureTfvcRoot", out var azureTfvcRoot ) )
         AzureTfvcRoot = azureTfvcRoot;
   }

   /// <summary>
   /// Applies the source-selection keys (which source to index and its git/github specifics plus include/exclude
   /// globs) from a parsed config map.
   /// </summary>
   /// <param name="config">The parsed config map to read override values from.</param>
   private static void ApplySourceSelectionSettings( Dictionary<string, string> config )
   {
      if( config.TryGetValue( "SourceType", out var sourceType ) )
         SourceType = sourceType;
      if( config.TryGetValue( "GitRepository", out var gitRepository ) )
         GitRepository = gitRepository;
      if( config.TryGetValue( "GitBranch", out var gitBranch ) )
         GitBranch = gitBranch;
      if( config.TryGetValue( "GitHubRepoUrl", out var gitHubRepoUrl ) )
         GitHubRepoUrl = gitHubRepoUrl;
      if( config.TryGetValue( "GitHubToken", out var gitHubToken ) )
         GitHubToken = gitHubToken;
      if( config.TryGetValue( "IncludeGlobs", out var includeGlobs ) )
         IncludeGlobs = includeGlobs;
      if( config.TryGetValue( "ExcludeGlobs", out var excludeGlobs ) )
         ExcludeGlobs = excludeGlobs;
   }

   /// <summary>
   /// Applies the optional reranker and RRF-fusion tuning keys from a parsed config map. Numeric and boolean
   /// keys are guarded by TryParse so an unparseable value leaves the tuned default untouched; the distance
   /// threshold is parsed with invariant culture so a config authored under any locale reads consistently.
   /// </summary>
   /// <param name="config">The parsed config map to read override values from.</param>
   private static void ApplySearchTuningSettings( Dictionary<string, string> config )
   {
      if( config.TryGetValue( "RerankerModelPath", out var rerankerModelPath ) )
         RerankerModelPath = rerankerModelPath;
      if( config.TryGetValue( "RerankerInstruction", out var rerankerInstruction ) && !string.IsNullOrWhiteSpace( rerankerInstruction ) )
         RerankerInstruction = rerankerInstruction;
      if( config.TryGetValue( "RerankerModelName", out var rerankerModelName ) && !string.IsNullOrWhiteSpace( rerankerModelName ) )
         RerankerModelName = rerankerModelName;
      if( config.TryGetValue( "RerankerEnabled", out var rerankerEnabledText ) && bool.TryParse( rerankerEnabledText, out var rerankerEnabled ) )
         RerankerEnabled = rerankerEnabled;
      if( config.TryGetValue( "RerankerInputSize", out var rerankerInputSizeText ) && int.TryParse( rerankerInputSizeText, out var rerankerInputSize ) )
         RerankerInputSize = rerankerInputSize;
      if( config.TryGetValue( "RrfVectorWeight", out var vectorWeightText ) && int.TryParse( vectorWeightText, out var vectorWeight ) )
         RrfVectorWeight = vectorWeight;
      if( config.TryGetValue( "RrfChunkFtsWeight", out var chunkWeightText ) && int.TryParse( chunkWeightText, out var chunkWeight ) )
         RrfChunkFtsWeight = chunkWeight;
      if( config.TryGetValue( "RrfFileFtsWeight", out var fileWeightText ) && int.TryParse( fileWeightText, out var fileWeight ) )
         RrfFileFtsWeight = fileWeight;
      if( config.TryGetValue( "MinFtsRank", out var minFtsRankText ) && int.TryParse( minFtsRankText, out var minFtsRank ) )
         MinFtsRank = minFtsRank;
      if( config.TryGetValue( "MaxVectorDistance", out var maxVectorDistanceText ) && double.TryParse( maxVectorDistanceText, System.Globalization.NumberStyles.Float, System.Globalization.CultureInfo.InvariantCulture, out var maxVectorDistance ) )
         MaxVectorDistance = maxVectorDistance;
   }

   #endregion Private Methods
}
