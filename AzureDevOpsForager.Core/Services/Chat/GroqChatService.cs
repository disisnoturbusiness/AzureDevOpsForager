namespace AzureDevOpsForager.Core.Services.Chat
{
   /// <summary>
   /// Concrete chat service bound to the Groq LLM provider.
   /// <para>
   /// All of the real work (posting a question to the Forager Server's /chat endpoint,
   /// the local known-answers cache, and feedback logging) lives in <see cref="BaseChatService"/>.
   /// This subclass exists only to name the provider so callers can pick a chat implementation
   /// by type. It carries no state and adds no behavior of its own.
   /// </para>
   /// </summary>
   public class GroqChatService : BaseChatService
   {
      #region Constructor

      /// <summary>
      /// Creates a Groq-flavored chat service. There is nothing Groq-specific to set up on the
      /// client side (the server decides which LLM to call), so this simply defers to the base
      /// constructor, which wires up the HTTP client and the feedback / known-answers file paths.
      /// </summary>
      public GroqChatService()
      {
      }

      #endregion Constructor
   }
}
