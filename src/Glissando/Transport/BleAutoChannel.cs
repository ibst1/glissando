using System.Diagnostics;
using Windows.Devices.Bluetooth;
using Windows.Devices.Bluetooth.Advertisement;

namespace Glissando.Transport;

/// <summary>
/// Decides at runtime which PC connects to which. Advertise if the adapter can, and scan at the
/// same time. When the other PC's advertisement is seen, the PC that is not advertising
/// successfully, or (if both are) the one with the lower Bluetooth address, becomes the
/// central and connects. Both PCs apply the same rule, so exactly one of them connects.
/// </summary>
public sealed class BleAutoChannel : IChannel
{
    public event Action<byte[]>? Received;
    public event Action<bool>? LinkChanged;
    public event Action<string>? StatusChanged;

    private IChannel? _active;
    public bool IsLinked => _active?.IsLinked ?? false;
    public bool IsCentral => _active is BleCentralChannel;

    public async Task RunAsync(CancellationToken ct)
    {
        var adapter = await BluetoothAdapter.GetDefaultAsync();
        if (adapter == null) { StatusChanged?.Invoke("BLE error: no adapter"); Log.Error("no Bluetooth adapter"); return; }
        ulong myAddr = adapter.BluetoothAddress;

        BlePeripheralChannel? p = null;
        CancellationTokenSource? pCts = null;
        Task? pTask = null;
        Action? detachP = null;
        var watcher = new BluetoothLEAdvertisementWatcher { ScanningMode = BluetoothLEScanningMode.Active };
        watcher.AdvertisementFilter.Advertisement.ServiceUuids.Add(BleIds.Service);

        if (adapter.IsPeripheralRoleSupported)
        {
            p = new BlePeripheralChannel();
            detachP = Attach(p, up =>
            {
                // While someone is connected to us there is nothing to decide; resume scanning if they leave.
                try { if (up) watcher.Stop(); else watcher.Start(); } catch { }
            });
            _active = p;
            pCts = CancellationTokenSource.CreateLinkedTokenSource(ct);
            pTask = p.RunAsync(pCts.Token);
        }
        else
        {
            Log.Info("BLE auto: adapter cannot advertise; this PC will connect");
        }

        var decision = new TaskCompletionSource<ulong>(TaskCreationOptions.RunContinuationsAsynchronously);
        var started = Stopwatch.StartNew();
        watcher.Received += (s, e) =>
        {
            if (e.BluetoothAddress == myAddr || e.BluetoothAddress == 0) return;
            if (p != null && p.IsLinked) return;
            if (p != null && started.ElapsedMilliseconds < 3000) return; // let our own advertising settle first
            bool advertising = p != null && p.IsAdvertising;
            if (!advertising || myAddr < e.BluetoothAddress)
                decision.TrySetResult(e.BluetoothAddress);
            else
                Log.Info($"BLE auto: peer {e.BluetoothAddress:X12} seen; it has the lower address and should connect to us");
        };
        watcher.Start();
        StatusChanged?.Invoke(p != null ? "advertising, scanning" : "scanning");

        ulong peer;
        using (ct.Register(() => decision.TrySetCanceled()))
        {
            try { peer = await decision.Task; }
            catch (OperationCanceledException)
            {
                try { watcher.Stop(); } catch { }
                if (p != null) { pCts!.Cancel(); try { await pTask!; } catch { } }
                return;
            }
        }
        try { watcher.Stop(); } catch { }

        Log.Info($"BLE auto: peer {peer:X12} seen; this PC ({myAddr:X12}) connects" +
                 (p != null && p.IsAdvertising ? " (lower address)" : " (not advertising)"));
        if (p != null)
        {
            pCts!.Cancel();
            try { await pTask!; } catch { }
            detachP?.Invoke();
            p.Dispose();
        }

        var c = new BleCentralChannel();
        Attach(c, null);
        _active = c;
        await c.RunAsync(ct);
    }

    private Action Attach(IChannel ch, Action<bool>? onLink)
    {
        Action<byte[]> r = b => Received?.Invoke(b);
        Action<bool> l = up => { LinkChanged?.Invoke(up); onLink?.Invoke(up); };
        Action<string> s = st => StatusChanged?.Invoke(st);
        ch.Received += r; ch.LinkChanged += l; ch.StatusChanged += s;
        return () => { ch.Received -= r; ch.LinkChanged -= l; ch.StatusChanged -= s; };
    }

    public Task<bool> SendAsync(byte[] packet) => _active?.SendAsync(packet) ?? Task.FromResult(false);

    public void Reconnect() => _active?.Reconnect();

    public void Dispose() => _active?.Dispose();
}
