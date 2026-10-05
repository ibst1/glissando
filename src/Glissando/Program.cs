using System.Reflection;

namespace Glissando;

internal static class Program
{
    /// <summary>Short build id, e.g. "0.3.7+dc7d57e" (set by build.ps1; "0.3.0" for a plain IDE build).</summary>
    public static string Version { get; } = ComputeVersion(false);

    /// <summary>Build id with the build time, for humans.</summary>
    public static string VersionLong { get; } = ComputeVersion(true);

    private static string ComputeVersion(bool withTime)
    {
        var asm = Assembly.GetExecutingAssembly();
        var info = asm.GetCustomAttribute<AssemblyInformationalVersionAttribute>()?.InformationalVersion
                   ?? asm.GetName().Version?.ToString(3) ?? "0.0.0";
        if (!withTime) return info;
        var time = asm.GetCustomAttributes<AssemblyMetadataAttribute>().FirstOrDefault(a => a.Key == "BuildTime")?.Value;
        return string.IsNullOrEmpty(time) ? info : $"{info} ({time})";
    }

    [STAThread]
    private static int Main(string[] args)
    {
        string? settingsPath = null;
        bool console = false, setup = false, mirrorTest = false, exitOther = false;
        string? mirrorTitle = null;
        var overrides = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);

        for (int i = 0; i < args.Length; i++)
        {
            switch (args[i].ToLowerInvariant())
            {
                case "--console": console = true; break;
                case "--setup": setup = true; break;
                case "--exit": exitOther = true; break;
                case "--keylog": Host.InputCapture.KeyLog = true; break;
                case "--cursor-test":
                    // Hidden: blank the system cursors for two seconds and restore them, to check the mechanism.
                    Host.CursorHider.Hide();
                    Thread.Sleep(2000);
                    Host.CursorHider.RestoreAll();
                    return 0;
                case "--settings": settingsPath = Next(args, ref i); break;
                case "--ble": overrides["ble"] = Next(args, ref i); break;
                case "--mirror-test":
                    // Window-mirroring increment 1: measure a window's stream cost. Optional title follows.
                    mirrorTest = true;
                    if (i + 1 < args.Length && !args[i + 1].StartsWith('-')) mirrorTitle = args[++i];
                    break;
                case "--help":
                case "-h":
                case "/?":
                    Native.AllocConsole();
                    Console.WriteLine(Usage);
                    return 0;
                default:
                    Native.AllocConsole();
                    Console.WriteLine($"Unknown argument: {args[i]}");
                    Console.WriteLine(Usage);
                    return 1;
            }
        }

        if (console)
        {
            Native.AllocConsole();
            Log.EchoToConsole = true;
        }

        Native.timeBeginPeriod(1);
        ApplicationConfiguration.Initialize();

        if (mirrorTest)
        {
            Native.AllocConsole();
            IntPtr target = string.IsNullOrEmpty(mirrorTitle) ? Mirror.WindowCapture.Foreground() : Mirror.WindowCapture.Find(mirrorTitle);
            if (target == IntPtr.Zero)
            {
                Console.WriteLine(string.IsNullOrEmpty(mirrorTitle) ? "No foreground window." : $"No window whose title contains \"{mirrorTitle}\".");
                return 1;
            }
            Mirror.MirrorProbe.Run(target, seconds: 12, fps: 5, quality: 60);
            Console.WriteLine("\nDone. Press Enter to close.");
            Console.ReadLine();
            return 0;
        }

        // If the cursor set was swapped out while controlling the other PC, or a mirrored window was made
        // invisible, put them back no matter how we end.
        AppDomain.CurrentDomain.ProcessExit += (_, _) => { Host.CursorHider.RestoreAll(); Mirror.WindowHider.UnhideAll(); };
        AppDomain.CurrentDomain.UnhandledException += (_, _) => { Host.CursorHider.RestoreAll(); Mirror.WindowHider.UnhideAll(); };

        // "Glissando.exe --exit": stop the instance that runs, for when its tray icon is not there to click.
        if (exitOther)
        {
            bool gone = InstanceSignals.RequestExit();
            Native.AllocConsole();
            Console.WriteLine(gone ? "Glissando has exited." : "Glissando is not running.");
            return gone ? 0 : 1;
        }

        // One instance per user. Wait a little so "restart" from the settings dialog works.
        using var single = new Mutex(false, InstanceSignals.MutexName);
        bool owned;
        try { owned = single.WaitOne(5000); }
        catch (AbandonedMutexException) { owned = true; }
        if (!owned)
        {
            // Another instance runs. Hand it the request instead of a box: a second start (or --setup) means
            // "show me the settings" - its tray icon may well be missing (see TrayIconProbe), and the old box
            // only said to look for it. The box remains for an instance too old to listen.
            if (InstanceSignals.RequestSetup()) return 0;
            MessageBox.Show("Glissando is already running. Its icon may be hidden in the taskbar's ^ overflow.\n\nTo stop it, run:  Glissando.exe --exit", "Glissando", MessageBoxButtons.OK, MessageBoxIcon.Information);
            return 1;
        }

        var settings = Settings.Load(settingsPath);
        // Autostart must launch THIS exe, whichever exe ticked the box (see Startup): a newer build under another
        // path otherwise loses to the old one at every logon.
        if (Startup.RepointIfStale() is string repointed) Log.Info(repointed);
        // Always reload the cursor scheme at startup. If any previous instance was killed while the cursor
        // was hidden, the whole system is left with blank cursors; this heals it whatever the setting says,
        // so the user can never be stuck unable to click. It just reloads the user's own cursors otherwise.
        Host.CursorHider.RestoreSchemeAtStartup();
        // Likewise a window left invisible by an instance that was killed while mirroring it.
        Mirror.WindowHider.HealAtStartup();
        if (overrides.TryGetValue("ble", out var b) && Enum.TryParse<BleMode>(b, true, out var ble)) settings.Ble = ble;

        if (setup || string.IsNullOrWhiteSpace(settings.Passphrase))
        {
            using var form = new SettingsForm(settings, firstRun: true, peerName: "");
            if (form.ShowDialog() != DialogResult.OK)
            {
                Log.Info("setup cancelled");
                return 0;
            }
            settings.Save();
        }

        Log.Info($"Glissando {Version} starting: ble={settings.Ble} peer origin=({settings.PeerX},{settings.PeerY}) capture={settings.CaptureEnabled} settings={Settings.FilePath}");
        try
        {
            Application.Run(new TrayContext(settings));
        }
        catch (Exception ex)
        {
            Host.CursorHider.RestoreAll();
            Mirror.WindowHider.UnhideAll();
            Log.Error("fatal: " + ex);
            MessageBox.Show(ex.ToString(), "Glissando crashed", MessageBoxButtons.OK, MessageBoxIcon.Error);
            return 2;
        }
        Log.Info("exit");
        return 0;
    }

    private static string Next(string[] args, ref int i)
    {
        if (i + 1 >= args.Length) throw new ArgumentException($"{args[i]} needs a value");
        return args[++i];
    }

    private const string Usage = """
        Glissando - share one keyboard and mouse between two PCs over Bluetooth LE.

        Glissando.exe [--setup] [--exit] [--ble auto|advertise|connect] [--settings <path>] [--console]

          --setup     open the settings dialog before starting (if Glissando already runs, opens its settings)
          --exit      stop the running Glissando (for when its tray icon is not there to click)
          --ble       which PC is the BLE peripheral (auto decides by itself)
          --settings  settings file (default %APPDATA%\Glissando\settings.json)
          --console   show a console window with the log
          --mirror-test [title]  measure a window's mirroring data rate (foreground if no title)

        Command-line values override the settings file for this run only.
        """;
}
