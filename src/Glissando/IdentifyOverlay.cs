namespace Glissando;

/// <summary>
/// The big number Windows' Display settings shows on each screen under "Identify": one borderless box near the
/// bottom-left corner of every monitor, gone after a few seconds. The numbers are the ones the Screens tab draws
/// on the tiles. Click-through and never activated, so it does not take focus from whatever is in front.
/// </summary>
internal sealed class IdentifyOverlay : Form
{
    private const int ShowMs = 3000;
    private static readonly List<IdentifyOverlay> Open = [];

    private readonly string _number;
    private readonly string _caption;

    /// <summary>Shows the numbers on this PC's screens. <paramref name="otherPc"/>: the other PC asked for it
    /// (blue, like its screens on that PC's Screens tab); otherwise dark, like this PC's tiles.</summary>
    public static void ShowAll(bool otherPc)
    {
        foreach (var o in Open.ToArray()) o.Close();
        var monitors = Glissando.Layout.MyMonitors();
        var origin = Glissando.Layout.MyVirtualScreen().Location;
        var color = otherPc ? Color.FromArgb(66, 133, 244) : Color.FromArgb(30, 32, 46);
        foreach (var m in monitors)
        {
            var b = m.Bounds;
            b.Offset(origin);
            var overlay = new IdentifyOverlay(m.Number > 0 ? m.Number.ToString() : "?", Environment.MachineName, color);
            // Sized from the monitor itself (physical pixels), so it looks the same on every screen whatever its scaling.
            int side = Math.Clamp(b.Height / 4, 120, 400);
            int margin = side / 6;
            overlay.Show();
            SetWindowPos(overlay.Handle, HWND_TOPMOST, b.Left + margin, b.Bottom - margin - side, side, side, SWP_NOACTIVATE | SWP_SHOWWINDOW);
            Log.Info($"identify: screen {m.Number} at {b}{(otherPc ? " (asked by the other PC)" : "")}");
        }
    }

    private IdentifyOverlay(string number, string caption, Color color)
    {
        _number = number;
        _caption = caption;
        FormBorderStyle = FormBorderStyle.None;
        ShowInTaskbar = false;
        StartPosition = FormStartPosition.Manual;
        TopMost = true;
        BackColor = color;
        AutoScaleMode = AutoScaleMode.None;
        Opacity = 0.92;
        DoubleBuffered = true;
        Bounds = new Rectangle(-32000, -32000, 10, 10);
        Open.Add(this);
        var timer = new System.Windows.Forms.Timer { Interval = ShowMs };
        timer.Tick += (_, _) => { timer.Dispose(); Close(); };
        timer.Start();
        FormClosed += (_, _) => Open.Remove(this);
    }

    protected override bool ShowWithoutActivation => true;

    private static readonly IntPtr HWND_TOPMOST = new(-1);
    private const uint SWP_NOACTIVATE = 0x10, SWP_SHOWWINDOW = 0x40;
    [System.Runtime.InteropServices.DllImport("user32.dll")] private static extern bool SetWindowPos(IntPtr hWnd, IntPtr after, int x, int y, int cx, int cy, uint flags);

    protected override CreateParams CreateParams
    {
        get
        {
            const int WS_EX_TOOLWINDOW = 0x80, WS_EX_TRANSPARENT = 0x20, WS_EX_NOACTIVATE = 0x08000000, WS_EX_TOPMOST = 0x8;
            var cp = base.CreateParams;
            cp.ExStyle |= WS_EX_TOOLWINDOW | WS_EX_TRANSPARENT | WS_EX_NOACTIVATE | WS_EX_TOPMOST;
            return cp;
        }
    }

    protected override void OnResize(EventArgs e)
    {
        base.OnResize(e);
        Invalidate();
    }

    protected override void OnPaint(PaintEventArgs e)
    {
        base.OnPaint(e);
        var g = e.Graphics;
        g.TextRenderingHint = System.Drawing.Text.TextRenderingHint.AntiAliasGridFit;
        var fmt = new StringFormat { Alignment = StringAlignment.Center, LineAlignment = StringAlignment.Center };
        int h = ClientSize.Height;
        using var big = new Font("Segoe UI Semibold", h * 0.5f, GraphicsUnit.Pixel);
        using var small = new Font("Segoe UI", Math.Max(10, h * 0.08f), GraphicsUnit.Pixel);
        g.DrawString(_number, big, Brushes.White, new RectangleF(0, 0, ClientSize.Width, h * 0.82f), fmt);
        g.DrawString(_caption, small, Brushes.White, new RectangleF(0, h * 0.74f, ClientSize.Width, h * 0.2f), fmt);
    }
}
