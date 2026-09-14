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

    /// <summary>Thumbnail mode: no legend, no hint, no dragging - just the two desktops and the crossings.</summary>
    public bool Compact { get; set; }

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
        float legend = Compact ? 4 : 20, hint = Compact ? 4 : 18;
        float margin = (Compact ? 0.08f : 0.18f) * Math.Max(union.Width, union.Height);
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

        if (Compact) return;

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
        foreach (var (bounds, number, _) in tiles)
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
        if (Compact || e.Button != MouseButtons.Left || Peer.Length == 0) return;
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
    public event Action<Settings>? Saved;

    private readonly Settings _s;
    private readonly bool _firstRun;
    private readonly string _peerName;
    private readonly TextBox _pass = new() { Width = 300 };
    private readonly PlacementCanvas _canvas = new() { Dock = DockStyle.Fill };
    private readonly NumericUpDown _peerX = new() { Minimum = -30000, Maximum = 30000, Width = 80 };
    private readonly NumericUpDown _peerY = new() { Minimum = -30000, Maximum = 30000, Width = 80 };
    private readonly CheckBox _capture = new(), _clip = new(), _autostart = new(), _hideCursor = new(), _hideMirror = new(), _udpMirror = new(), _powerSave = new();
    private readonly ComboBox _ble = new() { DropDownStyle = ComboBoxStyle.DropDownList, Width = 200 };
    private readonly Label _btInfo = new() { AutoSize = true, MaximumSize = new Size(460, 0) };
    private readonly Label _setupNow = new() { AutoSize = true, MaximumSize = new Size(460, 0) };
    private readonly FlowLayoutPanel _gallery = new() { AutoSize = true, AutoSizeMode = AutoSizeMode.GrowAndShrink, WrapContents = true, MaximumSize = new Size(TextWidth + 40, 250), AutoScroll = true, Margin = new Padding(0, 2, 0, 0) };
    private string? _selectedSetup;
    private readonly FlowLayoutPanel _galleryOther = new() { AutoSize = true, AutoSizeMode = AutoSizeMode.GrowAndShrink, WrapContents = true, MaximumSize = new Size(TextWidth + 40, 0), Margin = new Padding(0, 2, 0, 0) };
    private readonly Label _matchHeader = Header("Matching desks - the same screens on both PCs");
    private readonly Label _otherHeader = Header("Other desks");
    private readonly Button _useDesk = new() { Text = "Use its placement", AutoSize = true, Margin = new Padding(18, 0, 0, 0) };
    private readonly TextBox _deskName = new() { Width = 150 };
    private readonly ToolTip _tips = new();
    private readonly Label _deskStatus = new() { AutoSize = true, MaximumSize = new Size(460, 0), Margin = new Padding(0, 4, 0, 0) };
    private readonly Point _originalOrigin;
    private bool _syncing;

    // Text width for wrapped option labels and notes: the tab page minus padding, at 100 % scaling (the
    // form's Dpi auto-scaling scales it along with everything else).
    private const int TextWidth = 440;

    public SettingsForm(Settings working, bool firstRun, string peerName)
    {
        _s = working;
        _firstRun = firstRun;
        _peerName = peerName;
        var initial = Glissando.Layout.FromSettings(working);
        _originalOrigin = initial.PeerOrigin;

        Text = (firstRun ? "Glissando setup" : "Glissando settings") + $"  ({Program.VersionLong})";
        // Resizable, never taller than the screen; each tab scrolls on its own if it must. The fixed 800 px
        // height of the first version ran off a laptop screen and hid the buttons.
        FormBorderStyle = FormBorderStyle.Sizable;
        MaximizeBox = true;
        MinimizeBox = false;
        StartPosition = FormStartPosition.CenterScreen;
        AutoScaleMode = AutoScaleMode.Dpi;
        int workingHeight = Screen.PrimaryScreen?.WorkingArea.Height ?? 800;
        ClientSize = new Size(540, Math.Clamp(workingHeight - 120, 420, 640));
        MinimumSize = new Size(460, 360);
        Font = new Font("Segoe UI", 9f);
        Log.Info($"settings form opening: firstRun={firstRun} peerOrigin={initial.PeerOrigin} mine={initial.Mine.Length} peer={initial.Peer.Length}");
        Log.Info("crossings: " + string.Join(" | ", initial.CrossingSegments().Select(s => $"{s.Side}@{s.Fixed} {s.From}-{s.To}")));

        // Fixed button bar at the bottom, tabs above it. Added last so it docks first.
        var buttonBar = new Panel { Dock = DockStyle.Bottom, Height = 46, Padding = new Padding(14, 8, 14, 6) };
        var tabs = new TabControl { Dock = DockStyle.Fill, Padding = new Point(14, 6) };
        Controls.Add(tabs);
        Controls.Add(buttonBar);

        tabs.TabPages.Add(BuildSetupPage(working));
        tabs.TabPages.Add(BuildScreensPage(working, initial));
        tabs.TabPages.Add(BuildDesksPage());
        tabs.TabPages.Add(BuildOptionsPage(working));
        // First run: the pairing tab; afterwards the screens, which is what one comes back to adjust.
        tabs.SelectedIndex = firstRun ? 0 : 1;

        // Save commits and closes; Close just closes. Both work whether the form is modal (first run, from
        // Program) or modeless (from the tray) -- a button's DialogResult alone only closes a modal form,
        // which is why the old Cancel button did nothing from the tray.
        var buttons = new FlowLayoutPanel { FlowDirection = FlowDirection.RightToLeft, Dock = DockStyle.Fill, WrapContents = false };
        var save = new Button { Text = firstRun ? "Save and start" : "Save", AutoSize = true, Margin = new Padding(6, 0, 0, 0) };
        var close = new Button { Text = firstRun ? "Cancel" : "Close", AutoSize = true };
        save.Click += (_, _) => { if (!Commit()) return; if (Modal) DialogResult = DialogResult.OK; else Close(); };
        close.Click += (_, _) => { if (Modal) DialogResult = DialogResult.Cancel; else Close(); };
        buttons.Controls.Add(save);
        buttons.Controls.Add(close);
        buttonBar.Controls.Add(buttons);
        AcceptButton = save;
        CancelButton = close;

        Shown += (_, _) => FitToScreen();
        Shown += async (_, _) => await LoadBluetoothInfoAsync();
    }

    // ---- pages ---------------------------------------------------------------------

    private static TabPage Page(string title, out TableLayoutPanel layout, bool fill = false)
    {
        var page = new TabPage(title) { UseVisualStyleBackColor = true, Padding = new Padding(0) };
        var scroller = new Panel { Dock = DockStyle.Fill, AutoScroll = !fill };
        layout = new TableLayoutPanel
        {
            Dock = fill ? DockStyle.Fill : DockStyle.Top,
            AutoSize = !fill,
            AutoSizeMode = AutoSizeMode.GrowAndShrink,
            ColumnCount = 1,
            Padding = new Padding(14, 10, 14, 10),
        };
        layout.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 100));
        scroller.Controls.Add(layout);
        page.Controls.Add(scroller);
        return page;
    }

    private TabPage BuildSetupPage(Settings working)
    {
        var page = Page("Setup", out var layout);

        layout.Controls.Add(Header("1. Pair the PCs once"));
        _btInfo.Text = "Reading Bluetooth adapter...";
        layout.Controls.Add(_btInfo);
        var btRow = new FlowLayoutPanel { AutoSize = true, FlowDirection = FlowDirection.LeftToRight, Margin = new Padding(0, 2, 0, 8) };
        var btBtn = new Button { Text = "Open Bluetooth settings", AutoSize = true };
        btBtn.Click += (_, _) => TryStart("ms-settings:bluetooth");
        btRow.Controls.Add(btBtn);
        btRow.Controls.Add(new Label { Text = "Keep the Bluetooth page open on both PCs while pairing.", AutoSize = true, Padding = new Padding(6, 6, 0, 0) });
        layout.Controls.Add(btRow);

        layout.Controls.Add(Header("2. Passphrase (type the same one on the other PC)"));
        var passRow = new FlowLayoutPanel { AutoSize = true, Margin = new Padding(0, 2, 0, 8) };
        _pass.Text = working.Passphrase;
        var gen = new Button { Text = "Generate", AutoSize = true };
        gen.Click += (_, _) => _pass.Text = GeneratePassphrase();
        passRow.Controls.Add(_pass);
        passRow.Controls.Add(gen);
        layout.Controls.Add(passRow);

        layout.Controls.Add(Header("3. Bluetooth role"));
        var bleRow = new FlowLayoutPanel { AutoSize = true, Margin = new Padding(0, 2, 0, 8) };
        _ble.Items.AddRange(["Automatic (recommended)", "This PC advertises", "This PC connects"]);
        _ble.SelectedIndex = (int)working.Ble;
        bleRow.Controls.Add(_ble);
        layout.Controls.Add(bleRow);
        layout.Controls.Add(Note("Automatic lets the adapters decide: whichever can advertise does, and the PC with the lower address connects. Changing the role or the passphrase restarts Glissando."));

        layout.Controls.Add(Header("4. Start"));
        _autostart.Checked = Settings.StartWithWindows;
        layout.Controls.Add(Option(_autostart, "Start Glissando with Windows"));
        return page;
    }

    private TabPage BuildScreensPage(Settings working, Glissando.Layout initial)
    {
        var page = Page("Screens", out var layout, fill: true);
        layout.RowStyles.Add(new RowStyle(SizeType.AutoSize));       // header
        layout.RowStyles.Add(new RowStyle(SizeType.Percent, 100));   // canvas
        layout.RowStyles.Add(new RowStyle(SizeType.AutoSize));       // fields
        layout.RowStyles.Add(new RowStyle(SizeType.AutoSize));       // which desk this is

        layout.Controls.Add(Header("Where do the other PC's screens stand?"));
        _canvas.Mine = initial.Mine;
        _canvas.Peer = initial.Peer;
        _canvas.OtherName = string.IsNullOrEmpty(_peerName) ? "Other PC" : _peerName;
        _canvas.PeerOrigin = initial.PeerOrigin;
        _canvas.PlacementChanged += () => SyncFromCanvas();
        var canvasHost = new Panel { Dock = DockStyle.Fill, MinimumSize = new Size(0, 150), Margin = new Padding(0, 2, 0, 4), BorderStyle = BorderStyle.FixedSingle };
        canvasHost.Controls.Add(_canvas);
        layout.Controls.Add(canvasHost);

        var placeRow = new FlowLayoutPanel { AutoSize = true, Margin = new Padding(0, 0, 0, 6) };
        placeRow.Controls.Add(new Label { Text = "Other PC's top-left corner, in this PC's pixels:  X", AutoSize = true, Padding = new Padding(0, 6, 4, 0) });
        _peerX.Value = Math.Clamp(initial.PeerOrigin.X, (int)_peerX.Minimum, (int)_peerX.Maximum);
        _peerX.ValueChanged += (_, _) => SyncFromFields();
        placeRow.Controls.Add(_peerX);
        placeRow.Controls.Add(new Label { Text = "Y", AutoSize = true, Padding = new Padding(6, 6, 4, 0) });
        _peerY.Value = Math.Clamp(initial.PeerOrigin.Y, (int)_peerY.Minimum, (int)_peerY.Maximum);
        _peerY.ValueChanged += (_, _) => SyncFromFields();
        placeRow.Controls.Add(_peerY);
        placeRow.Controls.Add(new Label { Text = "px", AutoSize = true, Padding = new Padding(4, 6, 0, 0) });
        layout.Controls.Add(placeRow);

        layout.Controls.Add(_deskStatus);
        RefreshSetups();
        return page;
    }

    /// <summary>Every desk the placement has been saved for, as a thumbnail of its layout; the one you sit at
    /// is marked. A desk is told apart by its screens (this PC's and the other PC's); the name is yours.</summary>
    private TabPage BuildDesksPage()
    {
        var page = Page("Desks", out var layout);
        layout.Controls.Add(Header("Remembered desks"));
        layout.Controls.Add(Note("Each desk is remembered by its screens - which panels are connected to this PC and to the other one, told apart by their serial numbers - and the placement saved on the Screens tab belongs to the desk you are at. Green: this desk. Click one to select it."));
        layout.Controls.Add(_setupNow);
        _gallery.MaximumSize = new Size(TextWidth + 40, 0);
        _gallery.AutoScroll = false;
        layout.Controls.Add(_matchHeader);
        layout.Controls.Add(_gallery);
        layout.Controls.Add(_otherHeader);
        layout.Controls.Add(_galleryOther);

        var nameRow = new FlowLayoutPanel { AutoSize = true, Margin = new Padding(0, 6, 0, 0) };
        nameRow.Controls.Add(new Label { Text = "Name", AutoSize = true, Padding = new Padding(0, 6, 4, 0) });
        var setName = new Button { Text = "Set", AutoSize = true };
        void ApplyName()
        {
            if (_selectedSetup == null || !_s.Placements.TryGetValue(_selectedSetup, out var p)) return;
            string name = _deskName.Text.Trim();
            if (name != p.Name) { p.Name = name; p.NameUpdated = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds(); }
            RefreshSetups();
        }
        setName.Click += (_, _) => ApplyName();
        _deskName.KeyDown += (_, e) => { if (e.KeyCode == Keys.Enter) { ApplyName(); e.SuppressKeyPress = true; } };
        nameRow.Controls.Add(_deskName);
        nameRow.Controls.Add(setName);
        // A matching desk's placement can be taken over: it lands on the Screens canvas, Save keeps it for this desk.
        _useDesk.Click += (_, _) =>
        {
            if (_selectedSetup == null || !_s.Placements.TryGetValue(_selectedSetup, out var p)) return;
            _canvas.PeerOrigin = new Point(p.PeerX, p.PeerY);
            SyncFromCanvas();
            _setupNow.Text = $"Placement of {(p.Name != "" ? $"\"{p.Name}\"" : "the selected desk")} applied on the Screens tab - Save to keep it for this desk.";
        };
        nameRow.Controls.Add(_useDesk);
        var forget = new Button { Text = "Forget selected", AutoSize = true, Margin = new Padding(18, 0, 0, 0) };
        forget.Click += (_, _) =>
        {
            if (_selectedSetup != null) { _s.Placements.Remove(_selectedSetup); _selectedSetup = null; RefreshSetups(); }
        };
        nameRow.Controls.Add(forget);
        layout.Controls.Add(nameRow);
        layout.Controls.Add(Note("Names and placements are shared with the other PC when you save; whoever named a desk last wins."));
        return page;
    }

    private TabPage BuildOptionsPage(Settings working)
    {
        var page = Page("Options", out var layout);
        layout.Controls.Add(Header("Control"));
        _capture.Checked = working.CaptureEnabled;
        _clip.Checked = working.SyncClipboard;
        _hideCursor.Checked = working.HideCursorWhileControlling;
        layout.Controls.Add(Option(_capture, "This PC's mouse and keyboard may control the other PC"));
        layout.Controls.Add(Option(_clip, "Copy clipboard text across when the cursor crosses"));
        layout.Controls.Add(Option(_hideCursor, "Hide this PC's cursor while controlling the other PC (swaps the system cursors; restored on return, and on next start if Glissando is ever killed while they are hidden)"));

        layout.Controls.Add(Header("Window mirroring"));
        _hideMirror.Checked = working.HideMirroredWindow;
        _udpMirror.Checked = working.UdpMirror;
        layout.Controls.Add(Option(_hideMirror, "Hide a window on this PC while it is mirrored to the other PC. It becomes invisible here, as if it had moved over, and shows again when the mirror stops, when you click where it was, or when you type into it."));
        layout.Controls.Add(Option(_udpMirror, "Send the mirrored picture over the network when both PCs are on one (Wi-Fi/LAN): 25-30 frames a second instead of Bluetooth's few. Addresses are exchanged automatically; nothing to configure, no firewall rule. Falls back to Bluetooth by itself."));

        layout.Controls.Add(Header("Power"));
        _powerSave.Checked = working.PowerSaveWhenIdle;
        layout.Controls.Add(Option(_powerSave, "Save power after 5 minutes without input (the first move after a pause is then slower)"));
        return page;
    }

    // ---- pieces --------------------------------------------------------------------

    private static Label Header(string text) => new()
    {
        Text = text,
        AutoSize = true,
        Font = new Font("Segoe UI Semibold", 9.5f),
        Margin = new Padding(0, 8, 0, 2),
    };

    private static Label Note(string text) => new()
    {
        Text = text,
        AutoSize = true,
        MaximumSize = new Size(TextWidth, 0),
        ForeColor = SystemColors.GrayText,
        Margin = new Padding(0, 0, 0, 6),
    };

    /// <summary>A check box with a wrapping label beside it. A CheckBox's own text does not wrap reliably (the
    /// long options were cut off mid-sentence), a Label does; clicking the label toggles the box.</summary>
    private static Control Option(CheckBox box, string text)
    {
        var row = new TableLayoutPanel { AutoSize = true, AutoSizeMode = AutoSizeMode.GrowAndShrink, ColumnCount = 2, Margin = new Padding(0, 1, 0, 3) };
        row.ColumnStyles.Add(new ColumnStyle(SizeType.AutoSize));
        row.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 100));
        box.AutoSize = true;
        box.Text = "";
        box.Margin = new Padding(0, 2, 4, 0);
        var label = new Label { Text = text, AutoSize = true, MaximumSize = new Size(TextWidth - 24, 0), Margin = new Padding(0, 1, 0, 0), Cursor = Cursors.Hand };
        label.Click += (_, _) => box.Checked = !box.Checked;
        row.Controls.Add(box, 0, 0);
        row.Controls.Add(label, 1, 0);
        return row;
    }

    private string CurrentSetupKey() => Glissando.Layout.SetupKey(_canvas.Mine, _peerName, _s.OtherMonitorList);

    /// <summary>One thumbnail per remembered setup: the two desktops drawn as on the canvas above, the current
    /// one marked in green, the selected one in blue. Text alone made two desks with the same screen sizes
    /// indistinguishable; the picture shows where the other PC stands.</summary>
    private void RefreshSetups()
    {
        string current = CurrentSetupKey();
        bool complete = Glissando.Layout.IsCompleteSetupKey(current);
        string currentName = _s.Placements.TryGetValue(current, out var cp) && cp.Name != "" ? $"\"{cp.Name}\"" : "";
        _setupNow.Text = !complete
            ? "This desk is not known yet - it is remembered once the other PC has said hello."
            : _s.Placements.ContainsKey(current) ? $"You are at {(currentName != "" ? currentName : "a remembered desk")} (green)."
            : "This desk is new - Save on the Screens tab to remember it.";
        _deskStatus.Text = !complete ? "This desk is remembered once the other PC has said hello."
            : _s.Placements.ContainsKey(current) ? $"This desk: {(currentName != "" ? currentName : "unnamed")} - remembered; Save updates it. Name it on the Desks tab."
            : "This desk is new - Save remembers it. Name it on the Desks tab.";
        if (_selectedSetup != null && _s.Placements.TryGetValue(_selectedSetup, out var sp)) _deskName.Text = sp.Name; else _deskName.Text = "";
        _deskName.Enabled = _selectedSetup != null;
        _useDesk.Enabled = _selectedSetup != null && _selectedSetup != current && Glissando.Layout.SameScreens(_selectedSetup, current);
        // Controls made after the form was shown are not DPI-scaled by WinForms: scale them by hand, or at
        // 150 % the tiles come out two thirds of their size and the captions lose their second line (RS).
        float k = DeviceDpi / 96f;
        int S(int logical) => (int)Math.Round(logical * k);
        var tileFont = new Font(Font.FontFamily, 8f);
        int captionH = TextRenderer.MeasureText("Xg", tileFont).Height * 2 + S(6);
        _gallery.MaximumSize = new Size(S(TextWidth + 40), 0);
        _galleryOther.MaximumSize = new Size(S(TextWidth + 40), 0);
        _gallery.SuspendLayout(); _galleryOther.SuspendLayout();
        foreach (var g in new[] { _gallery, _galleryOther })
            foreach (Control c in g.Controls.Cast<Control>().ToArray()) { g.Controls.Remove(c); c.Dispose(); }
        // The exact desk first, then by recency; desks with the same screen counts on both PCs in the first
        // group (they are the candidates when the arrangement is new), everything else below.
        foreach (var kv in _s.Placements.OrderByDescending(kv => kv.Key == current).ThenByDescending(kv => kv.Value.Updated))
        {
            if (!Glissando.Layout.TryParseSetupKey(kv.Key, out var mine, out var name, out var peer)) continue;
            string key = kv.Key;
            bool isCurrent = key == current, isSelected = key == _selectedSetup;
            var tile = new Panel { Width = S(150), Height = S(90) + captionH, Margin = new Padding(0, 0, S(8), S(8)), Padding = new Padding(S(2)), Cursor = Cursors.Hand, Tag = key,
                BackColor = isSelected ? Color.FromArgb(66, 133, 244) : isCurrent ? Color.FromArgb(76, 175, 80) : Color.FromArgb(200, 200, 205) };
            var thumb = new PlacementCanvas { Compact = true, Mine = mine, Peer = peer, PeerOrigin = new Point(kv.Value.PeerX, kv.Value.PeerY), OtherName = name, Dock = DockStyle.Fill, Cursor = Cursors.Hand };
            var when = DateTimeOffset.FromUnixTimeMilliseconds(kv.Value.Updated).ToLocalTime();
            string title = kv.Value.Name != "" ? kv.Value.Name : $"{mine.Length} + {peer.Length} screens";
            var caption = new Label { Dock = DockStyle.Bottom, Height = captionH, BackColor = Color.White, TextAlign = ContentAlignment.MiddleCenter, Font = tileFont, Cursor = Cursors.Hand, AutoEllipsis = true,
                Text = $"{title}\n{name}, {when:d MMM}" };
            _tips.SetToolTip(caption, $"{title}{(isCurrent ? " - you are at this desk" : "")}\n{mine.Length} + {peer.Length} screens, other PC at ({kv.Value.PeerX}, {kv.Value.PeerY})\nsaved {when:g}");
            tile.Controls.Add(thumb);
            tile.Controls.Add(caption);
            EventHandler select = (_, _) => { _selectedSetup = key; RefreshSetups(); };
            tile.Click += select; thumb.Click += select; caption.Click += select;
            (complete && Glissando.Layout.SameScreens(key, current) ? _gallery : _galleryOther).Controls.Add(tile);
        }
        bool anyMatch = _gallery.Controls.Count > 0, anyOther = _galleryOther.Controls.Count > 0;
        _matchHeader.Visible = anyMatch; _gallery.Visible = anyMatch;
        _otherHeader.Visible = anyOther; _galleryOther.Visible = anyOther;
        if (!anyMatch && !anyOther) { _otherHeader.Visible = true; _otherHeader.Text = "Remembered desks"; _galleryOther.Visible = true; _galleryOther.Controls.Add(new Label { Text = "(none yet)", AutoSize = true, ForeColor = SystemColors.GrayText, Padding = new Padding(2, 4, 0, 4) }); }
        else _otherHeader.Text = anyMatch ? "Other desks" : "Remembered desks";
        _gallery.ResumeLayout(); _galleryOther.ResumeLayout();
    }

    /// <summary>Shrink and nudge the window so it fits, fully visible, on whatever screen it actually opened on.</summary>
    private void FitToScreen()
    {
        var wa = Screen.FromControl(this).WorkingArea;
        if (Height > wa.Height) Height = wa.Height;
        if (Width > wa.Width) Width = wa.Width;
        if (Bottom > wa.Bottom) Top = wa.Bottom - Height;
        if (Top < wa.Top) Top = wa.Top;
        if (Right > wa.Right) Left = wa.Right - Width;
        if (Left < wa.Left) Left = wa.Left;
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
        _s.OtherMonitorList = current.OtherMonitorList;
        RefreshSetups();
    }

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
        _s.RememberPlacement(CurrentSetupKey());
        _s.CaptureEnabled = _capture.Checked;
        _s.SyncClipboard = _clip.Checked;
        _s.HideCursorWhileControlling = _hideCursor.Checked;
        _s.HideMirroredWindow = _hideMirror.Checked;
        _s.UdpMirror = _udpMirror.Checked;
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
