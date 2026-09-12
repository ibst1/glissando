using System.Diagnostics;
using System.Net;
using System.Net.NetworkInformation;
using System.Net.Sockets;

namespace Glissando.Transport;

/// <summary>
/// A second, fast path beside BLE for the bulk of window mirroring: UDP over whatever IP network both PCs
/// happen to share (the home Wi-Fi). BLE stays the control plane -- handshake, input, and the pixels when
/// this path is not there -- so nothing depends on it.
///
/// Neither PC may open a firewall port (no administrator), and Windows Firewall drops unsolicited inbound
/// datagrams. But it lets in UDP that answers a flow this PC opened itself, so if BOTH sides start sending
/// to the other's address:port, each firewall sees only replies and both directions open -- the hole
/// punching VoIP and games use, verified between these two PCs on 2026-09-12 (tools\udp-punch-test.ps1).
/// The addresses and port travel in Hello over BLE, so there is nothing to configure. Everything sent here
/// is sealed with the session key like a BLE packet, in a nonce space of its own.
///
/// Up means: a datagram from the peer decrypted within the last few seconds. Probes go once a second to
/// every candidate until one answers; the endpoint that answers is the peer from then on. At work the two
/// networks may not route to each other at all; then this never comes up and BLE carries the pixels as before.
/// </summary>
internal sealed class UdpPixelLink : IDisposable
{
    private const int SilenceDownMs = 3500;

    private readonly UdpClient _udp;
    private readonly CancellationTokenSource _cts = new();
    private readonly object _gate = new();
    private IPEndPoint[] _candidates = [];
    private IPEndPoint? _peer;
    private long _lastRx;
    private long _sentBytes, _recvBytes;

    /// <summary>Raised on the receive thread with a datagram and where it came from.</summary>
    public event Action<byte[], IPEndPoint>? Received;
    /// <summary>Raised when the path comes up or goes down.</summary>
    public event Action<bool>? UpChanged;

    public bool Up { get; private set; }
    public int LocalPort { get; }
    public IPEndPoint? Peer { get { lock (_gate) return _peer; } }
    public long SentBytes => Interlocked.Read(ref _sentBytes);
    public long ReceivedBytes => Interlocked.Read(ref _recvBytes);

    public UdpPixelLink()
    {
        _udp = new UdpClient(new IPEndPoint(IPAddress.Any, 0));
        // Windows reports an ICMP port-unreachable for an earlier send as a ConnectionReset on the next
        // receive; while probing candidates that will happen constantly. Turn it off (SIO_UDP_CONNRESET).
        try { _udp.Client.IOControl(unchecked((int)0x9800000C), [0, 0, 0, 0], null); } catch { }
        LocalPort = ((IPEndPoint)_udp.Client.LocalEndPoint!).Port;
        Task.Run(ReceiveLoop);
    }

    /// <summary>This PC's IPv4 addresses the peer could try: up interfaces, unicast, not loopback or APIPA.</summary>
    public static IPAddress[] LocalAddresses()
    {
        var list = new List<IPAddress>();
        try
        {
            foreach (var nic in NetworkInterface.GetAllNetworkInterfaces())
            {
                if (nic.OperationalStatus != OperationalStatus.Up || nic.NetworkInterfaceType == NetworkInterfaceType.Loopback) continue;
                foreach (var ua in nic.GetIPProperties().UnicastAddresses)
                {
                    var a = ua.Address;
                    if (a.AddressFamily != AddressFamily.InterNetwork) continue;
                    var b = a.GetAddressBytes();
                    if (b[0] == 127 || (b[0] == 169 && b[1] == 254)) continue;
                    list.Add(a);
                }
            }
        }
        catch (Exception ex) { Log.Warn("udp: listing addresses: " + ex.Message); }
        return list.Take(8).ToArray();
    }

    /// <summary>The peer's Hello told us where it listens.</summary>
    public void SetCandidates(IPAddress[] addresses, int port)
    {
        if (port <= 0 || addresses.Length == 0) return;
        var c = addresses.Select(a => new IPEndPoint(a, port)).ToArray();
        lock (_gate)
        {
            if (_candidates.Length == c.Length && _candidates.Zip(c).All(p => p.First.Equals(p.Second))) return;
            _candidates = c;
            if (_peer != null && !c.Any(e => e.Equals(_peer))) { _peer = null; SetUp(false); }
        }
        Log.Info($"udp: peer candidates {string.Join(", ", c.Select(e => e.ToString()))} (we listen on {LocalPort})");
    }

    /// <summary>A datagram from this endpoint decrypted with the session key: it is the peer, and the path is up.</summary>
    public void Confirm(IPEndPoint from)
    {
        Interlocked.Exchange(ref _lastRx, Stopwatch.GetTimestamp());
        bool announce = false;
        lock (_gate)
        {
            if (_peer == null || !_peer.Equals(from)) { _peer = from; announce = true; }
            if (!Up) { SetUp(true); announce = false; Log.Info($"udp: path up via {from}"); }
        }
        if (announce) Log.Info($"udp: peer now answers from {from}");
    }

    /// <summary>Send to the confirmed peer; false when the path is down.</summary>
    public bool Send(byte[] packet)
    {
        IPEndPoint? p; lock (_gate) p = Up ? _peer : null;
        if (p == null) return false;
        try { _udp.Send(packet, packet.Length, p); Interlocked.Add(ref _sentBytes, packet.Length); return true; }
        catch (Exception ex) { Log.Warn("udp send: " + ex.Message); return false; }
    }

    /// <summary>Once a second: keep the hole open toward the peer, or knock on every candidate until one answers;
    /// and drop the path after a few seconds of silence.</summary>
    public void Tick(byte[] probe)
    {
        IPEndPoint[] targets;
        lock (_gate)
        {
            if (Up && Stopwatch.GetTimestamp() - Interlocked.Read(ref _lastRx) > Stopwatch.Frequency * SilenceDownMs / 1000)
            {
                SetUp(false);
                Log.Info("udp: path down (silence)");
            }
            targets = Up && _peer != null ? [_peer] : _candidates;
        }
        foreach (var t in targets)
        {
            try { _udp.Send(probe, probe.Length, t); } catch { }
        }
    }

    private void SetUp(bool v)
    {
        if (Up == v) return;
        Up = v;
        ThreadPool.QueueUserWorkItem(_ => UpChanged?.Invoke(v));
    }

    private async Task ReceiveLoop()
    {
        while (!_cts.IsCancellationRequested)
        {
            try
            {
                var r = await _udp.ReceiveAsync(_cts.Token);
                Interlocked.Add(ref _recvBytes, r.Buffer.Length);
                Received?.Invoke(r.Buffer, r.RemoteEndPoint);
            }
            catch (OperationCanceledException) { return; }
            catch (ObjectDisposedException) { return; }
            catch (Exception ex) { Log.Warn("udp receive: " + ex.Message); await Task.Delay(50); }
        }
    }

    public void Dispose()
    {
        _cts.Cancel();
        try { _udp.Dispose(); } catch { }
    }
}
