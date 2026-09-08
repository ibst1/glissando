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
}

public static class BleIds
{
    public static readonly Guid Service = new("6c1d0a10-9a3e-4b8f-8d2e-7f5c1e2a3b40");
    /// <summary>Central writes here (WriteWithoutResponse).</summary>
    public static readonly Guid Rx = new("6c1d0a11-9a3e-4b8f-8d2e-7f5c1e2a3b40");
    /// <summary>Peripheral notifies here.</summary>
    public static readonly Guid Tx = new("6c1d0a12-9a3e-4b8f-8d2e-7f5c1e2a3b40");
}
