using System.Buffers.Binary;
using System.Diagnostics;
using System.Runtime.InteropServices;
using System.Threading;

namespace Glissando.Host;

/// <summary>
/// Controlling side. Low-level hooks watch for this PC's own cursor reaching an edge that leads to the
/// other PC; while "remote", every mouse and keyboard event is swallowed and forwarded. Mouse deltas come
/// from Raw Input (true device counts, unaffected by the hook swallowing the moves).
/// Must be created and used on a thread that pumps messages.
/// </summary>
public sealed class InputCapture : IDisposable
{
    public event Action<Frame>? Output;
    public event Action? EnteredRemote;
    public event Action? LeftRemote;

    /// <summary>Ctrl+Alt+L was pressed: the user wants to toggle "keep the cursor on this PC".</summary>
    public event Action? LockToggleRequested;

    /// <summary>Ctrl+Alt+J was pressed while local: the user wants to jump to the other PC without crossing an edge.</summary>
    public event Action? JumpRequested;

    /// <summary>The user touched this PC's own mouse/keyboard while it was being controlled: take control back.</summary>
    public event Action? LocalInputWhileControlled;

    /// <summary>True while the other PC is controlling this one (set by the session from the injector state).</summary>
    public volatile bool BeingControlled;

    private long _takeBackAllowedAt;
    /// <summary>Ignore take-back briefly (e.g. right after control starts, while the cursor is being placed).</summary>
    public void SuppressTakeBack(int ms) => _takeBackAllowedAt = Stopwatch.GetTimestamp() + Stopwatch.Frequency * ms / 1000;
    private bool TakeBackArmed => Stopwatch.GetTimestamp() >= _takeBackAllowedAt;

    /// <summary>True while input is being forwarded to the other PC.</summary>
    public volatile bool Remote;

    /// <summary>Asked before crossing over; false keeps the cursor local.</summary>
    public Func<bool>? CanEnter { get; set; }

    /// <summary>Both PCs' monitors and where the other PC sits. Replaced whole when settings or screens change.</summary>
    public Layout Layout { get; set; }

    private readonly IntPtr _rawInputWindow;
    private Native.HookProc? _mouseProc, _keyProc;
    private IntPtr _mouseHook, _keyHook;
    private Rectangle _screen;
    private long _ignoreEdgeUntil;
    private readonly HashSet<(ushort vk, ushort scan, bool ext)> _keysDown = [];
    private readonly HashSet<byte> _buttonsDown = [];
    private readonly byte[] _rawBuf = new byte[128];

    // A named event that exists only while this PC is actively controlling the other one. It is a passive
    // signal any sibling tool may read; Glissando does not depend on anyone reading it. Sostenuto uses it to
    // suspend its CapsLock stuck-key probe while remote: that probe travels the hook chain and is swallowed
    // and forwarded here, so without the signal Sostenuto reads it as a blocker and drops its modifier
    // mid-hold. Session-local name (no "Global\"), so it needs no privilege and is scoped to this login.
    private const string RemoteSignalName = "Glissando.ControllingRemote";
    private EventWaitHandle? _remoteSignal;

    // Enter/leave may be called from the hook (UI thread), the receive thread and the timer thread.
    // Without this the hook could re-enter remote mode in the middle of a leave and win the race.
    private readonly object _modeGate = new();

    public InputCapture(Layout layout, IntPtr rawInputWindow)
    {
        Layout = layout;
        _rawInputWindow = rawInputWindow;
        _screen = Layout.MyVirtualScreen();
    }

    public void Install()
    {
        _mouseProc = MouseHook;
        _keyProc = KeyHook;
        var mod = Native.GetModuleHandle(null);
        _mouseHook = Native.SetWindowsHookEx(Native.WH_MOUSE_LL, _mouseProc, mod, 0);
        if (_mouseHook == IntPtr.Zero) throw new InvalidOperationException("WH_MOUSE_LL failed: " + Marshal.GetLastWin32Error());
        _keyHook = Native.SetWindowsHookEx(Native.WH_KEYBOARD_LL, _keyProc, mod, 0);
        if (_keyHook == IntPtr.Zero) throw new InvalidOperationException("WH_KEYBOARD_LL failed: " + Marshal.GetLastWin32Error());

        var rid = new[] { new Native.RAWINPUTDEVICE { usUsagePage = 1, usUsage = 2, dwFlags = Native.RIDEV_INPUTSINK, hwndTarget = _rawInputWindow } };
        if (!Native.RegisterRawInputDevices(rid, 1, (uint)Marshal.SizeOf<Native.RAWINPUTDEVICE>()))
            throw new InvalidOperationException("RegisterRawInputDevices failed: " + Marshal.GetLastWin32Error());
        Log.Info($"input capture installed: {Layout.Mine.Length} monitors, peer origin {Layout.PeerOrigin}, virtual screen {_screen}");
    }

    public void ScreenChanged() => _screen = Layout.MyVirtualScreen();


    /// <summary>Stop forwarding and hand control back locally (peer lost, user escape, exit).</summary>
    public void Release() => LeaveRemote(null);

    /// <summary>The other PC reports its cursor crossed back toward us.</summary>
    public void OnReturnEdge(Crossing c) => LeaveRemote(c);

    private Point ToLocal(Native.POINT p) => new(p.X - _screen.X, p.Y - _screen.Y);
    private Point ToScreen(Point p) => new(p.X + _screen.X, p.Y + _screen.Y);

    // ---- hooks ----------------------------------------------------------------

    private IntPtr MouseHook(int nCode, IntPtr wParam, IntPtr lParam)
    {
        if (nCode < 0) return Native.CallNextHookEx(_mouseHook, nCode, wParam, lParam);
        int msg = (int)wParam;
        var info = Marshal.PtrToStructure<Native.MSLLHOOKSTRUCT>(lParam);

        // Crossing is detected from raw input (OnRawInput), which keeps firing when Windows has
        // clamped the cursor against the edge and the hook has stopped reporting new positions.
        if (!Remote)
        {
            // While this PC is being controlled by the other one, its own physical mouse (the injected
            // moves carry LLMHF_INJECTED, real ones do not) means the user has grabbed the local mouse:
            // hand control back so they can use, and re-share, this machine.
            if (BeingControlled && TakeBackArmed && (info.flags & Native.LLMHF_INJECTED) == 0) LocalInputWhileControlled?.Invoke();
            return Native.CallNextHookEx(_mouseHook, nCode, wParam, lParam);
        }

        switch (msg)
        {
            case Native.WM_MOUSEMOVE:
                break; // deltas arrive via WM_INPUT
            case Native.WM_LBUTTONDOWN: Button(1, true); break;
            case Native.WM_LBUTTONUP: Button(1, false); break;
            case Native.WM_RBUTTONDOWN: Button(2, true); break;
            case Native.WM_RBUTTONUP: Button(2, false); break;
            case Native.WM_MBUTTONDOWN: Button(3, true); break;
            case Native.WM_MBUTTONUP: Button(3, false); break;
            case Native.WM_XBUTTONDOWN: Button((byte)(3 + (info.mouseData >> 16)), true); break;
            case Native.WM_XBUTTONUP: Button((byte)(3 + (info.mouseData >> 16)), false); break;
            // Wheel is swallowed here (return 1 below) but forwarded from Raw Input in OnRawInput, which also
            // catches a precision touchpad's scroll that never reaches this hook.
            case Native.WM_MOUSEWHEEL:
            case Native.WM_MOUSEHWHEEL:
                break;
        }
        return 1;
    }

    private void Button(byte button, bool down)
    {
        if (down) _buttonsDown.Add(button); else _buttonsDown.Remove(button);
        Output?.Invoke(Frame.MouseButton(button, down));
    }

    private IntPtr KeyHook(int nCode, IntPtr wParam, IntPtr lParam)
    {
        if (nCode < 0) return Native.CallNextHookEx(_keyHook, nCode, wParam, lParam);
        var info = Marshal.PtrToStructure<Native.KBDLLHOOKSTRUCT>(lParam);
        bool up = (info.flags & Native.LLKHF_UP) != 0;
        bool ext = (info.flags & Native.LLKHF_EXTENDED) != 0;

        if (!Remote)
        {
            // Local mode: only the lock hotkey is ours. Modifier state is real here (nothing is swallowed).
            if (!up && info.vkCode == Native.VK_L && Native.IsDown(Native.VK_CONTROL) && Native.IsDown(Native.VK_MENU))
            {
                LockToggleRequested?.Invoke();
                return 1;
            }
            if (!up && info.vkCode == Native.VK_J && Native.IsDown(Native.VK_CONTROL) && Native.IsDown(Native.VK_MENU))
            {
                JumpRequested?.Invoke();
                return 1;
            }
            // A real local keystroke while being controlled means the user wants this machine back.
            if (BeingControlled && TakeBackArmed && !up && (info.flags & Native.LLKHF_INJECTED) == 0) LocalInputWhileControlled?.Invoke();
            return Native.CallNextHookEx(_keyHook, nCode, wParam, lParam);
        }

        var key = ((ushort)info.vkCode, (ushort)info.scanCode, ext);

        if (!up && info.vkCode == Native.VK_ESCAPE && CtrlDown() && AltDown())
        {
            Log.Info("escape hotkey: leaving remote");
            LeaveRemote(null);
            return 1;
        }
        if (!up && info.vkCode == Native.VK_L && CtrlDown() && AltDown())
        {
            Log.Info("lock hotkey while remote: leaving remote and locking");
            LeaveRemote(null);
            LockToggleRequested?.Invoke();
            return 1;
        }
        if (!up && info.vkCode == Native.VK_J && CtrlDown() && AltDown())
        {
            Log.Info("jump hotkey while remote: coming back");
            LeaveRemote(null);
            return 1;
        }

        if (up) _keysDown.Remove(key); else _keysDown.Add(key);
        // Forward auto-repeat too. For a held key it re-asserts the down ~30x/s, which is what keeps a held
        // modifier (Shift while arrowing, Ctrl while typing) alive across a dropped packet on the lossy link,
        // and for an ordinary key it produces hold-to-repeat on the other PC.
        if (KeyLog) Log.Info($"fwd key vk=0x{info.vkCode:X2} {(up ? "up  " : "down")} ext={ext} scan=0x{info.scanCode:X2}");
        Output?.Invoke(Frame.Key(key.Item1, key.Item2, up, ext));
        return 1;
    }

    /// <summary>Log every key forwarded and applied, to diagnose lost keystrokes. Enable with --keylog.</summary>
    public static bool KeyLog;

    private bool CtrlDown() => _keysDown.Any(k => k.vk is Native.VK_LCONTROL or Native.VK_RCONTROL);
    private bool AltDown() => _keysDown.Any(k => k.vk is Native.VK_LMENU or Native.VK_RMENU);

    // ---- raw input --------------------------------------------------------------

    /// <summary>Call from the window procedure for WM_INPUT.</summary>
    public void OnRawInput(IntPtr hRawInput)
    {
        uint size = (uint)_rawBuf.Length;
        uint got = Native.GetRawInputData(hRawInput, Native.RID_INPUT, _rawBuf, ref size, Native.RawHeaderSize);
        if (got == uint.MaxValue || got < Native.RawHeaderSize + 20) return;
        uint type = BinaryPrimitives.ReadUInt32LittleEndian(_rawBuf);
        if (type != Native.RIM_TYPEMOUSE) return;
        // Note: no filtering on hDevice. Some mice and touchpads report no device handle, and the loop this
        // once guarded against cannot happen: while we are remote the other PC is never injecting into us.
        int m = Native.RawHeaderSize;
        ushort flags = BinaryPrimitives.ReadUInt16LittleEndian(_rawBuf.AsSpan(m));
        if ((flags & Native.MOUSE_MOVE_ABSOLUTE) != 0) return; // tablets/RDP: not supported yet

        // Wheel comes from Raw Input, not the low-level hook: a precision touchpad's two-finger scroll bypasses
        // WH_MOUSE_LL entirely (it would otherwise scroll the parked local cursor and never reach the peer).
        // Raw Input sees the device directly. The hook still swallows a regular mouse's wheel but no longer
        // forwards it, so the two sources never double. Wheel reports carry no movement, so handle it first.
        ushort buttonFlags = BinaryPrimitives.ReadUInt16LittleEndian(_rawBuf.AsSpan(m + 4));
        if ((buttonFlags & (Native.RI_MOUSE_WHEEL | Native.RI_MOUSE_HWHEEL)) != 0 && Remote)
        {
            short delta = unchecked((short)BinaryPrimitives.ReadUInt16LittleEndian(_rawBuf.AsSpan(m + 6)));
            Output?.Invoke(Frame.MouseWheel(delta, (buttonFlags & Native.RI_MOUSE_HWHEEL) != 0));
        }

        int dx = BinaryPrimitives.ReadInt32LittleEndian(_rawBuf.AsSpan(m + 12));
        int dy = BinaryPrimitives.ReadInt32LittleEndian(_rawBuf.AsSpan(m + 16));
        if (dx == 0 && dy == 0) return;
        if (!Remote) { TryCross(dx, dy); return; }
        Interlocked.Increment(ref MovesForwarded);
        Output?.Invoke(Frame.MouseMove(dx, dy));
    }

    /// <summary>
    /// A raw mouse delta arrived while local. If the cursor now sits on an edge that leads to the other
    /// PC and the motion points into it, cross. Reading the position here (not in the LL hook) means the
    /// push is seen even while the cursor is pinned to the edge, so crossing is not timing-sensitive.
    /// </summary>
    private void TryCross(int dx, int dy)
    {
        if (Remote || Stopwatch.GetTimestamp() < _ignoreEdgeUntil) return;
        if (!(CanEnter?.Invoke() ?? false)) return;
        if (!Native.GetCursorPos(out var cur)) return;
        if (Layout.FindCrossing(ToLocal(cur), dx, dy) is Crossing c) EnterRemote(c);
    }

    /// <summary>Mouse-move frames produced while remote (diagnostics).</summary>
    public long MovesForwarded;

    // ---- crossing ---------------------------------------------------------------

    /// <summary>Start controlling the other PC at the given point of its screen, without an edge crossing.</summary>
    public void EnterAt(Crossing c)
    {
        if (Remote || !(CanEnter?.Invoke() ?? false)) return;
        EnterRemote(c);
    }

    private void EnterRemote(Crossing c)
    {
        lock (_modeGate)
        {
            if (Remote || Stopwatch.GetTimestamp() < _ignoreEdgeUntil) return;
            Remote = true;
            RaiseRemoteSignal();
            _keysDown.Clear();
            _buttonsDown.Clear();
            Log.Info($"entering remote: {c.Dir} -> peer {c.Target}");
            Output?.Invoke(Frame.Enter(c));
        }
        EnteredRemote?.Invoke();
    }

    private void LeaveRemote(Crossing? back)
    {
        lock (_modeGate)
        {
            if (!Remote) return;
            // Close the door before anything else: a cursor still parked on the edge must not re-enter.
            _ignoreEdgeUntil = Stopwatch.GetTimestamp() + Stopwatch.Frequency / 4;
            Remote = false;
            ClearRemoteSignal();
            LeaveRemoteCore(back);
        }
        Log.Info("left remote");
        LeftRemote?.Invoke();
    }

    private void LeaveRemoteCore(Crossing? back)
    {
        foreach (var k in _keysDown.ToArray()) Output?.Invoke(Frame.Key(k.vk, k.scan, true, k.ext));
        foreach (var b in _buttonsDown.ToArray()) Output?.Invoke(Frame.MouseButton(b, false));
        _keysDown.Clear();
        _buttonsDown.Clear();
        Output?.Invoke(Frame.Leave());

        // Land a few pixels inside the edge so the cursor does not cross straight back.
        const int inset = 3;
        Point target;
        if (back is Crossing c)
        {
            target = Layout.ClampIntoMonitor(c.Target, Layout.Add(c.Target, Layout.Unit(c.Dir), inset));
        }
        else
        {
            Native.GetCursorPos(out var cur);
            target = ToLocal(cur);
            if (Layout.FindCrossing(target, 0, 0) is Crossing edge)
                target = Layout.ClampIntoMonitor(target, Layout.Add(target, Layout.Unit(edge.Dir), -inset));
        }
        var s = ToScreen(target);
        Native.SetCursorPos(s.X, s.Y);
        _ignoreEdgeUntil = Stopwatch.GetTimestamp() + Stopwatch.Frequency / 4;
    }

    // Held (created) only while remote, so a reader sees the name resolve exactly while we control the peer.
    // Best-effort: a failure here must never break control, so the signal is simply absent if it cannot form.
    private void RaiseRemoteSignal()
    {
        try { _remoteSignal ??= new EventWaitHandle(false, EventResetMode.ManualReset, RemoteSignalName); }
        catch { _remoteSignal = null; }
    }

    private void ClearRemoteSignal()
    {
        try { _remoteSignal?.Dispose(); } catch { }
        _remoteSignal = null;
    }

    public void Dispose()
    {
        LeaveRemote(null);
        ClearRemoteSignal();
        if (_mouseHook != IntPtr.Zero) Native.UnhookWindowsHookEx(_mouseHook);
        if (_keyHook != IntPtr.Zero) Native.UnhookWindowsHookEx(_keyHook);
        _mouseHook = _keyHook = IntPtr.Zero;
    }
}
