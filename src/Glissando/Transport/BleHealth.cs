namespace Glissando.Transport;

/// <summary>
/// Is Windows' Bluetooth LE stack on this PC healthy? It can hang so that calls into it never return: the GATT
/// server is never created, advertising never starts, an old server cannot be released, a fresh link never
/// answers a lookup (LU, 2026-10-06, all morning). Glissando then works around each hang, but nothing it does
/// cures the stack, and from the outside it only looked like "advertising" on one PC and "restarting" on the
/// other, with no hint what to do.
///
/// So the places that see such a hang report it here, and this turns the reports into one plain verdict the
/// tray shows: a second sign is "turn Bluetooth off and on" (a single one says nothing yet); signs that keep
/// coming for several minutes (the toggle did not help, as that morning) are "restart Windows on this PC", the
/// only cure seen. A link that holds for three minutes clears it. The verdict also goes to the other PC, so it
/// can say which PC to restart.
/// </summary>
public static class BleHealth
{
    public enum Verdict { Fine, ToggleBluetooth, RestartWindows }

    private const long ForgetAfterMs = 30 * 60_000;   // signs older than this no longer count
    private const long PersistsMs = 3 * 60_000;       // signs spread over this long: toggling did not help
    // One sign alone is not a stuck stack: a lookup that times out once right after either PC restarted is
    // known and harmless, and it put "BLUETOOTH STUCK" on the tray for ten minutes of a working link
    // (2026-10-06). Two signs are.
    private const int SignsForVerdict = 2;

    private static readonly object Gate = new();
    private static readonly List<long> Signs = [];    // Environment.TickCount64 of each report

    /// <summary>Raised (on any thread) whenever the verdict may have changed.</summary>
    public static event Action? Changed;

    /// <summary>The latest sign, in words, for the tray and the diagnostics.</summary>
    public static string LastSign { get; private set; } = "";

    public static void Report(string sign)
    {
        Verdict v;
        lock (Gate)
        {
            Signs.Add(Environment.TickCount64);
            LastSign = sign;
            v = VerdictLocked();
        }
        Log.Warn($"Bluetooth health: {sign} -> {(v == Verdict.Fine ? "one sign, watching" : Describe(v))}");
        Changed?.Invoke();
    }

    public static Verdict Current { get { lock (Gate) return VerdictLocked(); } }

    private static Verdict VerdictLocked()
    {
        long now = Environment.TickCount64;
        Signs.RemoveAll(t => now - t > ForgetAfterMs);
        if (Signs.Count < SignsForVerdict) return Verdict.Fine;
        return Signs[^1] - Signs[0] >= PersistsMs ? Verdict.RestartWindows : Verdict.ToggleBluetooth;
    }

    /// <summary>The link has held for a good while (three minutes): whatever was wrong is over.</summary>
    public static void LinkHeld()
    {
        lock (Gate)
        {
            if (Signs.Count == 0) return;
            Signs.Clear();
            LastSign = "";
        }
        Log.Info("Bluetooth health: the link has held for 3 minutes; the stack is fine again");
        Changed?.Invoke();
    }

    public static string Describe(Verdict v) => v switch
    {
        Verdict.ToggleBluetooth => "turn Bluetooth off and on",
        Verdict.RestartWindows => "restart Windows",
        _ => "fine",
    };
}
