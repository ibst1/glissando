namespace Glissando;

public enum Dir : byte { Left = 0, Right = 1, Up = 2, Down = 3 }

/// <summary>A monitor: bounds relative to its PC's virtual-screen origin, and the number Windows shows for it.</summary>
public readonly record struct MonitorRect(Rectangle Bounds, int Number)
{
    public MonitorRect Offset(Point by) => new(new Rectangle(Bounds.X + by.X, Bounds.Y + by.Y, Bounds.Width, Bounds.Height), Number);
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
    /// Returns where it crosses to the other PC, or null if it is not on such an edge.
    /// Windows clamps the cursor to the monitors, so being on the edge is enough; the delta only
    /// disambiguates corners where two directions both lead across.
    /// </summary>
    public Crossing? FindCrossing(Point p, int dx, int dy)
    {
        var peer = PeerInMine();
        var candidates = new List<Crossing>(2);
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
                var q = Add(p, Unit(d));
                if (Mine.Any(o => Contains(o.Bounds, q))) continue;   // a seam to another of my monitors
                if (!peer.Any(o => Contains(o.Bounds, q))) continue;  // empty desktop, not the peer
                candidates.Add(new Crossing(d, new Point(q.X - PeerOrigin.X, q.Y - PeerOrigin.Y)));
            }
        }
        if (candidates.Count == 0) return null;
        if (candidates.Count == 1 || (dx == 0 && dy == 0)) return candidates[0];

        Dir? wantX = dx < 0 ? Dir.Left : dx > 0 ? Dir.Right : null;
        Dir? wantY = dy < 0 ? Dir.Up : dy > 0 ? Dir.Down : null;
        Dir? primary = Math.Abs(dx) >= Math.Abs(dy) ? wantX ?? wantY : wantY ?? wantX;
        Dir? secondary = primary == wantX ? wantY : wantX;
        foreach (var want in new[] { primary, secondary })
            if (want != null)
                foreach (var c in candidates)
                    if (c.Dir == want) return c;
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
                Rectangle strip = d switch
                {
                    Dir.Left => new Rectangle(r.Left - 1, r.Top, 1, r.Height),
                    Dir.Right => new Rectangle(r.Right, r.Top, 1, r.Height),
                    Dir.Up => new Rectangle(r.Left, r.Top - 1, r.Width, 1),
                    _ => new Rectangle(r.Left, r.Bottom, r.Width, 1),
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
    public static Point Snap(MonitorRect[] mine, MonitorRect[] peer, Point proposed, int minOverlap = 64)
    {
        if (mine.Length == 0 || peer.Length == 0) return proposed;
        Point? best = null;
        long bestD = long.MaxValue;
        Dir bestSide = Dir.Right;
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
                    if (Overlaps(mine, peer, o)) continue;
                    long d = Sq(o.X - proposed.X) + Sq(o.Y - proposed.Y);
                    if (d < bestD) { bestD = d; best = o; bestSide = side; }
                }
        if (best is not Point chosen)
        {
            var u = Union(mine); var pb = Union(peer);
            return new Point(u.Right - pb.X, proposed.Y);
        }

        // Corner snap: align the free axis with a nearby edge if that still leaves a crossing.
        bool sideVertical = bestSide is Dir.Left or Dir.Right;
        var mu = Union(mine);
        long tol = Math.Max(24, (long)(0.05 * Math.Max(mu.Width, mu.Height)));
        Point cornered = chosen;
        long cd = long.MaxValue;
        foreach (var A in mine)
            foreach (var B in peer)
            {
                var a = A.Bounds; var b = B.Bounds;
                int[] options = sideVertical
                    ? [a.Top - b.Y, a.Bottom - b.Bottom, a.Bottom - b.Y, a.Top - b.Bottom]
                    : [a.Left - b.X, a.Right - b.Right, a.Right - b.X, a.Left - b.Right];
                foreach (var v in options)
                {
                    var o = sideVertical ? new Point(chosen.X, v) : new Point(v, chosen.Y);
                    long d = sideVertical ? Sq(v - chosen.Y) : Sq(v - chosen.X);
                    if (d > tol * tol || d >= cd) continue;
                    if (Overlaps(mine, peer, o)) continue;
                    if (!new Layout(mine, peer, o).CrossingSegments().Any()) continue;
                    cd = d; cornered = o;
                }
            }
        return cornered;
    }

    private static long Sq(long v) => v * v;

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
        var list = Screen.AllScreens
            .Select((s, i) => new MonitorRect(
                new Rectangle(s.Bounds.X - v.X, s.Bounds.Y - v.Y, s.Bounds.Width, s.Bounds.Height),
                DisplayNumber(s.DeviceName, i + 1)))
            .ToArray();
        return list.Length > 0 ? list : [new MonitorRect(new Rectangle(0, 0, v.Width, v.Height), 1)];
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
