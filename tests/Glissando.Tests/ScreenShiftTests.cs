using System.Drawing;
using Xunit;

namespace Glissando.Tests;

/// <summary>Screens moved on their own (Layout.MineShift / PeerShift) and the snapping that places them.</summary>
public class ScreenShiftTests
{
    private static readonly MonitorRect[] Laptop = [new(new Rectangle(0, 0, 1920, 1200), 1)];


    // The other PC: two 1920x1080 screens, A above B in its Windows. In the placement they stand apart: A left of
    // this laptop, B right of it.
    private static readonly MonitorRect[] PeerPair =
    [
        new(new Rectangle(0, 0, 1920, 1080), 1, "A"),
        new(new Rectangle(0, 1080, 1920, 1080), 2, "B"),
    ];

    private static Layout Split()
    {
        var origin = new Point(-1920, 0);          // A flush left of the laptop
        Point[] shifts = [Point.Empty, new Point(3840, -1080)];  // B from (-1920,1080) to (1920,0): right of it
        return new Layout(Laptop, PeerPair, origin, null, shifts);
    }

    [Fact]
    public void Split_BothSidesCross_IntoTheRightScreen()
    {
        var l = Split();
        Assert.Equal(new Rectangle(1920, 0, 1920, 1080), l.PeerInMine()[1].Bounds);
        Assert.Equal(new Crossing(Dir.Left, new Point(1919, 500)), l.FindCrossing(new Point(0, 500), -1, 0));   // into A
        Assert.Equal(new Crossing(Dir.Right, new Point(0, 1580)), l.FindCrossing(new Point(1919, 500), 1, 0));  // into B
        Assert.Equal(2, l.CrossingSegments().Count());
    }

    [Fact]
    public void Split_Inverse_ComesBackFromEachScreen_ButNotAcrossItsOwnSeam()
    {
        var back = Split().Inverse();
        Assert.Equal(new Crossing(Dir.Right, new Point(0, 500)), back.FindCrossing(new Point(1919, 500), 1, 0));     // A's right edge
        Assert.Equal(new Crossing(Dir.Left, new Point(1919, 500)), back.FindCrossing(new Point(0, 1580), -1, 0));    // B's left edge
        Assert.Null(back.FindCrossing(new Point(500, 1079), 0, 1));    // A to B: Windows moves the cursor across
        Assert.True(back.NearCrossingEdge(new Point(1, 1580), 4));     // B's left edge, in its real pixels
        Assert.False(back.NearCrossingEdge(new Point(1, 500), 4));     // A's left edge leads nowhere
    }

    [Fact]
    public void Shifts_RoundTripByPanel()
    {
        Point[] shifts = [Point.Empty, new Point(5, -7)];
        string text = Layout.FormatShifts(PeerPair, shifts);
        Assert.Equal("B=5,-7", text);
        Assert.Equal(shifts, Layout.ParseShifts(PeerPair, text));
        // The same panels listed in another order keep their own shifts.
        Assert.Equal([new Point(5, -7), Point.Empty], Layout.ParseShifts([PeerPair[1], PeerPair[0]], text)!);
        Assert.Null(Layout.ParseShifts(PeerPair, ""));
    }

    [Fact]
    public void SnapRect_SitsFlush_AndNeverOverlaps()
    {
        Rectangle[] others = [new(0, 0, 1920, 1080), new(1920, 0, 1920, 1080)];
        var at = Layout.SnapRect(new Size(1920, 1080), others, others, new Point(1000, 900), 32);
        Assert.NotNull(at);
        var r = new Rectangle(at!.Value, new Size(1920, 1080));
        Assert.All(others, o => { var i = Rectangle.Intersect(o, r); Assert.True(i.Width <= 0 || i.Height <= 0); });
        Assert.Equal(1080, r.Top);   // dropped below the pair
    }

    [Fact]
    public void Carry_FollowsThePanelTheOtherPcStandsBeside()
    {
        // This PC: A left of B; the other PC stands right of B. B is then moved to the left of A in Windows.
        MonitorRect[] before = [new(new Rectangle(0, 0, 1920, 1080), 1, "A"), new(new Rectangle(1920, 0, 1920, 1080), 2, "B")];
        MonitorRect[] after = [new(new Rectangle(1920, 0, 1920, 1080), 1, "A"), new(new Rectangle(0, 0, 1920, 1080), 2, "B")];
        string from = Layout.SetupKey(before, "RS", Laptop), to = Layout.SetupKey(after, "RS", Laptop);
        var carried = Layout.CarryPlacement(new Point(3840, 0), from, to);
        // It stays beside B, now at the far left: B's right edge is A, so it snaps to the nearest free edge.
        Assert.NotNull(carried);
        var l = new Layout(after, Laptop, carried!.Value);
        Assert.NotEmpty(l.CrossingSegments());
        Assert.False(Layout.Overlaps(after, Laptop, carried.Value));
    }

    [Fact]
    public void Renumbered_IsTheSameDesk_AndThePlacementStays()
    {
        // 2026-10-17: the other PC restarted and its screen came back as number 1 instead of 11.
        MonitorRect[] mine = [new(new Rectangle(1920, 0, 3840, 2160), 3, "AOC-46"), new(new Rectangle(0, 1080, 1920, 1080), 2, "X"), new(new Rectangle(2599, 2160, 1920, 1200), 1, "LGD")];
        string before = Layout.SetupKey(mine, "MED", [new(new Rectangle(0, 0, 1920, 1200), 11, "CSW")]);
        string after = Layout.SetupKey(mine, "MED", [new(new Rectangle(0, 0, 1920, 1200), 1, "CSW")]);
        Assert.True(Layout.SameScreens(before, after));
        Assert.Equal(new Point(679, 2160), Layout.CarryPlacement(new Point(679, 2160), before, after));
        // And from the other PC's side, where this PC's screens are the peer.
        string theirsBefore = Layout.SetupKey([new(new Rectangle(0, 0, 1920, 1200), 11, "CSW")], "LU", mine);
        string theirsAfter = Layout.SetupKey([new(new Rectangle(0, 0, 1920, 1200), 1, "CSW")], "LU", mine);
        Assert.Equal(new Point(-679, -2160), Layout.CarryPlacement(new Point(-679, -2160), theirsBefore, theirsAfter));
    }
}
