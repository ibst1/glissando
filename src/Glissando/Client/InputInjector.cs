namespace Glissando.Client;

/// <summary>Controlled side: replays frames from the other PC with SendInput and watches for edges that lead back.</summary>
public sealed class InputInjector
{
    /// <summary>Raised when the cursor left one of our monitors toward the other PC.</summary>
    public event Action<Crossing>? ReturnEdgeHit;
    public event Action? StateChanged;

    /// <summary>Raised when mouse or key input arrives while not active: the controller still thinks it drives us.</summary>
    public event Action? InputWhileInactive;

    private long _lastDetachedTicks;

    /// <summary>True between Enter and Leave/return.</summary>
    public volatile bool Active;

    /// <summary>Both PCs' monitors and where the other PC sits. Replaced whole when settings or screens change.</summary>
    public Layout Layout { get; set; }

    private Rectangle _screen;
    // The return crossing is disarmed until the cursor has moved this far in from every edge that leads
    // back. Without this the cursor, which lands a few px inside on Enter, bounces home on the first jitter.
    private const int ArmMargin = 24;
    private bool _returnArmed;
    private readonly HashSet<(ushort vk, ushort scan, bool ext)> _keysDown = [];
    private readonly HashSet<byte> _buttonsDown = [];
    private readonly object _gate = new();

    public InputInjector(Layout layout)
    {
        Layout = layout;
        _screen = Layout.MyVirtualScreen();
    }

    public void ScreenChanged() => _screen = Layout.MyVirtualScreen();

    public void Apply(Frame f)
    {
        lock (_gate)
        {
            if (!Active && f.Type is FrameType.MouseMove or FrameType.MouseButton or FrameType.MouseWheel or FrameType.Key)
            {
                // Only Enter starts a session; anything else means the two sides disagree. Say so (rate limited) and ignore.
                long now = System.Diagnostics.Stopwatch.GetTimestamp();
                if (now - _lastDetachedTicks > System.Diagnostics.Stopwatch.Frequency / 2)
                {
                    _lastDetachedTicks = now;
                    InputWhileInactive?.Invoke();
                }
                return;
            }
            switch (f.Type)
            {
                case FrameType.Enter: if (f.ReadCrossing() is Crossing c) Enter(c); break;
                case FrameType.Leave: ReleaseAll(); SetActive(false); break;
                case FrameType.MouseMove: Move(f.I16(0), f.I16(2)); break;
                case FrameType.MouseButton: Button(f.Payload[0], f.Payload[1] != 0); break;
                case FrameType.MouseWheel:
                    Native.Send(Native.Mouse(f.Payload[2] != 0 ? Native.MOUSEEVENTF_HWHEEL : Native.MOUSEEVENTF_WHEEL, data: unchecked((uint)(int)f.I16(0))));
                    break;
                case FrameType.Key: Key(f.U16(0), f.U16(2), (f.Payload[4] & 1) != 0, (f.Payload[4] & 2) != 0); break;
            }
        }
    }

    private void Enter(Crossing c)
    {
        _screen = Layout.MyVirtualScreen();
        // Land a few pixels inside the edge; crossing back needs a real push toward it, not jitter.
        var p = Layout.ClampIntoMonitor(c.Target, Layout.Add(c.Target, Layout.Unit(c.Dir), 6));
        Native.SetCursorPos(p.X + _screen.X, p.Y + _screen.Y);
        _returnArmed = false; // must move well clear of the edges before a return counts
        SetActive(true);
    }

    private void Move(int dx, int dy)
    {
        if (!Active) return;
        Native.Send(Native.Mouse(Native.MOUSEEVENTF_MOVE, dx, dy));
        if (!Native.GetCursorPos(out var cur)) return;
        var p = new Point(cur.X - _screen.X, cur.Y - _screen.Y);
        if (!_returnArmed)
        {
            // Arm once the cursor is clear of every edge that leads back.
            if (!Layout.NearCrossingEdge(p, ArmMargin)) _returnArmed = true;
            return;
        }
        if (Layout.FindCrossing(p, dx, dy) is not Crossing back) return;
        ReleaseAll();
        _returnArmed = false;
        SetActive(false);
        ReturnEdgeHit?.Invoke(back);
    }

    private void Button(byte button, bool down)
    {
        uint flag; uint data = 0;
        switch (button)
        {
            case 1: flag = down ? Native.MOUSEEVENTF_LEFTDOWN : Native.MOUSEEVENTF_LEFTUP; break;
            case 2: flag = down ? Native.MOUSEEVENTF_RIGHTDOWN : Native.MOUSEEVENTF_RIGHTUP; break;
            case 3: flag = down ? Native.MOUSEEVENTF_MIDDLEDOWN : Native.MOUSEEVENTF_MIDDLEUP; break;
            case 4: flag = down ? Native.MOUSEEVENTF_XDOWN : Native.MOUSEEVENTF_XUP; data = 1; break;
            case 5: flag = down ? Native.MOUSEEVENTF_XDOWN : Native.MOUSEEVENTF_XUP; data = 2; break;
            default: return;
        }
        if (down) _buttonsDown.Add(button); else _buttonsDown.Remove(button);
        Native.Send(Native.Mouse(flag, data: data));
    }

    private void Key(ushort vk, ushort scan, bool up, bool ext)
    {
        uint flags = up ? Native.KEYEVENTF_KEYUP : 0;
        if (ext) flags |= Native.KEYEVENTF_EXTENDEDKEY;
        if (scan != 0) { flags |= Native.KEYEVENTF_SCANCODE; vk = 0; }
        var key = (vk, scan, ext);
        if (up) _keysDown.Remove(key); else _keysDown.Add(key);
        Native.Send(Native.Keyboard(vk, scan, flags));
    }

    /// <summary>Release anything still held so nothing sticks when the other PC goes away.</summary>
    public void ReleaseAll()
    {
        lock (_gate)
        {
            foreach (var k in _keysDown.ToArray())
            {
                uint flags = Native.KEYEVENTF_KEYUP | (k.ext ? Native.KEYEVENTF_EXTENDEDKEY : 0) | (k.scan != 0 ? Native.KEYEVENTF_SCANCODE : 0);
                Native.Send(Native.Keyboard(k.vk, k.scan, flags));
            }
            _keysDown.Clear();
            foreach (var b in _buttonsDown.ToArray()) Button(b, false);
            _buttonsDown.Clear();
        }
    }

    public void Deactivate()
    {
        lock (_gate)
        {
            ReleaseAll();
            SetActive(false);
        }
    }

    private void SetActive(bool v)
    {
        if (Active == v) return;
        Active = v;
        Log.Info(v ? "being controlled" : "no longer controlled");
        StateChanged?.Invoke();
    }
}
