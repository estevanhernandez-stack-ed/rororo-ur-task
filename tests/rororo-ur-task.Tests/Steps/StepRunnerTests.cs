using Labs626.UrTask.Macros;
using Labs626.UrTask.Macros.Steps;

namespace Labs626.UrTask.Tests.Steps;

public class StepRunnerTests
{
    private static readonly Rgb Green = new(139, 224, 58);
    private static readonly Rgb Grey = new(150, 150, 160);
    private static readonly Rgb DarkBlue = new(20, 30, 90);

    /// <summary>Virtual clock; the screen is a function of (time, x, y).</summary>
    private sealed class FakeIo : IStepIo
    {
        public long NowMs { get; private set; }
        public List<MacroEvent> Sent = new();
        public List<(int, int)> Relative = new();
        public Func<long, int, int, Rgb> Screen = (_, _, _) => new Rgb(0, 0, 0);
        public (int W, int H)? Client = (800, 599);
        public bool Foreground = true;
        public bool CaptureWorks = true;
        public (int X, int Y) Cursor = (0, 0);
        public int Captures;
        public List<(int X, int Y, int W, int H)> CaptureRects = new();
        public List<long> CaptureTimes = new();
        public Action? OnDelay;
        public bool SendWorks = true;
        public bool WrongGeometry; // returns a block one pixel off from the rect asked for
        // The target window has closed: no client origin, so client-space mouse sends fail and
        // the cursor has no client position. Keys and screen-space releases still work.
        public bool WindowGone;
        public List<int> Released = new();

        public bool Send(MacroEvent e)
        {
            Sent.Add(e with { TimestampMs = NowMs });
            if (e.Kind == MacroEventKind.MouseMove) Cursor = (e.X, e.Y);
            if (WindowGone && e.Kind is not (MacroEventKind.KeyDown or MacroEventKind.KeyUp)) return false;
            return SendWorks;
        }
        public bool ReleaseButton(int button)
        {
            Released.Add(button);
            Sent.Add(new MacroEvent(NowMs, MacroEventKind.MouseUp, 0, Cursor.X, Cursor.Y, button, 0));
            return true;
        }
        public bool MoveRelative(int dx, int dy) { Relative.Add((dx, dy)); return true; }
        public (int X, int Y)? CursorClient() => WindowGone ? null : Cursor;
        public (int W, int H)? ClientSize() => Client;
        public bool TargetInForeground() => Foreground;
        public PixelBlock? Capture(int x, int y, int w, int h)
        {
            Captures++;
            CaptureRects.Add((x, y, w, h));
            CaptureTimes.Add(NowMs);
            if (!CaptureWorks) return null;
            var px = new uint[w * h];
            for (int r = 0; r < h; r++)
            for (int c = 0; c < w; c++)
            {
                var rgb = Screen(NowMs, x + c, y + r);
                px[r * w + c] = 0xFF000000u | (uint)(rgb.R << 16) | (uint)(rgb.G << 8) | (uint)rgb.B;
            }
            return WrongGeometry ? new PixelBlock(x + 1, y, w, h, px) : new PixelBlock(x, y, w, h, px);
        }
        public Task Delay(int ms, CancellationToken ct)
        {
            ct.ThrowIfCancellationRequested();
            NowMs += ms;
            OnDelay?.Invoke();
            ct.ThrowIfCancellationRequested();
            return Task.CompletedTask;
        }
        public IEnumerable<MacroEvent> Downs => Sent.Where(e => e.Kind == MacroEventKind.MouseDown);
    }

    private static StepContext Ctx(List<string>? log = null, Func<string, PointAdjustment?>? adjust = null,
        (int, int)? actual = null)
        => new("CElCPapa", 5400534998, "m1", (800, 599), actual ?? (800, 599), 100,
            adjust ?? (_ => null), s => log?.Add(s));

    private static ColorCheck GreenCheck(Rgb? other = null) => new(new CheckBox(), Green, other);

    // ---------- hold ----------

    private static readonly Rgb Ore = new(240, 170, 40);   // Sunstone orange
    private static readonly Rgb Rock = new(30, 35, 80);    // navy top-layer rock

    private static HoldStep Hold(int? maxMs = null, int button = 1, int x = 400, int y = 244, string label = "Spot N")
        => new(0, "spot-N", label, x, y, button, new HoldCheck(new CheckBox()), maxMs);

    [Fact]
    public async Task A_hold_keeps_the_button_down_until_the_colour_moves()
    {
        var log = new List<string>();
        // Press lands at 150 ms (after the jump). The block turns to rock at 1500 ms.
        var io = new FakeIo { Screen = (t, _, _) => t >= 1500 ? Rock : Ore };
        var r = await StepRunner.RunAsync(new MacroStep[] { Hold() }, Ctx(log), io, default);

        Assert.Equal(PlaybackOutcome.Completed, r.Outcome);
        var down = Assert.Single(io.Downs);
        var up = Assert.Single(io.Sent, e => e.Kind == MacroEventKind.MouseUp);
        Assert.Equal((400, 244, 1), (down.X, down.Y, down.MouseButton));
        Assert.Equal(150, down.TimestampMs);
        Assert.InRange(up.TimestampMs, 1500, 1500 + StepTiming.HoldDriftPolls * StepTiming.PollMs);
        Assert.Empty(io.Released); // the step let go itself; the finally had nothing to do
        Assert.Contains(log, l => l.StartsWith("step 1 'Spot N' held 1.5 s, released: colour moved from ") && l.Contains("(distance "));
    }

    [Fact]
    public async Task A_hold_stays_down_while_the_colour_holds_and_lets_go_at_maxMs()
    {
        var log = new List<string>();
        var io = new FakeIo { Screen = (_, _, _) => Ore };
        var r = await StepRunner.RunAsync(new MacroStep[] { Hold(maxMs: 5000, button: 2) }, Ctx(log), io, default);

        Assert.Equal(PlaybackOutcome.Completed, r.Outcome);
        var down = Assert.Single(io.Downs);
        var up = Assert.Single(io.Sent, e => e.Kind == MacroEventKind.MouseUp);
        Assert.Equal((2, 2), (down.MouseButton, up.MouseButton));
        Assert.Equal(5000, up.TimestampMs - down.TimestampMs);
        Assert.Contains("step 1 'Spot N' held 5.0 s, released: reached its 5000 ms limit", log);
    }

    [Fact]
    public async Task One_frame_past_the_tolerance_does_not_end_a_hold()
    {
        var log = new List<string>();
        // Polls fall at 250, 350, ... ms. Exactly one of them (1050) sees rock: a hit particle.
        var io = new FakeIo { Screen = (t, _, _) => t == 1050 ? Rock : Ore };
        await StepRunner.RunAsync(new MacroStep[] { Hold(maxMs: 3000) }, Ctx(log), io, default);
        Assert.Contains("step 1 'Spot N' held 3.0 s, released: reached its 3000 ms limit", log);
    }

    [Fact]
    public async Task Drift_within_tolerance_keeps_holding()
    {
        var log = new List<string>();
        var shimmer = new Rgb(Ore.R + 6, Ore.G + 6, Ore.B + 6); // distance ~10, under the default 15
        var io = new FakeIo { Screen = (t, _, _) => (t / 100) % 2 == 0 ? Ore : shimmer };
        await StepRunner.RunAsync(new MacroStep[] { Hold(maxMs: 2000) }, Ctx(log), io, default);
        Assert.Contains("step 1 'Spot N' held 2.0 s, released: reached its 2000 ms limit", log);
    }

    [Fact]
    public async Task A_hold_samples_its_starting_colour_after_the_jump()
    {
        // Hover tints the block the moment the pointer arrives (100 ms, the jump's last move).
        // Sampled before the jump, the start would be the untinted colour, and the press itself
        // would read as the colour moving.
        var log = new List<string>();
        var io = new FakeIo { Screen = (t, _, _) => t < 100 ? Rock : Ore };
        await StepRunner.RunAsync(new MacroStep[] { Hold(maxMs: 1000) }, Ctx(log), io, default);

        var down = Assert.Single(io.Downs);
        Assert.Equal(down.TimestampMs, io.CaptureTimes[0]);
        Assert.Contains(io.Sent, e => e.Kind == MacroEventKind.MouseMove && (e.X, e.Y) == (400, 244) && e.TimestampMs <= io.CaptureTimes[0]);
        Assert.Contains("step 1 'Spot N' held 1.0 s, released: reached its 1000 ms limit", log);
    }

    [Fact]
    public async Task Losing_the_foreground_mid_hold_releases_and_aborts()
    {
        var log = new List<string>();
        var io = new FakeIo { Screen = (_, _, _) => Ore };
        io.OnDelay = () => { if (io.NowMs >= 1000) io.Foreground = false; };
        var r = await StepRunner.RunAsync(new MacroStep[] { Hold() }, Ctx(log), io, default);

        Assert.Equal(PlaybackOutcome.Aborted, r.Outcome);
        Assert.Equal("Foreground shifted away from CElCPapa at step 1/1.", r.Reason);
        Assert.Null(r.StepIndex); // a focus change, never a check failure
        Assert.Equal(new[] { 1 }, io.Released);
        Assert.Contains(log, l => l.StartsWith("step 1 'Spot N' held ") && l.EndsWith(" s, then the playback ended"));
    }

    [Fact]
    public async Task Cancelling_a_hold_releases_its_button_and_logs_the_time()
    {
        // Ore that never breaks: Esc or StopMacro is the way out (spec, failure cases).
        using var cts = new CancellationTokenSource();
        var log = new List<string>();
        var io = new FakeIo { Screen = (_, _, _) => Ore };
        io.OnDelay = () => { if (io.NowMs >= 2000) cts.Cancel(); };
        var r = await StepRunner.RunAsync(new MacroStep[] { Hold() }, Ctx(log), io, cts.Token);

        Assert.Equal("Playback cancelled.", r.Reason);
        Assert.Null(r.StepIndex);
        Assert.Equal(new[] { 1 }, io.Released);
        Assert.Contains(log, l => l.StartsWith("step 1 'Spot N' held 1.9 s") || l.StartsWith("step 1 'Spot N' held 2.0 s"));
    }

    [Fact]
    public async Task A_window_that_closes_mid_hold_still_gets_its_button_released()
    {
        using var cts = new CancellationTokenSource();
        var io = new FakeIo { Screen = (_, _, _) => Ore };
        io.OnDelay = () => { if (io.Downs.Any() && io.NowMs >= 1000 && !io.WindowGone) { io.WindowGone = true; cts.Cancel(); } };
        var r = await StepRunner.RunAsync(new MacroStep[] { Hold() }, Ctx(), io, cts.Token);

        Assert.Equal("Playback cancelled.", r.Reason);
        Assert.Equal(new[] { 1 }, io.Released);
        Assert.Single(io.Sent, e => e.Kind == MacroEventKind.MouseUp); // the screen-space release only
    }

    [Fact]
    public async Task A_hold_scales_to_the_window_and_takes_its_adjustment()
    {
        var io = new FakeIo { Client = (1000, 749), Screen = (_, _, _) => Ore };
        await StepRunner.RunAsync(new MacroStep[] { Hold(maxMs: 500) },
            Ctx(adjust: id => id == "spot-N" ? new PointAdjustment(48, 401) : null, actual: (1000, 749)), io, default);
        // (48, 401) recorded in 800x599, placed in 1000x749.
        Assert.Equal((60, 501), (io.Downs.Single().X, io.Downs.Single().Y));
    }

    [Fact]
    public async Task A_hold_box_outside_the_window_refuses_before_any_input()
    {
        var io = new FakeIo();
        var r = await StepRunner.RunAsync(new MacroStep[] { Hold(x: 799, y: 598, label: "Edge") }, Ctx(), io, default);
        Assert.Equal("CElCPapa: step 1 'Edge' checks a box outside the window.", r.Reason);
        Assert.Equal(0, r.StepIndex);
        Assert.Empty(io.Sent);
    }

    [Fact]
    public async Task A_hold_that_cannot_see_the_window_stops_before_pressing()
    {
        var io = new FakeIo { CaptureWorks = false };
        var r = await StepRunner.RunAsync(new MacroStep[] { Hold() }, Ctx(), io, default);
        Assert.Equal("CElCPapa: step 1 'Spot N' could not see the window.", r.Reason);
        Assert.Equal(0, r.StepIndex);
        Assert.Empty(io.Downs);
    }

    [Fact]
    public async Task Hold_log_lines_use_invariant_numbers()
    {
        var prev = System.Globalization.CultureInfo.CurrentCulture;
        System.Globalization.CultureInfo.CurrentCulture = new System.Globalization.CultureInfo("de-DE");
        try
        {
            var log = new List<string>();
            var io = new FakeIo { Screen = (_, _, _) => Ore };
            await StepRunner.RunAsync(new MacroStep[] { Hold(maxMs: 1500) }, Ctx(log), io, default);
            Assert.Contains("step 1 'Spot N' held 1.5 s, released: reached its 1500 ms limit", log);
        }
        finally { System.Globalization.CultureInfo.CurrentCulture = prev; }
    }

    [Fact]
    public async Task Unchecked_point_waits_its_delay_then_presses()
    {
        var io = new FakeIo();
        var r = await StepRunner.RunAsync(new MacroStep[] { new PointStep(1500, "p1", null, 42, 398) }, Ctx(), io, default);
        Assert.Equal(PlaybackOutcome.Completed, r.Outcome);
        var down = Assert.Single(io.Downs);
        Assert.Equal((42, 398), (down.X, down.Y));
        Assert.True(down.TimestampMs >= 1500);
        Assert.Contains(io.Sent, e => e.Kind == MacroEventKind.MouseUp && e.X == 42);
    }

    [Fact]
    public async Task Points_scale_to_the_actual_window()
    {
        var io = new FakeIo { Client = (1000, 749) };
        await StepRunner.RunAsync(new MacroStep[] { new PointStep(0, "p1", null, 400, 300) }, Ctx(actual: (1000, 749)), io, default);
        Assert.Equal((500, 375), (io.Downs.Single().X, io.Downs.Single().Y));
    }

    [Fact]
    public async Task An_adjustment_replaces_the_recorded_point()
    {
        var io = new FakeIo();
        await StepRunner.RunAsync(new MacroStep[] { new PointStep(0, "p2", null, 42, 398) },
            Ctx(adjust: id => id == "p2" ? new PointAdjustment(48, 401) : null), io, default);
        Assert.Equal((48, 401), (io.Downs.Single().X, io.Downs.Single().Y));
    }

    [Fact]
    public async Task Checked_point_presses_as_soon_as_the_colour_shows()
    {
        var io = new FakeIo { Screen = (t, _, _) => t >= 1000 ? Green : DarkBlue };
        var step = new PointStep(5200, "p1", "Tile", 100, 100, Check: GreenCheck(), CheckEnabled: true);
        var r = await StepRunner.RunAsync(new MacroStep[] { step }, Ctx(), io, default);
        Assert.Equal(PlaybackOutcome.Completed, r.Outcome);
        var down = io.Downs.Single();
        Assert.InRange(down.TimestampMs, 1000, 1000 + StepTiming.PollMs + StepTiming.JumpWiggleMs);
    }

    [Fact]
    public async Task Nearby_search_finds_a_shifted_button_and_logs_it()
    {
        var log = new List<string>();
        // Green band x 84..90: the box at 12 px left (86..90) is all green, the one at 8 px left (90..94) is not.
        var io = new FakeIo { Screen = (_, x, _) => x is >= 84 and <= 90 ? Green : DarkBlue };
        var step = new PointStep(0, "p1", "Opener", 100, 100, Check: GreenCheck(), CheckEnabled: true);
        var r = await StepRunner.RunAsync(new MacroStep[] { step }, Ctx(log), io, default);
        Assert.Equal(PlaybackOutcome.Completed, r.Outcome);
        Assert.Equal((88, 100), (io.Downs.Single().X, io.Downs.Single().Y));
        Assert.Contains(log, l => l.Contains("'Opener' found 12 px left"));
    }

    [Fact]
    public async Task A_check_that_never_matches_stops_with_a_readable_report()
    {
        var io = new FakeIo { Screen = (_, _, _) => DarkBlue };
        var steps = new MacroStep[]
        {
            new PointStep(0, "p1", null, 10, 10),
            new PointStep(2800, "p2", "Teleport opener", 100, 100, Check: GreenCheck(), CheckEnabled: true),
        };
        var r = await StepRunner.RunAsync(steps, Ctx(), io, default);
        Assert.Equal(PlaybackOutcome.Aborted, r.Outcome);
        Assert.Equal(1, r.StepIndex);
        Assert.Equal("CElCPapa: step 2 'Teleport opener' expected green #8BE03A, saw dark blue #141E5A (distance 230) after 5.8 s at 100%.", r.Reason);
        Assert.Single(io.Downs); // only step 1 pressed
    }

    [Fact]
    public async Task First_match_skips_a_locked_tile_without_waiting()
    {
        // #8 grey (locked), #7 green: every candidate resolves on the first poll.
        var io = new FakeIo { Screen = (_, x, _) => x < 200 ? Grey : Green };
        var fm = new FirstMatchStep(400, "f1", "Best mine", new[]
        {
            new PointStep(0, "t8", "#8", 137, 390, Check: GreenCheck(Grey)),
            new PointStep(0, "t7", "#7", 312, 390, Check: GreenCheck(Grey)),
        });
        var r = await StepRunner.RunAsync(new MacroStep[] { fm }, Ctx(), io, default);
        Assert.Equal(PlaybackOutcome.Completed, r.Outcome);
        Assert.Equal((312, 390), (io.Downs.Single().X, io.Downs.Single().Y));
        Assert.True(io.Downs.Single().TimestampMs < 400 + 3000);
    }

    [Fact]
    public async Task First_match_waits_for_a_window_still_sliding_in()
    {
        // Nothing resolves until t=1200 (the window fades in), then #8 is green.
        var io = new FakeIo { Screen = (t, _, _) => t >= 1200 ? Green : DarkBlue };
        var fm = new FirstMatchStep(0, "f1", "Best mine", new[]
        {
            new PointStep(0, "t8", "#8", 137, 390, Check: GreenCheck(Grey)),
            new PointStep(0, "t7", "#7", 312, 390, Check: GreenCheck(Grey)),
        });
        await StepRunner.RunAsync(new MacroStep[] { fm }, Ctx(), io, default);
        var down = io.Downs.Single();
        Assert.Equal((137, 390), (down.X, down.Y));
        Assert.True(down.TimestampMs >= 1200);
    }

    [Fact]
    public async Task First_match_with_no_match_skips_or_stops_as_told()
    {
        var io = new FakeIo { Screen = (_, _, _) => Grey };
        PointStep[] cands = { new(0, "t8", "#8", 137, 390, Check: GreenCheck(Grey)), new(0, "t7", "#7", 312, 390, Check: GreenCheck(Grey)) };

        var skip = await StepRunner.RunAsync(new MacroStep[] { new FirstMatchStep(0, "f1", "Best mine", cands, NoMatchAction.Skip), new PointStep(0, "p9", null, 5, 5) }, Ctx(), io, default);
        Assert.Equal(PlaybackOutcome.Completed, skip.Outcome);
        Assert.Equal((5, 5), (io.Downs.Single().X, io.Downs.Single().Y));

        var io2 = new FakeIo { Screen = (_, _, _) => Grey };
        var stop = await StepRunner.RunAsync(new MacroStep[] { new FirstMatchStep(0, "f1", "Best mine", cands) }, Ctx(), io2, default);
        Assert.Equal(PlaybackOutcome.Aborted, stop.Outcome);
        Assert.StartsWith("CElCPapa: step 1 'Best mine' found no candidate that matched after", stop.Reason);
        // Full colour names plus expected colour and distance, per the failure-text constraint.
        Assert.Contains("'#8' expected green #8BE03A, saw grey #9696A0 (distance 126)", stop.Reason);
        Assert.Equal(0, stop.StepIndex);
    }

    [Fact]
    public async Task First_match_parks_the_pointer_off_every_candidate()
    {
        // The pointer rests on #8 after an earlier press, and hover would tint it.
        var io = new FakeIo { Cursor = (137, 390), Screen = (_, _, _) => Green };
        var fm = new FirstMatchStep(0, "f1", "Best mine", new[]
        {
            new PointStep(0, "t8", "#8", 137, 390, Check: GreenCheck(Grey)),
            new PointStep(0, "t7", "#7", 312, 390, Check: GreenCheck(Grey)),
        });
        await StepRunner.RunAsync(new MacroStep[] { fm }, Ctx(), io, default);
        var park = io.Sent.First(e => e.Kind == MacroEventKind.MouseMove);
        Assert.True(Math.Abs(park.X - 137) > 10 || Math.Abs(park.Y - 390) > 10);
        Assert.True(Math.Abs(park.X - 312) > 10 || Math.Abs(park.Y - 390) > 10);
    }

    [Fact]
    public async Task A_window_covered_while_waiting_for_its_colour_is_not_a_colour_mismatch()
    {
        // Another window comes to the front 300 ms into the wait. What the capture sees then is
        // not the target, so the stop must say so rather than report a wrong colour.
        var io = new FakeIo { Screen = (_, _, _) => DarkBlue };
        io.OnDelay = () => { if (io.NowMs >= 300) io.Foreground = false; };
        var step = new PointStep(1000, "p1", "Tile", 100, 100, Check: GreenCheck(), CheckEnabled: true);
        var r = await StepRunner.RunAsync(new MacroStep[] { step }, Ctx(), io, default);
        Assert.Equal("CElCPapa: step 1 'Tile' could not see the window.", r.Reason);
        Assert.Equal(0, r.StepIndex);
        Assert.Empty(io.Downs);
    }

    [Fact]
    public async Task First_match_covered_while_waiting_could_not_see_the_window()
    {
        var io = new FakeIo { Screen = (_, _, _) => DarkBlue };
        io.OnDelay = () => { if (io.NowMs >= 300) io.Foreground = false; };
        var fm = new FirstMatchStep(0, "f1", "Best mine", new[] { new PointStep(0, "t8", "#8", 137, 390, Check: GreenCheck(Grey)) });
        var r = await StepRunner.RunAsync(new MacroStep[] { fm }, Ctx(), io, default);
        Assert.Equal("CElCPapa: step 1 'Best mine' could not see the window.", r.Reason);
        Assert.Empty(io.Downs);
    }

    [Fact]
    public async Task A_check_box_outside_the_window_refuses()
    {
        var io = new FakeIo();
        var step = new PointStep(0, "p1", "Edge", 799, 598, Check: GreenCheck(), CheckEnabled: true);
        var r = await StepRunner.RunAsync(new MacroStep[] { step }, Ctx(), io, default);
        Assert.Equal("CElCPapa: step 1 'Edge' checks a box outside the window.", r.Reason);
        Assert.Empty(io.Downs);
    }

    [Fact]
    public async Task Search_never_samples_outside_the_window()
    {
        // The point sits 4 px from the left edge and its button is 8 px right (green at x 10..14).
        // The search must skip offsets whose box would cross x < 0, still find the button, and
        // never capture a pixel outside the 800x599 client area.
        var io = new FakeIo { Screen = (_, x, _) => x is >= 10 and <= 14 ? Green : DarkBlue };
        var step = new PointStep(0, "p1", "Edge", 4, 100, Check: GreenCheck(), CheckEnabled: true);
        var r = await StepRunner.RunAsync(new MacroStep[] { step }, Ctx(), io, default);
        Assert.Equal(PlaybackOutcome.Completed, r.Outcome);
        Assert.Equal((12, 100), (io.Downs.Single().X, io.Downs.Single().Y));
        Assert.NotEmpty(io.CaptureRects);
        Assert.All(io.CaptureRects, c => Assert.True(c.X >= 0 && c.Y >= 0 && c.X + c.W <= 800 && c.Y + c.H <= 599, $"captured {c} outside the window"));
    }

    [Fact]
    public async Task A_window_that_cannot_be_seen_is_not_a_colour_mismatch()
    {
        var io = new FakeIo { CaptureWorks = false };
        var step = new PointStep(0, "p1", "Tile", 100, 100, Check: GreenCheck(), CheckEnabled: true);
        var r = await StepRunner.RunAsync(new MacroStep[] { step }, Ctx(), io, default);
        Assert.Equal("CElCPapa: step 1 'Tile' could not see the window.", r.Reason);
    }

    [Fact]
    public async Task Losing_the_foreground_aborts()
    {
        var io = new FakeIo { Foreground = false };
        var r = await StepRunner.RunAsync(new MacroStep[] { new PointStep(0, "p1", null, 1, 1) }, Ctx(), io, default);
        Assert.Equal(PlaybackOutcome.Aborted, r.Outcome);
        Assert.Equal("Foreground shifted away from CElCPapa at step 1/1.", r.Reason);
        Assert.Null(r.StepIndex); // not a check failure, so GetPlayback must not call it check-failed
    }

    [Fact]
    public async Task Losing_the_foreground_during_an_unchecked_delay_sends_nothing()
    {
        // SendInput goes to whatever window is in front, so the press must not follow a focus change.
        var io = new FakeIo();
        io.OnDelay = () => { if (io.NowMs >= 300) io.Foreground = false; };
        var r = await StepRunner.RunAsync(new MacroStep[] { new PointStep(1000, "p1", null, 42, 398) }, Ctx(), io, default);
        Assert.Equal(PlaybackOutcome.Aborted, r.Outcome);
        Assert.Equal("Foreground shifted away from CElCPapa at step 1/1.", r.Reason);
        Assert.Null(r.StepIndex);
        Assert.Empty(io.Downs);
        Assert.Empty(io.Sent);
    }

    [Fact]
    public async Task Losing_the_foreground_midway_through_a_drag_stops_moving_and_releases()
    {
        var io = new FakeIo();
        int sentAtDrop = -1;
        // Jump takes 150 ms, then 30 ms slices: the drop lands after the second drag move.
        io.OnDelay = () => { if (io.NowMs >= 240 && io.Foreground) { io.Foreground = false; sentAtDrop = io.Sent.Count; } };
        var r = await StepRunner.RunAsync(new MacroStep[] { new DragStep(0, 2, 400, 300, 0, 300, 300) }, Ctx(), io, default);
        Assert.Equal(PlaybackOutcome.Aborted, r.Outcome);
        Assert.Null(r.StepIndex);
        Assert.True(sentAtDrop > 0);
        var after = io.Sent.Skip(sentAtDrop).ToList();
        var release = Assert.Single(after); // no further moves, only the finally's release
        Assert.Equal((MacroEventKind.MouseUp, 2), (release.Kind, release.MouseButton));
    }

    [Fact]
    public async Task A_refused_send_aborts()
    {
        var io = new FakeIo { SendWorks = false };
        var r = await StepRunner.RunAsync(new MacroStep[] { new PointStep(0, "p1", null, 42, 398), new PointStep(0, "p2", null, 5, 5) }, Ctx(), io, default);
        Assert.Equal(PlaybackOutcome.Aborted, r.Outcome);
        Assert.Equal("Could not send input to CElCPapa at step 1/2.", r.Reason);
        Assert.Null(r.StepIndex);
        Assert.Empty(io.Downs);
    }

    [Fact]
    public async Task A_capture_of_the_wrong_shape_could_not_see_the_window()
    {
        var io = new FakeIo { WrongGeometry = true, Screen = (_, _, _) => Green };
        var step = new PointStep(0, "p1", "Tile", 100, 100, Check: GreenCheck(), CheckEnabled: true);
        var r = await StepRunner.RunAsync(new MacroStep[] { step }, Ctx(), io, default);
        Assert.Equal("CElCPapa: step 1 'Tile' could not see the window.", r.Reason);
        Assert.Equal(0, r.StepIndex);

        var io2 = new FakeIo { WrongGeometry = true, Screen = (_, _, _) => Green };
        var fm = new FirstMatchStep(0, "f1", "Best mine", new[] { new PointStep(0, "t8", "#8", 137, 390, Check: GreenCheck(Grey)) });
        var r2 = await StepRunner.RunAsync(new MacroStep[] { fm }, Ctx(), io2, default);
        Assert.Equal("CElCPapa: step 1 'Best mine' could not see the window.", r2.Reason);
    }

    [Fact]
    public async Task Tuning_log_lines_use_invariant_numbers()
    {
        var prev = System.Globalization.CultureInfo.CurrentCulture;
        System.Globalization.CultureInfo.CurrentCulture = new System.Globalization.CultureInfo("de-DE");
        try
        {
            var log = new List<string>();
            var io = new FakeIo { Screen = (t, _, _) => t >= 1000 ? Green : DarkBlue };
            var step = new PointStep(5200, "p1", "Tile", 100, 100, Check: GreenCheck(), CheckEnabled: true);
            await StepRunner.RunAsync(new MacroStep[] { step }, Ctx(log), io, default);
            Assert.Contains(log, l => l.Contains("matched at distance 0 after 1.0 s"));
        }
        finally { System.Globalization.CultureInfo.CurrentCulture = prev; }
    }

    [Fact]
    public async Task Cancelling_during_a_wait_for_colour_stops_and_releases_keys()
    {
        using var cts = new CancellationTokenSource();
        var io = new FakeIo { Screen = (_, _, _) => DarkBlue };
        io.OnDelay = () => { if (io.NowMs >= 500) cts.Cancel(); };
        var steps = new MacroStep[]
        {
            new KeyStep(0, 0x41, true),
            new PointStep(0, "p1", "Tile", 100, 100, Check: GreenCheck(), CheckEnabled: true),
        };
        var r = await StepRunner.RunAsync(steps, Ctx(), io, cts.Token);
        Assert.Equal("Playback cancelled.", r.Reason);
        Assert.Null(r.StepIndex); // Esc is not a check failure
        Assert.True(io.NowMs < 1000);
        Assert.Contains(io.Sent, e => e.Kind == MacroEventKind.KeyUp && e.VirtualKeyCode == 0x41);
    }

    [Fact]
    public async Task A_window_that_closes_mid_press_still_gets_its_button_and_keys_released()
    {
        // AutoStopCoordinator aborts exactly when the account's window closes. The press is under
        // way (button down, 80 ms hold), the client origin is gone, and a client-space MouseUp
        // would be dropped: the release has to go out in screen space instead.
        using var cts = new CancellationTokenSource();
        var io = new FakeIo();
        io.OnDelay = () => { if (io.Downs.Any() && !io.WindowGone) { io.WindowGone = true; cts.Cancel(); } };
        var steps = new MacroStep[] { new KeyStep(0, 0x57, true), new PointStep(0, "p1", null, 42, 398) };
        var r = await StepRunner.RunAsync(steps, Ctx(), io, cts.Token);
        Assert.Equal("Playback cancelled.", r.Reason);
        Assert.Equal(new[] { 1 }, io.Released);
        Assert.Single(io.Sent, e => e.Kind == MacroEventKind.MouseUp); // the release, not a finished press
        Assert.Contains(io.Sent, e => e.Kind == MacroEventKind.KeyUp && e.VirtualKeyCode == 0x57);
    }

    [Fact]
    public async Task Held_keys_are_released_at_the_end()
    {
        var io = new FakeIo();
        await StepRunner.RunAsync(new MacroStep[] { new KeyStep(0, 0x57, true) }, Ctx(), io, default);
        Assert.Equal(MacroEventKind.KeyUp, io.Sent[^1].Kind);
        Assert.Equal(0x57, io.Sent[^1].VirtualKeyCode);
    }

    [Fact]
    public async Task Right_drag_presses_moves_and_releases()
    {
        var io = new FakeIo();
        await StepRunner.RunAsync(new MacroStep[] { new DragStep(0, 2, 400, 300, 0, 300, 400) }, Ctx(), io, default);
        var down = io.Sent.First(e => e.Kind == MacroEventKind.MouseDown);
        var up = io.Sent.Last(e => e.Kind == MacroEventKind.MouseUp);
        Assert.Equal((2, 400, 300), (down.MouseButton, down.X, down.Y));
        Assert.Equal((400, 600), (up.X, up.Y));
    }

    [Fact]
    public async Task Pointer_move_sends_relative_movement()
    {
        var io = new FakeIo();
        await StepRunner.RunAsync(new MacroStep[] { new PointerMoveStep(0, 0, 200, 300) }, Ctx(), io, default);
        Assert.Equal(200, io.Relative.Sum(m => m.Item2));
    }

    [Fact]
    public async Task The_cursor_is_parked_off_a_box_before_checking()
    {
        var io = new FakeIo { Cursor = (100, 100), Screen = (_, _, _) => Green };
        var step = new PointStep(0, "p1", "Tile", 100, 100, Check: GreenCheck(), CheckEnabled: true);
        await StepRunner.RunAsync(new MacroStep[] { step }, Ctx(), io, default);
        var firstMove = io.Sent.First(e => e.Kind == MacroEventKind.MouseMove);
        Assert.True(Math.Abs(firstMove.X - 100) > 10 || Math.Abs(firstMove.Y - 100) > 10);
    }

    [Fact]
    public async Task Invalid_steps_refuse_before_any_input()
    {
        var io = new FakeIo();
        var r = await StepRunner.RunAsync(new MacroStep[] { new FirstMatchStep(0, "f1", "Best mine", Array.Empty<PointStep>()) }, Ctx(), io, default);
        Assert.Equal(PlaybackOutcome.Refused, r.Outcome);
        Assert.Empty(io.Sent);
    }
}
