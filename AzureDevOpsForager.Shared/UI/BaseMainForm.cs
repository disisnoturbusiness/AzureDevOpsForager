using System;
using System.Drawing;
using System.Windows.Forms;
using AzureDevOpsForager.Core.Services.Chat;
using AzureDevOpsForager.Core.Services.Utilities;

#pragma warning disable VSTHRD100

namespace AzureDevOpsForager.Shared.UI
{
   /// <summary>
   /// Shared base class for every AzureDevOpsForager WinForms chat window. It owns the common
   /// conversation loop (ask a question, show the answer, collect a rating) so that each concrete
   /// application (for example the Groq client) only has to supply provider-specific wording and
   /// behavior. Keeping this logic in one place means the feedback caching and logging story stays
   /// identical no matter which chat backend a derived form talks to.
   /// </summary>
   public abstract partial class BaseMainForm : Form
   {
      #region Data Members

      /// <summary>Scrolling transcript that shows the running conversation between the user and the AI.</summary>
      protected RichTextBox _chatHistory;

      /// <summary>Input box where the user types the question they want to ask the indexed codebase.</summary>
      protected TextBox _questionBox;

      /// <summary>Button that submits the current question to the chat service.</summary>
      protected Button _askButton;

      /// <summary>Button that wipes the transcript and resets the underlying conversation context.</summary>
      protected Button _clearButton;

      /// <summary>Positive-rating button; caching a good answer makes it instant next time.</summary>
      protected Button _thumbsUpButton;

      /// <summary>Negative-rating button; logs dissatisfaction (and, in derived forms, can trigger a retry).</summary>
      protected Button _thumbsDownButton;

      /// <summary>Button for "close but not what I meant" feedback, which opens a free-text follow-up dialog.</summary>
      protected Button _notWhatIWantButton;

      /// <summary>Status line at the bottom of the form used to communicate progress and outcomes to the user.</summary>
      protected Label _statusLabel;

      /// <summary>The chat backend this form talks to. Supplied by the derived class so the base loop stays provider-agnostic.</summary>
      protected BaseChatService _chatService;

      /// <summary>In-memory cache of previously approved answers so repeat questions can be served instantly.</summary>
      protected SmartCache _cache;

      /// <summary>Writes user feedback (thumbs up/down, corrections) to the feedback log for later review.</summary>
      protected FeedbackLogger _feedback;

      /// <summary>The most recent question the user asked; retained so a rating can be attributed to it.</summary>
      protected string _lastQuestion;

      /// <summary>The most recent answer that was displayed; retained so it can be cached or logged when rated.</summary>
      protected string _lastAnswer;

      #endregion Data Members

      #region Constructor

      /// <summary>
      /// Wires up the shared form once the derived class has built its designer controls. Derived
      /// classes must call this after InitializeComponent() so that the control references resolved
      /// through the virtual properties below are valid. Creating the cache and feedback logger here
      /// (rather than in a real constructor) keeps this reusable across forms that each generate
      /// their own component initialization.
      /// </summary>
      /// <param name="chatService">The provider-specific chat backend; must not be null.</param>
      protected void InitializeBaseForm( BaseChatService chatService )
      {
         InitializeComponent();

         _chatService = chatService ?? throw new ArgumentNullException( nameof( chatService ) );
         _cache = new SmartCache();
         _feedback = new FeedbackLogger();

         // Route every interactive control through the shared handlers. Using the virtual *Control
         // properties (not the fields directly) lets a derived form remap a control if it needs to.
         AskButtonControl.Click += AskButton_Click;
         ClearButtonControl.Click += ClearButton_Click;
         ThumbsUpButtonControl.Click += ThumbsUpButton_Click;
         ThumbsDownButtonControl.Click += ThumbsDownButton_Click;
         NotWhatIWantButtonControl.Click += NotWhatIWantButton_Click;
         QuestionTextBoxControl.KeyDown += QuestionBox_KeyDown;
      }

      #endregion Constructor

      #region Private Methods

      // --- Control accessors -------------------------------------------------
      // These virtual properties expose each backing field so a derived form can substitute a
      // different control instance without the base logic caring where the control came from.

      /// <summary>The chat transcript control the base logic reads from and writes to.</summary>
      protected virtual RichTextBox ChatHistoryControl => _chatHistory;

      /// <summary>The question input control the base logic reads the pending question from.</summary>
      protected virtual TextBox QuestionTextBoxControl => _questionBox;

      /// <summary>The submit control the base logic hooks the ask handler onto.</summary>
      protected virtual Button AskButtonControl => _askButton;

      /// <summary>The reset control the base logic hooks the clear handler onto.</summary>
      protected virtual Button ClearButtonControl => _clearButton;

      /// <summary>The positive-rating control the base logic hooks the thumbs-up handler onto.</summary>
      protected virtual Button ThumbsUpButtonControl => _thumbsUpButton;

      /// <summary>The negative-rating control the base logic hooks the thumbs-down handler onto.</summary>
      protected virtual Button ThumbsDownButtonControl => _thumbsDownButton;

      /// <summary>The "not what I wanted" control the base logic hooks its handler onto.</summary>
      protected virtual Button NotWhatIWantButtonControl => _notWhatIWantButton;

      /// <summary>The status label the base logic writes progress and outcome messages to.</summary>
      protected virtual Label StatusLabelControl => _statusLabel;

      // --- Event handlers ----------------------------------------------------

      /// <summary>Ask button click: run the shared question flow.</summary>
      private async void AskButton_Click( object sender, EventArgs e )
      {
         await AskQuestionAsync();
      }

      /// <summary>
      /// Keyboard shortcut in the question box: Ctrl+Enter submits the question, matching the
      /// muscle memory most chat tools use. The key press is swallowed so no stray newline lands
      /// in the input box.
      /// </summary>
      private async void QuestionBox_KeyDown( object sender, KeyEventArgs e )
      {
         if( e.Control && e.KeyCode == Keys.Enter )
         {
            e.Handled = true;
            e.SuppressKeyPress = true;
            await AskQuestionAsync();
         }
      }

      /// <summary>
      /// Clear button click: empty the transcript, drop the service's conversation context, reset
      /// the status line, and disable the rating buttons (there is nothing to rate once cleared).
      /// </summary>
      private void ClearButton_Click( object sender, EventArgs e )
      {
         ChatHistoryControl.Clear();
         _chatService.ClearHistory();
         OnChatCleared();
         StatusLabelControl.Text = GetDefaultStatusMessage();
         SetFeedbackButtonsEnabled( false );
      }

      /// <summary>
      /// Thumbs-up click: the user approved the last answer, so cache it locally for instant reuse,
      /// log the approval, and try to persist it to the shared known-answers store. The status line
      /// reflects whether the network save succeeded, since that determines whether other users
      /// benefit from this answer too.
      /// </summary>
      private void ThumbsUpButton_Click( object sender, EventArgs e )
      {
         _cache.Add( _lastQuestion, _lastAnswer );
         _feedback.LogThumbsUp( _lastQuestion, _lastAnswer );

         bool savedToNetwork = _chatService.AddToKnownAnswers( _lastQuestion, _lastAnswer );
         StatusLabelControl.Text = savedToNetwork
            ? "Answer cached - will be instant next time!"
            : "Cached locally (network save failed)";

         SetFeedbackButtonsEnabled( false );
      }

      /// <summary>
      /// Thumbs-down click: record that the answer missed. The base behavior simply logs the
      /// negative feedback; derived forms override this to do something smarter (the Groq form, for
      /// instance, retries the question with more context).
      /// </summary>
      protected virtual void ThumbsDownButton_Click( object sender, EventArgs e )
      {
         _feedback.LogThumbsDown( _lastQuestion, _lastAnswer );
         StatusLabelControl.Text = "Feedback logged";
         SetFeedbackButtonsEnabled( false );
      }

      /// <summary>
      /// "Not what I wanted" click: the answer was on-topic but off-target, so prompt the user to
      /// describe what they actually needed. That free-text correction is the most useful signal we
      /// can capture, so it is logged verbatim against the question/answer pair.
      /// </summary>
      private void NotWhatIWantButton_Click( object sender, EventArgs e )
      {
         var correction = PromptForCorrection();
         if( !string.IsNullOrEmpty( correction ) )
         {
            _feedback.LogNotWhatIWanted( _lastQuestion, _lastAnswer, correction );
            StatusLabelControl.Text = "Feedback submitted - thank you!";
         }

         SetFeedbackButtonsEnabled( false );
      }

      // --- Correction dialog -------------------------------------------------

      /// <summary>
      /// Builds and shows the modal "what did you want instead?" dialog and returns the trimmed
      /// text the user entered, or null if they cancelled or left it blank. The dialog is built by
      /// hand (rather than a designer) because it is a one-off with no reuse elsewhere.
      /// </summary>
      /// <returns>The user's correction text, or null when nothing usable was supplied.</returns>
      private string PromptForCorrection()
      {
         using( var dialog = new Form() )
         {
            dialog.Text = "What did you want instead?";
            dialog.Width = 500;
            dialog.Height = 250;
            dialog.FormBorderStyle = FormBorderStyle.FixedDialog;
            dialog.StartPosition = FormStartPosition.CenterParent;

            var correctionTextBox = new TextBox
            {
               Multiline = true,
               ScrollBars = ScrollBars.Vertical,
               Dock = DockStyle.Fill,
               Font = new Font( "Consolas", 10 )
            };

            var buttonPanel = BuildCorrectionButtonPanel( dialog );
            dialog.Controls.Add( correctionTextBox );
            dialog.Controls.Add( buttonPanel );

            if( dialog.ShowDialog() == DialogResult.OK )
            {
               var correction = correctionTextBox.Text.Trim();
               return string.IsNullOrEmpty( correction ) ? null : correction;
            }

            return null;
         }
      }

      /// <summary>
      /// Creates the bottom button strip (Submit / Cancel) for the correction dialog and wires the
      /// Cancel button up as the dialog's cancel action so Esc works as expected. Layout
      /// coordinates match the fixed-size dialog defined above.
      /// </summary>
      /// <param name="owner">The dialog the buttons belong to; used to set its CancelButton.</param>
      /// <returns>A docked panel containing the two buttons.</returns>
      private Panel BuildCorrectionButtonPanel( Form owner )
      {
         var panel = new Panel { Dock = DockStyle.Bottom, Height = 40 };

         var submitButton = new Button
         {
            Text = "&Submit",
            DialogResult = DialogResult.OK,
            Location = new Point( 300, 8 ),
            Width = 80
         };
         var cancelButton = new Button
         {
            Text = "Cancel",
            DialogResult = DialogResult.Cancel,
            Location = new Point( 390, 8 ),
            Width = 80
         };

         panel.Controls.Add( submitButton );
         panel.Controls.Add( cancelButton );
         owner.CancelButton = cancelButton;
         return panel;
      }

      // --- Conversation flow -------------------------------------------------

      /// <summary>
      /// The heart of the form: validate the pending question, echo it into the transcript, serve
      /// it from cache when possible (otherwise call the chat service), display the answer, and
      /// enable the rating buttons. Wrapped so the UI is always re-enabled and refocused even if the
      /// service throws. Marked virtual so a derived form can wrap or replace the flow.
      /// </summary>
      protected virtual async System.Threading.Tasks.Task AskQuestionAsync()
      {
         var question = QuestionTextBoxControl.Text.Trim();
         if( string.IsNullOrEmpty( question ) )
         {
            return;
         }

         SetUIEnabled( false );
         StatusLabelControl.Text = GetProcessingMessage();

         // If the request runs long it is almost certainly a scale-to-zero HF endpoint cold-starting; a
         // one-time transcript notice (cancelled the instant the answer lands) keeps the wait from reading
         // as a freeze, without nagging on fast/warm requests.
         var warmupNotice = new System.Threading.CancellationTokenSource();

         try
         {
            AppendToChat( "YOU", question, Color.Blue );
            _lastQuestion = question;
            _ = ShowWarmupNoticeIfSlowAsync( warmupNotice.Token );
            await ResolveAndShowAnswerAsync( question );

            SetFeedbackButtonsEnabled( true );
            QuestionTextBoxControl.Clear();
         }
         catch( Exception exception )
         {
            AppendToChat( "ERROR", exception.Message, Color.Red );
            StatusLabelControl.Text = "Error occurred";
         }
         finally
         {
            warmupNotice.Cancel();
            SetUIEnabled( true );
            QuestionTextBoxControl.Focus();
         }
      }

      /// <summary>
      /// After a short delay, if the in-flight question is still running, drops a one-time notice into the
      /// transcript that the Hugging Face endpoints are warming up — so a scale-to-zero cold-start (up to a
      /// minute on the first request after idle) reads as "warming up" rather than a freeze. Cancelled the
      /// moment the answer arrives, so fast/warm questions never show it.
      /// </summary>
      /// <param name="token">Cancelled by the caller as soon as the answer (or an error) is in.</param>
      private async System.Threading.Tasks.Task ShowWarmupNoticeIfSlowAsync( System.Threading.CancellationToken token )
      {
         try
         {
            await System.Threading.Tasks.Task.Delay( TimeSpan.FromSeconds( 6 ), token );
            if( token.IsCancellationRequested || !IsHandleCreated )
            {
               return;
            }

            BeginInvoke( (Action)( () =>
            {
               if( !token.IsCancellationRequested )
               {
                  AppendToChat( "SYSTEM", "Waking up the server - the search endpoints and the database can take up to 2 minutes to spin up on the first request after an idle period...", Color.DarkOrange );
               }
            } ) );
         }
         catch( System.Threading.Tasks.TaskCanceledException )
         {
            // Answer arrived before the delay elapsed (warm/fast) - nothing to show.
         }
      }

      /// <summary>
      /// Produces the answer for a question, preferring the local cache (instant, no network call)
      /// and falling back to the chat service. Either way the answer is stored as the last answer,
      /// shown in the transcript, and reflected in the status line; a cache hit additionally reports
      /// the running cache hit rate so the user can see the cache paying off.
      /// </summary>
      /// <param name="question">The already-trimmed, non-empty question to answer.</param>
      private async System.Threading.Tasks.Task ResolveAndShowAnswerAsync( string question )
      {
         if( _cache.TryGet( question, out var cachedAnswer ) )
         {
            _lastAnswer = cachedAnswer;
            AppendToChat( "AI", $"[CACHED] {cachedAnswer}", Color.Green );
            StatusLabelControl.Text = $"Cached answer (hit rate: {_cache.HitRate:P0})";
         }
         else
         {
            var answer = await _chatService.AskQuestionAsync( question );
            _lastAnswer = answer;
            AppendToChat( "AI", answer, Color.Green );
            StatusLabelControl.Text = GetDefaultStatusMessage();
         }
      }

      // --- Transcript rendering ----------------------------------------------

      /// <summary>
      /// Appends a formatted turn to the transcript: a gray bold timestamp, the speaker label in
      /// the caller's color, then the message body in plain black. Each write manipulates the
      /// RichTextBox selection so only the intended span picks up the color/font, then scrolls to
      /// keep the newest text in view.
      /// </summary>
      /// <param name="speaker">Short label for who is talking (for example YOU, AI, ERROR).</param>
      /// <param name="message">The message body to render.</param>
      /// <param name="speakerColor">The color used for the speaker label.</param>
      protected virtual void AppendToChat( string speaker, string message, Color speakerColor )
      {
         var timestamp = DateTime.Now.ToString( "HH:mm:ss" );

         ChatHistoryControl.SelectionStart = ChatHistoryControl.TextLength;
         ChatHistoryControl.SelectionLength = 0;
         ChatHistoryControl.SelectionColor = Color.Gray;
         ChatHistoryControl.SelectionFont = new Font( ChatHistoryControl.Font.FontFamily, 9, FontStyle.Bold );
         ChatHistoryControl.AppendText( $"[{timestamp}] " );

         ChatHistoryControl.SelectionColor = speakerColor;
         ChatHistoryControl.AppendText( $"{speaker}:\n" );

         ChatHistoryControl.SelectionColor = Color.Black;
         ChatHistoryControl.SelectionFont = new Font( ChatHistoryControl.Font.FontFamily, 9, FontStyle.Regular );
         ChatHistoryControl.AppendText( $"{message}\n\n" );

         ChatHistoryControl.SelectionStart = ChatHistoryControl.TextLength;
         ChatHistoryControl.ScrollToCaret();
      }

      // --- UI state helpers --------------------------------------------------

      /// <summary>
      /// Toggles the controls that must be locked while a question is in flight (the ask button and
      /// the question box) so the user cannot fire a second request mid-answer. Virtual so a derived
      /// form can extend the set of controls it disables.
      /// </summary>
      /// <param name="enabled">True to re-enable input, false to lock it during processing.</param>
      protected virtual void SetUIEnabled( bool enabled )
      {
         AskButtonControl.Enabled = enabled;
         QuestionTextBoxControl.Enabled = enabled;
      }

      /// <summary>
      /// Enables or disables the three rating buttons (thumbs up, thumbs down, not-what-I-wanted)
      /// as a group. They are only meaningful once an answer exists, so the flow enables them after
      /// a successful answer and disables them again once a rating is given or the chat is cleared.
      /// </summary>
      /// <param name="enabled">True to allow rating, false to hide the option.</param>
      protected void SetFeedbackButtonsEnabled( bool enabled )
      {
         ThumbsUpButtonControl.Enabled = enabled;
         ThumbsDownButtonControl.Enabled = enabled;
         NotWhatIWantButtonControl.Enabled = enabled;
      }

      // --- Overridable behavior hooks ----------------------------------------

      /// <summary>
      /// Extension point invoked right after the chat is cleared. The base does nothing; derived
      /// forms override it to, for example, print a "context reset" banner into the fresh transcript.
      /// </summary>
      protected virtual void OnChatCleared()
      {
      }

      /// <summary>
      /// The status text shown while a question is being answered. Overridable so each provider can
      /// use its own wording (the base default is a generic "Getting answer...").
      /// </summary>
      protected virtual string GetProcessingMessage()
      {
         return "Getting answer...";
      }

      /// <summary>
      /// The idle status text shown when the form is ready for input. Overridable so each provider
      /// can tailor the prompt (the base default is simply "Ready").
      /// </summary>
      protected virtual string GetDefaultStatusMessage()
      {
         return "Ready";
      }

      #endregion Private Methods
   }
}
