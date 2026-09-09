using System.Threading.Channels;
using Windows.Devices.Bluetooth;
using Windows.Devices.Bluetooth.GenericAttributeProfile;
using Windows.Foundation;
using Windows.Storage.Streams;

namespace Glissando.Transport;

/// <summary>This PC advertises the Glissando GATT service and talks to whichever central subscribes.</summary>
public sealed class BlePeripheralChannel : IChannel
{
    public event Action<byte[]>? Received;
    public event Action<bool>? LinkChanged;
    public event Action<string>? StatusChanged;

    public bool IsLinked { get; private set; }
    public bool IsCentral => false;

    /// <summary>True while Windows reports the advertisement as running.</summary>
    public bool IsAdvertising => _provider?.AdvertisementStatus == GattServiceProviderAdvertisementStatus.Started;

    private GattServiceProvider? _provider;
    private GattLocalCharacteristic? _rx, _tx;
    private readonly SemaphoreSlim _sendLock = new(1, 1);
    private readonly GattServiceProviderAdvertisingParameters _advParams = new() { IsConnectable = true, IsDiscoverable = true };

    // Write requests are raised in order, but awaiting GetRequestAsync inside the handler lets two of
    // them race and finish swapped. Queue them and let one consumer read them strictly in sequence.
    private readonly Channel<(GattWriteRequestedEventArgs args, Deferral deferral)> _writes =
        Channel.CreateUnbounded<(GattWriteRequestedEventArgs, Deferral)>(new UnboundedChannelOptions { SingleReader = true });

    public async Task RunAsync(CancellationToken ct)
    {
        var adapter = await BluetoothAdapter.GetDefaultAsync();
        if (adapter == null) { Fail("no Bluetooth adapter"); return; }
        Log.Info($"BLE adapter {adapter.BluetoothAddress:X12} peripheral={adapter.IsPeripheralRoleSupported} central={adapter.IsCentralRoleSupported}");
        if (!adapter.IsPeripheralRoleSupported) { Fail("adapter cannot advertise; set ble=connect on this PC"); return; }

        var created = await GattServiceProvider.CreateAsync(BleIds.Service);
        if (created.Error != BluetoothError.Success) { Fail($"GattServiceProvider: {created.Error}"); return; }
        _provider = created.ServiceProvider;

        var rx = await _provider.Service.CreateCharacteristicAsync(BleIds.Rx, new GattLocalCharacteristicParameters
        {
            CharacteristicProperties = GattCharacteristicProperties.WriteWithoutResponse | GattCharacteristicProperties.Write,
            WriteProtectionLevel = GattProtectionLevel.Plain,
            UserDescription = "Glissando rx",
        });
        if (rx.Error != BluetoothError.Success) { Fail($"rx characteristic: {rx.Error}"); return; }
        _rx = rx.Characteristic;

        var tx = await _provider.Service.CreateCharacteristicAsync(BleIds.Tx, new GattLocalCharacteristicParameters
        {
            CharacteristicProperties = GattCharacteristicProperties.Notify,
            ReadProtectionLevel = GattProtectionLevel.Plain,
            UserDescription = "Glissando tx",
        });
        if (tx.Error != BluetoothError.Success) { Fail($"tx characteristic: {tx.Error}"); return; }
        _tx = tx.Characteristic;

        _rx.WriteRequested += OnWriteRequested;
        _tx.SubscribedClientsChanged += OnSubscribedClientsChanged;
        _provider.AdvertisementStatusChanged += (s, e) =>
        {
            Log.Info($"BLE advertising {e.Status} ({e.Error})");
            StatusChanged?.Invoke(e.Status == GattServiceProviderAdvertisementStatus.Started ? "advertising" : $"advertising {e.Status}");
        };

        _provider.StartAdvertising(_advParams);
        StatusChanged?.Invoke("advertising");
        _ = Task.Run(() => ProcessWritesAsync(ct));

        // Windows sometimes reports Aborted right after StartAdvertising and, on some
        // machines, never recovers on its own. Poke it until it sticks.
        while (!ct.IsCancellationRequested)
        {
            try { await Task.Delay(2000, ct); } catch (OperationCanceledException) { break; }
            var st = _provider.AdvertisementStatus;
            if (st is GattServiceProviderAdvertisementStatus.Aborted or GattServiceProviderAdvertisementStatus.Stopped)
            {
                Log.Warn($"BLE advertising is {st}; restarting");
                try { _provider.StartAdvertising(_advParams); }
                catch (Exception ex) { Log.Error("StartAdvertising: " + ex.Message); }
            }
        }
        try { _provider.StopAdvertising(); } catch { }
    }

    private void Fail(string why)
    {
        Log.Error("BLE peripheral: " + why);
        StatusChanged?.Invoke("BLE error: " + why);
    }

    private void OnWriteRequested(GattLocalCharacteristic sender, GattWriteRequestedEventArgs args)
    {
        // Take the deferral synchronously; the consumer completes it once the packet has been handled in order.
        _writes.Writer.TryWrite((args, args.GetDeferral()));
    }

    private async Task ProcessWritesAsync(CancellationToken ct)
    {
        var reader = _writes.Reader;
        try
        {
            while (await reader.WaitToReadAsync(ct))
            {
                while (reader.TryRead(out var item))
                {
                    try
                    {
                        var req = await item.args.GetRequestAsync();
                        if (req == null) continue;
                        if (req.Option == GattWriteOption.WriteWithResponse) req.Respond();
                        var data = new byte[req.Value.Length];
                        DataReader.FromBuffer(req.Value).ReadBytes(data);
                        Received?.Invoke(data);
                    }
                    catch (Exception ex) { Log.Error("BLE write handler: " + ex.Message); }
                    finally { item.deferral.Complete(); }
                }
            }
        }
        catch (OperationCanceledException) { }
    }

    private void OnSubscribedClientsChanged(GattLocalCharacteristic sender, object args)
    {
        bool up = sender.SubscribedClients.Count > 0;
        Log.Info($"BLE subscribed clients: {sender.SubscribedClients.Count}");
        if (up == IsLinked) return;
        IsLinked = up;
        StatusChanged?.Invoke(up ? "linked" : "advertising");
        LinkChanged?.Invoke(up);
    }

    public async Task<bool> SendAsync(byte[] packet)
    {
        var tx = _tx;
        if (tx == null || !IsLinked) return false;
        await _sendLock.WaitAsync();
        try
        {
            var w = new DataWriter();
            w.WriteBytes(packet);
            var results = await tx.NotifyValueAsync(w.DetachBuffer());
            foreach (var r in results)
                if (r.Status != GattCommunicationStatus.Success) return false;
            return results.Count > 0;
        }
        catch (Exception ex)
        {
            Log.Warn("BLE notify failed: " + ex.Message);
            return false;
        }
        finally { _sendLock.Release(); }
    }

    public void Reconnect()
    {
        // The central owns the connection; all we can do is make sure we are really advertising.
        // After sleep Windows may still report Started while nothing is on the air, so restart it.
        var p = _provider;
        if (p == null) return;
        try { p.StopAdvertising(); } catch { }
        try { p.StartAdvertising(_advParams); Log.Info("BLE advertising restarted"); }
        catch (Exception ex) { Log.Error("StartAdvertising: " + ex.Message); }
    }

    public void Dispose()
    {
        try { _provider?.StopAdvertising(); } catch { }
        _sendLock.Dispose();
    }
}
