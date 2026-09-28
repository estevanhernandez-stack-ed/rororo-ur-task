// tests/rororo-ur-task.Tests/Ipc/ClearAtMacroTests.cs
using System.Text.Json;
using Labs626.UrTask.Ipc;
using Labs626.UrTask.Macros;
using Labs626.UrTask.Macros.Steps;

namespace Labs626.UrTask.Tests.Ipc;

public class ClearAtMacroTests
{
    // The spec's example call ("The ClearAt bridge call"), plus a second point with no label.
    private const string SpecJson = """
        { "contractVersion": "1.0", "method": "ClearAt", "callerPluginId": "626labs.ur-ocr",
          "target": "123456789",
          "client": { "w": 800, "h": 599 },
          "points": [ { "x": 412, "y": 288, "label": "ore 1" }, { "x": 390, "y": 390 } ],
          "outline": { "w": 50, "h": 50, "minCount": 60, "whiteMin": 225 },
          "maxMsPerPoint": null }
        """;

    private static ClearAtRequest Spec() => JsonSerializer.Deserialize<ClearAtRequest>(SpecJson, BridgeContract.Json)!;

    /// <summary>A valid call with <paramref name="points"/> points on a grid, labelled "ore N".</summary>
    private static ClearAtRequest Valid(int points = 2, int? maxMs = null) => new(
        "1.0", "ClearAt", "626labs.ur-ocr", "123", new ClearAtClient(800, 599),
        Enumerable.Range(0, points).Select(i => new ClearAtPoint(100 + i % 8 * 60, 100 + i / 8 * 50, $"ore {i + 1}")).ToList(),
        new ClearAtOutline(50, 50, 60), maxMs);

    [Fact]
    public void The_spec_example_deserializes()
    {
        var r = Spec();
        Assert.Equal(("ClearAt", "123456789", 800, 599), (r.Method, r.Target, r.Client!.W, r.Client.H));
        Assert.Equal((412, 288, "ore 1"), (r.Points![0].X, r.Points[0].Y, r.Points[0].Label));
        Assert.Null(r.Points[1].Label);
        Assert.Equal((50, 50, 60, 225), (r.Outline!.W, r.Outline.H, r.Outline.MinCount, r.Outline.WhiteMin));
        Assert.Null(r.MaxMsPerPoint);
    }

    [Fact]
    public void A_missing_whiteMin_reads_as_225()
    {
        var json = SpecJson.Replace(", \"whiteMin\": 225", "");
        Assert.Equal(OutlineCheck.DefaultWhiteMin, JsonSerializer.Deserialize<ClearAtRequest>(json, BridgeContract.Json)!.Outline!.WhiteMin);
    }

    [Fact]
    public void Build_makes_one_reach_hold_per_point_in_order()
    {
        var m = ClearAtMacro.Build(Spec(), "clearat-pb1");

        Assert.Equal(("clearat-pb1", Macro.CoordSpaceClient, (int?)800, (int?)599), (m.Id, m.CoordSpace, m.RecordedClientW, m.RecordedClientH));
        Assert.True(m.HasSteps);
        Assert.Empty(m.Events);
        var holds = m.Steps!.Cast<HoldStep>().ToList();
        Assert.Equal(new[] { ("p1", "ore 1", 412, 288), ("p2", "point 2", 390, 390) }, holds.Select(h => (h.Id, h.Label!, h.X, h.Y)));
        Assert.All(holds, h =>
        {
            Assert.Equal((0, 1, (int?)null), (h.DelayMs, h.Button, h.MaxMs));
            Assert.Equal(new OutlineCheck(new CheckBox(-25, -25, 50, 50), 60, 225), h.Reach);
            Assert.Same(ClearAtMacro.PointCheck, h.Check);
        });
    }

    [Theory]
    [InlineData(1, "ClearAt (1 point)")]
    [InlineData(3, "ClearAt (3 points)")]
    public void Build_names_the_macro_by_its_point_count(int points, string name)
        => Assert.Equal(name, ClearAtMacro.Build(Valid(points), "clearat-x").Name);

    [Fact]
    public void Build_carries_maxMsPerPoint_to_every_hold()
        => Assert.All(ClearAtMacro.Build(Valid(3, maxMs: 4000), "clearat-x").Steps!.Cast<HoldStep>(), h => Assert.Equal(4000, h.MaxMs));

    [Fact]
    public void The_built_steps_pass_the_step_validator()
        => Assert.Null(StepValidator.Validate(ClearAtMacro.Build(Valid(64), "clearat-x").Steps!));

    [Fact]
    public void Validate_accepts_the_spec_example_and_64_points()
    {
        Assert.Null(ClearAtMacro.Validate(Spec()));
        Assert.Null(ClearAtMacro.Validate(Valid(64)));
    }

    [Theory]
    [InlineData("no-target", "ClearAt needs a target account.")]
    [InlineData("foreground-target", "ClearAt needs a decimal user id; got an invalid target.")]
    [InlineData("newline-target", "ClearAt needs a decimal user id; got an invalid target.")]
    [InlineData("zero-target", "ClearAt target '0' is not a decimal user id.")]
    [InlineData("21-digit-target", "ClearAt needs a decimal user id; got an invalid target.")]
    [InlineData("no-client", "ClearAt needs the client size the points were measured in.")]
    [InlineData("zero-client", "ClearAt needs the client size the points were measured in.")]
    [InlineData("no-points", "ClearAt takes 1 to 64 points; got 0.")]
    [InlineData("65-points", "ClearAt takes 1 to 64 points; got 65.")]
    [InlineData("null-point", "ClearAt point 2 is empty.")]
    [InlineData("point-outside", "ClearAt point 2 'ore 2' at 800,300 is outside the 800x599 client.")]
    [InlineData("box-outside", "ClearAt point 2 'ore 2' has an outline box outside the 800x599 client.")]
    [InlineData("no-outline", "ClearAt needs an outline box.")]
    [InlineData("empty-box", "ClearAt has an empty outline box.")]
    [InlineData("big-box", "ClearAt has an outline box larger than 120x120.")]
    [InlineData("minCount-0", "ClearAt has an outline minCount below 1.")]
    [InlineData("minCount-over-box", "ClearAt has an outline minCount larger than its box, so it can never pass.")]
    [InlineData("whiteMin-0", "ClearAt has an outline whiteMin outside 1 to 255.")]
    [InlineData("whiteMin-256", "ClearAt has an outline whiteMin outside 1 to 255.")]
    [InlineData("maxMs-0", "ClearAt has a maxMsPerPoint below 1.")]
    public void Validate_refuses_with_one_sentence(string @case, string sentence)
    {
        var ok = Valid();
        var p = ok.Points!;
        var r = @case switch
        {
            "no-target" => ok with { Target = null },
            "foreground-target" => ok with { Target = "foreground" },
            "newline-target" => ok with { Target = "123456789\r\n2026-09-28 fake line" },
            "zero-target" => ok with { Target = "0" },
            "21-digit-target" => ok with { Target = new string('9', 21) },
            "no-client" => ok with { Client = null },
            "zero-client" => ok with { Client = new ClearAtClient(0, 599) },
            "no-points" => ok with { Points = Array.Empty<ClearAtPoint>() },
            "65-points" => Valid(65),
            "null-point" => ok with { Points = new[] { p[0], null! } },
            "point-outside" => ok with { Points = new[] { p[0], new ClearAtPoint(800, 300, "ore 2") } },
            "box-outside" => ok with { Points = new[] { p[0], new ClearAtPoint(790, 300, "ore 2") } },
            "no-outline" => ok with { Outline = null },
            "empty-box" => ok with { Outline = new ClearAtOutline(0, 50, 1) },
            "big-box" => ok with { Outline = new ClearAtOutline(121, 50, 60) },
            "minCount-0" => ok with { Outline = new ClearAtOutline(50, 50, 0) },
            "minCount-over-box" => ok with { Outline = new ClearAtOutline(10, 10, 101) },
            "whiteMin-0" => ok with { Outline = new ClearAtOutline(50, 50, 60, 0) },
            "whiteMin-256" => ok with { Outline = new ClearAtOutline(50, 50, 60, 256) },
            "maxMs-0" => ok with { MaxMsPerPoint = 0 },
            _ => throw new ArgumentOutOfRangeException(nameof(@case)),
        };
        Assert.Equal(sentence, ClearAtMacro.Validate(r));
    }

    /// <summary>The label a one-point call's hold plays and logs under.</summary>
    private static string LabelOf(string? label)
        => ((HoldStep)ClearAtMacro.Build(Valid(1) with { Points = new[] { new ClearAtPoint(100, 100, label) } }, "clearat-x").Steps![0]).Label!;

    [Fact]
    public void A_label_cannot_forge_a_log_line()
    {
        var label = LabelOf("ore 1\r\n2026-09-28 fake line");
        Assert.Equal("ore 1  2026-09-28 fake line", label);
        Assert.DoesNotContain(label, char.IsControl);
    }

    [Fact]
    public void A_long_label_is_capped_at_40_chars()
        => Assert.Equal(new string('a', 40), LabelOf(new string('a', 200)));

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("\r\n\t ")]
    public void A_label_with_nothing_left_plays_as_point_N(string? label)
        => Assert.Equal("point 1", LabelOf(label));

    [Fact]
    public void A_refusal_names_the_point_by_its_sanitised_label()
        => Assert.Equal("ClearAt point 1 'ore  1' at 800,300 is outside the 800x599 client.",
            ClearAtMacro.Validate(Valid(1) with { Points = new[] { new ClearAtPoint(800, 300, "ore\r\n1") } }));
}
