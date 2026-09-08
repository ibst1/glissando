using System.Buffers.Binary;
using System.Diagnostics;
using System.Runtime.InteropServices;

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
    private int _rawDx, _rawDy; // last physical movement seen while local: disambiguates corners
    private readonly HashSet<(ushort vk, ushort scan, bool ext)> _keysDown = [];
    private readonly HashSet<byte> _buttonsDown = [];
    private readonly byte[] _rawBuf = new byte[128];

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
    public void Release()
    {
        if (Remote) LeaveRemote(null);
    }

    /// <summary>The other PC reports its cursor crossed back toward us.</summary>
    public void OnReturnEdge(Crossing c)
    {
        if (Remote) LeaveRemote(c);
    }

    private Point ToLocal(Native.POINT p) => new(p.X - _screen.X, p.Y - _screen.Y);
    private Point ToScreen(Point p) => new(p.X + _screen.X, p.Y + _screen.Y);

    // ---- hooks ----------------------------------------------------------------

    private IntPtr MouseHook(int nCode, IntPtr wParam, IntPtr lParam)
    {
        if (nCode < 0) return Native.CallNextHookEx(_mouseHook, nCode, wParam, lParam);
        int msg = (int)wParam;
        var info = Marshal.PtrToStructure<Native.MSLLHOOKSTRUCT>(lParam);

        if (!Remote)
        {
            if (msg == Native.WM_MOUSEMOVE && (info.flags & Native.LLMHF_INJECTED) == 0
                && Stopwatch.GetTimestamp() > _ignoreEdgeUntil && (CanEnter?.Invoke() ?? false))
            {
                var c = Layout.FindCrossing(ToLocal(info.pt), _rawDx, _rawDy);
                if (c != null)
                {
                    EnterRemote(c.Value);
                    return 1;
                }
            }
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
            case Native.WM_MOUSEWHEEL: Output?.Invoke(Frame.MouseWheel((short)(info.mouseData >> 16), false)); break;
            case Native.WM_MOUSEHWHEEL: Output?.Invoke(Frame.MouseWheel((short)(info.mouseData >> 16), true)); break;
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

        if (up) _keysDown.Remove(key); else _keysDown.Add(key);
        Output?.Invoke(Frame.Key(key.Item1, key.Item2, up, ext));
        return 1;
    }

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
        long hDevice = BinaryPrimitives.ReadInt64LittleEndian(_rawBuf.AsSpan(8));
        if (hDevice == 0) return; // injected by SendInput (e.g. the other PC controlling us): never echo it back
        int m = Native.RawHeaderSize;
        ushort flags = BinaryPrimitives.ReadUInt16LittleEndian(_rawBuf.AsSpan(m));
        if ((flags & Native.MOUSE_MOVE_ABSOLUTE) != 0) return; // tablets/RDP: not supported yet
        int dx = BinaryPrimitives.ReadInt32LittleEndian(_rawBuf.AsSpan(m + 12));
        int dy = BinaryPrimitives.ReadInt32LittleEndian(_rawBuf.AsSpan(m + 16));
        if (dx == 0 && dy == 0) return;
        if (!Remote) { _rawDx = dx; _rawDy = dy; return; }
        Output?.Invoke(Frame.MouseMove(dx, dy));
    }

    // ---- crossing ---------------------------------------------------------------

    private void EnterRemote(Crossing c)
    {
        Remote = true;
        _keysDown.Clear();
        _buttonsDown.Clear();
        Log.Info($"entering remote: {c.Dir} -> peer {c.Target}");
        Output?.Invoke(Frame.Enter(c));
        EnteredRemote?.Invoke();
    }

    private void LeaveRemote(Crossing? back)
    {
        Remote = false;
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
        Log.Info("left remote");
        LeftRemote?.Invoke();
    }

    public void Dispose()
    {
        if (Remote) LeaveRemote(null);
        if (_mouseHook != IntPtr.Zero) Native.UnhookWindowsHookEx(_mouseHook);
        if (_keyHook != IntPtr.Zero) Native.UnhookWindowsHookEx(_keyHook);
        _mouseHook = _keyHook = IntPtr.Zero;
    }
}
