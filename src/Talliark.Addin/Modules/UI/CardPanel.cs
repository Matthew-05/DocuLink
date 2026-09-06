using System.Drawing;
using System.Drawing.Drawing2D;
using System.Windows.Forms;

namespace Talliark.Addin.Modules.UI
{
    /// <summary>
    /// Rounded, hairline-bordered surface that groups one section of a dialog.
    /// The corners outside the rounded border are painted in the parent's colour
    /// so the card sits on the canvas without square edges showing through.
    /// </summary>
    internal sealed class CardPanel : Panel
    {
        internal CardPanel()
        {
            SetStyle(
                ControlStyles.AllPaintingInWmPaint
                | ControlStyles.UserPaint
                | ControlStyles.OptimizedDoubleBuffer
                | ControlStyles.ResizeRedraw,
                true);

            BackColor = DialogTheme.Surface;
        }

        protected override void OnPaint(PaintEventArgs e)
        {
            e.Graphics.Clear(Parent != null ? Parent.BackColor : DialogTheme.Canvas);
            e.Graphics.SmoothingMode = SmoothingMode.AntiAlias;

            var bounds = new Rectangle(0, 0, Width - 1, Height - 1);
            using (GraphicsPath path = CreateRoundedPath(bounds, DialogTheme.CornerRadius))
            using (var fill = new SolidBrush(DialogTheme.Surface))
            using (var pen = new Pen(DialogTheme.Border))
            {
                e.Graphics.FillPath(fill, path);
                e.Graphics.DrawPath(pen, path);
            }

            base.OnPaint(e);
        }

        private static GraphicsPath CreateRoundedPath(Rectangle bounds, int radius)
        {
            int diameter = radius * 2;
            var path = new GraphicsPath();

            if (bounds.Width <= diameter || bounds.Height <= diameter)
            {
                path.AddRectangle(bounds);
                return path;
            }

            path.AddArc(bounds.X, bounds.Y, diameter, diameter, 180f, 90f);
            path.AddArc(bounds.Right - diameter, bounds.Y, diameter, diameter, 270f, 90f);
            path.AddArc(bounds.Right - diameter, bounds.Bottom - diameter, diameter, diameter, 0f, 90f);
            path.AddArc(bounds.X, bounds.Bottom - diameter, diameter, diameter, 90f, 90f);
            path.CloseFigure();
            return path;
        }
    }
}
