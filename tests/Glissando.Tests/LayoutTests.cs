using System.Drawing;
using Xunit;

namespace Glissando.Tests;

public class LayoutTests
{
    // Four monitors: three 2560x1440 in a row with a 1920x1200 above the middle one, normalised so the
    // virtual screen starts at (0,0). The empty top-left corner is the "notch".
    private static readonly MonitorRect[] Four =
    [
        new(new Rectangle(0, 1200, 2560, 1440), 1),
        new(new Rectangle(2560, 1200, 2560, 1440), 2),
        new(new Rectangle(5120, 1200, 2560, 1440), 3),
        new(new Rectangle(2560, 0, 1920, 1200), 4),
    ];

    private static readonly MonitorRect[] Laptop = [new(new Rectangle(0, 0, 1920, 1200), 1)];

    private static Layout Notch() => new(Four, Laptop, new Point(640, 0));

    [Fact]
    public void Notch_LeftEdgeOfMonitor4_CrossesLeft()
    {
        var c = Notch().FindCrossing(new Point(2560, 500), -1, 0);
        Assert.Equal(new Crossing(Dir.Left, new Point(1919, 500)), c);
    }

    [Fact]
    public void Notch_TopOfMonitor1_CrossesUp()
    {
        var c = Notch().FindCrossing(new Point(1000, 1200), 0, -1);
        Assert.Equal(new Crossing(Dir.Up, new Point(360, 1199)), c);
    }

    [Fact]
    public void Seam_BetweenOwnMonitors_IsNotACrossing()
    {
        Assert.Null(Notch().FindCrossing(new Point(2559, 1500), 1, 0));   // monitor 1 → monitor 2
        Assert.Null(Notch().FindCrossing(new Point(3000, 1200), 0, -1));  // monitor 2 → monitor 4
    }

    [Fact]
    public void EmptyDesktop_IsNotACrossing()
    {
        Assert.Null(Notch().FindCrossing(new Point(7679, 1300), 1, 0));   // right of monitor 3: nothing there
        Assert.Null(Notch().FindCrossing(new Point(100, 1200), 0, -1));   // above monitor 1 left of the peer
    }

    [Fact]
    public void NotOnAnEdge_IsNotACrossing()
    {
        Assert.Null(Notch().FindCrossing(new Point(2000, 1300), -1, 0));
    }

    [Fact]
    public void OnTheEdge_ButNotPushingIntoIt_IsNotACrossing()
    {
        var edge = new Point(2560, 500); // left edge of monitor 4, peer beyond
        Assert.Null(Notch().FindCrossing(edge, 0, 5));    // sliding along the edge
        Assert.Null(Notch().FindCrossing(edge, 3, 0));    // moving away from it
        Assert.NotNull(Notch().FindCrossing(edge, -1, 4)); // mostly along, but still pushing in
        Assert.NotNull(Notch().FindCrossing(edge, 0, 0));  // no movement info: counts
    }

    [Fact]
    public void Corner_PrefersTheDominantMovementDirection()
    {
        // One monitor of mine; the peer has two monitors, one to my right and one directly above me,
        // so my top-right corner can cross in two directions.
        MonitorRect[] mine = [new(new Rectangle(0, 0, 1000, 1000), 1)];
        MonitorRect[] peer = [new(new Rectangle(0, 0, 1000, 1000), 1), new(new Rectangle(-1000, -1000, 1000, 1000), 2)];
        var layout = new Layout(mine, peer, new Point(1000, 0));
        var corner = new Point(999, 0);

        Assert.Equal(Dir.Right, layout.FindCrossing(corner, 3, -1)!.Value.Dir);
        Assert.Equal(Dir.Up, layout.FindCrossing(corner, 1, -3)!.Value.Dir);
        Assert.Equal(Dir.Up, layout.FindCrossing(corner, 0, -1)!.Value.Dir);
        Assert.NotNull(layout.FindCrossing(corner, 0, 0)); // no movement info: still a crossing
    }

    [Fact]
    public void Inverse_MapsTheCrossingBackToWhereItCameFrom()
    {
        var mine = Notch();
        var theirs = mine.Inverse();
        var start = new Point(2560, 500);
        var over = mine.FindCrossing(start, -1, 0)!.Value;

        // On the other PC the cursor lands at the target; pushing back the way it came returns to us.
        var back = theirs.FindCrossing(over.Target, 1, 0);
        Assert.NotNull(back);
        Assert.Equal(Dir.Right, back!.Value.Dir);
        Assert.Equal(start, back.Value.Target);
    }

    [Fact]
    public void Inverse_IsItsOwnInverse()
    {
        var l = Notch();
        Assert.Equal(l.PeerOrigin, l.Inverse().Inverse().PeerOrigin);
        Assert.Equal(new Point(-640, 0), l.Inverse().PeerOrigin);
    }

    [Fact]
    public void NearCrossingEdge_TrueOnlyCloseToACrossingEdge()
    {
        var l = Notch();
        Assert.True(l.NearCrossingEdge(new Point(2565, 500), 24));  // 5 px inside monitor 4's left (a crossing) edge
        Assert.False(l.NearCrossingEdge(new Point(2600, 500), 24)); // 40 px inside: clear
        Assert.False(l.NearCrossingEdge(new Point(5, 1500), 24));   // left edge of monitor 1: no peer there
        Assert.False(l.NearCrossingEdge(new Point(1000, 2000), 24)); // middle of a monitor
    }

    [Fact]
    public void CrossingSegments_NotchTouchesTwoEdges()
    {
        var segments = Notch().CrossingSegments().OrderBy(s => s.Side).ToArray();
        Assert.Equal(2, segments.Length);
        Assert.Contains(new EdgeSegment(Dir.Left, 2560, 0, 1200), segments);
        Assert.Contains(new EdgeSegment(Dir.Up, 1200, 640, 2560), segments);
    }

    [Fact]
    public void CrossingSegments_TouchingCornersDoNotCountAsOverlap()
    {
        // Peer flush right of monitor 3, level with it. Monitor 4 touches the strip only at a corner.
        var layout = new Layout(Four, Laptop, new Point(7680, 1200));
        var only = Assert.Single(layout.CrossingSegments());
        Assert.Equal(new EdgeSegment(Dir.Right, 7680, 1200, 2400), only);
    }

    [Fact]
    public void SmallStep_BridgedSoBothMonitorsCross()
    {
        // A tall and a short monitor side by side; the short one's bottom is 8 px above the tall one's -- the
        // DPI step Windows leaves between a 2160- and a 1080-tall screen, which it will not let you nudge away.
        // A single peer sits flush below the tall one. Both must cross to it: the tall one exactly, the short
        // one across the 8 px step.
        MonitorRect[] mine = [new(new Rectangle(0, 0, 100, 100), 1), new(new Rectangle(100, 0, 100, 92), 2)];
        MonitorRect[] peer = [new(new Rectangle(0, 0, 200, 50), 1)];
        var layout = new Layout(mine, peer, new Point(0, 100)); // peer occupies y 100..150

        Assert.NotNull(layout.FindCrossing(new Point(50, 99), 0, 1));   // tall monitor, flush
        Assert.NotNull(layout.FindCrossing(new Point(150, 91), 0, 1));  // short monitor, 8 px step bridged
        Assert.Equal(2, layout.CrossingSegments().Count(s => s.Side == Dir.Down));
    }

    [Fact]
    public void GapBeyondTolerance_StillDoesNotCross()
    {
        MonitorRect[] mine = [new(new Rectangle(100, 0, 100, 92), 1)];
        MonitorRect[] peer = [new(new Rectangle(0, 0, 200, 50), 1)];
        var layout = new Layout(mine, peer, new Point(0, 120)); // peer 28 px below the monitor's bottom
        Assert.Null(layout.FindCrossing(new Point(150, 91), 0, 1));
    }

    [Fact]
    public void Snap_IntoTheNotch_TouchesBothEdges()
    {
        var origin = Layout.Snap(Four, Laptop, new Point(700, 50));
        Assert.Equal(new Point(640, 0), origin);
    }

    [Fact]
    public void Snap_FlushRight_KeepsVerticalPosition()
    {
        var origin = Layout.Snap(Four, Laptop, new Point(9000, 1300));
        Assert.Equal(7680, origin.X);
        Assert.Equal(1300, origin.Y);
        Assert.False(Layout.Overlaps(Four, Laptop, origin));
    }

    [Fact]
    public void Snap_NeverOverlapsAndAlwaysLeavesACrossing()
    {
        var rnd = new Random(1234);
        for (int i = 0; i < 300; i++)
        {
            var proposed = new Point(rnd.Next(-4000, 12000), rnd.Next(-3000, 6000));
            var origin = Layout.Snap(Four, Laptop, proposed);
            Assert.False(Layout.Overlaps(Four, Laptop, origin));
            Assert.NotEmpty(new Layout(Four, Laptop, origin).CrossingSegments());
        }
    }

    [Fact]
    public void Snap_KeepsAMinimumOverlapAlongTheEdge()
    {
        // Dragged far away from everything: wherever it lands, it must share at least 64 px of edge.
        foreach (var proposed in new[] { new Point(7680, -5000), new Point(-9000, 9000), new Point(20000, 1300) })
        {
            var origin = Layout.Snap(Four, Laptop, proposed);
            var longest = new Layout(Four, Laptop, origin).CrossingSegments().Max(s => s.To - s.From);
            Assert.True(longest >= 64, $"{proposed} -> {origin}: longest crossing {longest}");
        }
    }

    [Fact]
    public void Snap_AlignsOnlyWithinTolerance()
    {
        Assert.Equal(1200, Layout.Snap(Four, Laptop, new Point(9000, 1230), alignTolerance: 64).Y); // snaps to monitor 3's top
        Assert.Equal(1300, Layout.Snap(Four, Laptop, new Point(9000, 1300), alignTolerance: 64).Y); // too far: kept
    }

    [Fact]
    public void ClampIntoMonitor_KeepsPointsInsideTheMonitorContainingTheReference()
    {
        var l = Notch();
        var p = l.ClampIntoMonitor(new Point(2560, 500), new Point(2557, 500)); // inset left of monitor 4's left edge
        Assert.Equal(new Point(2560, 500), p);
    }
}

public class CarryPlacementTests
{
    // RS at the clinic desk: two monitors above its own panel R; LU's panel L stands left of R, 16 px lower.
    private static readonly MonitorRect[] RsDocked =
    [
        new(new Rectangle(0, 0, 1920, 1080), 2, "X"),
        new(new Rectangle(1920, 16, 1920, 1080), 3, "Y"),
        new(new Rectangle(0, 1080, 1920, 1200), 1, "R"),
    ];
    private static readonly MonitorRect[] RsAlone = [new(new Rectangle(0, 0, 1920, 1200), 1, "R")];
    private static readonly MonitorRect[] Lu = [new(new Rectangle(0, 0, 1920, 1200), 1, "L")];

    [Fact]
    public void Undocking_KeepsTheOtherPcBesideTheSharedPanel()
    {
        // 2026-10-09: the docked placement was filed as is for RS alone, putting LU at a corner (-1920,1096).
        var p = Layout.CarryPlacement(new Point(-1920, 1096), Layout.SetupKey(RsDocked, "LU", Lu), Layout.SetupKey(RsAlone, "LU", Lu));
        Assert.Equal(new Point(-1920, 16), p);
    }

    [Fact]
    public void TheOtherPc_CarriesTheMirrorImage()
    {
        var p = Layout.CarryPlacement(new Point(1920, -1096), Layout.SetupKey(Lu, "RS", RsDocked), Layout.SetupKey(Lu, "RS", RsAlone));
        Assert.Equal(new Point(1920, -16), p);
    }

    [Fact]
    public void NoPanelInCommon_CarriesNothing()
    {
        MonitorRect[] other = [new(new Rectangle(0, 0, 1920, 1200), 1, "Z")];
        Assert.Null(Layout.CarryPlacement(new Point(-1920, 1096), Layout.SetupKey(RsDocked, "LU", Lu), Layout.SetupKey(other, "LU", Lu)));
    }

    [Fact]
    public void ViewHash_FollowsBoundsOnly()
    {
        Assert.Equal(0u, Layout.ViewHash([]));
        Assert.Equal(Layout.ViewHash(RsAlone), Layout.ViewHash([RsAlone[0] with { Id = "", Number = 7 }]));
        Assert.NotEqual(Layout.ViewHash(RsAlone), Layout.ViewHash(RsDocked));
    }
}

public class DeskForMyScreensTests
{
    private static readonly MonitorRect[] Home =
    [
        new(new Rectangle(1920, 0, 3840, 2160), 2, "A"),
        new(new Rectangle(5760, 0, 3840, 2160), 3, "B"),
        new(new Rectangle(0, 1072, 1920, 1080), 4, "C"),
        new(new Rectangle(3118, 2160, 1920, 1200), 1, "L"),
    ];
    private static readonly MonitorRect[] LaptopAlone = [new(new Rectangle(0, 0, 1920, 1200), 1, "L")];
    private static readonly MonitorRect[] PeerLaptop = [new(new Rectangle(0, 0, 1920, 1200), 1, "R")];
    private static readonly MonitorRect[] PeerOffice =
    [
        new(new Rectangle(0, 0, 1920, 1080), 2, "X"),
        new(new Rectangle(1920, 16, 1920, 1080), 3, "Y"),
        new(new Rectangle(0, 1080, 1920, 1200), 1, "R"),
    ];

    [Fact]
    public void HomeScreens_FindTheHomeDesk_NotTheOfficeOneUsedLast()
    {
        string home = Layout.SetupKey(Home, "RS", PeerLaptop);
        string office = Layout.SetupKey(LaptopAlone, "RS", PeerOffice);
        var desks = new[] { (home, "Hemma", 1L), (office, "", 3L) };
        Assert.Equal(home, Layout.DeskForMyScreens(desks, Home, "RS"));
    }

    [Fact]
    public void SamePanelsMoved_StillFound_ExactArrangementPreferred()
    {
        var moved = Home.Select(m => m with { Bounds = m.Bounds with { X = m.Bounds.X + 10 } }).ToArray();
        string exact = Layout.SetupKey(Home, "RS", PeerLaptop);
        string other = Layout.SetupKey(moved, "RS", PeerOffice);
        Assert.Equal(other, Layout.DeskForMyScreens([(other, "", 5L)], Home, "RS"));
        Assert.Equal(exact, Layout.DeskForMyScreens([(other, "Named", 5L), (exact, "", 1L)], Home, "RS"));
    }

    [Fact]
    public void RearrangedWhileAway_BorrowedPeerSide_MakesADeskOfItsOwn()
    {
        // The laptop panel moved right on the Screens tab while RS was away: the other PC's side comes from the
        // desk found, this PC's from the screens as they stand - a new desk, not the old arrangement's key.
        var moved = Home.Select(m => m.Id == "L" ? m with { Bounds = m.Bounds with { X = 4000 } } : m).ToArray();
        string old = Layout.SetupKey(Home, "RS", PeerOffice);
        var desk = Layout.DeskForMyScreens([(old, "Klinik", 5L)], moved, "");
        Assert.Equal(old, desk);
        Assert.True(Layout.TryParseSetupKey(desk!, out _, out var name, out var peer));
        string key = Layout.SetupKey(moved, name, peer);
        Assert.NotEqual(old, key);
        Assert.Equal(Layout.SetupKey(moved, "RS", PeerOffice), key);
        Assert.True(Layout.SameScreens(old, key));
    }

    [Fact]
    public void UnknownScreensOrOtherPeer_Null()
    {
        string home = Layout.SetupKey(Home, "RS", PeerLaptop);
        Assert.Null(Layout.DeskForMyScreens([(home, "", 1L)], LaptopAlone, "RS"));
        Assert.Null(Layout.DeskForMyScreens([(home, "", 1L)], Home, "SOMEONE"));
        Assert.Equal(home, Layout.DeskForMyScreens([(home, "", 1L)], Home, ""));   // before any hello this run
    }
}

public class ArrangementToRestoreTests
{
    // The clinic desk: a 1080p screen, two 4K screens and the laptop's own panel under the 4K row.
    private static readonly MonitorRect[] Desk =
    [
        new(new Rectangle(1920, 0, 3840, 2160), 3, "A"),
        new(new Rectangle(5760, 0, 3840, 2160), 4, "B"),
        new(new Rectangle(0, 1080, 1920, 1080), 2, "C"),
        new(new Rectangle(2599, 2160, 1920, 1200), 1, "L"),
    ];
    // After sleep (2026-10-18): renumbered, and the panel top right.
    private static readonly MonitorRect[] Woke =
    [
        new(new Rectangle(1920, 0, 3840, 2160), 2, "A"),
        new(new Rectangle(5760, 0, 3840, 2160), 3, "B"),
        new(new Rectangle(9600, 0, 1920, 1200), 4, "L"),
        new(new Rectangle(0, 1080, 1920, 1080), 1, "C"),
    ];
    private static readonly MonitorRect[] Peer = [new(new Rectangle(0, 0, 1920, 1200), 1, "R")];
    private static readonly string DeskKey = Layout.SetupKey(Desk, "LU", Peer);
    private static readonly string ThreeScreens = Layout.SetupKey(Desk.Take(3).ToArray(), "LU", Peer);

    [Fact]
    public void ScreensBackElsewhere_MovedBackByNumber()
    {
        var moves = Layout.ArrangementToRestore([(DeskKey, 5L)], Woke, ThreeScreens);
        Assert.NotNull(moves);
        Assert.Contains((4, new Point(2599, 2160)), moves!);
        Assert.Contains((1, new Point(0, 1080)), moves!);
        Assert.Contains((2, new Point(1920, 0)), moves!);
    }

    [Fact]
    public void SameScreensRearranged_LeftAlone()
    {
        Assert.Null(Layout.ArrangementToRestore([(DeskKey, 5L)], Woke, DeskKey));
    }

    [Fact]
    public void LastUsedArrangementWins()
    {
        string wokeKey = Layout.SetupKey(Woke, "LU", Peer);
        Assert.Null(Layout.ArrangementToRestore([(DeskKey, 5L), (wokeKey, 9L)], Woke, ThreeScreens));
        Assert.NotNull(Layout.ArrangementToRestore([(DeskKey, 9L), (wokeKey, 5L)], Woke, ThreeScreens));
    }

    [Fact]
    public void AlreadyInPlace_ShiftedOrigin_Null()
    {
        var shifted = Desk.Select(m => m with { Bounds = m.Bounds with { X = m.Bounds.X + 100 } }).ToArray();
        Assert.Null(Layout.ArrangementToRestore([(DeskKey, 5L)], shifted, ThreeScreens));
    }

    [Fact]
    public void UnknownIdentityOrResized_Null()
    {
        var noId = Woke.Select(m => m.Id == "L" ? m with { Id = "" } : m).ToArray();
        Assert.Null(Layout.ArrangementToRestore([(DeskKey, 5L)], noId, ThreeScreens));
        var resized = Woke.Select(m => m.Id == "L" ? m with { Bounds = m.Bounds with { Width = 1280, Height = 800 } } : m).ToArray();
        Assert.Null(Layout.ArrangementToRestore([(DeskKey, 5L)], resized, ThreeScreens));
    }
}
