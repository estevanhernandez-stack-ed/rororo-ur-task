using Labs626.UrTask.Macros;

namespace Labs626.UrTask.Tests;

public class PlaybackEndLogTests
{
    private static readonly TimeSpan T = TimeSpan.FromMilliseconds(12_400);

    [Fact]
    public void A_clean_finish_reads_as_finished()
        => Assert.Equal("playback finished: 'Go to Top' on CElCPapa in 12.4 s.",
            PlaybackEndLog.Line("Go to Top", "CElCPapa", PlaybackResult.Completed(), T));

    [Fact]
    public void A_check_failure_carries_its_step_and_the_whole_sentence()
        => Assert.Equal("playback stopped at step 2: 'Mine spot N' on CElCPapa after 12.4 s. CElCPapa: step 2 'Spot N' could not see the window.",
            PlaybackEndLog.Line("Mine spot N", "CElCPapa", PlaybackResult.AbortedAt("CElCPapa: step 2 'Spot N' could not see the window.", 1), T));

    [Fact]
    public void A_foreground_abort_has_no_step()
        => Assert.Equal("playback stopped: 'Mine spot N' on CElCPapa after 12.4 s. Foreground shifted away from CElCPapa at step 2/3.",
            PlaybackEndLog.Line("Mine spot N", "CElCPapa", PlaybackResult.Aborted("Foreground shifted away from CElCPapa at step 2/3."), T));

    [Fact]
    public void A_refusal_after_start_reads_as_refused()
        => Assert.Equal("playback refused: 'x' on A after 12.4 s. Step 1 has no point id.",
            PlaybackEndLog.Line("x", "A", PlaybackResult.Refused("Step 1 has no point id."), T));

    [Fact]
    public void An_exception_reads_as_an_error_and_a_blank_name_as_unnamed()
        => Assert.Equal("playback ended with an error: '(unnamed)' on A after 12.4 s.",
            PlaybackEndLog.Line("  ", "A", null, T));

    [Fact]
    public void Seconds_are_invariant()
    {
        var prev = System.Globalization.CultureInfo.CurrentCulture;
        System.Globalization.CultureInfo.CurrentCulture = new System.Globalization.CultureInfo("de-DE");
        try { Assert.Contains("in 12.4 s.", PlaybackEndLog.Line("x", "A", PlaybackResult.Completed(), T)); }
        finally { System.Globalization.CultureInfo.CurrentCulture = prev; }
    }

    [Fact]
    public void The_bridge_line_matches_what_GetPlayback_returns()
    {
        Assert.Equal("bridge playback abc 'Go to Top': finished", PlaybackEndLog.BridgeLine("abc", "Go to Top", "finished", null, null));
        Assert.Equal("bridge playback abc 'Mine spot N': failed (check-failed). CElCPapa: step 2 'Spot N' could not see the window.",
            PlaybackEndLog.BridgeLine("abc", "Mine spot N", "failed", "check-failed", "CElCPapa: step 2 'Spot N' could not see the window."));
        Assert.Equal("bridge playback abc '(unnamed)': stopped", PlaybackEndLog.BridgeLine("abc", null, "stopped", null, null));
    }

    [Fact]
    public void A_run_skipped_by_reach_reads_as_finished_skipped()
        => Assert.Equal("playback finished (skipped: no outline): 'Clear spot N' on CElCPapa in 12.4 s.",
            PlaybackEndLog.Line("Clear spot N", "CElCPapa", PlaybackResult.CompletedSkippedByReach(), T));

    [Fact]
    public void The_bridge_line_for_a_skip_carries_the_reason()
        => Assert.Equal("bridge playback abc 'Clear spot N': finished (skipped)",
            PlaybackEndLog.BridgeLine("abc", "Clear spot N", "finished", "skipped", null));
}
