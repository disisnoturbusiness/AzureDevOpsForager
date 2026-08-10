using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Threading.Tasks;
using AzureDevOpsForager.Core.Models.API;
using AzureDevOpsForager.Core.Models.Search;
using AzureDevOpsForager.Core.Services.Embedding;
using AzureDevOpsForager.Core.Services.Reranking;
using Microsoft.Data.SqlClient;

namespace AzureDevOpsForager.Core.Services.Search;

/// <summary>
/// Coordinates a hybrid code search that blends SQL Server full-text (keyword) search with
/// vector semantic search so users can find files by exact terms and by meaning at once.
///
/// The preferred path pushes all three signals (vector similarity, chunk-level full-text, and
/// file-level full-text) into a single stored procedure that fuses them with Reciprocal Rank
/// Fusion, which keeps ranking logic on the database side and cuts the query to one round-trip.
/// When embeddings or the vector index are unavailable, the service degrades to a full-text-only
/// path so search keeps working rather than failing outright. An optional cross-encoder reranker
/// can reorder the shortlist for higher precision. The service is deliberately language-generic:
/// it carries no domain or vendor routing, so it works uniformly across whatever content is indexed.
/// </summary>
public class HybridSearchService : IDisposable
{
   #region Data Members

   /// <summary>
   /// Minimum trimmed length a question must have before it is used as a filename search term in the
   /// full-text-only fallback. Below this, the LIKE pattern SearchByFilename builds is too broad to be
   /// meaningful (an empty term becomes LIKE '%%', matching every file), so the filename merge is skipped.
   /// </summary>
   private const int MinFilenameSearchLength = 2;

   /// <summary>Full-text (keyword) search path and filename lookups. Owned and disposed here.</summary>
   private readonly SqlFtsService _ftsService;

   /// <summary>Vector similarity path, used here for collection health/stats. Owned and disposed here.</summary>
   private readonly SqlVectorService _vectorService;

   /// <summary>
   /// Turns the user's question into a query embedding (local ONNX or remote HF). May be null when
   /// embeddings are not available, in which case the service runs the full-text-only fallback path.
   /// </summary>
   private readonly IEmbedder _embeddingService;

   /// <summary>
   /// Optional second-stage cross-encoder reranker. Null means no rerank stage; even when set,
   /// reranking only runs if it is also enabled in configuration.
   /// </summary>
   private readonly IReranker _reranker;

   /// <summary>Guards against double-dispose of the owned search services.</summary>
   private bool _disposed;

   #endregion Data Members

   #region Constructor

   /// <summary>
   /// Builds the hybrid search service. The full-text and vector services default to fresh
   /// instances when not supplied (the normal runtime case); tests inject fakes instead. The
   /// embedding service and reranker are intentionally optional so the service can run in a
   /// degraded, full-text-only mode when either is unavailable.
   /// </summary>
   public HybridSearchService( SqlFtsService fts = null, SqlVectorService vector = null, IEmbedder embeddings = null, IReranker reranker = null )
   {
      _ftsService = fts ?? new SqlFtsService();
      _vectorService = vector ?? new SqlVectorService();
      _embeddingService = embeddings; // may be null if embeddings aren't available
      _reranker = reranker;           // may be null => no second-stage rerank
   }

   #endregion Constructor

   #region Public Methods

   /// <summary>
   /// Runs the main hybrid search for a request. When embeddings are available it takes the
   /// preferred path: embed the question, then let the stored procedure fuse vector + chunk-FTS
   /// + file-FTS in one round-trip. If embedding or the proc/vector index fails, it falls back to
   /// a full-text-only search (keyword hits merged with filename hits) so the caller still gets
   /// results. Any unexpected failure is captured on the response rather than thrown.
   /// </summary>
   public async Task<SearchResponse> SearchAsync( SearchRequest request )
   {
      var response = new SearchResponse();
      try
      {
         if( _embeddingService != null )
         {
            try
            {
               var queryVector = await _embeddingService.EmbedQueryAsync( request.Question );
               return await SearchViaProcAsync( queryVector, request );
            }
            catch( Exception vectorException )
            {
               Log( $"[SEARCH] RRF proc path failed, falling back to FTS-only: {vectorException.Message}" );
            }
         }

         // Fallback: FTS-only (no embeddings, or the proc / vector index is unavailable).
         return BuildFtsOnlyResponse( request, response );
      }
      catch( Exception exception )
      {
         response.Error = exception.Message;
      }

      return response;
   }

   /// <summary>
   /// Searches by filename substring only, bypassing content and vector ranking. Useful when the
   /// user already knows roughly what the file is called. Failures are captured on the response.
   /// </summary>
   public SearchResponse SearchByFilename( string filename, int nResults = 5 )
   {
      var response = new SearchResponse();

      try
      {
         var results = _ftsService.SearchByFilename( filename, nResults );

         response.Ids = new List<List<string>> { results.Select( r => r.FilePath ).ToList() };
         response.Documents = new List<List<string>> { results.Select( r => TruncateContent( r.Content ) ).ToList() };
         response.Metadatas = new List<List<Dictionary<string, string>>> { results.Select( r => r.ToMetadata() ).ToList() };
      }
      catch( Exception exception )
      {
         response.Error = exception.Message;
      }

      return response;
   }

   /// <summary>
   /// Reports database health and index sizes: the full-text file count plus the vector
   /// collection's point count and status. Marks the response "healthy" on success, or "error"
   /// with the message on any failure, so a caller can surface a simple status without throwing.
   /// </summary>
   public async Task<HealthResponse> GetHealthAsync()
   {
      var health = new HealthResponse();

      try
      {
         health.FtsFileCount = _ftsService.GetFileCount();

         var vectorInfo = await _vectorService.GetCollectionInfoAsync();
         health.VectorPointCount = vectorInfo.PointCount;
         health.VectorStatus = vectorInfo.Status;

         // The overall verdict must follow the vector store rather than merely reporting that no
         // exception was thrown. Previously this was an unconditional "healthy", so a Red or Error
         // vector store still surfaced as a healthy service — hiding the one failure that matters
         // most, since a dead vector leg degrades search to full-text without any visible error.
         health.Status = vectorInfo.Status == "Green" ? "healthy" : "degraded";
         if( vectorInfo.Status != "Green" )
            health.Error = vectorInfo.Detail;
      }
      catch( Exception exception )
      {
         health.Status = "error";
         health.Error = exception.Message;
      }

      return health;
   }

   /// <summary>
   /// Releases the owned search services. Safe to call more than once; the disposed guard makes
   /// repeat calls a no-op. This satisfies <see cref="IDisposable"/>; it is an interface
   /// implementation, not an override.
   /// </summary>
   public void Dispose()
   {
      if( _disposed )
         return;

      _ftsService?.Dispose();
      _vectorService?.Dispose();
      ( _embeddingService as IDisposable )?.Dispose();
      _disposed = true;
   }

   #endregion Public Methods

   #region Private Methods

   /// <summary>
   /// Preferred search path: server-side Reciprocal Rank Fusion via dbo.SearchCode, followed by
   /// an optional cross-encoder rerank. When reranking is on, it over-fetches a wider candidate
   /// pool so the reranker has more to work with, then caps the final list back to NResults.
   /// </summary>
   private async Task<SearchResponse> SearchViaProcAsync( float[] queryVector, SearchRequest request )
   {
      var json = System.Text.Json.JsonSerializer.Serialize( queryVector );
      var doRerank = _reranker != null && Config.RerankerEnabled;

      // Over-fetch a bigger pool only when a rerank stage will trim it back down.
      var fetchN = doRerank ? Math.Max( request.NResults, Config.RerankerInputSize ) : request.NResults;

      var rows = await FetchFusedRowsAsync( json, request.Question, fetchN );

      if( doRerank && rows.Count > 1 )
         rows = await ApplyRerankAsync( request.Question, rows, request.NResults );

      var top = rows.Take( request.NResults ).ToList();
      return new SearchResponse
      {
         Ids = new List<List<string>> { top.Select( t => t.FilePath ).ToList() },
         Documents = new List<List<string>> { top.Select( ( t, i ) => i == 0 ? t.Content : TruncateContent( t.Content ) ).ToList() },
         Metadatas = new List<List<Dictionary<string, string>>> { top.Select( t => t.Meta ).ToList() },
      };
   }

   /// <summary>
   /// Calls dbo.SearchCode with the serialized query vector, search text, and the configured RRF
   /// weights/thresholds, then materializes each returned row into a (path, content, metadata)
   /// tuple. The metadata keys are the API's public snake_case contract and are kept verbatim.
   /// </summary>
   private async Task<List<(string FilePath, string Content, Dictionary<string, string> Meta)>> FetchFusedRowsAsync( string json, string question, int fetchN )
   {
      var rows = new List<(string FilePath, string Content, Dictionary<string, string> Meta)>();

      using( var connection = AzureDevOpsForager.Core.Services.Utilities.SqlResilience.CreateConnection(Config.SqlConnectionString ) )
      {
         await connection.OpenAsync();
         using var command = new SqlCommand(
            $"DECLARE @qv VECTOR({Config.EmbeddingDimension}) = CAST(@json AS VECTOR({Config.EmbeddingDimension})); " +
            "EXEC dbo.SearchCode @SearchText=@txt, @QueryVector=@qv, @TopN=@topN, " +
            "@VectorWeight=@vw, @ChunkFtsWeight=@cw, @FileFtsWeight=@fw, @MinFtsRank=@minRank, @MaxDistance=@maxDist;", connection );
         command.Parameters.AddWithValue( "@json", json );
         command.Parameters.AddWithValue( "@txt", (object)question ?? "" );
         command.Parameters.AddWithValue( "@topN", fetchN );
         command.Parameters.AddWithValue( "@vw", Config.RrfVectorWeight );
         command.Parameters.AddWithValue( "@cw", Config.RrfChunkFtsWeight );
         command.Parameters.AddWithValue( "@fw", Config.RrfFileFtsWeight );
         command.Parameters.AddWithValue( "@minRank", Config.MinFtsRank );
         command.Parameters.AddWithValue( "@maxDist", Config.MaxVectorDistance );

         using var reader = await command.ExecuteReaderAsync();
         while( await reader.ReadAsync() )
         {
            var filePath = GetStr( reader, "FilePath" );
            rows.Add( ( filePath, GetStr( reader, "ChunkContent" ), new Dictionary<string, string>
            {
               ["_file_path"] = filePath,
               ["class_name"] = GetStr( reader, "ClassName" ),
               ["chunk_type"] = GetStr( reader, "ChunkType" ),
               ["chunk_name"] = GetStr( reader, "ChunkName" ),
               ["start_line"] = GetStr( reader, "StartLine" ),
               ["end_line"] = GetStr( reader, "EndLine" ),
               ["namespace"] = GetStr( reader, "ChunkNamespace" ),
               ["signature"] = GetStr( reader, "Signature" ),
               ["score"] = GetStr( reader, "Score" ),
               ["match_source"] = GetStr( reader, "MatchSource" ),
               ["vector_rrf"] = GetStr( reader, "VectorRRF" ),
               ["chunk_fts_rrf"] = GetStr( reader, "ChunkFtsRRF" ),
               ["file_fts_rrf"] = GetStr( reader, "FileFtsRRF" ),
               ["distance"] = GetStr( reader, "Distance" ),
            } ) );
         }
      }

      // Make an empty vector leg observable. When the distance ceiling (or a missing/unpopulated
      // index) filters every vector candidate away, the proc still succeeds and still returns rows —
      // they are just all full-text. Without this line that degradation is completely invisible:
      // the API looks fine, /health looks fine, and only the match_source field betrays it.
      var vectorBackedRows = rows.Count( r => r.Meta.TryGetValue( "match_source", out var source )
                                              && source != "FullText" );
      if( rows.Count > 0 && vectorBackedRows == 0 )
         Logger.Warn( $"Vector leg returned no candidates for \"{question}\" — all {rows.Count} results are full-text only. " +
                      $"MaxVectorDistance={Config.MaxVectorDistance}, EmbeddingDimension={Config.EmbeddingDimension}. " +
                      "If this is every query, the distance ceiling is likely below the embedding model's real distance floor.", "Search" );
      else
         Logger.Info( $"Fused {rows.Count} rows for \"{question}\" ({vectorBackedRows} vector-backed).", "Search" );

      return rows;
   }

   /// <summary>
   /// Runs the second-stage cross-encoder rerank over the fused rows and returns them in the
   /// reranker's order, stamping each surviving row with its rerank score. Fail-soft: if the
   /// reranker returns nothing usable, the original RRF order is preserved.
   /// </summary>
   private async Task<List<(string FilePath, string Content, Dictionary<string, string> Meta)>> ApplyRerankAsync(
      string question,
      List<(string FilePath, string Content, Dictionary<string, string> Meta)> rows,
      int nResults )
   {
      var candidates = rows.Select( ( row, i ) => new RerankerCandidate( i, row.Content ) ).ToList();
      var reranked = await _reranker.RerankAsync( question, candidates, nResults );
      if( reranked == null || reranked.Count == 0 )
         return rows;

      var (ordered, dropped) = ApplyRelevanceGate( reranked, rows, Config.MinRerankScore );

      if( dropped > 0 )
         Logger.Info( $"Dropped {dropped} result(s) below MinRerankScore={Config.MinRerankScore} for \"{question}\"; {ordered.Count} kept.", "Search" );

      // Deliberately NOT falling back to the unfiltered rows when everything is filtered out: an empty
      // result set is the correct, informative answer to a question this corpus cannot answer. The
      // fallback applies only when the reranker produced no usable indices at all, i.e. nothing was
      // scored and nothing was deliberately dropped.
      return ordered.Count > 0 || dropped > 0 ? ordered : rows;
   }

   /// <summary>
   /// Reorders rows into the reranker's order, stamps each with its score, and drops anything scoring
   /// below <paramref name="minScore"/>. Pure and static so the gate can be tested without a database,
   /// an embedder or a live cross-encoder.
   /// <para>
   /// The gate exists because retrieval always returns its nearest N candidates, so without it a question
   /// the corpus cannot answer comes back with a full page of confident-looking results. The cross-encoder
   /// is the only signal that separates answerable from unanswerable cleanly — vector distance does not,
   /// because off-topic and on-topic hits occupy the same distance band — which is why the filter lives
   /// here rather than earlier in the pipeline.
   /// </para>
   /// </summary>
   /// <param name="reranked">Reranker output, already ordered by descending score.</param>
   /// <param name="rows">The first-stage rows, indexed by <see cref="RerankerResult.OriginalIndex"/>.</param>
   /// <param name="minScore">Inclusive floor; 0 keeps everything the reranker returned.</param>
   /// <returns>The surviving rows in rerank order, and how many were dropped by the floor.</returns>
   public static (List<(string FilePath, string Content, Dictionary<string, string> Meta)> Ordered, int Dropped)
      ApplyRelevanceGate(
         IReadOnlyList<RerankerResult> reranked,
         List<(string FilePath, string Content, Dictionary<string, string> Meta)> rows,
         double minScore )
   {
      var ordered = new List<(string FilePath, string Content, Dictionary<string, string> Meta)>();
      var dropped = 0;

      foreach( var rerankResult in reranked )
      {
         // Out-of-range indices are skipped rather than counted as drops: they are a reranker contract
         // violation, not a relevance decision, and must not be mistaken for "we filtered something".
         if( rerankResult.OriginalIndex < 0 || rerankResult.OriginalIndex >= rows.Count )
            continue;

         var row = rows[rerankResult.OriginalIndex];
         row.Meta["rerank_score"] = rerankResult.Score.ToString( "0.#####" );

         if( rerankResult.Score < minScore )
         {
            dropped++;
            continue;
         }

         ordered.Add( row );
      }

      return (ordered, dropped);
   }

   /// <summary>
   /// Builds the full-text-only fallback response used when the vector path is unavailable or
   /// fails. It over-fetches keyword hits, always merges in filename matches the keyword search
   /// missed, blends and ranks them, and truncates every document except the top hit (kept full
   /// so the best match shows complete context). Populates and returns the supplied response.
   /// </summary>
   private SearchResponse BuildFtsOnlyResponse( SearchRequest request, SearchResponse response )
   {
      var ftsResults = _ftsService.Search( request.Question, request.ModuleFilter, null, Math.Max( request.NResults * 4, 20 ) );

      // Fold in filename matches, adding only files the keyword search hasn't already returned. Skip
      // this merge for a null/whitespace/too-short question: SearchByFilename builds a LIKE pattern,
      // so an empty term degrades to LIKE '%%' and would return every file indiscriminately.
      var question = request.Question;
      if( !string.IsNullOrWhiteSpace( question ) && question.Trim().Length >= MinFilenameSearchLength )
      {
         var existingPaths = new HashSet<string>( ftsResults.Select( r => r.FilePath ) );
         foreach( var filenameHit in _ftsService.SearchByFilename( question, request.NResults ) )
            if( existingPaths.Add( filenameHit.FilePath ) )
               ftsResults.Add( filenameHit );
      }

      var combined = CombineResults( ftsResults, null, request.NResults, request.Question );
      var documents = combined.Select( ( r, i ) => i == 0 ? r.Content : TruncateContent( r.Content ) ).ToList();
      response.Ids = new List<List<string>> { combined.Select( r => r.FilePath ).ToList() };
      response.Documents = new List<List<string>> { documents };
      response.Metadatas = new List<List<Dictionary<string, string>>> { combined.Select( r => r.Metadata ).ToList() };
      return response;
   }

   /// <summary>
   /// Merges full-text and (optionally) vector hits into one ranked list keyed by file path. A
   /// file found by both paths keeps its keyword-derived score and gets a large vector boost; a
   /// file found only by vector search is seeded as a new entry with a smaller vector-only score.
   /// The result is ordered by the blended final score and capped to the requested limit.
   /// </summary>
   private List<CombinedResult> CombineResults( List<FtsResult> ftsResults, List<VectorResult> vectorResults, int limit, string originalQuestion )
   {
      var combined = new Dictionary<string, CombinedResult>();

      // Seed the map with full-text hits and their keyword-derived score.
      foreach( var fts in ftsResults )
      {
         combined[fts.FilePath] = new CombinedResult
         {
            FilePath = fts.FilePath,
            Content = fts.Content,
            Metadata = fts.ToMetadata(),
            FtsRank = fts.Rank,
            VectorScore = 0,
            FinalScore = CalculateFtsScore( fts, originalQuestion )
         };
      }

      // Merge vector hits: boost a file both paths agree on, or seed a vector-only entry.
      if( vectorResults != null )
      {
         foreach( var vec in vectorResults )
         {
            if( combined.TryGetValue( vec.FilePath, out var existing ) )
            {
               existing.VectorScore = vec.Score;
               existing.FinalScore += vec.Score * 100;
            }
            else
            {
               combined[vec.FilePath] = new CombinedResult
               {
                  FilePath = vec.FilePath,
                  Content = "",
                  Metadata = vec.Payload,
                  FtsRank = 999,
                  VectorScore = vec.Score,
                  FinalScore = vec.Score * 50
               };
            }
         }
      }

      return combined.Values
         .OrderByDescending( r => r.FinalScore )
         .Take( limit )
         .ToList();
   }

   /// <summary>
   /// Scores a single full-text hit. Starts from BM25 mapped into a 0-100 band (better rank =>
   /// higher score), then adds a large boost when the question clearly names the file or its
   /// class, so an exact name match reliably rises to the top of the blended results.
   /// </summary>
   private double CalculateFtsScore( FtsResult fts, string originalQuestion )
   {
      double score = Math.Max( 0, 100 - ( fts.Rank * 5 ) );

      var questionLower = originalQuestion.ToLowerInvariant();
      var fileNameLower = Path.GetFileNameWithoutExtension( fts.FilePath ).ToLowerInvariant();
      var classNameLower = ( fts.ClassName ?? "" ).ToLowerInvariant();

      if( ( fileNameLower.Length > 0 && questionLower.Contains( fileNameLower ) ) ||
          ( classNameLower.Length > 0 && questionLower.Contains( classNameLower ) ) ||
          ( questionLower.Length > 0 && fileNameLower.Contains( questionLower.Replace( " ", "" ) ) ) )
      {
         score += 200; // exact name match trumps everything
      }

      return score;
   }

   /// <summary>
   /// Caps content to the configured maximum length, appending a truncation marker when trimmed.
   /// Keeps document payloads bounded so a single large file cannot bloat a response.
   /// </summary>
   private string TruncateContent( string content )
   {
      if( string.IsNullOrEmpty( content ) )
         return "";

      if( content.Length <= Config.MaxContentLength )
         return content;

      return content.Substring( 0, Config.MaxContentLength ) + "\n... [truncated] ...";
   }

   /// <summary>
   /// Reads a column as a string, mapping SQL NULL (and null values) to an empty string so the
   /// metadata dictionary never carries nulls that downstream string handling would choke on.
   /// </summary>
   private static string GetStr( SqlDataReader reader, string columnName )
   {
      var ordinal = reader.GetOrdinal( columnName );
      return reader.IsDBNull( ordinal ) ? "" : ( reader.GetValue( ordinal )?.ToString() ?? "" );
   }

   /// <summary>Writes a diagnostic line to the console. Centralized so the sink is easy to swap.</summary>
   private static void Log( string message ) => Console.WriteLine( message );

   #endregion Private Methods
}
