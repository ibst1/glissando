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

    /// <summary>Stopwatch time of the last physical (not injected) mouse event on this PC. The keyboard follows
    /// whichever mouse was used last: this PC's own, and the keys stay here; the other PC's (seen as its cursor
    /// coming and going), and they go there.</summary>
    public long LastLocalMouseTicks;

    /// <summary>This PC's own mouse moved while its keys were going to the other PC: time to bring them home.</summary>
    public event Action? LocalMouseWhileForwarding;

    private volatile bool _forwardKeys;
    private readonly HashSet<(ushort vk, ushort scan, bool ext)> _typedKeysDown = [];

    // Keys whose DOWN was let through to this PC. Their UP must go the same way whatever mode we are in by then:
    // a Shift pressed while the other PC's cursor was here, and released after it had left, had its release
    // swallowed and forwarded -- Windows here never saw it, and Shift stayed down (2026-09-23). Same for a key
    // held while crossing over with the trackpad.
    private readonly HashSet<(ushort vk, ushort scan, bool ext)> _localKeysDown = [];

    // Mouse buttons whose DOWN was let through to this PC, by the same rule: a button held while the cursor
    // crossed over (a drag pushed against the edge) had its release swallowed and forwarded, so Windows here
    // kept it down -- the cursor still moved and the wheel worked, but no click registered until
    // Ctrl+Alt+Del (2026-10-02).
    private readonly HashSet<byte> _localButtonsDown = [];

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
    private Thread? _hookThread;
    private uint _hookThreadId;

    [StructLayout(LayoutKind.Sequential)]
    private struct MSG { public IntPtr hwnd; public uint message; public IntPtr wParam, lParam; public uint time; public int ptX, ptY; }
    [DllImport("user32.dll")] private static extern int GetMessage(out MSG msg, IntPtr hwnd, uint min, uint max);
    [DllImport("user32.dll")] private static extern bool TranslateMessage(ref MSG msg);
    [DllImport("user32.dll")] private static extern IntPtr DispatchMessage(ref MSG msg);
    [DllImport("user32.dll")] private static extern bool PostThreadMessage(uint threadId, uint msg, IntPtr wParam, IntPtr lParam);
    [DllImport("kernel32.dll")] private static extern uint GetCurrentThreadId();
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

    /// <summary>
    /// Installs the low-level hooks on a thread of their own and registers Raw Input with the UI window.
    /// The hooks used to live on the UI thread. Every key and mouse event on this PC -- the ones we inject for
    /// the other PC as well -- waits until each low-level hook has answered, and that thread also serves the
    /// tray, menus, mirror windows and clipboard. Whenever it was busy for a moment, SendInput waited out the
    /// 1 s hook timeout and typing driven from the other PC crawled (RS driving LU, 2026-10-06: the UI thread
    /// answered after 0.6-2.8 s, SendInput took 1000 ms). The hook thread does nothing but pump the hooks.
    /// </summary>
    public void Install()
    {
        _mouseProc = MouseHook;
        _keyProc = KeyHook;
        var ready = new ManualResetEventSlim();
        Exception? failed = null;
        _hookThread = new Thread(() =>
        {
            try
            {
                _hookThreadId = GetCurrentThreadId();
                var mod = Native.GetModuleHandle(null);
                _mouseHook = Native.SetWindowsHookEx(Native.WH_MOUSE_LL, _mouseProc, mod, 0);
                if (_mouseHook == IntPtr.Zero) throw new InvalidOperationException("WH_MOUSE_LL failed: " + Marshal.GetLastWin32Error());
                _keyHook = Native.SetWindowsHookEx(Native.WH_KEYBOARD_LL, _keyProc, mod, 0);
                if (_keyHook == IntPtr.Zero) throw new InvalidOperationException("WH_KEYBOARD_LL failed: " + Marshal.GetLastWin32Error());
            }
            catch (Exception ex) { failed = ex; ready.Set(); return; }
            ready.Set();
            // Low-level hooks are called through this thread's message loop; nothing else runs here.
            while (GetMessage(out var msg, IntPtr.Zero, 0, 0) > 0)
            {
                TranslateMessage(ref msg);
                DispatchMessage(ref msg);
            }
        }) { IsBackground = true, Name = "input-hooks", Priority = ThreadPriority.Highest };
        _hookThread.Start();
        ready.Wait();
        if (failed != null) throw failed;

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
                LastLocalMouseTicks = Stopwatch.GetTimestamp();
                if (_forwardKeys) LocalMouseWhileForwarding?.Invoke();
                if (BeingControlled && TakeBackArmed) LocalInputWhileControlled?.Invoke();
                if (msg is Native.WM_LBUTTONDOWN or Native.WM_RBUTTONDOWN or Native.WM_MBUTTONDOWN or Native.WM_XBUTTONDOWN)
                    LocalActivity?.Invoke(new Point(info.pt.X, info.pt.Y));
            }
            if (ButtonOf(msg, info.mouseData) is (byte b, bool down))
                lock (_modeGate) { if (down) _localButtonsDown.Add(b); else _localButtonsDown.Remove(b); }
            return Native.CallNextHookEx(_mouseHook, nCode, wParam, lParam);
        }

        // The release of a button pressed here before crossing belongs here too (see _localButtonsDown).
        if (ButtonOf(msg, info.mouseData) is (byte lb, false))
            lock (_modeGate)
                if (_localButtonsDown.Remove(lb))
                {
                    Log.Info($"button {lb} was pressed before crossing: its release stays on this PC");
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

    /// <summary>Button number (1 left, 2 right, 3 middle, 4/5 X) and press/release of a mouse message, or null.</summary>
    private static (byte button, bool down)? ButtonOf(int msg, uint mouseData) => msg switch
    {
        Native.WM_LBUTTONDOWN => (1, true), Native.WM_LBUTTONUP => (1, false),
        Native.WM_RBUTTONDOWN => (2, true), Native.WM_RBUTTONUP => (2, false),
        Native.WM_MBUTTONDOWN => (3, true), Native.WM_MBUTTONUP => (3, false),
        Native.WM_XBUTTONDOWN => ((byte)(3 + (mouseData >> 16)), true),
        Native.WM_XBUTTONUP => ((byte)(3 + (mouseData >> 16)), false),
        _ => null,
    };

    private void Button(byte button, bool down)
    {
        lock (_modeGate) { if (down) _buttonsDown.Add(button); else _buttonsDown.Remove(button); }
        Output?.Invoke(Frame.MouseButton(button, down));
    }

    private IntPtr KeyHook(int nCode, IntPtr wParam, IntPtr lParam)
    {
        if (nCode < 0) return Native.CallNextHookEx(_keyHook, nCode, wParam, lParam);
        var info = Marshal.PtrToStructure<Native.KBDLLHOOKSTRUCT>(lParam);
        bool up = (info.flags & Native.LLKHF_UP) != 0;
        bool ext = (info.flags & Native.LLKHF_EXTENDED) != 0;
        // The left Ctrl as the KEYBOARD holds it, in every mode: only a physical press counts for our hotkeys.
        if (info.vkCode == Native.VK_LCONTROL && (info.flags & Native.LLKHF_INJECTED) == 0) _physLCtrl = !up;
        if ((info.flags & Native.LLKHF_INJECTED) == 0)
            lock (_physDown) { if (up) _physDown.Remove(info.vkCode); else _physDown[info.vkCode] = Stopwatch.GetTimestamp(); }

        if (!Remote)
        {
            // Local mode: only the lock/jump hotkeys are ours. LEFT Ctrl specifically, physically pressed, and no
            // right Ctrl, so a key-remapper that turns CapsLock into RIGHT Ctrl (Sostenuto does) cannot trip these
            // -- otherwise its CapsLock+Alt+J reads as Ctrl+Alt+J and jumps (see LCtrlDown).
            // While the keys are going to the other PC, a held Ctrl or Alt was swallowed and sent there, so
            // Windows here does not know it is down; ask the typed-keys set too, or Ctrl+Alt+L -- the very way
            // to bring the keyboard home -- can never fire (2026-09-29).
            if (!up && info.vkCode is Native.VK_L or Native.VK_J && LocalHotkeyModifiers())
            {
                if (info.vkCode == Native.VK_L) LockToggleRequested?.Invoke(); else JumpRequested?.Invoke();
                return 1;
            }
            if (!up && (info.flags & Native.LLKHF_INJECTED) == 0)
            {
                // A real local keystroke while being controlled means the user wants this machine back -- unless
                // the keyboard follows the cursor: then typing while the other PC's cursor is here is the point.
                if (BeingControlled && TakeBackArmed && !KeyboardFollowsCursor) LocalInputWhileControlled?.Invoke();
                // A modifier alone is not typing: the Ctrl and Alt of Ctrl+Alt+J, pressed to jump over to the other
                // PC, revealed the mirrored window that still held the focus here (2026-10-05).
                if (!Native.IsModifierVk(info.vkCode)) LocalActivity?.Invoke(null);
            }
            var tk = ((ushort)info.vkCode, (ushort)info.scanCode, ext);
            lock (_modeGate)
            {
                // The release of a key this PC saw go down goes to this PC, even if the keyboard has since
                // switched to the other PC.
                if (up && _localKeysDown.Remove(tk)) return Native.CallNextHookEx(_keyHook, nCode, wParam, lParam);
                // Keyboard follows the cursor, and the cursor is on the other PC: this key is for it. Our own
                // injections (InjectTag) are for this PC, as in remote mode; a remapper's injections are forwarded.
                if (_forwardKeys && info.dwExtraInfo != Native.InjectTag)
                {
                    if (up) _typedKeysDown.Remove(tk); else _typedKeysDown.Add(tk);
                    if (KeyLog) Log.Info($"typed key vk=0x{info.vkCode:X2} {(up ? "up  " : "down")} ext={ext} scan=0x{info.scanCode:X2}");
                    Output?.Invoke(Frame.TypedKey(tk.Item1, tk.Item2, up, ext));
                    return 1;
                }
                if (!up) _localKeysDown.Add(tk);
            }
            return Native.CallNextHookEx(_keyHook, nCode, wParam, lParam);
        }

        // Our own injections are for this PC (see MouseHook): keys typed into the mirror went to the peer instead.
        if (info.dwExtraInfo == Native.InjectTag) return Native.CallNextHookEx(_keyHook, nCode, wParam, lParam);

        var key = ((ushort)info.vkCode, (ushort)info.scanCode, ext);
        // A key that went down on this PC before we crossed over is released here, not on the other PC.
        lock (_modeGate) if (up && _localKeysDown.Remove(key)) return Native.CallNextHookEx(_keyHook, nCode, wParam, lParam);

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

        // The hooks run on their own thread now, while Raw Input (crossings) runs on the UI thread: both touch these.
        lock (_modeGate) { if (up) _keysDown.Remove(key); else _keysDown.Add(key); }
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

    // Our hotkeys want the LEFT Ctrl, physically pressed, and no right Ctrl. Sostenuto turns CapsLock into a
    // right Ctrl, and with CapsAltIsCtrl its CapsLock+Alt+J/L (!>^j, !>^l) sends Ctrl+Left/Right by pressing
    // the LEFT Ctrl itself: Windows then reported the left Ctrl down, and with Alt and J held that read as our
    // Ctrl+Alt+J (2026-10-07). A left Ctrl a program injects never counts, and a right Ctrl held (by the user
    // or by such a remapper) rules our hotkeys out.
    private volatile bool _physLCtrl;

    // Every key this PC's own keyboard holds (not injected), whatever mode we are in, with when it last went down
    // or auto-repeated. The injector asks it before releasing a modifier nobody accounts for: a key the user
    // really holds here must never be let go. Not checked against Windows, which reads a key we swallow as up;
    // instead a held key repeats many times a second, so one silent for PhysHeldMs had its up lost (the secure
    // desktop of Win+L eats it) and no longer counts.
    private readonly Dictionary<uint, long> _physDown = [];
    private const int PhysHeldMs = 2000;

    /// <summary>True while any key of this PC's own keyboard is held.</summary>
    public bool AnyPhysicalKeyDown()
    {
        long now = Stopwatch.GetTimestamp();
        lock (_physDown)
            return _physDown.Values.Any(t => now - t < Stopwatch.Frequency * PhysHeldMs / 1000);
    }

    private bool RCtrlHeld()
    {
        if (Native.IsDown(Native.VK_RCONTROL)) return true;
        lock (_modeGate) return _keysDown.Any(k => k.vk == Native.VK_RCONTROL) || _typedKeysDown.Any(k => k.vk == Native.VK_RCONTROL);
    }

    private bool LCtrlDown() => _physLCtrl && !RCtrlHeld();
    private bool AltDown() { lock (_modeGate) return _keysDown.Any(k => k.vk is Native.VK_LMENU or Native.VK_RMENU); }

    /// <summary>Left Ctrl (physically) and Alt held, as Windows sees it here or as the keys forwarded to the
    /// other PC say, and no right Ctrl.</summary>
    private bool LocalHotkeyModifiers()
    {
        if (!LCtrlDown()) return false;
        bool alt = Native.IsDown(Native.VK_MENU);
        if (!alt)
            lock (_modeGate) alt = _typedKeysDown.Any(k => k.vk is Native.VK_LMENU or Native.VK_RMENU or Native.VK_MENU);
        return alt;
    }

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
        if (_hookThreadId != 0) PostThreadMessage(_hookThreadId, 0x0012 /* WM_QUIT */, IntPtr.Zero, IntPtr.Zero);
    }
}
