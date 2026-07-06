using System;
using System.Drawing;
using AzureDevOpsForager.Core.Services;
using AzureDevOpsForager.Core.Services.Chat;
using AzureDevOpsForager.Shared.UI;

#pragma warning disable VSTHRD100

namespace AzureDevOpsForager.WinForms
{
   /// <summary>
   /// Main window for the Groq-backed Azure DevOps Forager chat client.
   /// It reuses the shared chat UI and behavior from <see cref="BaseMainForm"/> (input box,
   /// chat transcript, feedback buttons, caching) and layers on the Groq-specific pieces:
   /// the wording shown in the status bar and a smarter "thumbs down" that re-asks the
   /// question with more context rather than merely recording that the answer was poor.
   /// </summary>
   public class GroqMainForm : BaseMainForm
   {
      #region Constructor

      /// <summary>
      /// Builds the form, wires it to a Groq chat backend, and greets the user.
      /// Titles the window, hands a fresh <see cref="BaseChatService"/> to the base class so all
      /// shared UI events are bound, then posts an intro message explaining the privacy model
      /// (code is retrieved server-side; only the question plus retrieved snippets reach the LLM).
      /// </summary>
      public GroqMainForm()
      {
         this.Text = "Azure DevOps Forager — Chat";
         InitializeBaseForm( new BaseChatService() );

         AppendToChat( "SYSTEM",
            "Azure DevOps Forager chat ready.\n" +
            "Your code stays on your infrastructure — only your question and the snippets the server retrieves are sent to the LLM.\n" +
            "Ask a question about the indexed codebase.",
            Color.DarkOrange );
      }

      #endregion Constructor

      #region Overrides

      /// <summary>
      /// Status-bar text shown while a request is in flight. Overrides the generic base wording
      /// with a phrase that names the Forager server the Groq client talks to.
      /// </summary>
      protected override string GetProcessingMessage()
      {
         return "Asking the Forager server...";
      }

      /// <summary>
      /// Idle status-bar text shown when the client is ready for input. Nudges the user toward
      /// the app's single purpose (asking a question) instead of the base class's bare "Ready".
      /// </summary>
      protected override string GetDefaultStatusMessage()
      {
         return "Ready - Ask a question";
      }

      /// <summary>
      /// Runs after the transcript is cleared. Posts a system line so the user has a visible
      /// confirmation that both the on-screen history and the server-side conversation context
      /// were reset (a cleared Groq session no longer remembers earlier turns).
      /// </summary>
      protected override void OnChatCleared()
      {
         AppendToChat( "SYSTEM", "Chat cleared - conversation context reset", Color.DarkOrange );
      }

      /// <summary>
      /// Groq-specific "thumbs down" behavior. Where the base class just logs that an answer was
      /// unhelpful, Groq turns the negative signal into a second attempt: it re-asks the last
      /// question with more detail and shows the improved answer, so the user gets value from the
      /// downvote rather than a dead end.
      /// </summary>
      /// <param name="sender">The feedback button that was clicked (unused; required by the handler signature).</param>
      /// <param name="e">Event data for the click (unused; required by the handler signature).</param>
      protected override async void ThumbsDownButton_Click( object sender, EventArgs e )
      {
         // Lock the feedback and ask controls so the user can't fire a second retry mid-flight.
         SetFeedbackButtonsEnabled( false );
         _askButton.Enabled = false;
         _statusLabel.Text = "Retrying with more context...";

         try
         {
            AppendToChat( "SYSTEM", "🔄 Bad answer - retrying with more detail...", Color.DarkOrange );

            var retryAnswer = await _chatService.RetryWithMoreDetailAsync( _lastQuestion );
            AppendToChat( "AI", retryAnswer, Color.Green );
            _lastAnswer = retryAnswer;

            // A fresh answer is on screen, so let the user rate this one too.
            SetFeedbackButtonsEnabled( true );

            _statusLabel.Text = "Retry complete - rate this answer";
         }
         catch( Exception exception )
         {
            AppendToChat( "ERROR", $"Retry failed: {exception.Message}", Color.Red );
            _statusLabel.Text = "Retry failed";
         }
         finally
         {
            // Re-enable Ask no matter what so the window never gets stuck in a disabled state.
            _askButton.Enabled = true;
         }
      }

      #endregion Overrides
   }
}
