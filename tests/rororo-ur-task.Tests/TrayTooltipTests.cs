using Labs626.UrTask;
using Labs626.UrTask.UI;
using Xunit;

namespace Labs626.UrTask.Tests;

/// L3-10: the tray tooltip said "idle" while keep-alive ran, and "idle" after it stopped.
public class TrayTooltipTests
{
    [Fact]
    public void Says_keep_alive_is_on_when_the_loop_runs_and_nothing_else_is_happening()
        => Assert.Equal("RoRoRo Ur Task: keep-alive is on.", TrayService.TooltipFor(PluginState.Idle, loopRunning: true));

    [Fact]
    public void Says_keep_alive_is_off_when_the_loop_is_stopped()
        => Assert.Equal("RoRoRo Ur Task: keep-alive is off.", TrayService.TooltipFor(PluginState.Idle, loopRunning: false));

    [Fact]
    public void Recording_and_playing_take_priority()
    {
        Assert.Equal("RoRoRo Ur Task: recording.", TrayService.TooltipFor(PluginState.Recording, loopRunning: true));
        Assert.Equal("RoRoRo Ur Task: playing.", TrayService.TooltipFor(PluginState.Playing, loopRunning: false));
    }
}
