using System;
using System.Net.Http;
using System.Threading.Tasks;

namespace AzureDevOpsForager.Core.Services.Integration;
/// <summary>
/// Shared HTTP GET retry policy used by the Azure DevOps and GitHub clients when foraging
/// repository data across paged REST endpoints. Both integrations hit rate-limited cloud APIs,
/// so rather than duplicating retry logic in each client this class centralizes the "when to
/// retry and how long to wait" decision in one place.
///
/// Retries are attempted for transient failures only: HTTP 429 (Too Many Requests), any 5xx
/// server error, and network-level exceptions (HttpRequestException). When the server tells us
/// how long to wait via a Retry-After header we honor that; otherwise we fall back to exponential
/// backoff capped at 30 seconds so a struggling endpoint is not hammered.
///
/// Note that a returned response may still carry a non-success status code (for example, a 429
/// on the final attempt). This class deliberately does not throw on those; the caller inspects
/// IsSuccessStatusCode / EnsureSuccessStatusCode and decides how to react.
///
/// The network-exception path is different: a transient HttpRequestException is retried only while
/// attempts remain, so an <see cref="HttpRequestException"/> on the final attempt is rethrown to the
/// caller rather than being converted into a response. Callers that must fail soft on a terminal
/// network error have to catch it themselves.
/// </summary>
internal static class HttpRetry
{
   #region Public Methods

   /// <summary>
   /// Issues an HTTP GET against <paramref name="url"/> and retries it on transient failure until
   /// either the request succeeds, a non-transient status is returned, or the attempt budget is
   /// exhausted. This is the single entry point every API client uses so retry behavior stays
   /// consistent across the whole forager.
   /// </summary>
   /// <param name="httpClient">The client used to issue the request. The caller owns its lifetime.</param>
   /// <param name="url">The absolute request URL to GET.</param>
   /// <param name="logTag">
   /// A short prefix (for example "[AZURE]" or "[GITHUB]") stamped on every retry log line so the
   /// console output makes clear which integration is backing off.
   /// </param>
   /// <param name="maxAttempts">The maximum number of attempts before the last response is returned as-is.</param>
   /// <returns>
   /// The final <see cref="HttpResponseMessage"/>. It may be a success response, or a non-success
   /// response when a transient HTTP status never cleared within the attempt budget.
   /// </returns>
   /// <exception cref="HttpRequestException">
   /// Thrown when the request fails at the network level (DNS, connection reset, TLS) on the final
   /// attempt: the exception is only swallowed while retries remain, so a terminal network failure
   /// propagates rather than being returned as a response.
   /// </exception>
   public static async Task<HttpResponseMessage> GetWithRetryAsync( HttpClient httpClient, string url, string logTag, int maxAttempts = 5 )
   {
      // Unbounded loop; every exit path is guarded by the maxAttempts checks inside the body.
      for( int attempt = 1; ; attempt++ )
      {
         try
         {
            var response = await httpClient.GetAsync( url );
            if( response.IsSuccessStatusCode )
               return response;

            // Only 429 and 5xx are worth retrying. A 4xx (other than 429) is the caller's own
            // problem (bad URL, auth, etc.) and will never clear by waiting.
            bool isTransient = (int)response.StatusCode == 429 || (int)response.StatusCode >= 500;
            if( !isTransient || attempt >= maxAttempts )
               return response;

            var retryDelay = ResolveRetryDelay( response, attempt );
            LogRetry( logTag, $"HTTP {(int)response.StatusCode}", attempt, maxAttempts, retryDelay );

            // Dispose the failed response before sleeping so its socket/handle is not held
            // open during the (potentially long) backoff wait.
            response.Dispose();
            await Task.Delay( retryDelay );
         }
         catch( HttpRequestException ) when( attempt < maxAttempts )
         {
            // Network-level failure (DNS, connection reset, TLS). There is no response to read a
            // Retry-After from, so we always fall back to plain exponential backoff here.
            var retryDelay = ExponentialBackoff( attempt );
            LogRetry( logTag, "network error", attempt, maxAttempts, retryDelay );
            await Task.Delay( retryDelay );
         }
      }
   }

   #endregion

   #region Private Methods

   /// <summary>
   /// Decides how long to wait before the next attempt. Prefers the server's own guidance via the
   /// Retry-After header (either a relative delta or an absolute date), which is the polite thing
   /// to do against a rate-limited API. Falls back to exponential backoff when no usable header is
   /// present.
   /// </summary>
   /// <param name="response">The failed (transient) response whose headers may carry Retry-After.</param>
   /// <param name="attempt">The current attempt number, used only for the backoff fallback.</param>
   /// <returns>The delay to wait before retrying.</returns>
   private static TimeSpan ResolveRetryDelay( HttpResponseMessage response, int attempt )
   {
      var retryAfter = response.Headers.RetryAfter;
      if( retryAfter != null )
      {
         // Retry-After: <seconds> form.
         if( retryAfter.Delta.HasValue )
            return retryAfter.Delta.Value;

         // Retry-After: <HTTP-date> form. Convert the absolute time into a delay from now,
         // ignoring it if the date is already in the past (a stale/clock-skewed header).
         if( retryAfter.Date.HasValue )
         {
            var timeUntilRetry = retryAfter.Date.Value - DateTimeOffset.UtcNow;
            if( timeUntilRetry > TimeSpan.Zero )
               return timeUntilRetry;
         }
      }

      return ExponentialBackoff( attempt );
   }

   /// <summary>
   /// The single source of the "2^n seconds, capped at 30" backoff rule. Keeping it in one method
   /// means both the transient-status path and the network-exception path back off identically.
   /// </summary>
   /// <param name="attempt">The current attempt number; the exponent in 2^attempt.</param>
   /// <returns>A delay of 2^attempt seconds, never exceeding 30 seconds.</returns>
   private static TimeSpan ExponentialBackoff( int attempt )
      => TimeSpan.FromSeconds( Math.Min( 30, Math.Pow( 2, attempt ) ) );

   /// <summary>
   /// Writes a single, consistently formatted retry line to the console. Centralized so the
   /// transient-status and network-error paths produce identically shaped output differing only in
   /// the <paramref name="reason"/>.
   /// </summary>
   /// <param name="logTag">The caller's log prefix (for example "[AZURE]").</param>
   /// <param name="reason">A short description of why we are retrying (for example "network error").</param>
   /// <param name="attempt">The attempt that just failed.</param>
   /// <param name="maxAttempts">The total attempt budget, for context in the log line.</param>
   /// <param name="retryDelay">How long we are about to wait before the next attempt.</param>
   private static void LogRetry( string logTag, string reason, int attempt, int maxAttempts, TimeSpan retryDelay )
   {
      Console.WriteLine( $"{logTag} {reason} (attempt {attempt}/{maxAttempts}); retrying in {retryDelay.TotalSeconds:F0}s..." );
   }

   #endregion
}
