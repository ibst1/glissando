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

    /// <summary>Stopwatch timestamp of the last input frame from the peer. Lets the host know the peer is
    /// still feeding us injected moves, so it should not treat that injected cursor as its own.</summary>
    public long LastInputTicks;

    private Rectangle _screen;
    // The return crossing is disarmed until the cursor has moved this far in from every edge that leads
    // back. Without this the cursor, which lands a few px inside on Enter, bounces home on the first jitter.
    private const int ArmMargin = 24;
    private bool _returnArmed;
    private readonly HashSet<(ushort vk, ushort scan, bool ext)> _keysDown = [];
    private readonly HashSet<byte> _buttonsDown = [];
    private readonly object _gate = new();

    // When each held key was last pressed (or auto-repeated). A modifier the other PC really holds is re-sent
    // by its keyboard auto-repeat several times a second, so one whose down has not been renewed in this long
    // has had its release lost on the lossy link: drop it, or it would be re-asserted before every key from
    // then on and everything typed here would be capitals (2026-09-23). Ordinary keys are left alone.
    private const int StaleModifierMs = 2500;
    private readonly Dictionary<(ushort vk, ushort scan, bool ext), long> _keyDownAt = [];
    private readonly System.Threading.Timer _staleTimer;

    // ---- orphaned modifiers ------------------------------------------------------------------------------
    // A modifier Windows holds down that nobody accounts for: not one we injected and still track, not one this
    // PC's own keyboard holds. A remapper here can produce one from keys we injected (Sostenuto turns an injected
    // CapsLock into a held right Ctrl) and then lose track of it; Ctrl stayed down on LU with nothing in our own
    // set to release (2026-10-09). Checked shortly after control ends, and every second: one that persists for
    // StaleModifierMs while no key at all is held, here or by the peer, is released and logged.
    private static readonly (ushort vk, bool ext, string name)[] SidedModifiers =
    [
        (Native.VK_LSHIFT, false, "LShift"), (Native.VK_RSHIFT, true, "RShift"),
        (Native.VK_LCONTROL, false, "LCtrl"), (Native.VK_RCONTROL, true, "RCtrl"),
        (Native.VK_LMENU, false, "LAlt"), (Native.VK_RMENU, true, "RAlt"),
        (Native.VK_LWIN, true, "LWin"), (Native.VK_RWIN, true, "RWin"),
    ];
    private readonly Dictionary<ushort, long> _orphanSince = [];
    private const ushort VK_PACKET = 0xE7;

    /// <summary>Whether this PC's own keyboard holds any key (InputCapture). Unset: assume it does, release nothing.</summary>
    public Func<bool>? AnyPhysicalKeyDown;

    // ---- smoothing --------------------------------------------------------------------------------------
    // The sender coalesces moves and sends them 8-60 ms apart (MovePacing), so the link is not flooded. Applied
    // as they come, each packet was one jump: 30 jumps a second on a slower link, a visibly choppy pointer both
    // ways (2026-10-07). Instead a packet's movement is spread over the time until the next one is expected
    // (the measured packet interval, at most MaxSpreadMs), in steps of about StepMs, by a thread of its own.
    // Same packets on the link, a gliding pointer, and at most one packet interval of added delay. Small steps
    // also get Windows' pointer acceleration closer to what the mouse itself would have got than one big
    // coalesced jump did. Packets closer than DirectBelowMs are applied at once, as before.
    private const int StepMs = 4, MaxSpreadMs = 40, DirectBelowMs = 6;
    private double _pendX, _pendY;          // movement received but not yet injected
    private long _drainBy;                  // Stopwatch time by which the pending movement should be out
    private long _lastMoveAt;
    private double _intervalMs = 8;         // running estimate of the time between move packets
    private readonly AutoResetEvent _smoothWake = new(false);

    /// <summary>The running estimate of the time between move packets, for the log.</summary>
    public double MoveIntervalMs => _intervalMs;

    public InputInjector(Layout layout)
    {
        Layout = layout;
        _screen = Layout.MyVirtualScreen();
        _staleTimer = new System.Threading.Timer(_ => ReleaseStaleModifiers(), null, 1000, 1000);
        new Thread(SmoothLoop) { IsBackground = true, Name = "move-smoother", Priority = ThreadPriority.AboveNormal }.Start();
    }

    private void SmoothLoop()
    {
        while (true)
        {
            bool more;
            lock (_gate) more = StepPending();
            if (more) Thread.Sleep(StepMs);   // 1 ms timer resolution is set for the process (Program)
            else _smoothWake.WaitOne();
        }
    }

    /// <summary>Inject the share of the pending movement due by now; true while some is left. Under _gate.</summary>
    private bool StepPending()
    {
        if (!Active) { _pendX = _pendY = 0; return false; }
        if (Math.Abs(_pendX) < 1 && Math.Abs(_pendY) < 1) return false;
        long now = System.Diagnostics.Stopwatch.GetTimestamp();
        double left = (_drainBy - now) * 1000.0 / System.Diagnostics.Stopwatch.Frequency;
        double share = left <= StepMs ? 1 : StepMs / left;
        int ix = (int)(_pendX * share), iy = (int)(_pendY * share);
        if (share >= 1) { ix = (int)Math.Round(_pendX); iy = (int)Math.Round(_pendY); }
        if (ix == 0 && iy == 0) return true;   // less than a pixel due yet: wait for the next step
        _pendX -= ix; _pendY -= iy;
        if (share >= 1) _pendX = _pendY = 0;
        InjectMove(ix, iy);
        return Math.Abs(_pendX) >= 1 || Math.Abs(_pendY) >= 1;
    }

    /// <summary>Put out all pending movement at once: before a click or a wheel turn, so it lands where the
    /// pointer was going. Under _gate.</summary>
    private void FlushPending()
    {
        int ix = (int)Math.Round(_pendX), iy = (int)Math.Round(_pendY);
        _pendX = _pendY = 0;
        if (Active && (ix != 0 || iy != 0)) InjectMove(ix, iy);
    }

    /// <summary>Release the modifiers Windows holds that nobody accounts for (see SidedModifiers). With
    /// <paramref name="why"/> set, at once; otherwise only those that have been orphaned for StaleModifierMs.</summary>
    private void ReleaseOrphanModifiers(string? why)
    {
        lock (_gate)
        {
            long now = System.Diagnostics.Stopwatch.GetTimestamp();
            if (_keysDown.Count > 0 || (AnyPhysicalKeyDown?.Invoke() ?? true)) { _orphanSince.Clear(); return; }
            var released = new List<string>();
            foreach (var (vk, ext, name) in SidedModifiers)
            {
                if (!Native.IsDown(vk)) { _orphanSince.Remove(vk); continue; }
                if (why == null)
                {
                    if (!_orphanSince.TryGetValue(vk, out long since)) { _orphanSince[vk] = now; continue; }
                    if (now - since < System.Diagnostics.Stopwatch.Frequency * StaleModifierMs / 1000) continue;
                }
                _orphanSince.Remove(vk);
                ReleaseModifier(vk, ext);
                released.Add(name);
            }
            if (released.Count > 0)
                Log.Info($"released {string.Join(", ", released)}: down in Windows, but neither injected by us nor held on this keyboard ({why ?? $"for {StaleModifierMs} ms"})");
        }
    }

    /// <summary>Key-up of a modifier. Alt and Win get the unassigned vkE8 in between first, so a lone release
    /// opens neither the menu bar nor the Start menu.</summary>
    private static void ReleaseModifier(ushort vk, bool ext)
    {
        if (vk is Native.VK_LMENU or Native.VK_RMENU or Native.VK_LWIN or Native.VK_RWIN or 0x12)
        {
            SendKey(0xE8, 0, up: false, ext: false);
            SendKey(0xE8, 0, up: true, ext: false);
        }
        SendKey(vk, 0, up: true, ext);
    }

    private void ReleaseStaleModifiers()
    {
        ReleaseOrphanModifiers(null);
        lock (_gate)
        {
            if (_keysDown.Count == 0) return;
            long now = System.Diagnostics.Stopwatch.GetTimestamp();
            foreach (var k in _keysDown.ToArray())
            {
                if (k.vk == 0 || !Native.IsModifierVk(k.vk)) continue;
                if (!_keyDownAt.TryGetValue(k, out long at) || now - at < System.Diagnostics.Stopwatch.Frequency * StaleModifierMs / 1000) continue;
                Log.Info($"releasing modifier vk=0x{k.vk:X2} held without a repeat for {StaleModifierMs} ms (its release was lost)");
                _keysDown.Remove(k);
                _keyDownAt.Remove(k);
                SendKey(k.vk, k.scan, up: true, k.ext);
            }
        }
    }

    public void ScreenChanged() => _screen = Layout.MyVirtualScreen();

    public void Apply(Frame f)
    {
        lock (_gate)
        {
            if (f.Type is FrameType.MouseMove or FrameType.MouseButton or FrameType.MouseWheel or FrameType.Key)
                LastInputTicks = System.Diagnostics.Stopwatch.GetTimestamp();
            // Enter must always be processed -- it is what activates us. Everything else while inactive is
            // ignored: a key/button signals a genuine desync (rate limited), a stray mouse move is just an
            // unreliable frame that outran the Enter and must NOT be read as desync.
            if (!Active && f.Type != FrameType.Enter)
            {
                if (f.Type is FrameType.Key or FrameType.MouseButton)
                {
                    long now = System.Diagnostics.Stopwatch.GetTimestamp();
                    if (now - _lastDetachedTicks > System.Diagnostics.Stopwatch.Frequency / 2)
                    {
                        _lastDetachedTicks = now;
                        InputWhileInactive?.Invoke();
                    }
                }
                return;
            }
            switch (f.Type)
            {
                case FrameType.Enter: if (f.ReadCrossing() is Crossing c) Enter(c); break;
                case FrameType.Leave: ReleaseAll(); SetActive(false); break;
                case FrameType.MouseMove: Move(f.I16(0), f.I16(2)); break;
                case FrameType.MouseButton: FlushPending(); Button(f.Payload[0], f.Payload[1] != 0); break;
                case FrameType.MouseWheel:
                    FlushPending();
                    Native.Send(Native.Mouse(f.Payload[2] != 0 ? Native.MOUSEEVENTF_HWHEEL : Native.MOUSEEVENTF_WHEEL, data: unchecked((uint)(int)f.I16(0))));
                    break;
                case FrameType.Key: Key(f.U16(0), f.U16(2), (f.Payload[4] & 1) != 0, (f.Payload[4] & 2) != 0); break;
            }
        }
    }

    /// <summary>A key from the other PC's keyboard that follows the cursor: applied whether or not that PC controls
    /// us -- its cursor is at home here, so its typing belongs here. Not control input: it neither refreshes the
    /// "peer is controlling us" clock nor counts as a desync. Anything still held is released with the rest.</summary>
    public void ApplyTypedKey(Frame f)
    {
        lock (_gate)
            if (f.Payload.Length >= 5) Key(f.U16(0), f.U16(2), (f.Payload[4] & 1) != 0, (f.Payload[4] & 2) != 0);
    }

    /// <summary>Refresh the "peer is still controlling us" clock (Enter and each ControlAlive heartbeat).</summary>
    public void KeepAlive() => LastInputTicks = System.Diagnostics.Stopwatch.GetTimestamp();

    private void Enter(Crossing c)
    {
        _pendX = _pendY = 0;
        LastInputTicks = System.Diagnostics.Stopwatch.GetTimestamp();
        _screen = Layout.MyVirtualScreen();
        // Land a few pixels inside the edge; crossing back needs a real push toward it, not jitter.
        var p = Layout.ClampIntoMonitor(c.Target, Layout.Add(c.Target, Layout.Unit(c.Dir), 6));
        // Absolute SendInput (not SetCursorPos) so this placement is flagged injected and does not trip the
        // controlled PC's own take-back detection.
        Native.MoveCursorAbsolute(p.X + _screen.X, p.Y + _screen.Y);
        _returnArmed = false; // must move well clear of the edges before a return counts
        SetActive(true);
    }

    private void Move(int dx, int dy)
    {
        if (!Active) return;
        long now = System.Diagnostics.Stopwatch.GetTimestamp();
        double gap = (now - _lastMoveAt) * 1000.0 / System.Diagnostics.Stopwatch.Frequency;
        _lastMoveAt = now;
        if (gap < 100) _intervalMs = _intervalMs * 0.7 + Math.Clamp(gap, 1, MaxSpreadMs) * 0.3;   // not across a pause
        double spread = Math.Min(_intervalMs, MaxSpreadMs);
        _pendX += dx; _pendY += dy;
        if (spread < DirectBelowMs) { FlushPending(); return; }
        _drainBy = now + (long)(spread * System.Diagnostics.Stopwatch.Frequency / 1000);
        _smoothWake.Set();
    }

    /// <summary>One relative step of the pointer, and the check for an edge that leads back.</summary>
    private void InjectMove(int dx, int dy)
    {
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
        _pendX = _pendY = 0;   // the rest of this movement belongs to the other PC now
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
        var key = (vk, scan, ext);
        if (up) { _keysDown.Remove(key); _keyDownAt.Remove(key); }
        else { _keysDown.Add(key); _keyDownAt[key] = System.Diagnostics.Stopwatch.GetTimestamp(); }
        if (Host.InputCapture.KeyLog) Log.Info($"apply key vk=0x{vk:X2} {(up ? "up  " : "down")} ext={ext} scan=0x{scan:X2}");

        // An injected held modifier only sticks to the very next key, so a held Shift gave one capital then
        // lowercase. Before each non-modifier key-down, re-assert every modifier the controller is holding,
        // so the modifier is freshly set for this key too.
        if (!up && vk != 0 && !Native.IsModifierVk(vk))
            foreach (var m in _keysDown)
                if (m.vk != 0 && Native.IsModifierVk(m.vk))
                    SendKey(m.vk, m.scan, up: false, m.ext);

        SendKey(vk, scan, up, ext);
    }

    /// <summary>
    /// Inject a key. Modifiers and most keys go by virtual-key code, which sets the state the OS applies to
    /// character translation; a held modifier is kept effective for every key by the re-assertion above.
    /// Fall back to the scancode only for a key that has no virtual-key code.
    /// </summary>
    internal static void SendKey(ushort vk, ushort scan, bool up, bool ext)
    {
        uint flags = up ? Native.KEYEVENTF_KEYUP : 0;
        // VK_PACKET is not a key: its "scan code" is a UTF-16 character that a program on the other PC typed as
        // text (SendText, Expanto's insert). Replayed as a virtual key it typed nothing.
        if (vk == VK_PACKET) { Native.Send(Native.Keyboard(0, scan, flags | Native.KEYEVENTF_UNICODE)); return; }
        if (ext) flags |= Native.KEYEVENTF_EXTENDEDKEY;
        if (vk != 0)
            Native.Send(Native.Keyboard(vk, scan, flags));
        else
            Native.Send(Native.Keyboard(0, scan, flags | Native.KEYEVENTF_SCANCODE));
    }

    /// <summary>Release anything still held so nothing sticks when the other PC goes away.</summary>
    public void ReleaseAll()
    {
        lock (_gate)
        {
            if (_keysDown.Count > 0)
                Log.Info($"released on leave: {string.Join(", ", _keysDown.Select(k => $"vk=0x{k.vk:X2}"))}");
            foreach (var k in _keysDown.ToArray())
                if (Native.IsModifierVk(k.vk)) ReleaseModifier(k.vk, k.ext); else SendKey(k.vk, k.scan, up: true, k.ext);
            _keysDown.Clear();
            _keyDownAt.Clear();
            foreach (var b in _buttonsDown.ToArray()) Button(b, false);
            _buttonsDown.Clear();
        }
        // A remapper here gets a moment to let go of what it made from our keys (Sostenuto's right Ctrl for an
        // injected CapsLock) before anything still down is taken for orphaned.
        Task.Delay(500).ContinueWith(_ => { if (!Active) ReleaseOrphanModifiers("control ended"); });
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
