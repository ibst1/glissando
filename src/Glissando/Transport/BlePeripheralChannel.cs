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

    /// <summary>True while Windows reports the advertisement as running. "Started without all advertisement
    /// data" (a payload that did not fit) is still on the air: service data with the 128-bit service UUID gave
    /// that status, and reading it as "not advertising" made this PC connect while it was advertising too.
    /// The status as last REPORTED (AdvertisementStatusChanged, or read by the run loop), never read from the
    /// provider here: BleAutoChannel's watchdog asks this every second, and on a hung stack the provider's
    /// property hung with it, so the watchdog that should have said "Bluetooth stuck" was stuck too (2026-10-06).</summary>
    public bool IsAdvertising => _advStatus is GattServiceProviderAdvertisementStatus.Started
        or GattServiceProviderAdvertisementStatus.StartedWithoutAllAdvertisementData;
    private volatile GattServiceProviderAdvertisementStatus _advStatus = GattServiceProviderAdvertisementStatus.Created;

    private const int MaxAdvRestarts = 8;
    private int _advFails;
    /// <summary>Windows answered StartAdvertising with Aborted/Stopped: this adapter refuses to advertise (the
    /// central-only PC). Not a hang, so BleAutoChannel must not report the stack as stuck.</summary>
    public bool AdvertisingRefused => _advFails > 0;
    private GattServiceProvider? _provider;
    private GattLocalCharacteristic? _rx, _tx;
    private readonly SemaphoreSlim _sendLock = new(1, 1);
    private readonly GattServiceProviderAdvertisingParameters _advParams = new() { IsConnectable = true, IsDiscoverable = true };

    // Cancelled to make RunAsync return, so that BleAutoChannel starts a fresh instance with a NEW GATT server.
    // Restarting the advertisement alone is not enough after sleep: the old server keeps a subscribed client
    // that is no longer there, every notification to it fails ("Value does not fall within the expected
    // range"), so the peer that connects hears nothing, never authenticates, and the two go round every 15 s
    // for hours (2026-09-30). Only a new GattServiceProvider sheds the ghost client.
    private CancellationTokenSource? _restart;
    private int _notifyFailures, _undelivered;
    private const int NotifyFailuresBeforeRestart = 6;

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
            _advStatus = e.Status;
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
        _restart = CancellationTokenSource.CreateLinkedTokenSource(ct);
        var loop = _restart.Token;
        while (!loop.IsCancellationRequested)
        {
            try { await Task.Delay(2000, loop); } catch (OperationCanceledException) { break; }
            var st = _advStatus = _provider.AdvertisementStatus;
            if (st is GattServiceProviderAdvertisementStatus.Aborted or GattServiceProviderAdvertisementStatus.Stopped)
            {
                if (_advFails >= MaxAdvRestarts)
                {
                    if (_advFails == MaxAdvRestarts) { _advFails++; Log.Warn($"BLE advertising will not start after {MaxAdvRestarts} tries; giving up (this PC acts as central only)"); }
                    continue;
                }
                _advFails++;
                Log.Warn($"BLE advertising is {st}; restarting ({_advFails}/{MaxAdvRestarts})");
                try { _provider.StartAdvertising(_advParams); }
                catch (Exception ex) { Log.Error("StartAdvertising: " + ex.Message); }
            }
            else if (st == GattServiceProviderAdvertisementStatus.Started) _advFails = 0;
        }
        try { _provider.StopAdvertising(); } catch { }
        _advStatus = GattServiceProviderAdvertisementStatus.Stopped;
        if (!ct.IsCancellationRequested)
        {
            // A restart, not the end: drop this server so nothing keeps it alive, and report the link gone.
            Log.Info("BLE peripheral: releasing the GATT server; a fresh one follows");
            _rx = null; _tx = null; _provider = null;
            if (IsLinked) { IsLinked = false; LinkChanged?.Invoke(false); }
            StatusChanged?.Invoke("restarting");
        }
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
            bool ok = results.Count > 0;
            foreach (var r in results)
                if (r.Status != GattCommunicationStatus.Success) ok = false;
            if (ok) { _notifyFailures = 0; _undelivered = 0; return true; }
            // Not an exception, so it used to pass in silence: a link whose notify direction had died looked
            // healthy from here for minutes (2026-10-02). Say so, sparingly; the session decides what to do.
            if (++_undelivered % 50 == 1)
                Log.Warn($"BLE notify not delivered ({(results.Count == 0 ? "no subscribed client" : results[0].Status.ToString())}); {_undelivered} in a row");
            return false;
        }
        catch (Exception ex)
        {
            Log.Warn("BLE notify failed: " + ex.Message);
            if (++_notifyFailures == NotifyFailuresBeforeRestart)
            {
                Log.Warn($"BLE peripheral: {NotifyFailuresBeforeRestart} notifications in a row failed; the subscribed client is a ghost, recreating the GATT server");
                Reconnect();
            }
            return false;
        }
        finally { _sendLock.Release(); }
    }

    /// <summary>Start over with a new GATT server (RunAsync returns; BleAutoChannel runs a fresh instance).
    /// The central owns the connection, and restarting only the advertisement leaves a ghost client behind.</summary>
    public void Reconnect()
    {
        var r = _restart;
        if (r == null || r.IsCancellationRequested) return;
        Log.Info("BLE peripheral: reconnect requested; recreating the GATT server");
        try { r.Cancel(); } catch { }
    }

    public void Dispose()
    {
        try { _provider?.StopAdvertising(); } catch { }
        _sendLock.Dispose();
    }
}
