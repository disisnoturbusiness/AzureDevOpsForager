using System.Threading.Tasks;

namespace AzureDevOpsForager.Core.Services.Chat;
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
   /// Sends a user question to the model along with the code context and returns the
   /// model's generated answer. The context is the assembled code from search results
   /// the answer should be grounded in.
   /// </summary>
   /// <param name="question">The natural-language question the user is asking.</param>
   /// <param name="context">Code context gathered from search results, used to ground the answer.</param>
   /// <returns>The answer text produced by the model.</returns>
   Task<string> AskAsync( string question, string context );

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
