using System;
using System.Drawing;
using System.Windows.Forms;

namespace AzureDevOpsForager.Shared.UI;
partial class BaseMainForm
{
   // Palette matched to the Indexer form so the two windows read as one product.
   private static readonly Color SectionGreen = Color.FromArgb( 198, 224, 180 );
   private static readonly Color SectionWhite = Color.FromArgb( 242, 242, 242 );
   private static readonly Color SectionRed = Color.FromArgb( 244, 199, 195 );
   private static readonly Color AccentBlue = Color.FromArgb( 0, 90, 158 );

   /// <summary>
   /// Builds the chat window with three group sections (Conversation / Ask / Rate), rounded buttons, and
   /// the same colored-section look as the Indexer. A root TableLayoutPanel keeps the transcript growing
   /// while the Ask and Rate strips stay put; each section's content sits in a nested TableLayout so it
   /// lays out deterministically on resize (no manual coordinates).
   /// </summary>
   private void InitializeComponent()
   {
      this.Text = "Azure DevOps Forager — Chat";
      this.Size = new Size( 1180, 860 );
      this.MinimumSize = new Size( 820, 640 );
      this.StartPosition = FormStartPosition.CenterScreen;
      this.BackColor = SystemColors.Control;
      this.Font = new Font( "Segoe UI", 10.5f );
      this.Padding = new Padding( 12 );

      var root = new TableLayoutPanel
      {
         Dock = DockStyle.Fill,
         ColumnCount = 1,
         RowCount = 3,
         BackColor = SystemColors.Control
      };
      root.RowStyles.Add( new RowStyle( SizeType.Percent, 100f ) );
      root.RowStyles.Add( new RowStyle( SizeType.Absolute, 150f ) );
      root.RowStyles.Add( new RowStyle( SizeType.Absolute, 110f ) );
      this.Controls.Add( root );

      // ---- Conversation section (green) : the transcript ----
      var convo = new SectionPanel { HeaderText = "Conversation", HeaderColor = SectionGreen, Dock = DockStyle.Fill, Margin = new Padding( 0, 0, 0, 8 ) };
      _chatHistory = new RichTextBox
      {
         Dock = DockStyle.Fill,
         ReadOnly = true,
         BackColor = Color.White,
         BorderStyle = BorderStyle.None,
         Font = new Font( "Consolas", 10.5f )
      };
      convo.Controls.Add( _chatHistory );
      root.Controls.Add( convo, 0, 0 );

      // ---- Ask section (white) : question box + Ask/Clear ----
      var ask = new SectionPanel { HeaderText = "Ask a question", HeaderColor = SectionWhite, Dock = DockStyle.Fill, Margin = new Padding( 0, 0, 0, 8 ) };
      var askGrid = new TableLayoutPanel { Dock = DockStyle.Fill, ColumnCount = 2, RowCount = 1, BackColor = SystemColors.Control };
      askGrid.ColumnStyles.Add( new ColumnStyle( SizeType.Percent, 100f ) );
      askGrid.ColumnStyles.Add( new ColumnStyle( SizeType.Absolute, 112f ) );

      _questionBox = new TextBox
      {
         Dock = DockStyle.Fill,
         Multiline = true,
         Font = new Font( "Consolas", 10.5f ),
         BorderStyle = BorderStyle.FixedSingle,
         Margin = new Padding( 0, 0, 8, 0 )
      };
      askGrid.Controls.Add( _questionBox, 0, 0 );

      var askButtons = new FlowLayoutPanel
      {
         Dock = DockStyle.Fill,
         FlowDirection = FlowDirection.TopDown,
         WrapContents = false,
         BackColor = SystemColors.Control
      };
      _askButton = new RoundedButton { Text = "Ask", Size = new Size( 100, 34 ), Margin = new Padding( 0, 0, 0, 6 ), Font = new Font( "Segoe UI", 10.5f, FontStyle.Bold ) };
      _clearButton = new RoundedButton { Text = "Clear", Size = new Size( 100, 34 ), Margin = new Padding( 0 ), Font = new Font( "Segoe UI", 10.5f ) };
      askButtons.Controls.Add( _askButton );
      askButtons.Controls.Add( _clearButton );
      askGrid.Controls.Add( askButtons, 1, 0 );

      ask.Controls.Add( askGrid );
      root.Controls.Add( ask, 0, 1 );

      // ---- Rate section (red) : feedback buttons + status ----
      var rate = new SectionPanel { HeaderText = "Rate the answer", HeaderColor = SectionRed, Dock = DockStyle.Fill, Margin = new Padding( 0 ) };
      var rateGrid = new TableLayoutPanel { Dock = DockStyle.Fill, ColumnCount = 2, RowCount = 1, BackColor = SystemColors.Control };
      rateGrid.ColumnStyles.Add( new ColumnStyle( SizeType.AutoSize ) );
      rateGrid.ColumnStyles.Add( new ColumnStyle( SizeType.Percent, 100f ) );

      var rateButtons = new FlowLayoutPanel
      {
         Dock = DockStyle.Fill,
         FlowDirection = FlowDirection.LeftToRight,
         WrapContents = false,
         AutoSize = true,
         BackColor = SystemColors.Control
      };
      _thumbsUpButton = new RoundedButton { Text = "Good", Size = new Size( 96, 32 ), Enabled = false, Margin = new Padding( 0, 0, 8, 0 ) };
      _thumbsDownButton = new RoundedButton { Text = "Bad", Size = new Size( 96, 32 ), Enabled = false, Margin = new Padding( 0, 0, 8, 0 ) };
      _notWhatIWantButton = new RoundedButton { Text = "Not what I want", Size = new Size( 150, 32 ), Enabled = false, Margin = new Padding( 0 ) };
      rateButtons.Controls.Add( _thumbsUpButton );
      rateButtons.Controls.Add( _thumbsDownButton );
      rateButtons.Controls.Add( _notWhatIWantButton );
      rateGrid.Controls.Add( rateButtons, 0, 0 );

      _statusLabel = new Label
      {
         Dock = DockStyle.Fill,
         Text = "Ready — Ask a question",
         TextAlign = ContentAlignment.MiddleLeft,
         ForeColor = AccentBlue,
         Font = new Font( "Segoe UI", 9.5f, FontStyle.Bold ),
         Margin = new Padding( 12, 0, 0, 0 )
      };
      rateGrid.Controls.Add( _statusLabel, 1, 0 );

      rate.Controls.Add( rateGrid );
      root.Controls.Add( rate, 0, 2 );
   }
}
