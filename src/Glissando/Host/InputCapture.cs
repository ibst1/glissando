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

    /// <summary>A physical (not injected) click or key-down on this PC while it is not controlling the other one.
    /// Cheap to raise; used to show a hidden mirrored window again, since someone is evidently sitting here.</summary>
    public event Action<Point?>? LocalActivity;   // the click's screen point; null for a key

    /// <summary>True while the other PC is controlling this one (set by the session from the injector state).</summary>
    public volatile bool BeingControlled;

    /// <summary>The setting "keyboard follows the cursor" (mirrors Settings.KeyboardFollowsCursor). With it, a key
    /// typed while being controlled no longer takes this PC back: the other PC's cursor is here, so typing here
    /// is the point. Its mouse still takes the machine back.</summary>
    public volatile bool KeyboardFollowsCursor;

    private volatile bool _forwardKeys;
    private readonly HashSet<(ushort vk, ushort scan, bool ext)> _typedKeysDown = [];

    /// <summary>"Keyboard follows the cursor", the active half: while true and not remote, every key typed on this
    /// PC is swallowed and sent as a TypedKey, which the other PC applies without a control session. The session
    /// keeps this in step with the link and with who controls whom (off while the other PC's cursor is on this
    /// PC, so its keyboard then types here). The lock/jump hotkeys stay local. Turning it off releases on the
    /// other PC whatever is still held, so a Shift held across the switch cannot stick there.</summary>
    public bool ForwardKeys
    {
        get => _forwardKeys;
        set
        {
            lock (_modeGate)
            {
                if (_forwardKeys == value) return;
                _forwardKeys = value;
                if (value)
                {
                    RaiseRemoteSignal(); // Sostenuto's CapsLock probe would be swallowed and forwarded, as when remote
                    Log.Info("keyboard follows the cursor: keys now go to the other PC");
                }
                else
                {
                    foreach (var k in _typedKeysDown.ToArray()) Output?.Invoke(Frame.TypedKey(k.vk, k.scan, true, k.ext));
                    _typedKeysDown.Clear();
                    if (!Remote) ClearRemoteSignal();
                    Log.Info("keyboard follows the cursor: keys now type on this PC");
                }
            }
        }
    }

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
        if (KeyLog && msg is Native.WM_MOUSEWHEEL or Native.WM_MOUSEHWHEEL)
            Log.Info($"hook wheel msg=0x{msg:X} delta={unchecked((short)(info.mouseData >> 16))} flags=0x{info.flags:X} extra=0x{info.dwExtraInfo.ToInt64():X} remote={Remote}");

        // Crossing is detected from raw input (OnRawInput), which keeps firing when Windows has
        // clamped the cursor against the edge and the hook has stopped reporting new positions.
        if (!Remote)
        {
            // While this PC is being controlled by the other one, its own physical mouse (the injected
            // moves carry LLMHF_INJECTED, real ones do not) means the user has grabbed the local mouse:
            // hand control back so they can use, and re-share, this machine.
            if ((info.flags & Native.LLMHF_INJECTED) == 0)
            {
                if (BeingControlled && TakeBackArmed) LocalInputWhileControlled?.Invoke();
                if (msg is Native.WM_LBUTTONDOWN or Native.WM_RBUTTONDOWN or Native.WM_MBUTTONDOWN or Native.WM_XBUTTONDOWN)
                    LocalActivity?.Invoke(new Point(info.pt.X, info.pt.Y));
            }
            return Native.CallNextHookEx(_mouseHook, nCode, wParam, lParam);
        }

        // Our own injections while remote -- the mirror driving a window on THIS PC while it controls the
        // other one -- are for this PC: pass them through, neither swallowed nor forwarded. Without this the
        // mirror's clicks were eaten here and sent to the peer as clicks (2026-09-12). Only OUR tag is exempt:
        // a key-remapper's injected keys carry its own tag, and forwarding those is how its remaps reach the peer.
        if (info.dwExtraInfo == Native.InjectTag) return Native.CallNextHookEx(_mouseHook, nCode, wParam, lParam);

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
            // Wheel is swallowed here (return 1 below) but forwarded from Raw Input in OnRawInput. A precision
            // touchpad's scroll gets here only thanks to the ScrollCatcher window under the frozen cursor.
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
            // Local mode: only the lock/jump hotkeys are ours. LEFT Ctrl specifically, so a key-remapper that
            // turns CapsLock into RIGHT Ctrl (Sostenuto does) cannot trip these -- otherwise its CapsLock+Alt+J
            // reads as Ctrl+Alt+J and jumps. Modifier state is real here (nothing is swallowed).
            if (!up && info.vkCode == Native.VK_L && Native.IsDown(Native.VK_LCONTROL) && Native.IsDown(Native.VK_MENU))
            {
                LockToggleRequested?.Invoke();
                return 1;
            }
            if (!up && info.vkCode == Native.VK_J && Native.IsDown(Native.VK_LCONTROL) && Native.IsDown(Native.VK_MENU))
            {
                JumpRequested?.Invoke();
                return 1;
            }
            if (!up && (info.flags & Native.LLKHF_INJECTED) == 0)
            {
                // A real local keystroke while being controlled means the user wants this machine back -- unless
                // the keyboard follows the cursor: then typing while the other PC's cursor is here is the point.
                if (BeingControlled && TakeBackArmed && !KeyboardFollowsCursor) LocalInputWhileControlled?.Invoke();
                LocalActivity?.Invoke(null);
            }
            // Keyboard follows the cursor, and the cursor is on the other PC: this key is for it. Our own
            // injections (InjectTag) are for this PC, as in remote mode; a remapper's injections are forwarded.
            if (_forwardKeys && info.dwExtraInfo != Native.InjectTag)
            {
                var tk = ((ushort)info.vkCode, (ushort)info.scanCode, ext);
                lock (_modeGate)
                {
                    if (!_forwardKeys) return Native.CallNextHookEx(_keyHook, nCode, wParam, lParam);
                    if (up) _typedKeysDown.Remove(tk); else _typedKeysDown.Add(tk);
                }
                if (KeyLog) Log.Info($"typed key vk=0x{info.vkCode:X2} {(up ? "up  " : "down")} ext={ext} scan=0x{info.scanCode:X2}");
                Output?.Invoke(Frame.TypedKey(tk.Item1, tk.Item2, up, ext));
                return 1;
            }
            return Native.CallNextHookEx(_keyHook, nCode, wParam, lParam);
        }

        // Our own injections are for this PC (see MouseHook): keys typed into the mirror went to the peer instead.
        if (info.dwExtraInfo == Native.InjectTag) return Native.CallNextHookEx(_keyHook, nCode, wParam, lParam);

        var key = ((ushort)info.vkCode, (ushort)info.scanCode, ext);

        if (!up && info.vkCode == Native.VK_ESCAPE && LCtrlDown() && AltDown())
        {
            Log.Info("escape hotkey: leaving remote");
            LeaveRemote(null);
            return 1;
        }
        if (!up && info.vkCode == Native.VK_L && LCtrlDown() && AltDown())
        {
            Log.Info("lock hotkey while remote: leaving remote and locking");
            LeaveRemote(null);
            LockToggleRequested?.Invoke();
            return 1;
        }
        if (!up && info.vkCode == Native.VK_J && LCtrlDown() && AltDown())
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

    /// <summary>A wheel message that reached the ScrollCatcher window itself (normally the hook swallows the
    /// input first and Raw Input forwards it; this is the fallback).</summary>
    public void ForwardWheel(int delta, bool horizontal)
    {
        if (!Remote) return;
        if (KeyLog) Log.Info($"catcher wheel delta={delta} horizontal={horizontal}");
        Output?.Invoke(Frame.MouseWheel(delta, horizontal));
    }

    /// <summary>Log every key forwarded and applied, to diagnose lost keystrokes. Enable with --keylog.</summary>
    public static bool KeyLog;

    // LEFT Ctrl only, so a CapsLock-to-right-Ctrl remapper's chords do not fire our escape/lock/jump hotkeys.
    private bool LCtrlDown() => _keysDown.Any(k => k.vk == Native.VK_LCONTROL);
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

        // A mouse's wheel is forwarded from Raw Input; the hook swallows it (so it does not scroll a local
        // window) but does not forward it, so the two never double. A precision touchpad's two-finger scroll
        // only becomes wheel input when a plain Win32 window lies under the cursor - ScrollCatcher parks one
        // there while remote - and then arrives here too. Wheel reports carry no movement, so handle it first.
        // Raw Input carries SendInput's dwExtraInfo as ulExtraInformation: our own injected wheel and moves
        // (the mirror scrolling a window on this PC) must not be forwarded to the peer on top.
        bool ours = got >= (uint)(m + 24) && BinaryPrimitives.ReadUInt32LittleEndian(_rawBuf.AsSpan(m + 20)) == (uint)Native.InjectTag.ToInt64();
        ushort buttonFlags = BinaryPrimitives.ReadUInt16LittleEndian(_rawBuf.AsSpan(m + 4));
        if (KeyLog && (buttonFlags & (Native.RI_MOUSE_WHEEL | Native.RI_MOUSE_HWHEEL)) != 0)
            Log.Info($"raw wheel buttonFlags=0x{buttonFlags:X} delta={unchecked((short)BinaryPrimitives.ReadUInt16LittleEndian(_rawBuf.AsSpan(m + 6)))} ours={ours} remote={Remote} hDevice=0x{BinaryPrimitives.ReadUInt64LittleEndian(_rawBuf.AsSpan(8)):X}");
        if ((buttonFlags & (Native.RI_MOUSE_WHEEL | Native.RI_MOUSE_HWHEEL)) != 0 && Remote && !ours)
        {
            short delta = unchecked((short)BinaryPrimitives.ReadUInt16LittleEndian(_rawBuf.AsSpan(m + 6)));
            Output?.Invoke(Frame.MouseWheel(delta, (buttonFlags & Native.RI_MOUSE_HWHEEL) != 0));
        }

        int dx = BinaryPrimitives.ReadInt32LittleEndian(_rawBuf.AsSpan(m + 12));
        int dy = BinaryPrimitives.ReadInt32LittleEndian(_rawBuf.AsSpan(m + 16));
        if (dx == 0 && dy == 0) return;
        if (!Remote) { TryCross(dx, dy); return; }
        if (ours) return; // the mirror's own move on this PC, not the user's hand on the mouse
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
            if (!_forwardKeys) ClearRemoteSignal(); // the keyboard may still be going to the other PC
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
