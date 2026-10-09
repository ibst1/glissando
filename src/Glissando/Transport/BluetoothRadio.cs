using Windows.Devices.Radios;

namespace Glissando.Transport;

/// <summary>
/// Whether this PC's Bluetooth radio is switched on. With it off (the quick-settings button, airplane mode,
/// or Windows' power saving across a sleep) every BLE call fails, but only as "advertising Aborted" and
/// "RadioNotAvailable": on 2026-10-08 LU's radio went off at 18:00 and Glissando retried all night, the
/// tray saying "advertising Aborted", nothing saying that Bluetooth was simply off. This watches the radio
/// and says so; when it comes back on, the link starts over at once instead of after the 5-minute backoff.
/// </summary>
public static class BluetoothRadio
{
    private static Radio? _radio;

    /// <summary>True while the radio is on, false while off, null when it cannot be told (no radio found).</summary>
    public static bool? IsOn { get; private set; }

    /// <summary>Raised (on any thread) when the radio is switched on or off.</summary>
    public static event Action<bool>? Changed;

    /// <summary>Find the Bluetooth radio and follow its state. Safe to call once at start.</summary>
    public static async Task StartAsync()
    {
        try
        {
            var radios = await Radio.GetRadiosAsync();
            _radio = radios.FirstOrDefault(r => r.Kind == RadioKind.Bluetooth);
            if (_radio == null) { Log.Warn("Bluetooth radio: none found"); return; }
            Update(_radio.State);
            _radio.StateChanged += (r, _) => Update(r.State);
        }
        catch (Exception ex) { Log.Warn("Bluetooth radio: cannot follow its state: " + ex.Message); }
    }

    private static void Update(RadioState state)
    {
        bool on = state == RadioState.On;
        if (IsOn == on) return;
        bool first = IsOn == null;
        IsOn = on;
        Log.Info($"Bluetooth radio is {(on ? "on" : "OFF")}" + (on ? "" : " - nothing can link until it is switched on"));
        if (!first) Changed?.Invoke(on);
        else if (!on) Changed?.Invoke(false);
    }
}
