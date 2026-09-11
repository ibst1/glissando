using System.Drawing;
using System.Drawing.Imaging;
using System.Runtime.InteropServices;
using System.Text;

namespace Glissando.Mirror;

/// <summary>
/// Captures a window's pixels with PrintWindow (GDI, no Direct3D), for the window-mirroring feature.
/// PrintWindow asks the window to paint itself on demand, so it works even when the window is off-screen or
/// DWM-cloaked -- which is how the source window will be hidden from its own screen while still being mirrored.
/// The trade-off: heavily GPU-rendered windows (video, some browsers) may come back partly blank. Fine for the
/// documents and dashboards this feature targets.
/// </summary>
internal static class WindowCapture
{
    private const uint PW_RENDERFULLCONTENT = 0x00000002;

    [DllImport("user32.dll")] private static extern bool PrintWindow(IntPtr hwnd, IntPtr hdc, uint flags);
    [DllImport("user32.dll")] private static extern bool GetWindowRect(IntPtr hwnd, out RECT r);
    [DllImport("user32.dll")] private static extern bool IsWindow(IntPtr hwnd);
    [DllImport("user32.dll")] private static extern bool IsWindowVisible(IntPtr hwnd);
    [DllImport("user32.dll")] private static extern IntPtr GetForegroundWindow();
    [DllImport("user32.dll")] private static extern uint GetWindowThreadProcessId(IntPtr hwnd, out uint pid);
    [DllImport("user32.dll", CharSet = CharSet.Unicode)] private static extern int GetWindowTextW(IntPtr hwnd, StringBuilder s, int max);
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

    /// <summary>Capture the window to a 32bpp bitmap, or null if it has no area or is gone.</summary>
    public static Bitmap? Capture(IntPtr hwnd)
    {
        if (SizeOf(hwnd) is not Size s) return null;
        var bmp = new Bitmap(s.Width, s.Height, PixelFormat.Format32bppArgb);
        using (var g = Graphics.FromImage(bmp))
        {
            IntPtr hdc = g.GetHdc();
            try { PrintWindow(hwnd, hdc, PW_RENDERFULLCONTENT); }
            finally { g.ReleaseHdc(hdc); }
        }
        return bmp;
    }

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
