using System.IO;
using Labs626.UrTask.Macros;
using Labs626.UrTask.Macros.Steps;

namespace Labs626.UrTask.Tests.Steps;

public class StepConverterTests
{
    private static Macro Fixture(string name)
        => MacroV1Migrator.LoadAndMigrate(File.ReadAllText(Path.Combine(AppContext.BaseDirectory, "fixtures", name)));

    private static MacroEvent Ev(long t, MacroEventKind k, int x = 0, int y = 0, int vk = 0, int btn = 0, int wheel = 0)
        => new(t, k, vk, x, y, btn, wheel);

    [Fact]
    public void Mine_zone_8_collapses_to_five_points()
    {
        var steps = StepConverter.Convert(Fixture("mine-zone-8.json").Events);
        var points = steps.OfType<PointStep>().ToList();
        Assert.Equal(steps.Count, points.Count); // orphan key-ups dropped, modifier tail trimmed
        Assert.Equal(new[] { (472, 317), (42, 398), (100, 172), (134, 373), (302, 430) }, points.Select(p => (p.X, p.Y)));
        Assert.Equal(new[] { "p1", "p2", "p3", "p4", "p5" }, points.Select(p => p.Id));
    }

    [Fact]
    public void Mine_zone_8_keeps_still_time_and_drops_travel()
    {
        // Measured 2026-09-27: 19.4 s between clicks, 5.7 s of it hand travel.
        var points = StepConverter.Convert(Fixture("mine-zone-8.json").Events).OfType<PointStep>().ToList();
        var expected = new[] { 1500, 400, 4800, 1900, 5200 };
        for (int i = 0; i < expected.Length; i++)
            Assert.InRange(points[i].DelayMs, expected[i] - 250, expected[i] + 250);
        Assert.InRange(points.Sum(p => p.DelayMs), 13_300, 14_300);
    }

    [Fact]
    public void Egg_macro_collapses_auto_repeat_into_single_key_downs()
    {
        var steps = StepConverter.Convert(Fixture("mining-z8-egg.json").Events);
        // 8 clicks. One moves 6 px between down (578,420) and up (581,426). At the old 4 px
        // threshold it became a drag, which is why the threshold is 8 px.
        Assert.Equal(8, steps.OfType<PointStep>().Count());
        Assert.Empty(steps.OfType<DragStep>());
        Assert.Contains(steps.OfType<PointStep>(), p => (p.X, p.Y) == (578, 420));
        Assert.IsType<PointStep>(steps[0]);
        Assert.Equal(new KeyStep(steps[1].DelayMs, 0x41, true), steps[1]); // A pressed right after the wake-up click
        Assert.Equal(1, steps.OfType<KeyStep>().Count(k => k.VirtualKeyCode == 0x57 && k.Down)); // W: 142 recorded downs, one press
        Assert.Equal(1, steps.OfType<KeyStep>().Count(k => k.VirtualKeyCode == 0x45 && k.Down)); // E

        // The property that matters, whatever the fixture's key-ups look like:
        // no key is ever pressed again while it is still held.
        var held = new HashSet<int>();
        foreach (var k in steps.OfType<KeyStep>())
        {
            if (k.Down) Assert.True(held.Add(k.VirtualKeyCode), $"vk {k.VirtualKeyCode:X2} pressed while held");
            else held.Remove(k.VirtualKeyCode);
        }
    }

    [Fact]
    public void Egg_macro_drops_the_stop_hotkey_tail()
    {
        // The recording ends LCtrl down, LShift down, LShift up, LShift down (the stop hotkey).
        // Every modifier down with no later up goes, so Ctrl can never be left held. The
        // Shift down/up pair is a complete press and stays.
        var steps = StepConverter.Convert(Fixture("mining-z8-egg.json").Events);
        var keys = steps.OfType<KeyStep>().ToList();
        Assert.DoesNotContain(keys, k => k.VirtualKeyCode == 0xA2); // LCtrl, the key the old trim left held
        for (int i = 0; i < keys.Count; i++)
        {
            var k = keys[i];
            if (k.Down && k.VirtualKeyCode is 0x10 or 0x11 or 0x12 or 0xA0 or 0xA1 or 0xA2 or 0xA3 or 0xA4 or 0xA5)
                Assert.Contains(keys.Skip(i + 1), u => !u.Down && u.VirtualKeyCode == k.VirtualKeyCode);
        }
    }

    [Fact]
    public void Mine_zone_8_drops_its_stop_hotkey_ctrl()
        => Assert.DoesNotContain(StepConverter.Convert(Fixture("mine-zone-8.json").Events).OfType<KeyStep>(), k => k.VirtualKeyCode == 0xA2);

    [Fact]
    public void A_key_keeps_its_real_held_time_while_the_mouse_moves()
    {
        // W held 1100 ms while the hand moves the mouse for 900 ms of it. Travel is dropped only
        // before clicks; a key's up delay is its real held time.
        var evs = new List<MacroEvent> { Ev(0, MacroEventKind.KeyDown, vk: 0x57) };
        for (int i = 0; i < 19; i++) evs.Add(Ev(100 + i * 50, MacroEventKind.MouseMove, i * 10, 0));
        evs.Add(Ev(1100, MacroEventKind.KeyUp, vk: 0x57));
        var up = Assert.IsType<KeyStep>(StepConverter.Convert(evs)[1]);
        Assert.False(up.Down);
        Assert.Equal(1100, up.DelayMs);
    }

    [Fact]
    public void A_click_during_a_key_hold_keeps_the_key_held_for_its_real_time()
    {
        // W held 2.0 s. The hand moves the mouse for 0.5 s, then clicks at 1.0 s (80 ms press).
        // Travel is not dropped while W is down, so the timeline from W's down to its up still
        // adds up to 2.0 s: key down, click delay, the click's own press, key up delay.
        var evs = new List<MacroEvent> { Ev(0, MacroEventKind.KeyDown, vk: 0x57) };
        for (int t = 500; t <= 1000; t += 50) evs.Add(Ev(t, MacroEventKind.MouseMove, (t - 500) / 5, 100));
        evs.Add(Ev(1000, MacroEventKind.MouseDown, 100, 100, btn: 1));
        evs.Add(Ev(1080, MacroEventKind.MouseUp, 100, 100, btn: 1));
        evs.Add(Ev(2000, MacroEventKind.KeyUp, vk: 0x57));
        var steps = StepConverter.Convert(evs);
        Assert.Equal(3, steps.Count);
        var p = Assert.IsType<PointStep>(steps[1]);
        var up = Assert.IsType<KeyStep>(steps[2]);
        Assert.False(up.Down);
        Assert.Equal(1000, p.DelayMs);
        Assert.Equal(2000, steps[0].DelayMs + p.DelayMs + (1080 - 1000) + up.DelayMs);
    }

    [Fact]
    public void A_wheel_during_a_key_hold_keeps_its_travel()
    {
        var evs = new List<MacroEvent> { Ev(0, MacroEventKind.KeyDown, vk: 0x57) };
        for (int t = 500; t <= 1000; t += 50) evs.Add(Ev(t, MacroEventKind.MouseMove, (t - 500) / 5, 100));
        evs.Add(Ev(1000, MacroEventKind.MouseWheel, 100, 100, wheel: -120));
        evs.Add(Ev(2000, MacroEventKind.KeyUp, vk: 0x57));
        var steps = StepConverter.Convert(evs);
        Assert.Equal(1000, Assert.IsType<WheelStep>(steps[1]).DelayMs);
        Assert.Equal(1000, steps[2].DelayMs);
    }

    [Fact]
    public void Down_and_up_within_eight_pixels_is_a_point()
    {
        // 7 px and 6 px of hand jitter: still one click (the real egg recording has a 6 px click).
        var steps = StepConverter.Convert(new[] { Ev(100, MacroEventKind.MouseDown, 50, 50, btn: 1), Ev(180, MacroEventKind.MouseUp, 57, 56, btn: 1) });
        var p = Assert.IsType<PointStep>(Assert.Single(steps));
        Assert.Equal((50, 50), (p.X, p.Y));
        Assert.Equal(100, p.DelayMs);
    }

    [Fact]
    public void Right_drag_gets_the_camera_margin()
    {
        var steps = StepConverter.Convert(new[] { Ev(0, MacroEventKind.MouseDown, 400, 300, btn: 2), Ev(400, MacroEventKind.MouseUp, 400, 500, btn: 2) });
        var d = Assert.IsType<DragStep>(Assert.Single(steps));
        Assert.Equal((2, 400, 300, 0, 300, 400), (d.Button, d.StartX, d.StartY, d.Dx, d.Dy, d.DurationMs));
    }

    [Fact]
    public void Left_drag_keeps_its_exact_distance()
    {
        var steps = StepConverter.Convert(new[] { Ev(0, MacroEventKind.MouseDown, 100, 100, btn: 1), Ev(300, MacroEventKind.MouseUp, 180, 100, btn: 1) });
        var d = Assert.IsType<DragStep>(Assert.Single(steps));
        Assert.Equal((80, 0), (d.Dx, d.Dy));
    }

    [Fact]
    public void Wheel_becomes_a_wheel_step()
    {
        var w = Assert.IsType<WheelStep>(Assert.Single(StepConverter.Convert(new[] { Ev(50, MacroEventKind.MouseWheel, 10, 20, wheel: -120) })));
        Assert.Equal((50, 10, 20, -120), (w.DelayMs, w.X, w.Y, w.Delta));
    }

    [Fact]
    public void A_button_never_released_stays_raw()
    {
        var steps = StepConverter.Convert(new[] { Ev(0, MacroEventKind.MouseDown, 10, 10, btn: 1), Ev(50, MacroEventKind.MouseMove, 20, 20) });
        var raw = Assert.IsType<RawStep>(Assert.Single(steps));
        Assert.Equal("A mouse button was pressed and never released.", raw.Note);
    }

    [Fact]
    public void Two_buttons_held_at_once_stay_raw()
    {
        var steps = StepConverter.Convert(new[]
        {
            Ev(0, MacroEventKind.MouseDown, 10, 10, btn: 1),
            Ev(50, MacroEventKind.MouseDown, 10, 10, btn: 2),
            Ev(90, MacroEventKind.MouseUp, 10, 10, btn: 2),
            Ev(120, MacroEventKind.MouseUp, 10, 10, btn: 1),
            Ev(500, MacroEventKind.MouseDown, 30, 30, btn: 1),
            Ev(560, MacroEventKind.MouseUp, 30, 30, btn: 1),
        });
        var raw = Assert.IsType<RawStep>(steps[0]);
        Assert.Equal(4, raw.Events.Count);
        Assert.Equal(0, raw.Events[0].TimestampMs);
        Assert.Equal("Two mouse buttons were held at once.", raw.Note);
        Assert.IsType<PointStep>(steps[1]);
    }

    [Fact]
    public void Travel_is_only_counted_while_the_mouse_moves()
    {
        // 300 ms of motion in 50 ms hops, then a 1 s still, then the click.
        var evs = new List<MacroEvent>();
        for (int i = 0; i <= 6; i++) evs.Add(Ev(i * 50, MacroEventKind.MouseMove, i * 10, 0));
        evs.Add(Ev(1300, MacroEventKind.MouseDown, 60, 0, btn: 1));
        evs.Add(Ev(1380, MacroEventKind.MouseUp, 60, 0, btn: 1));
        var p = Assert.IsType<PointStep>(Assert.Single(StepConverter.Convert(evs)));
        Assert.InRange(p.DelayMs, 950, 1050);
    }
}
