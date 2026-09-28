using System.IO;
using System.Text.Json;
using Labs626.UrTask.Macros;
using Labs626.UrTask.Macros.Steps;

namespace Labs626.UrTask.Tests.Steps;

/// <summary>
/// The ore-stop example macros are written by generate.ps1 from measured.json. These tests read
/// the committed files the way Ur Task does and hold them to the measured values, so an edit to
/// measured.json without a regenerate fails here, and so does any press outside the named points.
/// </summary>
public class OreStopExampleMacrosTests
{
    private static readonly string[] RingNames = { "N", "NE", "E", "SE", "S", "SW", "W", "NW" };

    private static string ExampleDir()
    {
        var dir = new DirectoryInfo(AppContext.BaseDirectory);
        while (dir is not null && !File.Exists(Path.Combine(dir.FullName, "rororo-ur-task.csproj"))) dir = dir.Parent;
        Assert.True(dir is not null, "Could not find the repo root (walked up from the test binary looking for rororo-ur-task.csproj).");
        return Path.Combine(dir!.FullName, "docs", "reference", "events", "macros", "space-mine-ore-stop");
    }

    private static (IReadOnlyList<Macro> Macros, JsonElement Measured) Load()
    {
        var dir = ExampleDir();
        var macrosDir = Path.Combine(dir, "macros");
        Assert.True(Directory.Exists(macrosDir), $"No generated macros at {macrosDir}. Run generate.ps1.");
        var result = new MacroStore(macrosDir).LoadAll();
        Assert.Empty(result.Failures);
        using var doc = JsonDocument.Parse(File.ReadAllText(Path.Combine(dir, "measured.json")));
        return (result.Macros, doc.RootElement.Clone());
    }

    private static (int X, int Y) Xy(JsonElement e) => (e.GetProperty("x").GetInt32(), e.GetProperty("y").GetInt32());
    private static Rgb Colour(JsonElement e) => new(e.GetProperty("r").GetInt32(), e.GetProperty("g").GetInt32(), e.GetProperty("b").GetInt32());
    private static CheckBox BoxOf(JsonElement e) => new(e.GetProperty("offsetX").GetInt32(), e.GetProperty("offsetY").GetInt32(), e.GetProperty("w").GetInt32(), e.GetProperty("h").GetInt32());

    /// <summary>The pickaxe press, gated on the dot: skipped only when the dot shows the other
    /// state, so a screen covered by a popup or captcha stops the macro before it presses.</summary>
    private static void AssertDot(FirstMatchStep f, JsonElement am, bool expectGreen)
    {
        Assert.Equal(NoMatchAction.SkipIfOther, f.OnNoMatch);
        var c = Assert.Single(f.Candidates);
        Assert.Equal(Xy(am.GetProperty("pickaxe")), (c.X, c.Y));
        Assert.Equal(BoxOf(am.GetProperty("dotBox")), c.Check!.Box);
        var green = Colour(am.GetProperty("green"));
        var red = Colour(am.GetProperty("red"));
        Assert.Equal(expectGreen ? green : red, c.Check.Expect);
        Assert.Equal(expectGreen ? red : green, c.Check.Other);
        Assert.Equal(am.GetProperty("tolerance").GetInt32(), c.Check.Tolerance);
    }

    [Fact]
    public void Every_example_loads_validates_and_is_saved_as_its_id()
    {
        var (macros, m) = Load();
        var expected = new[] { "Auto Mine off (checked)", "Auto Mine on (checked)", "Camera top-down", "Go to Top" }
            .Concat(RingNames.Select(n => $"Mine spot {n}"))
            .OrderBy(s => s, StringComparer.Ordinal);
        Assert.Equal(expected, macros.Select(x => x.Name!).OrderBy(s => s, StringComparer.Ordinal));

        var client = m.GetProperty("client");
        foreach (var macro in macros)
        {
            Assert.Null(StepValidator.Validate(macro.Steps!));
            Assert.Equal(Macro.CurrentSchemaVersion, macro.SchemaVersion);
            Assert.True(macro.IsClientSpace);
            Assert.Empty(macro.Events);
            Assert.Equal((client.GetProperty("w").GetInt32(), client.GetProperty("h").GetInt32()), (macro.RecordedClientW!.Value, macro.RecordedClientH!.Value));
            Assert.Equal(client.GetProperty("displayScale").GetInt32(), macro.RecordedDisplayScale);
            Assert.True(File.Exists(Path.Combine(ExampleDir(), "macros", macro.Id + ".json")), $"'{macro.Name}' is not saved as <id>.json");
        }
    }

    [Fact]
    public void Each_mine_spot_turns_Auto_Mine_off_holds_its_ring_spot_and_turns_it_back_on()
    {
        var (macros, m) = Load();
        var ring = m.GetProperty("ring");
        var am = m.GetProperty("autoMine");
        foreach (var spot in ring.GetProperty("spots").EnumerateArray())
        {
            var n = spot.GetProperty("name").GetString();
            var macro = Assert.Single(macros, x => x.Name == $"Mine spot {n}");
            Assert.Equal(3, macro.Steps!.Count);
            AssertDot(Assert.IsType<FirstMatchStep>(macro.Steps[0]), am, expectGreen: true);
            var hold = Assert.IsType<HoldStep>(macro.Steps[1]);
            Assert.Equal(Xy(spot), (hold.X, hold.Y));
            Assert.Equal(1, hold.Button);
            Assert.Null(hold.MaxMs); // ore is never abandoned for taking long (spec, decision 5)
            Assert.Equal(BoxOf(ring.GetProperty("box")), hold.Check!.Box);
            Assert.Equal(ring.GetProperty("tolerance").GetInt32(), hold.Check.Tolerance);
            Assert.Equal(ring.GetProperty("holdDelayMs").GetInt32(), hold.DelayMs);
            AssertDot(Assert.IsType<FirstMatchStep>(macro.Steps[2]), am, expectGreen: false);
        }
    }

    [Fact]
    public void The_checked_Auto_Mine_toggles_press_only_on_the_right_dot()
    {
        var (macros, m) = Load();
        var am = m.GetProperty("autoMine");
        AssertDot(Assert.IsType<FirstMatchStep>(Assert.Single(Assert.Single(macros, x => x.Name == "Auto Mine off (checked)").Steps!)), am, expectGreen: true);
        AssertDot(Assert.IsType<FirstMatchStep>(Assert.Single(Assert.Single(macros, x => x.Name == "Auto Mine on (checked)").Steps!)), am, expectGreen: false);
    }

    [Fact]
    public void Camera_top_down_drags_past_the_pitch_limit_then_counts_back()
    {
        var (macros, m) = Load();
        var cam = m.GetProperty("camera");
        var steps = Assert.Single(macros, x => x.Name == "Camera top-down").Steps!;
        var start = Xy(cam.GetProperty("start"));
        // The 0.9.0 camera margin, on the vertical component only.
        var dy = (int)Math.Ceiling(cam.GetProperty("pitchTravelPx").GetInt32() * StepConverter.CameraDragMargin);

        var down = Assert.IsType<DragStep>(steps[0]);
        Assert.Equal((2, start.X, start.Y, 0, dy), (down.Button, down.StartX, down.StartY, down.Dx, down.Dy));
        Assert.True(down.StartY + down.Dy < m.GetProperty("client").GetProperty("h").GetInt32(), "The drag leaves the client area.");

        var countBack = cam.GetProperty("countBackPx").GetInt32();
        if (countBack == 0) { Assert.Single(steps); return; }
        var back = Assert.IsType<DragStep>(steps[1]);
        Assert.Equal((2, start.X, start.Y + dy, 0, -countBack), (back.Button, back.StartX, back.StartY, back.Dx, back.Dy));
    }

    [Fact]
    public void Go_to_Top_presses_the_button_waits_then_turns_Auto_Mine_on()
    {
        var (macros, m) = Load();
        var g = m.GetProperty("goToTop");
        var steps = Assert.Single(macros, x => x.Name == "Go to Top").Steps!;
        Assert.Equal(3, steps.Count);
        var press = Assert.IsType<PointStep>(steps[0]);
        Assert.Equal(Xy(g), (press.X, press.Y));
        var check = g.GetProperty("check");
        Assert.Equal(check.ValueKind != JsonValueKind.Null, press.CheckEnabled);
        if (check.ValueKind != JsonValueKind.Null)
        {
            // Ur OCR fills this in once the sweep measures it; an edit to measured.json without a
            // regenerate must fail here, same as every other measured value in this file.
            Assert.Equal(BoxOf(check.GetProperty("box")), press.Check!.Box);
            Assert.Equal(Colour(check.GetProperty("expect")), press.Check.Expect);
            Assert.Equal(check.GetProperty("tolerance").GetInt32(), press.Check.Tolerance);
            Assert.Null(press.Check.Other); // generate.ps1 never writes an "other" for Go to Top
        }
        Assert.Equal(g.GetProperty("settleMs").GetInt32(), Assert.IsType<WaitStep>(steps[1]).DelayMs);
        AssertDot(Assert.IsType<FirstMatchStep>(steps[2]), m.GetProperty("autoMine"), expectGreen: false);
    }

    [Fact]
    public void Nothing_presses_outside_the_named_points()
    {
        // Never a captcha, the Enchant Machine or an invite popup (ore-stop spec). Every press lands
        // on the pickaxe, Go to Top or a ring spot; the only other input is the right-button drag.
        var (macros, m) = Load();
        var allowed = new HashSet<(int, int)> { Xy(m.GetProperty("autoMine").GetProperty("pickaxe")), Xy(m.GetProperty("goToTop")) };
        foreach (var s in m.GetProperty("ring").GetProperty("spots").EnumerateArray()) allowed.Add(Xy(s));

        foreach (var macro in macros)
        foreach (var step in macro.Steps!)
        {
            switch (step)
            {
                case PointStep p: Assert.Contains((p.X, p.Y), allowed); break;
                case HoldStep h: Assert.Contains((h.X, h.Y), allowed); break;
                case FirstMatchStep f: Assert.All(f.Candidates, c => Assert.Contains((c.X, c.Y), allowed)); break;
                case DragStep d: Assert.Equal(2, d.Button); break;
                case WaitStep: break;
                default: Assert.Fail($"'{macro.Name}' has a {step.GetType().Name}; the examples use only points, holds, first matches, right drags and waits."); break;
            }
        }
    }
}
