using System.Diagnostics;
using System.Security.Cryptography;
using System.Text;
using System.Threading.Channels;
using Glissando.Client;
using Glissando.Host;
using Glissando.Transport;

namespace Glissando;

public interface IClipboardAccess
{
    string? GetText();
    void SetText(string text);

    /// <summary>The clipboard's picture as PNG, or as JPEG if the PNG is over <paramref name="maxBytes"/>; null
    /// when there is no picture (or text is present, which travels instead) or it is too big even as JPEG.</summary>
    byte[]? GetImage(int maxBytes);

    /// <summary>Put an encoded picture (PNG or JPEG) on the clipboard.</summary>
    void SetImage(byte[] encoded);
}

/// <summary>
/// Ties transport, crypto, handshake, keepalive and both input halves together. Both PCs run the
/// same code: whichever PC's own cursor crosses an edge that leads to the other takes control.
/// </summary>
public sealed class Session : IDisposable
{
    public event Action? StateChanged;

    /// <summary>Raised when placement or the peer's monitors were adopted from the other PC (settings already saved).</summary>
    public event Action? SettingsChanged;

    public string TransportStatus { get; private set; } = "starting";
    public bool Linked => _channel.IsLinked;
    public bool Authenticated { get; private set; }
    public string PeerName { get; private set; } = "";

    /// <summary>The other PC's build id from its Hello, e.g. "0.3.8+1a2b3c4".</summary>
    public string PeerVersion { get; private set; } = "";
    public bool Controlling => _capture.Remote;
    public bool Controlled => _injector.Active;

    /// <summary>User asked to keep the cursor on this PC (Ctrl+Alt+L or the tray menu). Not persisted.</summary>
    public bool Locked { get; private set; }

    /// <summary>Latest measured round trip to the peer in milliseconds, 0 until a pong has arrived.</summary>
    public double RttMs { get; private set; }

    /// <summary>This PC's Windows session is locked (lock screen). Nothing is captured or injected meanwhile.</summary>
    public bool LocalLocked { get; private set; }

    /// <summary>The other PC reported its session locked; crossing over is pointless until it is unlocked.</summary>
    public bool PeerLocked { get; private set; }

    /// <summary>The other PC pings slowly because it is idle; allow it more silence before declaring it lost.</summary>
    public bool PeerPowerSaving { get; private set; }

    /// <summary>What the other PC last said about its own Bluetooth (kept after the link drops: that is when it
    /// matters, the PC that cannot link is the one to fix), and when.</summary>
    public Transport.BleHealth.Verdict PeerBluetooth { get; private set; }
    private long _peerBluetoothAt;

    private bool _powerSaving;
    private int _detachPending;
    private long _lastDetachLog;

    private readonly Settings _s;
    private readonly IChannel _channel;
    private readonly InputCapture _capture;
    private readonly InputInjector _injector;
    private readonly IClipboardAccess _clipboard;
    // The windows this PC mirrors to the other one, by mirror id (the first byte of every mirror frame).
    private readonly Dictionary<byte, Mirror.MirrorSender> _mirrors = [];
    private readonly object _mirrorGate = new();
    private byte _lastMirrorId;
    private Crypto? _crypto;

    // The UDP pixel path (see UdpPixelLink): optional, beside BLE, with a crypto of its own nonce space.
    private readonly UdpPixelLink? _udp;
    private Crypto? _cryptoUdp;
    private long _lastUdpTick;

    /// <summary>True while mirror pixels can travel over the shared IP network instead of BLE.</summary>
    public bool UdpUp => _udp?.Up ?? false;

    /// <summary>Raised on the receive thread with each incoming window-mirroring frame; the tray marshals it to
    /// the UI thread and into a MirrorReceiver.</summary>
    public event Action<Frame>? MirrorReceived;

    /// <summary>True while this PC is mirroring at least one of its windows to the other PC.</summary>
    public bool Mirroring { get { lock (_mirrorGate) return _mirrors.Count > 0; } }

    private Mirror.MirrorSender[] Mirrors { get { lock (_mirrorGate) return [.. _mirrors.Values]; } }

    /// <summary>The windows mirrored now, with their titles, oldest first.</summary>
    public (IntPtr Hwnd, string Title)[] MirroredWindows => Mirrors.OrderBy(m => m.Id).Select(m => (m.Hwnd, m.Title)).ToArray();

    public bool IsMirroring(IntPtr hwnd) => Mirrors.Any(m => m.Hwnd == hwnd);

    /// <summary>True while this window is mirrored and invisible on this PC.</summary>
    public bool MirrorHidden(IntPtr hwnd) => Mirrors.Any(m => m.Hwnd == hwnd && m.Hidden);

    /// <summary>Start mirroring the given window to the peer, beside any already mirrored. False if it already is.</summary>
    public bool StartMirror(IntPtr hwnd)
    {
        Mirror.MirrorSender m;
        lock (_mirrorGate)
        {
            if (_mirrors.Values.Any(x => x.Hwnd == hwnd)) return false;
            // Ids go round 1..255 rather than restarting at 1, so a late frame of a mirror just stopped is never
            // taken for a new one (the viewer also ignores an id for a few seconds after its End).
            byte id = _lastMirrorId;
            do id = (byte)(id == 255 ? 1 : id + 1); while (_mirrors.ContainsKey(id));
            _lastMirrorId = id;
            m = new Mirror.MirrorSender(hwnd, id, () => { lock (_mirrorGate) return _mirrors.Count; }, Enqueue, () => Volatile.Read(ref _queued),
                slowLink: !_channel.IsCentral, hide: _s.HideMirroredWindow, udpUp: () => UdpUp, sendUdp: SendUdp);
            m.Gone += gone => StopMirror(gone.Hwnd);
            _mirrors[id] = m;
        }
        m.Start();
        // Started with the other PC's mouse over here (the tray menu clicked from there): that mouse goes on
        // driving this PC, so the window it just hid must not catch its clicks.
        if (_injector.Active) { Mirror.WindowHider.SetClickThrough(true); Mirror.WindowHider.StepAside(); }
        Log.Info($"mirror #{m.Id}: started for window {hwnd:X} \"{m.Title}\"");
        StateChanged?.Invoke();
        return true;
    }

    /// <summary>Viewer side: an interaction with the mirror (pointer, click, wheel, key) for the source PC.</summary>
    public void SendMirrorInput(Frame f) { if (Authenticated) Send(f); }

    public void StopMirror(IntPtr hwnd)
    {
        Mirror.MirrorSender? m;
        lock (_mirrorGate)
        {
            m = _mirrors.Values.FirstOrDefault(x => x.Hwnd == hwnd);
            if (m != null) _mirrors.Remove(m.Id);
        }
        if (m == null) return;
        m.Stop();
        Log.Info($"mirror #{m.Id}: stopped");
        StateChanged?.Invoke();
    }

    public void StopAllMirrors()
    {
        foreach (var m in Mirrors) StopMirror(m.Hwnd);
    }
    private readonly Channel<Frame> _queue = Channel.CreateUnbounded<Frame>(new UnboundedChannelOptions { SingleReader = true });
    private readonly CancellationTokenSource _cts = new();
    private readonly byte[] _myNonce = new byte[16];
    private byte[]? _peerNonce; // the peer's last Hello nonce: a new one means a new Hello from it
    private long _lastPeer, _lastSend, _lastHello, _lastMoveSend;
    private long _lastSendOk, _lastRawRx;   // the link itself carries packets each way (whatever they hold)

    // Sender statistics, logged every 2 s while this PC controls the other one: where does the time go when
    // driving lags? Glissando's own queue growing means SendAsync is slow (the stack makes us wait); a short
    // queue with fast sends and a long round trip means the stack takes packets faster than the radio sends
    // them (2026-10-06: RS -> LU lagged up to 3.5 s whichever PC was central).
    private int _stMoves, _stKeys, _stOther, _stFailed, _stMaxQueued, _stBytes;
    private double _stSendMs, _stMaxSendMs;
    private long _stSince = Stopwatch.GetTimestamp();
    // The receiving side of the same question: how long does applying a frame take here (decrypting it and,
    // above all, SendInput, which waits on every low-level input hook on this PC)?
    private int _rxN, _rxSlow;

    /// <summary>Longest wait for the UI thread (which runs our own low-level hooks) since the last stats line;
    /// measured by the tray.</summary>
    public static int UiLagMaxMs;
    private double _rxMs, _rxMaxMs;
    private bool _noAuthExplained;
    private long _lastPong, _lastLivePing, _lastOneWayReconnect;   // is the peer HEARING us? see TimerLoop
    private int _pingsSincePong;
    private long _linkedSince;   // when the current link came up, for the stuck-handshake watchdog
    private long _lastActivePing, _lastRttLog, _lastPeriodicHello;
    private string _lastHelloSummary = "";
    private double _lastRttLogged;
    private int _queued;
    private byte _clipId;
    private string? _lastClipboardSent;
    private readonly Dictionary<byte, (byte total, byte[]?[] chunks)> _clipIn = [];
    private string _setupKey = "";

    /// <summary>The screen setup changed (start, displays added or removed, the peer's monitors arrived): bring
    /// in the placement remembered for it, or remember the current one for a setup seen for the first time.</summary>
    private void ReconcilePlacement(string why)
    {
        var mine = Layout.MyMonitors();
        var key = Layout.SetupKey(mine, PeerName ?? "", _s.OtherMonitorList);
        bool peerSwapped = false;
        if (!Authenticated && !_s.Placements.ContainsKey(key))
        {
            // The other PC is not here to say what it has: go by this PC's screens (see DeskForMyScreens).
            var desk = Layout.DeskForMyScreens(_s.Placements.Select(kv => (kv.Key, kv.Value.Name, kv.Value.Updated)), mine, PeerName ?? "");
            if (desk != null && Layout.TryParseSetupKey(desk, out _, out _, out var peerThere))
            {
                peerSwapped = !peerThere.SequenceEqual(_s.OtherMonitorList);
                _s.OtherMonitorList = peerThere;
                key = desk;
            }
        }
        if (key == _setupKey) return;
        _setupKey = key;
        if (_s.RecallPlacement(key, out bool changed))
        {
            Log.Info($"screen setup ({why}): recalled placement, other PC at ({_s.PeerX},{_s.PeerY}){(changed ? "" : " (as now)")}");
            if (!changed && !peerSwapped) return;
            RebuildLayout();
            try { _s.Save(); } catch (Exception ex) { Log.Warn("save settings: " + ex.Message); }
            SettingsChanged?.Invoke();
            if (Linked) SendHello();
        }
        else if (Layout.IsCompleteSetupKey(key))
        {
            // Not seen in this exact arrangement. If exactly one remembered desk has the same physical screens
            // on both PCs, it is almost certainly this desk (a monitor renumbered or nudged): take its placement.
            // With several candidates, do not guess - keep the current placement and let the Desks tab offer them.
            var matches = _s.Placements.Keys.Where(k => k != key && Layout.SameScreens(k, key)).ToArray();
            if (matches.Length == 1)
            {
                var only = _s.Placements[matches[0]];
                _s.PeerX = only.PeerX; _s.PeerY = only.PeerY; _s.PlacementUpdated = only.Updated;
                _s.RememberPlacement(key);
                if (_s.Placements.TryGetValue(key, out var np)) np.Name = only.Name;
                try { _s.Save(); } catch (Exception ex) { Log.Warn("save settings: " + ex.Message); }
                Log.Info($"screen setup ({why}): new arrangement, but one remembered desk has the same screens on both PCs{(only.Name != "" ? $" (\"{only.Name}\")" : "")}: using its placement, other PC at ({only.PeerX},{only.PeerY})");
                RebuildLayout();
                SettingsChanged?.Invoke();
                if (Linked) SendHello();
            }
            // Remember a new desk only with the other PC linked: without it its monitor list is the last desk's,
            // and the placement in use belongs to that desk too.
            else if (_s.PeerX != null && Authenticated)
            {
                _s.RememberPlacement(key);
                try { _s.Save(); } catch (Exception ex) { Log.Warn("save settings: " + ex.Message); }
                Log.Info($"screen setup ({why}): new; remembering the current placement for it" + (matches.Length > 1 ? $" ({matches.Length} remembered desks have the same screens - pick one on the Desks tab)" : ""));
            }
        }
    }

    public Session(Settings s, IChannel channel, InputCapture capture, InputInjector injector, IClipboardAccess clipboard)
    {
        _s = s;
        _channel = channel;
        _capture = capture;
        _injector = injector;
        _clipboard = clipboard;
        // Do not start controlling while the peer is still feeding us injected moves: right after it hands
        // control back, its trailing moves land on our return edge, and reading that injected cursor as our
        // own would start a crossing war. This clears within ~150 ms of the peer's last frame, so a real
        // handover (the peer has actually stopped) stays responsive.
        _capture.CanEnter = () => Authenticated && _s.CaptureEnabled && !Locked && !LocalLocked && !PeerLocked
            && !_injector.Active && !PeerRecentlyInjected();
        // Every state change (link, authentication, control, lock, settings) re-evaluates where the keyboard goes,
        // as does the first movement of this PC's own mouse while the keys are away (raised only then, not per move).
        StateChanged += UpdateKeyForwarding;
        _capture.LocalMouseWhileForwarding += () => { UpdateKeyForwarding(); StateChanged?.Invoke(); };

        if (s.UdpMirror)
        {
            try
            {
                _udp = new UdpPixelLink();
                _udp.Received += OnUdpReceived;
                _udp.UpChanged += up => { if (!up) Log.Info("udp: mirror pixels back on BLE"); StateChanged?.Invoke(); };
                Log.Info($"udp: pixel path listening on port {_udp.LocalPort}");
            }
            catch (Exception ex) { Log.Warn("udp: pixel path unavailable: " + ex.Message); _udp = null; }
        }
    }

    /// <summary>A pixel frame for the peer: over UDP when that path is up, else queued on BLE like everything.</summary>
    private void SendUdp(Frame f)
    {
        var c = _cryptoUdp;
        if (c == null || _udp == null || !_udp.Up || !_udp.Send(c.Seal(f.Encode()))) Enqueue(f);
    }

    /// <summary>A datagram on the UDP path: only what decrypts with the session key counts, and the first one
    /// from an endpoint makes that endpoint the peer. Probes carry nothing; everything else is an app frame.</summary>
    private void OnUdpReceived(byte[] packet, System.Net.IPEndPoint from)
    {
        var c = _cryptoUdp;
        if (c == null || _udp == null) return;
        if (!c.Open(packet, out var plain)) return;
        if (!Frame.TryDecode(plain, out var f)) return;
        _udp.Confirm(from);
        _lastPeer = Stopwatch.GetTimestamp();
        if (f.Type == FrameType.UdpProbe) return;
        lock (_rxGate)
        {
            if (IsDuplicate(f)) return;
            HandleApp(f);
        }
    }

    /// <summary>"Keyboard follows the cursor": this PC's keys go to the other PC only while linked and
    /// authenticated, neither side locked, and the other PC's cursor not on this PC (then they type here).
    /// While this PC controls the other, the ordinary remote path carries the keys instead.</summary>
    private void UpdateKeyForwarding()
    {
        _capture.KeyboardFollowsCursor = _s.KeyboardFollowsCursor;
        _capture.ForwardKeys = _s.KeyboardFollowsCursor && Authenticated && !Locked && !LocalLocked && !PeerLocked && !_injector.Active
            && PeerMouseUsedLast();
    }

    // When the other PC's mouse was last heard from: its cursor arriving, moving here, or leaving.
    private long _lastPeerMouseTicks;

    /// <summary>The keyboard follows the mouse used last. A mouse on this PC (2026-09-29: the user plugged one
    /// in and could no longer type on this PC) keeps the keys here from its first movement; the other PC's
    /// mouse, whose life at home this PC cannot see, takes them back with its next visit here. Neither used
    /// yet: the other PC's, which is what the setting is for.</summary>
    private bool PeerMouseUsedLast() => _lastPeerMouseTicks >= _capture.LastLocalMouseTicks;

    private bool PeerRecentlyInjected()
    {
        long t = _injector.LastInputTicks;
        return t != 0 && Stopwatch.GetTimestamp() - t < Stopwatch.Frequency * 150 / 1000;
    }

    /// <summary>Windows locked or unlocked this session.</summary>
    public void SetLocalLocked(bool locked)
    {
        if (LocalLocked == locked) return;
        LocalLocked = locked;
        Log.Info(locked ? "session locked" : "session unlocked");
        if (locked)
        {
            _capture.Release();
            _injector.Deactivate();
        }
        if (Authenticated) SendStatus();
        StateChanged?.Invoke();
    }

    private void SendStatus()
    {
        // bits 2-3: this PC's Bluetooth verdict (BleHealth), so the other PC can say which PC needs the cure
        byte flags = (byte)((LocalLocked ? 1 : 0) | (_powerSaving ? 2 : 0) | ((int)Transport.BleHealth.Current << 2));
        Send(new Frame(FrameType.Status, [flags]));
    }

    /// <summary>Ctrl+Alt+J: jump to the middle of the other PC's first screen without touching an edge.</summary>
    private void Jump()
    {
        var layout = _capture.Layout;
        if (layout.Peer.Length == 0) return;
        var m = layout.Peer.MinBy(p => p.Number == 0 ? int.MaxValue : p.Number).Bounds;
        var center = new Point(m.X + m.Width / 2, m.Y + m.Height / 2);
        _capture.EnterAt(new Crossing(Dir.Right, center));
    }

    /// <summary>Everything worth pasting into a bug report; the passphrase is masked.</summary>
    public string Diagnostics(string adapterInfo)
    {
        var layout = _capture.Layout;
        var sb = new StringBuilder();
        sb.AppendLine($"Glissando {Program.VersionLong} on {Environment.MachineName}, {Environment.OSVersion}, {DateTime.Now:yyyy-MM-dd HH:mm:ss}; peer build: {(string.IsNullOrEmpty(PeerVersion) ? "unknown" : PeerVersion)}");
        sb.AppendLine($"Bluetooth: {adapterInfo}");
        sb.AppendLine($"Transport: {TransportStatus}; linked={Linked} role={(Linked ? (_channel.IsCentral ? "central" : "peripheral") : "-")} authenticated={Authenticated} peer='{PeerName}' rtt={RttMs:F0} ms");
        sb.AppendLine($"Bluetooth health: this PC {Transport.BleHealth.Describe(Transport.BleHealth.Current)}{(Transport.BleHealth.LastSign != "" ? $" (last sign: {Transport.BleHealth.LastSign})" : "")}; {PeerName} {Transport.BleHealth.Describe(PeerBluetooth)}");
        sb.AppendLine($"State: controlling={Controlling} controlled={Controlled} keysForwarded={_capture.ForwardKeys} locked={Locked} sessionLocked={LocalLocked} peerLocked={PeerLocked} powerSaving={_powerSaving} peerPowerSaving={PeerPowerSaving} idle={Native.IdleMs() / 1000} s");
        sb.AppendLine($"Counters: movesForwarded={Interlocked.Read(ref _capture.MovesForwarded)} rejectedPackets={Rejected} queued={Volatile.Read(ref _queued)} dedupSeen={_dedupSeen.Count}");
        sb.AppendLine(_udp == null ? "UDP pixel path: off" : $"UDP pixel path: port {_udp.LocalPort} up={_udp.Up} peer={_udp.Peer?.ToString() ?? "-"} sent={_udp.SentBytes / 1024} kB received={_udp.ReceivedBytes / 1024} kB");
        sb.AppendLine($"Settings: ble={_s.Ble} capture={_s.CaptureEnabled} clipboard={_s.SyncClipboard} hideCursor={_s.HideCursorWhileControlling} powerSave={_s.PowerSaveWhenIdle} keepalive={_s.KeepaliveMs} timeout={_s.PeerTimeoutMs} passphrase={(string.IsNullOrEmpty(_s.Passphrase) ? "(empty)" : $"set, {_s.Passphrase.Length} chars")} placementUpdated={_s.PlacementUpdated}");
        sb.AppendLine($"Layout: peerOrigin={layout.PeerOrigin} virtualScreen={Layout.MyVirtualScreen()}");
        foreach (var m in layout.Mine) sb.AppendLine($"  mine #{m.Number}: {m.Bounds}");
        foreach (var m in layout.Peer) sb.AppendLine($"  peer #{m.Number}: {m.Bounds}");
        foreach (var s in layout.CrossingSegments()) sb.AppendLine($"  crossing: {s.Side}@{s.Fixed} {s.From}-{s.To}");
        sb.AppendLine("Log tail:");
        try
        {
            foreach (var line in File.ReadLines(Log.FilePath).TakeLast(60)) sb.AppendLine("  " + line);
        }
        catch (Exception ex) { sb.AppendLine("  (log unreadable: " + ex.Message + ")"); }
        return sb.ToString();
    }

    public void SetLocked(bool locked)
    {
        if (Locked == locked) return;
        Locked = locked;
        if (locked) _capture.Release();
        Log.Info(locked ? "cursor locked to this PC" : "cursor unlocked");
        StateChanged?.Invoke();
    }

    /// <summary>The machine is about to sleep: release the peer and the radio cleanly (see IChannel.Sleep).
    /// Must be quick - Windows gives suspend handlers a couple of seconds.</summary>
    public void Suspend()
    {
        Log.Info("system suspending; releasing the link and the radio");
        SayGoodbye();
        DrainSendQueue(300);
        SetAuthenticated(false);
        RttMs = 0;
        try { _channel.Sleep(); } catch (Exception ex) { Log.Warn("sleep: " + ex.Message); }
        StateChanged?.Invoke();
    }

    /// <summary>The machine woke up: Bluetooth state is suspect, so start the transport over and re-establish.</summary>
    public void Resume()
    {
        Log.Info("system resumed; re-establishing the link");
        SetAuthenticated(false);
        RttMs = 0;
        try { _channel.Wake(); } catch (Exception ex) { Log.Warn("wake: " + ex.Message); }
        StateChanged?.Invoke();
        _ = ResumeRecoveryAsync();
    }

    /// <summary>The Bluetooth radio can take several seconds to be usable after wake, and the peer is waking
    /// too, so a single reconnect races the radio and the two never find each other. Nudge the link every few
    /// seconds for about half a minute, until it is back.</summary>
    private async Task ResumeRecoveryAsync()
    {
        // The transport is starting from scratch after Wake(); give that 8 s before nudging it.
        try { await Task.Delay(8000, _cts.Token); } catch (OperationCanceledException) { return; }
        for (int i = 0; i < 12; i++)
        {
            if (_cts.IsCancellationRequested || (Linked && Authenticated)) return;
            // Only nudge a link that is DOWN. Nudging a link that was up but not yet authenticated restarted the
            // peripheral's advertising every 3 s, which kicked the central's fresh subscription each time and
            // kept the handshake from ever finishing after a wake (2026-09-15). A stuck-but-linked handshake is
            // the timer loop's job (15 s rule).
            if (!Linked) _channel.Reconnect();
            try { await Task.Delay(3000, _cts.Token); } catch (OperationCanceledException) { return; }
        }
    }

    public void Start()
    {
        Transport.BleHealth.Changed += () => { if (Authenticated) SendStatus(); StateChanged?.Invoke(); };
        ReconcilePlacement("start");
        _channel.Received += OnReceived;
        _channel.LinkChanged += OnLinkChanged;
        _channel.StatusChanged += st => { TransportStatus = st; StateChanged?.Invoke(); };

        _capture.Output += Send;
        _capture.EnteredRemote += () =>
        {
            if (_s.HideCursorWhileControlling) CursorHider.Hide();
            StateChanged?.Invoke();
            // Off this thread: reading the clipboard waits for the UI thread, and this may be the input-hook thread.
            if (_s.SyncClipboard) Task.Run(SendClipboard);
        };
        _capture.LeftRemote += () => { CursorHider.Restore(); StateChanged?.Invoke(); };
        _capture.LockToggleRequested += () => SetLocked(!Locked);
        _capture.JumpRequested += Jump;

        _injector.StateChanged += () =>
        {
            _capture.BeingControlled = _injector.Active;
            if (_injector.Active) _capture.SuppressTakeBack(300); // ignore the cursor-placement settle
            // The other PC's mouse has come over: its cursor is the one shown here now, so a mirror's hiding of
            // it must end, or the user drives an invisible pointer (2026-10-05).
            if (_injector.Active) CursorHider.Restore(CursorHider.Mirror);
            if (_injector.Active) Mirror.WindowHider.SetClickThrough(true);   // otherwise RelaxIfIdle sees to it
            if (_injector.Active) Mirror.WindowHider.StepAside();
            StateChanged?.Invoke();
        };
        _capture.LocalInputWhileControlled += () =>
        {
            if (!_injector.Active) return;
            Log.Info($"local input on this PC while controlled by {PeerName}; taking control back");
            // Not here, on the input-hook thread: releasing keys is SendInput, which waits for this very hook.
            Task.Run(_injector.Deactivate); // the peer stops via the ControlAlive -> Detached handshake
        };
        // Someone is at this PC: an invisible window in front of them would swallow their clicks, so show it.
        // The mirror goes on; only the hiding ends, until the next mirror is started.
        _capture.LocalActivity += at =>
        {
            // Show a hidden mirrored window again only when the person at this PC actually runs into it: a click
            // where it invisibly is, or typing while it holds the focus. Any click or key at all was too eager -
            // the user sat at this PC working in another window and the mirror never stayed hidden (2026-09-13).
            // Raised on the input-hook thread for every local key and click: decide and reveal off it, since
            // showing a window again (SetWindowPos, a redraw) waits on that window's program.
            if (!Mirroring) return;
            Task.Run(() => RevealIfHit(at));
        };
        void RevealIfHit(Point? at)
        {
            foreach (var m in Mirrors)
            {
                if (!m.Hidden) continue;
                // A click-through window cannot have caught the click: it went to what lies behind.
                bool hit = at is Point p
                    ? Mirror.WindowCapture.VisibleBounds(m.Hwnd).Contains(p) && !Mirror.WindowHider.IsClickThrough(m.Hwnd)
                    : Mirror.WindowCapture.Foreground() == m.Hwnd;
                if (!hit) continue;
                m.Reveal(at is Point q ? $"a click at ({q.X},{q.Y}) on the hidden window" : "a key typed while the hidden window had the focus");
                StateChanged?.Invoke();
                break;   // a click lands on one window only, the top one
            }
        }
        _injector.InputWhileInactive += () =>
        {
            // Give a late Enter 300 ms to show up before declaring the two sides out of step.
            if (Interlocked.Exchange(ref _detachPending, 1) == 1) return;
            _ = Task.Delay(300).ContinueWith(_ =>
            {
                Interlocked.Exchange(ref _detachPending, 0);
                if (_injector.Active || !Authenticated) return;
                Log.Warn("input keeps arriving while not being controlled; telling the other PC to release");
                Send(new Frame(FrameType.Detached, []));
            });
        };
        _injector.ReturnEdgeHit += back =>
        {
            Send(Frame.ReturnEdge(back));
            if (_s.SyncClipboard) SendClipboard();
        };

        _ = Task.Run(() => _channel.RunAsync(_cts.Token));
        _ = Task.Run(SenderLoop);
        _ = Task.Run(TimerLoop);
    }

    public void Reconnect() => _channel.Reconnect();

    /// <summary>Settings were edited locally: rebuild the layout and tell the peer.</summary>
    public void ApplySettings()
    {
        RebuildLayout();
        _s.RememberPlacement(_setupKey);
        try { _s.Save(); } catch (Exception ex) { Log.Warn("save settings: " + ex.Message); }
        if (Linked) SendHello();
        StateChanged?.Invoke();
    }

    /// <summary>Monitors were added or removed on this PC.</summary>
    public void ScreenChanged()
    {
        RebuildLayout();
        ReconcilePlacement("displays changed");
        _capture.ScreenChanged();
        _injector.ScreenChanged();
        if (Linked) SendHello();
    }

    private void RebuildLayout()
    {
        var layout = Layout.FromSettings(_s);
        _capture.Layout = layout;
        _injector.Layout = layout;
    }

    // ---- link / handshake ------------------------------------------------------

    private void OnLinkChanged(bool up)
    {
        Log.Info(up ? $"link up ({(_channel.IsCentral ? "central" : "peripheral")})" : "link down");
        RttMs = 0;
        _linkedSince = Stopwatch.GetTimestamp();
        _noAuthExplained = false;
        // A fresh connection restarts the reliable stream on both sides.
        ResetReliable();
        // Without the link no mirror can drive the cursor here, so nothing may keep it hidden.
        if (!up) CursorHider.Restore(CursorHider.Mirror);
        if (up)
        {
            var old = _crypto;
            _crypto = new Crypto(_s.Passphrase, _channel.IsCentral);
            old?.Dispose();
            var oldU = _cryptoUdp;
            _cryptoUdp = new Crypto(_s.Passphrase, _channel.IsCentral, channel: 1);
            oldU?.Dispose();
            _lastPeer = Stopwatch.GetTimestamp();
            SendHello();
        }
        else
        {
            SetAuthenticated(false);
        }
        StateChanged?.Invoke();
    }

    private void SendHello()
    {
        RandomNumberGenerator.Fill(_myNonce);
        var layout = _capture.Layout;
        var hello = Frame.Hello(new HelloInfo(_s.CaptureEnabled, layout.PeerOrigin, layout.MyBounds.Size, _s.PlacementUpdated, _myNonce, layout.Mine, Environment.MachineName, Program.Version,
            (ushort)(_udp?.LocalPort ?? 0), _udp != null ? UdpPixelLink.LocalAddresses() : null,
            _s.Placements.TryGetValue(_setupKey, out var desk) ? desk.Name : "", _s.Placements.TryGetValue(_setupKey, out var desk2) ? desk2.NameUpdated : 0));
        // The monitor layout travels in Hello. A display or settings change mid-session sends one Hello, and on
        // this lossy link a single packet can be lost, leaving the peer with a stale arrangement -- the two
        // dialogs then disagree and a corner monitor becomes unreachable. Send a few copies so the update
        // survives loss; adopting the same Hello twice is idempotent and the extra HelloAcks are harmless.
        for (int i = 0; i < Redundancy; i++) Enqueue(hello);
        _lastHello = Stopwatch.GetTimestamp();
    }

    private void SetAuthenticated(bool v)
    {
        bool changed = Authenticated != v;
        // Flip the flag first so the mouse hook cannot re-enter remote mode while we are releasing it.
        Authenticated = v;
        if (!v)
        {
            _capture.Release();
            _injector.Deactivate();
        }
        if (!changed) return;
        Log.Info(v ? $"peer authenticated: {PeerName}" : "peer lost");
        if (v) { _lastPong = Stopwatch.GetTimestamp(); _pingsSincePong = 0; } // the "does it hear us" clock starts now
        if (v) SendStatus(); // the reliable stream is reset on link change, not on re-auth over the same link
        else { PeerLocked = false; PeerPowerSaving = false; }
        StateChanged?.Invoke();
    }

    private readonly object _rxGate = new();
    private long _rejected;

    private void OnReceived(byte[] packet)
    {
        _lastRawRx = Stopwatch.GetTimestamp();
        // Apply frames one at a time, in arrival order. BLE may deliver a little out of order: the crypto
        // sliding window accepts that without calling it a replay, a reordered mouse delta is invisible,
        // and the one ordering that matters -- an Enter arriving after the moves it precedes -- is handled
        // by the 300 ms delay before Detached. An earlier reorder buffer here stalled the mouse whenever a
        // packet was actually lost (not just reordered), which BLE does often, and made motion jumpy.
        long t0 = Stopwatch.GetTimestamp();
        lock (_rxGate) Dispatch(packet);
        double ms = (Stopwatch.GetTimestamp() - t0) * 1000.0 / Stopwatch.Frequency;
        lock (_relGate)
        {
            _rxN++; _rxMs += ms;
            if (ms > _rxMaxMs) _rxMaxMs = ms;
            if (ms > 50) _rxSlow++;
        }
    }

    /// <summary>Packets that failed decryption or the replay window (diagnostics).</summary>
    public long Rejected => Interlocked.Read(ref _rejected);

    private void Dispatch(byte[] packet)
    {
        var crypto = _crypto;
        if (crypto == null) return;
        if (!crypto.Open(packet, out var plain))
        {
            long n = Interlocked.Increment(ref _rejected);
            if (n <= 5 || n % 100 == 0) Log.Warn($"rejected packet #{n} ({packet.Length} bytes): bad passphrase, replay or corruption");
            return;
        }
        if (!Frame.TryDecode(plain, out var f)) return;
        _lastPeer = Stopwatch.GetTimestamp();
        if (IsDuplicate(f)) return; // a redundant copy of a key/button/control frame already applied
        HandleApp(f);
    }

    /// <summary>Acts on a received frame.</summary>
    private void HandleApp(Frame f)
    {
        if (f.Type is FrameType.Enter or FrameType.Leave or FrameType.ReturnEdge or FrameType.MouseMove or FrameType.MouseButton or FrameType.MouseWheel)
            _lastPeerMouseTicks = Stopwatch.GetTimestamp();
        switch (f.Type)
        {
            case FrameType.Hello:
                var hello = HelloInfo.Parse(f);
                if (hello == null)
                {
                    Log.Warn("hello with another protocol version; both PCs need the same Glissando version");
                    TransportStatus = "other PC runs another version";
                    StateChanged?.Invoke();
                    return;
                }
                PeerName = hello.Name;
                PeerVersion = hello.AppVersion;
                // Hello now also comes once a minute as a refresh; log it only when it says something new.
                string summary = $"hello from {hello.Name} [{hello.AppVersion}]: capture={hello.CaptureEnabled} ourOriginThere={hello.PeerOrigin} screen={hello.Screen.Width}x{hello.Screen.Height} monitors={hello.Monitors.Length} updated={hello.PlacementUpdated}";
                if (summary != _lastHelloSummary)
                {
                    _lastHelloSummary = summary;
                    Log.Info(summary);
                    if (hello.AppVersion != Program.Version) Log.Warn($"build mismatch: this PC {Program.Version}, {hello.Name} {hello.AppVersion}");
                }
                AdoptPeerInfo(hello);
                if (_udp != null && hello.UdpPort != 0 && hello.UdpAddresses is { Length: > 0 } ua) _udp.SetCandidates(ua, hello.UdpPort);
                // A Hello with a new nonce is a new SendHello on the peer: a restart, a re-auth, a settings or
                // display change. Start its reliable stream afresh. A restarted peer numbers its frames from the
                // start again, and if this side never saw the link drop (2026-09-23: the peripheral did not) the
                // old window swallowed the new frames as duplicates: keys went missing, a Shift release among
                // them, and Enter and Detached never landed, so control stuck for as long as the numbers overlapped.
                // Resetting on a mere settings change costs at most one late duplicate of a key or click.
                if (_peerNonce != null && !hello.Nonce.AsSpan(0, 16).SequenceEqual(_peerNonce)) ResetReliable();
                _peerNonce = hello.Nonce.AsSpan(0, 16).ToArray();
                Enqueue(Frame.HelloAck(hello.Nonce));
                if (!Authenticated && Stopwatch.GetTimestamp() - _lastHello > Stopwatch.Frequency) SendHello();
                break;

            case FrameType.HelloAck:
                if (f.Payload.Length >= 16 && CryptographicOperations.FixedTimeEquals(f.Payload.AsSpan(0, 16), _myNonce))
                    SetAuthenticated(true);
                break;

            case FrameType.Ping:
                Enqueue(Frame.Pong(f.Payload));
                break;

            case FrameType.Pong:
                _lastPong = Stopwatch.GetTimestamp();
                _pingsSincePong = 0;
                if (f.Payload.Length >= 8)
                {
                    double rtt = (Stopwatch.GetTimestamp() - f.I64(0)) * 1000.0 / Stopwatch.Frequency;
                    if (rtt >= 0 && rtt < 10_000)
                    {
                        // Light smoothing so the tray text does not flicker.
                        RttMs = RttMs == 0 ? rtt : RttMs * 0.7 + rtt * 0.3;
                        StateChanged?.Invoke();
                    }
                }
                break;

            case FrameType.ReturnEdge:
                if (Authenticated && f.ReadCrossing() is Crossing back) _capture.OnReturnEdge(back);
                break;

            case FrameType.Clipboard:
                if (Authenticated) ReceiveClipboard(f);
                break;

            case FrameType.ClipboardImage:
                if (Authenticated) ReceiveClipboardImage(f);
                break;

            case FrameType.Bye:
                // The peer is exiting or suspending. The central must release the LE link at once: a link it
                // keeps holding survives the peer's GATT server by 30 s in Windows, and a new connection to the
                // peer's next process cannot be made until it is gone (37 s restarts, 2026-09-15). The
                // peripheral has nothing to release; the central's Windows drops the link when its process ends.
                Log.Info($"{PeerName} says goodbye (exiting or sleeping)");
                SetAuthenticated(false);
                // What arrived before the goodbye proves nothing about the link from now on: without this the
                // "packets flow both ways" rule kept a link to a process that had just exited (2026-10-06).
                _lastRawRx = 0;
                if (_channel.IsCentral) _channel.Reconnect();
                break;

            case FrameType.Detached:
                if (_capture.Remote)
                {
                    Log.Warn($"{PeerName} says it is not being controlled; releasing");
                    _capture.Release();
                }
                break;

            case FrameType.ControlAlive:
                if (!Authenticated) break;
                if (_injector.Active) _injector.KeepAlive();
                // The other PC thinks it is controlling us, but we are not being controlled (its Leave or
                // our ReturnEdge was lost). Tell it to release, so the two sides cannot deadlock with one
                // stuck "controlling" and thereby blocking the other direction.
                else
                {
                    long nowT = Stopwatch.GetTimestamp();
                    if (nowT - _lastDetachLog > Stopwatch.Frequency) { _lastDetachLog = nowT; Log.Info("got ControlAlive but our injector is inactive; sending Detached"); }
                    Send(new Frame(FrameType.Detached, []));
                }
                break;

            case FrameType.Status:
                if (f.Payload.Length >= 1)
                {
                    bool wasLocked = PeerLocked;
                    PeerLocked = (f.Payload[0] & 1) != 0;
                    PeerPowerSaving = (f.Payload[0] & 2) != 0;
                    var peerBt = (Transport.BleHealth.Verdict)((f.Payload[0] >> 2) & 3);
                    if (peerBt != PeerBluetooth) Log.Info($"{PeerName} says its Bluetooth: {Transport.BleHealth.Describe(peerBt)}");
                    PeerBluetooth = peerBt;
                    _peerBluetoothAt = Environment.TickCount64;
                    if (PeerLocked && !wasLocked)
                    {
                        Log.Info($"{PeerName} locked its session");
                        _capture.Release();
                    }
                    else if (!PeerLocked && wasLocked) Log.Info($"{PeerName} unlocked its session");
                    StateChanged?.Invoke();
                }
                break;

            case FrameType.Enter:
                if (!Authenticated) break;
                if (_capture.Remote) _capture.Release(); // cannot control each other at once
                _injector.Apply(f);
                break;

            case FrameType.TypedKey:
                // The other PC's keyboard follows the cursor, and the cursor is here.
                if (Authenticated) _injector.ApplyTypedKey(f);
                break;

            case FrameType.MirrorStop:
                // The viewer closed the mirror's window: stop sending it, or the next tile opens it again.
                if (Authenticated)
                {
                    Mirror.MirrorSender? closed;
                    lock (_mirrorGate) _mirrors.TryGetValue(Frame.MirrorIdOf(f), out closed);
                    if (closed != null) { Log.Info($"mirror #{closed.Id}: closed on {PeerName}"); StopMirror(closed.Hwnd); }
                }
                break;

            case FrameType.MirrorPointer:
            case FrameType.MirrorMouse:
            case FrameType.MirrorKey:
            case FrameType.MirrorResize:
                // The viewer interacting with the window we mirror: apply it to that window here.
                if (Authenticated)
                {
                    Mirror.MirrorSender? target;
                    lock (_mirrorGate) _mirrors.TryGetValue(Frame.MirrorIdOf(f), out target);
                    target?.Input(f);
                }
                break;

            case FrameType.MirrorBegin:
            case FrameType.MirrorChunk:
            case FrameType.MirrorEnd:
                if (Authenticated) MirrorReceived?.Invoke(f);
                break;

            default:
                if (Authenticated) _injector.Apply(f);
                break;
        }
    }

    /// <summary>Take the peer's monitors, and its placement if it was edited more recently than ours.</summary>
    private void AdoptPeerInfo(HelloInfo hello)
    {
        bool changed = false;
        if (hello.Monitors.Length > 0 && !hello.Monitors.SequenceEqual(_s.OtherMonitorList))
        {
            _s.OtherMonitorList = hello.Monitors;
            changed = true;
        }
        if (!hello.Screen.IsEmpty && hello.Screen != _s.OtherScreen)
        {
            _s.OtherScreen = hello.Screen;
            changed = true;
        }
        // The peer's monitors (or its name) may make this a setup we remember: recall before comparing notes.
        ReconcilePlacement("hello");
        // The peer tells us where OUR origin sits in ITS pixels; ours is the mirror image.
        var theirView = new Point(-hello.PeerOrigin.X, -hello.PeerOrigin.Y);
        var myView = _capture.Layout.PeerOrigin;
        bool newer = hello.PlacementUpdated > _s.PlacementUpdated;
        // Nobody has saved a placement yet and the two defaults disagree (both say "the other PC is to my
        // right"): let the PC with the greater name adopt the other's view, so the pair is at least consistent
        // and the cursor can come back. Saving in the settings dialog on either PC replaces this.
        bool tieBreak = !newer && hello.PlacementUpdated == _s.PlacementUpdated && theirView != myView
            && string.CompareOrdinal(Environment.MachineName, hello.Name) > 0;
        if (newer || tieBreak)
        {
            Log.Info($"adopting placement from {hello.Name}{(tieBreak ? " (tie-break)" : "")}: peer origin at {theirView}");
            _s.PeerX = theirView.X;
            _s.PeerY = theirView.Y;
            _s.PlacementUpdated = hello.PlacementUpdated;
            _s.RememberPlacement(_setupKey);
            changed = true;
        }
        // The desk's name travels too: whoever named (or renamed) it last wins, so both PCs call it the same.
        if (hello.DeskName != "" && Layout.IsCompleteSetupKey(_setupKey))
        {
            if (!_s.Placements.ContainsKey(_setupKey)) _s.RememberPlacement(_setupKey);
            if (_s.Placements.TryGetValue(_setupKey, out var mine) && hello.DeskNameUpdated > mine.NameUpdated && hello.DeskName != mine.Name)
            {
                Log.Info($"desk name from {hello.Name}: \"{hello.DeskName}\"" + (mine.Name != "" ? $" (was \"{mine.Name}\")" : ""));
                mine.Name = hello.DeskName;
                mine.NameUpdated = hello.DeskNameUpdated;
                changed = true;
            }
        }
        if (!changed) return;
        RebuildLayout();
        try { _s.Save(); } catch (Exception ex) { Log.Warn("save settings: " + ex.Message); }
        SettingsChanged?.Invoke();
    }

    // ---- sending -----------------------------------------------------------------

    // --- lightweight redundancy for the frames that must not be lost -----------------
    // Keys, buttons and the control frames must survive a dropped packet, but a strict ordered/acked stream
    // head-of-line-blocks and storms retransmits on this lossy link. Instead, send each such frame a few times
    // with a small sequence id and drop duplicates on the far side. No ordering guarantee, no retransmit
    // buildup. Mouse moves and clipboard stay best-effort (a lost move is invisible, a lost clipboard just
    // means that one sync did not take).
    private const int Redundancy = 3;
    private readonly object _relGate = new();
    // Starts somewhere random: a restart that numbered from 0 again ran straight into the window of numbers the
    // peer had already seen from the previous run, whenever the peer had not noticed the link drop.
    private ushort _relSeq = (ushort)Random.Shared.Next(65536);
    private readonly Queue<ushort> _dedupOrder = new();
    private readonly HashSet<ushort> _dedupSeen = [];

    private static bool IsReliableType(FrameType t) => t is FrameType.Key or FrameType.TypedKey or FrameType.MouseButton
        or FrameType.Enter or FrameType.Leave or FrameType.ReturnEdge or FrameType.Detached or FrameType.Status
        or FrameType.MirrorMouse or FrameType.MirrorKey    // a click or key into a mirror must land; pointer moves may not
        or FrameType.MirrorStop or FrameType.MirrorResize;

    /// <summary>Send a frame; the ones that must not be lost go several times with a dedup id appended.</summary>
    private void Send(Frame f)
    {
        if (!IsReliableType(f.Type)) { Enqueue(f); return; }
        ushort seq;
        lock (_relGate) seq = ++_relSeq;
        var p = new byte[f.Payload.Length + 2];
        f.Payload.CopyTo(p, 0);
        System.Buffers.Binary.BinaryPrimitives.WriteUInt16LittleEndian(p.AsSpan(f.Payload.Length), seq);
        var rel = new Frame(f.Type, p);
        // The copies guard against a packet lost on a quiet link. Behind a backlog nothing is lost, only late,
        // and three copies of every key made a busy link three times busier (2026-10-06): one copy then.
        int copies = Volatile.Read(ref _queued) > 8 ? 1 : Redundancy;
        for (int i = 0; i < copies; i++) Enqueue(rel);
    }

    /// <summary>Reliable frames carry a trailing 2-byte id; a copy we have already applied is dropped.
    /// The app reads only fixed low offsets of the payload, so the trailing id is ignored there.</summary>
    private bool IsDuplicate(Frame f)
    {
        if (!IsReliableType(f.Type) || f.Payload.Length < 2) return false;
        ushort seq = System.Buffers.Binary.BinaryPrimitives.ReadUInt16LittleEndian(f.Payload.AsSpan(f.Payload.Length - 2));
        lock (_relGate)
        {
            if (!_dedupSeen.Add(seq)) return true;
            _dedupOrder.Enqueue(seq);
            if (_dedupOrder.Count > 1024) _dedupSeen.Remove(_dedupOrder.Dequeue());
        }
        return false;
    }

    private void ResetReliable()
    {
        lock (_relGate) { _dedupSeen.Clear(); _dedupOrder.Clear(); }
    }

    private void Enqueue(Frame f)
    {
        // Drop mouse moves rather than let a stalled link build a long queue.
        if (f.Type == FrameType.MouseMove && Volatile.Read(ref _queued) > 64) return;
        if (f.Type == FrameType.MirrorPointer && Volatile.Read(ref _queued) > 64) return;
        // Mirror chunks are bulky and best-effort: drop them under a backlog so keyboard and mouse, which the
        // link must deliver promptly, never queue behind a window frame. The next keyframe repaints the gap.
        if (f.Type == FrameType.MirrorChunk && Volatile.Read(ref _queued) > 200) return;
        Interlocked.Increment(ref _queued);
        _queue.Writer.TryWrite(f);
    }

    private async Task SenderLoop()
    {
        var reader = _queue.Reader;
        try
        {
            while (await reader.WaitToReadAsync(_cts.Token))
            {
                while (reader.TryRead(out var f))
                {
                    Interlocked.Decrement(ref _queued);
                    if (f.Type == FrameType.MouseMove)
                    {
                        // Hold the move until the pacing gap has passed; moves that arrive meanwhile join it below.
                        double sinceMove = (Stopwatch.GetTimestamp() - _lastMoveSend) * 1000.0 / Stopwatch.Frequency;
                        int wait = MovePacing.GapMs(RttMs) - (int)sinceMove;
                        if (wait > 0) await Task.Delay(wait, _cts.Token);
                        int dx = f.I16(0), dy = f.I16(2);
                        while (reader.TryPeek(out var next) && next.Type == FrameType.MouseMove && reader.TryRead(out next))
                        {
                            Interlocked.Decrement(ref _queued);
                            dx += next.I16(0);
                            dy += next.I16(2);
                        }
                        f = Frame.MouseMove(dx, dy);
                        _lastMoveSend = Stopwatch.GetTimestamp();
                    }
                    var crypto = _crypto;
                    if (crypto == null || !_channel.IsLinked) continue;
                    var packet = crypto.Seal(f.Encode());
                    _lastSend = Stopwatch.GetTimestamp();
                    int q = Volatile.Read(ref _queued);
                    bool ok = await _channel.SendAsync(packet);
                    long done = Stopwatch.GetTimestamp();
                    if (ok) _lastSendOk = done;
                    double ms = (done - _lastSend) * 1000.0 / Stopwatch.Frequency;
                    lock (_relGate)
                    {
                        if (f.Type == FrameType.MouseMove) _stMoves++;
                        else if (f.Type is FrameType.Key or FrameType.TypedKey or FrameType.MouseButton) _stKeys++;
                        else _stOther++;
                        if (!ok) _stFailed++;
                        _stBytes += packet.Length;
                        _stSendMs += ms;
                        if (ms > _stMaxSendMs) _stMaxSendMs = ms;
                        if (q > _stMaxQueued) _stMaxQueued = q;
                    }
                }
            }
        }
        catch (OperationCanceledException) { }
        catch (Exception ex) { Log.Error("sender loop: " + ex); }
    }

    private async Task TimerLoop()
    {
        try
        {
            while (!_cts.IsCancellationRequested)
            {
                await Task.Delay(250, _cts.Token);
                // A mirror hides this PC's cursor while it drives it over the hidden window (MirrorTarget.Place);
                // the moment this PC's own mouse moves, someone is here and needs to see where it is. So must the
                // other PC's mouse driving this one (see the injector's StateChanged). Before the link check: with
                // the link down the cursor stayed blank for minutes under a moving mouse (2026-10-05).
                long now = Stopwatch.GetTimestamp();
                if (_injector.Active || (!_capture.Remote && now - _capture.LastLocalMouseTicks < Stopwatch.Frequency / 2)) CursorHider.Restore(CursorHider.Mirror);
                if (!_channel.IsLinked) continue;
                if (now - _stSince > Stopwatch.Frequency * 2)
                {
                    lock (_relGate)
                    {
                        int n = _stMoves + _stKeys + _stOther;
                        double secs = (now - _stSince) / (double)Stopwatch.Frequency;
                        var (calls, sendAvg, sendMax) = Native.TakeSendStats();
                        if (_injector.Active && _rxN > 0)
                            Log.Info($"rx while controlled: {_rxN / secs:F0} frames/s; applying {_rxMs / _rxN:F1} ms avg, {_rxMaxMs:F0} ms max, {_rxSlow} over 50 ms; SendInput {calls}x {sendAvg:F1} ms avg {sendMax:F0} ms max; UI thread lag max {Volatile.Read(ref UiLagMaxMs)} ms; rtt {RttMs:F0}");
                        Interlocked.Exchange(ref UiLagMaxMs, 0);
                        _rxN = _rxSlow = 0; _rxMs = _rxMaxMs = 0;
                        if (_capture.Remote && n > 0)
                            Log.Info($"tx while controlling: {n / secs:F0} packets/s ({_stMoves} moves, {_stKeys} keys/buttons, {_stOther} other, {_stFailed} failed), {_stBytes / secs / 1024:F1} kB/s; send {_stSendMs / n:F1} ms avg, {_stMaxSendMs:F0} ms max; queue max {_stMaxQueued}; move gap {MovePacing.GapMs(RttMs)} ms at rtt {RttMs:F0}");
                        _stMoves = _stKeys = _stOther = _stFailed = _stMaxQueued = _stBytes = 0;
                        _stSendMs = _stMaxSendMs = 0;
                        _stSince = now;
                    }
                }

                // Three minutes of an authenticated link: the stack is fine, whatever was reported before.
                if (Authenticated && now - _linkedSince > Stopwatch.Frequency * 180) Transport.BleHealth.LinkHeld();
                double sinceSend = (now - _lastSend) * 1000.0 / Stopwatch.Frequency;
                double sincePeer = (now - _lastPeer) * 1000.0 / Stopwatch.Frequency;
                double sinceHello = (now - _lastHello) * 1000.0 / Stopwatch.Frequency;

                // Power saving: after a long idle stretch, ping rarely so the BLE link can lengthen its
                // connection interval. Tell the peer, so it tolerates the longer silence.
                bool saving = _s.PowerSaveWhenIdle && !Controlling && !Controlled && Native.IdleMs() > (uint)_s.PowerSaveAfterMs;
                if (saving != _powerSaving)
                {
                    _powerSaving = saving;
                    Log.Info(saving ? "idle: slowing keepalive" : "active again: normal keepalive");
                    if (Authenticated) SendStatus();
                }
                int keepalive = saving ? _s.IdleKeepaliveMs : _s.KeepaliveMs;
                int timeout = PeerPowerSaving ? Math.Max(_s.PeerTimeoutMs, _s.IdleKeepaliveMs * 2 + 2000) : _s.PeerTimeoutMs;

                if (Authenticated && sincePeer > timeout)
                {
                    Log.Warn($"no frames from peer for {sincePeer:F0} ms");
                    SetAuthenticated(false);
                }

                // Linked but the handshake never completes: one side is talking to a stale connection (the
                // other PC's process restarted, or it slept and came back with a new GATT server - the peer
                // kept "linked" for minutes after a wake, 2026-09-15). Reconnect, at most every 15 s.
                // But only when the link is actually broken. If packets go out AND come in, the link works and a
                // new connection (for the peripheral: a new GATT server) cannot help -- the handshake fails on
                // something else, a different passphrase, say -- and every fresh server wears this stack down
                // (2026-10-06). Then say what is likely wrong, once, and leave the link alone.
                if (Linked && !Authenticated && now - _linkedSince > Stopwatch.Frequency * 15)
                {
                    bool bothWays = now - _lastSendOk < Stopwatch.Frequency * 5 && now - _lastRawRx < Stopwatch.Frequency * 5;
                    if (bothWays)
                    {
                        if (!_noAuthExplained)
                        {
                            _noAuthExplained = true;
                            Log.Warn("linked for 15 s and packets flow both ways, yet no authentication: the passphrases (or versions) differ? Keeping the link rather than reconnecting");
                        }
                    }
                    else
                    {
                        Log.Warn("linked for 15 s without authenticating, and the link does not carry packets both ways; reconnecting");
                        _linkedSince = now;
                        _channel.Reconnect();
                    }
                }

                // The mirror of the controlled side's rule further down: while we control the other PC it
                // answers our 2 Hz pings, so 1.5 s without a frame means it is gone - exited without a word,
                // crashed, radio lost. Let go of the pointer now, not after the general peer timeout above:
                // stuck on a dead remote screen is the one wait the user cannot do anything about.
                if (_capture.Remote && sincePeer > 1500)
                {
                    Log.Warn($"controlling, but no frames from {PeerName} for {sincePeer:F0} ms; releasing");
                    _capture.Release();
                }

                // A liveness ping every 2 s, whatever else is being sent: its Pong is the only proof that the peer
                // HEARS this PC. Frames arriving from it prove nothing about that. On 2026-10-02 one direction of
                // the link died while the other lived: the peer kept sending (it believed it was controlling this
                // PC and never heard our Detached), this PC's cursor crossed over, was hidden here and moved
                // nothing there, and nothing brought it back, because the peer's frames kept every "peer is
                // gone" check quiet. So: several pings unanswered -> let go of the pointer, and reconnect.
                if (Authenticated && now - _lastLivePing > Stopwatch.Frequency * 2)
                {
                    _lastLivePing = now;
                    _pingsSincePong++;
                    Enqueue(Frame.Ping(now));
                }
                double sincePong = (now - _lastPong) * 1000.0 / Stopwatch.Frequency;
                if (Authenticated && _pingsSincePong >= 2 && sincePong > (_capture.Remote ? 4000 : 8000))
                {
                    if (_capture.Remote)
                    {
                        Log.Warn($"controlling, but {PeerName} has not answered a ping for {sincePong:F0} ms (it does not hear this PC); releasing");
                        _capture.Release();
                    }
                    if (now - _lastOneWayReconnect > Stopwatch.Frequency * 20)
                    {
                        _lastOneWayReconnect = now;
                        Log.Warn($"{PeerName} sends but has not answered a ping for {sincePong:F0} ms: the link carries one direction only; reconnecting");
                        _channel.Reconnect();
                    }
                }

                // While controlling, tell the other PC so it stays active even when the mouse is still.
                if (_capture.Remote) Enqueue(new Frame(FrameType.ControlAlive, []));

                // The UDP pixel path: once a second, knock on the peer's candidates until one answers, then keep
                // the hole open; and let the path lapse after a few seconds of silence. Sealed like any frame,
                // so a stray datagram from elsewhere can never bring it up.
                if (_udp != null && Authenticated && _cryptoUdp is { } cu && now - _lastUdpTick > Stopwatch.Frequency)
                {
                    _lastUdpTick = now;
                    _udp.Tick(cu.Seal(new Frame(FrameType.UdpProbe, []).Encode()));
                }

                // Keep the round trip fresh while control is active, so the diagnostic below samples the live
                // latency rather than the last idle ping (mouse moves keep _lastSend fresh, so the normal
                // keepalive ping rarely fires while controlling). A ping is a few bytes at 2 Hz.
                if ((_capture.Remote || _injector.Active) && now - _lastActivePing > Stopwatch.Frequency / 2)
                {
                    _lastActivePing = now;
                    Enqueue(Frame.Ping(now));
                }

                // Log the round trip, so an intermittent lag on a healthy link is captured with a timestamp:
                // a heartbeat every few seconds, plus an immediate line whenever it jumps, since the spikes
                // are the interesting moments. This is what tells interval drift (RTT climbs and stays) from
                // radio congestion (RTT spikes erratically) without another two-PC session.
                if (Authenticated && RttMs > 0)
                {
                    bool spike = RttMs > 45 && RttMs > _lastRttLogged * 1.6 && now - _lastRttLog > Stopwatch.Frequency / 2;
                    if (spike || now - _lastRttLog > Stopwatch.Frequency * 15)
                    {
                        _lastRttLog = now;
                        _lastRttLogged = RttMs;
                        Log.Info($"rtt {RttMs:F0} ms controlling={_capture.Remote} controlled={_injector.Active}");
                    }
                }

                // If we are being controlled but the controller has gone silent (a lost Leave, or it
                // crashed), release: no input or heartbeat for 1.2 s. The heartbeat above is 4x/s, so a
                // real pause with the controller present does not trip this.
                if (_injector.Active && now - _injector.LastInputTicks > Stopwatch.Frequency * 1200 / 1000)
                {
                    Log.Warn("controller went silent; releasing control locally");
                    _injector.Deactivate();
                }

                // Once a minute, make sure both sides still agree on the screens. A display change can be missed
                // here (Windows' notice arriving while the link was down, or not at all), and the Hello that
                // carries one can be lost; the peer then keeps a stale monitor list, and a crossing works in one
                // direction only (2026-09-24: the peer showed one of this PC's four monitors for hours).
                // Re-enumerate: if it differs, treat it as a display change; else just re-send Hello.
                if (Authenticated && now - _lastPeriodicHello > Stopwatch.Frequency * 60)
                {
                    _lastPeriodicHello = now;
                    var live = Layout.MyMonitors();
                    if (!live.SequenceEqual(_capture.Layout.Mine))
                    {
                        Log.Info($"display change missed: {_capture.Layout.Mine.Length} -> {live.Length} monitors; rebuilding the layout and telling {PeerName}");
                        ScreenChanged();
                    }
                    else SendHello();
                }

                if (!Authenticated && sinceHello > 2000) SendHello();
                else if (sinceSend > keepalive) Enqueue(Frame.Ping(now));
            }
        }
        catch (OperationCanceledException) { }
    }

    // ---- clipboard -------------------------------------------------------------

    private void SendClipboard()
    {
        try
        {
            var text = _clipboard.GetText();
            if (!string.IsNullOrEmpty(text))
            {
                if (text.Length > 60_000) text = text[..60_000];
                // Don't resend an unchanged clipboard on every crossing; a big one clogs the slow link.
                if (text == _lastClipboardSent) return;
                _lastClipboardSent = text;
                byte id = ++_clipId;
                foreach (var chunk in Frame.ClipboardText(id, text)) Enqueue(chunk); // best-effort, not multiplied
                Log.Info($"clipboard sent ({text.Length} chars)");
                return;
            }
            // No text: a picture, then? Win+Shift+S puts only a bitmap on the clipboard (2026-09-24). Over the
            // UDP path any screenshot goes in a moment; over Bluetooth it has to stay small enough to arrive
            // within seconds, so a big one is sent as JPEG and a huge one not at all.
            bool udp = _udp is { Up: true };
            var picture = _clipboard.GetImage(udp ? 8_000_000 : BleImageLimit);
            if (picture == null) return;
            var hash = Convert.ToHexString(SHA256.HashData(picture));
            if (hash == _lastClipboardImage) return;
            _lastClipboardImage = hash;
            byte pid = ++_clipId;
            int chunks = 0;
            foreach (var chunk in Frame.ClipboardImage(pid, picture, udp ? 1200 : 400)) { if (udp) SendUdp(chunk); else Enqueue(chunk); chunks++; }
            Log.Info($"clipboard picture sent ({picture.Length / 1024} kB in {chunks} chunks over {(udp ? "the network" : "Bluetooth")})");
        }
        catch (Exception ex) { Log.Warn("clipboard read: " + ex.Message); }
    }

    private const int BleImageLimit = 300_000;
    private string? _lastClipboardImage;
    private (byte id, byte[]?[] chunks)? _imageIn;

    private void ReceiveClipboardImage(Frame f)
    {
        if (f.Payload.Length < 5) return;
        byte id = f.Payload[0];
        int index = f.U16(1), total = f.U16(3);
        if (total == 0 || index >= total || total > 8000) return;
        lock (_clipIn)
        {
            if (_imageIn is not { } cur || cur.id != id || cur.chunks.Length != total)
                _imageIn = cur = (id, new byte[]?[total]);
            cur.chunks[index] = f.Payload[5..];
            if (cur.chunks.Any(c => c == null)) return;
            _imageIn = null;
            var bytes = cur.chunks.SelectMany(c => c!).ToArray();
            try
            {
                _clipboard.SetImage(bytes);
                _lastClipboardImage = Convert.ToHexString(SHA256.HashData(bytes)); // don't bounce it straight back
                Log.Info($"clipboard picture received ({bytes.Length / 1024} kB)");
            }
            catch (Exception ex) { Log.Warn("clipboard picture write: " + ex.Message); }
        }
    }

    private void ReceiveClipboard(Frame f)
    {
        if (f.Payload.Length < 3) return;
        byte id = f.Payload[0], index = f.Payload[1], total = f.Payload[2];
        if (total == 0 || index >= total) return;
        lock (_clipIn)
        {
            if (!_clipIn.TryGetValue(id, out var entry) || entry.total != total)
            {
                entry = (total, new byte[]?[total]);
                _clipIn[id] = entry;
            }
            entry.chunks[index] = f.Payload[3..];
            if (entry.chunks.Any(c => c == null)) return;
            _clipIn.Remove(id);
            var text = Encoding.UTF8.GetString(entry.chunks.SelectMany(c => c!).ToArray());
            try
            {
                _clipboard.SetText(text);
                _lastClipboardSent = text; // don't bounce it straight back on the return crossing
                Log.Info($"clipboard received ({text.Length} chars)");
            }
            catch (Exception ex) { Log.Warn("clipboard write: " + ex.Message); }
        }
    }

    /// <summary>A Bluetooth problem in words, for the tray: this PC's, or the other PC's as it last told us
    /// (for half an hour). Empty when there is none.</summary>
    public string BluetoothProblem
    {
        get
        {
            var mine = Transport.BleHealth.Current;
            if (mine == Transport.BleHealth.Verdict.RestartWindows) return "Bluetooth on this PC is stuck: restart Windows on this PC";
            if (mine == Transport.BleHealth.Verdict.ToggleBluetooth) return "Bluetooth on this PC is not responding: turn Bluetooth off and on";
            string peer = PeerName != "" ? PeerName : "the other PC";
            if (PeerBluetooth != Transport.BleHealth.Verdict.Fine && Environment.TickCount64 - _peerBluetoothAt < 30 * 60_000)
                return PeerBluetooth == Transport.BleHealth.Verdict.RestartWindows
                    ? $"Bluetooth on {peer} is stuck: restart Windows on {peer}"
                    : $"Bluetooth on {peer} is not responding: turn Bluetooth off and on there";
            return "";
        }
    }

    public string StatusText
    {
        get
        {
            if (!Linked) return $"Looking for the other PC ({TransportStatus})";
            if (!Authenticated) return TransportStatus.StartsWith("other PC runs") ? TransportStatus : "Linked, authenticating (do both passphrases match?)";
            string rtt = RttMs > 0 ? $", {RttMs:F0} ms" : "";
            // Show the peer's build next to its name; flag it when it differs from ours.
            string peer = string.IsNullOrEmpty(PeerVersion) ? PeerName
                : PeerVersion == Program.Version ? $"{PeerName} [{PeerVersion}]"
                : $"{PeerName} [{PeerVersion}, this PC {Program.Version}]";
            if (Controlling) return $"Controlling {peer}{rtt}";
            if (Controlled) return $"{peer} is controlling this PC{rtt}";
            if (LocalLocked) return $"This session is locked{rtt}";
            if (PeerLocked) return $"{peer} is locked; unlock it with its own keyboard{rtt}";
            if (Locked) return $"Cursor locked to this PC (Ctrl+Alt+L unlocks){rtt}";
            if (_capture.ForwardKeys) return $"Linked to {peer}{rtt}; this keyboard types on {PeerName}";
            return _s.CaptureEnabled
                ? $"Ready, linked to {peer}{rtt}"
                : $"Ready, linked to {peer}{rtt} (this PC does not take control)";
        }
    }

    public void Dispose()
    {
        StopAllMirrors();
        // Tell the other PC before the link goes. Being controlled, send Detached so the controller lets go of
        // the pointer at once (its own fallbacks - link down, peer timeout - take seconds, spent staring at a
        // dead remote screen). Controlling, Release() below sends Leave, which frees the controlled PC without
        // its 1.2 s silence rule. Both go through the sender queue, and cancelling that queue is what Dispose
        // did next - so the frames never left. Drain it first, bounded: exiting must never hang on a dead link.
        if (Authenticated && _injector.Active) Send(new Frame(FrameType.Detached, []));
        _capture.Release();
        CursorHider.RestoreAll();
        _injector.Deactivate();
        SayGoodbye();
        DrainSendQueue(400);
        _cts.Cancel();
        _queue.Writer.TryComplete();
        _channel.Dispose();
        _udp?.Dispose();
        _crypto?.Dispose();
        _cryptoUdp?.Dispose();
    }

    /// <summary>Tell an authenticated peer we are going, twice (Bye is not a reliable frame), so it drops the
    /// link now instead of finding out from silence.</summary>
    private void SayGoodbye()
    {
        if (!Authenticated || !Linked) return;
        Send(Frame.Bye());
        Send(Frame.Bye());
    }

    /// <summary>Wait, at most maxMs, for the sender loop to hand every queued frame to the channel. _queued
    /// drops when a frame is taken off the queue, before its SendAsync completes, so a short grace period
    /// follows for the last write in flight.</summary>
    private void DrainSendQueue(int maxMs)
    {
        var sw = Stopwatch.StartNew();
        while (Volatile.Read(ref _queued) > 0 && _channel.IsLinked && sw.ElapsedMilliseconds < maxMs) Thread.Sleep(10);
        if (_channel.IsLinked) Thread.Sleep(80);
    }
}
