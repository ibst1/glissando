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
    public bool Controlling => _capture.Remote;
    public bool Controlled => _injector.Active;

    /// <summary>User asked to keep the cursor on this PC (Ctrl+Alt+L or the tray menu). Not persisted.</summary>
    public bool Locked { get; private set; }

    /// <summary>Latest measured round trip to the peer in milliseconds, 0 until a pong has arrived.</summary>
    public double RttMs { get; private set; }

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
    private int _queued;
    private byte _clipId;
    private readonly Dictionary<byte, (byte total, byte[]?[] chunks)> _clipIn = [];

    public Session(Settings s, IChannel channel, InputCapture capture, InputInjector injector, IClipboardAccess clipboard)
    {
        _s = s;
        _channel = channel;
        _capture = capture;
        _injector = injector;
        _clipboard = clipboard;
        _capture.CanEnter = () => Authenticated && _s.CaptureEnabled && !Locked && !_injector.Active;
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

        _capture.Output += Enqueue;
        _capture.EnteredRemote += () =>
        {
            if (_s.HideCursorWhileControlling) CursorHider.Hide();
            StateChanged?.Invoke();
            if (_s.SyncClipboard) SendClipboard();
        };
        _capture.LeftRemote += () => { CursorHider.Restore(); StateChanged?.Invoke(); };
        _capture.LockToggleRequested += () => SetLocked(!Locked);

        _injector.StateChanged += () => StateChanged?.Invoke();
        _injector.ReturnEdgeHit += back =>
        {
            Enqueue(Frame.ReturnEdge(back));
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
        Enqueue(Frame.Hello(new HelloInfo(_s.CaptureEnabled, layout.PeerOrigin, layout.MyBounds.Size, _s.PlacementUpdated, _myNonce, layout.Mine, Environment.MachineName)));
        _lastHello = Stopwatch.GetTimestamp();
    }

    private void SetAuthenticated(bool v)
    {
        if (!v)
        {
            _capture.Release();
            _injector.Deactivate();
        }
        if (Authenticated == v) return;
        Authenticated = v;
        Log.Info(v ? $"peer authenticated: {PeerName}" : "peer lost");
        StateChanged?.Invoke();
    }

    private void OnReceived(byte[] packet)
    {
        var crypto = _crypto;
        if (crypto == null) return;
        if (!crypto.Open(packet, out var plain))
        {
            Log.Warn($"rejected packet ({packet.Length} bytes): bad passphrase, replay or corruption");
            return;
        }
        if (!Frame.TryDecode(plain, out var f)) return;
        _lastPeer = Stopwatch.GetTimestamp();

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
                Log.Info($"hello from {hello.Name}: capture={hello.CaptureEnabled} ourOriginThere={hello.PeerOrigin} screen={hello.Screen.Width}x{hello.Screen.Height} monitors={hello.Monitors.Length} updated={hello.PlacementUpdated}");
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
        if (hello.PlacementUpdated > _s.PlacementUpdated)
        {
            // The peer tells us where OUR origin sits in ITS pixels; ours is the mirror image.
            var mine = new Point(-hello.PeerOrigin.X, -hello.PeerOrigin.Y);
            Log.Info($"adopting placement from {hello.Name}: peer origin at {mine}");
            _s.PeerX = mine.X;
            _s.PeerY = mine.Y;
            _s.PlacementUpdated = hello.PlacementUpdated;
            changed = true;
        }
        if (!changed) return;
        RebuildLayout();
        try { _s.Save(); } catch (Exception ex) { Log.Warn("save settings: " + ex.Message); }
        SettingsChanged?.Invoke();
    }

    // ---- sending -----------------------------------------------------------------

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

                if (Authenticated && sincePeer > _s.PeerTimeoutMs)
                {
                    Log.Warn($"no frames from peer for {sincePeer:F0} ms");
                    SetAuthenticated(false);
                }
                if (!Authenticated && sinceHello > 2000) SendHello();
                else if (sinceSend > _s.KeepaliveMs) Enqueue(Frame.Ping(now));
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
            byte id = ++_clipId;
            foreach (var chunk in Frame.ClipboardText(id, text)) Enqueue(chunk);
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
            if (Controlling) return $"Controlling {PeerName}{rtt}";
            if (Controlled) return $"{PeerName} is controlling this PC{rtt}";
            if (Locked) return $"Cursor locked to this PC (Ctrl+Alt+L unlocks){rtt}";
            return _s.CaptureEnabled
                ? $"Ready, linked to {PeerName}{rtt}"
                : $"Ready, linked to {PeerName}{rtt} (this PC does not take control)";
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
