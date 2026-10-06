using System.Drawing;
using System.Drawing.Imaging;
using System.Runtime.InteropServices;
using System.Text;

namespace Glissando.Mirror;

/// <summary>
/// Captures a window's pixels with PrintWindow (GDI, no Direct3D), for the window-mirroring feature.
/// PrintWindow asks the window to paint itself on demand, so it works even when the window is behind others or
/// made invisible with layered alpha -- which is how WindowHider hides the source window from its own screen
/// while it is mirrored.
/// The trade-off: heavily GPU-rendered windows (video, some browsers) may come back partly blank. Fine for the
/// documents and dashboards this feature targets.
/// </summary>
internal static class WindowCapture
{
    private const uint PW_RENDERFULLCONTENT = 0x00000002;

    [DllImport("user32.dll")] private static extern bool PrintWindow(IntPtr hwnd, IntPtr hdc, uint flags);
    [DllImport("user32.dll")] private static extern IntPtr GetDC(IntPtr hwnd);
    [DllImport("user32.dll")] private static extern int ReleaseDC(IntPtr hwnd, IntPtr hdc);
    [DllImport("gdi32.dll")] private static extern bool BitBlt(IntPtr hdcDest, int x, int y, int w, int h, IntPtr hdcSrc, int sx, int sy, uint rop);
    private const uint SRCCOPY = 0x00CC0020, CAPTUREBLT = 0x40000000;
    [DllImport("user32.dll")] private static extern bool GetWindowRect(IntPtr hwnd, out RECT r);
    [DllImport("user32.dll")] private static extern bool IsWindow(IntPtr hwnd);
    [DllImport("user32.dll")] private static extern bool IsWindowVisible(IntPtr hwnd);
    [DllImport("user32.dll")] private static extern IntPtr GetForegroundWindow();
    [DllImport("user32.dll")] private static extern uint GetWindowThreadProcessId(IntPtr hwnd, out uint pid);
    // InternalGetWindowText, not GetWindowText: GetWindowText of another process's window sends it WM_GETTEXT
    // and waits for the answer. The tray reads the foreground window's title every 400 ms on the UI thread,
    // which also runs our low-level input hooks; while the program in front was busy taking the keys typed
    // into it, that wait blocked our hooks, so every injected key waited out the 1 s hook timeout (RS driving
    // LU, 2026-10-06: typing crawled, SendInput 1000 ms). InternalGetWindowText reads the title Windows keeps
    // and never waits on the window's thread.
    [DllImport("user32.dll", CharSet = CharSet.Unicode, EntryPoint = "InternalGetWindowText")] private static extern int GetWindowTextW(IntPtr hwnd, StringBuilder s, int max);
    [DllImport("user32.dll")] private static extern bool EnumWindows(EnumProc cb, IntPtr lp);
    private delegate bool EnumProc(IntPtr hwnd, IntPtr lp);

    [StructLayout(LayoutKind.Sequential)] private struct RECT { public int Left, Top, Right, Bottom; }

    public static IntPtr Foreground() => GetForegroundWindow();
    public static bool Visible(IntPtr hwnd) => IsWindowVisible(hwnd);
    public static uint ProcessId(IntPtr hwnd) { GetWindowThreadProcessId(hwnd, out uint pid); return pid; }

    public static string Title(IntPtr hwnd)
    {
        var sb = new StringBuilder(512);
        return GetWindowTextW(hwnd, sb, sb.Capacity) > 0 ? sb.ToString() : "";
    }

    public static Size? SizeOf(IntPtr hwnd)
    {
        if (!IsWindow(hwnd) || !GetWindowRect(hwnd, out var r)) return null;
        int w = r.Right - r.Left, h = r.Bottom - r.Top;
        return w > 0 && h > 0 ? new Size(w, h) : null;
    }

    /// <summary>True when the screen shows this window in front: it is the foreground window, or the foreground
    /// window belongs to the same process -- a context menu, drop-down, mini toolbar or dialog of it. Office's
    /// popups take activation, so while one is open the foreground window is the popup, not the app.</summary>
    public static bool InFront(IntPtr hwnd)
    {
        IntPtr fg = GetForegroundWindow();
        return fg == hwnd || (fg != IntPtr.Zero && ProcessId(fg) == ProcessId(hwnd));
    }

    /// <summary>Capture the window to a 32bpp bitmap, or null if it has no area or is gone.
    /// Menus, drop-downs, tooltips and dialogs are top-level windows of their own, which PrintWindow of the
    /// main window never shows. So while the window is in front (see InFront) -- always the case when the
    /// viewer is interacting with it -- the picture is taken from the screen inside its rectangle instead,
    /// popups included (CAPTUREBLT for the layered ones Office uses). Otherwise PrintWindow, which also works
    /// for a window behind others or off screen, and never shows whatever happens to cover it.</summary>
    public static Bitmap? Capture(IntPtr hwnd) => Capture(hwnd, hidden: false, out _);

    /// <param name="hidden">The window is invisible on this screen (WindowHider): the screen inside its rectangle
    /// shows what lies behind it, so it is always drawn with PrintWindow; its popups are opaque windows of their
    /// own and still show on the screen, so those are copied from the screen on top.</param>
    public static Bitmap? Capture(IntPtr hwnd, bool hidden, out bool fromScreen)
    {
        fromScreen = false;
        if (!IsWindow(hwnd) || !GetWindowRect(hwnd, out var r)) return null;
        int w = r.Right - r.Left, h = r.Bottom - r.Top;
        if (w <= 0 || h <= 0) return null;
        var bmp = new Bitmap(w, h, PixelFormat.Format32bppArgb);
        using (var g = Graphics.FromImage(bmp))
        {
            IntPtr hdc = g.GetHdc();
            try
            {
                bool done = false;
                if (!hidden && InFront(hwnd))
                {
                    IntPtr screen = GetDC(IntPtr.Zero);
                    try { done = BitBlt(hdc, 0, 0, w, h, screen, r.Left, r.Top, SRCCOPY | CAPTUREBLT); }
                    finally { ReleaseDC(IntPtr.Zero, screen); }
                }
                fromScreen = done;
                if (!done)
                {
                    PrintWindow(hwnd, hdc, PW_RENDERFULLCONTENT);
                    if (hidden) { WindowHider.ReleaseGonePopups(); OverlayPopups(hwnd, hdc, r, hidden: true); }
                }
            }
            finally { g.ReleaseHdc(hdc); }
        }
        return bmp;
    }

    private const int GWL_STYLE = -16;
    private const long WS_POPUP = 0x80000000;

    /// <summary>The visible popup windows of the window's process (menus, drop-downs, tooltips, dialogs: WS_POPUP)
    /// clipped to the rectangle r, screen coordinates; top-most first as EnumWindows lists them.
    /// Only those above the window: a popup shows over its owner, and EnumWindows goes top to bottom, so the
    /// search ends at the window itself. And never the shell's own surfaces: File Explorer's windows live in
    /// explorer.exe beside the desktop and the taskbar, both WS_POPUP, and the desktop covering the whole screen
    /// was copied from the screen over the mirror -- which, with the window hidden, showed whatever lay behind
    /// it (2026-10-05).</summary>
    private static List<(IntPtr h, RECT clip, RECT full)> FindPopups(IntPtr hwnd, RECT r)
    {
        uint pid = ProcessId(hwnd);
        var popups = new List<(IntPtr, RECT, RECT)>();
        var cls = new StringBuilder(64);
        EnumWindows((h, _) =>
        {
            if (h == hwnd) return false;   // below this, nothing can be a popup over the window
            if (!IsWindowVisible(h) || ProcessId(h) != pid) return true;
            if ((GetWindowLongPtr(h, GWL_STYLE).ToInt64() & WS_POPUP) == 0) return true;
            if (DwmGetWindowAttributeInt(h, DWMWA_CLOAKED, out int cloaked, sizeof(int)) == 0 && cloaked != 0) return true;
            cls.Clear();
            GetClassNameW(h, cls, cls.Capacity);
            if (ShellSurfaces.Contains(cls.ToString())) return true;
            if (!GetWindowRect(h, out var p)) return true;
            int left = Math.Max(p.Left, r.Left), top = Math.Max(p.Top, r.Top);
            int right = Math.Min(p.Right, r.Right), bottom = Math.Min(p.Bottom, r.Bottom);
            if (right > left && bottom > top) popups.Add((h, new RECT { Left = left, Top = top, Right = right, Bottom = bottom }, p));
            return true;
        }, IntPtr.Zero);
        return popups;
    }

    [DllImport("dwmapi.dll")] private static extern int DwmGetWindowAttribute(IntPtr hwnd, int attr, out RECT rect, int size);
    [DllImport("dwmapi.dll", EntryPoint = "DwmGetWindowAttribute")] private static extern int DwmGetWindowAttributeInt(IntPtr hwnd, int attr, out int value, int size);
    [DllImport("user32.dll", CharSet = CharSet.Unicode)] private static extern int GetClassNameW(IntPtr hwnd, StringBuilder s, int max);
    private const int DWMWA_CLOAKED = 14;

    /// <summary>The desktop, its wallpaper layer and the taskbars: WS_POPUP windows of explorer.exe that are no
    /// File Explorer window's popups.</summary>
    private static readonly HashSet<string> ShellSurfaces = ["Progman", "WorkerW", "Shell_TrayWnd", "Shell_SecondaryTrayWnd"];

    /// <summary>The window's visible frame in screen pixels - what the compositor shows and what
    /// Windows.Graphics.Capture delivers - without the invisible resize borders (7-8 px a side) that
    /// GetWindowRect includes on Windows 10/11. Falls back to GetWindowRect.</summary>
    public static Rectangle VisibleBounds(IntPtr hwnd)
    {
        const int DWMWA_EXTENDED_FRAME_BOUNDS = 9;
        if (DwmGetWindowAttribute(hwnd, DWMWA_EXTENDED_FRAME_BOUNDS, out var r, Marshal.SizeOf<RECT>()) == 0 && r.Right > r.Left && r.Bottom > r.Top)
            return Rectangle.FromLTRB(r.Left, r.Top, r.Right, r.Bottom);
        return GetWindowRect(hwnd, out var w) ? Rectangle.FromLTRB(w.Left, w.Top, w.Right, w.Bottom) : Rectangle.Empty;
    }

    /// <summary>Copy the app's popups onto a 32bpp buffer holding the window's visible bounds (a GPU-captured
    /// frame, which has only the window's own surface). False when there are none.</summary>
    /// <param name="hidden">The window is hidden on this PC: its popups are hidden too (see OverlayPopups).</param>
    public static bool OverlayPopupsOnto(byte[] pixels, int stride, int w, int h, IntPtr hwnd, bool hidden)
    {
        WindowHider.ReleaseGonePopups();
        var vb = VisibleBounds(hwnd);
        if (vb.IsEmpty) return false;
        var r = new RECT { Left = vb.Left, Top = vb.Top, Right = vb.Left + w, Bottom = vb.Top + h };
        if (FindPopups(hwnd, r).Count == 0) return false;
        var handle = GCHandle.Alloc(pixels, GCHandleType.Pinned);
        try
        {
            using var bmp = new Bitmap(w, h, stride, PixelFormat.Format32bppArgb, handle.AddrOfPinnedObject());
            using var g = Graphics.FromImage(bmp);
            IntPtr hdc = g.GetHdc();
            try { OverlayPopups(hwnd, hdc, r, hidden); } finally { g.ReleaseHdc(hdc); }
        }
        finally { handle.Free(); }
        return true;
    }

    /// <summary>Copy the process's visible popup windows (menus, drop-downs, tooltips, dialogs: WS_POPUP, above
    /// their owner) that overlap the window rectangle onto the capture, bottom-most first so the z-order holds.
    /// Only the part inside the window rectangle, which is all the capture covers.
    /// Normally from the screen. But when the window is hidden on this PC its popups would still show here -- a
    /// right-click menu opened through the mirror appeared on both PCs (2026-10-05) -- so they are hidden the
    /// same way (WindowHider.HidePopup) and drawn with PrintWindow instead; a popup that cannot be hidden (one
    /// layered per pixel) stays visible and comes from the screen as before.</summary>
    private static void OverlayPopups(IntPtr hwnd, IntPtr hdc, RECT r, bool hidden)
    {
        var popups = FindPopups(hwnd, r);
        if (popups.Count == 0) return;
        IntPtr screen = GetDC(IntPtr.Zero);
        try
        {
            for (int i = popups.Count - 1; i >= 0; i--) // EnumWindows lists top-most first
            {
                var (h, c, f) = popups[i];
                int cw = c.Right - c.Left, ch = c.Bottom - c.Top;
                if (hidden && WindowHider.HidePopup(h) && PrintPopup(h, f, hdc, c.Left - r.Left, c.Top - r.Top, c.Left - f.Left, c.Top - f.Top, cw, ch))
                    continue;
                BitBlt(hdc, c.Left - r.Left, c.Top - r.Top, cw, ch, screen, c.Left, c.Top, SRCCOPY | CAPTUREBLT);
            }
        }
        finally { ReleaseDC(IntPtr.Zero, screen); }
    }

    /// <summary>Draw part of a popup with PrintWindow (it may be invisible on the screen) onto hdc.</summary>
    private static bool PrintPopup(IntPtr h, RECT full, IntPtr hdc, int dx, int dy, int sx, int sy, int w, int ht)
    {
        int fw = full.Right - full.Left, fh = full.Bottom - full.Top;
        if (fw <= 0 || fh <= 0) return false;
        using var pb = new Bitmap(fw, fh, PixelFormat.Format32bppArgb);
        using var g = Graphics.FromImage(pb);
        IntPtr phdc = g.GetHdc();
        try
        {
            if (!PrintWindow(h, phdc, PW_RENDERFULLCONTENT)) return false;
            return BitBlt(hdc, dx, dy, w, ht, phdc, sx, sy, SRCCOPY);
        }
        finally { g.ReleaseHdc(phdc); }
    }

    [DllImport("user32.dll")] private static extern IntPtr GetWindowLongPtr(IntPtr hwnd, int index);

    private static ImageCodecInfo? _jpeg;

    /// <summary>Encode a bitmap (or a sub-rectangle of it) to JPEG at the given quality (1-100).</summary>
    public static byte[] EncodeJpeg(Bitmap bmp, long quality, Rectangle? region = null)
    {
        _jpeg ??= ImageCodecInfo.GetImageEncoders().First(c => c.FormatID == ImageFormat.Jpeg.Guid);
        using var p = new EncoderParameters(1);
        p.Param[0] = new EncoderParameter(System.Drawing.Imaging.Encoder.Quality, quality);
        using var ms = new MemoryStream();
        if (region is Rectangle r && r != new Rectangle(Point.Empty, bmp.Size))
        {
            using var crop = bmp.Clone(r, bmp.PixelFormat);
            crop.Save(ms, _jpeg, p);
        }
        else bmp.Save(ms, _jpeg, p);
        return ms.ToArray();
    }

    /// <summary>Encode many tiles of one captured frame in parallel, straight from its 32bpp pixel buffer: each
    /// tile is a Bitmap over the shared memory (no copy, no Clone), and GDI+ encodes separate Bitmap objects on
    /// separate threads without complaint. Serial Clone+Save managed ~360 tiles/s; this scales with the cores.</summary>
    public static byte[][] EncodeTiles(byte[] pixels, int stride, IReadOnlyList<Rectangle> regions, long quality)
    {
        _jpeg ??= ImageCodecInfo.GetImageEncoders().First(c => c.FormatID == ImageFormat.Jpeg.Guid);
        var result = new byte[regions.Count][];
        if (regions.Count == 0) return result;
        var handle = GCHandle.Alloc(pixels, GCHandleType.Pinned);
        try
        {
            IntPtr basePtr = handle.AddrOfPinnedObject();
            var opts = new ParallelOptions { MaxDegreeOfParallelism = Math.Max(1, Environment.ProcessorCount - 2) };
            Parallel.For(0, regions.Count, opts, i =>
            {
                var r = regions[i];
                using var tile = new Bitmap(r.Width, r.Height, stride, PixelFormat.Format32bppArgb, basePtr + r.Y * stride + r.X * 4);
                result[i] = EncodeJpeg(tile, quality);
            });
        }
        finally { handle.Free(); }
        return result;
    }

    /// <summary>Union rectangle of all <paramref name="tile"/>-sized blocks whose 32bpp pixels differ between
    /// two same-size frames, or null if identical.</summary>
    public static Rectangle? ChangedBox(byte[] a, byte[] b, int stride, int w, int h, int tile)
    {
        int minX = int.MaxValue, minY = int.MaxValue, maxX = -1, maxY = -1;
        for (int ty = 0; ty < h; ty += tile)
        {
            int ty2 = Math.Min(ty + tile, h);
            for (int tx = 0; tx < w; tx += tile)
            {
                int tx2 = Math.Min(tx + tile, w);
                if (!TileEqual(a, b, stride, tx, ty, tx2, ty2))
                {
                    if (tx < minX) minX = tx;
                    if (ty < minY) minY = ty;
                    if (tx2 > maxX) maxX = tx2;
                    if (ty2 > maxY) maxY = ty2;
                }
            }
        }
        return maxX < 0 ? null : new Rectangle(minX, minY, maxX - minX, maxY - minY);
    }

    public static void CopyTile(byte[] src, byte[] dst, int stride, int x0, int y0, int x1, int y1)
    {
        int bytes = (x1 - x0) * 4;
        for (int y = y0; y < y1; y++) Array.Copy(src, y * stride + x0 * 4, dst, y * stride + x0 * 4, bytes);
    }

    public static bool TileEqual(byte[] a, byte[] b, int stride, int x0, int y0, int x1, int y1)
    {
        int bytes = (x1 - x0) * 4;
        for (int y = y0; y < y1; y++)
        {
            int off = y * stride + x0 * 4;
            if (!a.AsSpan(off, bytes).SequenceEqual(b.AsSpan(off, bytes))) return false;
        }
        return true;
    }

    /// <summary>Lock the bitmap and copy its 32bpp pixels out; returns the row stride.</summary>
    public static byte[] Pixels(Bitmap bmp, out int stride)
    {
        var bits = bmp.LockBits(new Rectangle(0, 0, bmp.Width, bmp.Height), ImageLockMode.ReadOnly, PixelFormat.Format32bppArgb);
        stride = bits.Stride;
        var buf = new byte[stride * bmp.Height];
        Marshal.Copy(bits.Scan0, buf, 0, buf.Length);
        bmp.UnlockBits(bits);
        return buf;
    }

    /// <summary>First top-level visible window whose title contains <paramref name="titleSubstring"/>.</summary>
    public static IntPtr Find(string titleSubstring)
    {
        IntPtr found = IntPtr.Zero;
        var sb = new StringBuilder(512);
        EnumWindows((h, _) =>
        {
            if (!IsWindowVisible(h)) return true;
            sb.Clear();
            if (GetWindowTextW(h, sb, sb.Capacity) > 0 &&
                sb.ToString().Contains(titleSubstring, StringComparison.OrdinalIgnoreCase))
            {
                found = h;
                return false;
            }
            return true;
        }, IntPtr.Zero);
        return found;
    }
}
