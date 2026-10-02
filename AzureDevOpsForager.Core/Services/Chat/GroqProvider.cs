using System;
using System.Collections.Generic;
using System.Net.Http;
using System.Text;
using System.Threading.Tasks;
using Newtonsoft.Json;
using Newtonsoft.Json.Linq;

namespace AzureDevOpsForager.Core.Services.Chat;
/// <summary>
/// LLM provider backed by Groq's hosted inference API. This is the concrete implementation
/// of <see cref="ILLMProvider"/> that the /chat feature talks to when answering questions
/// about a codebase. Groq is used because its free tier is generous, its responses come back
/// quickly (roughly 10-15 seconds), and the model it serves is strong on code.
/// The provider is effectively stateless: every request carries its own system prompt and
/// code context, so nothing needs to be retained between calls.
/// </summary>
public class GroqProvider : ILLMProvider
{
   #region Data Members

   /// <summary>
   /// The Groq-hosted model this provider requests, overridable with the GROQ_MODEL environment
   /// variable.
   /// <para>
   /// Was <c>llama-3.3-70b-versatile</c> until Groq retired the entire Llama family. The failure is
   /// not graceful: the API answers 404 <c>model_not_found</c> and the demo surfaces a red error box
   /// under a search that otherwise worked, because retrieval succeeds and only the answer step dies.
   /// A vendor withdrawing a model should cost a setting change and a restart, not a rebuild and a
   /// redeploy, which is why this is no longer a compile-time constant.
   /// </para>
   /// <para>
   /// Default is <c>qwen/qwen3.8-27b</c>, chosen on measured results rather than on parameter count.
   /// Across 76 graded seats per model in the LLMQuorum harness it answered 23.7% correct against
   /// 18.4% for the far larger <c>openai/gpt-oss-120b</c>, hallucinated less (36 against 44) and was
   /// faster (428 ms against 611 ms). It also beats the retired <c>llama-3.3-70b</c> it replaces
   /// (17.1%), so this is an upgrade rather than a substitution. The hallucination margin is the
   /// reason it wins: a grounded code-search answer that invents detail is the worst failure this
   /// demo has.
   /// </para>
   /// <para>
   /// Those figures are general-knowledge questions answered from memory, not code questions
   /// answered over retrieved context, so they rank these models against each other rather than
   /// predicting absolute quality here.
   /// </para>
   /// <para>
   /// Avoid the <c>openai/gpt-oss-*</c> models unless <c>max_tokens</c> is generous. They emit
   /// reasoning tokens that count against the budget and arrive in a separate <c>reasoning</c>
   /// field, so a small budget returns EMPTY content with a success status, which reads as a broken
   /// response rather than a truncated one. The 3000 configured below is clear of it, and
   /// qwen3.8-27b has no reasoning overhead at all.
   /// </para>
   /// </summary>
   private static readonly string GroqModel =
      System.Environment.GetEnvironmentVariable( "GROQ_MODEL" ) is string configured
      && !string.IsNullOrWhiteSpace( configured )
         ? configured
         : "qwen/qwen3.8-27b";

   /// <summary>
   /// Output-token ceiling sent with each request, overridable with the GROQ_MAX_TOKENS environment
   /// variable.
   /// <para>
   /// This is a rate-limit setting as much as a length setting. Groq's on-demand tier enforces an
   /// output-tokens-per-minute budget (1000 on this account) and rejects a request outright when the
   /// <c>max_tokens</c> it asks for exceeds the budget REMAINING in the current window, not when the
   /// answer actually gets that long. The previous 3000 therefore failed with
   /// <c>rate_limit_exceeded</c> any time the preceding minute had seen traffic, while succeeding on
   /// an idle window, which made it look intermittent rather than misconfigured.
   /// </para>
   /// <para>
   /// 800 keeps a full-length answer inside the per-minute budget. Raising it trades the ability to
   /// answer twice in a minute for longer single answers, so it belongs with the tier rather than in
   /// the source.
   /// </para>
   /// </summary>
   private static readonly int GroqMaxTokens =
      int.TryParse( System.Environment.GetEnvironmentVariable( "GROQ_MAX_TOKENS" ),
         System.Globalization.NumberStyles.Integer,
         System.Globalization.CultureInfo.InvariantCulture, out var configuredMaxTokens )
      && configuredMaxTokens > 0
         ? configuredMaxTokens
         : 800;

   /// <summary>
   /// Absolute URL of Groq's OpenAI-compatible chat completions endpoint. Kept as a constant
   /// so the one network contract this class depends on lives in a single place.
   /// </summary>
   private const string GroqChatCompletionsUrl = "https://api.groq.com/openai/v1/chat/completions";

   /// <summary>
   /// Prefix stamped on every user-facing failure string. Groq answers are returned as plain
   /// text (not exceptions), so errors are surfaced inline with a consistent marker the chat
   /// UI can recognise and style. Centralised here so all failure messages read the same way.
   /// </summary>
   private const string ErrorPrefix = "❌";

   /// <summary>
   /// Reusable HTTP client pre-loaded with the bearer Authorization header and a request
   /// timeout. Created once in the constructor and shared across all requests, which is the
   /// recommended usage for <see cref="HttpClient"/>.
   /// </summary>
   private readonly HttpClient _groqClient;

   #endregion Data Members

   #region Constructor

   /// <summary>
   /// Resolves the API key, builds the shared HTTP client, and records whether the provider
   /// is usable. If no key can be found the client is still created (so the type is safe to
   /// construct), but <see cref="IsConfigured"/> stays false and callers can degrade the
   /// /chat feature gracefully rather than crashing.
   /// </summary>
   public GroqProvider()
   {
      string apiKey = ReadGroqApiKey();

      _groqClient = new HttpClient();
      if( !string.IsNullOrWhiteSpace( apiKey ) )
      {
         _groqClient.DefaultRequestHeaders.Add( "Authorization", $"Bearer {apiKey}" );
      }
      _groqClient.Timeout = TimeSpan.FromMinutes( 2 );

      IsConfigured = !string.IsNullOrWhiteSpace( apiKey );
   }

   #endregion Constructor

   #region Public Methods

   /// <summary>
   /// Answers a user's question by calling Groq. The outgoing request is assembled as an
   /// ordered messages array (system prompt, then the current question with any code context
   /// appended), posted to Groq, and the model's reply is extracted from the JSON response.
   /// Any failure, whether an HTTP error or a thrown exception, is returned as an inline error
   /// string rather than propagated, because the chat UI displays whatever text comes back.
   /// </summary>
   /// <param name="question">The user's natural-language question.</param>
   /// <param name="context">Relevant code retrieved for the question; may be empty.</param>
   /// <returns>The model's answer, or an error string prefixed with the failure marker.</returns>
   public async Task<string> AskAsync( string question, string context )
   {
      try
      {
         var messages = BuildMessages( question, context );
         var requestContent = BuildRequestContent( messages );

         var response = await _groqClient.PostAsync( GroqChatCompletionsUrl, requestContent );
         var responseText = await response.Content.ReadAsStringAsync();

         if( !response.IsSuccessStatusCode )
         {
            return $"{ErrorPrefix} Groq API error: {response.StatusCode}\n{responseText}";
         }

         return ParseAnswer( responseText );
      }
      catch( Exception exception )
      {
         return $"{ErrorPrefix} Groq Error: {exception.Message}";
      }
   }

   /// <summary>
   /// True when an API key was found at construction time. When false the /chat feature
   /// knows to disable or warn rather than issue requests that would be rejected.
   /// </summary>
   public bool IsConfigured
   {
      get; private set;
   }

   #endregion Public Methods

   #region Private Methods

   /// <summary>
   /// Assembles the ordered messages array Groq expects: the system prompt first, then the
   /// current user turn. Splitting this out keeps <see cref="AskAsync"/> focused on the
   /// request/response round trip.
   /// </summary>
   private List<object> BuildMessages( string question, string context )
   {
      var messages = new List<object>();

      // The system prompt varies depending on whether we have code context to ground answers in.
      messages.Add( new
      {
         role = "system",
         content = BuildSystemPrompt( !string.IsNullOrEmpty( context ) )
      } );

      // Prepend the code context to the question so the model sees the evidence before the ask.
      string userMessage = string.IsNullOrEmpty( context )
          ? question
          : $"{context}\n\nQuestion: {question}";

      messages.Add( new
      {
         role = "user",
         content = userMessage
      } );

      return messages;
   }

   /// <summary>
   /// Serialises the request body (model, messages, and sampling parameters) into the
   /// JSON HTTP content Groq expects. Temperature is kept low for deterministic, factual
   /// code answers; the output ceiling is GroqMaxTokens, which is a rate-limit setting as much as a length one
   /// enough to avoid the rate limits a 6000-token ceiling tends to trip.
   /// </summary>
   private StringContent BuildRequestContent( List<object> messages )
   {
      var requestBody = new
      {
         model = GroqModel,
         messages = messages,
         temperature = 0.1,
         max_tokens = GroqMaxTokens,
         top_p = 0.9
      };

      var requestJson = JsonConvert.SerializeObject( requestBody );
      return new StringContent( requestJson, Encoding.UTF8, "application/json" );
   }

   /// <summary>
   /// Pulls the assistant's answer out of Groq's JSON response, drilling into the first
   /// choice's message content. Returns a friendly error string if the expected shape is
   /// missing so the caller never has to handle a null.
   /// </summary>
   private string ParseAnswer( string responseText )
   {
      var result = JObject.Parse( responseText );
      var answer = result["choices"]?[0]?["message"]?["content"]?.ToString();
      return answer ?? $"{ErrorPrefix} No response from Groq";
   }

   /// <summary>
   /// Resolves the Groq API key via the consolidated secret store: the GROQ_API_KEY environment
   /// variable takes precedence, falling back to the "GROQ_API_KEY" entry in the encrypted secrets.enc
   /// (or the legacy groqapikey.enc). Returns null when no key is available so /chat degrades gracefully.
   /// </summary>
   private string ReadGroqApiKey()
      => AzureDevOpsForager.Core.Services.Utilities.SecretStore.Get( "GROQ_API_KEY" );

   /// <summary>
   /// Produces the system prompt that steers the model. When code context is present the
   /// prompt demands grounded, verbatim-code answers with citations and forbids placeholder
   /// stand-ins; without context it falls back to a general expert-engineer instruction.
   /// </summary>
   /// <param name="hasContext">True when retrieved code was supplied for the question.</param>
   private string BuildSystemPrompt( bool hasContext )
   {
      if( hasContext )
      {
         return @"You are an expert software engineer answering questions about a codebase.
You have been given the most relevant code retrieved for the question. Ground every answer in that code — SHOW ACTUAL CODE, not vague summaries.

RULES:
- When asked to 'show' or 'see' something, return the FULL class/method code from the provided context.
- When asked what is abstract / virtual / async, show the ACTUAL signatures from the context.
- When asked what properties or members exist, list their names and types from the context.
- Prefer complete method bodies over signatures; include namespaces and inheritance.
- Cite the specific file (and line numbers when available) you drew each answer from.
- For 'how to' questions, explain the pattern first, then show a concrete example from the context.
- If the context does not contain enough to answer, say so plainly rather than guessing.

NEVER use placeholder phrases like '// implementation', '// rest of code', '// ... (rest of the method)', 'implementation not shown', or 'truncated'. If the context contains a method body, output it completely. NO PLACEHOLDERS.";
      }
      else
      {
         return @"You are an expert software engineer.
Answer thoroughly and accurately, with complete, runnable code examples where relevant.";
      }
   }

   #endregion Private Methods
}
