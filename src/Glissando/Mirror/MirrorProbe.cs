using System.Diagnostics;
using System.Drawing;
using System.Drawing.Imaging;
using System.Runtime.InteropServices;

namespace Glissando.Mirror;

/// <summary>
/// Increment 1 of window mirroring: capture one window and measure how many bytes per second it would take to
/// stream it, as a full JPEG frame and as a dirty rectangle (only the changed region). Runs on one PC and needs
/// no peer -- it answers the gating question before the transport is built: can BLE (~180 kB/s here) carry this
/// particular window, or does it need a faster link?
/// </summary>
internal static class MirrorProbe
{
    private const double BleCeilingKBs = 180; // rough sustained app throughput between the author's adapters

    public static void Run(IntPtr hwnd, int seconds, int fps, long quality, int tile = 64)
    {
        var title = WindowCapture.Title(hwnd);
        var size = WindowCapture.SizeOf(hwnd);
        Console.WriteLine($"Mirror probe: \"{title}\"  {size?.Width}x{size?.Height}  {fps} fps  q{quality}  {seconds}s");
        Console.WriteLine("The dirty-rect figure is the bounding box of all changed tiles, an upper bound when");
        Console.WriteLine("changes are scattered. Interact with the window (type, move the caret) while it runs.\n");

        byte[]? prev = null;
        int prevStride = 0, prevW = 0, prevH = 0;
        long fullTotal = 0, dirtyTotal = 0, frames = 0, changed = 0, fullMax = 0, dirtyMax = 0;
        int interval = Math.Max(1, 1000 / Math.Max(1, fps));
        var run = Stopwatch.StartNew();
        var frame = Stopwatch.StartNew();

        while (run.Elapsed.TotalSeconds < seconds)
        {
            frame.Restart();
            using var bmp = WindowCapture.Capture(hwnd);
            if (bmp == null) { Console.WriteLine("window gone"); break; }
            frames++;

            byte[] full = WindowCapture.EncodeJpeg(bmp, quality);
            fullTotal += full.Length; fullMax = Math.Max(fullMax, full.Length);

            var cur = WindowCapture.Pixels(bmp, out int stride);

            long dirty = 0;
            if (prev != null && prevW == bmp.Width && prevH == bmp.Height && prevStride == stride)
            {
                if (WindowCapture.ChangedBox(prev, cur, stride, bmp.Width, bmp.Height, tile) is Rectangle box)
                {
                    changed++;
                    dirty = WindowCapture.EncodeJpeg(bmp, quality, box).Length;
                }
            }
            else { changed++; dirty = full.Length; } // first frame is a full send
            dirtyTotal += dirty; dirtyMax = Math.Max(dirtyMax, dirty);

            prev = cur; prevStride = stride; prevW = bmp.Width; prevH = bmp.Height;

            int spent = (int)frame.ElapsedMilliseconds;
            if (spent < interval) Thread.Sleep(interval - spent);
        }

        double secs = run.Elapsed.TotalSeconds;
        double fullKBs = fullTotal / 1024.0 / secs;
        double dirtyKBs = dirtyTotal / 1024.0 / secs;
        Console.WriteLine($"frames {frames}, changed {changed}, over {secs:F1}s");
        Console.WriteLine($"full-frame : {fullTotal / 1024.0 / Math.Max(1, frames):F0} kB/frame avg, {fullMax / 1024.0:F0} kB max  ->  {fullKBs:F0} kB/s");
        Console.WriteLine($"dirty-rect : {(changed > 0 ? dirtyTotal / 1024.0 / changed : 0):F0} kB/change avg, {dirtyMax / 1024.0:F0} kB max  ->  {dirtyKBs:F0} kB/s");
        Console.WriteLine($"\nBLE carries about {BleCeilingKBs:F0} kB/s between these adapters.");
        Console.WriteLine(dirtyKBs <= BleCeilingKBs
            ? $"-> dirty-rect fits ({dirtyKBs:F0} <= {BleCeilingKBs:F0} kB/s). Viable on BLE for this window."
            : $"-> dirty-rect exceeds it ({dirtyKBs:F0} > {BleCeilingKBs:F0} kB/s). Needs a faster transport, or lower fps/quality.");
    }
}
