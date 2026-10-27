namespace Glissando.Printing;

/// <summary>
/// The folder whose files are printed on the other PC: "Glissando print" in the user's profile. Any program can
/// print there without an installed printer (no administrator needed): print to "Microsoft Print to PDF" and save
/// in this folder. "Send to" and Glissando.exe --print copy a file here too. Files wait here while the other PC
/// is not linked; once sent they move to Sent (kept two weeks) or Failed.
/// </summary>
internal sealed class PrintFolder : IDisposable
{
    public static string FolderPath => Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.UserProfile), "Glissando print");
    private static string SentPath => Path.Combine(FolderPath, "Sent");
    private static string FailedPath => Path.Combine(FolderPath, "Failed");

    private readonly FileSystemWatcher? _watcher;
    private readonly System.Threading.Timer _debounce;
    private readonly HashSet<string> _taken = new(StringComparer.OrdinalIgnoreCase);
    private readonly Func<bool> _ready;

    /// <summary>A file to send, raised once per file (on a worker thread).</summary>
    public event Action<string>? FileReady;

    public PrintFolder(Func<bool> ready)
    {
        _ready = ready;
        _debounce = new System.Threading.Timer(_ => Scan());
        try
        {
            Directory.CreateDirectory(FolderPath);
            foreach (var old in Directory.Exists(SentPath) ? Directory.EnumerateFiles(SentPath) : [])
                try { if (File.GetLastWriteTime(old) < DateTime.Now.AddDays(-14)) File.Delete(old); } catch { }
            _watcher = new FileSystemWatcher(FolderPath) { IncludeSubdirectories = false, NotifyFilter = NotifyFilters.FileName | NotifyFilters.Size | NotifyFilters.LastWrite };
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
            foreach (var path in Directory.EnumerateFiles(FolderPath))
            {
                if (!PrintFrames.Printable(path)) continue;
                lock (_taken) if (!_taken.Add(path)) continue;
                FileReady?.Invoke(path);
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

    /// <summary>For Glissando.exe --print and Send to: copy the file into the folder, where the running instance
    /// picks it up (or the next one, when the other PC is linked).</summary>
    public static string Enqueue(string file)
    {
        Directory.CreateDirectory(FolderPath);
        string target = Path.Combine(FolderPath, Path.GetFileName(file));
        for (int i = 2; File.Exists(target); i++)
            target = Path.Combine(FolderPath, $"{Path.GetFileNameWithoutExtension(file)} ({i}){Path.GetExtension(file)}");
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

    public void Dispose()
    {
        _watcher?.Dispose();
        _debounce.Dispose();
    }
}
