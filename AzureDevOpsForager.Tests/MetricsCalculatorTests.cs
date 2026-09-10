using System.Collections.Generic;
using AzureDevOpsForager.Eval.Models;
using AzureDevOpsForager.Eval.Services;
using Xunit;

namespace AzureDevOpsForager.Tests;

/// <summary>
/// Tests for the metrics calculator, covering the properties that make a tuning report trustworthy:
/// that recall counts distinct answers rather than repeated ones, that precision is measured against
/// the requested K rather than the returned count, that nDCG actually rewards better ordering, and
/// that failed queries cannot quietly improve a mean by leaving the denominator.
/// </summary>
public class MetricsCalculatorTests
{
   #region Data Members

   /// <summary>Tolerance for floating-point comparisons, in decimal places.</summary>
   private const int Precision = 4;

   #endregion Data Members

   #region Private Methods

   /// <summary>
   /// Builds a golden query whose labels carry the given grades, one label per grade.
   /// </summary>
   /// <param name="grades">The grades to assign, one labelled file each.</param>
   /// <returns>The constructed golden query.</returns>
   private static GoldenQuery QueryWithGrades( params int[] grades )
   {
      var query = new GoldenQuery { Id = "q1", Question = "test" };

      for( int i = 0; i < grades.Length; i++ )
         query.Relevant.Add( new RelevantItem { FilePath = $"src/File{i}.cs", Grade = grades[i] } );

      return query;
   }

   /// <summary>
   /// Builds a judged ranking from (grade, labelIndex) pairs, which is the shape the calculator
   /// consumes from the judge.
   /// </summary>
   /// <param name="pairs">Grade and golden-item index for each rank position.</param>
   /// <returns>The judged ranking.</returns>
   private static List<RankedJudgement> Ranking( params (int Grade, int Index)[] pairs )
   {
      var judgements = new List<RankedJudgement>();

      foreach( var pair in pairs )
         judgements.Add( new RankedJudgement { Grade = pair.Grade, GoldenItemIndex = pair.Index } );

      return judgements;
   }

   #endregion Private Methods

   #region Public Methods

   /// <summary>
   /// Verifies recall counts each labelled item once, so a search that returns the same good file
   /// twice does not score as though it found two different answers.
   /// </summary>
   [Fact]
   public void RecallAtK_RepeatedSameLabel_CountsOnce()
   {
      var query = QueryWithGrades( 3, 3 );
      var recall = new MetricsCalculator().RecallAtK( query, Ranking( ( 3, 0 ), ( 3, 0 ) ) );

      Assert.Equal( 0.5, recall, Precision );
   }

   /// <summary>Verifies recall reaches 1.0 only when every labelled item was surfaced.</summary>
   [Fact]
   public void RecallAtK_AllLabelsFound_IsOne()
   {
      var query = QueryWithGrades( 3, 2 );
      var recall = new MetricsCalculator().RecallAtK( query, Ranking( ( 3, 0 ), ( 2, 1 ) ) );

      Assert.Equal( 1.0, recall, Precision );
   }

   /// <summary>
   /// Verifies grade-0 labels are excluded from the recall denominator, since they record something
   /// deliberately judged irrelevant rather than something to be found.
   /// </summary>
   [Fact]
   public void RecallAtK_ZeroGradeLabels_AreNotCountable()
   {
      var query = QueryWithGrades( 3, 0 );
      var recall = new MetricsCalculator().RecallAtK( query, Ranking( ( 3, 0 ) ) );

      Assert.Equal( 1.0, recall, Precision );
   }

   /// <summary>
   /// Verifies precision divides by the requested K rather than by the number of results returned,
   /// so a configuration that quietly returns fewer results is penalised for the empty slots.
   /// </summary>
   [Fact]
   public void PrecisionAtK_DividesByRequestedK()
   {
      var precision = new MetricsCalculator().PrecisionAtK( Ranking( ( 3, 0 ), ( 0, -1 ) ), 10 );

      Assert.Equal( 0.1, precision, Precision );
   }

   /// <summary>Verifies reciprocal rank reports the position of the first relevant result.</summary>
   [Fact]
   public void ReciprocalRank_UsesFirstRelevantPosition()
   {
      var calculator = new MetricsCalculator();

      Assert.Equal( 1.0, calculator.ReciprocalRank( Ranking( ( 3, 0 ), ( 0, -1 ) ) ), Precision );
      Assert.Equal( 0.5, calculator.ReciprocalRank( Ranking( ( 0, -1 ), ( 3, 0 ) ) ), Precision );
      Assert.Equal( 0.0, calculator.ReciprocalRank( Ranking( ( 0, -1 ), ( 0, -1 ) ) ), Precision );
   }

   /// <summary>Verifies a ranking in the ideal order scores a perfect nDCG.</summary>
   [Fact]
   public void NdcgAtK_IdealOrder_IsOne()
   {
      var query = QueryWithGrades( 3, 2 );
      var ndcg = new MetricsCalculator().NdcgAtK( query, Ranking( ( 3, 0 ), ( 2, 1 ) ), 10 );

      Assert.Equal( 1.0, ndcg, Precision );
   }

   /// <summary>
   /// Verifies nDCG penalises putting the weaker answer first. This is the property that makes it
   /// worth computing at all: recall, precision and MRR are all identical between these two rankings.
   /// </summary>
   [Fact]
   public void NdcgAtK_WeakerAnswerFirst_ScoresLower()
   {
      var query = QueryWithGrades( 3, 2 );
      var calculator = new MetricsCalculator();

      var ideal = calculator.NdcgAtK( query, Ranking( ( 3, 0 ), ( 2, 1 ) ), 10 );
      var swapped = calculator.NdcgAtK( query, Ranking( ( 2, 1 ), ( 3, 0 ) ), 10 );

      Assert.True( swapped < ideal );
      Assert.True( swapped > 0.0 );
   }

   /// <summary>Verifies nDCG is zero when nothing relevant was returned.</summary>
   [Fact]
   public void NdcgAtK_NothingRelevant_IsZero()
   {
      var query = QueryWithGrades( 3 );
      var ndcg = new MetricsCalculator().NdcgAtK( query, Ranking( ( 0, -1 ), ( 0, -1 ) ), 10 );

      Assert.Equal( 0.0, ndcg, Precision );
   }

   /// <summary>
   /// Verifies the cutoff is honoured, so a relevant result below K does not count toward an @K metric.
   /// </summary>
   [Fact]
   public void Score_AppliesCutoff()
   {
      var query = QueryWithGrades( 3 );
      var outcome = new MetricsCalculator().Score( query, Ranking( ( 0, -1 ), ( 0, -1 ), ( 3, 0 ) ), 2 );

      Assert.Equal( 0.0, outcome.RecallAtK, Precision );
      Assert.Equal( 2, outcome.RankedGrades.Count );
   }

   /// <summary>
   /// Verifies nearest-rank percentiles return values that were actually observed rather than
   /// interpolated ones, so a reported p95 is a latency some query really experienced.
   /// </summary>
   [Fact]
   public void Percentile_UsesNearestRank()
   {
      var calculator = new MetricsCalculator();
      var sorted = new List<double> { 10, 20, 30, 40, 50 };

      Assert.Equal( 30.0, calculator.Percentile( sorted, 50 ), Precision );
      Assert.Equal( 50.0, calculator.Percentile( sorted, 95 ), Precision );
      Assert.Equal( 0.0, calculator.Percentile( new List<double>(), 95 ), Precision );
   }

   /// <summary>
   /// Verifies failed queries are excluded from the quality means but still counted, so a run cannot
   /// improve its own score by crashing on the queries it handles worst.
   /// </summary>
   [Fact]
   public void Summarize_ExcludesFailuresFromMeansButCountsThem()
   {
      var outcomes = new List<QueryOutcome>
      {
         new QueryOutcome { QueryId = "a", NdcgAtK = 1.0, ReciprocalRank = 1.0, LatencyMilliseconds = 100 },
         new QueryOutcome { QueryId = "b", NdcgAtK = 0.0, ReciprocalRank = 0.0, LatencyMilliseconds = 200 },
         new QueryOutcome { QueryId = "c", NdcgAtK = 0.0, Error = "boom" },
      };

      var summary = new MetricsCalculator().Summarize( new RunConfiguration { Name = "test" }, outcomes, 10 );

      Assert.Equal( 2, summary.QueriesRun );
      Assert.Equal( 1, summary.QueriesFailed );
      Assert.Equal( 0.5, summary.MeanNdcgAtK, Precision );
      Assert.Equal( 0.5, summary.SuccessRate, Precision );
   }

   #endregion Public Methods
}
