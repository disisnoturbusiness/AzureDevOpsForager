using System;
using System.Collections.Generic;
using System.Linq;
using System.Text.RegularExpressions;
using AzureDevOpsForager.Core.Models.Search;
using Microsoft.Data.SqlClient;

namespace AzureDevOpsForager.Core.Services.Search;

/// <summary>
/// SQL Server Full-Text Search service. This is the keyword (lexical) half of the
/// hybrid search stack; the vector service supplies the semantic half. It leans on
/// SQL Server's FREETEXTTABLE for stemmed, ranked keyword matching, but layers three
/// query-shaping heuristics on top so that developer-style questions resolve to code
/// files rather than prose:
///   1. a field-assignment heuristic ("where is field X populated") that hunts for
///      assignment sites in the indexed source,
///   2. PascalCase-identifier detection so a question mentioning a type or member name
///      goes straight to a targeted name lookup (with a Levenshtein fuzzy retry when the
///      exact spelling misses), and
///   3. a stop-word-filtered keyword fallback that hands the remaining terms to full-text.
/// The service is deliberately language-generic: it carries no domain or vendor concepts,
/// only the shape of a code index.
/// </summary>
public class SqlFtsService : IDisposable
{
   #region Data Members

   /// <summary>
   /// Connection string for the code-index database. Captured once at construction so every
   /// query opens its own short-lived connection against the same target.
   /// </summary>
   private readonly string _connectionString;

   /// <summary>Guards <see cref="Dispose"/> so repeated disposal is a harmless no-op.</summary>
   private bool _disposed;

   /// <summary>
   /// The shared column projection used by every plain CodeFiles SELECT (i.e. everything
   /// except the full-text path, which also pulls the RANK column). Keeping the list in one
   /// place guarantees the reader-mapping offsets in <see cref="MapFtsRow"/> stay in sync
   /// with what the queries actually return.
   /// </summary>
   private const string FileColumns =
      "FilePath, Content, ClassName, BaseClass, MethodNames, PropertyNames, EnumValues, FileType";

   #endregion Data Members

   #region Constructor

   /// <summary>
   /// Creates the service against an explicit connection string, or falls back to the
   /// application-wide <see cref="Config.SqlConnectionString"/> when none is supplied. The
   /// optional parameter keeps test/host wiring simple while still allowing a default.
   /// </summary>
   /// <param name="connectionString">
   /// Target index database; when null, the configured default is used.
   /// </param>
   public SqlFtsService( string connectionString = null )
   {
      _connectionString = connectionString ?? Config.SqlConnectionString;
   }

   #endregion Constructor

   #region Public Methods

   /// <summary>
   /// No-op retained for call compatibility. Some callers were written against an older
   /// connection-per-instance design and still invoke Open(); connections are now opened
   /// per-query, so there is nothing to do here.
   /// </summary>
   public void Open() { }

   /// <summary>
   /// Primary entry point for keyword search. Routes the incoming natural-language question
   /// through the three heuristics in priority order (field-assignment, then PascalCase name
   /// lookup, then a plain keyword fallback) and returns the first strategy that yields hits.
   /// </summary>
   /// <param name="question">The raw developer question or search phrase.</param>
   /// <param name="moduleFilter">
   /// Accepted for call compatibility but no longer applied; this facet was domain-specific
   /// and is not stored in the current index. It is still threaded into the fuzzy retry so a
   /// recursive call keeps the caller's original argument shape.
   /// </param>
   /// <param name="systemFilter">
   /// Accepted for call compatibility but no longer applied (same reasoning as moduleFilter).
   /// </param>
   /// <param name="nResults">Maximum number of rows to return.</param>
   public List<FtsResult> Search( string question, string moduleFilter = null, string systemFilter = null, int nResults = 10 )
   {
      // Strategy 1: field-assignment questions ("what fills field X", "where is field X populated").
      var fieldResults = TrySearchFieldAssignment( question, nResults );
      if( fieldResults != null )
         return fieldResults;

      // Strategy 2: PascalCase identifiers (e.g. "MyClassName") get a targeted name search,
      // with a Levenshtein fuzzy retry if the exact spelling finds nothing.
      var pascalCaseTerms = ExtractPascalCaseIdentifiers( question );
      if( pascalCaseTerms.Any() )
      {
         var results = SearchByPascalCase( pascalCaseTerms, nResults );
         if( results.Count == 0 )
            results = FuzzyRetry( question, pascalCaseTerms, moduleFilter, nResults );
         return results;
      }

      // Strategy 3: fall back to stop-word-filtered full-text keyword search.
      var keywords = ExtractKeywords( question );
      if( !keywords.Any() )
         return new List<FtsResult>();

      return SearchByKeywords( keywords, nResults );
   }

   /// <summary>
   /// Finds files whose path contains the given substring, i.e. files NAMED like the query
   /// rather than files whose contents match it. Useful when the user already knows (part of)
   /// the filename.
   /// </summary>
   /// <param name="filename">Substring to match anywhere in the file path.</param>
   /// <param name="nResults">Maximum number of rows to return.</param>
   public List<FtsResult> SearchByFilename( string filename, int nResults = 5 )
      => RunFileColumnsQuery( "WHERE FilePath LIKE @pattern ORDER BY FilePath", nResults,
            command => command.Parameters.AddWithValue( "@pattern", $"%{filename}%" ) );

   /// <summary>Returns the total number of files currently in the index (a quick health/coverage check).</summary>
   public int GetFileCount()
   {
      using var connection = AzureDevOpsForager.Core.Services.Utilities.SqlResilience.CreateConnection(_connectionString );
      connection.Open();

      using var command = new SqlCommand( "SELECT COUNT(*) FROM dbo.CodeFiles", connection );
      return (int)command.ExecuteScalar();
   }

   /// <summary>
   /// Disposes the service. There is no long-lived unmanaged state to release (connections are
   /// per-query), so this only trips the idempotency guard; the method exists to satisfy
   /// <see cref="IDisposable"/> for callers that wrap the service in a using block.
   /// </summary>
   public void Dispose()
   {
      if( _disposed ) return;
      _disposed = true;
   }

   #endregion Public Methods

   #region Private Methods

   /// <summary>
   /// Detects and services a field-assignment question. The trigger is deliberately narrow:
   /// the phrase must mention "field" together with "filled" or "populated". When matched, the
   /// field name is pulled out with a regex and handed to <see cref="SearchFieldAssignment"/>.
   /// Returns null when this is not a field-assignment question or the heuristic finds nothing,
   /// which signals the caller to fall through to the next strategy.
   /// </summary>
   private List<FtsResult> TrySearchFieldAssignment( string question, int nResults )
   {
      bool mentionsField = question.IndexOf( "field", StringComparison.OrdinalIgnoreCase ) >= 0;
      bool mentionsAssignment =
         question.IndexOf( "filled", StringComparison.OrdinalIgnoreCase ) >= 0 ||
         question.IndexOf( "populated", StringComparison.OrdinalIgnoreCase ) >= 0;

      if( !mentionsField || !mentionsAssignment )
         return null;

      var fieldMatch = Regex.Match( question, @"field\s+(\w+)", RegexOptions.IgnoreCase );
      if( !fieldMatch.Success )
         return null;

      var fieldResults = SearchFieldAssignment( fieldMatch.Groups[1].Value, nResults );
      return fieldResults.Count > 0 ? fieldResults : null;
   }

   /// <summary>
   /// Shared query runner for the plain (non-full-text) file-column searches. Opens a
   /// connection, builds a `SELECT TOP (@nResults) {FileColumns} FROM dbo.CodeFiles {whereAndOrder}`,
   /// binds the row cap, lets the caller bind its own predicate parameters, and maps the rows.
   /// This is the one place the open-connection + projection boilerplate lives, so the search
   /// methods only have to describe their WHERE/ORDER clause and parameters.
   /// </summary>
   /// <param name="whereAndOrder">The WHERE (and optional ORDER BY) fragment to append.</param>
   /// <param name="nResults">Row cap, bound to the @nResults parameter.</param>
   /// <param name="addParameters">Callback that binds the caller's own query parameters.</param>
   private List<FtsResult> RunFileColumnsQuery( string whereAndOrder, int nResults, Action<SqlCommand> addParameters )
   {
      using var connection = AzureDevOpsForager.Core.Services.Utilities.SqlResilience.CreateConnection(_connectionString );
      connection.Open();
      using var command = new SqlCommand( $"SELECT TOP (@nResults) {FileColumns} FROM dbo.CodeFiles {whereAndOrder}", connection );
      command.Parameters.AddWithValue( "@nResults", nResults );
      addParameters( command );
      return ReadFtsResults( command );
   }

   /// <summary>
   /// Searches indexed content for assignment sites of a field. Because the caller may type the
   /// field in either casing, we probe three assignment patterns: the name as given, the same
   /// name PascalCased, and the PascalCased name followed by extra characters before the "=".
   /// All three are LIKE patterns matched against the source Content.
   /// </summary>
   /// <param name="fieldName">Field name as extracted from the question.</param>
   /// <param name="nResults">Maximum number of rows to return.</param>
   private List<FtsResult> SearchFieldAssignment( string fieldName, int nResults )
   {
      // Uppercase the first character so a lower-cased field name still matches PascalCase members.
      var pascalCase = char.ToUpper( fieldName[0] ) + fieldName.Substring( 1 );
      return RunFileColumnsQuery(
         "WHERE (Content LIKE @pat1 OR Content LIKE @pat2 OR Content LIKE @pat3) ORDER BY FilePath",
         nResults,
         command =>
         {
            command.Parameters.AddWithValue( "@pat1", $"%.{fieldName} =%" );
            command.Parameters.AddWithValue( "@pat2", $"%.{pascalCase} =%" );
            command.Parameters.AddWithValue( "@pat3", $"%.{pascalCase}% =%" );
         } );
   }

   /// <summary>
   /// Targeted name lookup for one or more PascalCase identifiers. Each term becomes a LIKE
   /// clause spanning the four name-bearing columns (ClassName, BaseClass, MethodNames,
   /// PropertyNames), OR-combined so any single match qualifies the row. Results are ordered by
   /// class-name length so the shortest (and usually most exact) type name floats to the top.
   /// </summary>
   /// <param name="terms">PascalCase identifiers to search for.</param>
   /// <param name="nResults">Maximum number of rows to return.</param>
   private List<FtsResult> SearchByPascalCase( List<string> terms, int nResults )
   {
      var likeConditions = new List<string>();
      var termParameters = new List<(string Name, string Value)>();
      for( int i = 0; i < terms.Count; i++ )
      {
         var parameterName = $"@term{i}";
         likeConditions.Add( $"(ClassName LIKE {parameterName} OR BaseClass LIKE {parameterName} OR MethodNames LIKE {parameterName} OR PropertyNames LIKE {parameterName})" );
         termParameters.Add( (parameterName, $"%{terms[i]}%") );
      }

      return RunFileColumnsQuery(
         $"WHERE ({string.Join( " OR ", likeConditions )}) ORDER BY LEN(ClassName), FilePath",
         nResults,
         command => { foreach( var ( name, value ) in termParameters ) command.Parameters.AddWithValue( name, value ); } );
   }

   /// <summary>
   /// Full-text keyword search over the whole index using FREETEXTTABLE, which applies stemming
   /// and returns SQL Server's own relevance RANK. The raw RANK is then rescaled into the
   /// service's 0..20 relevance space so results are comparable with the other search paths
   /// (higher SQL RANK, lower divided value, higher normalized score, capped at 0 on the floor).
   /// </summary>
   /// <param name="keywords">Keyword terms, OR-combined into the free-text search string.</param>
   /// <param name="nResults">Maximum number of rows to return.</param>
   private List<FtsResult> SearchByKeywords( List<string> keywords, int nResults )
   {
      using var connection = AzureDevOpsForager.Core.Services.Utilities.SqlResilience.CreateConnection(_connectionString );
      connection.Open();

      var searchTerms = string.Join( " OR ", keywords );

      var command = new SqlCommand( $@"
         SELECT TOP (@nResults)
            cf.FilePath, cf.Content, cf.ClassName, cf.BaseClass,
            cf.MethodNames, cf.PropertyNames, cf.EnumValues, cf.FileType,
            ft.[RANK]
         FROM dbo.CodeFiles cf
         INNER JOIN FREETEXTTABLE(dbo.CodeFiles, *, @searchTerms) ft ON cf.Id = ft.[KEY]
         ORDER BY ft.[RANK] DESC", connection );
      command.Parameters.AddWithValue( "@nResults", nResults );
      command.Parameters.AddWithValue( "@searchTerms", searchTerms );

      var results = new List<FtsResult>();

      using var reader = command.ExecuteReader();
      while( reader.Read() )
      {
         // Column 8 is the SQL RANK; rescale it into the shared 0..20 relevance range.
         var sqlRank = reader.GetInt32( 8 );
         var normalizedRank = Math.Max( 0, 20.0 - ( sqlRank / 50.0 ) );
         results.Add( MapFtsRow( reader, normalizedRank ) );
      }

      return results;
   }

   /// <summary>
   /// Fuzzy retry for PascalCase searches that returned nothing. For each original term it finds
   /// the closest indexed class name by Levenshtein distance, and if any term actually changed,
   /// rebuilds the question with the corrected spellings and re-runs <see cref="Search"/>. When no
   /// term has a close-enough neighbour, it gives up with an empty list rather than recursing.
   /// </summary>
   /// <param name="question">The original question, used as the substitution template.</param>
   /// <param name="pascalCaseTerms">The identifiers that failed the exact lookup.</param>
   /// <param name="moduleFilter">Threaded back into the recursive Search call for arg-shape parity.</param>
   /// <param name="nResults">Maximum number of rows to return.</param>
   private List<FtsResult> FuzzyRetry( string question, List<string> pascalCaseTerms, string moduleFilter, int nResults )
   {
      var fuzzyTerms = new List<string>();
      bool hadFuzzy = false;

      foreach( var term in pascalCaseTerms )
      {
         var closest = FindClosestClassName( term );
         if( closest != null && closest != term )
         {
            fuzzyTerms.Add( closest );
            hadFuzzy = true;
         }
      }

      if( hadFuzzy )
      {
         // Swap each original term for its corrected neighbour, then re-run the full search.
         var fuzzyQuestion = question;
         for( int i = 0; i < pascalCaseTerms.Count && i < fuzzyTerms.Count; i++ )
            fuzzyQuestion = fuzzyQuestion.Replace( pascalCaseTerms[i], fuzzyTerms[i] );

         return Search( fuzzyQuestion, moduleFilter, null, nResults );
      }

      return new List<FtsResult>();
   }

   /// <summary>
   /// Finds the indexed class name closest to the given term, subject to a Levenshtein distance
   /// of 3 or fewer edits. Only reasonably long class names (8+ characters) are considered so we
   /// do not "correct" a short term into an unrelated short type. Returns null when nothing is
   /// within the edit-distance budget.
   /// </summary>
   /// <param name="term">The (possibly misspelled) identifier to snap to a real class name.</param>
   private string FindClosestClassName( string term )
   {
      using var connection = AzureDevOpsForager.Core.Services.Utilities.SqlResilience.CreateConnection(_connectionString );
      connection.Open();

      using var command = new SqlCommand(
         "SELECT DISTINCT ClassName FROM dbo.CodeFiles WHERE ClassName IS NOT NULL AND ClassName != '' AND LEN(ClassName) >= 8", connection );
      var classNames = new List<string>();
      using var reader = command.ExecuteReader();
      while( reader.Read() )
         classNames.Add( reader.GetString( 0 ) );

      string closest = null;
      int minDistance = int.MaxValue;

      foreach( var className in classNames )
      {
         var distance = LevenshteinDistance( term, className );
         if( distance < minDistance && distance <= 3 )
         {
            minDistance = distance;
            closest = className;
         }
      }

      return closest;
   }

   /// <summary>
   /// Executes a prepared command and maps its rows to results with a fixed rank of 0. Used by
   /// the plain file-column searches, which have no relevance score of their own (only full-text
   /// search carries a meaningful RANK).
   /// </summary>
   private List<FtsResult> ReadFtsResults( SqlCommand command )
   {
      var results = new List<FtsResult>();

      using var reader = command.ExecuteReader();
      while( reader.Read() )
         results.Add( MapFtsRow( reader, 0 ) );

      return results;
   }

   /// <summary>
   /// Maps the shared 8-column CodeFiles projection (in <see cref="FileColumns"/> order) to an
   /// <see cref="FtsResult"/>, applying the supplied rank. Nullable string columns collapse to
   /// null (or empty string for Content) so downstream consumers never see a DBNull.
   /// </summary>
   /// <param name="reader">Reader positioned on a row with the FileColumns projection.</param>
   /// <param name="rank">Relevance score to stamp on the mapped result.</param>
   private static FtsResult MapFtsRow( SqlDataReader reader, double rank )
   {
      return new FtsResult
      {
         FilePath = reader.GetString( 0 ),
         Content = reader.IsDBNull( 1 ) ? "" : reader.GetString( 1 ),
         ClassName = reader.IsDBNull( 2 ) ? null : reader.GetString( 2 ),
         BaseClass = reader.IsDBNull( 3 ) ? null : reader.GetString( 3 ),
         MethodNames = reader.IsDBNull( 4 ) ? null : reader.GetString( 4 ),
         PropertyNames = reader.IsDBNull( 5 ) ? null : reader.GetString( 5 ),
         EnumValues = reader.IsDBNull( 6 ) ? null : reader.GetString( 6 ),
         FileType = reader.IsDBNull( 7 ) ? null : reader.GetString( 7 ),
         Rank = rank
      };
   }

   /// <summary>
   /// Pulls PascalCase identifiers out of the question so they can be routed to the targeted
   /// name search. A word qualifies as an identifier only when it is at least 8 characters,
   /// starts with an uppercase letter, contains two or more uppercase letters (the hump of
   /// PascalCase), and has at least one lowercase letter, which together filter out ordinary
   /// words and all-caps acronyms.
   /// </summary>
   /// <param name="question">The raw question to scan.</param>
   private List<string> ExtractPascalCaseIdentifiers( string question )
   {
      var identifiers = new List<string>();
      var words = question.Split( new[] { ' ', ',', '?', '!', '.', '\'', '"', '(', ')' },
         StringSplitOptions.RemoveEmptyEntries );

      foreach( var word in words )
      {
         if( word.Length >= 8 &&
             char.IsUpper( word[0] ) &&
             word.Count( char.IsUpper ) >= 2 &&
             word.Any( char.IsLower ) )
         {
            identifiers.Add( word );
         }
      }

      return identifiers;
   }

   /// <summary>
   /// Reduces the question to distinct, lower-cased search keywords. Common interrogatives and
   /// filler words are stripped via a stop-word set, and very short tokens (2 characters or
   /// fewer) are dropped, leaving the terms most likely to be meaningful in a code index.
   /// </summary>
   /// <param name="question">The raw question to tokenize.</param>
   private List<string> ExtractKeywords( string question )
   {
      var stopWords = new HashSet<string>( StringComparer.OrdinalIgnoreCase )
      {
         "what", "where", "when", "who", "how", "would", "should", "could",
         "create", "make", "build", "properties", "does", "have", "the", "for",
         "a", "an", "in", "on", "to", "is", "are", "show", "me", "find", "get",
         "exist", "exists", "see", "can", "using"
      };

      return question
         .Split( new[] { ' ', ',', '?', '!', '.', '\'', '"' }, StringSplitOptions.RemoveEmptyEntries )
         .Where( word => word.Length > 2 && !stopWords.Contains( word ) )
         .Select( word => word.ToLowerInvariant() )
         .Distinct()
         .ToList();
   }

   /// <summary>
   /// Classic dynamic-programming Levenshtein edit distance between two strings (the minimum
   /// number of single-character insertions, deletions, or substitutions to turn one into the
   /// other). Drives the fuzzy class-name matching. Empty inputs short-circuit to the other
   /// string's length.
   /// </summary>
   private int LevenshteinDistance( string first, string second )
   {
      if( string.IsNullOrEmpty( first ) ) return second?.Length ?? 0;
      if( string.IsNullOrEmpty( second ) ) return first.Length;

      int[,] distance = new int[first.Length + 1, second.Length + 1];

      // Seed the first row/column with the cost of building each prefix from scratch.
      for( int i = 0; i <= first.Length; i++ ) distance[i, 0] = i;
      for( int j = 0; j <= second.Length; j++ ) distance[0, j] = j;

      for( int j = 1; j <= second.Length; j++ )
      {
         for( int i = 1; i <= first.Length; i++ )
         {
            int cost = ( first[i - 1] == second[j - 1] ) ? 0 : 1;
            distance[i, j] = Math.Min(
               Math.Min( distance[i - 1, j] + 1, distance[i, j - 1] + 1 ),
               distance[i - 1, j - 1] + cost );
         }
      }

      return distance[first.Length, second.Length];
   }

   #endregion Private Methods
}
