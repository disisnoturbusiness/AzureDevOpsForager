using System;
using System.Collections.Generic;
using System.IO;

namespace AzureDevOpsForager.Core.Services.Utilities
{
   /// <summary>
   /// Captures user feedback on chat answers so the quality of the assistant can be
   /// reviewed after the fact. Every rating (a free-text "that isn't what I wanted",
   /// a thumbs-up, or a thumbs-down) is appended as a row to a single JSON file in the
   /// application's local app-data folder. Keeping the history in one flat file makes it
   /// trivial to open, diff, or feed back into prompt-tuning without any database.
   /// </summary>
   public class FeedbackLogger
   {
      #region Data Members

      /// <summary>
      /// Full path to the JSON file that accumulates every feedback entry. It lives under
      /// the shared local app-data root alongside the other persisted state (cache, config,
      /// logs) so all of the tool's artifacts sit in one predictable location.
      /// </summary>
      private readonly string _feedbackFile = Path.Combine( Config.LocalAppDataRoot, "feedback.json" );

      /// <summary>
      /// Guards read-modify-write access to the feedback file. Because a rating is logged by
      /// loading the whole list, appending one row, and writing it back, concurrent callers
      /// could otherwise clobber each other's entries; this lock serializes those bursts.
      /// </summary>
      private readonly object _fileLock = new object();

      #endregion

      #region Public Methods

      /// <summary>
      /// Records a negative rating where the user has told us, in their own words, what the
      /// answer should have been. This is the richest feedback signal: it pairs the original
      /// query and the model's reply with a human-authored correction, which is exactly what
      /// you want when refining prompts or building a known-good answer set.
      /// </summary>
      /// <param name="query">The question the user originally asked.</param>
      /// <param name="groqResponse">The answer the assistant returned.</param>
      /// <param name="userWanted">The user's description of the answer they actually expected.</param>
      public void LogNotWhatIWanted( string query, string groqResponse, string userWanted )
      {
         AppendFeedback( BuildEntry( query, groqResponse, userWanted, isPositive: false ) );
      }

      /// <summary>
      /// Records a positive one-click rating (thumbs up). There is no free-text correction, so
      /// the "what the user wanted" slot is filled with the sentinel THUMBS_UP to mark the row
      /// as a coarse approval rather than a detailed critique.
      /// </summary>
      /// <param name="query">The question the user originally asked.</param>
      /// <param name="answer">The answer the user approved of.</param>
      public void LogThumbsUp( string query, string answer )
      {
         AppendFeedback( BuildEntry( query, answer, "THUMBS_UP", isPositive: true ) );
      }

      /// <summary>
      /// Records a negative one-click rating (thumbs down). Like the thumbs-up path there is no
      /// correction text, so the row is tagged with the THUMBS_DOWN sentinel to distinguish a
      /// quick rejection from the detailed <see cref="LogNotWhatIWanted"/> feedback.
      /// </summary>
      /// <param name="query">The question the user originally asked.</param>
      /// <param name="answer">The answer the user rejected.</param>
      public void LogThumbsDown( string query, string answer )
      {
         AppendFeedback( BuildEntry( query, answer, "THUMBS_DOWN", isPositive: false ) );
      }

      #endregion

      #region Private Methods

      /// <summary>
      /// Builds a timestamped <see cref="FeedbackEntry"/> from the common fields shared by every
      /// rating path. Centralizing construction here keeps the three public log methods to a single
      /// line each and guarantees they stamp the entry identically.
      /// </summary>
      /// <param name="query">The question the user originally asked.</param>
      /// <param name="response">The assistant's answer being rated.</param>
      /// <param name="userWanted">Either the user's correction text or a sentinel (THUMBS_UP / THUMBS_DOWN).</param>
      /// <param name="isPositive">True for approving feedback, false for negative or corrective feedback.</param>
      private static FeedbackEntry BuildEntry( string query, string response, string userWanted, bool isPositive )
      {
         return new FeedbackEntry
         {
            Timestamp = DateTime.Now,
            Query = query,
            GroqResponse = response,
            UserWanted = userWanted,
            IsPositive = isPositive
         };
      }

      /// <summary>
      /// Appends a single entry to the feedback file under the file lock. The existing list is read
      /// (treating a missing or empty file as an empty list), the new entry is added, and the whole
      /// list is rewritten as formatted JSON. Any failure is swallowed and logged rather than thrown:
      /// dropping a piece of feedback should never interrupt the user's actual work.
      /// </summary>
      /// <param name="entry">The rating to persist.</param>
      private void AppendFeedback( FeedbackEntry entry )
      {
         lock ( _fileLock )
         {
            try
            {
               var entries = FileHelper.ReadJson<List<FeedbackEntry>>( _feedbackFile, "FeedbackLogger" ) ?? new List<FeedbackEntry>();
               entries.Add( entry );
               FileHelper.WriteJson( _feedbackFile, entries, true, "FeedbackLogger" );
            }
            catch ( Exception ex )
            {
               Logger.Log( "ERROR", "FeedBackFile", $"Failed to write feedback: {ex.Message}" );
            }
         }
      }

      #endregion
   }

   /// <summary>
   /// One persisted feedback record: a snapshot of a single query/answer pair together with the
   /// user's verdict on it. Serialized directly to and from the feedback JSON file.
   /// </summary>
   public class FeedbackEntry
   {
      /// <summary>When the feedback was captured (local machine time).</summary>
      public DateTime Timestamp { get; set; }

      /// <summary>The question the user originally asked the assistant.</summary>
      public string Query { get; set; }

      /// <summary>The answer the assistant returned for the query.</summary>
      public string GroqResponse { get; set; }

      /// <summary>
      /// For corrective feedback, the user's description of the answer they actually wanted;
      /// for one-click ratings, the sentinel THUMBS_UP or THUMBS_DOWN.
      /// </summary>
      public string UserWanted { get; set; }

      /// <summary>True when the entry represents approving feedback, false otherwise.</summary>
      public bool IsPositive { get; set; }
   }
}
