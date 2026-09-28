using Labs626.UrTask.Macros.Steps;

namespace Labs626.UrTask.Tests.Steps;

public class ColorCheckTests
{
    private static readonly Rgb Green = new(139, 224, 58);
    private static readonly Rgb Grey = new(128, 128, 128);

    [Fact]
    public void Distance_is_euclidean_rgb()
        => Assert.Equal(5.0, new Rgb(0, 3, 4).DistanceTo(new Rgb(0, 0, 0)), 3);

    [Fact]
    public void A_colour_is_stored_as_exactly_r_g_b()
    {
        // The shape Ur OCR matches. A stray "hex" key here is the bug this pins.
        var opts = new System.Text.Json.JsonSerializerOptions { PropertyNamingPolicy = System.Text.Json.JsonNamingPolicy.CamelCase };
        Assert.Equal("{\"r\":139,\"g\":224,\"b\":58}", System.Text.Json.JsonSerializer.Serialize(Green, opts));
        Assert.Equal("{\"offsetX\":-2,\"offsetY\":-2,\"w\":5,\"h\":5}", System.Text.Json.JsonSerializer.Serialize(new CheckBox(), opts));
    }

    [Fact]
    public void Within_tolerance_matches()
    {
        var v = ColorMatcher.Evaluate(new Rgb(135, 220, 60), new ColorCheck(new CheckBox(), Green));
        Assert.True(v.Matched);
    }

    [Fact]
    public void Beyond_tolerance_does_not_match()
    {
        var v = ColorMatcher.Evaluate(Grey, new ColorCheck(new CheckBox(), Green));
        Assert.False(v.Matched);
        Assert.True(v.Distance > 15);
    }

    [Fact]
    public void Other_state_wins_when_closer_even_inside_tolerance()
    {
        // A wide tolerance alone would accept this; the other-state rule must not.
        var check = new ColorCheck(new CheckBox(), Expect: new Rgb(120, 140, 120), Other: Grey, Tolerance: 40);
        var v = ColorMatcher.Evaluate(new Rgb(126, 130, 126), check);
        Assert.False(v.Matched);
        Assert.NotNull(v.DistanceToOther);
    }

    [Fact]
    public void Shows_other_only_when_within_tolerance_of_other_and_nearer_it()
    {
        var check = new ColorCheck(new CheckBox(), Green, Other: Grey);
        Assert.True(ColorMatcher.ShowsOther(new Rgb(130, 128, 126), check));   // grey, 2.8 from Other
        Assert.False(ColorMatcher.ShowsOther(Green, check));                   // the expected state
        Assert.False(ColorMatcher.ShowsOther(new Rgb(20, 30, 90), check));     // neither state yet
        Assert.False(ColorMatcher.ShowsOther(Grey, new ColorCheck(new CheckBox(), Green))); // no Other set
    }

    [Theory]
    [InlineData(5, 5, true)]
    [InlineData(9, 9, true)]
    [InlineData(10, 5, false)]
    [InlineData(0, 5, false)]
    public void Box_size_limits(int w, int h, bool valid)
        => Assert.Equal(valid, new CheckBox(0, 0, w, h).IsValid);

    [Theory]
    [InlineData(139, 224, 58, "green #8BE03A")]
    [InlineData(128, 128, 128, "grey #808080")]
    [InlineData(230, 40, 70, "red #E62846")]
    [InlineData(20, 30, 90, "dark blue #141E5A")]
    [InlineData(250, 250, 250, "white #FAFAFA")]
    [InlineData(5, 5, 5, "black #050505")]
    public void Names_colours_for_reports(int r, int g, int b, string expected)
        => Assert.Equal(expected, ColorNamer.Describe(new Rgb(r, g, b)));
}
