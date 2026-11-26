using Labs626.UrTask.Macros;
using Labs626.UrTask.Macros.Steps;

namespace Labs626.UrTask.Tests;

public class RecordingHotkeyTrimTests
{
    private const int LCtrl = 0xA2, LShift = 0xA0, R = 0x52, E = 0x45, Space = 0x20;

    private static MacroEvent Down(long t, int vk) => new(t, MacroEventKind.KeyDown, vk, 0, 0, 0, 0);
    private static MacroEvent Up(long t, int vk) => new(t, MacroEventKind.KeyUp, vk, 0, 0, 0, 0);
    private static MacroEvent Click(long t, MacroEventKind kind) => new(t, kind, 0, 10, 20, 1, 0);

    // The shape of a real 2026-10-25 recording ("hatch 3"): the start chord's releases lead, the
    // stop chord's presses trail with no release, 10.8 s after the last real key.
    private static readonly MacroEvent[] Hatch3 =
    {
        Up(97, LCtrl), Up(101, R), Up(124, LShift),
        Down(1138, E), Up(1206, E),
        Down(11985, LCtrl), Down(11998, LShift),
    };

    [Fact]
    public void Drops_the_start_chord_release_and_the_stop_chord_press()
    {
        var trimmed = MacroRecorder.TrimHotkeyKeys(Hatch3);

        Assert.Equal(new[] { Down(1138, E), Up(1206, E) }, trimmed);
    }

    [Fact]
    public void The_recording_ends_at_the_stop_chord_not_at_the_last_real_key()
    {
        // Owner ruling 2 (L3-13). The chord's keys are trimmed, but the time they were pressed is
        // when the recording ended: 10.8 s after E was let go.
        var end = MacroRecorder.RecordingEndMs(Hatch3, stoppedAtMs: 0);

        Assert.Equal(11998, end);
        Assert.Equal(10792, end - MacroRecorder.TrimHotkeyKeys(Hatch3)[^1].TimestampMs);
    }

    [Fact]
    public void The_recorders_own_stop_time_wins_when_it_is_later_than_the_last_captured_key()
    {
        // A per-window recording drops the chord's keys when another window is in front, and the
        // stop button presses no key at all: the recorder's clock at stop still says when it ended.
        Assert.Equal(12040, MacroRecorder.RecordingEndMs(Hatch3, stoppedAtMs: 12040));
        Assert.Equal(30_000, MacroRecorder.RecordingEndMs(new[] { Down(1138, E), Up(1206, E) }, stoppedAtMs: 30_000));
        Assert.Equal(0, MacroRecorder.RecordingEndMs(Array.Empty<MacroEvent>(), stoppedAtMs: 0));
    }

    [Fact]
    public void A_point_macro_plays_the_wait_before_the_stop_chord_and_none_of_the_chord()
    {
        // Hatch 3 with a click where the E was: the kind of recording that becomes steps.
        var events = new[]
        {
            Up(97, LCtrl), Up(101, R), Up(124, LShift),
            Click(1138, MacroEventKind.MouseDown), Click(1206, MacroEventKind.MouseUp),
            Down(11985, LCtrl), Down(11998, LShift),
        };

        var steps = StepConverter.Convert(
            MacroRecorder.TrimHotkeyKeys(events), MacroRecorder.RecordingEndMs(events, stoppedAtMs: 0));

        Assert.Equal(2, steps.Count);
        Assert.IsType<PointStep>(steps[0]);
        Assert.Equal(new WaitStep(10792), steps[1]);   // 11998 - 1206: last real input to the chord
        Assert.Empty(steps.OfType<KeyStep>());
    }

    [Fact]
    public void A_keyboard_only_recording_still_ends_at_its_last_key()
    {
        // Not the ruled behaviour, and deliberately pinned: keyboard-only recordings stay event
        // lists, and an event list cannot say "nothing happens until T" without a new field (no
        // schema change in a patch) or an extra key sent to the game. Until the schema grows one,
        // the 10.8 s after the last key is not played back.
        var macro = new Macro(Macro.CurrentSchemaVersion, "id", "hatch 3", "PerWindow", null, null, null, 0,
            MacroRecorder.TrimHotkeyKeys(Hatch3));

        Assert.Equal(TimeSpan.FromMilliseconds(1206), macro.Duration);
        Assert.Same(macro, RecordingFinalizer.WithSteps(macro, 100, MacroRecorder.RecordingEndMs(Hatch3, 0)));
    }

    [Fact]
    public void Drops_a_complete_modifier_pair_in_the_tail()
    {
        // The stop chord can come as LCtrl down, LShift down, LShift up, LShift down. A Shift tap
        // left in would toggle Roblox's shift-lock at the end of every pass.
        var events = new[]
        {
            Down(0, Space), Up(80, Space),
            Down(900, LCtrl), Down(910, LShift), Up(950, LShift), Down(980, LShift),
        };

        Assert.Equal(new[] { Down(0, Space), Up(80, Space) }, MacroRecorder.TrimHotkeyKeys(events));
    }

    [Fact]
    public void Keeps_modifiers_the_user_pressed_before_their_last_real_input()
    {
        // A Ctrl+V the user typed, and a Shift held through a click: real input, kept as is.
        var events = new[]
        {
            Down(0, LCtrl), Down(30, 0x56), Up(60, 0x56), Up(90, LCtrl),
            Down(200, LShift), Click(250, MacroEventKind.MouseDown), Click(300, MacroEventKind.MouseUp),
            Down(900, LCtrl),
        };

        var trimmed = MacroRecorder.TrimHotkeyKeys(events);

        Assert.Equal(events[..7], trimmed);
    }

    [Fact]
    public void Keeps_mouse_moves_in_the_tail_and_an_all_chord_recording_becomes_empty()
    {
        var move = new MacroEvent(500, MacroEventKind.MouseMove, 0, 5, 5, 0, 0);
        Assert.Equal(new[] { move }, MacroRecorder.TrimHotkeyKeys(new[] { Up(10, LCtrl), Down(400, LCtrl), move, Down(510, LShift) }));
        Assert.Empty(MacroRecorder.TrimHotkeyKeys(new[] { Up(10, LCtrl), Up(12, R), Down(400, LCtrl), Down(410, LShift) }));
    }
}
