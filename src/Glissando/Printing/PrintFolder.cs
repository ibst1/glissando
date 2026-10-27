namespace Glissando.Printing;

/// <summary>
/// The folder whose files are printed on the other PC: "Glissando print" in the user's profile. Any program can
/// print there without an installed printer (no administrator needed): print to "Microsoft Print to PDF" and save
/// in this folder. "Send to" and Glissando.exe --print copy a file here too. Files wait here while the other PC
/// is not linked; once sent they move to Sent (kept two weeks) or Failed. Files in the folder itself print one- or
/// two-sided as chosen in the tray menu; those in its One-sided and Two-sided subfolders print that way.
/// </summary>
internal sealed class PrintFolder : IDisposable
{
    public static string FolderPath => Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.UserProfile), "Glissando print");
    private static string SentPath => Path.Combine(FolderPath, "Sent");
    private static string FailedPath => Path.Combine(FolderPath, "Failed");
    private static string OneSidedPath => Path.Combine(FolderPath, "One-sided");
    private static string TwoSidedPath => Path.Combine(FolderPath, "Two-sided");

    private readonly FileSystemWatcher? _watcher;
    private readonly System.Threading.Timer _debounce;
    private readonly HashSet<string> _taken = new(StringComparer.OrdinalIgnoreCase);
    private readonly Func<bool> _ready;

    /// <summary>A file to send, and whether it must print two-sided (null: as chosen in the menu). Raised once per
    /// file, on a worker thread.</summary>
    public event Action<string, bool?>? FileReady;

    public PrintFolder(Func<bool> ready)
    {
        _ready = ready;
        _debounce = new System.Threading.Timer(_ => Scan());
        try
        {
            Directory.CreateDirectory(FolderPath);
            Directory.CreateDirectory(OneSidedPath);
            Directory.CreateDirectory(TwoSidedPath);
            foreach (var old in Directory.Exists(SentPath) ? Directory.EnumerateFiles(SentPath) : [])
                try { if (File.GetLastWriteTime(old) < DateTime.Now.AddDays(-14)) File.Delete(old); } catch { }
            _watcher = new FileSystemWatcher(FolderPath) { IncludeSubdirectories = true, NotifyFilter = NotifyFilters.FileName | NotifyFilters.Size | NotifyFilters.LastWrite };
            _watcher.Created += (_, _) => ScanSoon();
            _watcher.Renamed += (_, _) => ScanSoon();
            _watcher.Changed += (_, _) => ScanSoon();
            _watcher.EnableRaisingEvents = true;
        }
        catch (Exception ex) { Log.Warn("print folder: " + ex.Message); }
    }

    /// <summary>Look again in a second (a file being written raises many events).</summary>
    public void ScanSoon() => _debounce.Change(1000, Timeout.Infinite);

    /// <summary>Hand every printable file not yet handed on, if the other PC can take them now.</summary>
    public void Scan()
    {
        if (!_ready()) return;
        try
        {
            foreach (var (dir, twoSided) in new[] { (FolderPath, (bool?)null), (OneSidedPath, false), (TwoSidedPath, true) })
                foreach (var path in Directory.Exists(dir) ? Directory.EnumerateFiles(dir) : [])
                {
                    if (!PrintFrames.Printable(path)) continue;
                    lock (_taken) if (!_taken.Add(path)) continue;
                    FileReady?.Invoke(path, twoSided);
                }
        }
        catch (Exception ex) { Log.Warn("print folder scan: " + ex.Message); }
    }

    /// <summary>The file was sent: into Sent or Failed with it, under a name that is free there.</summary>
    public void Done(string path, bool printed)
    {
        try
        {
            string dir = printed ? SentPath : FailedPath;
            Directory.CreateDirectory(dir);
            string target = Path.Combine(dir, Path.GetFileName(path));
            for (int i = 2; File.Exists(target); i++)
                target = Path.Combine(dir, $"{Path.GetFileNameWithoutExtension(path)} ({i}){Path.GetExtension(path)}");
            File.Move(path, target);
        }
        catch (Exception ex) { Log.Warn($"print folder: moving {Path.GetFileName(path)}: {ex.Message}"); }
        finally { lock (_taken) _taken.Remove(path); }
    }

    /// <summary>For Glissando.exe --print, Send to and the Explorer menu: copy the file into the folder (or its
    /// One-sided or Two-sided subfolder), where the running instance picks it up (or the next one, when the other
    /// PC is linked).</summary>
    public static string Enqueue(string file, bool? twoSided = null)
    {
        string dir = twoSided switch { true => TwoSidedPath, false => OneSidedPath, null => FolderPath };
        Directory.CreateDirectory(dir);
        string target = Path.Combine(dir, Path.GetFileName(file));
        for (int i = 2; File.Exists(target); i++)
            target = Path.Combine(dir, $"{Path.GetFileNameWithoutExtension(file)} ({i}){Path.GetExtension(file)}");
        // Copy to a name the watcher ignores, then rename: the folder never shows half a file.
        string temp = target + ".part";
        File.Copy(file, temp);
        File.Move(temp, target);
        return target;
    }

    /// <summary>A "Send to" entry that runs Glissando.exe --print on the chosen files. Made once; returns its path
    /// when it was just made.</summary>
    public static string? EnsureSendTo()
    {
        try
        {
            string link = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.SendTo), "Print on the other PC (Glissando).lnk");
            string exe = Environment.ProcessPath ?? "";
            if (exe == "" || File.Exists(link) && Startup.ShortcutTarget(link) is string t && t.Equals(exe, StringComparison.OrdinalIgnoreCase)) return null;
            Startup.CreateShortcut(link, exe, "--print", "Print the file on the other PC's printer");
            return link;
        }
        catch (Exception ex) { Log.Warn("Send to shortcut: " + ex.Message); return null; }
    }

    /// <summary>"Print on PEER (one-sided)" and "(two-sided)" in Explorer's right-click menu of PDFs and pictures,
    /// running Glissando.exe --print. Under HKCU\Software\Classes, so no administrator is needed; Windows 11 shows
    /// such entries under "Show more options". Written only when something changed (another exe, another peer).</summary>
    public static void EnsureExplorerMenu(string peer)
    {
        try
        {
            string exe = Environment.ProcessPath ?? "";
            if (exe == "") return;
            foreach (var ext in PrintFrames.Extensions)
                foreach (var (verb, label, flag) in new[] { ("GlissandoPrint1", "one-sided", "--one-sided"), ("GlissandoPrint2", "two-sided", "--two-sided") })
                {
                    using var key = Microsoft.Win32.Registry.CurrentUser.CreateSubKey($@"Software\Classes\SystemFileAssociations\{ext}\shell\{verb}");
                    string text = $"Print on {peer} ({label})", command = $"\"{exe}\" --print {flag} \"%1\"";
                    if (key.GetValue("MUIVerb") as string == text && key.OpenSubKey("command")?.GetValue("") as string == command) continue;
                    key.SetValue("MUIVerb", text);
                    key.SetValue("Icon", exe + ",0");
                    // Up to some hundred files at once instead of Explorer's 15; each one starts its own --print.
                    key.SetValue("MultiSelectModel", "Player");
                    using var cmd = key.CreateSubKey("command");
                    cmd.SetValue("", command);
                }
        }
        catch (Exception ex) { Log.Warn("Explorer menu: " + ex.Message); }
    }

    public void Dispose()
    {
        _watcher?.Dispose();
        _debounce.Dispose();
    }
}
