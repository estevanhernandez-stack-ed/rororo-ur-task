using Labs626.UrTask.Hotkeys;

namespace Labs626.UrTask.Tests;

public class SweepKeyFilterTests
{
    private const int W = 0x57, LCtrl = 0xA2, LShift = 0xA0, Space = 0x20;

    [Fact]
    public void Drops_the_users_keys_but_never_ur_tasks_own_space()
    {
        var f = new SweepKeyFilter();

        Assert.False(f.OnKey(Space, down: true, injected: true, out _));
        Assert.False(f.OnKey(Space, down: false, injected: true, out _));
        Assert.True(f.OnKey(W, down: true, injected: false, out var chord));
        Assert.Null(chord);
        Assert.True(f.OnKey(W, down: true, injected: false, out _));   // auto-repeat
        Assert.True(f.OnKey(W, down: false, injected: false, out _));
        Assert.Equal(1, f.Dropped);
    }

    [Fact]
    public void A_key_held_from_before_the_sweep_is_let_go_not_left_stuck()
    {
        var f = new SweepKeyFilter();
        Assert.False(f.OnKey(W, down: false, injected: false, out _));
        Assert.False(f.OnButton(1, down: false, injected: false));
        Assert.Equal(0, f.Dropped);
    }

    [Fact]
    public void The_stop_chords_are_dropped_and_reported()
    {
        var f = new SweepKeyFilter();
        f.OnKey(LCtrl, true, false, out _);
        f.OnKey(LShift, true, false, out _);
        Assert.True(f.OnKey(SweepKeyFilter.VK_F12, true, false, out var abort));
        Assert.Equal(HotkeyKind.Abort, abort);
        Assert.True(f.OnKey(SweepKeyFilter.VK_F11, true, false, out var glass));
        Assert.Equal(HotkeyKind.Breakglass, glass);

        // Ctrl held from before the sweep counts; F12 without the chord is just a key.
        var held = new SweepKeyFilter(ctrlHeld: true, shiftHeld: false);
        Assert.True(held.OnKey(SweepKeyFilter.VK_F12, true, false, out var none));
        Assert.Null(none);
        Assert.True(held.OnKey(SweepKeyFilter.VK_ESCAPE, true, false, out var esc));
        Assert.Equal(HotkeyKind.Abort, esc);
    }

    [Fact]
    public void A_held_stop_chord_is_reported_once_per_press_not_on_every_auto_repeat()
    {
        // The hook sees a key-down for every typematic repeat. Break-glass is a toggle, so a held
        // Ctrl+Shift+F11 flipped it on, off and on again (L3-01).
        var f = new SweepKeyFilter(ctrlHeld: true, shiftHeld: true);

        Assert.True(f.OnKey(SweepKeyFilter.VK_F11, true, false, out var first));
        Assert.Equal(HotkeyKind.Breakglass, first);
        Assert.True(f.OnKey(SweepKeyFilter.VK_F11, true, false, out var repeat));   // auto-repeat: still dropped
        Assert.Null(repeat);
        Assert.True(f.OnKey(SweepKeyFilter.VK_F11, false, false, out _));
        Assert.True(f.OnKey(SweepKeyFilter.VK_F11, true, false, out var second));   // a new press
        Assert.Equal(HotkeyKind.Breakglass, second);

        Assert.True(f.OnKey(SweepKeyFilter.VK_ESCAPE, true, false, out var esc));
        Assert.Equal(HotkeyKind.Abort, esc);
        Assert.True(f.OnKey(SweepKeyFilter.VK_ESCAPE, true, false, out var escRepeat));
        Assert.Null(escRepeat);
        Assert.True(f.OnKey(SweepKeyFilter.VK_F12, true, false, out var f12));
        Assert.Equal(HotkeyKind.Abort, f12);
        Assert.True(f.OnKey(SweepKeyFilter.VK_F12, true, false, out var f12Repeat));
        Assert.Null(f12Repeat);
    }

    [Fact]
    public void A_key_held_before_its_chord_still_reports_the_chord_once()
    {
        // F12 goes down alone (just a key), then Ctrl+Shift join while it repeats: that repeat is
        // the first time the chord is down, so it is reported, once.
        var f = new SweepKeyFilter();
        Assert.True(f.OnKey(SweepKeyFilter.VK_F12, true, false, out var alone));
        Assert.Null(alone);
        f.OnKey(LCtrl, true, false, out _);
        f.OnKey(LShift, true, false, out _);
        Assert.True(f.OnKey(SweepKeyFilter.VK_F12, true, false, out var chord));
        Assert.Equal(HotkeyKind.Abort, chord);
        Assert.True(f.OnKey(SweepKeyFilter.VK_F12, true, false, out var again));
        Assert.Null(again);
    }

    [Fact]
    public void Clicks_and_the_wheel_are_dropped_and_injected_ones_pass()
    {
        var f = new SweepKeyFilter();
        Assert.True(f.OnButton(1, down: true, injected: false));
        Assert.True(f.OnButton(1, down: false, injected: false));
        Assert.True(f.OnButton(0, down: true, injected: false, isWheel: true));
        Assert.False(f.OnButton(2, down: true, injected: true));
        Assert.Equal(2, f.Dropped);
    }
}
