using Microsoft.Win32;

namespace Glissando;

/// <summary>
/// The autostart entry and its one failure mode: HKCU\...\Run\Glissando is written with the path of the exe that
/// was running when the box was ticked. A newer build published under another path or name leaves the OLD exe
/// starting at logon; it takes the per-user mutex, and every newer start dies with "already running" (RS,
/// 2026-09-14: 0.3.44 kept starting while 0.3.46 sat in dist). So at every start the entry is re-pointed to the
/// exe actually running - always, not only when the old file is gone, since the old file usually still exists.
/// </summary>
public static class Startup
{
    private const string RunKey = @"Software\Microsoft\Windows\CurrentVersion\Run";
    private const string ValueName = "Glissando";

    /// <summary>Does a Run-key value ("C:\x\Glissando.exe", quoted or not, possibly with arguments) launch this exe?</summary>
    public static bool RunKeyPointsAt(string? value, string exe)
    {
        if (string.IsNullOrWhiteSpace(value) || string.IsNullOrWhiteSpace(exe)) return false;
        string v = value.Trim();
        string path;
        if (v.StartsWith('"'))
        {
            int end = v.IndexOf('"', 1);
            path = end > 0 ? v[1..end] : v[1..];
        }
        else
        {
            // Unquoted: the executable ends at the first ".exe" (paths with spaces are legal here without quotes).
            int i = v.IndexOf(".exe", StringComparison.OrdinalIgnoreCase);
            path = i > 0 ? v[..(i + 4)] : v;
        }
        return string.Equals(NormalizePath(path), NormalizePath(exe), StringComparison.OrdinalIgnoreCase);
    }

    private static string NormalizePath(string p)
    {
        try { return Path.GetFullPath(p.Trim()).TrimEnd('\\'); }
        catch { return p.Trim(); }
    }

    /// <summary>The registered Run-key value, or null when autostart is off.</summary>
    public static string? RegisteredValue()
    {
        try
        {
            using var k = Registry.CurrentUser.OpenSubKey(RunKey);
            return k?.GetValue(ValueName) as string;
        }
        catch { return null; }
    }

    /// <summary>If autostart is on but points elsewhere, point it at this exe. Returns what was done, for the log.</summary>
    public static string? RepointIfStale()
    {
        string? exe = Environment.ProcessPath;
        string? current = RegisteredValue();
        if (current == null || string.IsNullOrEmpty(exe) || RunKeyPointsAt(current, exe)) return null;
        try
        {
            using var k = Registry.CurrentUser.CreateSubKey(RunKey);
            k.SetValue(ValueName, $"\"{exe}\"");
            return $"start with Windows: re-pointed from {current} to \"{exe}\"";
        }
        catch (Exception ex) { return "start with Windows: could not re-point (" + ex.Message + ")"; }
    }
}

/// <summary>
/// Two named events let another Glissando process reach the running instance when its tray icon is not there to
/// click: "exit" (Glissando.exe --exit) and "setup" (a second start, or --setup, opens the settings dialog of the
/// instance that already runs instead of complaining that it exists).
/// </summary>
internal static class InstanceSignals
{
    private static string ExitName => "Glissando-exit-" + Environment.UserName;
    private static string SetupName => "Glissando-setup-" + Environment.UserName;
    public static string MutexName => "Glissando-" + Environment.UserName;

    private static EventWaitHandle? _exit, _setup;
    private static RegisteredWaitHandle? _exitWait, _setupWait;

    /// <summary>Running instance: create the events and run the callbacks when they are set (callbacks arrive on
    /// a pool thread; marshal to the UI yourself).</summary>
    public static void Listen(Action onExit, Action onSetup)
    {
        try
        {
            _exit = new EventWaitHandle(false, EventResetMode.AutoReset, ExitName);
            _setup = new EventWaitHandle(false, EventResetMode.AutoReset, SetupName);
            _exitWait = ThreadPool.RegisterWaitForSingleObject(_exit, (_, _) => onExit(), null, -1, executeOnlyOnce: true);
            _setupWait = ThreadPool.RegisterWaitForSingleObject(_setup, (_, _) => onSetup(), null, -1, executeOnlyOnce: false);
        }
        catch (Exception ex) { Log.Warn("instance signals: " + ex.Message); }
    }

    public static void Dispose()
    {
        try { _exitWait?.Unregister(null); _setupWait?.Unregister(null); _exit?.Dispose(); _setup?.Dispose(); } catch { }
        _exit = _setup = null;
    }

    /// <summary>Another process: ask the running instance to exit and wait, up to 10 s, for it to be gone (its
    /// mutex becomes free). False when no instance was listening.</summary>
    public static bool RequestExit()
    {
        if (!EventWaitHandle.TryOpenExisting(ExitName, out var ev)) return false;
        using (ev) ev.Set();
        try
        {
            using var m = new Mutex(false, MutexName);
            bool got;
            try { got = m.WaitOne(10000); }
            catch (AbandonedMutexException) { got = true; }   // it died holding the mutex: gone all the same
            if (got) { try { m.ReleaseMutex(); } catch { } }
            return got;
        }
        catch { return true; }
    }

    /// <summary>Another process: ask the running instance to open its settings. False when none is listening.</summary>
    public static bool RequestSetup()
    {
        if (!EventWaitHandle.TryOpenExisting(SetupName, out var ev)) return false;
        using (ev) ev.Set();
        return true;
    }
}
