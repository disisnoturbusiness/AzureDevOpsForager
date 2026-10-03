using System.Diagnostics;
using System.Net;
using System.Text;
using AzureDevOpsForager.Core;
using AzureDevOpsForager.Core.Services.Embedding;
using Xunit;

namespace AzureDevOpsForager.Tests;

/// <summary>
/// Covers the time budget on <see cref="HuggingFaceEmbedder"/>'s QUERY path, using a fake
/// HttpMessageHandler in place of the hosted endpoint so no network or GPU is involved.
///
/// A query embed is the first thing a visitor's search waits on, and the old warm-up loop (30 attempts,
/// 3-minute timeout each) could outlive Azure App Service's ~230 second front-end limit on its own. The
/// budget makes a stuck endpoint throw in time for HybridSearchService to serve full-text-only results.
/// <see cref="PassageEmbed_IsNotBoundedByTheQueryBudget"/> pins the other half: the indexer's passage path
/// must keep waiting out a cold start, or a reindex would skip every chunk on a cold endpoint.
///
/// Budgets here are a few seconds rather than the production 90 so the suite stays fast. The loop's
/// backoff is fixed at 2s, 4s, 6s..., so each test's budget is chosen to stop it after a known number of
/// attempts.
/// </summary>
public class HuggingFaceEmbedderTests
{
   private const string EndpointUrl = "https://fake-embed-endpoint.example";

   /// <summary>Slack allowed on top of a budget for scheduling jitter before a timing assertion fails.</summary>
   private static readonly TimeSpan Slack = TimeSpan.FromSeconds( 2 );

   private static HuggingFaceEmbedder Embedder( FakeEndpoint endpoint, double budgetSeconds, double attemptSeconds ) =>
      new HuggingFaceEmbedder( EndpointUrl, "test-token", endpoint,
         TimeSpan.FromSeconds( budgetSeconds ), TimeSpan.FromSeconds( attemptSeconds ) );

   private static HttpResponseMessage Status( HttpStatusCode code, string body = "" ) =>
      new HttpResponseMessage( code ) { Content = new StringContent( body, Encoding.UTF8, "application/json" ) };

   /// <summary>A TEI-shaped success: a flat array of the configured dimension, every component 2.0.</summary>
   private static HttpResponseMessage Vector() => Status( HttpStatusCode.OK,
      "[" + string.Join( ",", Enumerable.Repeat( "2.0", Config.EmbeddingDimension ) ) + "]" );

   /// <summary>An endpoint that accepts the request and never answers until the request is cancelled.</summary>
   private static async Task<HttpResponseMessage> Hang( CancellationToken cancellationToken )
   {
      await Task.Delay( Timeout.Infinite, cancellationToken );
      throw new InvalidOperationException( "unreachable" );
   }

   [Fact]
   public async Task StuckEndpoint_QueryEmbedThrowsTimeoutWithinBudget()
   {
      // THE REGRESSION TEST. 503 forever. Budget 3s: attempt 1 at ~0s, sleep 2s, attempt 2 at ~2s, then a
      // 4s sleep would pass the budget, so it stops there and throws for the search to fall back on.
      var endpoint = new FakeEndpoint( ( call, ct ) =>
         Task.FromResult( Status( HttpStatusCode.ServiceUnavailable, "Waiting for requested hardware" ) ) );
      var stopwatch = Stopwatch.StartNew();

      var exception = await Assert.ThrowsAsync<TimeoutException>( () =>
         Embedder( endpoint, budgetSeconds: 3, attemptSeconds: 1 ).EmbedQueryAsync( "query" ) );

      Assert.True( stopwatch.Elapsed < TimeSpan.FromSeconds( 3 ) + Slack, $"took {stopwatch.Elapsed.TotalSeconds:F1}s" );
      Assert.Equal( 2, endpoint.Calls );
      Assert.Contains( "503", exception.Message );
   }

   [Fact]
   public async Task HungRequest_QueryEmbedIsCutByAttemptTimeout()
   {
      // An endpoint that never answers. The per-attempt deadline (0.5s) must cut it; the old code waited
      // out the HttpClient's 3-minute timeout.
      var endpoint = new FakeEndpoint( ( call, ct ) => Hang( ct ) );
      var stopwatch = Stopwatch.StartNew();

      await Assert.ThrowsAsync<TimeoutException>( () =>
         Embedder( endpoint, budgetSeconds: 1.5, attemptSeconds: 0.5 ).EmbedQueryAsync( "query" ) );

      Assert.True( stopwatch.Elapsed < TimeSpan.FromSeconds( 1.5 ) + Slack, $"took {stopwatch.Elapsed.TotalSeconds:F1}s" );
      Assert.Equal( 1, endpoint.Calls );
   }

   [Fact]
   public async Task WarmingEndpoint_QueryEmbedRetriesThenReturnsUnitVector()
   {
      // One 503 while the GPU wakes, then a real vector. The budget must not stop a wake that finishes
      // inside it, and the result must still come back L2-normalized.
      var endpoint = new FakeEndpoint( ( call, ct ) =>
         Task.FromResult( call == 1 ? Status( HttpStatusCode.ServiceUnavailable ) : Vector() ) );

      var vector = await Embedder( endpoint, budgetSeconds: 10, attemptSeconds: 5 ).EmbedQueryAsync( "query" );

      Assert.Equal( 2, endpoint.Calls );
      Assert.Equal( Config.EmbeddingDimension, vector.Length );
      Assert.Equal( 1.0, Math.Sqrt( vector.Sum( v => (double)v * v ) ), 3 );
   }

   [Fact]
   public async Task PassageEmbed_IsNotBoundedByTheQueryBudget()
   {
      // Query budget 1s, but the endpoint needs a 2s backoff before it answers. A passage embed must
      // ride that out; only queries are bounded.
      var endpoint = new FakeEndpoint( ( call, ct ) =>
         Task.FromResult( call == 1 ? Status( HttpStatusCode.ServiceUnavailable ) : Vector() ) );

      var vector = await Embedder( endpoint, budgetSeconds: 1, attemptSeconds: 0.5 ).EmbedPassageAsync( "passage" );

      Assert.Equal( 2, endpoint.Calls );
      Assert.Equal( Config.EmbeddingDimension, vector.Length );
   }

   [Fact]
   public async Task NonTransientStatus_ThrowsWithoutRetrying()
   {
      // A 401 will not fix itself; retrying it would only spend the visitor's time.
      var endpoint = new FakeEndpoint( ( call, ct ) => Task.FromResult( Status( HttpStatusCode.Unauthorized ) ) );

      var exception = await Assert.ThrowsAsync<HttpRequestException>( () =>
         Embedder( endpoint, budgetSeconds: 10, attemptSeconds: 5 ).EmbedQueryAsync( "query" ) );

      Assert.Equal( 1, endpoint.Calls );
      Assert.Contains( "401", exception.Message );
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
