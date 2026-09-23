using Windows.Devices.Bluetooth;
using Windows.Devices.Bluetooth.GenericAttributeProfile;
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

    private const int MaxAdvRestarts = 8;
    private int _advFails;
    /// <summary>Windows answered StartAdvertising with Aborted/Stopped: this adapter refuses to advertise (the
    /// central-only PC). Not a hang, so BleAutoChannel must not report the stack as stuck.</summary>
    public bool AdvertisingRefused => _advFails > 0;
    private GattServiceProvider? _provider;
    private GattLocalCharacteristic? _rx, _tx;
    private readonly SemaphoreSlim _sendLock = new(1, 1);
    private readonly GattServiceProviderAdvertisingParameters _advParams = new() { IsConnectable = true, IsDiscoverable = true };

    public async Task RunAsync(CancellationToken ct)
    {
        var adapter = await BluetoothAdapter.GetDefaultAsync();
        if (adapter == null) { Fail("no Bluetooth adapter"); return; }
        Log.Info($"BLE adapter {adapter.BluetoothAddress:X12} peripheral={adapter.IsPeripheralRoleSupported} central={adapter.IsCentralRoleSupported}");
        if (!adapter.IsPeripheralRoleSupported) { Fail("adapter cannot advertise; set ble=connect on this PC"); return; }

        // Carry the real adapter address in the advertisement (see BleIds.TieBreak): the address the peer sees
        // the advertisement from is a random private one, useless for deciding who connects. Best effort: the
        // property needs Windows 11 22H2, and if Windows will not start the advertisement with it, it is dropped.
        try
        {
            if (Windows.Foundation.Metadata.ApiInformation.IsPropertyPresent(
                    "Windows.Devices.Bluetooth.GenericAttributeProfile.GattServiceProviderAdvertisingParameters", "ServiceData"))
            {
                var w = new DataWriter();
                w.WriteBytes(BleIds.TieBreak(adapter.BluetoothAddress));
                _advParams.ServiceData = w.DetachBuffer();
            }
            else Log.Info("BLE advertising: this Windows cannot add service data; the peer decides who connects by the advertised address");
        }
        catch (Exception ex) { Log.Warn("BLE advertising: cannot add the adapter address to the advertisement: " + ex.Message); }

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

        // Windows sometimes reports Aborted right after StartAdvertising and, on some machines recovers on a
        // poke. But an adapter that cannot advertise at all (the peer's is one) never recovers, and poking it
        // every 2 s forever churns the radio during the very window the central half is trying to scan and
        // connect. So give up after a few tries and go quiet: BleAutoChannel already treats "not advertising"
        // as "this PC connects", and scanning continues regardless. A run that does start resets the budget.
        _advFails = 0;
        while (!ct.IsCancellationRequested)
        {
            try { await Task.Delay(2000, ct); } catch (OperationCanceledException) { break; }
            var st = _provider.AdvertisementStatus;
            if (st is GattServiceProviderAdvertisementStatus.Aborted or GattServiceProviderAdvertisementStatus.Stopped)
            {
                if (_advFails >= MaxAdvRestarts)
                {
                    if (_advFails == MaxAdvRestarts) { _advFails++; Log.Warn($"BLE advertising will not start after {MaxAdvRestarts} tries; giving up (this PC acts as central only)"); }
                    continue;
                }
                _advFails++;
                Log.Warn($"BLE advertising is {st}; restarting ({_advFails}/{MaxAdvRestarts})");
                if (_advFails == 2 && _advParams.ServiceData != null)
                {
                    // In case the service data is what the stack objects to: try once more without it.
                    Log.Warn("BLE advertising: dropping the service data (adapter address) in case it is the reason");
                    _advParams.ServiceData = null;
                }
                try { _provider.StartAdvertising(_advParams); }
                catch (Exception ex) { Log.Error("StartAdvertising: " + ex.Message); }
            }
            else if (st == GattServiceProviderAdvertisementStatus.Started) _advFails = 0;
        }
        try { _provider.StopAdvertising(); } catch { }
    }

    private void Fail(string why)
    {
        Log.Error("BLE peripheral: " + why);
        StatusChanged?.Invoke("BLE error: " + why);
    }

    private async void OnWriteRequested(GattLocalCharacteristic sender, GattWriteRequestedEventArgs args)
    {
        // Handle each write concurrently so throughput keeps up with a fast mouse. Reordering a few
        // packets is harmless: a swapped mouse delta is invisible, and a late Enter is covered by the
        // Detached delay and the post-control edge suppression in Session.
        var deferral = args.GetDeferral();
        try
        {
            var req = await args.GetRequestAsync();
            if (req == null) return;
            if (req.Option == GattWriteOption.WriteWithResponse) req.Respond();
            var data = new byte[req.Value.Length];
            DataReader.FromBuffer(req.Value).ReadBytes(data);
            Received?.Invoke(data);
        }
        catch (Exception ex) { Log.Error($"BLE write handler: {ex.GetType().Name} 0x{ex.HResult:X8} {ex.Message}"); }
        finally { deferral.Complete(); }
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
        _advFails = 0; // a manual reconnect or a wake gets a fresh set of restart attempts
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
