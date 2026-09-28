using Labs626.UrTask.Macros;
using Labs626.UrTask.Macros.Steps;

namespace Labs626.UrTask.Tests.Steps;

public class AutoHotkeyStepExportTests
{
    private static Macro M(params MacroStep[] steps) => new(
        Macro.CurrentSchemaVersion, Guid.NewGuid().ToString(), "Mine Zone 8", "PerWindow", 1, "a", null, 1,
        Array.Empty<MacroEvent>(), CoordSpace: Macro.CoordSpaceClient, RecordedClientW: 800, RecordedClientH: 599,
        RecordedDisplayScale: 100, Steps: steps);

    private static readonly ColorCheck Green = new(new CheckBox(), new Rgb(139, 224, 58));

    [Fact]
    public void Points_export_as_clicks_with_their_delays()
    {
        var v1 = AutoHotkeyExporter.Export(M(new PointStep(1500, "p1", null, 472, 317)), AhkVersion.V1);
        Assert.Contains("CoordMode, Mouse, Client", v1);
        Assert.Contains("Sleep, 1500", v1);
        Assert.Contains("Click, 472, 317, Left", v1);

        var v2 = AutoHotkeyExporter.Export(M(new PointStep(1500, "p1", null, 472, 317)), AhkVersion.V2);
        Assert.Contains("Click \"472 317 Left\"", v2);
    }

    [Fact]
    public void Checks_and_first_match_export_as_comments()
    {
        var text = AutoHotkeyExporter.Export(M(
            new PointStep(0, "p2", "Teleport opener", 42, 398, Check: Green, CheckEnabled: true),
            new FirstMatchStep(0, "f1", "Best mine", new[] { new PointStep(0, "t8", "#8", 137, 390, Check: Green), new PointStep(0, "t7", "#7", 312, 390, Check: Green) })),
            AhkVersion.V1);
        Assert.Contains("; check: 'Teleport opener' expects #8BE03A here (Ur Task only)", text);
        Assert.Contains("; first match 'Best mine': Ur Task presses the first candidate whose colour matches; exported as '#8'", text);
        Assert.Contains("Click, 137, 390, Left", text);
    }

    [Fact]
    public void Keys_drags_and_camera_moves_export()
    {
        var text = AutoHotkeyExporter.Export(M(
            new KeyStep(0, 0x41, true), new KeyStep(3000, 0x41, false),
            new DragStep(0, 2, 400, 300, 0, 300, 400),
            new PointerMoveStep(0, 0, 200, 300)), AhkVersion.V1);
        Assert.Contains("Send, {vk41 down}", text);
        Assert.Contains("Sleep, 3000", text);
        Assert.Contains("MouseClickDrag, Right, 400, 300, 400, 600", text);
        Assert.Contains("DllCall(\"mouse_event\", \"UInt\", 1, \"Int\", 0, \"Int\", 200, \"UInt\", 0, \"UPtr\", 0)", text);
    }

    [Fact]
    public void V3_macros_export_exactly_as_before()
    {
        var v3 = M() with { Steps = null, Events = new[] { new MacroEvent(0, MacroEventKind.MouseDown, 0, 1, 2, 1, 0) } };
        Assert.Contains("Click, 1, 2, Left, , D", AutoHotkeyExporter.Export(v3, AhkVersion.V1));
    }

    [Fact]
    public void FirstMatch_with_no_candidates_does_not_throw_and_notes_it()
    {
        var text = AutoHotkeyExporter.Export(
            M(new FirstMatchStep(0, "f0", "Empty pick", Array.Empty<PointStep>())), AhkVersion.V1);
        Assert.Contains("; first match 'Empty pick' has no candidates", text);
    }

    [Fact]
    public void Labels_with_newlines_are_sanitised_in_comments()
    {
        var text = AutoHotkeyExporter.Export(M(
            new PointStep(0, "p1", "Bad\r\nLabel", 1, 2, Check: Green, CheckEnabled: true),
            new FirstMatchStep(0, "f1", "Multi\nLine", new[] { new PointStep(0, "t1", "Cand\nLabel", 3, 4, Check: Green) })),
            AhkVersion.V1);
        Assert.Contains("; check: 'Bad Label' expects #8BE03A here (Ur Task only)", text);
        Assert.Contains("; first match 'Multi Line': Ur Task presses the first candidate whose colour matches; exported as 'Cand Label'", text);
    }

    [Fact]
    public void A_hold_exports_as_a_button_down_a_sleep_and_an_up_with_a_note()
    {
        var hold = new HoldStep(300, "spot-N", "Spot N", 400, 244, Check: new HoldCheck(new CheckBox()));

        var v1 = AutoHotkeyExporter.Export(M(hold), AhkVersion.V1);
        Assert.Contains("Sleep, 300\r\n; hold: 'Spot N' stays down until its colour changes (Ur Task only); exported as a 1000 ms hold\r\n", v1);
        Assert.Contains("Click, 400, 244, Left, , D\r\nSleep, 1000\r\nClick, 400, 244, Left, , U", v1);

        var v2 = AutoHotkeyExporter.Export(M(hold with { MaxMs = 2500, Button = 2, Label = "Two\nLines" }), AhkVersion.V2);
        Assert.Contains("; hold: 'Two Lines' stays down until its colour changes or 2500 ms pass (Ur Task only); exported as a 2500 ms hold", v2);
        Assert.Contains("Click \"400 244 Right Down\"\r\nSleep 2500\r\nClick \"400 244 Right Up\"", v2);
    }
}
