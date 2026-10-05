using System.Diagnostics;
using System.Drawing;
using System.Drawing.Imaging;
using System.Windows.Forms;

namespace Glissando.Mirror;

/// <summary>Destination side: a borderless, top-most window that shows the mirrored source window. A backing
/// bitmap holds the whole source; each decoded region is blitted into it and the changed part repainted. All
/// methods run on the UI thread (the receiver marshals to it).
///
/// The window is also the way to use the mirrored program: the pointer over it, clicks, wheel and keys typed
/// into it go back to the source PC as mirror-input frames, in source-window pixels (the window may be shown
/// scaled down, so client coordinates are mapped through the backing size). Keys are taken raw from the
/// message loop, before WinForms turns Tab, arrows or Alt into navigation of its own, and every key still held
/// when the window loses focus is released on the source, so nothing sticks there.</summary>
internal sealed class MirrorWindow : Form
{
    private Bitmap? _backing;

    /// <summary>Set by the receiver: where interactions go.</summary>
    public Action<Frame>? Input { get; set; }

    private readonly HashSet<(ushort vk, ushort scan, bool ext)> _held = [];
    private Point _lastSent = new(-1, -1);
    private long _lastMoveTicks;

    private readonly byte _id;

    public MirrorWindow(byte id)
    {
        _id = id;
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
    public void Prepare(int srcW, int srcH, string title = "")
    {
        if (srcW <= 0 || srcH <= 0) return;
        string text = title != "" ? $"{title} - Glissando mirror" : "Glissando mirror";
        if (Text != text) Text = text;
        if (_backing == null || _backing.Width != srcW || _backing.Height != srcH)
        {
            var old = _backing;
            _backing = new Bitmap(srcW, srcH, PixelFormat.Format32bppArgb);
            using (var g = Graphics.FromImage(_backing)) g.Clear(Color.Black);
            old?.Dispose();

            var wa = Screen.FromControl(this).WorkingArea;
            ClientSize = new Size(Math.Min(srcW, wa.Width - 80), Math.Min(srcH, wa.Height - 80));
            // Several mirrors: each opens a little further down and right, not all on top of each other.
            int step = 40 + 32 * (_id % 8);
            if (Location == Point.Empty) Location = new Point(wa.X + step, wa.Y + step);
        }
        if (!Visible) Show();
    }

    public void Blit(Point at, Bitmap region, bool keyframe)
    {
        if (_backing == null) return;
        // Each tile just replaces its own rectangle; a keyframe is simply the whole grid of tiles, so there is
        // nothing to clear here (clearing per tile would wipe every tile but the last).
        _ = keyframe;
        // Pixel rectangles on both sides. DrawImageUnscaled(image, point) is DrawImage(image, x, y) underneath,
        // which scales by the image's DPI against the target's: a JPEG tile from a PC at 125-150 % scaling then
        // landed at 96/120 of its size, a grid of shrunken tiles with black gutters (2026-09-11).
        using (var g = Graphics.FromImage(_backing))
            g.DrawImage(region, new Rectangle(at, region.Size), new Rectangle(Point.Empty, region.Size), GraphicsUnit.Pixel);
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

    // ---- interaction: viewer -> source ----

    /// <summary>A client point in source-window pixels, or null before the first frame.</summary>
    private Point? ToSource(Point client)
    {
        if (_backing == null || ClientSize.Width <= 0 || ClientSize.Height <= 0) return null;
        int x = (int)((long)client.X * _backing.Width / ClientSize.Width);
        int y = (int)((long)client.Y * _backing.Height / ClientSize.Height);
        return new Point(Math.Clamp(x, 0, _backing.Width - 1), Math.Clamp(y, 0, _backing.Height - 1));
    }

    private static byte ButtonCode(MouseButtons b) => b switch
    {
        MouseButtons.Left => 1, MouseButtons.Right => 2, MouseButtons.Middle => 3,
        MouseButtons.XButton1 => 4, MouseButtons.XButton2 => 5, _ => 0,
    };

    protected override void OnMouseMove(MouseEventArgs e)
    {
        base.OnMouseMove(e);
        if (ToSource(e.Location) is not Point p || p == _lastSent) return;
        // A few dozen moves a second is plenty for hover; the link is shared with the picture.
        long now = Stopwatch.GetTimestamp();
        if (now - _lastMoveTicks < Stopwatch.Frequency / 40) return;
        _lastMoveTicks = now;
        _lastSent = p;
        Input?.Invoke(Frame.MirrorPointer(_id, p.X, p.Y));
    }

    protected override void OnMouseDown(MouseEventArgs e)
    {
        base.OnMouseDown(e);
        Focus();
        if (ToSource(e.Location) is Point p && ButtonCode(e.Button) is byte b and > 0)
            Input?.Invoke(Frame.MirrorMouse(_id, 1, p.X, p.Y, b, 0));
    }

    protected override void OnMouseUp(MouseEventArgs e)
    {
        base.OnMouseUp(e);
        if (ToSource(e.Location) is Point p && ButtonCode(e.Button) is byte b and > 0)
            Input?.Invoke(Frame.MirrorMouse(_id, 2, p.X, p.Y, b, 0));
    }

    protected override void OnMouseWheel(MouseEventArgs e)
    {
        base.OnMouseWheel(e);
        if (ToSource(e.Location) is Point p) Input?.Invoke(Frame.MirrorMouse(_id, 3, p.X, p.Y, 0, e.Delta));
    }

    private const int WM_MOUSEHWHEEL = 0x020E;

    /// <summary>Keep WinForms from pre-processing keys (Tab, arrows, Alt, mnemonics) so WndProc sees them all.</summary>
    public override bool PreProcessMessage(ref Message msg)
    {
        if (msg.Msg is Native.WM_KEYDOWN or Native.WM_KEYUP or Native.WM_SYSKEYDOWN or Native.WM_SYSKEYUP) return false;
        return base.PreProcessMessage(ref msg);
    }

    protected override void WndProc(ref Message m)
    {
        switch (m.Msg)
        {
            case Native.WM_KEYDOWN or Native.WM_SYSKEYDOWN or Native.WM_KEYUP or Native.WM_SYSKEYUP:
            {
                bool up = m.Msg is Native.WM_KEYUP or Native.WM_SYSKEYUP;
                long lp = m.LParam.ToInt64();
                ushort vk = (ushort)(m.WParam.ToInt64() & 0xFFFF);
                ushort scan = (ushort)((lp >> 16) & 0xFF);
                bool ext = ((lp >> 24) & 1) != 0;
                var key = (vk, scan, ext);
                if (up) _held.Remove(key); else _held.Add(key);
                Input?.Invoke(Frame.MirrorKey(_id, vk, scan, up, ext));
                return; // swallowed here: the key is for the source window, not for this form
            }
            case WM_MOUSEHWHEEL:
            {
                int delta = (short)((m.WParam.ToInt64() >> 16) & 0xFFFF);
                long lp = m.LParam.ToInt64();
                var screen = new Point((short)(lp & 0xFFFF), (short)((lp >> 16) & 0xFFFF));
                if (ToSource(PointToClient(screen)) is Point p) Input?.Invoke(Frame.MirrorMouse(_id, 4, p.X, p.Y, 0, delta));
                return;
            }
        }
        base.WndProc(ref m);
    }

    protected override void OnDeactivate(EventArgs e)
    {
        base.OnDeactivate(e);
        // Focus left with keys down (Alt+Tab away, a click elsewhere): release them on the source now, since
        // their real key-ups will never reach this window.
        foreach (var k in _held.ToArray()) Input?.Invoke(Frame.MirrorKey(_id, k.vk, k.scan, up: true, k.ext));
        _held.Clear();
    }

    protected override void Dispose(bool disposing)
    {
        if (disposing) _backing?.Dispose();
        base.Dispose(disposing);
    }
}
