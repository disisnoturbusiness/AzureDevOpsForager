using System;
using System.Collections.Generic;
using System.Net.Http;
using System.Text;
using System.Threading.Tasks;
using Newtonsoft.Json;
using Newtonsoft.Json.Linq;

namespace AzureDevOpsForager.Core.Services.Chat
{
   /// <summary>
   /// Base chat service. This is a deliberately thin HTTP client of the Forager Server's
   /// /chat endpoint: the heavy lifting (document search plus the LLM completion) happens
   /// server-side, so the client's only job is to post the user's question and present what
   /// comes back. On top of that it layers two local conveniences that do not need the
   /// server: a per-user known-answers cache (so repeated questions return instantly) and a
   /// feedback log (thumbs up/down) that we can later mine to improve answer quality.
   /// </summary>
   public abstract class BaseChatService
   {
      #region Data Members

      /// <summary>
      /// The HTTP client used for every call to the server's /chat endpoint. A single
      /// long-lived instance is shared for the lifetime of the service to avoid socket
      /// exhaustion from repeated construction.
      /// </summary>
      protected readonly HttpClient _httpClient;

      /// <summary>
      /// Absolute path to the append-only feedback log. Each thumbs up/down (and each retry
      /// request) is written here as one pipe-delimited line for later review.
      /// </summary>
      protected readonly string _feedbackLogPath;

      /// <summary>
      /// Absolute path to the JSON file backing the local known-answers cache. The cache maps
      /// a normalized question to a previously accepted answer.
      /// </summary>
      protected readonly string _knownAnswersPath;

      /// <summary>
      /// In-memory transcript of the current conversation. The server's /chat endpoint is
      /// stateless, so this history is not sent anywhere; it exists purely so the local UI can
      /// show what has been asked and answered this session.
      /// </summary>
      protected List<object> _conversationHistory = new List<object>();

      #endregion Data Members

      #region Constructor

      /// <summary>
      /// Wires up the base service: creates the shared HTTP client (with a generous five-minute
      /// timeout, since server-side search plus an LLM call can be slow — and a scale-to-zero
      /// embedding endpoint can cold-start for a couple of minutes on the very first request),
      /// ensures the per-user data directory exists, and resolves the on-disk paths for the
      /// feedback log and the known-answers cache. Both files live under the shared local
      /// app-data root so every derived chat service reads and writes the same caches.
      /// </summary>
      protected BaseChatService()
      {
         _httpClient = new HttpClient();
         _httpClient.Timeout = TimeSpan.FromMinutes( 5 );

         System.IO.Directory.CreateDirectory( Config.LocalAppDataRoot );
         _feedbackLogPath = System.IO.Path.Combine( Config.LocalAppDataRoot, "feedback.log" );
         _knownAnswersPath = Config.KnownAnswersPath;
      }

      #endregion Constructor

      #region Public Methods

      /// <summary>
      /// Answers a user's question. The cache is consulted first so a previously accepted
      /// answer comes back instantly (flagged as cached); otherwise the question is posted to
      /// the server and the fresh answer is recorded in the local conversation history. Any
      /// failure along the way is swallowed and reported as a friendly "server unreachable"
      /// message rather than surfacing a raw exception to the user.
      /// </summary>
      /// <param name="question">User's question.</param>
      /// <returns>Server-generated answer (with an optional Sources list appended), a cached answer, or a fallback message.</returns>
      public virtual async Task<string> AskQuestionAsync( string question )
      {
         try
         {
            // A cache hit short-circuits the network round trip entirely.
            string cachedAnswer = CheckKnownAnswers( question );
            if( cachedAnswer != null )
            {
               return "📌 [Cached Answer]\n\n" + cachedAnswer;
            }

            string answer = await PostChatAsync( question );

            // The server is stateless, so we keep our own transcript for local UX only.
            RecordExchange( question, answer );

            return answer;
         }
         catch( Exception )
         {
            return ServerUnreachableMessage();
         }
      }

      /// <summary>
      /// Discards the in-memory conversation transcript, effectively starting a fresh session
      /// from the user's point of view. Has no effect on the server (which is stateless) or on
      /// the persisted caches.
      /// </summary>
      public virtual void ClearHistory()
      {
         _conversationHistory.Clear();
      }

      /// <summary>
      /// Appends one feedback entry (a thumbs up or thumbs down against a specific question and
      /// answer) to the feedback log. Writes are best-effort: if the log cannot be written we
      /// silently give up, because losing a feedback line must never interrupt the user's chat.
      /// </summary>
      /// <param name="question">The question the user rated.</param>
      /// <param name="answer">The answer the user rated.</param>
      /// <param name="isPositive">True for a thumbs up, false for a thumbs down.</param>
      public void LogFeedback( string question, string answer, bool isPositive )
      {
         try
         {
            var rating = isPositive ? "POSITIVE" : "NEGATIVE";
            var logEntry = $"{DateTime.Now:yyyy-MM-dd HH:mm:ss}|{rating}|Q: {question}|A: {answer}\n";
            System.IO.File.AppendAllText( _feedbackLogPath, logEntry );
         }
         catch
         {
            // Feedback logging is non-critical; never let it break the chat flow.
         }
      }

      /// <summary>
      /// Promotes a question/answer pair into the local known-answers cache so future asks of
      /// the same (normalized) question return instantly without hitting the server. Existing
      /// entries for the same normalized key are overwritten. Returns false, rather than
      /// throwing, if the cache file cannot be read or written.
      /// </summary>
      /// <param name="question">The question to key the cached answer under.</param>
      /// <param name="answer">The answer to cache.</param>
      /// <returns>True if the cache was updated and persisted; false on any failure.</returns>
      public bool AddToKnownAnswers( string question, string answer )
      {
         try
         {
            var knownAnswers = FileHelper.ReadJson<Dictionary<string, string>>( _knownAnswersPath, "KnownAnswers" )
                               ?? new Dictionary<string, string>();
            knownAnswers[NormalizeQuestion( question )] = answer;
            return FileHelper.WriteJson( _knownAnswersPath, knownAnswers, true, "KnownAnswers" );
         }
         catch
         {
            return false;
         }
      }

      /// <summary>
      /// Re-asks a question when the user was unhappy with the first answer (the "Bad" path).
      /// The retry is recorded in the feedback log, then the question is re-posted with an
      /// explicit request for more detail so the server's LLM produces a fuller response. As
      /// with the initial ask, network failures fall back to the friendly unreachable message.
      /// </summary>
      /// <param name="question">Original question to retry.</param>
      /// <returns>A new (hopefully more detailed) answer, or a fallback message on failure.</returns>
      public virtual async Task<string> RetryWithMoreDetailAsync( string question )
      {
         try
         {
            // Record that the first answer was rejected before we try again.
            LogFeedback( question, "[RETRY REQUESTED]", isPositive: false );

            string answer = await PostChatAsync( "Please answer in more detail: " + question );

            return "🔄 [Retry with more detail]\n\n" + answer;
         }
         catch( Exception )
         {
            return ServerUnreachableMessage();
         }
      }

      #endregion Public Methods

      #region Private Methods

      /// <summary>
      /// The single canonical "server unreachable" message, shown whenever a /chat call fails
      /// or the server is down. Centralized so the wording (and the server URL it names) stays
      /// consistent across every failure path.
      /// </summary>
      protected static string ServerUnreachableMessage() => $"Could not reach the Forager server at {Config.ServerUrl}. Is it running?";

      /// <summary>
      /// Posts a question to the server's /chat endpoint and returns the answer text. On a
      /// non-success status code it returns the standard unreachable message instead of
      /// throwing. When the server includes a "sources" array, a short human-readable Sources
      /// list is appended to the answer.
      /// </summary>
      /// <param name="question">The question to send to the server.</param>
      /// <returns>The server's answer, optionally with an appended Sources section.</returns>
      protected async Task<string> PostChatAsync( string question )
      {
         var requestBody = new { question = question };
         var json = JsonConvert.SerializeObject( requestBody );
         var content = new StringContent( json, Encoding.UTF8, "application/json" );

         var response = await _httpClient.PostAsync( Config.ServerUrl.TrimEnd( '/' ) + "/chat", content );

         if( !response.IsSuccessStatusCode )
         {
            return ServerUnreachableMessage();
         }

         var responseText = await response.Content.ReadAsStringAsync();
         var parsedResponse = JObject.Parse( responseText );

         string answer = parsedResponse["answer"]?.ToString() ?? "";
         string sources = FormatSources( parsedResponse["sources"] as JArray );

         return string.IsNullOrEmpty( sources ) ? answer : answer + sources;
      }

      /// <summary>
      /// Turns the server's "sources" array into a short, bulleted list to append beneath an
      /// answer. This is deliberately forgiving: the server's source objects have historically
      /// used several different property names for the path, so we accept the first of a few
      /// common ones and quietly skip anything we cannot make sense of. Returns an empty string
      /// (not null) when there is nothing worth showing, so callers can concatenate freely.
      /// </summary>
      /// <param name="sources">The raw "sources" array from the server response, possibly null.</param>
      /// <returns>A "\n\nSources:" block, or an empty string when no usable source paths were found.</returns>
      protected string FormatSources( JArray sources )
      {
         var paths = ExtractSourcePaths( sources );
         if( paths.Count == 0 )
         {
            return "";
         }

         var builder = new StringBuilder();
         builder.Append( "\n\nSources:" );
         foreach( var path in paths )
         {
            builder.Append( "\n- " ).Append( path );
         }
         return builder.ToString();
      }

      /// <summary>
      /// Pulls a usable path/identifier string out of each entry in the server's "sources"
      /// array. Handles a null or empty array, non-object entries, and the several property
      /// names the server may use for the path. Empty values are dropped. The result feeds
      /// <see cref="FormatSources"/> and keeps its object-shape tolerance in one place.
      /// </summary>
      /// <param name="sources">The raw "sources" array from the server response, possibly null.</param>
      /// <returns>The list of non-empty source paths, in the order the server returned them.</returns>
      private static List<string> ExtractSourcePaths( JArray sources )
      {
         var paths = new List<string>();
         if( sources == null || sources.Count == 0 )
         {
            return paths;
         }

         foreach( var item in sources )
         {
            var sourceObject = item as JObject;
            if( sourceObject == null )
            {
               continue;
            }

            // Accept the most common path/id property names; skip an entry that has none.
            var value = sourceObject["path"] ?? sourceObject["file"] ?? sourceObject["filePath"] ?? sourceObject["id"];
            if( value != null )
            {
               var text = value.ToString();
               if( !string.IsNullOrEmpty( text ) )
               {
                  paths.Add( text );
               }
            }
         }

         return paths;
      }

      /// <summary>
      /// Reduces a question to a stable cache key by trimming surrounding whitespace and
      /// lower-casing it, so trivially different phrasings ("Foo?" vs " foo? ") resolve to the
      /// same cached answer. This is the single definition of "equivalent question" used by
      /// both the cache reader and writer.
      /// </summary>
      /// <param name="question">The raw question text.</param>
      /// <returns>The normalized cache key.</returns>
      private static string NormalizeQuestion( string question ) => question.Trim().ToLowerInvariant();

      /// <summary>
      /// Looks up a question in the local known-answers cache using the normalized key. Returns
      /// the cached answer on a hit, or null when there is no cache, no match, or the cache
      /// file could not be read. A read failure is treated as a miss so the caller simply falls
      /// through to asking the server.
      /// </summary>
      /// <param name="question">The question to look up.</param>
      /// <returns>The cached answer, or null if not found or unavailable.</returns>
      protected string CheckKnownAnswers( string question )
      {
         try
         {
            var knownAnswers = FileHelper.ReadJson<Dictionary<string, string>>( _knownAnswersPath, "KnownAnswers" );
            if( knownAnswers == null )
               return null;

            return knownAnswers.TryGetValue( NormalizeQuestion( question ), out var cachedAnswer ) ? cachedAnswer : null;
         }
         catch
         {
            return null;
         }
      }

      /// <summary>
      /// Appends a user question and its assistant answer to the in-memory conversation
      /// transcript. The two anonymous entries mirror the classic role/content chat shape purely
      /// for the local UI; nothing here is sent to the (stateless) server.
      /// </summary>
      /// <param name="question">The question the user asked.</param>
      /// <param name="answer">The answer that was returned.</param>
      private void RecordExchange( string question, string answer )
      {
         _conversationHistory.Add( new
         {
            role = "user",
            content = question
         } );
         _conversationHistory.Add( new
         {
            role = "assistant",
            content = answer
         } );
      }

      #endregion Private Methods
   }
}
