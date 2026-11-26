using Labs626.UrTask.Macros;

namespace Labs626.UrTask.Tests;

/// <summary>Owner ruling 1 (0.13.1): recording pauses keep-alive, and stopping brings it back.</summary>
public class RecordingPauseTests
{
    [Fact]
    public void A_running_loop_is_paused_at_record_start_and_resumed_at_stop()
    {
        var pause = new RecordingPause();

        Assert.True(pause.Begin(loopRunning: true));
        Assert.True(pause.IsPaused);
        Assert.True(pause.End(breakglassOn: false));
        Assert.False(pause.IsPaused);
    }

    // Break-glass released mid-recording wants keep-alive back, but the loop cannot start while a
    // recording runs. Refusing the restore and stopping there left keep-alive off with nothing to
    // bring it back, so the recording takes the restore and hands the loop back at stop.
    [Fact]
    public void A_break_glass_restore_that_lands_mid_recording_is_kept_for_the_stop()
    {
        var pause = new RecordingPause();
        Assert.False(pause.Begin(loopRunning: false));   // break-glass already had the loop stopped

        Assert.True(pause.TakeRestore());
        Assert.True(pause.IsPaused);
        Assert.True(pause.End(breakglassOn: false));
    }

    [Fact]
    public void A_restore_offered_outside_a_recording_is_not_taken_so_the_caller_starts_the_loop_itself()
    {
        var pause = new RecordingPause();

        Assert.False(pause.TakeRestore());
        Assert.False(pause.IsPaused);
        Assert.False(pause.End(breakglassOn: false));
    }

    [Fact]
    public void A_restore_taken_mid_recording_still_yields_to_break_glass_coming_back_on()
    {
        var pause = new RecordingPause();
        pause.Begin(loopRunning: false);
        pause.TakeRestore();

        Assert.False(pause.End(breakglassOn: true));
    }

    [Fact]
    public void A_loop_that_was_off_is_neither_paused_nor_started_at_stop()
    {
        var pause = new RecordingPause();

        Assert.False(pause.Begin(loopRunning: false));
        Assert.False(pause.IsPaused);
        Assert.False(pause.End(breakglassOn: false));
    }

    [Fact]
    public void Break_glass_on_at_stop_keeps_the_loop_off()
    {
        var pause = new RecordingPause();
        pause.Begin(loopRunning: true);

        Assert.False(pause.End(breakglassOn: true));
        Assert.False(pause.IsPaused);   // the debt is dropped, not carried to a later recording
    }

    [Fact]
    public void A_loop_the_user_started_during_the_recording_is_left_alone_at_stop()
    {
        // They ran a routine mid-recording: whatever state the loop is in at stop is their choice.
        var pause = new RecordingPause();
        pause.Begin(loopRunning: true);

        pause.LoopStartedMeanwhile();

        Assert.False(pause.IsPaused);
        Assert.False(pause.End(breakglassOn: false));
    }

    [Fact]
    public void A_loop_started_during_a_recording_that_paused_nothing_changes_nothing()
    {
        var pause = new RecordingPause();
        pause.Begin(loopRunning: false);

        pause.LoopStartedMeanwhile();

        Assert.False(pause.End(breakglassOn: false));
    }

    [Fact]
    public void The_resume_is_handed_out_once()
    {
        // Two stops can race (the hotkey, and the account closing): one restart, not two.
        var pause = new RecordingPause();
        pause.Begin(loopRunning: true);

        Assert.True(pause.End(breakglassOn: false));
        Assert.False(pause.End(breakglassOn: false));
    }

    [Fact]
    public void A_loop_start_outside_a_recording_is_ignored()
    {
        // The restart this class asked for comes after End, and must not count against the next recording.
        var pause = new RecordingPause();
        pause.Begin(loopRunning: true);
        Assert.True(pause.End(breakglassOn: false));

        pause.LoopStartedMeanwhile();

        Assert.True(pause.Begin(loopRunning: true));
        Assert.True(pause.End(breakglassOn: false));
    }

    [Fact]
    public void Each_recording_decides_afresh()
    {
        var pause = new RecordingPause();
        pause.Begin(loopRunning: true);
        pause.LoopStartedMeanwhile();
        pause.End(breakglassOn: false);

        Assert.False(pause.Begin(loopRunning: false));
        Assert.False(pause.End(breakglassOn: false));
        Assert.True(pause.Begin(loopRunning: true));
        Assert.True(pause.End(breakglassOn: false));
    }

    [Theory]
    [InlineData(true, false, true)]    // recording, loop paused or off: refuse the start
    [InlineData(true, true, false)]    // recording, a loop is running: Ctrl+Shift+P stops it, as always
    [InlineData(false, false, false)]  // not recording: nothing to refuse
    [InlineData(false, true, false)]
    public void Starting_the_loop_is_refused_only_while_recording(bool recording, bool loopRunning, bool refused)
    {
        Assert.Equal(refused, RecordingPause.RefusesLoopStart(recording, loopRunning));
    }

    [Fact]
    public void The_lines_are_the_ones_the_owner_ruled()
    {
        Assert.Equal("Keep-alive is paused while you record. It starts again when you stop.", RecordingPause.PausedLine);
        Assert.Equal("Keep-alive is back on.", RecordingPause.ResumedLine);
        Assert.Equal("Stop recording first. Keep-alive can't run while you record.", RecordingPause.RefusedLine);
    }
}
