using Labs626.UrTask.Macros;

namespace Labs626.UrTask.Tests;

/// <summary>L3-02: a per-window recording keeps only the keys pressed while its account is in front.</summary>
public class PerWindowKeyFilterTests
{
    private const int E = 0x45, W = 0x57, P = 0x50, LShift = 0xA0;
    private const MacroEventKind Down = MacroEventKind.KeyDown, Up = MacroEventKind.KeyUp;

    [Fact]
    public void Keeps_a_press_and_its_release_while_the_account_is_in_front()
    {
        var f = new PerWindowKeyFilter();

        Assert.True(f.Keep(Down, E, boundInFront: true));
        Assert.True(f.Keep(Up, E, boundInFront: true));
    }

    [Fact]
    public void Drops_everything_typed_into_another_window()
    {
        // The finding's scenario: alt-tab to a browser and type a password.
        var f = new PerWindowKeyFilter();
        Assert.True(f.Keep(Down, E, boundInFront: true));
        Assert.True(f.Keep(Up, E, boundInFront: true));

        foreach (var vk in new[] { P, 0x41, 0x53, 0x53 })
        {
            Assert.False(f.Keep(Down, vk, boundInFront: false));
            Assert.False(f.Keep(Up, vk, boundInFront: false));
        }

        Assert.True(f.Keep(Down, W, boundInFront: true));
        Assert.True(f.Keep(Up, W, boundInFront: true));
    }

    [Fact]
    public void Lets_the_release_through_for_a_key_pressed_in_the_game_and_let_go_elsewhere()
    {
        // W held, alt-tab away, let go: without the release the recording would hold W down for good.
        var f = new PerWindowKeyFilter();
        Assert.True(f.Keep(Down, W, boundInFront: true));

        Assert.True(f.Keep(Up, W, boundInFront: false));
    }

    [Fact]
    public void Drops_the_release_of_a_key_whose_press_was_dropped_even_back_in_the_game()
    {
        // Pressed in the browser, alt-tab back, let go over the game: the press was never recorded.
        var f = new PerWindowKeyFilter();
        Assert.False(f.Keep(Down, P, boundInFront: false));

        Assert.False(f.Keep(Up, P, boundInFront: true));
    }

    [Fact]
    public void Drops_auto_repeat_of_a_held_key_while_another_window_is_in_front_and_still_keeps_its_release()
    {
        var f = new PerWindowKeyFilter();
        Assert.True(f.Keep(Down, W, boundInFront: true));
        Assert.True(f.Keep(Down, W, boundInFront: true));    // auto-repeat in the game: as recorded today

        Assert.False(f.Keep(Down, W, boundInFront: false));  // still held, now over the browser
        Assert.False(f.Keep(Down, W, boundInFront: false));

        Assert.True(f.Keep(Up, W, boundInFront: false));
        Assert.False(f.Keep(Up, W, boundInFront: false));    // one release per recorded press
    }

    [Fact]
    public void A_key_released_elsewhere_and_pressed_again_there_is_dropped()
    {
        var f = new PerWindowKeyFilter();
        Assert.True(f.Keep(Down, E, boundInFront: true));
        Assert.True(f.Keep(Up, E, boundInFront: false));

        Assert.False(f.Keep(Down, E, boundInFront: false));
        Assert.False(f.Keep(Up, E, boundInFront: false));
    }

    [Fact]
    public void Drops_a_release_with_no_press_at_all()
    {
        // The start chord's own releases arrive first, with the account in front.
        var f = new PerWindowKeyFilter();

        Assert.False(f.Keep(Up, LShift, boundInFront: true));
    }

    [Fact]
    public void Tracks_each_key_on_its_own()
    {
        var f = new PerWindowKeyFilter();
        Assert.True(f.Keep(Down, LShift, boundInFront: true));
        Assert.False(f.Keep(Down, P, boundInFront: false));

        Assert.False(f.Keep(Up, P, boundInFront: false));
        Assert.True(f.Keep(Up, LShift, boundInFront: false));
    }

    [Fact]
    public void A_key_held_from_another_window_into_the_game_is_recorded_from_its_first_repeat_there()
    {
        // Its first press was dropped; the repeat that arrives with the game in front is game input.
        var f = new PerWindowKeyFilter();
        Assert.False(f.Keep(Down, W, boundInFront: false));

        Assert.True(f.Keep(Down, W, boundInFront: true));
        Assert.True(f.Keep(Up, W, boundInFront: true));
    }

    [Theory]
    [InlineData(MacroEventKind.MouseMove)]
    [InlineData(MacroEventKind.MouseDown)]
    [InlineData(MacroEventKind.MouseUp)]
    [InlineData(MacroEventKind.MouseWheel)]
    public void Has_no_say_over_mouse_events(MacroEventKind kind)
    {
        Assert.True(new PerWindowKeyFilter().Keep(kind, 0, boundInFront: false));
    }
}
