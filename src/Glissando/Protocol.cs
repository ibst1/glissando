using System.Buffers.Binary;
using System.Net;
using System.Security.Cryptography;
using System.Text;

namespace Glissando;

public enum FrameType : byte
{
    Hello = 1,       // see HelloInfo
    HelloAck = 2,    // peer nonce(16)
    Ping = 3,
    MouseMove = 4,   // dx(i16) dy(i16), relative device counts
    MouseButton = 5, // button(1: 1=left 2=right 3=middle 4=x1 5=x2) down(1)
    MouseWheel = 6,  // delta(i16) horizontal(1)
    Key = 7,         // vk(u16) scan(u16) flags(1: bit0=up bit1=extended)
    Enter = 8,       // x(i32) y(i32) dir(1): first pixel inside the receiver's monitor, in its pixels, and the travel direction
    Leave = 9,       // controller released control; the controlled PC releases all keys and buttons
    ReturnEdge = 10, // x(i32) y(i32) dir(1): the controlled PC's cursor left toward the controller; target in the controller's pixels
    Clipboard = 11,  // id(1) index(1) total(1) utf8 chunk
    Pong = 12,       // echo of a Ping's payload (sender's timestamp), for round-trip measurement
    Status = 13,     // flags(1): bit0 = this PC's session is locked, bit1 = power saving
    Detached = 14,   // controlled PC got input while not active: the controller should release
    ControlAlive = 15, // sent periodically by the controller while remote; its absence releases the controlled PC
    Reliable = 16,   // seq(u32) + an inner encoded frame; delivered in order, retransmitted until acked
    Ack = 17,        // seq(u32): every reliable frame up to and including this has been delivered
    // Every mirror frame starts with the mirror's id(1), so several windows can be mirrored at once.
    MirrorBegin = 18, // id(1) srcW(u16) srcH(u16) title(UTF-8): window mirroring starts/resizes
    MirrorChunk = 19, // id(1) frameId(u16) index(u16) count(u16) x(u16) y(u16) w(u16) h(u16) keyframe(1) + JPEG chunk
    MirrorEnd = 20,   // id(1): mirroring stopped; the receiver closes that mirror's window
    MirrorPointer = 21, // id(1) x(u16) y(u16): the viewer's pointer over the mirror, in source-window pixels (best effort)
    MirrorMouse = 22,   // id(1) kind(1: 1=down 2=up 3=wheel 4=hwheel) x(u16) y(u16) button(1) delta(i16): a click or wheel on the mirror
    MirrorKey = 23,     // id(1) vk(u16) scan(u16) flags(1: bit0=up bit1=extended): a key typed into the mirror
    UdpProbe = 24,      // empty; sent over the UDP pixel path to punch it through the firewalls and keep it up
    Bye = 25,           // empty; the sender is exiting or going to sleep: drop the link now, do not wait for timeouts
    TypedKey = 26,      // vk(u16) scan(u16) flags(1: bit0=up bit1=extended): a key from the other PC's keyboard that follows the cursor; applied without a control session
    ClipboardImage = 27, // id(1) index(u16) total(u16) + a chunk of a PNG or JPEG: the clipboard's picture (a screenshot), best effort
}

public readonly record struct Frame(FrameType Type, byte[] Payload)
{
    public byte[] Encode()
    {
        var b = new byte[1 + Payload.Length];
        b[0] = (byte)Type;
        Payload.CopyTo(b, 1);
        return b;
    }

    public static bool TryDecode(byte[] plain, out Frame frame)
    {
        frame = default;
        if (plain.Length < 1 || !Enum.IsDefined(typeof(FrameType), plain[0])) return false;
        frame = new Frame((FrameType)plain[0], plain.AsSpan(1).ToArray());
        return true;
    }

    public short I16(int offset) => BinaryPrimitives.ReadInt16LittleEndian(Payload.AsSpan(offset));
    public ushort U16(int offset) => BinaryPrimitives.ReadUInt16LittleEndian(Payload.AsSpan(offset));
    public int I32(int offset) => BinaryPrimitives.ReadInt32LittleEndian(Payload.AsSpan(offset));
    public uint U32(int offset) => BinaryPrimitives.ReadUInt32LittleEndian(Payload.AsSpan(offset));

    public static Frame Ack(uint seq)
    {
        var p = new byte[4];
        BinaryPrimitives.WriteUInt32LittleEndian(p, seq);
        return new(FrameType.Ack, p);
    }

    /// <summary>Wrap an inner frame for reliable delivery: seq(u32) followed by the inner frame's bytes.</summary>
    public static Frame Reliable(uint seq, byte[] innerEncoded)
    {
        var p = new byte[4 + innerEncoded.Length];
        BinaryPrimitives.WriteUInt32LittleEndian(p, seq);
        innerEncoded.CopyTo(p, 4);
        return new(FrameType.Reliable, p);
    }

    /// <summary>Ping carries the sender's Stopwatch timestamp; the peer echoes it in a Pong.</summary>
    public static Frame Ping(long timestamp)
    {
        var p = new byte[8];
        BinaryPrimitives.WriteInt64LittleEndian(p, timestamp);
        return new(FrameType.Ping, p);
    }

    public static Frame Pong(byte[] pingPayload) => new(FrameType.Pong, pingPayload);
    public long I64(int offset) => BinaryPrimitives.ReadInt64LittleEndian(Payload.AsSpan(offset));

    public static Frame Leave() => new(FrameType.Leave, []);
    public static Frame Bye() => new(FrameType.Bye, []);
    public static Frame Status(bool locked) => new(FrameType.Status, [locked ? (byte)1 : (byte)0]);

    public static Frame MouseMove(int dx, int dy)
    {
        var p = new byte[4];
        BinaryPrimitives.WriteInt16LittleEndian(p, (short)Math.Clamp(dx, short.MinValue, short.MaxValue));
        BinaryPrimitives.WriteInt16LittleEndian(p.AsSpan(2), (short)Math.Clamp(dy, short.MinValue, short.MaxValue));
        return new(FrameType.MouseMove, p);
    }

    public static Frame MouseButton(byte button, bool down) => new(FrameType.MouseButton, [button, down ? (byte)1 : (byte)0]);

    public static Frame MouseWheel(int delta, bool horizontal)
    {
        var p = new byte[3];
        BinaryPrimitives.WriteInt16LittleEndian(p, (short)Math.Clamp(delta, short.MinValue, short.MaxValue));
        p[2] = horizontal ? (byte)1 : (byte)0;
        return new(FrameType.MouseWheel, p);
    }

    public static Frame Key(ushort vk, ushort scan, bool up, bool extended)
    {
        var p = new byte[5];
        BinaryPrimitives.WriteUInt16LittleEndian(p, vk);
        BinaryPrimitives.WriteUInt16LittleEndian(p.AsSpan(2), scan);
        p[4] = (byte)((up ? 1 : 0) | (extended ? 2 : 0));
        return new(FrameType.Key, p);
    }

    /// <summary>Same layout as Key, but applied by the receiver whether or not it is being controlled.</summary>
    public static Frame TypedKey(ushort vk, ushort scan, bool up, bool extended) => new(FrameType.TypedKey, Key(vk, scan, up, extended).Payload);

    public static Frame Enter(Crossing c) => CrossingFrame(FrameType.Enter, c);
    public static Frame ReturnEdge(Crossing c) => CrossingFrame(FrameType.ReturnEdge, c);

    private static Frame CrossingFrame(FrameType t, Crossing c)
    {
        var p = new byte[9];
        BinaryPrimitives.WriteInt32LittleEndian(p, c.Target.X);
        BinaryPrimitives.WriteInt32LittleEndian(p.AsSpan(4), c.Target.Y);
        p[8] = (byte)c.Dir;
        return new(t, p);
    }

    public Crossing? ReadCrossing()
    {
        if (Payload.Length < 9 || Payload[8] > 3) return null;
        return new Crossing((Dir)Payload[8], new Point(I32(0), I32(4)));
    }

    public static Frame Hello(HelloInfo h)
    {
        var nameBytes = Encoding.UTF8.GetBytes(h.Name);
        if (nameBytes.Length > 64) nameBytes = nameBytes[..64];
        var versionBytes = Encoding.UTF8.GetBytes(h.AppVersion ?? "");
        if (versionBytes.Length > 40) versionBytes = versionBytes[..40];
        var monitors = h.Monitors.Take(HelloInfo.MaxMonitors).ToArray();
        var ids = monitors.Select(m => { var b = Encoding.UTF8.GetBytes(m.Id ?? ""); return b.Length > 40 ? b[..40] : b; }).ToArray();
        int udpCount = Math.Min(8, h.UdpAddresses?.Length ?? 0);
        var deskBytes = Encoding.UTF8.GetBytes(h.DeskName ?? "");
        if (deskBytes.Length > 40) deskBytes = deskBytes[..40];
        var p = new byte[HelloInfo.FixedSize + monitors.Length * HelloInfo.MonitorSize + ids.Sum(b => 1 + b.Length) + 1 + versionBytes.Length + 3 + udpCount * 4 + 9 + deskBytes.Length + nameBytes.Length];
        p[0] = HelloInfo.Version;
        p[1] = (byte)(h.CaptureEnabled ? 1 : 0);
        BinaryPrimitives.WriteInt32LittleEndian(p.AsSpan(2), h.PeerOrigin.X);
        BinaryPrimitives.WriteInt32LittleEndian(p.AsSpan(6), h.PeerOrigin.Y);
        BinaryPrimitives.WriteUInt16LittleEndian(p.AsSpan(10), (ushort)Math.Clamp(h.Screen.Width, 0, 65535));
        BinaryPrimitives.WriteUInt16LittleEndian(p.AsSpan(12), (ushort)Math.Clamp(h.Screen.Height, 0, 65535));
        BinaryPrimitives.WriteInt64LittleEndian(p.AsSpan(14), h.PlacementUpdated);
        h.Nonce.AsSpan(0, 16).CopyTo(p.AsSpan(22));
        p[38] = (byte)monitors.Length;
        int o = HelloInfo.FixedSize;
        for (int i = 0; i < monitors.Length; i++)
        {
            var m = monitors[i];
            BinaryPrimitives.WriteInt32LittleEndian(p.AsSpan(o), m.Bounds.X);
            BinaryPrimitives.WriteInt32LittleEndian(p.AsSpan(o + 4), m.Bounds.Y);
            BinaryPrimitives.WriteInt32LittleEndian(p.AsSpan(o + 8), m.Bounds.Width);
            BinaryPrimitives.WriteInt32LittleEndian(p.AsSpan(o + 12), m.Bounds.Height);
            p[o + 16] = (byte)Math.Clamp(m.Number, 0, 255);
            o += HelloInfo.MonitorSize;
            p[o++] = (byte)ids[i].Length;          // v6: the panel's identity follows each monitor
            ids[i].CopyTo(p, o);
            o += ids[i].Length;
        }
        p[o++] = (byte)versionBytes.Length;
        versionBytes.CopyTo(p, o);
        o += versionBytes.Length;
        // Where this PC listens for the UDP pixel path (port 0 = not offered), then the name to the end.
        var addrs = (h.UdpAddresses ?? []).Take(8).ToArray();
        BinaryPrimitives.WriteUInt16LittleEndian(p.AsSpan(o), h.UdpPort);
        p[o + 2] = (byte)addrs.Length;
        o += 3;
        foreach (var a in addrs) { a.GetAddressBytes().AsSpan(0, 4).CopyTo(p.AsSpan(o)); o += 4; }
        // v7: the name of the desk this PC believes it is at, and when that name was set.
        BinaryPrimitives.WriteInt64LittleEndian(p.AsSpan(o), h.DeskNameUpdated);
        p[o + 8] = (byte)deskBytes.Length;
        o += 9;
        deskBytes.CopyTo(p, o);
        o += deskBytes.Length;
        nameBytes.CopyTo(p, o);
        return new(FrameType.Hello, p);
    }

    public static Frame HelloAck(ReadOnlySpan<byte> peerNonce) => new(FrameType.HelloAck, peerNonce[..16].ToArray());

    // ---- window mirroring ----
    public const int MirrorChunkHeader = 16;

    /// <summary>Which mirror a mirror frame belongs to (its first byte).</summary>
    public static byte MirrorIdOf(Frame f) => f.Payload.Length > 0 ? f.Payload[0] : (byte)0;

    public static Frame MirrorBegin(byte id, int w, int h, string title = "")
    {
        var t = Encoding.UTF8.GetBytes(title.Length > 120 ? title[..120] : title);
        var p = new byte[5 + t.Length];
        p[0] = id;
        BinaryPrimitives.WriteUInt16LittleEndian(p.AsSpan(1), (ushort)Math.Clamp(w, 0, 65535));
        BinaryPrimitives.WriteUInt16LittleEndian(p.AsSpan(3), (ushort)Math.Clamp(h, 0, 65535));
        t.CopyTo(p, 5);
        return new(FrameType.MirrorBegin, p);
    }

    /// <summary>The source window's title carried in a MirrorBegin.</summary>
    public static string MirrorTitleOf(Frame begin) =>
        begin.Payload.Length > 5 ? Encoding.UTF8.GetString(begin.Payload, 5, begin.Payload.Length - 5) : "";

    /// <summary>One chunk of a captured region's JPEG. The region and keyframe flag ride in every chunk so a
    /// frame reassembles whatever order chunks arrive, and a keyframe alone (region = the whole window) tells
    /// the receiver the source size even if MirrorBegin was lost.</summary>
    public static Frame MirrorChunk(byte id, ushort frameId, ushort index, ushort count, int x, int y, int w, int h, bool keyframe, ReadOnlySpan<byte> data)
    {
        var p = new byte[MirrorChunkHeader + data.Length];
        p[0] = id;
        BinaryPrimitives.WriteUInt16LittleEndian(p.AsSpan(1), frameId);
        BinaryPrimitives.WriteUInt16LittleEndian(p.AsSpan(3), index);
        BinaryPrimitives.WriteUInt16LittleEndian(p.AsSpan(5), count);
        BinaryPrimitives.WriteUInt16LittleEndian(p.AsSpan(7), (ushort)Math.Clamp(x, 0, 65535));
        BinaryPrimitives.WriteUInt16LittleEndian(p.AsSpan(9), (ushort)Math.Clamp(y, 0, 65535));
        BinaryPrimitives.WriteUInt16LittleEndian(p.AsSpan(11), (ushort)Math.Clamp(w, 0, 65535));
        BinaryPrimitives.WriteUInt16LittleEndian(p.AsSpan(13), (ushort)Math.Clamp(h, 0, 65535));
        p[15] = keyframe ? (byte)1 : (byte)0;
        data.CopyTo(p.AsSpan(MirrorChunkHeader));
        return new(FrameType.MirrorChunk, p);
    }

    public static Frame MirrorEnd(byte id) => new(FrameType.MirrorEnd, [id]);

    // ---- interacting with a mirror (viewer -> source) ----
    public static Frame MirrorPointer(byte id, int x, int y)
    {
        var p = new byte[5];
        p[0] = id;
        BinaryPrimitives.WriteUInt16LittleEndian(p.AsSpan(1), (ushort)Math.Clamp(x, 0, 65535));
        BinaryPrimitives.WriteUInt16LittleEndian(p.AsSpan(3), (ushort)Math.Clamp(y, 0, 65535));
        return new(FrameType.MirrorPointer, p);
    }

    public static Frame MirrorMouse(byte id, byte kind, int x, int y, byte button, int delta)
    {
        var p = new byte[9];
        p[0] = id;
        p[1] = kind;
        BinaryPrimitives.WriteUInt16LittleEndian(p.AsSpan(2), (ushort)Math.Clamp(x, 0, 65535));
        BinaryPrimitives.WriteUInt16LittleEndian(p.AsSpan(4), (ushort)Math.Clamp(y, 0, 65535));
        p[6] = button;
        BinaryPrimitives.WriteInt16LittleEndian(p.AsSpan(7), (short)Math.Clamp(delta, short.MinValue, short.MaxValue));
        return new(FrameType.MirrorMouse, p);
    }

    public static Frame MirrorKey(byte id, ushort vk, ushort scan, bool up, bool extended)
    {
        var p = new byte[6];
        p[0] = id;
        BinaryPrimitives.WriteUInt16LittleEndian(p.AsSpan(1), vk);
        BinaryPrimitives.WriteUInt16LittleEndian(p.AsSpan(3), scan);
        p[5] = (byte)((up ? 1 : 0) | (extended ? 2 : 0));
        return new(FrameType.MirrorKey, p);
    }

    public static IEnumerable<Frame> ClipboardText(byte id, string text)
    {
        var bytes = Encoding.UTF8.GetBytes(text);
        const int chunk = 400;
        int total = Math.Max(1, (bytes.Length + chunk - 1) / chunk);
        if (total > 255) { total = 255; bytes = bytes[..(255 * chunk)]; }
        for (int i = 0; i < total; i++)
        {
            int len = Math.Min(chunk, bytes.Length - i * chunk);
            var p = new byte[3 + len];
            p[0] = id; p[1] = (byte)i; p[2] = (byte)total;
            bytes.AsSpan(i * chunk, len).CopyTo(p.AsSpan(3));
            yield return new Frame(FrameType.Clipboard, p);
        }
    }

    /// <summary>An encoded picture in chunks of <paramref name="chunk"/> bytes (400 fits a BLE write, 1200 a datagram).</summary>
    public static IEnumerable<Frame> ClipboardImage(byte id, byte[] data, int chunk)
    {
        int total = Math.Max(1, (data.Length + chunk - 1) / chunk);
        for (int i = 0; i < total; i++)
        {
            int len = Math.Min(chunk, data.Length - i * chunk);
            var p = new byte[5 + len];
            p[0] = id;
            BinaryPrimitives.WriteUInt16LittleEndian(p.AsSpan(1), (ushort)i);
            BinaryPrimitives.WriteUInt16LittleEndian(p.AsSpan(3), (ushort)total);
            data.AsSpan(i * chunk, len).CopyTo(p.AsSpan(5));
            yield return new Frame(FrameType.ClipboardImage, p);
        }
    }
}

/// <summary>
/// Handshake payload. Both sides send one when the link comes up and answer with HelloAck carrying the
/// peer's nonce; input is only accepted once our own nonce has been acknowledged. <see cref="PeerOrigin"/>
/// is where the sender believes the receiver's origin sits, in the sender's pixels; placements are
/// compared by timestamp so that whichever PC was configured last wins. Monitors are the sender's own.
/// </summary>
public sealed record HelloInfo(bool CaptureEnabled, Point PeerOrigin, Size Screen, long PlacementUpdated, byte[] Nonce, MonitorRect[] Monitors, string Name, string AppVersion = "",
    ushort UdpPort = 0, IPAddress[]? UdpAddresses = null, string DeskName = "", long DeskNameUpdated = 0)
{
    public const byte Version = 7;   // 5: UDP port+addresses; 6: a monitor id per monitor; 7: the current desk's name and when it was set
    public const int FixedSize = 1 + 1 + 4 + 4 + 2 + 2 + 8 + 16 + 1;
    public const int MonitorSize = 17;
    public const int MaxMonitors = 16;

    public static HelloInfo? Parse(Frame f)
    {
        var p = f.Payload;
        if (f.Type != FrameType.Hello || p.Length < FixedSize || p[0] != Version) return null;
        int count = p[38];
        int o = FixedSize;
        var monitors = new MonitorRect[count];
        for (int i = 0; i < count; i++)
        {
            if (p.Length < o + MonitorSize + 1) return null;
            var bounds = new Rectangle(
                BinaryPrimitives.ReadInt32LittleEndian(p.AsSpan(o)),
                BinaryPrimitives.ReadInt32LittleEndian(p.AsSpan(o + 4)),
                BinaryPrimitives.ReadInt32LittleEndian(p.AsSpan(o + 8)),
                BinaryPrimitives.ReadInt32LittleEndian(p.AsSpan(o + 12)));
            int number = p[o + 16];
            o += MonitorSize;
            int idLen = p[o++];
            if (p.Length < o + idLen) return null;
            monitors[i] = new MonitorRect(bounds, number, Encoding.UTF8.GetString(p, o, idLen));
            o += idLen;
        }
        if (p.Length < o + 1) return null;
        int versionLen = p[o++];
        if (p.Length < o + versionLen) return null;
        string appVersion = Encoding.UTF8.GetString(p, o, versionLen);
        o += versionLen;
        if (p.Length < o + 3) return null;
        ushort udpPort = BinaryPrimitives.ReadUInt16LittleEndian(p.AsSpan(o));
        int udpCount = p[o + 2];
        o += 3;
        if (p.Length < o + udpCount * 4) return null;
        var udpAddrs = new IPAddress[udpCount];
        for (int i = 0; i < udpCount; i++, o += 4) udpAddrs[i] = new IPAddress(p.AsSpan(o, 4));
        if (p.Length < o + 9) return null;
        long deskUpdated = BinaryPrimitives.ReadInt64LittleEndian(p.AsSpan(o));
        int deskLen = p[o + 8];
        o += 9;
        if (p.Length < o + deskLen) return null;
        string deskName = Encoding.UTF8.GetString(p, o, deskLen);
        o += deskLen;
        return new HelloInfo(
            p[1] != 0,
            new Point(BinaryPrimitives.ReadInt32LittleEndian(p.AsSpan(2)), BinaryPrimitives.ReadInt32LittleEndian(p.AsSpan(6))),
            new Size(BinaryPrimitives.ReadUInt16LittleEndian(p.AsSpan(10)), BinaryPrimitives.ReadUInt16LittleEndian(p.AsSpan(12))),
            BinaryPrimitives.ReadInt64LittleEndian(p.AsSpan(14)),
            p.AsSpan(22, 16).ToArray(),
            monitors,
            Encoding.UTF8.GetString(p, o, p.Length - o),
            appVersion,
            udpPort,
            udpAddrs,
            deskName,
            deskUpdated);
    }
}

/// <summary>
/// AES-GCM framing. Packet = counter(8, big-endian) || ciphertext || tag(16).
/// Nonce = direction(1) || channel(1) || 0(2) || counter(8). Counters start from the wall clock so they
/// stay unique across restarts. The receiver keeps a sliding window of the last 64 counters:
/// packets may arrive slightly out of order (BLE callbacks run concurrently) but never twice,
/// and anything older than the window is dropped.
/// The direction byte is 1 for frames sent by the BLE central and 2 for the peripheral. The channel byte is 0
/// for BLE and 1 for the UDP pixel path: same key, but two independent counter streams must never share a
/// nonce space under GCM, and each path keeps its own replay window since they reorder against each other.
/// </summary>
public sealed class Crypto : IDisposable
{
    private static readonly byte[] Salt = Encoding.ASCII.GetBytes("Glissando-v1");
    private readonly AesGcm _aes;
    private readonly byte _dirSend, _dirRecv, _channel;
    private long _sendCounter;
    private long _highest = -1;   // highest counter accepted so far
    private ulong _window;        // bit i set = counter (_highest - i) has been accepted
    private const int WindowSize = 64;
    private readonly object _gate = new();

    public const int Overhead = 8 + 16;

    public Crypto(string passphrase, bool isCentral, byte channel = 0)
    {
        _channel = channel;
        var key = Rfc2898DeriveBytes.Pbkdf2(passphrase, Salt, 200_000, HashAlgorithmName.SHA256, 32);
        _aes = new AesGcm(key, 16);
        _dirSend = isCentral ? (byte)1 : (byte)2;
        _dirRecv = isCentral ? (byte)2 : (byte)1;
        _sendCounter = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds() << 12;
    }

    public byte[] Seal(ReadOnlySpan<byte> plain)
    {
        long counter = Interlocked.Increment(ref _sendCounter);
        var packet = new byte[8 + plain.Length + 16];
        BinaryPrimitives.WriteInt64BigEndian(packet, counter);
        Span<byte> nonce = stackalloc byte[12];
        nonce[0] = _dirSend;
        nonce[1] = _channel;
        BinaryPrimitives.WriteInt64BigEndian(nonce[4..], counter);
        lock (_gate)
            _aes.Encrypt(nonce, plain, packet.AsSpan(8, plain.Length), packet.AsSpan(8 + plain.Length, 16));
        return packet;
    }

    public bool Open(ReadOnlySpan<byte> packet, out byte[] plain)
    {
        plain = [];
        if (packet.Length < Overhead) return false;
        long counter = BinaryPrimitives.ReadInt64BigEndian(packet);
        Span<byte> nonce = stackalloc byte[12];
        nonce[0] = _dirRecv;
        nonce[1] = _channel;
        BinaryPrimitives.WriteInt64BigEndian(nonce[4..], counter);
        var body = new byte[packet.Length - Overhead];
        lock (_gate)
        {
            long behind = _highest - counter; // negative = newer than anything seen
            if (_highest >= 0 && behind >= WindowSize) return false;                       // too old
            if (_highest >= 0 && behind >= 0 && ((_window >> (int)behind) & 1) != 0) return false; // replay
            try
            {
                _aes.Decrypt(nonce, packet.Slice(8, body.Length), packet[^16..], body);
            }
            catch (CryptographicException) { return false; }

            if (_highest < 0 || behind < 0)
            {
                long shift = _highest < 0 ? WindowSize : -behind;
                _window = shift >= WindowSize ? 0 : _window << (int)shift;
                _window |= 1;
                _highest = counter;
            }
            else
            {
                _window |= 1UL << (int)behind;
            }
        }
        plain = body;
        return true;
    }

    public void Dispose() => _aes.Dispose();
}

/// <summary>
/// How often mouse moves may go out. A BLE central's WriteWithoutResponse completes as soon as Windows has
/// buffered the packet, not when the radio has sent it, so the send queue never looks backed up: a wired mouse
/// reporting at 500-1000 Hz filled the stack's own buffer on 2026-10-05 and the round trip climbed to seconds,
/// with the peer dropping moves that arrived too late for its replay window. Spacing moves out lets the ones
/// that arrive in between coalesce into one; the gap widens when the measured round trip shows a backlog anyway.
/// </summary>
public static class MovePacing
{
    public const int BaseGapMs = 8;
    public const int MaxGapMs = 40;

    public static int GapMs(double rttMs)
    {
        if (rttMs < 60) return BaseGapMs;
        return (int)Math.Clamp(rttMs / 4, BaseGapMs, MaxGapMs);
    }
}
