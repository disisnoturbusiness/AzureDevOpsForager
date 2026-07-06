using System.Threading.Tasks;

namespace AzureDevOpsForager.Core.Services.Chat
{
   /// <summary>
   /// Abstraction over a Large Language Model chat provider (the current concrete
   /// implementation targets Groq). The chat feature builds up a code context from
   /// search results, then asks the model a natural-language question about that code.
   /// Hiding the provider behind this interface lets the rest of the application, and
   /// the UI in particular, stay provider-agnostic: swapping in a different backend
   /// only requires a new implementation of these members, with no downstream changes.
   /// </summary>
   public interface ILLMProvider
   {
      /// <summary>
      /// Sends a user question to the model along with the code context and prior
      /// turns of the conversation, and returns the model's generated answer.
      /// The context is the assembled code from search results the answer should be
      /// grounded in; the history lets the model resolve follow-up questions that
      /// refer back to earlier turns.
      /// </summary>
      /// <param name="question">The natural-language question the user is asking.</param>
      /// <param name="context">Code context gathered from search results, used to ground the answer.</param>
      /// <param name="conversationHistory">Prior messages in this conversation, oldest to newest, for multi-turn context.</param>
      /// <returns>The answer text produced by the model.</returns>
      Task<string> AskAsync( string question, string context, System.Collections.Generic.List<object> conversationHistory );

      /// <summary>
      /// Clears any conversation state the provider is holding so the next
      /// <see cref="AskAsync"/> call starts a fresh conversation with no memory of
      /// earlier turns. Used when the user begins a new chat.
      /// </summary>
      void ResetConversation();

      /// <summary>
      /// A human-readable name for this provider (for example "Groq"), used in
      /// logging and in the UI so it is clear which backend produced an answer.
      /// </summary>
      string ProviderName
      {
         get;
      }

      /// <summary>
      /// Whether the provider has everything it needs to serve requests (for example
      /// a configured API key). Callers check this before attempting a request so a
      /// missing configuration can be surfaced cleanly rather than failing mid-call.
      /// </summary>
      bool IsConfigured
      {
         get;
      }
   }
}
