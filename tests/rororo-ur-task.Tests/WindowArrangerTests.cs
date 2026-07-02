using Labs626.UrTask.PluginHost;

namespace Labs626.UrTask.Tests;

public class WindowArrangerTests
{
    [Fact]
    public void Stack_ReturnsAnchorRect_TimesCount()
    {
        var anchor = new RectPx(100, 50, 816, 638);
        var rects = WindowArranger.ComputeStack(anchor, 3);
        Assert.Equal(3, rects.Count);
        Assert.All(rects, r => Assert.Equal(anchor, r));
    }

    [Theory]
    [InlineData(1, 1, 1)]
    [InlineData(2, 2, 1)]
    [InlineData(3, 2, 2)]
    [InlineData(4, 2, 2)]
    [InlineData(5, 3, 2)]
    [InlineData(9, 3, 3)]
    public void Grid_ColsRows_FollowCeilSqrt(int count, int expectedCols, int expectedRows)
    {
        var layout = WindowArranger.ComputeGrid(new RectPx(0, 0, 3000, 2000), count, minW: 100, minH: 100);
        Assert.Equal(count, layout.Rects.Count);
        Assert.False(layout.Overlapping);
        var cols = layout.Rects.Select(r => r.X).Distinct().Count();
        var rows = layout.Rects.Select(r => r.Y).Distinct().Count();
        Assert.Equal(expectedCols, cols);
        Assert.Equal(expectedRows, rows);
    }

    [Fact]
    public void Grid_FourWindows_TilesQuadrants()
    {
        var layout = WindowArranger.ComputeGrid(new RectPx(0, 0, 2000, 1200), 4, 100, 100);
        Assert.Equal(new RectPx(0, 0, 1000, 600), layout.Rects[0]);
        Assert.Equal(new RectPx(1000, 0, 1000, 600), layout.Rects[1]);   // row-major
        Assert.Equal(new RectPx(0, 600, 1000, 600), layout.Rects[2]);
        Assert.Equal(new RectPx(1000, 600, 1000, 600), layout.Rects[3]);
    }

    [Fact]
    public void Grid_RespectsWorkAreaOrigin()
    {
        var layout = WindowArranger.ComputeGrid(new RectPx(50, 40, 2000, 1200), 1, 100, 100);
        var r = Assert.Single(layout.Rects);
        Assert.Equal(new RectPx(50, 40, 2000, 1200), r);
    }

    [Fact]
    public void Grid_CellsBelowMinimum_ClampAndOverlap()
    {
        // 4 windows in 1000x600 with 700x500 minimum: cells clamp to min and
        // strides shrink so all windows stay inside the work area (overlapping).
        var layout = WindowArranger.ComputeGrid(new RectPx(0, 0, 1000, 600), 4, minW: 700, minH: 500);
        Assert.True(layout.Overlapping);
        Assert.All(layout.Rects, r => { Assert.Equal(700, r.W); Assert.Equal(500, r.H); });
        Assert.All(layout.Rects, r =>
        {
            Assert.InRange(r.X, 0, 300);  // 1000 - 700
            Assert.InRange(r.Y, 0, 100);  // 600 - 500
        });
        Assert.Equal(4, layout.Rects.Distinct().Count()); // cascaded, not identical
    }
}
