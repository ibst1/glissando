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
        bool console = false, setup = false;
        var overrides = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);

        for (int i = 0; i < args.Length; i++)
        {
            switch (args[i].ToLowerInvariant())
            {
                case "--console": console = true; break;
                case "--setup": setup = true; break;
                case "--keylog": Host.InputCapture.KeyLog = true; break;
                case "--cursor-test":
                    // Hidden: blank the system cursors for two seconds and restore them, to check the mechanism.
                    Host.CursorHider.Hide();
                    Thread.Sleep(2000);
                    Host.CursorHider.Restore();
                    return 0;
                case "--settings": settingsPath = Next(args, ref i); break;
                case "--ble": overrides["ble"] = Next(args, ref i); break;
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

        // If the cursor set was swapped out while controlling the other PC, put it back no matter how we end.
        AppDomain.CurrentDomain.ProcessExit += (_, _) => Host.CursorHider.Restore();
        AppDomain.CurrentDomain.UnhandledException += (_, _) => Host.CursorHider.Restore();

        // One instance per user. Wait a little so "restart" from the settings dialog works.
        using var single = new Mutex(false, "Glissando-" + Environment.UserName);
        bool owned;
        try { owned = single.WaitOne(5000); }
        catch (AbandonedMutexException) { owned = true; }
        if (!owned)
        {
            MessageBox.Show("Glissando is already running (see the tray icon).", "Glissando", MessageBoxButtons.OK, MessageBoxIcon.Information);
            return 1;
        }

        var settings = Settings.Load(settingsPath);
        // If a previous instance was killed while the cursor was hidden, the whole system is left with
        // blank cursors. Put them back now, before anything else, so the user is never stuck unable to click.
        if (settings.HideCursorWhileControlling) Host.CursorHider.RestoreSchemeAtStartup();
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
            Host.CursorHider.Restore();
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

        Glissando.exe [--setup] [--ble auto|advertise|connect] [--settings <path>] [--console]

          --setup     open the settings dialog before starting
          --ble       which PC is the BLE peripheral (auto decides by itself)
          --settings  settings file (default %APPDATA%\Glissando\settings.json)
          --console   show a console window with the log

        Command-line values override the settings file for this run only.
        """;
}
