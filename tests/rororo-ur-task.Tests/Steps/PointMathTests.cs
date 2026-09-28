using Labs626.UrTask.Macros.Steps;

namespace Labs626.UrTask.Tests.Steps;

public class PointMathTests
{
    [Fact]
    public void Same_scale_keeps_the_recorded_size()
        => Assert.Equal((800, 599), PointMath.TargetClientSize((800, 599), 100, 100));

    [Fact]
    public void Unknown_recorded_scale_keeps_the_recorded_size()
        => Assert.Equal((800, 599), PointMath.TargetClientSize((800, 599), null, 125));

    [Fact]
    public void Scale_change_scales_the_target()
        => Assert.Equal((1000, 749), PointMath.TargetClientSize((800, 599), 100, 125));

    [Fact]
    public void Points_scale_with_the_actual_client()
        => Assert.Equal((500, 375), PointMath.Place((400, 300), (800, 599), (1000, 749)));

    [Fact]
    public void Radius_scales_like_a_point()
        => Assert.Equal(30, PointMath.ScaledRadius((800, 599), (1000, 749)));

    [Fact]
    public void Search_offsets_are_nearest_first_and_inside_the_radius()
    {
        var offs = PointMath.SearchOffsets(24);
        Assert.DoesNotContain((0, 0), offs);
        Assert.All(offs, o => Assert.True(o.Dx * o.Dx + o.Dy * o.Dy <= 24 * 24));
        var d = offs.Select(o => o.Dx * o.Dx + o.Dy * o.Dy).ToList();
        Assert.Equal(d.OrderBy(x => x), d);
        Assert.Equal(4, Math.Abs(offs[0].Dx) + Math.Abs(offs[0].Dy)); // first ring is 4 px away
    }

    [Fact]
    public void Box_rect_applies_the_offset()
        => Assert.Equal((98, 198, 5, 5), PointMath.BoxRect((100, 200), new CheckBox()));

    [Theory]
    [InlineData(0, 0, 5, 5, true)]
    [InlineData(-1, 0, 5, 5, false)]
    [InlineData(796, 594, 5, 5, false)]
    [InlineData(795, 594, 5, 5, true)]
    public void Inside_client_is_strict(int x, int y, int w, int h, bool inside)
        => Assert.Equal(inside, PointMath.InsideClient((x, y, w, h), (800, 599)));

    [Theory]
    [InlineData(100, 816, 638)]
    [InlineData(125, 1020, 797)]
    [InlineData(150, 1224, 957)]
    public void Roblox_minimum_window_scales(int scale, int w, int h)
        => Assert.Equal((w, h), PointMath.RobloxMinOuter(scale));
}
