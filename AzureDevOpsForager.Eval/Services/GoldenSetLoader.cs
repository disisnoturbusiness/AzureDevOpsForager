using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using AzureDevOpsForager.Eval.Models;
using Newtonsoft.Json;

namespace AzureDevOpsForager.Eval.Services;

/// <summary>
/// Reads the golden query set off disk and refuses to hand back a set that cannot produce
/// trustworthy numbers.
///
/// The validation here is deliberately strict and fails the whole load rather than dropping bad
/// entries quietly. A golden set is the measuring instrument; an instrument that silently ignores
/// the parts of itself it does not understand will still produce confident-looking readings, and
/// those readings are worse than no readings because they get believed.
/// </summary>
public class GoldenSetLoader
{
   #region Public Methods

   /// <summary>
   /// Loads and validates the golden set at the given path. Every structural problem found is
   /// collected and reported together, so a malformed file can be fixed in one pass instead of
   /// one error per run.
   /// </summary>
   /// <param name="path">Path to the golden queries JSON file.</param>
   /// <returns>The validated golden queries, in file order.</returns>
   /// <exception cref="FileNotFoundException">The file does not exist.</exception>
   /// <exception cref="InvalidDataException">The file is empty, unparseable, or fails validation.</exception>
   public List<GoldenQuery> Load( string path )
   {
      if( !File.Exists( path ) )
         throw new FileNotFoundException( $"Golden query set not found: {path}" );

      var json = File.ReadAllText( path );
      if( string.IsNullOrWhiteSpace( json ) )
         throw new InvalidDataException( $"Golden query set is empty: {path}" );

      List<GoldenQuery> queries;
      try
      {
         queries = JsonConvert.DeserializeObject<List<GoldenQuery>>( json );
      }
      catch( JsonException exception )
      {
         throw new InvalidDataException( $"Golden query set is not valid JSON: {exception.Message}", exception );
      }

      if( queries == null || queries.Count == 0 )
         throw new InvalidDataException( $"Golden query set contains no queries: {path}" );

      var problems = Validate( queries );
      if( problems.Count > 0 )
         throw new InvalidDataException(
            $"Golden query set failed validation ({problems.Count} problem(s)):{Environment.NewLine}" +
            string.Join( Environment.NewLine, problems.Select( p => "  - " + p ) ) );

      return queries;
   }

   #endregion Public Methods

   #region Private Methods

   /// <summary>
   /// Checks the loaded set for the mistakes that would corrupt a measurement: missing ids,
   /// duplicate ids, blank questions, unlabelled queries, and grades outside the scale.
   /// </summary>
   /// <param name="queries">The deserialized queries to inspect.</param>
   /// <returns>A human-readable description of every problem found; empty when the set is sound.</returns>
   private List<string> Validate( List<GoldenQuery> queries )
   {
      var problems = new List<string>();
      var seenIds = new HashSet<string>( StringComparer.OrdinalIgnoreCase );

      for( int i = 0; i < queries.Count; i++ )
      {
         var query = queries[i];
         var label = string.IsNullOrWhiteSpace( query.Id ) ? $"index {i}" : query.Id;

         if( string.IsNullOrWhiteSpace( query.Id ) )
            problems.Add( $"Query at index {i} has no id; ids are the join key for per-query tracking." );
         else if( !seenIds.Add( query.Id ) )
            problems.Add( $"Duplicate query id '{query.Id}'; per-query results would overwrite each other." );

         if( string.IsNullOrWhiteSpace( query.Question ) )
            problems.Add( $"Query '{label}' has no question text." );

         problems.AddRange( ValidateRelevantItems( query, label ) );
      }

      return problems;
   }

   /// <summary>
   /// Validates the labelled relevant items for a single query: that there is at least one, that
   /// each names a file, and that each grade sits on the defined scale.
   /// </summary>
   /// <param name="query">The query whose labels are being checked.</param>
   /// <param name="label">Display name for the query, used in problem messages.</param>
   /// <returns>Problems found for this query; empty when its labels are sound.</returns>
   private List<string> ValidateRelevantItems( GoldenQuery query, string label )
   {
      var problems = new List<string>();

      if( query.Relevant == null || query.Relevant.Count == 0 )
      {
         // Treated as an error rather than a zero-score query: an unlabelled entry and a genuinely
         // unanswerable one are indistinguishable here, and scoring the first as the second would
         // quietly reward a search that returns nothing.
         problems.Add( $"Query '{label}' has no relevant items; an unlabelled query cannot be scored." );
         return problems;
      }

      for( int i = 0; i < query.Relevant.Count; i++ )
      {
         var item = query.Relevant[i];

         if( string.IsNullOrWhiteSpace( item.FilePath ) )
            problems.Add( $"Query '{label}' relevant item {i} has no file path." );

         if( item.Grade < 0 || item.Grade > RelevantItem.MaximumGrade )
            problems.Add(
               $"Query '{label}' relevant item {i} has grade {item.Grade}, outside the 0-{RelevantItem.MaximumGrade} scale." );
      }

      if( query.Relevant.All( r => r.Grade < RelevantItem.MinimumBinaryGrade ) )
         problems.Add( $"Query '{label}' has only grade-0 items, so no result can ever count as a hit." );

      return problems;
   }

   #endregion Private Methods
}
