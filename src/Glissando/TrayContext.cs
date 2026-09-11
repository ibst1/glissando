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
    private readonly NotifyIcon _tray;
    private readonly ToolStripMenuItem _status;
    private readonly Session _session;
    private readonly InputCapture _capture;
    private readonly InputInjector _injector;
    private readonly Icon _iconIdle, _iconReady, _iconControlling, _iconControlled, _iconLocked, _iconError;
    private readonly ToolStripMenuItem _lockItem;
    private readonly ToolStripMenuItem _mirrorItem;
    private readonly Mirror.MirrorReceiver _mirrorRx = new();
    private readonly System.Windows.Forms.Timer _fgTimer = new() { Interval = 400 };
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
        _mirrorItem.Click += (_, _) => ToggleMirror();
        var menu = new ContextMenuStrip();
        menu.Items.Add(new ToolStripMenuItem($"Glissando {Program.VersionLong}") { Enabled = false });
        menu.Items.Add(_status);
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

        _tray = new NotifyIcon { Icon = _iconIdle, Visible = true, Text = "Glissando", ContextMenuStrip = menu };
        _tray.DoubleClick += (_, _) => OpenSettings();

        var layout = Layout.FromSettings(settings);
        _capture = new InputCapture(layout, _window.Handle);
        _window.RawInput += _capture.OnRawInput;
        _capture.Install();

        _injector = new InputInjector(layout);
        _window.DisplayChanged += () => _session?.ScreenChanged();

        IChannel channel = settings.Ble switch
        {
            BleMode.Advertise => new BlePeripheralChannel(),
            BleMode.Connect => new BleCentralChannel(),
            _ => new BleAutoChannel(),
        };

        _session = new Session(settings, channel, _capture, _injector, this);
        _session.StateChanged += () => _window.BeginInvoke(UpdateTray);
        _session.SettingsChanged += () => _window.BeginInvoke(() => _openForm?.Reload(_settings, _session.PeerName));
        // Incoming window-mirroring frames arrive on the receive thread; paint them on the UI thread.
        _session.MirrorReceived += f => _window.BeginInvoke(() => _mirrorRx.Handle(f));

        // After sleep the Bluetooth stack often needs a nudge.
        _powerHandler = (_, e) =>
        {
            if (e.Mode == Microsoft.Win32.PowerModes.Resume) _window.BeginInvoke(() => _session.Resume());
        };
        Microsoft.Win32.SystemEvents.PowerModeChanged += _powerHandler;

        // Lock screen: release whatever we hold and tell the peer.
        _sessionHandler = (_, e) =>
        {
            if (e.Reason == Microsoft.Win32.SessionSwitchReason.SessionLock) _window.BeginInvoke(() => _session.SetLocalLocked(true));
            else if (e.Reason == Microsoft.Win32.SessionSwitchReason.SessionUnlock) _window.BeginInvoke(() => _session.SetLocalLocked(false));
        };
        Microsoft.Win32.SystemEvents.SessionSwitch += _sessionHandler;

        _session.Start();
        UpdateTray();
    }

    private bool _versionWarned;

    /// <summary>Remember the front application window (not ours, visible, titled, of a real size), so mirroring
    /// can target the window the user was using rather than whatever tiny window has focus at menu time.</summary>
    private void TrackForeground()
    {
        if (_session.Mirroring) return; // keep the chosen window while mirroring
        var h = Mirror.WindowCapture.Foreground();
        if (h == IntPtr.Zero || h == _window.Handle) return;
        if (Mirror.WindowCapture.ProcessId(h) == (uint)Environment.ProcessId) return;
        if (!Mirror.WindowCapture.Visible(h) || string.IsNullOrEmpty(Mirror.WindowCapture.Title(h))) return;
        if (Mirror.WindowCapture.SizeOf(h) is not Size s || s.Width < 200 || s.Height < 150) return;
        _lastRealWindow = h;
    }

    private void ToggleMirror()
    {
        if (_session.Mirroring)
        {
            _session.StopMirror();
            _mirrorRx.Close();
        }
        else
        {
            if (_lastRealWindow == IntPtr.Zero || Mirror.WindowCapture.SizeOf(_lastRealWindow) == null)
            {
                _tray.ShowBalloonTip(3000, "Glissando", "No window to mirror. Click the window you want, then use the tray menu.", ToolTipIcon.Info);
                return;
            }
            string title = Mirror.WindowCapture.Title(_lastRealWindow);
            _session.StartMirror(_lastRealWindow);
            _tray.ShowBalloonTip(3000, "Glissando", $"Mirroring \"{title}\" to the other PC.", ToolTipIcon.Info);
        }
        UpdateTray();
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
            Clipboard.SetText(_session.Diagnostics(adapter));
            _tray.ShowBalloonTip(3000, "Glissando", "Diagnostics copied to the clipboard.", ToolTipIcon.Info);
        }
        catch (Exception ex) { Log.Warn("diagnostics: " + ex.Message); }
    }

    private void UpdateTray()
    {
        string text = _session.StatusText;
        _status.Text = text;
        _lockItem.Checked = _session.Locked;
        _mirrorItem.Checked = _session.Mirroring;
        _mirrorItem.Enabled = _session.Authenticated || _session.Mirroring;
        bool mismatch = _session.TransportStatus.StartsWith("other PC runs");
        if (mismatch && !_versionWarned)
        {
            _versionWarned = true;
            _tray.ShowBalloonTip(8000, "Glissando", $"The other PC runs another Glissando version. Install {Program.Version} there too.", ToolTipIcon.Warning);
        }
        if (_session.Authenticated) _versionWarned = false;
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
            string rtt = _session.RttMs > 0 ? $", {_session.RttMs:F0} ms" : "";
            string locked = _session.Locked ? ", locked" : "";
            tip += $": {role}{peer}{rtt}{locked}";
        }
        _tray.Text = Trim(tip, 63);
        _tray.Icon = _session.TransportStatus.StartsWith("BLE error") ? _iconError
            : _session.Controlling ? _iconControlling
            : _session.Controlled ? _iconControlled
            : _session.Locked ? _iconLocked
            : _session.Authenticated ? _iconReady
            : _iconIdle;
    }

    private static string Trim(string s, int max) => s.Length <= max ? s : s[..(max - 3)] + "...";

    private void OpenSettings()
    {
        if (_openForm != null) { _openForm.Activate(); return; }
        var working = _settings.Clone();
        _openForm = new SettingsForm(working, firstRun: false, _session.PeerName);
        _openForm.Saved += Apply;
        _openForm.FormClosed += (_, _) => { _openForm.Dispose(); _openForm = null; };
        _openForm.Show();
    }

    private void Apply(Settings working)
    {
        bool restart = working.Passphrase != _settings.Passphrase || working.Ble != _settings.Ble;
        _settings.Passphrase = working.Passphrase;
        _settings.Ble = working.Ble;
        _settings.PeerX = working.PeerX;
        _settings.PeerY = working.PeerY;
        _settings.PlacementUpdated = working.PlacementUpdated;
        _settings.CaptureEnabled = working.CaptureEnabled;
        _settings.SyncClipboard = working.SyncClipboard;
        _settings.HideCursorWhileControlling = working.HideCursorWhileControlling;
        try { _settings.Save(); } catch (Exception ex) { Log.Warn("save settings: " + ex.Message); }
        Log.Info($"settings saved: ble={_settings.Ble} peer origin=({_settings.PeerX},{_settings.PeerY}) capture={_settings.CaptureEnabled}");

        if (restart)
        {
            Log.Info("passphrase or Bluetooth role changed; restarting");
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
            _mirrorRx.Close();
            _session.Dispose();
            _capture.Dispose();
            Host.CursorHider.Restore();
            _tray.Visible = false;
            _tray.Dispose();
        }
        catch (Exception ex) { Log.Warn("exit cleanup: " + ex.Message); }
        ExitThread();
        // Guarantee the process actually terminates and releases the exe, even if some background or
        // WinRT thread would otherwise keep it alive (which once left it running with no tray icon).
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
