using System.Diagnostics;
using System.Net;
using System.Text;
using AzureDevOpsForager.Core.Services.Reranking;
using Xunit;

namespace AzureDevOpsForager.Tests;

/// <summary>
/// Covers the time budget on <see cref="HuggingFaceReranker"/>'s warm-up retry loop, using a fake
/// HttpMessageHandler in place of the hosted endpoint so no network or GPU is involved.
///
/// The budget exists because of an outage. On 2026-10-02 the endpoint sat in "Waiting for requested
/// hardware" and the old loop (30 attempts, 2-minute timeout each) kept retrying past Azure App Service's
/// ~230 second front-end limit, so every search came back 504 GatewayTimeout and the fail-soft fallback
/// never ran. <see cref="StuckEndpoint_FallsBackToRetrievalOrderWithinBudget"/> and
/// <see cref="HungRequest_IsCutByAttemptTimeoutAndFallsBack"/> are the regression tests for that; under
/// the old code the first ran for several minutes and the second hung for two.
///
/// Budgets here are a few seconds rather than the production 150 so the suite stays fast. The loop's
/// backoff is fixed at 2s, 4s, 6s..., so each test's budget is chosen to stop it after a known number of
/// attempts.
/// </summary>
public class HuggingFaceRerankerTests
{
   private const string EndpointUrl = "https://fake-endpoint.example";

   /// <summary>Slack allowed on top of a budget for scheduling jitter before a timing assertion fails.</summary>
   private static readonly TimeSpan Slack = TimeSpan.FromSeconds( 2 );

   /// <summary>Three candidates with OriginalIndex values that differ from their positions.</summary>
   private static List<RerankerCandidate> Candidates() => new List<RerankerCandidate>
   {
      new RerankerCandidate( 10, "first" ),
      new RerankerCandidate( 11, "second" ),
      new RerankerCandidate( 12, "third" )
   };

   private static HuggingFaceReranker Reranker( FakeEndpoint endpoint, double budgetSeconds, double attemptSeconds ) =>
      new HuggingFaceReranker( EndpointUrl, "test-token", endpoint,
         TimeSpan.FromSeconds( budgetSeconds ), TimeSpan.FromSeconds( attemptSeconds ) );

   private static HttpResponseMessage Status( HttpStatusCode code, string body = "" ) =>
      new HttpResponseMessage( code ) { Content = new StringContent( body, Encoding.UTF8, "application/json" ) };

   /// <summary>A vLLM/Jina-shaped success that ranks the third candidate first and the first candidate last.</summary>
   private static HttpResponseMessage Scored() => Status( HttpStatusCode.OK,
      "{\"results\":[{\"index\":0,\"relevance_score\":0.1},{\"index\":1,\"relevance_score\":0.5},{\"index\":2,\"relevance_score\":0.9}]}" );

   /// <summary>Asserts the fail-soft result: retrieval order, truncated to topK, with high pseudo-scores.</summary>
   private static void AssertRetrievalOrder( IReadOnlyList<RerankerResult> results, params int[] expectedOriginalIndexes )
   {
      Assert.Equal( expectedOriginalIndexes, results.Select( r => r.OriginalIndex ).ToArray() );
      Assert.All( results, r => Assert.True( r.Score > 0.9 ) );
   }

   [Fact]
   public async Task StuckEndpoint_FallsBackToRetrievalOrderWithinBudget()
   {
      // THE REGRESSION TEST. 503 forever, as HF returns while no GPU can be allocated. Budget 3s: attempt
      // 1 at ~0s, sleep 2s, attempt 2 at ~2s, then a 4s sleep would pass the budget, so it stops there.
      var endpoint = new FakeEndpoint( ( call, ct ) =>
         Task.FromResult( Status( HttpStatusCode.ServiceUnavailable, "Waiting for requested hardware" ) ) );
      var stopwatch = Stopwatch.StartNew();

      var results = await Reranker( endpoint, budgetSeconds: 3, attemptSeconds: 1 ).RerankAsync( "query", Candidates(), 2 );

      Assert.True( stopwatch.Elapsed < TimeSpan.FromSeconds( 3 ) + Slack, $"took {stopwatch.Elapsed.TotalSeconds:F1}s" );
      Assert.Equal( 2, endpoint.Calls );
      AssertRetrievalOrder( results, 10, 11 );
   }

   [Fact]
   public async Task HungRequest_IsCutByAttemptTimeoutAndFallsBack()
   {
      // An endpoint that accepts the request and never answers. The per-attempt deadline (0.5s) must cut
      // it; the old code waited out the HttpClient's 2-minute timeout, then rethrew that timeout.
      var endpoint = new FakeEndpoint( async ( call, ct ) =>
      {
         await Task.Delay( Timeout.Infinite, ct );
         throw new InvalidOperationException( "unreachable" );
      } );
      var stopwatch = Stopwatch.StartNew();

      var results = await Reranker( endpoint, budgetSeconds: 1.5, attemptSeconds: 0.5 ).RerankAsync( "query", Candidates(), 3 );

      Assert.True( stopwatch.Elapsed < TimeSpan.FromSeconds( 1.5 ) + Slack, $"took {stopwatch.Elapsed.TotalSeconds:F1}s" );
      Assert.Equal( 1, endpoint.Calls );
      AssertRetrievalOrder( results, 10, 11, 12 );
   }

   [Fact]
   public async Task WarmingEndpoint_RetriesThenReturnsRerankedOrder()
   {
      // The case the retry loop is for: one 503 while the GPU wakes, then real scores. The budget must
      // not stop a wake that finishes inside it.
      var endpoint = new FakeEndpoint( ( call, ct ) =>
         Task.FromResult( call == 1 ? Status( HttpStatusCode.ServiceUnavailable ) : Scored() ) );

      var results = await Reranker( endpoint, budgetSeconds: 10, attemptSeconds: 5 ).RerankAsync( "query", Candidates(), 3 );

      Assert.Equal( 2, endpoint.Calls );
      Assert.Equal( new[] { 12, 11, 10 }, results.Select( r => r.OriginalIndex ).ToArray() );
   }

   [Fact]
   public async Task NonTransientStatus_FallsBackWithoutRetrying()
   {
      // A 401 will not fix itself; retrying it would only spend the visitor's time.
      var endpoint = new FakeEndpoint( ( call, ct ) => Task.FromResult( Status( HttpStatusCode.Unauthorized ) ) );

      var results = await Reranker( endpoint, budgetSeconds: 10, attemptSeconds: 5 ).RerankAsync( "query", Candidates(), 2 );

      Assert.Equal( 1, endpoint.Calls );
      AssertRetrievalOrder( results, 10, 11 );
   }

   [Fact]
   public async Task CallerCancellation_StillPropagates()
   {
      // The budget's own timeouts become a fallback, but the CALLER cancelling must still throw, or a
      // shutdown or aborted request would sit waiting on a stuck endpoint for the full budget.
      var endpoint = new FakeEndpoint( async ( call, ct ) =>
      {
         await Task.Delay( Timeout.Infinite, ct );
         throw new InvalidOperationException( "unreachable" );
      } );
      using var caller = new CancellationTokenSource( TimeSpan.FromMilliseconds( 200 ) );

      await Assert.ThrowsAnyAsync<OperationCanceledException>( () =>
         Reranker( endpoint, budgetSeconds: 30, attemptSeconds: 30 ).RerankAsync( "query", Candidates(), 2, caller.Token ) );
   }

   /// <summary>
   /// Fake endpoint: answers every request through a scripted function given the 1-based call number and
   /// the request's cancellation token, and counts the calls.
   /// </summary>
   private sealed class FakeEndpoint : HttpMessageHandler
   {
      private readonly Func<int, CancellationToken, Task<HttpResponseMessage>> _respond;
      private int _calls;

      public FakeEndpoint( Func<int, CancellationToken, Task<HttpResponseMessage>> respond )
      {
         _respond = respond;
      }

      /// <summary>How many requests reached the endpoint.</summary>
      public int Calls => Volatile.Read( ref _calls );

      protected override Task<HttpResponseMessage> SendAsync( HttpRequestMessage request, CancellationToken cancellationToken )
      {
         return _respond( Interlocked.Increment( ref _calls ), cancellationToken );
      }
   }
}
