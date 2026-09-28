using System.Globalization;
using System.Text;
using Labs626.UrTask.Macros.Steps;

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

    /// <summary>How long an open hold (no maxMs) is held in an export. AutoHotkey cannot watch the
    /// colour, so the script holds for a fixed time and the comment says so.</summary>
    private const int HoldExportMs = 1000;

    public static string Export(Macro macro, AhkVersion version)
    {
        if (macro is null) throw new ArgumentNullException(nameof(macro));

        var sb = new StringBuilder();
        AppendHeader(sb, macro);
        AppendDirectives(sb, macro, version);
        if (macro.HasSteps) AppendSteps(sb, macro, version);
        else AppendEvents(sb, macro, version);
        return sb.ToString();
    }

    // ---------- Header ----------

    private static void AppendHeader(StringBuilder sb, Macro macro)
    {
        var name = SanitizeComment(macro.Name ?? "(unnamed)");
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

        if (macro.HasSteps)
            sb.Append("; Exported from point steps. Colour checks and first-match choices run in Ur Task only and appear as comments.").Append(Nl);

        sb.Append(Nl);
    }

    // ---------- Directives ----------

    private static void AppendDirectives(StringBuilder sb, Macro macro, AhkVersion version)
    {
        var hasMouseEvent = macro.HasSteps || macro.Events.Any(e => e.Kind is MacroEventKind.MouseMove
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

    // ---------- Steps (v4) ----------

    private static void AppendSteps(StringBuilder sb, Macro macro, AhkVersion version)
    {
        foreach (var step in macro.Steps!)
        {
            if (step.DelayMs > 0) sb.Append(Sleep(step.DelayMs, version)).Append(Nl);
            switch (step)
            {
                case KeyStep k:
                    sb.Append(FormatKey(k.VirtualKeyCode, k.Down, version)).Append(Nl);
                    break;
                case PointStep p:
                    if (p.CheckEnabled && p.Check is { } c)
                        sb.Append($"; check: '{SanitizeComment(p.Label ?? p.Id)}' expects {c.Expect.Hex} here (Ur Task only)").Append(Nl);
                    sb.Append(ClickAt(p.X, p.Y, p.Button, version)).Append(Nl);
                    break;
                case FirstMatchStep f:
                    if (f.Candidates is not { Count: > 0 })
                    {
                        sb.Append($"; first match '{SanitizeComment(f.Label ?? f.Id)}' has no candidates — nothing exported here").Append(Nl);
                        break;
                    }
                    var first = f.Candidates[0];
                    sb.Append($"; first match '{SanitizeComment(f.Label ?? f.Id)}': Ur Task presses the first candidate whose colour matches; exported as '{SanitizeComment(first.Label ?? first.Id)}'").Append(Nl);
                    sb.Append(ClickAt(first.X, first.Y, first.Button, version)).Append(Nl);
                    break;
                case HoldStep h:
                {
                    var holdMs = h.MaxMs ?? HoldExportMs;
                    var holdLabel = SanitizeComment(h.Label ?? h.Id);
                    sb.Append(h.MaxMs is null
                        ? FormattableString.Invariant($"; hold: '{holdLabel}' stays down until its colour changes (Ur Task only); exported as a {holdMs} ms hold")
                        : FormattableString.Invariant($"; hold: '{holdLabel}' stays down until its colour changes or {holdMs} ms pass (Ur Task only); exported as a {holdMs} ms hold")).Append(Nl);
                    sb.Append(FormatClick(new MacroEvent(0, MacroEventKind.MouseDown, 0, h.X, h.Y, h.Button, 0), isDown: true, version)).Append(Nl);
                    sb.Append(Sleep(holdMs, version)).Append(Nl);
                    sb.Append(FormatClick(new MacroEvent(0, MacroEventKind.MouseUp, 0, h.X, h.Y, h.Button, 0), isDown: false, version)).Append(Nl);
                    break;
                }
                case DragStep d:
                    var btn = ButtonName(d.Button);
                    sb.Append(version == AhkVersion.V1
                        ? FormattableString.Invariant($"MouseClickDrag, {btn}, {d.StartX}, {d.StartY}, {d.StartX + d.Dx}, {d.StartY + d.Dy}")
                        : FormattableString.Invariant($"MouseClickDrag \"{btn}\", {d.StartX}, {d.StartY}, {d.StartX + d.Dx}, {d.StartY + d.Dy}")).Append(Nl);
                    break;
                case WheelStep w:
                    sb.Append(version == AhkVersion.V1
                        ? FormattableString.Invariant($"MouseMove, {w.X}, {w.Y}")
                        : FormattableString.Invariant($"MouseMove {w.X}, {w.Y}")).Append(Nl);
                    sb.Append(FormatWheel(new MacroEvent(0, MacroEventKind.MouseWheel, 0, w.X, w.Y, 0, w.Delta), version)).Append(Nl);
                    break;
                case PointerMoveStep m:
                    sb.Append(FormattableString.Invariant($"DllCall(\"mouse_event\", \"UInt\", 1, \"Int\", {m.Dx}, \"Int\", {m.Dy}, \"UInt\", 0, \"UPtr\", 0)")).Append(Nl);
                    break;
                case RawStep r:
                    for (int i = 0; i < r.Events.Count; i++)
                    {
                        if (i > 0 && r.Events[i].TimestampMs > r.Events[i - 1].TimestampMs)
                            sb.Append(Sleep(r.Events[i].TimestampMs - r.Events[i - 1].TimestampMs, version)).Append(Nl);
                        AppendEvent(sb, r.Events[i], version);
                    }
                    break;
            }
        }
    }

    /// <summary>Strips CR/LF from label text before it lands in a `;` comment line — a raw
    /// newline would push the rest of the label past the comment onto its own, uncommented
    /// line. Same treatment <see cref="AppendHeader"/> gives the macro name.</summary>
    private static string SanitizeComment(string s) => s.Replace("\r", "").Replace("\n", " ");

    private static string Sleep(long ms, AhkVersion version)
        => version == AhkVersion.V1 ? FormattableString.Invariant($"Sleep, {ms}") : FormattableString.Invariant($"Sleep {ms}");

    private static string ClickAt(int x, int y, int button, AhkVersion version)
    {
        var btn = ButtonName(button);
        return version == AhkVersion.V1
            ? FormattableString.Invariant($"Click, {x}, {y}, {btn}")
            : FormattableString.Invariant($"Click \"{x} {y} {btn}\"");
    }
}
