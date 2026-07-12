using System.Globalization;
using System.Text;

namespace Labs626.UrTask.Macros;

/// <summary>Which AutoHotkey syntax generation to emit.</summary>
public enum AhkVersion
{
    V1,
    V2,
}

/// <summary>
/// Exports a single <see cref="Macro"/> to a standalone AutoHotkey (v1 or v2)
/// script. Best-effort port: it replays keyboard + mouse events with the same
/// relative timing as the original recording, but loses everything Ur Task's
/// SequencePlayer/AssignmentRunner provide on top of a raw macro — per-window
/// targeting, per-account round-robin, and skip-on-failure. Pure string
/// builder: no Win32, no file IO, so it's fully unit-testable without a
/// window handle.
/// </summary>
public static class AutoHotkeyExporter
{
    private const string Nl = "\r\n";

    public static string Export(Macro macro, AhkVersion version)
    {
        if (macro is null) throw new ArgumentNullException(nameof(macro));

        var sb = new StringBuilder();
        AppendHeader(sb, macro);
        AppendDirectives(sb, macro, version);
        AppendEvents(sb, macro, version);
        return sb.ToString();
    }

    // ---------- Header ----------

    private static void AppendHeader(StringBuilder sb, Macro macro)
    {
        var name = (macro.Name ?? "(unnamed)").Replace("\r", "").Replace("\n", " ");
        sb.Append($"; Exported from RoRoRo Ur Task — macro \"{name}\"").Append(Nl);
        sb.Append("; Best-effort port — original event timing is preserved via Sleep calls.").Append(Nl);
        sb.Append("; Caveats: plays on the ACTIVE window only (no per-window targeting); can't").Append(Nl);
        sb.Append("; reproduce Ur Task's per-account round-robin or skip-on-failure behavior.").Append(Nl);

        if (macro.IsClientSpace)
        {
            sb.Append("; This macro's mouse coords are relative to the recorded window's CLIENT").Append(Nl);
            sb.Append(FormattableString.Invariant(
                    $"; area — size your target window's client area to {macro.RecordedClientW}x{macro.RecordedClientH}"))
                .Append(Nl);
            sb.Append("; (the size it was recorded at) for clicks to land.").Append(Nl);
        }

        sb.Append(Nl);
    }

    // ---------- Directives ----------

    private static void AppendDirectives(StringBuilder sb, Macro macro, AhkVersion version)
    {
        var hasMouseEvent = macro.Events.Any(e => e.Kind is MacroEventKind.MouseMove
            or MacroEventKind.MouseDown or MacroEventKind.MouseUp or MacroEventKind.MouseWheel);
        var useClientCoords = hasMouseEvent && macro.IsClientSpace;

        if (version == AhkVersion.V1)
        {
            sb.Append("#NoEnv").Append(Nl);
            sb.Append("SendMode Input").Append(Nl);
            sb.Append("SetKeyDelay, -1, -1").Append(Nl);
            sb.Append("SetMouseDelay, -1").Append(Nl);
            sb.Append(useClientCoords ? "CoordMode, Mouse, Client" : "CoordMode, Mouse, Screen").Append(Nl);
        }
        else
        {
            sb.Append("SendMode \"Event\"").Append(Nl);
            sb.Append("SetKeyDelay -1, -1").Append(Nl);
            sb.Append("SetMouseDelay -1").Append(Nl);
            sb.Append(useClientCoords ? "CoordMode \"Mouse\", \"Client\"" : "CoordMode \"Mouse\", \"Screen\"").Append(Nl);
        }

        sb.Append(Nl);
    }

    // ---------- Events ----------

    private static void AppendEvents(StringBuilder sb, Macro macro, AhkVersion version)
    {
        var events = macro.Events;
        for (var i = 0; i < events.Count; i++)
        {
            if (i > 0)
            {
                var deltaMs = events[i].TimestampMs - events[i - 1].TimestampMs;
                if (deltaMs > 0)
                {
                    sb.Append(version == AhkVersion.V1
                            ? FormattableString.Invariant($"Sleep, {deltaMs}")
                            : FormattableString.Invariant($"Sleep {deltaMs}"))
                        .Append(Nl);
                }
            }

            AppendEvent(sb, events[i], version);
        }
    }

    private static void AppendEvent(StringBuilder sb, MacroEvent evt, AhkVersion version)
    {
        switch (evt.Kind)
        {
            case MacroEventKind.KeyDown:
                sb.Append(FormatKey(evt.VirtualKeyCode, down: true, version)).Append(Nl);
                break;
            case MacroEventKind.KeyUp:
                sb.Append(FormatKey(evt.VirtualKeyCode, down: false, version)).Append(Nl);
                break;
            case MacroEventKind.MouseMove:
                sb.Append(version == AhkVersion.V1
                        ? FormattableString.Invariant($"MouseMove, {evt.X}, {evt.Y}")
                        : FormattableString.Invariant($"MouseMove {evt.X}, {evt.Y}"))
                    .Append(Nl);
                break;
            case MacroEventKind.MouseDown:
                sb.Append(FormatClick(evt, isDown: true, version)).Append(Nl);
                break;
            case MacroEventKind.MouseUp:
                sb.Append(FormatClick(evt, isDown: false, version)).Append(Nl);
                break;
            case MacroEventKind.MouseWheel:
                sb.Append(FormatWheel(evt, version)).Append(Nl);
                break;
        }
    }

    private static string FormatKey(int vkCode, bool down, AhkVersion version)
    {
        var hex = vkCode.ToString("x2", CultureInfo.InvariantCulture);
        var state = down ? "down" : "up";
        return version == AhkVersion.V1
            ? $"Send, {{vk{hex} {state}}}"
            : $"Send \"{{vk{hex} {state}}}\"";
    }

    private static string FormatClick(MacroEvent evt, bool isDown, AhkVersion version)
    {
        var btn = ButtonName(evt.MouseButton);
        if (version == AhkVersion.V1)
        {
            var updown = isDown ? "D" : "U";
            return FormattableString.Invariant($"Click, {evt.X}, {evt.Y}, {btn}, , {updown}");
        }

        var downUp = isDown ? "Down" : "Up";
        return FormattableString.Invariant($"Click \"{evt.X} {evt.Y} {btn} {downUp}\"");
    }

    private static string FormatWheel(MacroEvent evt, AhkVersion version)
    {
        var notches = Math.Max(1, Math.Abs(evt.WheelDelta) / 120);
        var dir = evt.WheelDelta > 0 ? "WheelUp" : "WheelDown";
        return version == AhkVersion.V1
            ? FormattableString.Invariant($"Click, {dir}, {notches}")
            : FormattableString.Invariant($"Click \"{dir} {notches}\"");
    }

    /// <summary>Maps <see cref="MacroEvent.MouseButton"/>'s encoding (1=L 2=R 3=M 4=X1 5=X2,
    /// per MacroRecorder/MacroPlayer) to AutoHotkey's Click button names.</summary>
    private static string ButtonName(int mouseButton) => mouseButton switch
    {
        1 => "Left",
        2 => "Right",
        3 => "Middle",
        4 => "X1",
        5 => "X2",
        _ => "Left",
    };
}
