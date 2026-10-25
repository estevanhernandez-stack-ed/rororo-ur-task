using Labs626.UrTask.Macros;

namespace Labs626.UrTask.Tests;

public class RecordingHotkeyTrimTests
{
    private const int LCtrl = 0xA2, LShift = 0xA0, R = 0x52, E = 0x45, Space = 0x20;

    private static MacroEvent Down(long t, int vk) => new(t, MacroEventKind.KeyDown, vk, 0, 0, 0, 0);
    private static MacroEvent Up(long t, int vk) => new(t, MacroEventKind.KeyUp, vk, 0, 0, 0, 0);
    private static MacroEvent Click(long t, MacroEventKind kind) => new(t, kind, 0, 10, 20, 1, 0);

    [Fact]
    public void Drops_the_start_chord_release_and_the_stop_chord_press()
    {
        // The shape of a real 2026-10-25 recording ("hatch 3"): the start chord's releases lead,
        // the stop chord's presses trail with no release.
        var events = new[]
        {
            Up(97, LCtrl), Up(101, R), Up(124, LShift),
            Down(1138, E), Up(1206, E),
            Down(11985, LCtrl), Down(11998, LShift),
        };

        var trimmed = MacroRecorder.TrimHotkeyKeys(events);

        Assert.Equal(new[] { Down(1138, E), Up(1206, E) }, trimmed);
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
