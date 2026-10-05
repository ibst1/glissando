using System.Diagnostics;
using System.Drawing;
using System.Drawing.Imaging;
using System.Windows.Forms;

namespace Glissando.Mirror;

/// <summary>Destination side: a window that shows the mirrored source window. A backing bitmap holds the whole
/// source; each decoded region is blitted into it and the changed part repainted. All methods run on the UI
/// thread (the receiver marshals to it).
///
/// No title bar of its own -- the source window brings its own -- just a thin coloured frame (Settings, green
/// like the tray icon) that says "this is a mirror". The frame is the window's handle: its top edge moves it,
/// the other edges and the corners size it, a double-click on the top edge maximizes, and a right-click on it
/// offers to close the mirror.
///
/// It behaves like the window it mirrors: the picture is shown at 1:1, never scaled, and resizing this window
/// resizes the source window to match (sent when the drag ends), so the program lays itself out again at the
/// new size instead of being zoomed. Closing it stops the mirror on the source PC.
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
    private bool _sizing;        // the user is dragging a border; the size is sent when the drag ends
    private bool _sizingByCode;  // ClientSize set here to follow the source; not a request to resize it
    private bool _closingForMirror;

    /// <summary>True once the user closed this window (not the mirror ending).</summary>
    public bool ClosedByUser { get; private set; }

    private readonly byte _id;
    private readonly Func<Color> _frameColor;

    /// <summary>The frame's thickness in pixels, a little more on a high-DPI screen.</summary>
    private int B => Math.Max(4, DeviceDpi * 5 / 96);

    public MirrorWindow(byte id, Func<Color> frameColor)
    {
        _id = id;
        _frameColor = frameColor;
        Text = "Glissando mirror";
        FormBorderStyle = FormBorderStyle.None;
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
            var fit = new Size(Math.Min(srcW, wa.Width - 80 - 2 * B), Math.Min(srcH, wa.Height - 80 - 2 * B));
            _sizingByCode = true;
            try { ClientSize = new Size(fit.Width + 2 * B, fit.Height + 2 * B); } finally { _sizingByCode = false; }
            // A source bigger than this screen would show cropped at 1:1: ask it to take the size that fits.
            if (fit.Width < srcW || fit.Height < srcH) Input?.Invoke(Frame.MirrorResize(_id, fit.Width, fit.Height));
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
        // Repaint the touched area; the picture is shown at 1:1, inside the frame.
        Invalidate(new Rectangle(at.X + B, at.Y + B, region.Width, region.Height));
    }

    protected override void OnPaint(PaintEventArgs e)
    {
        var all = new Rectangle(Point.Empty, ClientSize);
        var inner = Inner;
        using (var frame = new Region(all))
        {
            frame.Exclude(inner);
            using var brush = new SolidBrush(_frameColor());
            e.Graphics.FillRegion(brush, frame);
        }
        if (_backing == null) { e.Graphics.FillRectangle(Brushes.Black, inner); return; }
        // 1:1 from the frame's inner corner. Where the window is bigger than the picture (the source has not
        // caught up with a resize yet, or will not go that big), the rest is black.
        e.Graphics.SetClip(inner);
        var pic = new Rectangle(B, B, _backing.Width, _backing.Height);
        e.Graphics.DrawImage(_backing, pic, new Rectangle(0, 0, _backing.Width, _backing.Height), GraphicsUnit.Pixel);
        using var black = new Region(inner);
        black.Exclude(pic);
        e.Graphics.FillRegion(Brushes.Black, black);
    }

    protected override void OnPaintBackground(PaintEventArgs e) { }   // OnPaint covers every pixel; no flash

    /// <summary>The picture's area: the client area inside the frame.</summary>
    private Rectangle Inner => new(B, B, Math.Max(0, ClientSize.Width - 2 * B), Math.Max(0, ClientSize.Height - 2 * B));

    protected override void OnResize(EventArgs e)
    {
        base.OnResize(e);
        Invalidate();   // the frame moves with the edges
        // Maximize, restore, Win+arrow snapping: no drag to wait for, the new size is final.
        if (!_sizing && !_sizingByCode && WindowState != FormWindowState.Minimized) RequestSourceSize();
    }

    protected override void OnResizeBegin(EventArgs e) { base.OnResizeBegin(e); _sizing = true; }

    protected override void OnResizeEnd(EventArgs e)
    {
        base.OnResizeEnd(e);
        _sizing = false;
        RequestSourceSize();   // also ends a plain move, which changes nothing and sends nothing
    }

    private void RequestSourceSize()
    {
        var inner = Inner.Size;
        if (_backing == null || inner.Width < 50 || inner.Height < 50) return;
        if (inner == _backing.Size) return;
        Input?.Invoke(Frame.MirrorResize(_id, inner.Width, inner.Height));
    }

    /// <summary>The mirror ended on the source: close without asking it to stop.</summary>
    public void CloseForMirror() { _closingForMirror = true; Close(); }

    protected override void OnFormClosing(FormClosingEventArgs e)
    {
        base.OnFormClosing(e);
        if (_closingForMirror || e.Cancel) return;
        // Closed here (the frame's menu, Windows shutting down): tell the source to stop this mirror, or its next
        // tile opens the window again (it did until 2026-10-05).
        ClosedByUser = true;
        Input?.Invoke(Frame.MirrorStop(_id));
    }

    // ---- interaction: viewer -> source ----

    /// <summary>A client point in source-window pixels (1:1 inside the frame), or null before the first frame
    /// or off the picture.</summary>
    private Point? ToSource(Point client)
    {
        if (_backing == null || !Inner.Contains(client)) return null;
        var p = new Point(client.X - B, client.Y - B);
        return p.X < _backing.Width && p.Y < _backing.Height ? p : null;
    }

    // ---- the frame: move, size, close ----

    private const int WM_NCHITTEST = 0x84, WM_NCRBUTTONUP = 0xA5, WM_NCLBUTTONDBLCLK = 0xA3;
    private const int HTCLIENT = 1, HTCAPTION = 2, HTLEFT = 10, HTRIGHT = 11, HTTOP = 12, HTTOPLEFT = 13, HTTOPRIGHT = 14,
        HTBOTTOM = 15, HTBOTTOMLEFT = 16, HTBOTTOMRIGHT = 17;

    /// <summary>What a point of the window is for: inside the frame the picture, on the top edge moving (its
    /// ends and the outer part of the corners size), on the other edges sizing.</summary>
    private int HitTest(Point c)
    {
        int b = B, w = ClientSize.Width, h = ClientSize.Height, corner = Math.Max(3 * b, 16);
        if (Inner.Contains(c)) return HTCLIENT;
        bool left = c.X < corner, right = c.X >= w - corner, top = c.Y < corner, bottom = c.Y >= h - corner;
        if (c.Y < b) return left ? HTTOPLEFT : right ? HTTOPRIGHT : HTCAPTION;
        if (c.Y >= h - b) return left ? HTBOTTOMLEFT : right ? HTBOTTOMRIGHT : HTBOTTOM;
        if (c.X < b) return top ? HTTOPLEFT : bottom ? HTBOTTOMLEFT : HTLEFT;
        return top ? HTTOPRIGHT : bottom ? HTBOTTOMRIGHT : HTRIGHT;
    }

    private void ShowFrameMenu(Point screen)
    {
        var menu = new ContextMenuStrip();
        menu.Items.Add("Close mirror", null, (_, _) => Close());
        menu.Closed += (_, _) => BeginInvoke(menu.Dispose);
        menu.Show(screen);
    }

    private void ToggleMaximize()
    {
        if (WindowState == FormWindowState.Maximized) { WindowState = FormWindowState.Normal; return; }
        // A window without a border would cover the taskbar when maximized: keep to the working area.
        var scr = Screen.FromControl(this);
        MaximizedBounds = new Rectangle(scr.WorkingArea.X - scr.Bounds.X, scr.WorkingArea.Y - scr.Bounds.Y, scr.WorkingArea.Width, scr.WorkingArea.Height);
        WindowState = FormWindowState.Maximized;
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
            case WM_NCHITTEST:
            {
                long lp = m.LParam.ToInt64();
                var c = PointToClient(new Point((short)(lp & 0xFFFF), (short)((lp >> 16) & 0xFFFF)));
                m.Result = (IntPtr)HitTest(c);
                return;
            }
            case WM_NCRBUTTONUP:
            {
                long lp = m.LParam.ToInt64();
                ShowFrameMenu(new Point((short)(lp & 0xFFFF), (short)((lp >> 16) & 0xFFFF)));
                return;
            }
            case WM_NCLBUTTONDBLCLK when (int)m.WParam == HTCAPTION:
                ToggleMaximize();
                return;
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
