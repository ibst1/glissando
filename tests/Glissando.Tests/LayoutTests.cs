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
