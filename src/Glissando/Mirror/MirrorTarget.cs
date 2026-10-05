using System.Diagnostics;
using System.Runtime.InteropServices;

namespace Glissando.Mirror;

/// <summary>
/// Source side of an interactive mirror: applies the viewer's pointer, clicks, wheel and keys to the mirrored
/// window. Everything goes through SendInput, the one path every application honours -- posting WM_* messages
/// to a background window loses the modifier state and is ignored by half the programs out there -- which
/// means the window must be on screen and in front when the input lands. So it is restored and brought forward
/// before a click or key (see Raise: Windows will not hand a background process the foreground for the asking,
/// so it attaches to the foreground thread's input queue first). The source screen shows the window while that
/// happens; nobody is sitting at it, the user is at the viewer. Pointer moves alone never raise the window,
/// they only place the cursor, so hover works once it is up; while the window is hidden here the cursor is
/// hidden with it.
/// </summary>
internal sealed class MirrorTarget
{
    private readonly IntPtr _hwnd;
    private readonly HashSet<(ushort vk, ushort scan, bool ext)> _keysDown = [];
    private readonly HashSet<byte> _buttonsDown = [];
    private readonly object _gate = new();
    private long _lastInputLog;
    private int _inputsApplied;

    public MirrorTarget(IntPtr hwnd) => _hwnd = hwnd;

    /// <summary>The picture the viewer sees is the window's VISIBLE frame (GPU capture), not GetWindowRect with
    /// its invisible borders, so a mirror pixel maps from the visible bounds' corner.</summary>
    public volatile bool UseVisibleBounds;

    public void Apply(Frame f)
    {
        lock (_gate)
        {
            switch (f.Type)
            {
                case FrameType.MirrorPointer:
                    Place(f.U16(0), f.U16(2), raise: false);
                    break;
                case FrameType.MirrorMouse:
                    if (f.Payload.Length >= 8) Mouse(f.Payload[0], f.U16(1), f.U16(3), f.Payload[5], f.I16(6));
                    break;
                case FrameType.MirrorKey:
                    if (f.Payload.Length >= 5) Key(f.U16(0), f.U16(2), (f.Payload[4] & 1) != 0, (f.Payload[4] & 2) != 0);
                    break;
            }
            // Evidence in the log that the viewer's input reaches this side and where it lands: a line every
            // few seconds while it flows, not one per event.
            if (f.Type != FrameType.MirrorPointer)
            {
                _inputsApplied++;
                long now = Stopwatch.GetTimestamp();
                if (now - _lastInputLog > Stopwatch.Frequency * 4)
                {
                    _lastInputLog = now;
                    Log.Info($"mirror input: {_inputsApplied} click/key events applied; foreground now \"{WindowCapture.Title(GetForegroundWindow())}\"");
                    _inputsApplied = 0;
                }
            }
        }
    }

    /// <summary>Put the cursor on a source-window pixel (the capture is the window rectangle, so the offset is
    /// from its top-left corner); false if the window is gone.</summary>
    private bool Place(int x, int y, bool raise)
    {
        if (!IsWindow(_hwnd)) return false;
        if (raise) { WindowHider.SetClickThrough(false); Raise(); }
        // The cursor is moved here for hover to work in the mirror, but over a window hidden on this PC it is
        // a pointer wandering across nothing (2026-10-05): hide it too, as if it had moved over with the window.
        // It comes back when the window shows again (WindowHider) or this PC's own mouse moves (Session).
        if (WindowHider.IsHidden(_hwnd)) Host.CursorHider.Hide(Host.CursorHider.Mirror);
        if (UseVisibleBounds)
        {
            var vb = WindowCapture.VisibleBounds(_hwnd);
            if (vb.IsEmpty) return false;
            Native.MoveCursorAbsolute(vb.Left + x, vb.Top + y);
            return true;
        }
        if (!GetWindowRect(_hwnd, out var r)) return false;
        Native.MoveCursorAbsolute(r.Left + x, r.Top + y);
        return true;
    }

    private void Raise()
    {
        if (IsIconic(_hwnd)) ShowWindow(_hwnd, SW_RESTORE);
        IntPtr fg = GetForegroundWindow();
        if (fg == _hwnd) return;
        // Windows refuses a background process the foreground: SetForegroundWindow from here merely flashed
        // the taskbar, the window stayed buried behind whatever was in front on this PC, and the viewer's
        // clicks landed on THAT window while its picture never changed -- the "frozen mirror" (2026-09-12).
        // The permitted way round: attach to the input queue of the thread that currently owns the
        // foreground, which lets the switch through, and drop the foreground-lock timeout for the moment.
        // Nobody sits at this PC while it is being driven through the mirror, so stealing focus is the
        // intent, not a side effect.
        // The attachment that counts is OUR calling thread's: joined to the queue that owns the foreground,
        // our SetForegroundWindow is treated as coming from the foreground itself. (Attaching the foreground
        // thread to the target's, two foreign queues, was the first attempt and failed on every click.)
        uint me = GetCurrentThreadId();
        uint fgThread = fg != IntPtr.Zero ? GetWindowThreadProcessId(fg, out _) : 0;
        uint target = GetWindowThreadProcessId(_hwnd, out _);
        SystemParametersInfo(SPI_SETFOREGROUNDLOCKTIMEOUT, 0, IntPtr.Zero, 0);
        bool toFg = fgThread != 0 && fgThread != me && AttachThreadInput(me, fgThread, true);
        bool toTarget = target != 0 && target != me && target != fgThread && AttachThreadInput(me, target, true);
        try
        {
            BringWindowToTop(_hwnd);
            SetForegroundWindow(_hwnd);
        }
        finally
        {
            if (toTarget) AttachThreadInput(me, target, false);
            if (toFg) AttachThreadInput(me, fgThread, false);
        }
        if (GetForegroundWindow() != _hwnd)
            Log.Warn($"mirror: could not bring the source window to the front (foreground stays \"{WindowCapture.Title(GetForegroundWindow())}\")");
    }

    private void Mouse(byte kind, int x, int y, byte button, int delta)
    {
        if (!Place(x, y, raise: kind is 1 or 2)) return;
        switch (kind)
        {
            case 1 or 2:
            {
                bool down = kind == 1;
                if (!ButtonFlag(button, down, out uint flag, out uint data)) return;
                if (down) _buttonsDown.Add(button); else _buttonsDown.Remove(button);
                Native.Send(Native.Mouse(flag, data: data));
                break;
            }
            case 3: Native.Send(Native.Mouse(Native.MOUSEEVENTF_WHEEL, data: unchecked((uint)delta))); break;
            case 4: Native.Send(Native.Mouse(Native.MOUSEEVENTF_HWHEEL, data: unchecked((uint)delta))); break;
        }
    }

    private static bool ButtonFlag(byte button, bool down, out uint flag, out uint data)
    {
        data = 0;
        switch (button)
        {
            case 1: flag = down ? Native.MOUSEEVENTF_LEFTDOWN : Native.MOUSEEVENTF_LEFTUP; return true;
            case 2: flag = down ? Native.MOUSEEVENTF_RIGHTDOWN : Native.MOUSEEVENTF_RIGHTUP; return true;
            case 3: flag = down ? Native.MOUSEEVENTF_MIDDLEDOWN : Native.MOUSEEVENTF_MIDDLEUP; return true;
            case 4: flag = down ? Native.MOUSEEVENTF_XDOWN : Native.MOUSEEVENTF_XUP; data = 1; return true;
            case 5: flag = down ? Native.MOUSEEVENTF_XDOWN : Native.MOUSEEVENTF_XUP; data = 2; return true;
            default: flag = 0; return false;
        }
    }

    private void Key(ushort vk, ushort scan, bool up, bool ext)
    {
        if (!IsWindow(_hwnd)) return;
        if (!up) { WindowHider.SetClickThrough(false); Raise(); }
        var key = (vk, scan, ext);
        if (up) _keysDown.Remove(key); else _keysDown.Add(key);
        // The injector's rule: an injected held modifier sticks only to the very next key, so re-assert every
        // held modifier before each non-modifier key-down.
        if (!up && vk != 0 && !Native.IsModifierVk(vk))
            foreach (var m in _keysDown)
                if (m.vk != 0 && Native.IsModifierVk(m.vk))
                    Client.InputInjector.SendKey(m.vk, m.scan, up: false, m.ext);
        Client.InputInjector.SendKey(vk, scan, up, ext);
    }

    /// <summary>Release whatever the viewer still holds, so nothing sticks when the mirror stops.</summary>
    public void ReleaseAll()
    {
        lock (_gate)
        {
            foreach (var k in _keysDown.ToArray()) Client.InputInjector.SendKey(k.vk, k.scan, up: true, k.ext);
            _keysDown.Clear();
            foreach (var b in _buttonsDown.ToArray())
                if (ButtonFlag(b, false, out uint flag, out uint data)) Native.Send(Native.Mouse(flag, data: data));
            _buttonsDown.Clear();
        }
    }

    private const int SW_RESTORE = 9;
    [StructLayout(LayoutKind.Sequential)] private struct RECT { public int Left, Top, Right, Bottom; }
    [DllImport("user32.dll")] private static extern bool IsWindow(IntPtr hwnd);
    [DllImport("user32.dll")] private static extern bool IsIconic(IntPtr hwnd);
    [DllImport("user32.dll")] private static extern bool ShowWindow(IntPtr hwnd, int cmd);
    [DllImport("user32.dll")] private static extern bool GetWindowRect(IntPtr hwnd, out RECT r);
    [DllImport("user32.dll")] private static extern IntPtr GetForegroundWindow();
    [DllImport("user32.dll")] private static extern bool SetForegroundWindow(IntPtr hwnd);
    [DllImport("user32.dll")] private static extern bool BringWindowToTop(IntPtr hwnd);
    [DllImport("user32.dll")] private static extern bool AttachThreadInput(uint idAttach, uint idAttachTo, bool attach);
    [DllImport("user32.dll")] private static extern uint GetWindowThreadProcessId(IntPtr hwnd, out uint pid);
    [DllImport("kernel32.dll")] private static extern uint GetCurrentThreadId();
    [DllImport("user32.dll")] private static extern bool SystemParametersInfo(uint action, uint param, IntPtr vparam, uint winIni);
    private const uint SPI_SETFOREGROUNDLOCKTIMEOUT = 0x2001;
}
