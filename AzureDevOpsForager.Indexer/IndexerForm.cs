using System;
using System.Drawing;
using System.IO;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using System.Windows.Forms;
using AzureDevOpsForager.Core;
using AzureDevOpsForager.Core.Services.Storage;

namespace AzureDevOpsForager.Indexer;

/// <summary>
/// The indexer's single main window. It lets an operator pick a source (Azure DevOps TFVC,
/// Azure DevOps Git, or GitHub) and a destination database (on-prem SQL Server or Azure SQL),
/// then build the code-search vector index into that database.
///
/// Everything lives in one form on purpose: this is an internal utility, and a one-window flow
/// keeps the "point it at a repo, point it at a database, press Build" story obvious. Destination
/// tables are created automatically on Connect, and wiping tables that already hold data requires
/// a deliberate double confirmation so nobody nukes a live index by reflex.
/// </summary>
public class IndexerForm : Form
{
   #region Data Members

   /// <summary>Source-kind selector: TFVC, Azure DevOps Git, or GitHub. Drives which source panel is shown.</summary>
   private ComboBox _type;

   // The source area is a container panel with three sub-panels overlaid inside it; only the
   // sub-panel matching the currently selected source type is made visible at any one time.
   /// <summary>Container that holds the three overlaid source sub-panels (only one visible at a time).</summary>
   private Panel _pnlSource, _pnlTfvc, _pnlGit, _pnlGitHub;

   /// <summary>TFVC source inputs: org URL, project, server root path, optional subfolder, and PAT.</summary>
   private TextBox _tfvcOrg, _tfvcProject, _tfvcRoot, _tfvcSub, _tfvcPat;

   /// <summary>Azure DevOps Git source inputs: org URL, project, repository, branch, and PAT.</summary>
   private TextBox _gitOrg, _gitProject, _gitRepo, _gitBranch, _gitPat;

   /// <summary>GitHub source inputs: repository URL, branch, and an optional access token.</summary>
   private TextBox _ghUrl, _ghBranch, _ghToken;

   /// <summary>Destination-kind selector: on-prem SQL Server or Azure SQL. Drives which destination panel is shown.</summary>
   private ComboBox _destType;

   /// <summary>Container that holds the two overlaid destination sub-panels (only one visible at a time).</summary>
   private Panel _pnlDest, _pnlSql, _pnlAzure;

   /// <summary>SQL Server destination inputs. User/password are only used when Windows auth is off.</summary>
   private TextBox _server, _database, _user, _password;

   /// <summary>SQL Server integrated-auth toggle. When checked the user/password fields are hidden and unused.</summary>
   private CheckBox _winAuth;

   /// <summary>Labels for the SQL Server user/password rows, hidden alongside their fields under Windows auth.</summary>
   private Label _lblUser, _lblPass;

   /// <summary>Azure SQL destination inputs. Azure always uses SQL authentication, so credentials are always shown.</summary>
   private TextBox _azServer, _azDatabase, _azUser, _azPassword;

   /// <summary>Default database name pre-filled for both destination kinds.</summary>
   private const string AzureDefaultDb = "AzureDevOpsForager";

   /// <summary>File-matching options: semicolon-separated include and exclude globs.</summary>
   private TextBox _include, _exclude;

   /// <summary>
   /// Optional path to a local embedding model. When set, this machine embeds locally with no
   /// file-count cap; when blank the hosted demo embedding service is used (which is capped).
   /// </summary>
   private TextBox _modelPath;

   /// <summary>"Download" link that fetches and installs the embedding model, then fills <see cref="_modelPath"/>.</summary>
   private LinkLabel _lnkDownload;

   /// <summary>Re-entrancy guard so extra clicks on Download are ignored while a download is already running.</summary>
   private bool _downloadingModel;

   /// <summary>Connect/init button: tests the target and ensures the schema exists.</summary>
   private Button _btnConnect;

   /// <summary>Build button. While a build is running it is repurposed as a Cancel button.</summary>
   private Button _btnBuild;

   /// <summary>Read-only console-style output box that shows progress and results for the current session.</summary>
   private TextBox _log;

   /// <summary>Shared tooltip provider for every field's hover help. Longer auto-pop so long hints stay readable.</summary>
   private readonly ToolTip _tip = new ToolTip { AutoPopDelay = 15000, InitialDelay = 400, ReshowDelay = 200 };

   /// <summary>Cancellation source for the in-progress build, so the Build-as-Cancel button can stop it.</summary>
   private CancellationTokenSource _cts;

   /// <summary>True while a build is running; gates the Build/Cancel dual behaviour.</summary>
   private bool _building;

   /// <summary>The three section group-panels, kept so the layout can re-flow when a sub-panel resizes.</summary>
   private SectionPanel _srcSection, _destSection, _optSection;

   /// <summary>Model-path row labels, kept so the footer can re-flow beneath the sections.</summary>
   private Label _lblModelPath, _lblModelOptional;

   #endregion Data Members

   #region Constructor

   /// <summary>
   /// Builds the window, wires up the log redirect, pre-fills defaults, and sets the initial
   /// source (GitHub) and destination (SQL Server) so the form opens ready to use.
   /// </summary>
   public IndexerForm()
   {
      Text = "Azure DevOps Forager — Indexer";
      Font = new Font( "Segoe UI", 10.5f );
      ClientSize = new Size( 760, 920 );
      StartPosition = FormStartPosition.CenterScreen;
      MinimumSize = new Size( 700, 760 );

      BuildUi();
      WireConsoleToLog();
      PreFill();

      // Selecting a different source/destination or toggling Windows auth re-flows which fields show.
      _type.SelectedIndexChanged += ( s, e ) => UpdateSourceVisibility();
      _destType.SelectedIndexChanged += ( s, e ) => UpdateDestVisibility();
      _winAuth.CheckedChanged += ( s, e ) => UpdateAuthFields();

      _type.SelectedIndex = 2;      // default source: GitHub
      _destType.SelectedIndex = 0;  // default destination: SQL Server
      UpdateSourceVisibility();
      UpdateDestVisibility();
      UpdateAuthFields();
   }

   #endregion Constructor

   #region Overrides

   /// <summary>
   /// Re-flows the sections once the window is actually shown. During construction every child control
   /// reports Visible=false (the form itself isn't visible yet), so the initial fit can't tell which
   /// sub-panel is showing; running it again here — when the flags are accurate — sizes each section to
   /// its real content instead of collapsing to one row.
   /// </summary>
   protected override void OnShown( EventArgs e )
   {
      base.OnShown( e );
      RelayoutForm();
   }

   #endregion Overrides

   #region Private Methods

   // --- UI construction -------------------------------------------------------------------------

   /// <summary>
   /// Lays out the whole window top to bottom: source section, destination section, options,
   /// the optional model path + Download link, the action buttons, and the log box. Splits the
   /// heavier sub-sections into helpers so this stays a readable outline of the form.
   /// </summary>
   private void BuildUi()
   {
      int y = 12;

      BuildSourceSection( ref y );
      BuildDestinationSection( ref y );
      BuildOptionsSection( ref y );
      BuildModelPathRow( ref y );
      BuildActionButtons( ref y );
      BuildLogBox( ref y );

      SetHints();
   }

   // Layout constants + per-section header colors for the three group-panel sections.
   private const int SectionMargin = 14;
   private const int SectionGap = 14;
   private const int LabelWidth = 150;
   private const int RowHeight = 34;
   private const int FieldHeight = 26;
   private static readonly Color SectionGreen = Color.FromArgb( 198, 224, 180 );
   private static readonly Color SectionWhite = Color.FromArgb( 242, 242, 242 );
   private static readonly Color SectionRed = Color.FromArgb( 244, 199, 195 );

   /// <summary>Creates a group-panel section (colored rounded header + inset body), adds it, and advances y past it.</summary>
   private SectionPanel NewSection( string title, Color headerColor, int contentHeight, ref int y )
   {
      var section = new SectionPanel
      {
         HeaderText = title,
         HeaderColor = headerColor,
         Left = SectionMargin,
         Top = y,
         Width = ClientSize.Width - SectionMargin * 2,
         Anchor = AnchorStyles.Top | AnchorStyles.Left | AnchorStyles.Right
      };
      section.Height = section.ContentTop + contentHeight + section.Padding.Bottom;
      Controls.Add( section );
      y += section.Height + SectionGap;
      return section;
   }

   /// <summary>Adds a labeled field row inside a section's inset content area (panel-relative positioning).</summary>
   private void SectionRow( SectionPanel section, string label, Control field, ref int rowY )
   {
      int fieldLeft = section.Padding.Left + LabelWidth;
      section.Controls.Add( new Label
      {
         Text = label, Left = section.Padding.Left, Top = rowY + 5, Width = LabelWidth - 8, Height = FieldHeight, AutoSize = false
      } );
      field.Left = fieldLeft;
      field.Top = rowY;
      field.Width = section.Width - fieldLeft - section.Padding.Right;
      field.Anchor = AnchorStyles.Top | AnchorStyles.Left | AnchorStyles.Right;
      section.Controls.Add( field );
      rowY += RowHeight;
   }

   /// <summary>Creates a read-only dropdown (used for the source/destination type selectors).</summary>
   private static ComboBox NewCombo() => new ComboBox { DropDownStyle = ComboBoxStyle.DropDownList };

   /// <summary>Builds the Source section: the type dropdown plus the three overlaid source sub-panels.</summary>
   private void BuildSourceSection( ref int y )
   {
      int panelHeight = 5 * RowHeight + 6;   // room for up to five overlaid sub-panel rows (TFVC/Git)
      var section = NewSection( "Source", SectionGreen, RowHeight + panelHeight, ref y );
      _srcSection = section;

      int rowY = section.ContentTop;
      SectionRow( section, "Type", _type = NewCombo(), ref rowY );
      _type.Items.AddRange( new object[] { "Azure DevOps (TFVC)", "Azure DevOps (Git)", "GitHub" } );

      // Source panel holds the three type sub-panels overlaid; one shown at a time.
      _pnlSource = new Panel
      {
         Left = section.Padding.Left, Top = rowY, Height = panelHeight,
         Width = section.Width - section.Padding.Left - section.Padding.Right,
         Anchor = AnchorStyles.Top | AnchorStyles.Left | AnchorStyles.Right
      };
      section.Controls.Add( _pnlSource );

      _pnlTfvc = BuildSourcePanel(
         new[] { "Organization URL", "Project", "Root path ($/…)", "Subfolder (optional)", "Personal Access Token" },
         out var tfvcBoxes, passwordIndex: 4 );
      _tfvcOrg = tfvcBoxes[0]; _tfvcProject = tfvcBoxes[1]; _tfvcRoot = tfvcBoxes[2]; _tfvcSub = tfvcBoxes[3]; _tfvcPat = tfvcBoxes[4];

      _pnlGit = BuildSourcePanel(
         new[] { "Organization URL", "Project", "Repository", "Branch (blank = default)", "Personal Access Token" },
         out var gitBoxes, passwordIndex: 4 );
      _gitOrg = gitBoxes[0]; _gitProject = gitBoxes[1]; _gitRepo = gitBoxes[2]; _gitBranch = gitBoxes[3]; _gitPat = gitBoxes[4];

      _pnlGitHub = BuildSourcePanel(
         new[] { "Repository URL", "Branch (blank = default)", "Token (blank = public)" },
         out var ghBoxes, passwordIndex: 2 );
      _ghUrl = ghBoxes[0]; _ghBranch = ghBoxes[1]; _ghToken = ghBoxes[2];

      _pnlSource.Controls.Add( _pnlTfvc );
      _pnlSource.Controls.Add( _pnlGit );
      _pnlSource.Controls.Add( _pnlGitHub );
   }

   /// <summary>Builds the Destination section: the type dropdown plus the SQL Server and Azure SQL sub-panels.</summary>
   private void BuildDestinationSection( ref int y )
   {
      int panelHeight = 5 * RowHeight + 6;   // room for up to five overlaid sub-panel rows (SQL Server auth)
      var section = NewSection( "Destination", SectionWhite, RowHeight + panelHeight, ref y );
      _destSection = section;

      int rowY = section.ContentTop;
      SectionRow( section, "Type", _destType = NewCombo(), ref rowY );
      _destType.Items.AddRange( new object[] { "SQL Server", "Azure SQL" } );

      // Destination panel holds the two sub-panels overlaid; one shown at a time.
      _pnlDest = new Panel
      {
         Left = section.Padding.Left, Top = rowY, Height = panelHeight,
         Width = section.Width - section.Padding.Left - section.Padding.Right,
         Anchor = AnchorStyles.Top | AnchorStyles.Left | AnchorStyles.Right
      };
      section.Controls.Add( _pnlDest );

      BuildSqlDestinationPanel();
      BuildAzureDestinationPanel();

      _pnlDest.Controls.Add( _pnlSql );
      _pnlDest.Controls.Add( _pnlAzure );
   }

   /// <summary>
   /// Builds the SQL Server destination sub-panel: server + database + a Windows Authentication
   /// toggle, with user/password rows that only apply when that toggle is off.
   /// </summary>
   private void BuildSqlDestinationPanel()
   {
      _pnlSql = new Panel { Dock = DockStyle.Fill, Visible = false };
      int yy = 0;
      AddPanelRow( _pnlSql, "Server", _server = NewTextBox(), ref yy );
      AddPanelRow( _pnlSql, "Database", _database = NewTextBox(), ref yy );
      _winAuth = new CheckBox { Text = "Windows Authentication", Left = LabelWidth, Top = yy, Width = 300, AutoSize = true };
      _pnlSql.Controls.Add( _winAuth );
      yy += RowHeight;
      AddPanelRow( _pnlSql, "User", _user = NewTextBox(), ref yy, out _lblUser );
      AddPanelRow( _pnlSql, "Password", _password = NewTextBox( isPassword: true ), ref yy, out _lblPass );
   }

   /// <summary>
   /// Builds the Azure SQL destination sub-panel: server + database + user + password. Azure always
   /// uses SQL authentication, so there is no Windows-auth option here and the database defaults to
   /// <see cref="AzureDefaultDb"/>.
   /// </summary>
   private void BuildAzureDestinationPanel()
   {
      _pnlAzure = new Panel { Dock = DockStyle.Fill, Visible = false };
      int yy = 0;
      AddPanelRow( _pnlAzure, "Server", _azServer = NewTextBox(), ref yy );
      AddPanelRow( _pnlAzure, "Database", _azDatabase = NewTextBox(), ref yy );
      AddPanelRow( _pnlAzure, "User", _azUser = NewTextBox(), ref yy );
      AddPanelRow( _pnlAzure, "Password", _azPassword = NewTextBox( isPassword: true ), ref yy );
   }

   /// <summary>Builds the Options section: the include-globs and exclude-globs rows.</summary>
   private void BuildOptionsSection( ref int y )
   {
      var section = NewSection( "Options", SectionRed, 2 * RowHeight + 4, ref y );
      _optSection = section;
      int rowY = section.ContentTop;
      SectionRow( section, "Include globs", _include = NewTextBox(), ref rowY );
      SectionRow( section, "Exclude globs", _exclude = NewTextBox(), ref rowY );
   }

   /// <summary>
   /// Builds the optional "Model Override Path" row: a label, an "optional" hint, the path textbox,
   /// and a Download link. A local model here means uncapped local embedding; blank means the hosted
   /// (capped) demo service.
   /// </summary>
   private void BuildModelPathRow( ref int y )
   {
      y += 10;
      _lblModelPath = new Label { Text = "Model Override Path", Left = 12, Top = y + 3, Width = 122, AutoSize = false };
      _lblModelOptional = new Label { Text = "optional", Left = 140, Top = y - 9, AutoSize = true, ForeColor = Color.Gray, Font = new Font( "Segoe UI", 7f, FontStyle.Italic ) };
      Controls.Add( _lblModelPath );
      Controls.Add( _lblModelOptional );
      _modelPath = new TextBox { Left = 140, Top = y, Width = 468, Anchor = AnchorStyles.Top | AnchorStyles.Left | AnchorStyles.Right };
      _lnkDownload = new LinkLabel { Text = "Download", Left = 616, Top = y + 3, AutoSize = true, Anchor = AnchorStyles.Top | AnchorStyles.Right };
      _lnkDownload.LinkClicked += async ( s, e ) => await DownloadModelAsync();
      Controls.Add( _modelPath );
      Controls.Add( _lnkDownload );
      y += 30;
   }

   /// <summary>Builds the Connect and Build buttons and wires their click handlers.</summary>
   private void BuildActionButtons( ref int y )
   {
      _btnConnect = new RoundedButton { Text = "Connect & Init", Left = 140, Top = y, Width = 150, Height = 30 };
      _btnBuild = new RoundedButton { Text = "Build Index", Left = 300, Top = y, Width = 150, Height = 30 };
      _btnConnect.Click += async ( s, e ) => await ConnectAsync();
      _btnBuild.Click += async ( s, e ) => await BuildOrCancelAsync();
      Controls.Add( _btnConnect );
      Controls.Add( _btnBuild );
      y += 40;
   }

   /// <summary>Builds the dark, monospaced, read-only log box that fills the rest of the window.</summary>
   private void BuildLogBox( ref int y )
   {
      _log = new TextBox
      {
         Left = 14, Top = y, Width = ClientSize.Width - 28, Height = ClientSize.Height - y - 14,
         Multiline = true, ReadOnly = true, ScrollBars = ScrollBars.Vertical,
         Font = new Font( "Consolas", 9.5f ), BackColor = Color.FromArgb( 24, 24, 24 ), ForeColor = Color.Gainsboro,
         Anchor = AnchorStyles.Top | AnchorStyles.Bottom | AnchorStyles.Left | AnchorStyles.Right
      };
      Controls.Add( _log );
   }

   /// <summary>
   /// Re-flows the whole stack after a sub-panel's visible height changes: sizes the Source and Destination
   /// sections to exactly fit their currently-visible sub-panel (removing dead space), then restacks
   /// Destination, Options, the model-path row, the buttons, and the log beneath them.
   /// </summary>
   private void RelayoutForm()
   {
      if( _srcSection == null || _log == null ) return;   // not fully built yet

      FitSection( _srcSection, _pnlSource );
      _destSection.Top = _srcSection.Bottom + SectionGap;
      FitSection( _destSection, _pnlDest );
      _optSection.Top = _destSection.Bottom + SectionGap;

      int y = _optSection.Bottom + 16;
      _lblModelOptional.Top = y - 9;
      _lblModelPath.Top = y + 3;
      _modelPath.Top = y;
      _lnkDownload.Top = y + 3;
      y += RowHeight + 8;
      _btnConnect.Top = y;
      _btnBuild.Top = y;
      y += 46;
      _log.Top = y;
      _log.Height = Math.Max( 90, ClientSize.Height - y - 14 );
   }

   /// <summary>Sizes a section to exactly fit the visible sub-panel inside its container (kills dead space).</summary>
   private void FitSection( SectionPanel section, Panel container )
   {
      var visible = VisibleChild( container );
      int contentBottom = visible != null ? VisibleBottom( visible ) : RowHeight;
      container.Height = contentBottom + 6;
      section.Height = container.Bottom + section.Padding.Bottom;
   }

   /// <summary>Returns the first visible child of a container (the currently-shown overlaid sub-panel), or null.</summary>
   private static Control VisibleChild( Panel container )
   {
      foreach( Control child in container.Controls )
         if( child.Visible ) return child;
      return null;
   }

   /// <summary>Returns the bottom-most edge of a control's visible children (its used content height).</summary>
   private static int VisibleBottom( Control panel )
   {
      int bottom = 0;
      foreach( Control child in panel.Controls )
         if( child.Visible ) bottom = Math.Max( bottom, child.Bottom );
      return bottom;
   }

   /// <summary>
   /// Builds one source sub-panel from a list of field labels. The panel docks to fill so the three
   /// source sub-panels can overlay in the same container, and the field at <paramref name="passwordIndex"/>
   /// is masked (it holds a token/PAT).
   /// </summary>
   private Panel BuildSourcePanel( string[] labels, out TextBox[] boxes, int passwordIndex )
   {
      var panel = new Panel { Dock = DockStyle.Fill, Visible = false };
      boxes = new TextBox[labels.Length];
      int y = 0;
      for( int i = 0; i < labels.Length; i++ )
      {
         var label = new Label { Text = labels[i], Left = 0, Top = y + 5, Width = LabelWidth - 8, Height = FieldHeight, AutoSize = false };
         var box = new TextBox { Left = LabelWidth, Top = y, Width = 560, Anchor = AnchorStyles.Top | AnchorStyles.Left | AnchorStyles.Right };
         if( i == passwordIndex ) box.UseSystemPasswordChar = true;
         panel.Controls.Add( label );
         panel.Controls.Add( box );
         boxes[i] = box;
         y += RowHeight;
      }
      return panel;
   }

   /// <summary>Adds a bold section header (Source / Destination / Options) at the current vertical offset.</summary>
   private void AddHeader( string text, ref int y )
   {
      y += 6;
      Controls.Add( new Label
      {
         Text = text, Left = 12, Top = y, Width = 672, AutoSize = false, Height = 20,
         Font = new Font( "Segoe UI", 9.5f, FontStyle.Bold ), ForeColor = Color.FromArgb( 0, 90, 158 )
      } );
      y += 24;
   }

   /// <summary>Adds a labeled field row directly to the form (label discarded). Overload of the out-Label variant.</summary>
   private void AddRow( Control parent, string label, Control field, ref int y )
      => AddRow( parent, label, field, ref y, out _ );

   /// <summary>
   /// Adds a labeled field row to <paramref name="parent"/> at the current vertical offset and returns
   /// the created label so callers that need to show/hide it (e.g. auth fields) can keep a reference.
   /// </summary>
   private void AddRow( Control parent, string label, Control field, ref int y, out Label createdLabel )
   {
      createdLabel = new Label { Text = label, Left = 12, Top = y + 3, Width = 122, AutoSize = false };
      field.Left = 140; field.Top = y;
      if( field.Width < 100 ) field.Width = 544;
      field.Anchor = AnchorStyles.Top | AnchorStyles.Left | AnchorStyles.Right;
      parent.Controls.Add( createdLabel );
      parent.Controls.Add( field );
      y += 28;
   }

   /// <summary>Adds a labeled field to a Dock=Fill sub-panel using panel-relative positioning (label discarded).</summary>
   private void AddPanelRow( Panel panel, string label, Control field, ref int y )
      => AddPanelRow( panel, label, field, ref y, out _ );

   /// <summary>
   /// Adds a labeled field to a Dock=Fill sub-panel (panel-relative positioning) and returns the created
   /// label so callers can toggle its visibility (used for the SQL Server user/password rows).
   /// </summary>
   private void AddPanelRow( Panel panel, string label, Control field, ref int y, out Label createdLabel )
   {
      createdLabel = new Label { Text = label, Left = 0, Top = y + 5, Width = LabelWidth - 8, Height = FieldHeight, AutoSize = false };
      field.Left = LabelWidth; field.Top = y; field.Width = 560;
      field.Anchor = AnchorStyles.Top | AnchorStyles.Left | AnchorStyles.Right;
      panel.Controls.Add( createdLabel );
      panel.Controls.Add( field );
      y += RowHeight;
   }

   /// <summary>Creates a standard-width textbox, optionally masked for secrets.</summary>
   private static TextBox NewTextBox( bool isPassword = false )
   {
      var textBox = new TextBox { Width = 544 };
      if( isPassword ) textBox.UseSystemPasswordChar = true;
      return textBox;
   }

   /// <summary>
   /// Sets the placeholder text and hover tooltip for every field so operators can see the expected
   /// format at a glance and get a fuller explanation on hover. Grouped by section to match the layout.
   /// </summary>
   private void SetHints()
   {
      _tip.SetToolTip( _type, "Where the source code lives: Azure DevOps TFVC, Azure DevOps Git, or GitHub." );

      // TFVC
      Hint( _tfvcOrg, "https://dev.azure.com/your-org", "Azure DevOps organization URL. Format: https://dev.azure.com/{organization}. Example: https://dev.azure.com/contoso" );
      Hint( _tfvcProject, "MyProject", "Azure DevOps project name. Example: MyTeamProject" );
      Hint( _tfvcRoot, "$/MyProject/Main/Src", "TFVC server path to index. Example: $/MyProject/Main/Src" );
      Hint( _tfvcSub, "Sub/Folder (optional)", "Optional subpath under the root to narrow scope. Blank = everything under the root." );
      Hint( _tfvcPat, "PAT with Code (Read)", "Azure DevOps Personal Access Token with Code (Read) scope. Create under User Settings -> Personal Access Tokens." );

      // Git
      Hint( _gitOrg, "https://dev.azure.com/your-org", "Azure DevOps organization URL. Format: https://dev.azure.com/{organization}. Example: https://dev.azure.com/contoso" );
      Hint( _gitProject, "MyProject", "Azure DevOps project that contains the repository." );
      Hint( _gitRepo, "my-repo", "Azure DevOps Git repository name. Example: my-service" );
      Hint( _gitBranch, "main (blank = default)", "Branch to index. Blank = the repository's default branch." );
      Hint( _gitPat, "PAT with Code (Read)", "Azure DevOps Personal Access Token with Code (Read) scope." );

      // GitHub
      Hint( _ghUrl, "https://github.com/owner/repo", "GitHub repository URL. Example: https://github.com/dotnet-architecture/eShopOnWeb" );
      Hint( _ghBranch, "main (blank = default)", "Branch to index. Blank = the repository's default branch." );
      Hint( _ghToken, "blank for public repos", "GitHub token. Optional for public repos; required for private repos or to raise rate limits." );

      // Destination
      _tip.SetToolTip( _destType, "Where to write the vector index: an on-prem SQL Server, or Azure SQL." );
      Hint( _server, @"localhost\SQLEXPRESS", @"SQL Server instance. Example: localhost\SQLEXPRESS or MACHINE\INSTANCE." );
      Hint( _database, "AzureDevOpsForager", "Target database name. Created automatically if it doesn't exist." );
      Hint( _user, "sql login", "SQL Server login (used when Windows Authentication is unchecked)." );
      Hint( _password, "password", "SQL Server password (used when Windows Authentication is unchecked)." );
      _tip.SetToolTip( _winAuth, "Use your Windows login (integrated auth). Uncheck for a SQL Server username/password." );
      Hint( _azServer, "yourserver.database.windows.net", "Azure SQL server — the name.database.windows.net value from the portal." );
      Hint( _azDatabase, "AzureDevOpsForager", "Azure SQL database name (created automatically if it doesn't exist)." );
      Hint( _azUser, "sql admin login", "Azure SQL admin login (SQL authentication) — the one you set when creating the server." );
      Hint( _azPassword, "password", "Azure SQL admin password." );

      // Options
      Hint( _include, "**/*.cs", "Semicolon-separated include globs. Example: **/*.cs" );
      Hint( _exclude, "**/bin/**;**/obj/**", "Semicolon-separated exclude globs. Example: **/bin/**;**/obj/**" );

      Hint( _modelPath, @"blank = hosted demo  ·  e.g. D:\models\e5-large-v2\e5-large-v2.onnx",
         "Optional. Point at a local e5-large-v2.onnx to embed on this machine with no file-count cap. Leave blank to use the hosted demo embedding service. Click Download to fetch and install the model for you." );
      _tip.SetToolTip( _lnkDownload, "Download the embedding model and set this path for you — no Python needed." );
   }

   /// <summary>Sets a textbox's placeholder and tooltip in one call; safely no-ops if the box is null.</summary>
   private void Hint( TextBox box, string placeholder, string tip )
   {
      if( box == null ) return;
      box.PlaceholderText = placeholder;
      _tip.SetToolTip( box, tip );
   }

   // --- State / pre-fill ------------------------------------------------------------------------

   /// <summary>
   /// Seeds the form with sensible defaults on open: the public GitHub demo repo, the configured
   /// glob defaults, a model path only if a real local model is already configured, the default
   /// database name for both destinations, and Windows auth on.
   /// </summary>
   private void PreFill()
   {
      // Source identity fields start blank so operators fill in their own; placeholders show the format.
      // GitHub is the exception: it pre-fills the public demo repo (eShopOnWeb), which stays overridable.
      _ghUrl.Text = Config.GitHubRepoUrl;

      _include.Text = Config.IncludeGlobs;
      _exclude.Text = Config.ExcludeGlobs;

      // Only surface a model path if a real local model is already configured; otherwise blank = hosted demo.
      _modelPath.Text = Config.IsLocalModelConfigured ? Config.OnnxModelPath : "";

      _database.Text = AzureDefaultDb;
      _azDatabase.Text = AzureDefaultDb;
      _winAuth.Checked = true;
   }

   /// <summary>Shows only the source sub-panel that matches the selected source type.</summary>
   private void UpdateSourceVisibility()
   {
      _pnlTfvc.Visible = _type.SelectedIndex == 0;
      _pnlGit.Visible = _type.SelectedIndex == 1;
      _pnlGitHub.Visible = _type.SelectedIndex == 2;
      RelayoutForm();
   }

   /// <summary>Shows the SQL Server destination panel for index 0, or the Azure SQL panel otherwise.</summary>
   private void UpdateDestVisibility()
   {
      bool isSqlServer = _destType.SelectedIndex == 0;
      _pnlSql.Visible = isSqlServer;
      _pnlAzure.Visible = !isSqlServer;
      RelayoutForm();
   }

   /// <summary>
   /// On the SQL Server panel, shows the user/password rows only when Windows Authentication is
   /// unchecked; under integrated auth those fields are hidden because they aren't used.
   /// </summary>
   private void UpdateAuthFields()
   {
      var showCredentials = !_winAuth.Checked;
      _lblUser.Visible = _user.Visible = showCredentials;
      _lblPass.Visible = _password.Visible = showCredentials;
      RelayoutForm();
   }

   // --- Actions ---------------------------------------------------------------------------------

   /// <summary>
   /// Copies the current form values into the shared <see cref="Config"/> (source identity, globs,
   /// model path) and builds the destination connection string from the active destination panel.
   /// Returns the connection string so the caller can reuse it for connect/build without rebuilding.
   /// </summary>
   private string ApplyConfigFromForm()
   {
      switch( _type.SelectedIndex )
      {
         case 0: // TFVC
            Config.SourceType = "tfvc";
            Config.AzureUrl = _tfvcOrg.Text.Trim();
            Config.AzureProject = _tfvcProject.Text.Trim();
            Config.AzureTfvcRoot = CombinePath( _tfvcRoot.Text, _tfvcSub.Text );
            Config.AzurePAT = _tfvcPat.Text.Trim();
            break;
         case 1: // Git
            Config.SourceType = "git";
            Config.AzureUrl = _gitOrg.Text.Trim();
            Config.AzureProject = _gitProject.Text.Trim();
            Config.GitRepository = _gitRepo.Text.Trim();
            Config.GitBranch = _gitBranch.Text.Trim();
            Config.AzurePAT = _gitPat.Text.Trim();
            break;
         default: // GitHub
            Config.SourceType = "github";
            Config.GitHubRepoUrl = _ghUrl.Text.Trim();
            Config.GitBranch = _ghBranch.Text.Trim();
            Config.GitHubToken = _ghToken.Text.Trim();
            break;
      }

      Config.IncludeGlobs = _include.Text.Trim();
      Config.ExcludeGlobs = _exclude.Text.Trim();
      Config.OnnxModelPath = _modelPath.Text.Trim();   // blank => hosted embedding (capped); a real path => local (uncapped)

      var destination = DestFields();
      var connectionString = ConnectionStringBuilder.Build( destination.server, destination.database, destination.winAuth, destination.user, destination.pass );
      Config.AzdoVectorConnectionString = connectionString;
      Config.SqlConnectionString = connectionString;
      return connectionString;
   }

   /// <summary>True when the Azure SQL destination is selected.</summary>
   private bool IsAzureDest => _destType.SelectedIndex == 1;

   /// <summary>Resolves the destination connection fields from whichever panel (SQL / Azure) is active.</summary>
   private (string server, string database, bool winAuth, string user, string pass) DestFields()
   {
      if( IsAzureDest ) // Azure SQL — SQL auth
         return ( _azServer.Text.Trim(), _azDatabase.Text.Trim(), false, _azUser.Text.Trim(), _azPassword.Text );
      return ( _server.Text.Trim(), _database.Text.Trim(), _winAuth.Checked, _user.Text.Trim(), _password.Text );
   }

   /// <summary>Joins a TFVC root path with an optional subfolder, tolerating slashes on either side.</summary>
   private static string CombinePath( string root, string sub )
   {
      root = ( root ?? "" ).Trim();
      sub = ( sub ?? "" ).Trim();
      if( sub.Length == 0 ) return root;
      return root.TrimEnd( '/' ) + "/" + sub.TrimStart( '/' );
   }

   /// <summary>
   /// Connect/Init handler: applies the form to config, validates the target, then tests the
   /// connection and ensures the schema (tables + full-text) exists. This is the safe, read-mostly
   /// path operators use to verify their settings before committing to a full build.
   /// </summary>
   private async Task ConnectAsync()
   {
      var connectionString = ApplyConfigFromForm();
      if( !ValidateTarget() ) return;

      SetBusy( true );
      try
      {
         if( !await EnsureConnectableAsync( connectionString ) ) return;

         Log( "Connected. Ensuring schema (tables + full-text)..." );
         await SchemaInitializer.EnsureSchemaAsync( connectionString );
         Log( "[OK] Schema ready." );
      }
      catch( Exception exception )
      {
         Log( "[ERROR] " + exception.Message );
      }
      finally
      {
         SetBusy( false );
      }
   }

   /// <summary>
   /// Build button handler. The one button serves double duty: while a build is running it cancels
   /// that run (stopping after the current file); otherwise it starts a new build.
   /// </summary>
   private async Task BuildOrCancelAsync()
   {
      if( _building )
      {
         _cts?.Cancel();
         _btnBuild.Enabled = false;   // prevent double-cancel; re-enabled when the run unwinds
         Log( "Cancelling — stopping after the current file..." );
         return;
      }
      await BuildAsync();
   }

   /// <summary>
   /// Runs a full index build: validates, ensures schema, gets destructive confirmation if the target
   /// already holds data, then runs the indexer to completion. On success it offers to point the local
   /// Server at the freshly built database. Cancellation and errors leave the live index intact.
   /// </summary>
   private async Task BuildAsync()
   {
      _log.Clear();   // fresh log per build run
      var connectionString = ApplyConfigFromForm();
      if( !ValidateTarget() ) return;

      SetBusy( true );
      try
      {
         if( !await EnsureConnectableAsync( connectionString ) ) return;

         Log( "Ensuring schema..." );
         await SchemaInitializer.EnsureSchemaAsync( connectionString );

         // A full build truncates both tables, so make the operator confirm twice before wiping real data.
         if( await SchemaInitializer.HasContentAsync( connectionString ) && !ConfirmDestructiveWipe() )
            return;

         await RunBuildAsync( connectionString );
      }
      catch( OperationCanceledException )
      {
         Log( "[CANCELLED] Build stopped — the live index was left intact." );
      }
      catch( Exception exception )
      {
         Log( "[ERROR] " + exception.Message );
         Error( exception.Message, "Build failed" );
      }
      finally
      {
         _building = false;
         _cts?.Dispose();
         _cts = null;
         _btnBuild.Text = "Build Index";
         SetBusy( false );
         SaveLastLog();
      }
   }

   /// <summary>
   /// Double confirmation before a destructive full build. Returns true only if the operator answers
   /// Yes to both prompts; either No leaves existing data intact. Both prompts default to No.
   /// </summary>
   private bool ConfirmDestructiveWipe()
   {
      var confirmFirst = MessageBox.Show( this,
         "Are you really sure you want to delete all data in this Database?\r\nThis process is irreversible!",
         "Delete all data?", MessageBoxButtons.YesNo, MessageBoxIcon.Warning, MessageBoxDefaultButton.Button2 );
      if( confirmFirst != DialogResult.Yes ) { Log( "Cancelled — existing data left intact." ); return false; }

      var confirmSecond = MessageBox.Show( this, "Last Chance. Are you sure?",
         "Confirm", MessageBoxButtons.YesNo, MessageBoxIcon.Warning, MessageBoxDefaultButton.Button2 );
      if( confirmSecond != DialogResult.Yes ) { Log( "Cancelled — existing data left intact." ); return false; }

      return true;
   }

   /// <summary>
   /// Kicks off the actual index build against an already-validated connection: sets up cancellation,
   /// repurposes the Build button as Cancel, runs the indexer off the UI thread, and on success reports
   /// completion and offers to wire the local Server to this database.
   /// </summary>
   private async Task RunBuildAsync( string connectionString )
   {
      Log( $"Build started {DateTime.Now:yyyy-MM-dd HH:mm:ss}." );
      Log( "Building index — this can take a while..." );
      _cts = new CancellationTokenSource();
      _building = true;
      _btnBuild.Enabled = true;     // repurpose the Build button as Cancel during the run
      _btnBuild.Text = "Cancel";

      using var indexer = new AzdoIndexerService();   // captures Config.AzdoVectorConnectionString set above
      // The hosted-cap prompt must run on the UI thread, so marshal it back via Invoke from the worker.
      indexer.OnHostedCapExceeded = total => (bool)Invoke( new Func<bool>( () => ConfirmHostedCap( total ) ) );
      var token = _cts.Token;
      await Task.Run( () => indexer.RunMonthlyAsync( token ), token );

      Log( "[DONE] Index build complete." );
      Info( "Index build complete.", "Done" );
      PromptSetLocalServerDb();
   }

   /// <summary>
   /// Tests the target connection. If the server is reachable but the database is missing, offers to
   /// create it and then waits (briefly) for the new database to accept connections. Returns true when
   /// the target is connectable, or false to abort the caller.
   /// </summary>
   private async Task<bool> EnsureConnectableAsync( string connectionString )
   {
      Log( "Testing connection..." );
      if( await SchemaInitializer.TestConnectionAsync( connectionString ) ) return true;

      var destination = DestFields();
      var database = destination.database;
      var masterConnectionString = ConnectionStringBuilder.Build( destination.server, "master", destination.winAuth, destination.user, destination.pass );

      if( await SchemaInitializer.TestConnectionAsync( masterConnectionString )
          && !await SchemaInitializer.DatabaseExistsAsync( masterConnectionString, database ) )
      {
         return await OfferCreateDatabaseAsync( connectionString, masterConnectionString, database );
      }

      Log( "[ERROR] Could not connect. Check server / database / credentials." );
      Error( "Could not connect to the database.", "Connection failed" );
      return false;
   }

   /// <summary>
   /// Offers to create the missing database on the reachable server, creates it if confirmed, then
   /// polls until it accepts connections. Returns true once connectable, false if declined or if the
   /// new database isn't ready in time.
   /// </summary>
   private async Task<bool> OfferCreateDatabaseAsync( string connectionString, string masterConnectionString, string database )
   {
      var create = MessageBox.Show( this,
         $"Database '{database}' does not exist. Would you like to create it now?",
         "Create database?", MessageBoxButtons.YesNo, MessageBoxIcon.Question );
      if( create != DialogResult.Yes ) { Log( "Cancelled — database not created." ); return false; }

      Log( $"Creating database '{database}'..." );
      await SchemaInitializer.CreateDatabaseAsync( masterConnectionString, database );
      SchemaInitializer.ClearConnectionPools();

      // A freshly created DB can take a moment to accept connections — poll until it does.
      for( int i = 0; i < 20; i++ )
      {
         if( await SchemaInitializer.TestConnectionAsync( connectionString ) )
         {
            Log( "[OK] Database created." );
            return true;
         }
         await System.Threading.Tasks.Task.Delay( 500 );
      }
      Log( "[ERROR] Database created but not yet connectable — click Connect again." );
      return false;
   }

   /// <summary>
   /// Validates the minimum destination inputs before connecting or building: a server is always
   /// required, and a user is required whenever SQL/Azure authentication is in play. Shows a warning
   /// and returns false on the first missing value.
   /// </summary>
   private bool ValidateTarget()
   {
      var destination = DestFields();
      if( string.IsNullOrWhiteSpace( destination.server ) )
      {
         Warn( "Server is required.", "Missing target" );
         return false;
      }
      if( !destination.winAuth && string.IsNullOrWhiteSpace( destination.user ) )
      {
         Warn( "User is required (SQL / Azure authentication).", "Missing credentials" );
         return false;
      }
      return true;
   }

   /// <summary>
   /// Hosted-embedding fair-use prompt (runs on the UI thread). The shared demo embedding service caps
   /// a run at Config.HostedEmbeddingFileCap files. Yes = index the top N; No = cancel (the operator can
   /// Download a local model to remove the cap). Returns true to proceed with the capped subset.
   /// </summary>
   private bool ConfirmHostedCap( int totalFiles )
   {
      var message =
         $"This source has {totalFiles:N0} files, but the shared demo embedding service is limited to " +
         $"{Config.HostedEmbeddingFileCap:N0} files per run.\r\n\r\n" +
         $"Yes — index the first {Config.HostedEmbeddingFileCap:N0} files now.\r\n" +
         "No — cancel. To index everything, click \"Download\" (Options, above) to install the model locally, then rebuild.";
      return MessageBox.Show( this, message, "File limit reached",
         MessageBoxButtons.YesNo, MessageBoxIcon.Warning, MessageBoxDefaultButton.Button1 ) == DialogResult.Yes;
   }

   /// <summary>
   /// After a successful build, offers to make the just-built database the local Server's data source
   /// (persisted to the shared per-user override). If accepted, the local Server and web UI then serve
   /// this data with no manual config edits.
   /// </summary>
   private void PromptSetLocalServerDb()
   {
      var choice = MessageBox.Show( this,
         "Set this database as your local Server's data source?\r\n\r\n" +
         "All of the data you just indexed will then be searchable in the UIs when you run the Server locally.",
         "Use this database?", MessageBoxButtons.YesNo, MessageBoxIcon.Question, MessageBoxDefaultButton.Button1 );
      if( choice != DialogResult.Yes ) return;

      Config.SaveUserOverride( "SqlConnectionString", Config.SqlConnectionString );
      Log( "[OK] Saved — your local Server will use this database." );
   }

   /// <summary>Enables/disables the action buttons and swaps the cursor to reflect a busy operation.</summary>
   private void SetBusy( bool busy )
   {
      _btnConnect.Enabled = !busy;
      _btnBuild.Enabled = !busy;
      Cursor = busy ? Cursors.WaitCursor : Cursors.Default;
   }

   // --- Model download --------------------------------------------------------------------------

   /// <summary>
   /// One-click model setup for self-hosters (no Python, no manual steps): pick an install folder, then
   /// download the model bundle to a temp file, unpack it into the chosen folder, resolve the .onnx, and
   /// set + persist the model path so this machine embeds locally (uncapped) and the local Server picks it up.
   /// </summary>
   private async Task DownloadModelAsync()
   {
      if( _downloadingModel ) return;   // ignore extra clicks while a download is already running

      var root = PickModelInstallFolder();
      if( root == null ) return;   // folder dialog cancelled — nothing else to do

      _downloadingModel = true;
      _lnkDownload.Enabled = false;
      _lnkDownload.Text = "Downloading...";
      SetBusy( true );
      try
      {
         var zipPath = await DownloadModelZipAsync();

         // The bundle carries its own onyx\models\e5-large-v2\ structure, so extract straight into the
         // chosen folder; that recreates {folder}\onyx\models\e5-large-v2\e5-large-v2.onnx with no double-nesting.
         Log( "Unpacking model (this can take a moment for a 1 GB+ model)..." );
         System.IO.Compression.ZipFile.ExtractToDirectory( zipPath, root, overwriteFiles: true );
         try { File.Delete( zipPath ); } catch { }

         var onnx = ResolveModelOnnx( root );
         if( onnx == null )
         {
            Warn( "The downloaded archive didn't contain an .onnx file.", "Model install" );
            return;
         }

         ApplyModelPath( onnx );
         Log( $"[OK] Model installed. Path set to {onnx}" );
         Info( "Model installed and path set. This machine will now embed locally (no file-count cap).", "Model ready" );
      }
      catch( Exception exception )
      {
         Log( "[ERROR] Model download failed: " + exception.Message );
         Error( "Download failed: " + exception.Message, "Model download failed" );
      }
      finally
      {
         _downloadingModel = false;
         _lnkDownload.Text = "Download";
         _lnkDownload.Enabled = true;
         SetBusy( false );
      }
   }

   /// <summary>Asks where to install the model; returns the chosen folder, or null if the user cancelled.</summary>
   private string PickModelInstallFolder()
   {
      using var openFolderDialog = new FolderBrowserDialog { Description = "Choose a folder to install the embedding model into" };
      return openFolderDialog.ShowDialog( this ) == DialogResult.OK ? openFolderDialog.SelectedPath : null;
   }

   /// <summary>
   /// Downloads the model bundle to a temp file and returns its path, logging live percentage progress
   /// because a plain copy gives no feedback for a ~1 GB file. Throws on failure (there is no local-file
   /// fallback — self-hosters don't have the bundle yet).
   /// </summary>
   private async Task<string> DownloadModelZipAsync()
   {
      var tempZip = Path.Combine( Path.GetTempPath(), "e5-large-v2-model.zip" );
      using var httpClient = new System.Net.Http.HttpClient { Timeout = TimeSpan.FromMinutes( 30 ) };
      using var response = await httpClient.GetAsync( Config.ModelDownloadUrl, System.Net.Http.HttpCompletionOption.ResponseHeadersRead );
      response.EnsureSuccessStatusCode();

      var totalBytes = response.Content.Headers.ContentLength ?? -1L;
      var totalMb = totalBytes / 1048576;
      Log( totalBytes > 0 ? $"Downloading model... 0%  (0 / {totalMb} MB)" : "Downloading model..." );

      // Stream in chunks so we can report live progress (a plain CopyToAsync gives no feedback for a ~1 GB file).
      using( var sourceStream = await response.Content.ReadAsStreamAsync() )
      using( var fileStream = new FileStream( tempZip, FileMode.Create, FileAccess.Write, FileShare.None ) )
      {
         var buffer = new byte[81920];
         long readTotal = 0;
         int lastPercent = 0;
         int read;
         while( ( read = await sourceStream.ReadAsync( buffer, 0, buffer.Length ) ) > 0 )
         {
            await fileStream.WriteAsync( buffer, 0, read );
            readTotal += read;
            if( totalBytes > 0 )
            {
               int percent = (int)( readTotal * 100 / totalBytes );
               if( percent >= lastPercent + 2 )   // log every ~2%
               {
                  lastPercent = percent;
                  Log( $"Downloading model... {percent}%  ({readTotal / 1048576} / {totalMb} MB)" );
               }
            }
         }
      }
      Log( "[OK] Download complete." );
      return tempZip;
   }

   /// <summary>Finds the e5 .onnx anywhere under the chosen folder: prefer the exact filename, else any .onnx.</summary>
   private string ResolveModelOnnx( string root )
   {
      var named = Directory.EnumerateFiles( root, "e5-large-v2.onnx", SearchOption.AllDirectories ).FirstOrDefault();
      return named ?? Directory.EnumerateFiles( root, "*.onnx", SearchOption.AllDirectories ).FirstOrDefault();
   }

   /// <summary>Sets and persists the model path so this run embeds locally and the local Server/clients pick it up.</summary>
   private void ApplyModelPath( string onnxPath )
   {
      _modelPath.Text = onnxPath;
      Config.OnnxModelPath = onnxPath;
      Config.SaveUserOverride( "OnnxModelPath", onnxPath );
   }

   // --- Logging ---------------------------------------------------------------------------------

   /// <summary>
   /// Persists this run's full log to lastlog.txt next to the exe (overwritten each run) so run times and
   /// messages stay reviewable after the window closes; the on-screen log itself clears at the start of
   /// each build. Failures to write are swallowed on purpose (logging must never break the build flow).
   /// </summary>
   private void SaveLastLog()
   {
      try
      {
         File.WriteAllText( Path.Combine( AppDomain.CurrentDomain.BaseDirectory, "lastlog.txt" ), _log.Text );
      }
      catch { }
   }

   /// <summary>Appends a single line (with newline) to the log box.</summary>
   private void Log( string message ) => AppendToLog( message + Environment.NewLine );

   /// <summary>Appends text to the log box, marshalling to the UI thread when called from a background worker.</summary>
   private void AppendToLog( string text )
   {
      if( _log.InvokeRequired ) { _log.BeginInvoke( new Action<string>( AppendToLog ), text ); return; }
      _log.AppendText( text );
   }

   /// <summary>Redirects the process's Console output into the log box so the indexer's writes show in the UI.</summary>
   private void WireConsoleToLog() => Console.SetOut( new TextBoxWriter( this ) );

   /// <summary>Raw append hook used by <see cref="TextBoxWriter"/> to route Console output into the log box.</summary>
   private void AppendRaw( string text ) => AppendToLog( text );

   // --- Message boxes ---------------------------------------------------------------------------

   /// <summary>Shows an informational message box owned by this form.</summary>
   private void Info( string message, string caption )
      => MessageBox.Show( this, message, caption, MessageBoxButtons.OK, MessageBoxIcon.Information );

   /// <summary>Shows a warning message box owned by this form.</summary>
   private void Warn( string message, string caption )
      => MessageBox.Show( this, message, caption, MessageBoxButtons.OK, MessageBoxIcon.Warning );

   /// <summary>Shows an error message box owned by this form.</summary>
   private void Error( string message, string caption )
      => MessageBox.Show( this, message, caption, MessageBoxButtons.OK, MessageBoxIcon.Error );

   #endregion Private Methods

   #region Nested Types

   /// <summary>
   /// A <see cref="TextWriter"/> that redirects Console output from the indexer into the form's log box.
   /// Installed as Console.Out so writes from deep in the indexing pipeline surface in the UI.
   /// </summary>
   private sealed class TextBoxWriter : TextWriter
   {
      /// <summary>The owning form whose log box receives the redirected Console output.</summary>
      private readonly IndexerForm _form;

      /// <summary>Creates a writer bound to the form that will receive Console output.</summary>
      public TextBoxWriter( IndexerForm form ) => _form = form;

      /// <summary>Console output is treated as UTF-8.</summary>
      public override Encoding Encoding => Encoding.UTF8;

      /// <summary>Writes a string to the log box (skipping empty writes to avoid needless UI churn).</summary>
      public override void Write( string value ) { if( !string.IsNullOrEmpty( value ) ) _form.AppendRaw( value ); }

      /// <summary>Writes a string followed by a newline to the log box.</summary>
      public override void WriteLine( string value ) => _form.AppendRaw( ( value ?? "" ) + Environment.NewLine );

      /// <summary>Writes a single character to the log box.</summary>
      public override void Write( char value ) => _form.AppendRaw( value.ToString() );
   }

   #endregion Nested Types
}
