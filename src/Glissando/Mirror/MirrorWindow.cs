using System.Drawing;
using System.Drawing.Imaging;
using System.Windows.Forms;

namespace Glissando.Mirror;

/// <summary>Destination side: a borderless, top-most window that shows the mirrored source window. A backing
/// bitmap holds the whole source; each decoded region is blitted into it and the changed part repainted. All
/// methods run on the UI thread (the receiver marshals to it).</summary>
internal sealed class MirrorWindow : Form
{
    private Bitmap? _backing;

    public MirrorWindow()
    {
        Text = "Glissando mirror";
        FormBorderStyle = FormBorderStyle.SizableToolWindow;
        TopMost = true;
        ShowInTaskbar = true;
        StartPosition = FormStartPosition.Manual;
        BackColor = Color.Black;
        DoubleBuffered = true;
        KeyPreview = true;
    }

    /// <summary>Ensure the backing bitmap and window match the source size; show the window if hidden.</summary>
    public void Prepare(int srcW, int srcH)
    {
        if (srcW <= 0 || srcH <= 0) return;
        if (_backing == null || _backing.Width != srcW || _backing.Height != srcH)
        {
            var old = _backing;
            _backing = new Bitmap(srcW, srcH, PixelFormat.Format32bppArgb);
            using (var g = Graphics.FromImage(_backing)) g.Clear(Color.Black);
            old?.Dispose();

            var wa = Screen.FromControl(this).WorkingArea;
            ClientSize = new Size(Math.Min(srcW, wa.Width - 80), Math.Min(srcH, wa.Height - 80));
            if (Location == Point.Empty) Location = new Point(wa.X + 40, wa.Y + 40);
        }
        if (!Visible) Show();
    }

    public void Blit(Point at, Bitmap region, bool keyframe)
    {
        if (_backing == null) return;
        // Each tile just replaces its own rectangle; a keyframe is simply the whole grid of tiles, so there is
        // nothing to clear here (clearing per tile would wipe every tile but the last).
        _ = keyframe;
        using (var g = Graphics.FromImage(_backing))
            g.DrawImageUnscaled(region, at);
        // Repaint the touched area, scaled to the (possibly clamped) client size.
        double sx = (double)ClientSize.Width / _backing.Width, sy = (double)ClientSize.Height / _backing.Height;
        var dirty = new Rectangle(
            (int)Math.Floor(at.X * sx) - 1, (int)Math.Floor(at.Y * sy) - 1,
            (int)Math.Ceiling(region.Width * sx) + 2, (int)Math.Ceiling(region.Height * sy) + 2);
        Invalidate(dirty);
    }

    protected override void OnPaint(PaintEventArgs e)
    {
        if (_backing == null) { base.OnPaint(e); return; }
        e.Graphics.InterpolationMode = System.Drawing.Drawing2D.InterpolationMode.NearestNeighbor;
        e.Graphics.PixelOffsetMode = System.Drawing.Drawing2D.PixelOffsetMode.Half;
        e.Graphics.DrawImage(_backing, new Rectangle(Point.Empty, ClientSize));
    }

    protected override void OnPaintBackground(PaintEventArgs e)
    {
        if (_backing == null) base.OnPaintBackground(e); // avoid a black flash over the image
    }

    protected override void OnResize(EventArgs e)
    {
        base.OnResize(e);
        Invalidate();
    }

    protected override void Dispose(bool disposing)
    {
        if (disposing) _backing?.Dispose();
        base.Dispose(disposing);
    }
}
