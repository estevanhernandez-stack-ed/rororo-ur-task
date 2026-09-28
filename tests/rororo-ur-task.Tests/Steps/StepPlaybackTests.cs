using Labs626.UrTask.Macros;
using Labs626.UrTask.Macros.Steps;

namespace Labs626.UrTask.Tests.Steps;

public class StepPlaybackTests
{
    [Fact]
    public void Window_came_out_larger_names_the_display_scale_and_roblox_minimum()
    {
        var text = MacroPlayer.SizeRefusalText((800, 599), (1002, 750), 125);
        Assert.Equal("Roblox wouldn't shrink this window to 800x599; it stayed 1002x750. At 125% display scale Roblox's smallest window is 1020x797. Record this macro at 125%, or play it on a PC set to the scale it was recorded at.", text);
    }

    [Fact]
    public void Window_came_out_smaller_keeps_todays_advice()
    {
        var text = MacroPlayer.SizeRefusalText((1718, 1360), (1718, 1300), 100);
        Assert.StartsWith("Couldn't size this window to the macro's recorded 1718x1360 (got 1718x1300).", text);
    }

    [Fact]
    public void Alt_outcome_carries_the_step_index()
    {
        var o = new AltOutcome(new Labs626.UrTask.PluginHost.AccountRegistry.AccountInfo(1001, 1, "alt-1", "acct-1"), PlaybackOutcome.Aborted, "x", 3);
        Assert.Equal(3, o.StepIndex);
    }
}
