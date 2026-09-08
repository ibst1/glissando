using System.Diagnostics;
using System.Drawing.Drawing2D;
using Windows.Devices.Bluetooth;

namespace Glissando;

/// <summary>
/// Drag the other PC's screens to where they physically stand: beside, above, below, or tucked into an
/// empty corner of this PC's desktop. They snap flush against a monitor edge; green marks every stretch
/// of edge the cursor can cross. Coordinates are this PC's pixels; the drawing is scaled to fit.
/// </summary>
internal sealed class PlacementCanvas : Control
{
    public event Action? PlacementChanged;

    public MonitorRect[] Mine { get; set; } = Glissando.Layout.MyMonitors();
    public MonitorRect[] Peer { get; set; } = [];
    public string OtherName { get; set; } = "Other PC";

    private Point _origin;
    public Point PeerOrigin
    {
        get => _origin;
        set { _origin = value; Invalidate(); }
    }

    private bool _dragging;
    private Point _grab; // grab point relative to the peer origin, in world pixels
    private (float scale, PointF origin)? _dragView;

    public PlacementCanvas()
    {
        DoubleBuffered = true;
        SetStyle(ControlStyles.ResizeRedraw | ControlStyles.UserPaint | ControlStyles.AllPaintingInWmPaint | ControlStyles.OptimizedDoubleBuffer, true);
        Cursor = Cursors.SizeAll;
        BackColor = Color.FromArgb(245, 245, 247);
    }

    private Glissando.Layout Current => new(Mine, Peer, _origin);

    // World: my virtual screen at (0,0). The view fits both PCs with some air; it is frozen while
    // dragging so a snap to another side cannot make it jump under the mouse.
    private (float scale, PointF origin) View()
    {
        if (_dragView is { } frozen) return frozen;
        var layout = Current;
        var union = Rectangle.Union(layout.MyBounds, layout.PeerBounds);
        if (union.IsEmpty) union = new Rectangle(0, 0, 1920, 1080);
        const float legend = 20, hint = 18;
        float margin = 0.18f * Math.Max(union.Width, union.Height);
        float worldW = union.Width + 2 * margin, worldH = union.Height + 2 * margin;
        float scale = Math.Min((Width - 16) / worldW, (Height - legend - hint - 8) / worldH);
        var origin = new PointF(
            Width / 2f - (union.X + union.Width / 2f) * scale,
            legend + (Height - legend - hint) / 2f - (union.Y + union.Height / 2f) * scale);
        return (scale, origin);
    }

    private static RectangleF ToCanvas(Rectangle world, float scale, PointF origin)
        => new(origin.X + world.X * scale, origin.Y + world.Y * scale, world.Width * scale, world.Height * scale);

    private static Point ToWorld(Point canvas, float scale, PointF origin)
        => new((int)Math.Round((canvas.X - origin.X) / scale), (int)Math.Round((canvas.Y - origin.Y) / scale));

    protected override void OnPaint(PaintEventArgs e)
    {
        base.OnPaint(e);
        var g = e.Graphics;
        g.SmoothingMode = SmoothingMode.AntiAlias;
        g.TextRenderingHint = System.Drawing.Text.TextRenderingHint.ClearTypeGridFit;
        var (scale, origin) = View();
        var layout = Current;

        using var mineBrush = new SolidBrush(Color.FromArgb(30, 32, 46));
        using var peerBrush = new SolidBrush(Color.FromArgb(66, 133, 244));
        using var white = new SolidBrush(Color.White);
        using var font = new Font(Font.FontFamily, 9f);
        using var small = new Font(Font.FontFamily, 7.5f);
        using var numberFont = new Font(Font.FontFamily, 12f, FontStyle.Bold);
        using var outline = new Pen(Color.FromArgb(120, 120, 130)) { DashStyle = DashStyle.Dash };
        using var tileBorder = new Pen(Color.FromArgb(245, 245, 247), 1.5f);
        var fmt = new StringFormat { Alignment = StringAlignment.Center, LineAlignment = StringAlignment.Center };

        // Dashed outline of each PC's virtual desktop, then the monitors as tiles.
        var mineBox = ToCanvas(layout.MyBounds, scale, origin);
        var peerBox = ToCanvas(layout.PeerBounds, scale, origin);
        g.DrawRectangle(outline, mineBox.X, mineBox.Y, mineBox.Width, mineBox.Height);
        g.DrawRectangle(outline, peerBox.X, peerBox.Y, peerBox.Width, peerBox.Height);

        DrawTiles(g, Mine, Point.Empty, mineBrush, tileBorder, white, numberFont, font, scale, origin, fmt,
            single: $"This PC\n{layout.MyBounds.Width} x {layout.MyBounds.Height}");
        DrawTiles(g, Peer, _origin, peerBrush, tileBorder, white, numberFont, font, scale, origin, fmt,
            single: Peer.Length == 1 && Peer[0].Number == 0 ? $"{OtherName}\n(size unknown)" : $"{OtherName}\n{layout.PeerBounds.Width} x {layout.PeerBounds.Height}");

        // Every stretch of edge the cursor can cross.
        using var reachable = new Pen(Color.FromArgb(76, 175, 80), 3f) { StartCap = LineCap.Round, EndCap = LineCap.Round };
        foreach (var s in layout.CrossingSegments())
        {
            float f = (s.Side is Dir.Left or Dir.Right ? origin.X : origin.Y) + s.Fixed * scale;
            float a = (s.Side is Dir.Left or Dir.Right ? origin.Y : origin.X) + s.From * scale;
            float b = (s.Side is Dir.Left or Dir.Right ? origin.Y : origin.X) + s.To * scale;
            if (s.Side is Dir.Left or Dir.Right) g.DrawLine(reachable, f, a, f, b);
            else g.DrawLine(reachable, a, f, b, f);
        }

        // Legend in the top corners and the hint at the bottom.
        using var legendMine = new SolidBrush(Color.FromArgb(60, 62, 76));
        using var legendPeer = new SolidBrush(Color.FromArgb(40, 90, 190));
        using var gray = new SolidBrush(Color.FromArgb(110, 110, 120));
        var left = new StringFormat(StringFormatFlags.NoWrap) { Alignment = StringAlignment.Near, LineAlignment = StringAlignment.Center };
        var right = new StringFormat(StringFormatFlags.NoWrap) { Alignment = StringAlignment.Far, LineAlignment = StringAlignment.Center };
        g.DrawString(Mine.Length == 1 ? $"This PC: {layout.MyBounds.Width} x {layout.MyBounds.Height}" : $"This PC: {Mine.Length} screens",
            small, legendMine, new RectangleF(6, 2, Width / 2f, 16), left);
        string peerText = Peer.Length == 1 && Peer[0].Number == 0 ? $"{OtherName}: size unknown until linked"
            : Peer.Length == 1 ? $"{OtherName}: {layout.PeerBounds.Width} x {layout.PeerBounds.Height}"
            : $"{OtherName}: {Peer.Length} screens";
        g.DrawString(peerText, small, legendPeer, new RectangleF(Width / 2f - 60, 2, Width / 2f + 54, 16), right);
        g.DrawString("Drag the blue screens to where they stand. Green = where the cursor crosses.",
            small, gray, new RectangleF(0, Height - 18, Width, 16), fmt);
    }

    private static void DrawTiles(Graphics g, MonitorRect[] tiles, Point offset, Brush fill, Pen border, Brush text,
        Font numberFont, Font font, float scale, PointF origin, StringFormat fmt, string single)
    {
        foreach (var (bounds, number) in tiles)
        {
            var world = bounds;
            world.Offset(offset);
            var tile = ToCanvas(world, scale, origin);
            g.FillRectangle(fill, tile);
            g.DrawRectangle(border, tile.X, tile.Y, tile.Width, tile.Height);
            if (tiles.Length == 1)
            {
                if (tile.Width >= 90 && tile.Height >= 34) g.DrawString(single, font, text, tile, fmt);
            }
            else if (number > 0 && tile.Width > 18 && tile.Height > 18)
            {
                g.DrawString(number.ToString(), numberFont, text, tile, fmt); // same numbers as Windows Display settings
            }
        }
    }

    protected override void OnMouseDown(MouseEventArgs e)
    {
        base.OnMouseDown(e);
        if (e.Button != MouseButtons.Left || Peer.Length == 0) return;
        var (scale, origin) = View();
        var w = ToWorld(e.Location, scale, origin);
        var peerBox = Current.PeerBounds;
        _dragging = true;
        _dragView = (scale, origin);
        _grab = peerBox.Contains(w)
            ? new Point(w.X - _origin.X, w.Y - _origin.Y)
            : new Point(peerBox.X - _origin.X + peerBox.Width / 2, peerBox.Y - _origin.Y + peerBox.Height / 2);
        Capture = true;
        Drag(e.Location);
    }

    protected override void OnMouseMove(MouseEventArgs e)
    {
        base.OnMouseMove(e);
        if (_dragging) Drag(e.Location);
    }

    protected override void OnMouseUp(MouseEventArgs e)
    {
        base.OnMouseUp(e);
        _dragging = false;
        _dragView = null;
        Capture = false;
        Invalidate();
    }

    private void Drag(Point canvasPoint)
    {
        var (scale, origin) = View();
        var w = ToWorld(canvasPoint, scale, origin);
        var proposed = new Point(w.X - _grab.X, w.Y - _grab.Y);
        // Align edges when they are within about eight canvas pixels of each other.
        var snapped = Glissando.Layout.Snap(Mine, Peer, proposed, 64, (int)Math.Max(16, 8 / scale));
        if (snapped != _origin)
        {
            _origin = snapped;
            Invalidate();
            PlacementChanged?.Invoke();
        }
    }
}

internal sealed class SettingsForm : Form
{
    /// <summary>Raised on OK with the edited settings (already validated).</summary>
    public event Action<Settings>? Saved;

    private readonly Settings _s;
    private readonly TextBox _pass = new() { Width = 300 };
    private readonly PlacementCanvas _canvas = new() { Dock = DockStyle.Fill };
    private readonly NumericUpDown _peerX = new() { Minimum = -30000, Maximum = 30000, Width = 80 };
    private readonly NumericUpDown _peerY = new() { Minimum = -30000, Maximum = 30000, Width = 80 };
    private readonly CheckBox _capture = new() { Text = "This PC's mouse and keyboard may control the other PC", AutoSize = true };
    private readonly CheckBox _clip = new() { Text = "Copy clipboard text across when the cursor crosses", AutoSize = true };
    private readonly CheckBox _autostart = new() { Text = "Start Glissando with Windows", AutoSize = true };
    private readonly CheckBox _hideCursor = new() { Text = "Hide this PC's cursor while controlling the other PC (swaps the system cursors; restored on return)", AutoSize = true, MaximumSize = new Size(480, 0) };
    private readonly CheckBox _powerSave = new() { Text = "Save power after 5 minutes without input (the first move after a pause is then slower)", AutoSize = true, MaximumSize = new Size(480, 0) };
    private readonly ComboBox _ble = new() { DropDownStyle = ComboBoxStyle.DropDownList, Width = 200 };
    private readonly Label _btInfo = new() { AutoSize = true, MaximumSize = new Size(470, 0) };
    private readonly Point _originalOrigin;
    private bool _syncing;

    public SettingsForm(Settings working, bool firstRun, string peerName)
    {
        _s = working;
        var initial = Glissando.Layout.FromSettings(working);
        _originalOrigin = initial.PeerOrigin;

        Text = (firstRun ? "Glissando setup" : "Glissando settings") + $"  (version {Program.Version})";
        FormBorderStyle = FormBorderStyle.FixedDialog;
        MaximizeBox = false;
        MinimizeBox = false;
        StartPosition = FormStartPosition.CenterScreen;
        AutoScaleMode = AutoScaleMode.Dpi;
        ClientSize = new Size(524, 800);
        MinimumSize = new Size(420, 420);
        Font = new Font("Segoe UI", 9f);
        Log.Info($"settings form opening: firstRun={firstRun} peerOrigin={initial.PeerOrigin} mine={initial.Mine.Length} peer={initial.Peer.Length}");
        Log.Info("crossings: " + string.Join(" | ", initial.CrossingSegments().Select(s => $"{s.Side}@{s.Fixed} {s.From}-{s.To}")));

        // Fixed button bar at the bottom, scrolling content above it. Added last so it docks first.
        var buttonBar = new Panel { Dock = DockStyle.Bottom, Height = 46, Padding = new Padding(14, 8, 14, 6) };
        var content = new Panel { Dock = DockStyle.Fill, AutoScroll = true };
        Controls.Add(content);
        Controls.Add(buttonBar);

        var layout = new TableLayoutPanel { Dock = DockStyle.Top, AutoSize = true, AutoSizeMode = AutoSizeMode.GrowAndShrink, ColumnCount = 1, Padding = new Padding(14, 10, 14, 10) };
        layout.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 100));
        content.Controls.Add(layout);

        // 1. Pairing
        layout.Controls.Add(Header("1. Pair the PCs once"));
        _btInfo.Text = "Reading Bluetooth adapter...";
        layout.Controls.Add(_btInfo);
        var btRow = new FlowLayoutPanel { AutoSize = true, FlowDirection = FlowDirection.LeftToRight, Margin = new Padding(0, 2, 0, 8) };
        var btBtn = new Button { Text = "Open Bluetooth settings", AutoSize = true };
        btBtn.Click += (_, _) => TryStart("ms-settings:bluetooth");
        btRow.Controls.Add(btBtn);
        btRow.Controls.Add(new Label { Text = "Keep the Bluetooth page open on both PCs while pairing.", AutoSize = true, Padding = new Padding(6, 6, 0, 0) });
        layout.Controls.Add(btRow);

        // 2. Passphrase
        layout.Controls.Add(Header("2. Passphrase (type the same one on the other PC)"));
        var passRow = new FlowLayoutPanel { AutoSize = true, Margin = new Padding(0, 2, 0, 8) };
        _pass.Text = working.Passphrase;
        var gen = new Button { Text = "Generate", AutoSize = true };
        gen.Click += (_, _) => _pass.Text = GeneratePassphrase();
        passRow.Controls.Add(_pass);
        passRow.Controls.Add(gen);
        layout.Controls.Add(passRow);

        // 3. Placement
        layout.Controls.Add(Header("3. Where do the other PC's screens stand?"));
        _canvas.Mine = initial.Mine;
        _canvas.Peer = initial.Peer;
        _canvas.OtherName = string.IsNullOrEmpty(peerName) ? "Other PC" : peerName;
        _canvas.PeerOrigin = initial.PeerOrigin;
        _canvas.PlacementChanged += () => SyncFromCanvas();
        var canvasHost = new Panel { Height = 178, Dock = DockStyle.Top, Margin = new Padding(0, 2, 0, 4), BorderStyle = BorderStyle.FixedSingle };
        canvasHost.Controls.Add(_canvas);
        layout.Controls.Add(canvasHost);

        var placeRow = new FlowLayoutPanel { AutoSize = true, Margin = new Padding(0, 0, 0, 8) };
        placeRow.Controls.Add(new Label { Text = "Other PC's top-left corner at X", AutoSize = true, Padding = new Padding(0, 6, 4, 0) });
        _peerX.Value = Math.Clamp(initial.PeerOrigin.X, (int)_peerX.Minimum, (int)_peerX.Maximum);
        _peerX.ValueChanged += (_, _) => SyncFromFields();
        placeRow.Controls.Add(_peerX);
        placeRow.Controls.Add(new Label { Text = "Y", AutoSize = true, Padding = new Padding(6, 6, 4, 0) });
        _peerY.Value = Math.Clamp(initial.PeerOrigin.Y, (int)_peerY.Minimum, (int)_peerY.Maximum);
        _peerY.ValueChanged += (_, _) => SyncFromFields();
        placeRow.Controls.Add(_peerY);
        placeRow.Controls.Add(new Label { Text = "px, in this PC's pixels", AutoSize = true, Padding = new Padding(4, 6, 0, 0) });
        layout.Controls.Add(placeRow);

        // 4. Options
        layout.Controls.Add(Header("4. Options"));
        _capture.Checked = working.CaptureEnabled;
        _clip.Checked = working.SyncClipboard;
        _autostart.Checked = Settings.StartWithWindows;
        _hideCursor.Checked = working.HideCursorWhileControlling;
        _powerSave.Checked = working.PowerSaveWhenIdle;
        layout.Controls.Add(_capture);
        layout.Controls.Add(_clip);
        layout.Controls.Add(_hideCursor);
        layout.Controls.Add(_powerSave);
        layout.Controls.Add(_autostart);

        var bleRow = new FlowLayoutPanel { AutoSize = true, Margin = new Padding(0, 6, 0, 8) };
        bleRow.Controls.Add(new Label { Text = "Bluetooth role", AutoSize = true, Padding = new Padding(0, 6, 4, 0) });
        _ble.Items.AddRange(["Automatic (recommended)", "This PC advertises", "This PC connects"]);
        _ble.SelectedIndex = (int)working.Ble;
        bleRow.Controls.Add(_ble);
        layout.Controls.Add(bleRow);

        // Buttons live in the fixed bottom bar so they are always visible, even when the content scrolls.
        var buttons = new FlowLayoutPanel { FlowDirection = FlowDirection.RightToLeft, Dock = DockStyle.Fill, AutoSize = false, WrapContents = false };
        var ok = new Button { Text = firstRun ? "Save and start" : "Save", AutoSize = true, DialogResult = DialogResult.OK, Margin = new Padding(6, 0, 0, 0) };
        var cancel = new Button { Text = "Cancel", AutoSize = true, DialogResult = DialogResult.Cancel };
        ok.Click += (_, _) => { if (!Commit()) DialogResult = DialogResult.None; };
        buttons.Controls.Add(ok);
        buttons.Controls.Add(cancel);
        buttonBar.Controls.Add(buttons);
        AcceptButton = ok;
        CancelButton = cancel;

        Shown += async (_, _) => await LoadBluetoothInfoAsync();
    }

    /// <summary>The peer sent its monitors or a newer placement: reflect it while the form is open.</summary>
    public void Reload(Settings current, string peerName)
    {
        var now = Glissando.Layout.FromSettings(current);
        _canvas.Peer = now.Peer;
        if (!string.IsNullOrEmpty(peerName)) _canvas.OtherName = peerName;
        if (now.PeerOrigin != _originalOrigin)
        {
            _canvas.PeerOrigin = now.PeerOrigin;
            SyncFromCanvas();
        }
        _canvas.Invalidate();
    }

    private static Label Header(string text) => new()
    {
        Text = text,
        AutoSize = true,
        Font = new Font("Segoe UI Semibold", 9.5f),
        Margin = new Padding(0, 6, 0, 2),
    };

    private void SyncFromCanvas()
    {
        if (_syncing) return;
        _syncing = true;
        _peerX.Value = Math.Clamp(_canvas.PeerOrigin.X, (int)_peerX.Minimum, (int)_peerX.Maximum);
        _peerY.Value = Math.Clamp(_canvas.PeerOrigin.Y, (int)_peerY.Minimum, (int)_peerY.Maximum);
        _syncing = false;
    }

    private void SyncFromFields()
    {
        if (_syncing) return;
        _syncing = true;
        _canvas.PeerOrigin = new Point((int)_peerX.Value, (int)_peerY.Value); // typed values are taken as they are
        _syncing = false;
    }

    private bool Commit()
    {
        var pass = _pass.Text.Trim();
        if (pass.Length < 4)
        {
            MessageBox.Show(this, "Enter a passphrase of at least 4 characters, or click Generate.", "Glissando", MessageBoxButtons.OK, MessageBoxIcon.Warning);
            _pass.Focus();
            return false;
        }
        _s.Passphrase = pass;
        _s.Ble = (BleMode)_ble.SelectedIndex;
        if (_canvas.PeerOrigin != _originalOrigin || _s.PeerX == null)
        {
            _s.PeerX = _canvas.PeerOrigin.X;
            _s.PeerY = _canvas.PeerOrigin.Y;
            if (_canvas.PeerOrigin != _originalOrigin) _s.PlacementUpdated = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds();
        }
        _s.CaptureEnabled = _capture.Checked;
        _s.SyncClipboard = _clip.Checked;
        _s.HideCursorWhileControlling = _hideCursor.Checked;
        _s.PowerSaveWhenIdle = _powerSave.Checked;
        Settings.StartWithWindows = _autostart.Checked;
        Saved?.Invoke(_s);
        return true;
    }

    private async Task LoadBluetoothInfoAsync()
    {
        try
        {
            var adapter = await BluetoothAdapter.GetDefaultAsync();
            if (adapter == null) { _btInfo.Text = "No Bluetooth adapter found on this PC."; return; }
            var addr = string.Join(":", Enumerable.Range(0, 6).Select(i => ((adapter.BluetoothAddress >> (8 * (5 - i))) & 0xFF).ToString("X2")));
            string can = adapter.IsPeripheralRoleSupported ? "" : " This adapter cannot advertise, so the other PC must.";
            _btInfo.Text = $"This PC is \"{Environment.MachineName}\" ({addr}). Pair it with the other PC in Windows Settings if you have not already.{can}";
        }
        catch (Exception ex)
        {
            _btInfo.Text = "Could not read the Bluetooth adapter: " + ex.Message;
        }
    }

    private static string GeneratePassphrase()
    {
        string[] words = ["cello", "viola", "oboe", "harp", "flute", "tuba", "lute", "horn", "drum", "fife", "bell", "reed", "organ", "gong", "lyre", "pipe"];
        var r = Random.Shared;
        return string.Join("-", Enumerable.Range(0, 4).Select(_ => words[r.Next(words.Length)])) + "-" + r.Next(100, 999);
    }

    private static void TryStart(string target)
    {
        try { Process.Start(new ProcessStartInfo(target) { UseShellExecute = true }); }
        catch (Exception ex) { Log.Warn("open: " + ex.Message); }
    }
}
