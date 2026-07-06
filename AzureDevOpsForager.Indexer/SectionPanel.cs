using System;
using System.Drawing;
using System.Drawing.Drawing2D;
using System.Windows.Forms;

namespace AzureDevOpsForager.Indexer;

/// <summary>
/// A "group panel" section: a rounded panel with a colored header strip across the top (rounded top corners)
/// and an auto-inset content area beneath it. Each instance sets its own <see cref="HeaderText"/> and
/// <see cref="HeaderColor"/> (e.g. green, white, red) so the sections read as distinct, clearly separated
/// groups. Child controls added to it sit inside the padding, below the header — so nothing overlaps the
/// header. Pure System.Drawing/Drawing2D; no libraries.
/// </summary>
internal sealed class SectionPanel : Panel
{
   #region Data Members

   /// <summary>Height of the colored header strip, in pixels.</summary>
   private const int HeaderHeight = 32;

   /// <summary>Corner radius for the rounded panel + header, in pixels.</summary>
   private const int CornerRadius = 8;

   private string _headerText = string.Empty;
   private Color _headerColor = SystemColors.Control;

   #endregion Data Members

   #region Constructor

   /// <summary>Configures flicker-free owner drawing and reserves the inset (content sits below the header).</summary>
   public SectionPanel()
   {
      SetStyle( ControlStyles.UserPaint | ControlStyles.AllPaintingInWmPaint
              | ControlStyles.OptimizedDoubleBuffer | ControlStyles.ResizeRedraw, true );
      BackColor = SystemColors.Control;
      Padding = new Padding( 16, HeaderHeight + 14, 16, 16 );
   }

   #endregion Constructor

   #region Public Members

   /// <summary>The Y coordinate (panel-relative) at which the first content row should sit, below the header.</summary>
   public int ContentTop => Padding.Top;

   /// <summary>Section title, drawn in the header strip.</summary>
   public string HeaderText { get => _headerText; set { _headerText = value ?? string.Empty; Invalidate(); } }

   /// <summary>Base color for this section's header (a light gradient is derived from it).</summary>
   public Color HeaderColor { get => _headerColor; set { _headerColor = value; Invalidate(); } }

   #endregion Public Members

   #region Overrides

   /// <summary>Paints the rounded body + border, the color-gradient header with rounded top corners, and the title.</summary>
   protected override void OnPaint( PaintEventArgs e )
   {
      var graphics = e.Graphics;
      graphics.SmoothingMode = SmoothingMode.AntiAlias;
      var outer = new Rectangle( 0, 0, Width - 1, Height - 1 );
      if( outer.Width <= 0 || outer.Height <= 0 ) return;

      using( var bodyPath = Rounded( outer, CornerRadius ) )
      {
         using( var body = new SolidBrush( SystemColors.Control ) )
            graphics.FillPath( body, bodyPath );

         // Header strip: only the TOP corners are rounded (it meets the content with a straight edge).
         var headerRect = new Rectangle( outer.X, outer.Y, outer.Width, HeaderHeight );
         using( var headerPath = RoundedTop( headerRect, CornerRadius ) )
         {
            var top = Blend( _headerColor, Color.White, 0.35f );
            var bottom = Blend( _headerColor, Color.Black, 0.08f );
            using var gradient = new LinearGradientBrush( new Rectangle( 0, 0, Width, HeaderHeight ), top, bottom, LinearGradientMode.Vertical );
            graphics.FillPath( gradient, headerPath );
         }

         // The border "pretty line" around the whole section.
         using( var border = new Pen( ControlPaint.Dark( SystemColors.Control ) ) )
            graphics.DrawPath( border, bodyPath );
      }

      // Divider between header and content.
      using( var rule = new Pen( ControlPaint.Dark( SystemColors.Control ) ) )
         graphics.DrawLine( rule, outer.Left, outer.Y + HeaderHeight, outer.Right, outer.Y + HeaderHeight );

      if( string.IsNullOrEmpty( _headerText ) ) return;
      graphics.TextRenderingHint = System.Drawing.Text.TextRenderingHint.ClearTypeGridFit;
      var textRect = new Rectangle( 16, 0, Width - 24, HeaderHeight );
      using var font = new Font( "Segoe UI", 11f, FontStyle.Bold );
      TextRenderer.DrawText( graphics, _headerText, font, textRect, SystemColors.ControlText,
         TextFormatFlags.Left | TextFormatFlags.VerticalCenter | TextFormatFlags.NoPrefix );
   }

   #endregion Overrides

   #region Private Methods

   /// <summary>Linear blend between two colors (t=0 -> a, t=1 -> b).</summary>
   private static Color Blend( Color a, Color b, float t )
      => Color.FromArgb(
         (int)( a.R + ( b.R - a.R ) * t ),
         (int)( a.G + ( b.G - a.G ) * t ),
         (int)( a.B + ( b.B - a.B ) * t ) );

   /// <summary>A rounded rectangle path (all four corners).</summary>
   private static GraphicsPath Rounded( Rectangle r, int radius )
   {
      int d = radius * 2;
      var path = new GraphicsPath();
      path.AddArc( r.X, r.Y, d, d, 180, 90 );
      path.AddArc( r.Right - d, r.Y, d, d, 270, 90 );
      path.AddArc( r.Right - d, r.Bottom - d, d, d, 0, 90 );
      path.AddArc( r.X, r.Bottom - d, d, d, 90, 90 );
      path.CloseFigure();
      return path;
   }

   /// <summary>A path with only the TOP two corners rounded (bottom is square) — for the header strip.</summary>
   private static GraphicsPath RoundedTop( Rectangle r, int radius )
   {
      int d = radius * 2;
      var path = new GraphicsPath();
      path.AddArc( r.X, r.Y, d, d, 180, 90 );
      path.AddArc( r.Right - d, r.Y, d, d, 270, 90 );
      path.AddLine( r.Right, r.Bottom, r.X, r.Bottom );
      path.CloseFigure();
      return path;
   }

   #endregion Private Methods
}
