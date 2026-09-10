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

    private bool _powerSaving;
    private int _detachPending;
    private long _lastDetachLog;

    private readonly Settings _s;
    private readonly IChannel _channel;
    private readonly InputCapture _capture;
    private readonly InputInjector _injector;
    private readonly IClipboardAccess _clipboard;
    private Crypto? _crypto;
    private readonly Channel<Frame> _queue = Channel.CreateUnbounded<Frame>(new UnboundedChannelOptions { SingleReader = true });
    private readonly CancellationTokenSource _cts = new();
    private readonly byte[] _myNonce = new byte[16];
    private long _lastPeer, _lastSend, _lastHello;
    private long _lastActivePing, _lastRttLog;
    private double _lastRttLogged;
    private int _queued;
    private byte _clipId;
    private string? _lastClipboardSent;
    private readonly Dictionary<byte, (byte total, byte[]?[] chunks)> _clipIn = [];

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
    }

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
        byte flags = (byte)((LocalLocked ? 1 : 0) | (_powerSaving ? 2 : 0));
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
        sb.AppendLine($"State: controlling={Controlling} controlled={Controlled} locked={Locked} sessionLocked={LocalLocked} peerLocked={PeerLocked} powerSaving={_powerSaving} peerPowerSaving={PeerPowerSaving} idle={Native.IdleMs() / 1000} s");
        sb.AppendLine($"Counters: movesForwarded={Interlocked.Read(ref _capture.MovesForwarded)} rejectedPackets={Rejected} queued={Volatile.Read(ref _queued)} dedupSeen={_dedupSeen.Count}");
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

    /// <summary>The machine woke up: Bluetooth state is suspect, so drop and re-establish the link.</summary>
    public void Resume()
    {
        Log.Info("system resumed; re-establishing the link");
        SetAuthenticated(false);
        RttMs = 0;
        _channel.Reconnect();
        StateChanged?.Invoke();
    }

    public void Start()
    {
        _channel.Received += OnReceived;
        _channel.LinkChanged += OnLinkChanged;
        _channel.StatusChanged += st => { TransportStatus = st; StateChanged?.Invoke(); };

        _capture.Output += Send;
        _capture.EnteredRemote += () =>
        {
            if (_s.HideCursorWhileControlling) CursorHider.Hide();
            StateChanged?.Invoke();
            if (_s.SyncClipboard) SendClipboard();
        };
        _capture.LeftRemote += () => { CursorHider.Restore(); StateChanged?.Invoke(); };
        _capture.LockToggleRequested += () => SetLocked(!Locked);
        _capture.JumpRequested += Jump;

        _injector.StateChanged += () =>
        {
            _capture.BeingControlled = _injector.Active;
            if (_injector.Active) _capture.SuppressTakeBack(300); // ignore the cursor-placement settle
            StateChanged?.Invoke();
        };
        _capture.LocalInputWhileControlled += () =>
        {
            if (!_injector.Active) return;
            Log.Info($"local input on this PC while controlled by {PeerName}; taking control back");
            _injector.Deactivate(); // the peer stops via the ControlAlive -> Detached handshake
        };
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
        if (Linked) SendHello();
        StateChanged?.Invoke();
    }

    /// <summary>Monitors were added or removed on this PC.</summary>
    public void ScreenChanged()
    {
        RebuildLayout();
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
        // A fresh connection restarts the reliable stream on both sides.
        ResetReliable();
        if (up)
        {
            var old = _crypto;
            _crypto = new Crypto(_s.Passphrase, _channel.IsCentral);
            old?.Dispose();
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
        var hello = Frame.Hello(new HelloInfo(_s.CaptureEnabled, layout.PeerOrigin, layout.MyBounds.Size, _s.PlacementUpdated, _myNonce, layout.Mine, Environment.MachineName, Program.Version));
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
        if (v) SendStatus(); // the reliable stream is reset on link change, not on re-auth over the same link
        else { PeerLocked = false; PeerPowerSaving = false; }
        StateChanged?.Invoke();
    }

    private readonly object _rxGate = new();
    private long _rejected;

    private void OnReceived(byte[] packet)
    {
        // Apply frames one at a time, in arrival order. BLE may deliver a little out of order: the crypto
        // sliding window accepts that without calling it a replay, a reordered mouse delta is invisible,
        // and the one ordering that matters -- an Enter arriving after the moves it precedes -- is handled
        // by the 300 ms delay before Detached. An earlier reorder buffer here stalled the mouse whenever a
        // packet was actually lost (not just reordered), which BLE does often, and made motion jumpy.
        lock (_rxGate) Dispatch(packet);
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
                Log.Info($"hello from {hello.Name} [{hello.AppVersion}]: capture={hello.CaptureEnabled} ourOriginThere={hello.PeerOrigin} screen={hello.Screen.Width}x{hello.Screen.Height} monitors={hello.Monitors.Length} updated={hello.PlacementUpdated}");
                if (hello.AppVersion != Program.Version) Log.Warn($"build mismatch: this PC {Program.Version}, {hello.Name} {hello.AppVersion}");
                AdoptPeerInfo(hello);
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
            changed = true;
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
    private ushort _relSeq;
    private readonly Queue<ushort> _dedupOrder = new();
    private readonly HashSet<ushort> _dedupSeen = [];

    private static bool IsReliableType(FrameType t) => t is FrameType.Key or FrameType.MouseButton
        or FrameType.Enter or FrameType.Leave or FrameType.ReturnEdge or FrameType.Detached or FrameType.Status;

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
        for (int i = 0; i < Redundancy; i++) Enqueue(rel);
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
                        int dx = f.I16(0), dy = f.I16(2);
                        while (reader.TryPeek(out var next) && next.Type == FrameType.MouseMove && reader.TryRead(out next))
                        {
                            Interlocked.Decrement(ref _queued);
                            dx += next.I16(0);
                            dy += next.I16(2);
                        }
                        f = Frame.MouseMove(dx, dy);
                    }
                    var crypto = _crypto;
                    if (crypto == null || !_channel.IsLinked) continue;
                    var packet = crypto.Seal(f.Encode());
                    _lastSend = Stopwatch.GetTimestamp();
                    await _channel.SendAsync(packet);
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
                if (!_channel.IsLinked) continue;
                long now = Stopwatch.GetTimestamp();
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

                // While controlling, tell the other PC so it stays active even when the mouse is still.
                if (_capture.Remote) Enqueue(new Frame(FrameType.ControlAlive, []));

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
            if (string.IsNullOrEmpty(text)) return;
            if (text.Length > 60_000) text = text[..60_000];
            // Don't resend an unchanged clipboard on every crossing; a big one clogs the slow link.
            if (text == _lastClipboardSent) return;
            _lastClipboardSent = text;
            byte id = ++_clipId;
            foreach (var chunk in Frame.ClipboardText(id, text)) Enqueue(chunk); // best-effort, not multiplied
            Log.Info($"clipboard sent ({text.Length} chars)");
        }
        catch (Exception ex) { Log.Warn("clipboard read: " + ex.Message); }
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
            return _s.CaptureEnabled
                ? $"Ready, linked to {peer}{rtt}"
                : $"Ready, linked to {peer}{rtt} (this PC does not take control)";
        }
    }

    public void Dispose()
    {
        _capture.Release();
        CursorHider.Restore();
        _injector.Deactivate();
        _cts.Cancel();
        _queue.Writer.TryComplete();
        _channel.Dispose();
        _crypto?.Dispose();
    }
}
