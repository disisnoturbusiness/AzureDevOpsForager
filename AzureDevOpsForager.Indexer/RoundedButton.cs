using System;
using System.Drawing;
using System.Drawing.Drawing2D;
using System.Windows.Forms;

namespace AzureDevOpsForager.Indexer;

/// <summary>
/// A standard button with smooth rounded corners. Owner-painted because WinForms' stock button can't
/// round its corners; it uses the system button colors so it matches the default form look — the only
/// change from a normal button is the rounded shape (plus a light hover/press).
/// </summary>
internal sealed class RoundedButton : Button
{
   #region Data Members

   /// <summary>Corner radius, in pixels.</summary>
   private const int CornerRadius = 6;

   /// <summary>True while the pointer is over the button.</summary>
   private bool _hover;

   /// <summary>True while the button is being pressed.</summary>
   private bool _pressed;

   #endregion Data Members

   #region Constructor

   /// <summary>Configures the button for owner-drawing: flat, borderless, transparent, double-buffered.</summary>
   public RoundedButton()
   {
      FlatStyle = FlatStyle.Flat;
      FlatAppearance.BorderSize = 0;
      FlatAppearance.MouseOverBackColor = Color.Transparent;
      FlatAppearance.MouseDownBackColor = Color.Transparent;
      BackColor = Color.Transparent;
      SetStyle( ControlStyles.UserPaint | ControlStyles.AllPaintingInWmPaint
              | ControlStyles.OptimizedDoubleBuffer | ControlStyles.SupportsTransparentBackColor, true );
   }

   #endregion Constructor

   #region Overrides

   protected override void OnMouseEnter( EventArgs e ) { _hover = true; Invalidate(); base.OnMouseEnter( e ); }
   protected override void OnMouseLeave( EventArgs e ) { _hover = false; _pressed = false; Invalidate(); base.OnMouseLeave( e ); }
   protected override void OnMouseDown( MouseEventArgs e ) { _pressed = true; Invalidate(); base.OnMouseDown( e ); }
   protected override void OnMouseUp( MouseEventArgs e ) { _pressed = false; Invalidate(); base.OnMouseUp( e ); }
   protected override void OnTextChanged( EventArgs e ) { Invalidate(); base.OnTextChanged( e ); }
   protected override void OnEnabledChanged( EventArgs e ) { Invalidate(); base.OnEnabledChanged( e ); }

   /// <summary>Paints the rounded fill + border + centered text using system button colors, anti-aliased.</summary>
   protected override void OnPaint( PaintEventArgs e )
   {
      var graphics = e.Graphics;
      graphics.SmoothingMode = SmoothingMode.AntiAlias;

      // Fill the square corners with the parent color so the rounded edges blend into the form.
      using( var backdrop = new SolidBrush( Parent?.BackColor ?? SystemColors.Control ) )
         graphics.FillRectangle( backdrop, ClientRectangle );

      var bounds = new Rectangle( 0, 0, Width - 1, Height - 1 );
      using var path = RoundedRectangle( bounds, CornerRadius );

      var fill = !Enabled ? SystemColors.Control
               : _pressed ? SystemColors.ControlDark
               : _hover ? SystemColors.ControlLight
               : SystemColors.ButtonFace;

      using( var brush = new SolidBrush( fill ) )
         graphics.FillPath( brush, path );
      using( var pen = new Pen( SystemColors.ControlDark ) )
         graphics.DrawPath( pen, path );

      var textColor = Enabled ? SystemColors.ControlText : SystemColors.GrayText;
      TextRenderer.DrawText( graphics, Text, Font, bounds, textColor,
         TextFormatFlags.HorizontalCenter | TextFormatFlags.VerticalCenter | TextFormatFlags.NoPrefix );
   }

   #endregion Overrides

   #region Private Methods

   /// <summary>Builds a rounded-rectangle path for the given bounds and corner radius.</summary>
   private static GraphicsPath RoundedRectangle( Rectangle bounds, int radius )
   {
      int diameter = radius * 2;
      var path = new GraphicsPath();
      path.AddArc( bounds.X, bounds.Y, diameter, diameter, 180, 90 );
      path.AddArc( bounds.Right - diameter, bounds.Y, diameter, diameter, 270, 90 );
      path.AddArc( bounds.Right - diameter, bounds.Bottom - diameter, diameter, diameter, 0, 90 );
      path.AddArc( bounds.X, bounds.Bottom - diameter, diameter, diameter, 90, 90 );
      path.CloseFigure();
      return path;
   }

   #endregion Private Methods
}
