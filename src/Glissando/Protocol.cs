using System.Buffers.Binary;
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
        var p = new byte[HelloInfo.FixedSize + monitors.Length * HelloInfo.MonitorSize + 1 + versionBytes.Length + nameBytes.Length];
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
        foreach (var m in monitors)
        {
            BinaryPrimitives.WriteInt32LittleEndian(p.AsSpan(o), m.Bounds.X);
            BinaryPrimitives.WriteInt32LittleEndian(p.AsSpan(o + 4), m.Bounds.Y);
            BinaryPrimitives.WriteInt32LittleEndian(p.AsSpan(o + 8), m.Bounds.Width);
            BinaryPrimitives.WriteInt32LittleEndian(p.AsSpan(o + 12), m.Bounds.Height);
            p[o + 16] = (byte)Math.Clamp(m.Number, 0, 255);
            o += HelloInfo.MonitorSize;
        }
        p[o++] = (byte)versionBytes.Length;
        versionBytes.CopyTo(p, o);
        o += versionBytes.Length;
        nameBytes.CopyTo(p, o);
        return new(FrameType.Hello, p);
    }

    public static Frame HelloAck(ReadOnlySpan<byte> peerNonce) => new(FrameType.HelloAck, peerNonce[..16].ToArray());

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
}

/// <summary>
/// Handshake payload. Both sides send one when the link comes up and answer with HelloAck carrying the
/// peer's nonce; input is only accepted once our own nonce has been acknowledged. <see cref="PeerOrigin"/>
/// is where the sender believes the receiver's origin sits, in the sender's pixels; placements are
/// compared by timestamp so that whichever PC was configured last wins. Monitors are the sender's own.
/// </summary>
public sealed record HelloInfo(bool CaptureEnabled, Point PeerOrigin, Size Screen, long PlacementUpdated, byte[] Nonce, MonitorRect[] Monitors, string Name, string AppVersion = "")
{
    public const byte Version = 4;
    public const int FixedSize = 1 + 1 + 4 + 4 + 2 + 2 + 8 + 16 + 1;
    public const int MonitorSize = 17;
    public const int MaxMonitors = 16;

    public static HelloInfo? Parse(Frame f)
    {
        var p = f.Payload;
        if (f.Type != FrameType.Hello || p.Length < FixedSize || p[0] != Version) return null;
        int count = p[38];
        int o = FixedSize;
        if (p.Length < o + count * MonitorSize + 1) return null;
        var monitors = new MonitorRect[count];
        for (int i = 0; i < count; i++, o += MonitorSize)
            monitors[i] = new MonitorRect(new Rectangle(
                BinaryPrimitives.ReadInt32LittleEndian(p.AsSpan(o)),
                BinaryPrimitives.ReadInt32LittleEndian(p.AsSpan(o + 4)),
                BinaryPrimitives.ReadInt32LittleEndian(p.AsSpan(o + 8)),
                BinaryPrimitives.ReadInt32LittleEndian(p.AsSpan(o + 12))), p[o + 16]);
        int versionLen = p[o++];
        if (p.Length < o + versionLen) return null;
        string appVersion = Encoding.UTF8.GetString(p, o, versionLen);
        o += versionLen;
        return new HelloInfo(
            p[1] != 0,
            new Point(BinaryPrimitives.ReadInt32LittleEndian(p.AsSpan(2)), BinaryPrimitives.ReadInt32LittleEndian(p.AsSpan(6))),
            new Size(BinaryPrimitives.ReadUInt16LittleEndian(p.AsSpan(10)), BinaryPrimitives.ReadUInt16LittleEndian(p.AsSpan(12))),
            BinaryPrimitives.ReadInt64LittleEndian(p.AsSpan(14)),
            p.AsSpan(22, 16).ToArray(),
            monitors,
            Encoding.UTF8.GetString(p, o, p.Length - o),
            appVersion);
    }
}

/// <summary>
/// AES-GCM framing. Packet = counter(8, big-endian) || ciphertext || tag(16).
/// Nonce = direction(1) || 0(3) || counter(8). Counters start from the wall clock so they
/// stay unique across restarts. The receiver keeps a sliding window of the last 64 counters:
/// packets may arrive slightly out of order (BLE callbacks run concurrently) but never twice,
/// and anything older than the window is dropped.
/// The direction byte is 1 for frames sent by the BLE central and 2 for the peripheral.
/// </summary>
public sealed class Crypto : IDisposable
{
    private static readonly byte[] Salt = Encoding.ASCII.GetBytes("Glissando-v1");
    private readonly AesGcm _aes;
    private readonly byte _dirSend, _dirRecv;
    private long _sendCounter;
    private long _highest = -1;   // highest counter accepted so far
    private ulong _window;        // bit i set = counter (_highest - i) has been accepted
    private const int WindowSize = 64;
    private readonly object _gate = new();

    public const int Overhead = 8 + 16;

    public Crypto(string passphrase, bool isCentral)
    {
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
