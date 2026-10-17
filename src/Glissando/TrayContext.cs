using System.Diagnostics;
using System.Drawing.Drawing2D;
using Glissando.Client;
using Glissando.Host;
using Glissando.Transport;

namespace Glissando;

/// <summary>Hidden top-level window: receives WM_INPUT and gives us an STA thread to invoke on.</summary>
internal sealed class HiddenWindow : Form
{
    public event Action<IntPtr>? RawInput;
    public event Action? DisplayChanged;

    public HiddenWindow()
    {
        Text = "Glissando";
        ShowInTaskbar = false;
        FormBorderStyle = FormBorderStyle.None;
        StartPosition = FormStartPosition.Manual;
        Location = new Point(-32000, -32000);
        Size = new Size(1, 1);
        Opacity = 0;
        _ = Handle; // create the window without showing it
    }

    protected override void SetVisibleCore(bool value) => base.SetVisibleCore(false);

    protected override void WndProc(ref Message m)
    {
        if (m.Msg == Native.WM_INPUT) RawInput?.Invoke(m.LParam);
        else if (m.Msg == Native.WM_DISPLAYCHANGE) DisplayChanged?.Invoke();
        base.WndProc(ref m);
    }
}

internal sealed class TrayContext : ApplicationContext, IClipboardAccess
{
    private readonly Settings _settings;
    private readonly HiddenWindow _window = new();
    private readonly ScrollCatcher _scrollCatcher = new();
    private readonly NotifyIcon _tray;
    private readonly ToolStripMenuItem _status;
    private readonly Session _session;
    private readonly InputCapture _capture;
    private readonly InputInjector _injector;
    private readonly Icon _iconIdle, _iconReady, _iconControlling, _iconControlled, _iconLocked, _iconError;
    private readonly ToolStripMenuItem _lockItem;
    private readonly ToolStripMenuItem _mirrorItem;
    private readonly ToolStripMenuItem _bluetoothItem;
    private string _bluetoothShown = "";
    private readonly ContextMenuStrip _menu;
    private readonly List<ToolStripItem> _mirrorStopItems = [];
    // Viewer side: one receiver (and window) per mirror id. An id just ended is ignored for a few seconds, so a
    // late chunk of a stopped mirror cannot open a fresh empty window. Touched on the receive threads.
    private readonly Dictionary<byte, Mirror.MirrorReceiver> _mirrorRx = [];
    private readonly Dictionary<byte, long> _mirrorEnded = [];
    private readonly System.Windows.Forms.Timer _fgTimer = new() { Interval = 400 };
    private readonly System.Threading.Timer _uiProbe;
    // The icon watchdog (see TrayIconProbe): checks at 2, 5, 10, 20, 40, 60 and 120 s after start.
    private readonly System.Windows.Forms.Timer _trayCheck = new();
    private static readonly int[] TrayCheckDelaysMs = [2000, 3000, 5000, 10000, 20000, 20000, 60000];
    private int _trayCheckStep;
    private bool? _trayVerified;
    private IntPtr _lastRealWindow;
    private SettingsForm? _openForm;
    private readonly Microsoft.Win32.PowerModeChangedEventHandler _powerHandler;
    private readonly Microsoft.Win32.SessionSwitchEventHandler _sessionHandler;

    public TrayContext(Settings settings)
    {
        _settings = settings;
        _iconIdle = TrayIcons.Make(Color.FromArgb(150, 150, 150));
        _iconReady = TrayIcons.Make(Color.FromArgb(76, 175, 80));
        _iconControlling = TrayIcons.Make(Color.FromArgb(66, 133, 244));
        _iconControlled = TrayIcons.Make(Color.FromArgb(255, 152, 0));
        _iconLocked = TrayIcons.Make(Color.FromArgb(171, 71, 188));
        _iconError = TrayIcons.Make(Color.FromArgb(229, 57, 53));

        _status = new ToolStripMenuItem("Starting") { Enabled = false };
        _lockItem = new ToolStripMenuItem("Lock cursor to this PC") { ShortcutKeyDisplayString = "Ctrl+Alt+L", CheckOnClick = true };
        _lockItem.Click += (_, _) => _session?.SetLocked(_lockItem.Checked);
        _mirrorItem = new ToolStripMenuItem("Mirror the active window to the other PC");
        _mirrorItem.Click += (_, _) => StartMirror();
        _bluetoothItem = new ToolStripMenuItem { Visible = false, ForeColor = Color.FromArgb(198, 40, 40), Font = new Font(SystemFonts.MenuFont ?? SystemFonts.DefaultFont, FontStyle.Bold) };
        _bluetoothItem.Click += (_, _) => ShowBluetoothBalloon(_session.BluetoothProblem);
        var menu = _menu = new ContextMenuStrip();
        menu.Opening += (_, _) => RebuildMirrorStopItems();
        menu.Items.Add(new ToolStripMenuItem($"Glissando {Program.VersionLong}") { Enabled = false });
        menu.Items.Add(_status);
        menu.Items.Add(_bluetoothItem);
        menu.Items.Add(new ToolStripSeparator());
        menu.Items.Add(_lockItem);
        menu.Items.Add(_mirrorItem);
        menu.Items.Add("Settings...", null, (_, _) => OpenSettings());
        menu.Items.Add("Reconnect", null, (_, _) => _session?.Reconnect());
        menu.Items.Add("Open log", null, (_, _) => OpenPath(Log.FilePath));
        menu.Items.Add("Copy diagnostics", null, (_, _) => CopyDiagnostics());
        menu.Items.Add(new ToolStripSeparator());
        menu.Items.Add("Exit", null, (_, _) => Exit());
        // Track the last real application window that was in front, so "Mirror the active window" targets what
        // the user was actually using -- reading the foreground when the tray menu opens grabs the wrong (tiny
        // shell) window, since clicking the tray already moved focus.
        _fgTimer.Tick += (_, _) => TrackForeground();
        _fgTimer.Start();
        // How promptly does this UI thread answer? It runs our low-level mouse and keyboard hooks, so a stall here
        // stalls every key and move on this PC, injected ones included. Probed four times a second.
        _uiProbe = new System.Threading.Timer(_ =>
        {
            long t0 = System.Diagnostics.Stopwatch.GetTimestamp();
            try
            {
                _window.BeginInvoke(() =>
                {
                    int ms = (int)((System.Diagnostics.Stopwatch.GetTimestamp() - t0) * 1000 / System.Diagnostics.Stopwatch.Frequency);
                    int cur;
                    while (ms > (cur = Volatile.Read(ref Session.UiLagMaxMs)) && Interlocked.CompareExchange(ref Session.UiLagMaxMs, ms, cur) != cur) { }
                    if (ms > 500) Log.Warn($"UI thread answered after {ms} ms (it runs the input hooks)");
                });
            }
            catch { }
        }, null, 1000, 250);

        _tray = new NotifyIcon { Icon = _iconIdle, Visible = true, Text = "Glissando", ContextMenuStrip = menu };
        _tray.DoubleClick += (_, _) => OpenSettings();
        // Explorer may have refused the icon (NIM_ADD times out on a busy shell at logon) and WinForms does not
        // notice; verify and re-add until Explorer holds it. And listen for another Glissando process asking us
        // to exit or to show the settings, which is how the instance is reached when the icon is missing.
        _trayCheck.Tick += (_, _) => CheckTrayIcon();
        StartTrayCheck();
        InstanceSignals.Listen(onExit: () => _window.BeginInvoke(Exit), onSetup: () => _window.BeginInvoke(OpenSettings));
        // A passphrase from before the strong generator still works, but a recording of the radio traffic is
        // enough to guess it (see Passphrases). The settings dialog says so; a balloon at every start was a nag.
        if (!Passphrases.IsStrong(settings.Passphrase))
            Log.Warn("the passphrase is weak; generate a new one in the settings");

        var layout = Layout.FromSettings(settings);
        _capture = new InputCapture(layout, _window.Handle);
        _window.RawInput += _capture.OnRawInput;
        _capture.Install();
        // Touchpad scroll reaches no hook; a window under the frozen cursor catches it (see ScrollCatcher).
        _scrollCatcher.Wheel += _capture.ForwardWheel;
        // These may now be raised on the input-hook thread (a hotkey): the scroll catcher is a form of the UI thread.
        _capture.EnteredRemote += () => { Native.GetCursorPos(out var p); _window.BeginInvoke(() => _scrollCatcher.ShowAt(new Point(p.X, p.Y))); };
        _capture.LeftRemote += () => _window.BeginInvoke(_scrollCatcher.HideAway);

        _injector = new InputInjector(layout);
        // The open dialog redraws this PC's screens too: they may just have been rearranged from its Screens tab.
        _window.DisplayChanged += () => { _session?.ScreenChanged(); _openForm?.Reload(_settings, _session?.PeerName ?? ""); };

        IChannel channel = settings.Ble switch
        {
            BleMode.Advertise => new BlePeripheralChannel(),
            BleMode.Connect => new BleCentralChannel(),
            _ => new BleAutoChannel(() => settings.BleLastRole, r => { settings.BleLastRole = r; settings.Save(); }),
        };

        _session = new Session(settings, channel, _capture, _injector, this);
        _session.StateChanged += () => _window.BeginInvoke(() => { UpdateTray(); _openForm?.SetPeerLinked(_session.Authenticated); });
        _session.SettingsChanged += () => _window.BeginInvoke(() => _openForm?.Reload(_settings, _session.PeerName));
        _session.IdentifyRequested += () => _window.BeginInvoke(() => IdentifyOverlay.ShowAll(otherPc: true));
        // Incoming window-mirroring frames arrive on the receive threads; the receiver reassembles and decodes
        // there and asks for the UI thread only to paint.
        _session.MirrorReceived += OnMirrorFrame;

        // Let go of the radio before sleep and start over after it: a live GATT server carried through
        // suspend is what left the Bluetooth stack with a dead LE server (2026-09-13). Suspend runs inline
        // (the handler has a couple of seconds); Resume is marshalled like everything else.
        _powerHandler = (_, e) =>
        {
            if (e.Mode == Microsoft.Win32.PowerModes.Suspend) _session.Suspend();
            else if (e.Mode == Microsoft.Win32.PowerModes.Resume) _window.BeginInvoke(() => { _session.Resume(); StartTrayCheck(); });
        };
        Microsoft.Win32.SystemEvents.PowerModeChanged += _powerHandler;

        // Lock screen: release whatever we hold and tell the peer.
        _sessionHandler = (_, e) =>
        {
            if (e.Reason == Microsoft.Win32.SessionSwitchReason.SessionLock) _window.BeginInvoke(() => _session.SetLocalLocked(true));
            else if (e.Reason == Microsoft.Win32.SessionSwitchReason.SessionUnlock) _window.BeginInvoke(() => { _session.SetLocalLocked(false); StartTrayCheck(); });
        };
        Microsoft.Win32.SystemEvents.SessionSwitch += _sessionHandler;

        _session.Start();
        UpdateTray();
    }

    private bool _versionWarned;

    // ---- tray icon watchdog ----

    private void StartTrayCheck()
    {
        _trayCheck.Stop();
        _trayCheckStep = 0;
        _trayVerified = null;
        _trayCheck.Interval = TrayCheckDelaysMs[0];
        _trayCheck.Start();
    }

    private void CheckTrayIcon()
    {
        _trayCheck.Stop();
        int n = _trayCheckStep + 1;
        bool? ok = TrayIconProbe.IsRegistered(_tray);
        if (ok == true)
        {
            _trayVerified = true;
            Log.Info(n > 1 ? $"tray icon verified after re-adding it ({n - 1}x)" : "tray icon verified");
            return;
        }
        if (ok == null)
        {
            // Explorer cannot be asked (NotifyIcon's internals changed): one blind re-add at the second check.
            if (n == 2) { Log.Warn("tray icon: cannot verify it; re-adding once blindly"); ReAddTrayIcon(); return; }
        }
        else
        {
            Log.Warn($"tray icon missing, re-adding ({n})");
            ReAddTrayIcon();
        }
        _trayCheckStep++;
        if (_trayCheckStep >= TrayCheckDelaysMs.Length)
        {
            _trayVerified = false;
            Log.Warn("tray icon: Explorer still does not hold it after two minutes of retries (Glissando.exe --exit still works)");
            return;
        }
        _trayCheck.Interval = TrayCheckDelaysMs[_trayCheckStep];
        _trayCheck.Start();
    }

    /// <summary>NIM_DELETE then NIM_ADD: WinForms resets its "added" flag in between, so the add is really retried.</summary>
    private void ReAddTrayIcon()
    {
        try { _tray.Visible = false; _tray.Visible = true; }
        catch (Exception ex) { Log.Warn("tray icon re-add: " + ex.Message); }
    }

    /// <summary>Remember the front application window (not ours, visible, titled, of a real size), so mirroring
    /// can target the window the user was using rather than whatever tiny window has focus at menu time.</summary>
    private void TrackForeground()
    {
        var h = Mirror.WindowCapture.Foreground();
        // A mirrored window comes to the front whenever the viewer clicks into it; that is not the user choosing it.
        if (_session.IsMirroring(h)) return;
        if (h == IntPtr.Zero || h == _window.Handle) return;
        if (Mirror.WindowCapture.ProcessId(h) == (uint)Environment.ProcessId) return;
        if (!Mirror.WindowCapture.Visible(h) || string.IsNullOrEmpty(Mirror.WindowCapture.Title(h))) return;
        if (Mirror.WindowCapture.SizeOf(h) is not Size s || s.Width < 200 || s.Height < 150) return;
        _lastRealWindow = h;
    }

    /// <summary>Mirror the window last used, beside any already mirrored.</summary>
    private void StartMirror()
    {
        var h = _lastRealWindow;
        if (h == IntPtr.Zero || Mirror.WindowCapture.SizeOf(h) == null)
        {
            _tray.ShowBalloonTip(3000, "Glissando", "No window to mirror. Click the window you want, then use the tray menu.", ToolTipIcon.Info);
            return;
        }
        string title = Mirror.WindowCapture.Title(h);
        if (!_session.StartMirror(h))
        {
            _tray.ShowBalloonTip(3000, "Glissando", $"\"{title}\" is already mirrored. Click another window first to mirror that one too.", ToolTipIcon.Info);
            return;
        }
        // Name both PCs: a window titled "... - RS - Edge" on LU, mirrored from LU's tray by mistake, read as
        // RS failing to hide its own window (2026-10-06).
        string direction = $"from this PC ({Environment.MachineName}) to {PeerLabel}";
        _tray.ShowBalloonTip(5000, "Glissando", _session.MirrorHidden(h)
            ? $"Mirroring \"{title}\" {direction}. It is invisible here until the mirror stops, or until you click or type on this PC."
            : $"Mirroring \"{title}\" {direction}.", ToolTipIcon.Info);
        UpdateTray();
    }

    private string PeerLabel => string.IsNullOrEmpty(_session.PeerName) ? "the other PC" : _session.PeerName;

    private static string Short(string title, int max = 50) =>
        (title == "" ? "(untitled)" : title.Length > max ? title[..(max - 3)] + "..." : title).Replace("&", "&&");

    /// <summary>The mirror items, rebuilt each time the menu opens, each saying which way it goes: the one that
    /// starts a mirror names the window and both PCs; one "Stop mirroring" per window this PC mirrors to the
    /// other (plus "Stop all" for several); one "Close" per window the other PC mirrors to this one.</summary>
    private void RebuildMirrorStopItems()
    {
        foreach (var item in _mirrorStopItems) { _menu.Items.Remove(item); item.Dispose(); }
        _mirrorStopItems.Clear();
        var h = _lastRealWindow;
        _mirrorItem.Text = h != IntPtr.Zero && Mirror.WindowCapture.SizeOf(h) != null && !_session.IsMirroring(h)
            ? $"Mirror \"{Short(Mirror.WindowCapture.Title(h), 40)}\" from this PC to {PeerLabel}"
            : $"Mirror the active window from this PC to {PeerLabel}";
        var windows = _session.MirroredWindows;
        int at = _menu.Items.IndexOf(_mirrorItem) + 1;
        foreach (var (hwnd, title) in windows)
        {
            var item = new ToolStripMenuItem($"Stop mirroring \"{Short(title)}\" (this PC \u2192 {PeerLabel})");
            item.Click += (_, _) => { _session.StopMirror(hwnd); UpdateTray(); };
            _mirrorStopItems.Add(item);
        }
        if (windows.Length > 1)
        {
            var all = new ToolStripMenuItem("Stop all mirrors");
            all.Click += (_, _) => { _session.StopAllMirrors(); UpdateTray(); };
            _mirrorStopItems.Add(all);
        }
        Mirror.MirrorReceiver[] incoming;
        lock (_mirrorRx) incoming = [.. _mirrorRx.Values];
        foreach (var rx in incoming)
        {
            var item = new ToolStripMenuItem($"Close mirror of \"{Short(rx.Title)}\" ({PeerLabel} \u2192 this PC)");
            item.Click += (_, _) => rx.StopFromViewer();
            _mirrorStopItems.Add(item);
        }
        foreach (var item in _mirrorStopItems) _menu.Items.Insert(at++, item);
    }

    /// <summary>Receive thread: route a mirror frame to its receiver, opening one for a new mirror.</summary>
    private void OnMirrorFrame(Frame f)
    {
        byte id = Frame.MirrorIdOf(f);
        Mirror.MirrorReceiver? rx;
        lock (_mirrorRx)
        {
            long now = Environment.TickCount64;
            if (_mirrorEnded.TryGetValue(id, out long endedAt))
            {
                if (now - endedAt < 5000) return;
                _mirrorEnded.Remove(id);
            }
            if (f.Type == FrameType.MirrorEnd)
            {
                _mirrorEnded[id] = now;
                if (!_mirrorRx.Remove(id, out rx)) return;
            }
            else if (!_mirrorRx.TryGetValue(id, out rx))
            {
                rx = new Mirror.MirrorReceiver(id)
                {
                    Ui = a => { try { _window.BeginInvoke(a); } catch (Exception ex) { Log.Warn("mirror ui: " + ex.Message); } },
                    Send = _session.SendMirrorInput,
                    FrameColor = () => _settings.MirrorFrame,
                };
                _mirrorRx[id] = rx;
            }
        }
        rx.Handle(f);
    }

    /// <summary>UI thread: close every viewer window (exit).</summary>
    private void CloseAllMirrorReceivers()
    {
        Mirror.MirrorReceiver[] all;
        lock (_mirrorRx) { all = [.. _mirrorRx.Values]; _mirrorRx.Clear(); }
        foreach (var rx in all) rx.Close();
    }

    private async void CopyDiagnostics()
    {
        string adapter;
        try
        {
            var a = await Windows.Devices.Bluetooth.BluetoothAdapter.GetDefaultAsync();
            adapter = a == null ? "no adapter"
                : $"{a.BluetoothAddress:X12} LE={a.IsLowEnergySupported} central={a.IsCentralRoleSupported} peripheral={a.IsPeripheralRoleSupported} extAdv={a.IsExtendedAdvertisingSupported}";
        }
        catch (Exception ex) { adapter = "error: " + ex.Message; }
        try
        {
            string tray = _trayVerified switch { true => "yes", false => "no", null => "not yet" };
            Clipboard.SetText(_session.Diagnostics(adapter) + Environment.NewLine
                + $"Process: exe={Environment.ProcessPath}; startWithWindows={Startup.RegisteredValue() ?? "(off)"}; trayIconVerified={tray}");
            _tray.ShowBalloonTip(3000, "Glissando", "Diagnostics copied to the clipboard.", ToolTipIcon.Info);
        }
        catch (Exception ex) { Log.Warn("diagnostics: " + ex.Message); }
    }

    private void UpdateTray()
    {
        string text = _session.StatusText;
        _status.Text = text;
        _lockItem.Checked = _session.Locked;
        _mirrorItem.Enabled = _session.Authenticated;
        bool mismatch = _session.TransportStatus.StartsWith("other PC runs");
        if (mismatch && !_versionWarned)
        {
            _versionWarned = true;
            _tray.ShowBalloonTip(8000, "Glissando", $"The other PC runs another Glissando version. Install {Program.Version} there too.", ToolTipIcon.Warning);
        }
        if (_session.Authenticated) _versionWarned = false;
        // Bluetooth trouble on either PC (BleHealth): a red line at the top of the menu that says what to do,
        // a red icon, and a balloon each time the advice changes.
        string problem = _session.BluetoothProblem;
        _bluetoothItem.Visible = problem != "";
        _bluetoothItem.Text = problem;
        if (problem != _bluetoothShown)
        {
            _bluetoothShown = problem;
            // Bluetooth switched off here (airplane mode, by hand) is the user's own doing: menu row and icon
            // only, no balloon. The other cases are faults the user cannot see otherwise.
            if (problem != "" && !problem.Contains("switched off")) ShowBluetoothBalloon(problem);
        }
        // Windows truncates a long tray tooltip on hover, so the tip is a short summary; the full multi-line
        // status lives in the first menu row (_status.Text above), which has no such limit.
        // Build id first so it survives the 63-char trim; the live status fills the rest.
        string tip = $"Glissando {Program.Version}";
        if (!_session.Authenticated)
            tip += ": " + _session.TransportStatus;                          // scanning / advertising / BLE error ...
        else
        {
            string role = _session.Controlling ? "controlling" : _session.Controlled ? "controlled" : "linked";
            string peer = string.IsNullOrEmpty(_session.PeerName) ? "" : " " + _session.PeerName;
            string locked = _session.Locked ? ", locked" : "";
            tip += $": {role}{peer}{locked}";   // no round trip here: it changes all the time (see below)
        }
        // Every change of the tooltip or icon is a call into Explorer that waits for its answer, on this UI thread,
        // which also runs our low-level input hooks; with the round trip in the tip it changed with nearly every
        // state change. So: no round trip in the tip (it is in the menu's status line), and no call unless
        // something actually changed.
        string tipText = problem.Contains("switched off") ? Trim(tip, 44) + " - BLUETOOTH OFF"
            : problem.Contains("restart Windows") ? Trim(tip, 40) + " - BLUETOOTH STUCK"
            : problem != "" ? Trim(tip, 42) + " - Bluetooth not answering" : Trim(tip, 63);
        var icon = _session.TransportStatus.StartsWith("BLE error") || problem != "" ? _iconError
            : _session.Controlling ? _iconControlling
            : _session.Controlled ? _iconControlled
            : _session.Locked ? _iconLocked
            : _session.Authenticated ? _iconReady
            : _iconIdle;
        if (_tray.Text != tipText) _tray.Text = tipText;
        if (!ReferenceEquals(_tray.Icon, icon)) _tray.Icon = icon;
    }

    private void ShowBluetoothBalloon(string problem)
    {
        if (problem == "") return;
        string detail = problem.Contains("switched off")
            ? "Turn Bluetooth on in the quick settings or Settings > Bluetooth & devices (check that airplane mode is off). Glissando links up by itself once it is on."
            : problem.Contains("restart Windows")
            ? "Windows' Bluetooth has been stuck for minutes; turning it off and on does not cure this. Glissando reconnects by itself after the restart."
            : "Windows' Bluetooth stopped answering (it happens after sleep). Turn it off and on in Settings > Bluetooth & devices; Glissando reconnects by itself. If it keeps happening, restart Windows.";
        _tray.ShowBalloonTip(20000, "Glissando: " + problem, detail, ToolTipIcon.Warning);
    }

    private static string Trim(string s, int max) => s.Length <= max ? s : s[..(max - 3)] + "...";

    private void OpenSettings()
    {
        if (_openForm != null) { _openForm.Activate(); return; }
        var working = _settings.Clone();
        _openForm = new SettingsForm(working, firstRun: false, _session.PeerName);
        _openForm.SetPeerLinked(_session.Authenticated);
        _openForm.Saved += Apply;
        _openForm.IdentifyRequested += () => _session.SendIdentify();
        _openForm.FormClosed += (_, _) => { _openForm.Dispose(); _openForm = null; };
        _openForm.Show();
    }

    private void Apply(Settings working)
    {
        bool restart = working.Passphrase != _settings.Passphrase || working.Ble != _settings.Ble || working.UdpMirror != _settings.UdpMirror;
        _settings.Passphrase = working.Passphrase;
        _settings.Ble = working.Ble;
        _settings.PeerX = working.PeerX;
        _settings.PeerY = working.PeerY;
        _settings.PeerShifts = working.PeerShifts;
        _settings.MyShifts = working.MyShifts;
        _settings.PlacementUpdated = working.PlacementUpdated;
        _settings.CaptureEnabled = working.CaptureEnabled;
        _settings.SyncClipboard = working.SyncClipboard;
        _settings.HideCursorWhileControlling = working.HideCursorWhileControlling;
        _settings.KeyboardFollowsCursor = working.KeyboardFollowsCursor; // forgotten here at first, like the two below
        _settings.HideMirroredWindow = working.HideMirroredWindow;   // these two were never copied: the boxes looked dead
        _settings.PowerSaveWhenIdle = working.PowerSaveWhenIdle;
        _settings.UdpMirror = working.UdpMirror;
        _settings.MirrorFrameColor = working.MirrorFrameColor;
        _settings.Placements = working.Placements;
        try { _settings.Save(); } catch (Exception ex) { Log.Warn("save settings: " + ex.Message); }
        Log.Info($"settings saved: ble={_settings.Ble} peer origin=({_settings.PeerX},{_settings.PeerY}) capture={_settings.CaptureEnabled}");

        if (restart)
        {
            Log.Info("passphrase, Bluetooth role or network mirroring changed; restarting");
            _session.Dispose();
            _capture.Dispose();
            _tray.Visible = false;
            Application.Restart();
            return;
        }
        _session.ApplySettings();
    }

    private static void OpenPath(string path)
    {
        try
        {
            if (!File.Exists(path)) Directory.CreateDirectory(Path.GetDirectoryName(path)!);
            if (!File.Exists(path)) File.WriteAllText(path, "");
            Process.Start(new ProcessStartInfo(path) { UseShellExecute = true });
        }
        catch (Exception ex) { Log.Warn("open: " + ex.Message); }
    }

    private bool _exiting;

    private void Exit()
    {
        if (_exiting) return;
        _exiting = true;
        Log.Info("exiting");
        try
        {
            Microsoft.Win32.SystemEvents.PowerModeChanged -= _powerHandler;
            Microsoft.Win32.SystemEvents.SessionSwitch -= _sessionHandler;
            _fgTimer.Stop();
            _fgTimer.Dispose();
            _trayCheck.Stop();
            _trayCheck.Dispose();
            InstanceSignals.Dispose();
            CloseAllMirrorReceivers();
            var sw = System.Diagnostics.Stopwatch.StartNew();
            _session.Dispose();
            _capture.Dispose();
            // One exit took 44 s with nothing logged (2026-09-15); say how long the teardown took.
            Log.Info($"session closed in {sw.ElapsedMilliseconds} ms");
            Host.CursorHider.RestoreAll();
            _tray.Visible = false;
            _tray.Dispose();
        }
        catch (Exception ex) { Log.Warn("exit cleanup: " + ex.Message); }
        ExitThread();
        // Guarantee the process actually terminates and releases the exe, even if some background or
        // WinRT thread would otherwise keep it alive (which once left it running with no tray icon).
        Log.Info("exit: process ending");
        Environment.Exit(0);
    }

    // ---- IClipboardAccess (marshalled to the STA thread) ----

    public string? GetText()
    {
        string? result = null;
        _window.Invoke(() => { if (Clipboard.ContainsText()) result = Clipboard.GetText(); });
        return result;
    }

    public void SetText(string text) => _window.Invoke(() => Clipboard.SetText(text));

    // The picture last put on the clipboard by the other PC, with the clipboard's sequence number at that
    // moment: while nothing has replaced it, GetImage hands back these very bytes, so the hash the session
    // compares matches and the picture is not sent straight back on the return crossing (a re-encoded PNG of
    // the same bitmap would not be byte-identical).
    private uint _setImageSeq;
    private byte[]? _setImageBytes;
    [System.Runtime.InteropServices.DllImport("user32.dll")] private static extern uint GetClipboardSequenceNumber();

    public byte[]? GetImage(int maxBytes)
    {
        byte[]? result = null;
        _window.Invoke(() =>
        {
            if (!Clipboard.ContainsImage() || Clipboard.ContainsText()) return;
            if (_setImageBytes != null && GetClipboardSequenceNumber() == _setImageSeq) { result = _setImageBytes; return; }
            using var img = Clipboard.GetImage();
            if (img == null) return;
            using var ms = new MemoryStream();
            img.Save(ms, System.Drawing.Imaging.ImageFormat.Png);
            if (ms.Length <= maxBytes) { result = ms.ToArray(); return; }
            long pngSize = ms.Length;
            ms.SetLength(0);
            var jpeg = System.Drawing.Imaging.ImageCodecInfo.GetImageEncoders().First(c => c.FormatID == System.Drawing.Imaging.ImageFormat.Jpeg.Guid);
            using var p = new System.Drawing.Imaging.EncoderParameters(1);
            p.Param[0] = new System.Drawing.Imaging.EncoderParameter(System.Drawing.Imaging.Encoder.Quality, 85L);
            img.Save(ms, jpeg, p);
            if (ms.Length <= maxBytes) result = ms.ToArray();
            else Log.Warn($"clipboard picture not sent: {pngSize / 1024} kB as PNG, {ms.Length / 1024} kB as JPEG, more than the {maxBytes / 1024} kB that Bluetooth carries in reasonable time");
        });
        return result;
    }

    public void SetImage(byte[] encoded) => _window.Invoke(() =>
    {
        using var ms = new MemoryStream(encoded);
        using var img = Image.FromStream(ms);
        Clipboard.SetImage(img);
        _setImageSeq = GetClipboardSequenceNumber();
        _setImageBytes = encoded;
    });
}

internal static class TrayIcons
{
    /// <summary>Rounded dark square with a slur curve; the curve's colour shows the state.</summary>
    public static Icon Make(Color accent)
    {
        const int size = 32;
        using var bmp = new Bitmap(size, size);
        using (var g = Graphics.FromImage(bmp))
        {
            g.SmoothingMode = SmoothingMode.AntiAlias;
            g.Clear(Color.Transparent);
            using var bg = new SolidBrush(Color.FromArgb(30, 32, 46));
            using var path = RoundedRect(new Rectangle(1, 1, size - 2, size - 2), 7);
            g.FillPath(bg, path);
            using var pen = new Pen(accent, 3.5f) { StartCap = LineCap.Round, EndCap = LineCap.Round };
            g.DrawBezier(pen, new PointF(6, 23), new PointF(11, 6), new PointF(21, 26), new PointF(26, 9));
        }
        IntPtr h = bmp.GetHicon();
        try
        {
            using var tmp = Icon.FromHandle(h);
            return (Icon)tmp.Clone();
        }
        finally { Native.DestroyIcon(h); }
    }

    private static GraphicsPath RoundedRect(Rectangle r, int radius)
    {
        int d = radius * 2;
        var p = new GraphicsPath();
        p.AddArc(r.X, r.Y, d, d, 180, 90);
        p.AddArc(r.Right - d, r.Y, d, d, 270, 90);
        p.AddArc(r.Right - d, r.Bottom - d, d, d, 0, 90);
        p.AddArc(r.X, r.Bottom - d, d, d, 90, 90);
        p.CloseFigure();
        return p;
    }
}
