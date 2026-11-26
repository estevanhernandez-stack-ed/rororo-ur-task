using Labs626.UrTask.UI;

namespace Labs626.UrTask.Tests;

/// L6-11: the second click of a double-click on PLAY ASSIGNMENTS stopped the loop the first click started.
public class ClickDebounceTests
{
    [Fact]
    public void First_call_is_allowed_even_at_clock_zero()
    {
        long now = 0;
        var d = new ClickDebounce(TimeSpan.FromMilliseconds(500), () => now);
        Assert.True(d.TryEnter());
    }

    [Fact]
    public void A_second_call_inside_the_window_is_ignored()
    {
        long now = 10_000;
        var d = new ClickDebounce(TimeSpan.FromMilliseconds(500), () => now);
        Assert.True(d.TryEnter());
        now += 200;
        Assert.False(d.TryEnter());
    }

    [Fact]
    public void An_ignored_call_does_not_extend_the_window()
    {
        long now = 10_000;
        var d = new ClickDebounce(TimeSpan.FromMilliseconds(500), () => now);
        Assert.True(d.TryEnter());
        now += 400;
        Assert.False(d.TryEnter());
        now += 150;   // 550 ms after the accepted call
        Assert.True(d.TryEnter());
    }
}
