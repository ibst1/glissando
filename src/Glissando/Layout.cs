namespace Glissando;

public enum Dir : byte { Left = 0, Right = 1, Up = 2, Down = 3 }

/// <summary>A monitor: bounds relative to its PC's virtual-screen origin, the number Windows shows for it, and
/// the identity of the physical panel (EDID model + serial, see MonitorIdentity; empty when unknown).</summary>
public readonly record struct MonitorRect(Rectangle Bounds, int Number, string Id = "")
{
    public MonitorRect Offset(Point by) => new(new Rectangle(Bounds.X + by.X, Bounds.Y + by.Y, Bounds.Width, Bounds.Height), Number, Id);
}

/// <summary>
/// A place where the cursor crosses to the other PC: the direction it was moving, and the first
/// pixel inside the destination monitor, in the destination PC's own coordinates.
/// </summary>
public readonly record struct Crossing(Dir Dir, Point Target);

/// <summary>A stretch of one of my monitor edges that leads to the other PC (for drawing).</summary>
public readonly record struct EdgeSegment(Dir Side, int Fixed, int From, int To);

/// <summary>
/// Both PCs' monitors in one coordinate space. <see cref="PeerOrigin"/> is where the other PC's
/// virtual-screen origin sits in my pixels; the other PC stores the negated vector with the lists swapped.
/// Anywhere one of my monitor edges borders one of the peer's monitors (with none of my own monitors in
/// between) the cursor can cross, so the peer can sit beside, above, below, or in a notch of my desktop.
/// </summary>
public sealed record Layout(MonitorRect[] Mine, MonitorRect[] Peer, Point PeerOrigin)
{
    /// <summary>A peer monitor within this many pixels of flush against one of my edges still counts as a
    /// crossing. Mixed-resolution monitors that Windows snaps together leave a small step (DPI rounding: an
    /// 8 px gap between a 2160- and a 1080-tall screen was the case that motivated this), and the user cannot
    /// nudge it away in Display settings. One of my own monitors in the gap still blocks, so this only bridges
    /// genuinely-empty small steps, never crosses through my own screen.</summary>
    private const int EdgeTolerance = 16;

    public static Point Unit(Dir d) => d switch
    {
        Dir.Left => new Point(-1, 0),
        Dir.Right => new Point(1, 0),
        Dir.Up => new Point(0, -1),
        _ => new Point(0, 1),
    };

    public static Point Add(Point a, Point b, int times = 1) => new(a.X + b.X * times, a.Y + b.Y * times);

    public Layout Inverse() => new(Peer, Mine, new Point(-PeerOrigin.X, -PeerOrigin.Y));

    public MonitorRect[] PeerInMine() => Peer.Select(m => m.Offset(PeerOrigin)).ToArray();

    public static Rectangle Union(IEnumerable<MonitorRect> monitors)
    {
        Rectangle? u = null;
        foreach (var m in monitors) u = u == null ? m.Bounds : Rectangle.Union(u.Value, m.Bounds);
        return u ?? Rectangle.Empty;
    }

    public Rectangle MyBounds => Union(Mine);
    public Rectangle PeerBounds => Union(PeerInMine());

    private static bool Contains(Rectangle r, Point p) => p.X >= r.Left && p.X < r.Right && p.Y >= r.Top && p.Y < r.Bottom;

    /// <summary>
    /// The cursor is at <paramref name="p"/> (my coordinates) and the last movement was (dx, dy).
    /// Returns where it crosses to the other PC, or null if it is not on such an edge, or if the
    /// movement does not push into that edge (sliding along it, or jitter away from it, must not
    /// cross). With no movement information (0, 0) any edge the cursor sits on counts.
    /// </summary>
    public Crossing? FindCrossing(Point p, int dx, int dy)
    {
        var peer = PeerInMine();
        var candidates = new List<Crossing>(2);
        bool hasDelta = dx != 0 || dy != 0;
        static bool Toward(Dir d, int dx, int dy) => d switch
        {
            Dir.Left => dx < 0,
            Dir.Right => dx > 0,
            Dir.Up => dy < 0,
            _ => dy > 0,
        };
        foreach (var m in Mine)
        {
            var r = m.Bounds;
            if (!Contains(r, p)) continue;
            foreach (Dir d in Enum.GetValues<Dir>())
            {
                bool onEdge = d switch
                {
                    Dir.Left => p.X == r.Left,
                    Dir.Right => p.X == r.Right - 1,
                    Dir.Up => p.Y == r.Top,
                    _ => p.Y == r.Bottom - 1,
                };
                if (!onEdge) continue;
                if (hasDelta && !Toward(d, dx, dy)) continue;
                // Scan up to EdgeTolerance px beyond the edge: a peer screen a few px away (a small snap step)
                // still counts, but one of my own monitors in the way blocks first, so a seam to my own screen
                // never becomes a crossing.
                Point? hit = null;
                for (int k = 1; k <= EdgeTolerance; k++)
                {
                    var q = Add(p, Unit(d), k);
                    if (Mine.Any(o => Contains(o.Bounds, q))) break;
                    if (peer.Any(o => Contains(o.Bounds, q))) { hit = q; break; }
                }
                if (hit is not Point qp) continue;
                candidates.Add(new Crossing(d, new Point(qp.X - PeerOrigin.X, qp.Y - PeerOrigin.Y)));
            }
        }
        if (candidates.Count == 0) return null;
        if (candidates.Count == 1 || !hasDelta) return candidates[0];

        // Corner with two ways across: take the axis the mouse moves most along.
        Dir primary = Math.Abs(dx) >= Math.Abs(dy)
            ? (dx < 0 ? Dir.Left : Dir.Right)
            : (dy < 0 ? Dir.Up : Dir.Down);
        foreach (var c in candidates)
            if (c.Dir == primary) return c;
        return candidates[0];
    }

    /// <summary>Clamps <paramref name="p"/> into the monitor that contains <paramref name="reference"/> (or the nearest monitor).</summary>
    public Point ClampIntoMonitor(Point reference, Point p)
    {
        if (Mine.Length == 0) return p;
        Rectangle r = Mine.FirstOrDefault(m => Contains(m.Bounds, reference)).Bounds;
        if (r.IsEmpty)
            r = Mine.MinBy(m => DistanceSq(m.Bounds, reference)).Bounds;
        return new Point(Math.Clamp(p.X, r.Left, r.Right - 1), Math.Clamp(p.Y, r.Top, r.Bottom - 1));
    }

    private static long DistanceSq(Rectangle r, Point p)
    {
        long dx = p.X < r.Left ? r.Left - p.X : p.X >= r.Right ? p.X - (r.Right - 1) : 0;
        long dy = p.Y < r.Top ? r.Top - p.Y : p.Y >= r.Bottom ? p.Y - (r.Bottom - 1) : 0;
        return dx * dx + dy * dy;
    }

    /// <summary>True when <paramref name="p"/> is within <paramref name="margin"/> px of any edge that leads to the other PC.</summary>
    public bool NearCrossingEdge(Point p, int margin)
    {
        foreach (var s in CrossingSegments())
        {
            bool vertical = s.Side is Dir.Left or Dir.Right;
            int fixedAxis = vertical ? p.X : p.Y;
            int alongAxis = vertical ? p.Y : p.X;
            if (Math.Abs(fixedAxis - s.Fixed) <= margin && alongAxis >= s.From - margin && alongAxis < s.To + margin)
                return true;
        }
        return false;
    }

    /// <summary>Every stretch of my monitor edges that leads to the other PC.</summary>
    public IEnumerable<EdgeSegment> CrossingSegments()
    {
        var peer = PeerInMine().Select(m => m.Bounds).ToArray();
        foreach (var m in Mine)
        {
            var r = m.Bounds;
            foreach (Dir d in Enum.GetValues<Dir>())
            {
                bool vertical = d is Dir.Left or Dir.Right;
                // The strip reaches EdgeTolerance px beyond the edge, so a peer a small step away still shows a
                // crossing; one of my own monitors in the strip is subtracted below and blocks it.
                Rectangle strip = d switch
                {
                    Dir.Left => new Rectangle(r.Left - EdgeTolerance, r.Top, EdgeTolerance, r.Height),
                    Dir.Right => new Rectangle(r.Right, r.Top, EdgeTolerance, r.Height),
                    Dir.Up => new Rectangle(r.Left, r.Top - EdgeTolerance, r.Width, EdgeTolerance),
                    _ => new Rectangle(r.Left, r.Bottom, r.Width, EdgeTolerance),
                };
                // Rectangle.Intersect returns zero-width/height rectangles for edges that merely touch;
                // only overlaps with real area count.
                var intervals = new List<(int a, int b)>();
                foreach (var pr in peer)
                {
                    var i = Rectangle.Intersect(strip, pr);
                    if (i.Width <= 0 || i.Height <= 0) continue;
                    intervals.Add(vertical ? (i.Top, i.Bottom) : (i.Left, i.Right));
                }
                foreach (var o in Mine)
                {
                    var i = Rectangle.Intersect(strip, o.Bounds);
                    if (i.Width <= 0 || i.Height <= 0) continue;
                    intervals = Subtract(intervals, vertical ? (i.Top, i.Bottom) : (i.Left, i.Right));
                }
                int fixedPos = d switch { Dir.Left => r.Left, Dir.Right => r.Right, Dir.Up => r.Top, _ => r.Bottom };
                foreach (var (a, b) in intervals)
                    if (b > a) yield return new EdgeSegment(d, fixedPos, a, b);
            }
        }
    }

    private static List<(int a, int b)> Subtract(List<(int a, int b)> src, (int a, int b) cut)
    {
        var result = new List<(int a, int b)>();
        foreach (var (a, b) in src)
        {
            if (cut.b <= a || cut.a >= b) { result.Add((a, b)); continue; }
            if (cut.a > a) result.Add((a, cut.a));
            if (cut.b < b) result.Add((cut.b, b));
        }
        return result;
    }

    public static bool Overlaps(MonitorRect[] mine, MonitorRect[] peer, Point origin)
    {
        foreach (var a in mine)
            foreach (var b in peer)
            {
                var r = b.Bounds;
                r.Offset(origin);
                var i = Rectangle.Intersect(a.Bounds, r);
                if (i.Width > 0 && i.Height > 0) return true;
            }
        return false;
    }

    /// <summary>
    /// Snaps a proposed peer origin so that some peer monitor edge lies flush against some monitor of
    /// mine, nothing overlaps, and the two share at least <paramref name="minOverlap"/> pixels of edge.
    /// Also snaps the other axis to a nearby edge, so the peer can be tucked into a corner or a notch.
    /// </summary>
    public static Point Snap(MonitorRect[] mine, MonitorRect[] peer, Point proposed, int minOverlap = 64, int alignTolerance = 64)
    {
        if (mine.Length == 0 || peer.Length == 0) return proposed;

        // Candidates: for every (my monitor, peer monitor, side) the peer sits flush against that side.
        // The free axis is either the proposed position (clamped to keep some overlap) or aligned with
        // an edge of one of my monitors. Aligned variants are what let the peer drop into a notch, where
        // only the combination of both axes is free of overlap.
        var candidates = new List<(Point o, bool aligned)>();
        foreach (var A in mine)
            foreach (var B in peer)
                foreach (Dir side in Enum.GetValues<Dir>())
                {
                    var a = A.Bounds; var b = B.Bounds; var o = proposed;
                    bool vertical = side is Dir.Left or Dir.Right;
                    switch (side)
                    {
                        case Dir.Right: o.X = a.Right - b.X; break;
                        case Dir.Left: o.X = a.Left - b.Right; break;
                        case Dir.Down: o.Y = a.Bottom - b.Y; break;
                        default: o.Y = a.Top - b.Bottom; break;
                    }
                    int ov = Math.Min(minOverlap, vertical ? Math.Min(a.Height, b.Height) : Math.Min(a.Width, b.Width));
                    if (vertical) o.Y = Math.Clamp(o.Y, a.Top + ov - b.Bottom, Math.Max(a.Top + ov - b.Bottom, a.Bottom - ov - b.Y));
                    else o.X = Math.Clamp(o.X, a.Left + ov - b.Right, Math.Max(a.Left + ov - b.Right, a.Right - ov - b.X));
                    candidates.Add((o, false));

                    foreach (var A2 in mine)
                    {
                        var a2 = A2.Bounds;
                        int[] options = vertical
                            ? [a2.Top - b.Y, a2.Bottom - b.Bottom, a2.Bottom - b.Y, a2.Top - b.Bottom]
                            : [a2.Left - b.X, a2.Right - b.Right, a2.Right - b.X, a2.Left - b.Right];
                        foreach (var v in options)
                            candidates.Add((vertical ? new Point(o.X, v) : new Point(v, o.Y), true));
                    }
                }

        bool Valid(Point o) => !Overlaps(mine, peer, o) && new Layout(mine, peer, o).CrossingSegments().Any();
        long Dist(Point p, Point q) => Sq(p.X - q.X) + Sq(p.Y - q.Y);

        var valid = candidates.Where(c => Valid(c.o)).Distinct().ToList();
        if (valid.Count == 0)
        {
            var u = Union(mine); var pb = Union(peer);
            return new Point(u.Right - pb.X, proposed.Y);
        }
        var best = valid.MinBy(c => Dist(c.o, proposed));

        // Nearby aligned position on the same side? Prefer it, so corners and edges line up when close.
        if (!best.aligned)
        {
            var near = valid
                .Where(c => c.aligned && (c.o.X == best.o.X || c.o.Y == best.o.Y))
                .Where(c => Math.Max(Math.Abs(c.o.X - best.o.X), Math.Abs(c.o.Y - best.o.Y)) <= alignTolerance)
                .OrderBy(c => Dist(c.o, best.o))
                .Take(1).ToList();
            if (near.Count == 1) best = near[0];
        }
        return best.o;
    }

    private static long Sq(long v) => v * v;

    // ---- screen setups --------------------------------------------------------------

    /// <summary>A monitor set as text, order-independent: "x,y,w,h#n@id;...".</summary>
    public static string MonitorSignature(IEnumerable<MonitorRect> monitors) =>
        string.Join(";", monitors.OrderBy(m => m.Bounds.Y).ThenBy(m => m.Bounds.X)
            .Select(m => $"{m.Bounds.X},{m.Bounds.Y},{m.Bounds.Width},{m.Bounds.Height}#{m.Number}@{m.Id}"));

    /// <summary>Identifies a desk: this PC's monitors, the other PC's name and its monitors. Placements are
    /// remembered per key (Settings.Placements). Before the other PC has said hello its part is empty, which
    /// is a key of its own; the real one takes over when the monitors arrive.</summary>
    public static string SetupKey(MonitorRect[] mine, string peerName, MonitorRect[] peer) =>
        $"{MonitorSignature(mine)}|{peerName}|{MonitorSignature(peer)}";

    /// <summary>True when both sides are known: this PC's monitors, the other PC's name and its monitors. Only
    /// complete setups are remembered; before hello the key is provisional.</summary>
    public static bool IsCompleteSetupKey(string key)
    {
        var parts = key.Split('|');
        return parts.Length == 3 && parts[0] != "" && parts[1] != "" && parts[2] != "";
    }

    /// <summary>The monitors of both sides back out of a key (the dialog draws remembered setups from it).</summary>
    public static bool TryParseSetupKey(string key, out MonitorRect[] mine, out string peerName, out MonitorRect[] peer)
    {
        mine = []; peer = []; peerName = "";
        var parts = key.Split('|');
        if (parts.Length != 3) return false;
        static MonitorRect[] Parse(string sig) => sig.Split(';', StringSplitOptions.RemoveEmptyEntries)
            .Select(p =>
            {
                var at = p.Split('@', 2);
                var np = at[0].Split('#');
                var a = np[0].Split(',');
                return (a, n: np.Length > 1 && int.TryParse(np[1], out var n) ? n : 0, id: at.Length > 1 ? at[1] : "");
            })
            .Where(x => x.a.Length >= 4 && x.a.All(v => int.TryParse(v, out _)))
            .Select(x => new MonitorRect(new Rectangle(int.Parse(x.a[0]), int.Parse(x.a[1]), int.Parse(x.a[2]), int.Parse(x.a[3])), x.n, x.id))
            .ToArray();
        mine = Parse(parts[0]); peerName = parts[1]; peer = Parse(parts[2]);
        return mine.Length > 0;
    }

    /// <summary>Two desks with the same physical screens on this PC and on the same other PC - the same panels
    /// by EDID identity, whatever their positions or numbers - which is what makes them candidates when the exact
    /// arrangement is not remembered. Where identities are missing (fake monitors, an EDID that could not be
    /// read) that side compares by size instead, so nothing is worse than before.</summary>
    public static bool SameScreens(string a, string b)
    {
        if (!TryParseSetupKey(a, out var am, out var an, out var ap) || !TryParseSetupKey(b, out var bm, out var bn, out var bp)) return false;
        return an == bn && SameSet(am, bm) && SameSet(ap, bp);

        static bool SameSet(MonitorRect[] x, MonitorRect[] y)
        {
            if (x.Length != y.Length) return false;
            bool ids = x.All(m => m.Id != "") && y.All(m => m.Id != "");
            IEnumerable<string> Keys(MonitorRect[] r) => r.Select(m => ids ? m.Id : $"{m.Bounds.Width}x{m.Bounds.Height}").OrderBy(k => k, StringComparer.Ordinal);
            return Keys(x).SequenceEqual(Keys(y));
        }
    }

    /// <summary>A setup key in words, for the settings dialog.</summary>
    public static string DescribeSetup(string key, string peerNameNow)
    {
        var parts = key.Split('|');
        static string Side(string sig)
        {
            var sizes = sig.Split(';', StringSplitOptions.RemoveEmptyEntries)
                .Select(p => p.Split('#')[0].Split(','))
                .Where(a => a.Length >= 4)
                .Select(a => $"{a[2]}x{a[3]}").ToArray();
            return sizes.Length == 0 ? "screens unknown" : sizes.Length == 1 ? sizes[0] : $"{sizes.Length} screens ({string.Join(", ", sizes)})";
        }
        string name = parts.Length > 1 && parts[1] != "" ? parts[1] : (string.IsNullOrEmpty(peerNameNow) ? "other PC" : peerNameNow);
        return $"this PC {Side(parts[0])} - {name} {(parts.Length > 2 ? Side(parts[2]) : "screens unknown")}";
    }

    // ---- this PC's monitors -------------------------------------------------------

    /// <summary>This PC's monitors relative to the virtual screen's top-left, numbered as in Display settings.</summary>
    public static MonitorRect[] MyMonitors()
    {
        // GLISSANDO_FAKE_MONITORS="x,y,w,h;x,y,w,h" previews a layout on a single-screen machine.
        var fake = Environment.GetEnvironmentVariable("GLISSANDO_FAKE_MONITORS");
        if (!string.IsNullOrWhiteSpace(fake))
        {
            var rects = fake.Split(';', StringSplitOptions.RemoveEmptyEntries)
                .Select(r => r.Split(',').Select(int.Parse).ToArray())
                .Select(a => new Rectangle(a[0], a[1], a[2], a[3])).ToArray();
            int minX = rects.Min(r => r.X), minY = rects.Min(r => r.Y);
            return rects.Select((r, i) => new MonitorRect(new Rectangle(r.X - minX, r.Y - minY, r.Width, r.Height), i + 1)).ToArray();
        }
        var v = Native.VirtualScreen();
        var ids = MonitorIdentity.Query();
        var list = LiveScreens()
            .Select((s, i) => new MonitorRect(
                new Rectangle(s.bounds.X - v.X, s.bounds.Y - v.Y, s.bounds.Width, s.bounds.Height),
                DisplayNumber(s.device, i + 1),
                ids.TryGetValue(s.device, out var info) ? info.Id : ""))
            .ToArray();
        return list.Length > 0 ? list : [new MonitorRect(new Rectangle(0, 0, v.Width, v.Height), 1)];
    }

    /// <summary>The monitors straight from Windows (EnumDisplayMonitors) rather than WinForms' Screen.AllScreens,
    /// which is a cache refreshed by a display-change notice that a tray app with no visible window does not
    /// always get: the other PC kept reporting one monitor of four for hours after waking (2026-09-24).</summary>
    private static List<(Rectangle bounds, string device)> LiveScreens()
    {
        var list = new List<(Rectangle, string)>();
        EnumDisplayMonitors(IntPtr.Zero, IntPtr.Zero, (h, _, _, _) =>
        {
            var mi = new MONITORINFOEX { cbSize = System.Runtime.InteropServices.Marshal.SizeOf<MONITORINFOEX>() };
            if (GetMonitorInfoW(h, ref mi))
                list.Add((Rectangle.FromLTRB(mi.rcMonitor.Left, mi.rcMonitor.Top, mi.rcMonitor.Right, mi.rcMonitor.Bottom), mi.szDevice ?? ""));
            return true;
        }, IntPtr.Zero);
        return list;
    }

    private delegate bool MonitorEnumProc(IntPtr hMonitor, IntPtr hdc, IntPtr rect, IntPtr data);
    [System.Runtime.InteropServices.DllImport("user32.dll")]
    private static extern bool EnumDisplayMonitors(IntPtr hdc, IntPtr clip, MonitorEnumProc proc, IntPtr data);
    [System.Runtime.InteropServices.DllImport("user32.dll", CharSet = System.Runtime.InteropServices.CharSet.Unicode)]
    private static extern bool GetMonitorInfoW(IntPtr hMonitor, ref MONITORINFOEX info);
    [System.Runtime.InteropServices.StructLayout(System.Runtime.InteropServices.LayoutKind.Sequential)]
    private struct RECT { public int Left, Top, Right, Bottom; }
    [System.Runtime.InteropServices.StructLayout(System.Runtime.InteropServices.LayoutKind.Sequential, CharSet = System.Runtime.InteropServices.CharSet.Unicode)]
    private struct MONITORINFOEX
    {
        public int cbSize;
        public RECT rcMonitor, rcWork;
        public uint dwFlags;
        [System.Runtime.InteropServices.MarshalAs(System.Runtime.InteropServices.UnmanagedType.ByValTStr, SizeConst = 32)] public string szDevice;
    }

    /// <summary>The virtual screen this PC's monitor coordinates are relative to (fake layouts have none).</summary>
    public static Rectangle MyVirtualScreen()
    {
        var fake = Environment.GetEnvironmentVariable("GLISSANDO_FAKE_MONITORS");
        if (!string.IsNullOrWhiteSpace(fake)) return new Rectangle(Point.Empty, Union(MyMonitors()).Size);
        return Native.VirtualScreen();
    }

    private static Size DefaultPeerSize(MonitorRect[] mine)
    {
        if (string.IsNullOrWhiteSpace(Environment.GetEnvironmentVariable("GLISSANDO_FAKE_MONITORS")))
        {
            var primary = Screen.PrimaryScreen?.Bounds.Size ?? Size.Empty;
            if (!primary.IsEmpty) return primary;
        }
        return mine.Length > 0 ? mine.MaxBy(m => (long)m.Bounds.Width * m.Bounds.Height).Bounds.Size : new Size(1920, 1080);
    }

    /// <summary>"\\.\DISPLAY3" -> 3, which is the number Windows shows in Display settings.</summary>
    private static int DisplayNumber(string deviceName, int fallback)
    {
        int i = deviceName.Length;
        while (i > 0 && char.IsDigit(deviceName[i - 1])) i--;
        return i < deviceName.Length && int.TryParse(deviceName.AsSpan(i), out int n) ? n : fallback;
    }

    /// <summary>Builds the layout for this PC from settings: my monitors, the peer's last known monitors, and the stored origin.</summary>
    public static Layout FromSettings(Settings s)
    {
        var mine = MyMonitors();
        var peer = s.OtherMonitorList;
        if (peer.Length == 0)
        {
            // Nothing known about the other PC yet: assume one screen like our primary one,
            // not a copy of our whole multi-monitor desktop.
            var size = s.OtherScreen.IsEmpty ? DefaultPeerSize(mine) : s.OtherScreen;
            peer = [new MonitorRect(new Rectangle(Point.Empty, size), 0)];
        }
        Point origin;
        if (s.PeerX is int x && s.PeerY is int y)
        {
            origin = new Point(x, y);
        }
        else
        {
            // Never placed: put the other PC to the right, level with our biggest monitor, flush against an edge.
            var anchor = mine.MaxBy(m => (long)m.Bounds.Width * m.Bounds.Height).Bounds;
            var peerBounds = Union(peer);
            var proposed = new Point(Union(mine).Right - peerBounds.X, anchor.Top + (anchor.Height - peerBounds.Height) / 2 - peerBounds.Y);
            origin = Snap(mine, peer, proposed);
        }
        return new Layout(mine, peer, origin);
    }
}
