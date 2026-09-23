namespace Glissando.Transport;

/// <summary>A bidirectional datagram link to the peer. Implementations reconnect on their own.</summary>
public interface IChannel : IDisposable
{
    /// <summary>Raised on an arbitrary thread for every packet received.</summary>
    event Action<byte[]>? Received;

    /// <summary>Raised when the link comes up or goes down.</summary>
    event Action<bool>? LinkChanged;

    /// <summary>Human-readable transport state for the tray.</summary>
    event Action<string>? StatusChanged;

    bool IsLinked { get; }

    /// <summary>True when this side is the BLE central (it connected to the other PC).</summary>
    bool IsCentral { get; }

    /// <summary>Runs until cancelled; keeps the link alive and reconnects as needed.</summary>
    Task RunAsync(CancellationToken ct);

    /// <summary>Sends one packet. Returns false if the link was down or the write failed.</summary>
    Task<bool> SendAsync(byte[] packet);

    /// <summary>Drops the current link so the run loop establishes a new one.</summary>
    void Reconnect();

    /// <summary>The machine is about to sleep: let go of the radio cleanly - stop advertising, release the GATT
    /// server, drop the link - so the Bluetooth stack does not carry a live LE server through suspend. Left in
    /// place across a night's sleep, the Intel stack woke with its GATT server dead (2026-09-13), and only a
    /// restart of Windows revived it. Default: nothing.</summary>
    void Sleep() { }

    /// <summary>The machine woke: start over from scratch. Default: nothing.</summary>
    void Wake() { }
}

public static class BleIds
{
    public static readonly Guid Service = new("6c1d0a10-9a3e-4b8f-8d2e-7f5c1e2a3b40");
    /// <summary>Central writes here (WriteWithoutResponse).</summary>
    public static readonly Guid Rx = new("6c1d0a11-9a3e-4b8f-8d2e-7f5c1e2a3b40");
    /// <summary>Peripheral notifies here.</summary>
    public static readonly Guid Tx = new("6c1d0a12-9a3e-4b8f-8d2e-7f5c1e2a3b40");

    /// <summary>Service data put in the advertisement: a marker and the advertiser's real adapter address. The
    /// address an advertisement is seen FROM is a random private one that Windows rotates, so it cannot break
    /// the who-connects tie: each PC compared its own real address with the other's random one, both concluded
    /// "the other has the lower address" and waited for each other (2026-09-23). This can.</summary>
    public static byte[] TieBreak(ulong address)
    {
        var b = new byte[8];
        b[0] = (byte)'G'; b[1] = (byte)'L';
        for (int i = 0; i < 6; i++) b[2 + i] = (byte)(address >> (8 * i));
        return b;
    }

    /// <summary>The address carried by <see cref="TieBreak"/> if this data section holds one (at any offset:
    /// Windows may prefix the service UUID), else null.</summary>
    public static ulong? ReadTieBreak(byte[] section)
    {
        for (int at = 0; at + 8 <= section.Length; at++)
        {
            if (section[at] != (byte)'G' || section[at + 1] != (byte)'L') continue;
            ulong a = 0;
            for (int i = 0; i < 6; i++) a |= (ulong)section[at + 2 + i] << (8 * i);
            if (a != 0) return a;
        }
        return null;
    }
}
