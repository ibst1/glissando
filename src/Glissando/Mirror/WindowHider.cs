using System.Runtime.InteropServices;

namespace Glissando.Mirror;

/// <summary>
/// Hides the source window from its own screen while it is mirrored (increment 3), so the window seems to have
/// moved over to the other PC. The mirror drives the window with SendInput, so it must stay on screen,
/// hit-testable and able to become the foreground window; that rules out the obvious ways of hiding it: the
/// cursor cannot reach a window moved off screen, and a DWM-cloaked window is skipped by hit-testing. What does
/// work is layered-window alpha. At 1/255 the window is invisible to the eye yet still takes clicks and keys,
/// still paints for PrintWindow, and keeps its taskbar button and Alt+Tab entry, so it is plainly still here.
/// (Alpha 0 would let clicks fall through to whatever lies behind, which is what "invisible but clickable"
/// overlays exploit the other way round.) Each mirrored window is hidden on its own, several at once.
///
/// The hidden windows are noted in a file, so that if Glissando is killed while they are hidden, the next
/// start makes them visible again -- the same self-healing the cursor scheme has.
/// </summary>
internal static class WindowHider
{
    private const int GWL_EXSTYLE = -20;
    private const long WS_EX_LAYERED = 0x80000;
    private const long WS_EX_TRANSPARENT = 0x20;
    private const long WS_EX_TOOLWINDOW = 0x80;
    private const uint GW_HWNDNEXT = 2;
    private const int DWMWA_CLOAKED = 14;
    private static readonly IntPtr HWND_BOTTOM = new(1);
    private const uint LWA_ALPHA = 0x2;
    private const byte HiddenAlpha = 1;
    private const uint SWP_NOSIZE = 0x1, SWP_NOMOVE = 0x2, SWP_NOZORDER = 0x4, SWP_NOACTIVATE = 0x10, SWP_FRAMECHANGED = 0x20;
    private const uint RDW_INVALIDATE = 0x1, RDW_ERASE = 0x4, RDW_ALLCHILDREN = 0x80, RDW_FRAME = 0x400;

    private static readonly object Gate = new();
    private static readonly Dictionary<IntPtr, long> Hidden = [];   // hidden window -> its original extended style

    /// <summary>True while the given window is one made invisible.</summary>
    public static bool IsHidden(IntPtr hwnd) { lock (Gate) return Hidden.ContainsKey(hwnd); }

    /// <summary>True while any window is invisible.</summary>
    public static bool AnyHidden { get { lock (Gate) return Hidden.Count > 0; } }

    private static string MarkerPath => Path.Combine(Path.GetDirectoryName(Log.FilePath)!, "hidden-window.txt");

    /// <summary>One line per hidden window, so a killed instance's windows can all be healed.</summary>
    private static void WriteMarker()
    {
        try
        {
            if (Hidden.Count == 0) File.Delete(MarkerPath);
            else File.WriteAllLines(MarkerPath, Hidden.Select(kv => $"{kv.Key.ToInt64()} {kv.Value}"));
        }
        catch (Exception e) { Log.Warn("hidden-window marker: " + e.Message); }
    }

    /// <summary>Make the window invisible in place. False (and nothing changed) if Windows refuses: an elevated
    /// window (UIPI), or one already layered per pixel, whose painting alpha attributes would break.</summary>
    public static bool Hide(IntPtr hwnd)
    {
        lock (Gate)
        {
            if (Hidden.ContainsKey(hwnd)) return true;
            if (!IsWindow(hwnd)) return false;

            long ex = GetWindowLongPtr(hwnd, GWL_EXSTYLE).ToInt64();
            bool wasLayered = (ex & WS_EX_LAYERED) != 0;
            if (wasLayered && !GetLayeredWindowAttributes(hwnd, out _, out _, out _))
            {
                Log.Warn($"mirror: window {hwnd:X} is a per-pixel layered window; leaving it visible");
                return false;
            }
            if (!wasLayered)
            {
                SetWindowLongPtr(hwnd, GWL_EXSTYLE, new IntPtr(ex | WS_EX_LAYERED));
                int err = Marshal.GetLastWin32Error();
                if ((GetWindowLongPtr(hwnd, GWL_EXSTYLE).ToInt64() & WS_EX_LAYERED) == 0)
                {
                    Log.Warn($"mirror: cannot hide window {hwnd:X} (error {err}; elevated?); leaving it visible");
                    return false;
                }
            }
            if (!SetLayeredWindowAttributes(hwnd, 0, HiddenAlpha, LWA_ALPHA))
            {
                int err = Marshal.GetLastWin32Error();
                if (!wasLayered) SetWindowLongPtr(hwnd, GWL_EXSTYLE, new IntPtr(ex));
                Log.Warn($"mirror: cannot hide window {hwnd:X} (error {err}); leaving it visible");
                return false;
            }
            Hidden[hwnd] = ex;
            WriteMarker();
            Log.Info($"mirror: window {hwnd:X} hidden on this PC");
            return true;
        }
    }

    /// <summary>
    /// Let clicks pass through the hidden windows to whatever lies behind them, or stop them doing so. A click on
    /// the mirror brings the source window to the front here; when the viewer's mouse then crosses over to
    /// drive this PC, its clicks (injected, so they do not count as someone sitting here) landed on the
    /// invisible window and nothing behind it could be clicked (2026-10-05). So they are click-through while this
    /// PC is being controlled, and solid again whenever a mirror's own input has to reach one.
    /// </summary>
    public static void SetClickThrough(bool on)
    {
        lock (Gate) foreach (var h in Hidden.Keys) SetClickThroughLocked(h, on);
    }

    /// <summary>The same for one hidden window (a mirror about to click or type into it).</summary>
    public static void SetClickThrough(IntPtr hwnd, bool on)
    {
        lock (Gate) if (Hidden.ContainsKey(hwnd)) SetClickThroughLocked(hwnd, on);
    }

    private static void SetClickThroughLocked(IntPtr hwnd, bool on)
    {
        if (!IsWindow(hwnd)) return;
        long ex = GetWindowLongPtr(hwnd, GWL_EXSTYLE).ToInt64();
        long want = on ? ex | WS_EX_TRANSPARENT : ex & ~WS_EX_TRANSPARENT;
        if (want == ex) return;
        SetWindowLongPtr(hwnd, GWL_EXSTYLE, new IntPtr(want));
        Log.Info($"mirror: hidden window {hwnd:X} {(on ? "lets clicks through" : "takes clicks again")}");
    }

    /// <summary>
    /// If a hidden window holds the focus, hand it to the first visible window behind it and send the hidden
    /// one to the back. A click on the mirror leaves the source window focused here; once the viewer's mouse
    /// crossed over to drive this PC, its typing went into that invisible window until something else was
    /// clicked (2026-10-05).
    /// </summary>
    public static void StepAside()
    {
        lock (Gate)
        {
            IntPtr fg = GetForegroundWindow();
            if (!Hidden.ContainsKey(fg)) return;
            IntPtr next = NextBehind(fg);
            SetWindowPos(fg, HWND_BOTTOM, 0, 0, 0, 0, SWP_NOMOVE | SWP_NOSIZE | SWP_NOACTIVATE);
            if (next == IntPtr.Zero) next = GetShellWindow();
            bool ok = next != IntPtr.Zero && MirrorTarget.ForceForeground(next);
            Log.Info($"mirror: hidden window {fg:X} gave up the focus to \"{WindowCapture.Title(next)}\"{(ok ? "" : " (refused)")}");
        }
    }

    /// <summary>The first window below this one in the z-order that a person could see and work in.</summary>
    private static IntPtr NextBehind(IntPtr hwnd)
    {
        for (IntPtr h = GetWindow(hwnd, GW_HWNDNEXT); h != IntPtr.Zero; h = GetWindow(h, GW_HWNDNEXT))
        {
            if (Hidden.ContainsKey(h) || !IsWindowVisible(h) || IsIconic(h)) continue;
            if ((GetWindowLongPtr(h, GWL_EXSTYLE).ToInt64() & WS_EX_TOOLWINDOW) != 0) continue;
            if (DwmGetWindowAttribute(h, DWMWA_CLOAKED, out int cloaked, sizeof(int)) == 0 && cloaked != 0) continue;
            if (WindowCapture.SizeOf(h) is not { } s || s.Width < 50 || s.Height < 50) continue;
            if (GetWindowLongPtr(h, GWL_EXSTYLE).ToInt64() is var ex && (ex & WS_EX_LAYERED) != 0
                && GetLayeredWindowAttributes(h, out _, out byte a, out uint f) && (f & LWA_ALPHA) != 0 && a < 16) continue;
            return h;
        }
        return IntPtr.Zero;
    }

    /// <summary>Show this window again (no-op if it is not hidden).</summary>
    public static void Unhide(IntPtr hwnd)
    {
        lock (Gate)
        {
            if (!Hidden.Remove(hwnd, out long ex)) return;
            Restore(hwnd, ex);
            AfterUnhideLocked();
        }
    }

    /// <summary>Show every hidden window again: on exit, a crash, the session closing.</summary>
    public static void UnhideAll()
    {
        lock (Gate)
        {
            if (Hidden.Count == 0) return;
            foreach (var (h, ex) in Hidden.ToArray()) Restore(h, ex);
            Hidden.Clear();
            AfterUnhideLocked();
        }
    }

    private static void AfterUnhideLocked()
    {
        // The cursor is hidden for the mirrors only while there is a hidden window to drive it over.
        if (Hidden.Count == 0) Host.CursorHider.Restore(Host.CursorHider.Mirror);
        WriteMarker();
    }

    private static void Restore(IntPtr h, long originalExStyle)
    {
        if (!IsWindow(h)) return;
        SetLayeredWindowAttributes(h, 0, 255, LWA_ALPHA);
        long cur = GetWindowLongPtr(h, GWL_EXSTYLE).ToInt64();
        long restored = cur & ~(WS_EX_LAYERED | WS_EX_TRANSPARENT) | originalExStyle & (WS_EX_LAYERED | WS_EX_TRANSPARENT);
        if (restored != cur) SetWindowLongPtr(h, GWL_EXSTYLE, new IntPtr(restored));
        // Dropping WS_EX_LAYERED does not repaint by itself.
        SetWindowPos(h, IntPtr.Zero, 0, 0, 0, 0, SWP_NOMOVE | SWP_NOSIZE | SWP_NOZORDER | SWP_NOACTIVATE | SWP_FRAMECHANGED);
        RedrawWindow(h, IntPtr.Zero, IntPtr.Zero, RDW_INVALIDATE | RDW_ERASE | RDW_ALLCHILDREN | RDW_FRAME);
        Log.Info($"mirror: window {h:X} visible again");
    }

    /// <summary>If an earlier instance died with windows hidden, make them visible again. A handle may have been
    /// reused since, so it is only touched when it still carries our signature: layered at alpha 1.</summary>
    public static void HealAtStartup()
    {
        string path = MarkerPath;
        try
        {
            if (!File.Exists(path)) return;
            var lines = File.ReadAllLines(path);
            File.Delete(path);
            foreach (var line in lines)
            {
                var parts = line.Trim().Split(' ');
                if (parts.Length < 2 || !long.TryParse(parts[0], out long hv) || !long.TryParse(parts[1], out long ex)) continue;
                var h = new IntPtr(hv);
                if (!IsWindow(h) || !GetLayeredWindowAttributes(h, out _, out byte alpha, out uint flags)) continue;
                if ((flags & LWA_ALPHA) == 0 || alpha != HiddenAlpha) continue;
                Restore(h, ex);
                Log.Info("a window left invisible by an earlier instance was restored");
            }
        }
        catch (Exception e) { Log.Warn("hidden-window heal: " + e.Message); }
    }

    [DllImport("user32.dll")] private static extern bool IsWindow(IntPtr hwnd);
    [DllImport("user32.dll", SetLastError = true)] private static extern IntPtr GetWindowLongPtr(IntPtr hwnd, int index);
    [DllImport("user32.dll", SetLastError = true)] private static extern IntPtr SetWindowLongPtr(IntPtr hwnd, int index, IntPtr value);
    [DllImport("user32.dll", SetLastError = true)] private static extern bool SetLayeredWindowAttributes(IntPtr hwnd, uint key, byte alpha, uint flags);
    [DllImport("user32.dll")] private static extern bool GetLayeredWindowAttributes(IntPtr hwnd, out uint key, out byte alpha, out uint flags);
    [DllImport("user32.dll")] private static extern bool SetWindowPos(IntPtr hwnd, IntPtr after, int x, int y, int w, int h, uint flags);
    [DllImport("user32.dll")] private static extern IntPtr GetForegroundWindow();
    [DllImport("user32.dll")] private static extern IntPtr GetWindow(IntPtr hwnd, uint cmd);
    [DllImport("user32.dll")] private static extern IntPtr GetShellWindow();
    [DllImport("user32.dll")] private static extern bool IsWindowVisible(IntPtr hwnd);
    [DllImport("user32.dll")] private static extern bool IsIconic(IntPtr hwnd);
    [DllImport("dwmapi.dll")] private static extern int DwmGetWindowAttribute(IntPtr hwnd, int attr, out int value, int size);
    [DllImport("user32.dll")] private static extern bool RedrawWindow(IntPtr hwnd, IntPtr rect, IntPtr rgn, uint flags);
}
