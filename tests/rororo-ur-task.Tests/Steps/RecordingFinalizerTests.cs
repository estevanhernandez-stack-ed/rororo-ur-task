using Labs626.UrTask.Macros;
using Labs626.UrTask.Macros.Steps;

namespace Labs626.UrTask.Tests.Steps;

public class RecordingFinalizerTests
{
    private static Macro Rec(string coordSpace, params MacroEvent[] evs) => new(
        Macro.CurrentSchemaVersion, Guid.NewGuid().ToString(), "r", "PerWindow", 1, "a", null, 1, evs,
        CoordSpace: coordSpace, RecordedClientW: 800, RecordedClientH: 599);

    private static readonly MacroEvent Down = new(100, MacroEventKind.MouseDown, 0, 5, 5, 1, 0);
    private static readonly MacroEvent Up = new(180, MacroEventKind.MouseUp, 0, 5, 5, 1, 0);
    private static readonly MacroEvent KeyA = new(10, MacroEventKind.KeyDown, 0x41, 0, 0, 0, 0);

    [Fact]
    public void A_client_recording_with_clicks_gains_steps_and_scale()
    {
        var m = RecordingFinalizer.WithSteps(Rec(Macro.CoordSpaceClient, Down, Up), 125);
        Assert.True(m.HasSteps);
        Assert.Equal(125, m.RecordedDisplayScale);
        Assert.Equal(2, m.Events.Count); // the original is kept
    }

    [Fact]
    public void Keyboard_only_recordings_stay_on_the_event_path()
    {
        // Client space on purpose: the screen-space guard must not be what keeps it off the step
        // path. It is the "no mouse press" rule.
        var m = RecordingFinalizer.WithSteps(Rec(Macro.CoordSpaceClient, KeyA), 100);
        Assert.False(m.HasSteps);
        Assert.Null(m.RecordedDisplayScale);
    }

    [Fact]
    public void Screen_space_recordings_are_left_alone()
    {
        var m = RecordingFinalizer.WithSteps(Rec(Macro.CoordSpaceScreen, Down, Up), 100);
        Assert.False(m.HasSteps);
    }

    [Fact]
    public void Converting_twice_does_not_rename_points()
    {
        var once = RecordingFinalizer.WithSteps(Rec(Macro.CoordSpaceClient, Down, Up), 100);
        var twice = RecordingFinalizer.WithSteps(once, 100);
        Assert.Same(once.Steps, twice.Steps);
    }

    [Fact]
    public void A_point_macro_keeps_the_wait_between_its_last_input_and_the_stop()
    {
        // L3-13: the recording ended 10 s after the click was let go.
        var m = RecordingFinalizer.WithSteps(Rec(Macro.CoordSpaceClient, Down, Up), 100, endMs: 10_180);

        Assert.Equal(new WaitStep(10_000), m.Steps![^1]);
        Assert.True(m.Duration >= TimeSpan.FromSeconds(10));
    }

    [Fact]
    public void A_keyboard_only_recording_cannot_carry_that_wait_yet()
    {
        // The event list has no way to say "nothing happens until T" without a new field or a key
        // sent to the game, so it still ends at its last key. Waits for a schema change.
        var m = RecordingFinalizer.WithSteps(Rec(Macro.CoordSpaceClient, KeyA), 100, endMs: 10_010);

        Assert.False(m.HasSteps);
        Assert.Equal(TimeSpan.FromMilliseconds(10), m.Duration);
    }
}
