using System;
using System.Collections.Generic;
using Labs626.UrTask.Macros;

namespace Labs626.UrTask.Tests;

public class AutoHotkeyExporterTests
{
    private static Macro MakeMacro(
        string name,
        IReadOnlyList<MacroEvent> events,
        string? coordSpace = null,
        int? clientW = null,
        int? clientH = null) => new(
        SchemaVersion: Macro.CurrentSchemaVersion,
        Id: Guid.NewGuid().ToString(),
        Name: name,
        RecordMode: "PerWindow",
        RecordedAgainstUserId: 42,
        RecordedAgainstDisplayName: "Goldnail8",
        InterAltDelayMs: null,
        RecordedAtUnixMs: 1750000000000,
        Events: events,
        CoordSpace: coordSpace ?? Macro.CoordSpaceScreen,
        RecordedClientW: clientW,
        RecordedClientH: clientH);

    private static MacroEvent Key(long ts, MacroEventKind kind, int vk) =>
        new(TimestampMs: ts, Kind: kind, VirtualKeyCode: vk, X: 0, Y: 0, MouseButton: 0, WheelDelta: 0);

    private static MacroEvent Mouse(long ts, MacroEventKind kind, int x, int y, int button = 0, int wheelDelta = 0) =>
        new(TimestampMs: ts, Kind: kind, VirtualKeyCode: 0, X: x, Y: y, MouseButton: button, WheelDelta: wheelDelta);

    [Fact]
    public void Export_KeyboardMacro_V1_UsesVkNotationAndCommaSyntax()
    {
        var macro = MakeMacro("space tap", new List<MacroEvent>
        {
            Key(0, MacroEventKind.KeyDown, 0x20),
            Key(500, MacroEventKind.KeyUp, 0x20),
        });

        var output = AutoHotkeyExporter.Export(macro, AhkVersion.V1);

        Assert.Contains("Send, {vk20 down}", output);
        Assert.Contains("Sleep,", output);
        Assert.Contains("Send, {vk20 up}", output);
    }

    [Fact]
    public void Export_KeyboardMacro_V2_UsesQuotedSendSyntax()
    {
        var macro = MakeMacro("space tap", new List<MacroEvent>
        {
            Key(0, MacroEventKind.KeyDown, 0x20),
            Key(500, MacroEventKind.KeyUp, 0x20),
        });

        var output = AutoHotkeyExporter.Export(macro, AhkVersion.V2);

        Assert.Contains("Send \"{vk20 down}\"", output);
        Assert.Contains("Send \"{vk20 up}\"", output);
    }

    [Fact]
    public void Export_EventsSpaced500ms_EmitsMatchingSleep()
    {
        var macro = MakeMacro("timing", new List<MacroEvent>
        {
            Key(1000, MacroEventKind.KeyDown, 0x41),
            Key(1500, MacroEventKind.KeyUp, 0x41),
        });

        var v1 = AutoHotkeyExporter.Export(macro, AhkVersion.V1);
        var v2 = AutoHotkeyExporter.Export(macro, AhkVersion.V2);

        Assert.Contains("Sleep, 500", v1);
        Assert.Contains("Sleep 500", v2);
    }

    [Fact]
    public void Export_ZeroDeltaBetweenEvents_EmitsNoSleep()
    {
        var macro = MakeMacro("simultaneous", new List<MacroEvent>
        {
            Key(1000, MacroEventKind.KeyDown, 0x41),
            Key(1000, MacroEventKind.KeyDown, 0x42),
        });

        var output = AutoHotkeyExporter.Export(macro, AhkVersion.V1);

        // The header's prose ("...preserved via Sleep calls.") legitimately contains the
        // word "Sleep" — assert on the actual emitted directive syntax ("Sleep, <ms>") instead.
        Assert.DoesNotContain("Sleep,", output);
    }

    [Fact]
    public void Export_ClientSpaceMouseMacro_HeaderMentionsRecordedClientSize()
    {
        var macro = MakeMacro("client click", new List<MacroEvent>
        {
            Mouse(0, MacroEventKind.MouseDown, 100, 200, button: 1),
            Mouse(50, MacroEventKind.MouseUp, 100, 200, button: 1),
        }, coordSpace: Macro.CoordSpaceClient, clientW: 816, clientH: 638);

        var output = AutoHotkeyExporter.Export(macro, AhkVersion.V1);

        Assert.Contains("816x638", output);
    }

    [Fact]
    public void Export_ClientSpaceMouseMacro_V1_SetsCoordModeClient()
    {
        var macro = MakeMacro("client click", new List<MacroEvent>
        {
            Mouse(0, MacroEventKind.MouseDown, 100, 200, button: 1),
            Mouse(50, MacroEventKind.MouseUp, 100, 200, button: 1),
        }, coordSpace: Macro.CoordSpaceClient, clientW: 816, clientH: 638);

        var output = AutoHotkeyExporter.Export(macro, AhkVersion.V1);

        Assert.Contains("CoordMode, Mouse, Client", output);
    }

    [Fact]
    public void Export_ClientSpaceMouseMacro_V2_SetsCoordModeClient()
    {
        var macro = MakeMacro("client click", new List<MacroEvent>
        {
            Mouse(0, MacroEventKind.MouseDown, 100, 200, button: 1),
            Mouse(50, MacroEventKind.MouseUp, 100, 200, button: 1),
        }, coordSpace: Macro.CoordSpaceClient, clientW: 816, clientH: 638);

        var output = AutoHotkeyExporter.Export(macro, AhkVersion.V2);

        Assert.Contains("CoordMode \"Mouse\", \"Client\"", output);
    }

    [Fact]
    public void Export_ScreenSpaceMouseMacro_SetsCoordModeScreen()
    {
        var macro = MakeMacro("screen click", new List<MacroEvent>
        {
            Mouse(0, MacroEventKind.MouseDown, 100, 200, button: 1),
            Mouse(50, MacroEventKind.MouseUp, 100, 200, button: 1),
        }, coordSpace: Macro.CoordSpaceScreen);

        var v1 = AutoHotkeyExporter.Export(macro, AhkVersion.V1);
        var v2 = AutoHotkeyExporter.Export(macro, AhkVersion.V2);

        Assert.Contains("CoordMode, Mouse, Screen", v1);
        Assert.Contains("CoordMode \"Mouse\", \"Screen\"", v2);
    }

    [Fact]
    public void Export_MouseDownUpPair_V1_EmitsClickDownAndUpWithMappedButton()
    {
        // MouseButton encoding (verified against MacroRecorder/MacroPlayer): 1=Left 2=Right 3=Middle.
        var macro = MakeMacro("right click", new List<MacroEvent>
        {
            Mouse(0, MacroEventKind.MouseDown, 10, 20, button: 2),
            Mouse(100, MacroEventKind.MouseUp, 10, 20, button: 2),
        });

        var output = AutoHotkeyExporter.Export(macro, AhkVersion.V1);

        Assert.Contains("Click, 10, 20, Right, , D", output);
        Assert.Contains("Click, 10, 20, Right, , U", output);
    }

    [Fact]
    public void Export_MouseDownUpPair_V2_EmitsClickDownAndUpWithMappedButton()
    {
        var macro = MakeMacro("middle click", new List<MacroEvent>
        {
            Mouse(0, MacroEventKind.MouseDown, 10, 20, button: 3),
            Mouse(100, MacroEventKind.MouseUp, 10, 20, button: 3),
        });

        var output = AutoHotkeyExporter.Export(macro, AhkVersion.V2);

        Assert.Contains("Click \"10 20 Middle Down\"", output);
        Assert.Contains("Click \"10 20 Middle Up\"", output);
    }

    [Fact]
    public void Export_MouseMove_EmitsMouseMoveInBothVersions()
    {
        var macro = MakeMacro("move", new List<MacroEvent>
        {
            Mouse(0, MacroEventKind.MouseMove, 5, 6),
        });

        var v1 = AutoHotkeyExporter.Export(macro, AhkVersion.V1);
        var v2 = AutoHotkeyExporter.Export(macro, AhkVersion.V2);

        Assert.Contains("MouseMove, 5, 6", v1);
        Assert.Contains("MouseMove 5, 6", v2);
    }

    [Fact]
    public void Export_MouseWheel_ComputesNotchesAndDirection()
    {
        var macro = MakeMacro("scroll", new List<MacroEvent>
        {
            Mouse(0, MacroEventKind.MouseWheel, 0, 0, wheelDelta: 240),
            Mouse(50, MacroEventKind.MouseWheel, 0, 0, wheelDelta: -120),
        });

        var v1 = AutoHotkeyExporter.Export(macro, AhkVersion.V1);
        var v2 = AutoHotkeyExporter.Export(macro, AhkVersion.V2);

        Assert.Contains("Click, WheelUp, 2", v1);
        Assert.Contains("Click, WheelDown, 1", v1);
        Assert.Contains("Click \"WheelUp 2\"", v2);
        Assert.Contains("Click \"WheelDown 1\"", v2);
    }

    [Fact]
    public void Export_Header_NamesMacroAndActiveWindowOnlyCaveat()
    {
        var macro = MakeMacro("my routine", new List<MacroEvent>
        {
            Key(0, MacroEventKind.KeyDown, 0x41),
        });

        var output = AutoHotkeyExporter.Export(macro, AhkVersion.V1);

        Assert.Contains("my routine", output);
        Assert.Contains("ACTIVE window only", output);
    }

    [Fact]
    public void Export_V1_UsesNoEnvAndCommaDirectives()
    {
        var macro = MakeMacro("directives", new List<MacroEvent> { Key(0, MacroEventKind.KeyDown, 0x41) });

        var output = AutoHotkeyExporter.Export(macro, AhkVersion.V1);

        Assert.Contains("#NoEnv", output);
        Assert.Contains("SendMode Input", output);
        Assert.Contains("SetKeyDelay, -1, -1", output);
        Assert.Contains("SetMouseDelay, -1", output);
    }

    [Fact]
    public void Export_V2_UsesQuotedDirectivesAndNoNoEnv()
    {
        var macro = MakeMacro("directives", new List<MacroEvent> { Key(0, MacroEventKind.KeyDown, 0x41) });

        var output = AutoHotkeyExporter.Export(macro, AhkVersion.V2);

        Assert.DoesNotContain("#NoEnv", output);
        Assert.Contains("SendMode \"Event\"", output);
        Assert.Contains("SetKeyDelay -1, -1", output);
        Assert.Contains("SetMouseDelay -1", output);
    }
}
