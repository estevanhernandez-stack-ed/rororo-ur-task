using Labs626.UrTask.Macros;
using Labs626.UrTask.Macros.Steps;
using Labs626.UrTask.Ipc;

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
        // Whether a mouse button is down right now: the game hides the white outline while one is.
        public bool ButtonDown;

        public bool Send(MacroEvent e)
        {
            Sent.Add(e with { TimestampMs = NowMs });
            if (e.Kind == MacroEventKind.MouseMove) Cursor = (e.X, e.Y);
            if (e.Kind == MacroEventKind.MouseDown) ButtonDown = true;
            if (e.Kind == MacroEventKind.MouseUp) ButtonDown = false;
            if (WindowGone && e.Kind is not (MacroEventKind.KeyDown or MacroEventKind.KeyUp)) return false;
            return SendWorks;
        }
        public bool ReleaseButton(int button)
        {
            Released.Add(button);
            ButtonDown = false;
            Sent.Add(new MacroEvent(NowMs, MacroEventKind.MouseUp, 0, Cursor.X, Cursor.Y, button, 0));
            return true;
        }
        public bool MoveRelative(int dx, int dy) { Relative.Add((dx, dy)); return true; }
        // The title-bar park: recorded with its time; false (nothing moved) when the window has no
        // frame above its client area, or has gone.
        public bool ParkWorks = true;
        public List<(int X, int Y, long T)> Parks = new();
        public bool ParkPointer(int x, int y)
        {
            Parks.Add((x, y, NowMs));
            if (WindowGone || !ParkWorks) return false;
            Cursor = (x, y);
            return true;
        }
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
        public IEnumerable<MacroEvent> Ups => Sent.Where(e => e.Kind == MacroEventKind.MouseUp);
    }

    private static StepContext Ctx(List<string>? log = null, Func<string, PointAdjustment?>? adjust = null,
        (int, int)? actual = null, bool shared = false, ScreenGuard? guard = null)
        => new("CElCPapa", 5400534998, shared ? "clearat-test" : "m1", (800, 599), actual ?? (800, 599), 100,
            adjust ?? (_ => null), s => log?.Add(s), SharedBaseline: shared, Guard: guard);

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
    public async Task Two_isolated_drift_polls_with_a_clean_one_between_do_not_end_a_hold()
    {
        var log = new List<string>();
        // Two drifted polls (1050, 1450), not adjacent, with a clean poll at 1150 between them.
        // The drift counter must reset on the clean poll, or these two isolated hits add up to
        // HoldDriftPolls and end the hold early on the second drift instead of at maxMs.
        var io = new FakeIo { Screen = (t, _, _) => t is 1050 or 1450 ? Rock : Ore };
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

    // ---------- reach (the white outline) ----------

    private static readonly Rgb White = new(245, 245, 245);
    private static readonly OutlineCheck Reach80 = new(new CheckBox(-40, -40, 80, 80), 60);

    /// <summary>Mine #8 rock and lava: navy, hot magenta, orange, pink, and a warm near-white whose
    /// blue sits under 225. None of them has every channel at 225 or more.</summary>
    private static readonly Rgb[] Lava = { new(8, 9, 19), new(235, 9, 116), new(255, 140, 30), new(255, 120, 200), new(250, 230, 180) };
    private static Rgb LavaAt(int x, int y) => Lava[(x * 7 + y * 13) % Lava.Length];

    /// <summary>A block face centred on (cx, cy): ore inside, a white frame <paramref name="thick"/>
    /// px wide at <paramref name="half"/> px out, rock beyond. The ring at distance k holds 8k
    /// pixels, so a 1 px frame at 28 is 224 pixels and a 2 px frame is 224 + 216 = 440.
    /// <paramref name="shown"/> turns the outline off (rock instead of white) at chosen times.</summary>
    private static Func<long, int, int, Rgb> Framed(int cx, int cy, int half = 28, int thick = 1, Func<long, bool>? shown = null)
        => (t, x, y) =>
        {
            var m = Math.Max(Math.Abs(x - cx), Math.Abs(y - cy));
            if (m > half) return Rock;
            if (m > half - thick) return shown is null || shown(t) ? White : Rock;
            return Ore;
        };

    /// <summary>True while the pointer is on the block centred on (cx, cy): the game draws the
    /// outline only on hover.</summary>
    private static bool Hovered(FakeIo io, int cx, int cy, int half = 28)
        => Math.Max(Math.Abs(io.Cursor.X - cx), Math.Abs(io.Cursor.Y - cy)) <= half;

    private static HoldStep ReachHold(int? maxMs = null, int x = 400, int y = 244)
        => Hold(maxMs, x: x, y: y) with { Reach = Reach80 };

    /// <summary>A shaft-sized box (cap raised 120 -> 240, "Block size is read every pass"): still
    /// just a bigger frame, counted the same way.</summary>
    private static readonly OutlineCheck Reach200 = new(new CheckBox(-100, -100, 200, 200), 60);

    [Fact]
    public async Task A_200x200_reach_box_counts_a_thin_white_frame_at_the_larger_size()
    {
        var log = new List<string>();
        var io = new FakeIo();
        io.Screen = Framed(400, 244, half: 85, shown: _ => Hovered(io, 400, 244, half: 85));
        var r = await StepRunner.RunAsync(new MacroStep[] { ReachHold(maxMs: 250) with { Reach = Reach200 } }, Ctx(log), io, default);

        Assert.Equal(PlaybackOutcome.Completed, r.Outcome);
        var down = Assert.Single(io.Downs);
        Assert.Equal((400, 244, ReachPressMs), (down.X, down.Y, down.TimestampMs));
        Assert.Contains("step 1 'Spot N' outline seen (680 near-white px over a baseline of 0, needs 60)", log);
    }

    [Theory]
    [InlineData(1, 224)]
    [InlineData(2, 440)]
    public async Task A_thin_white_frame_lets_the_hold_press(int thick, int count)
    {
        var log = new List<string>();
        var io = new FakeIo();
        io.Screen = Framed(400, 244, thick: thick, shown: _ => Hovered(io, 400, 244));
        var r = await StepRunner.RunAsync(new MacroStep[] { ReachHold(maxMs: 200) }, Ctx(log), io, default);

        Assert.Equal(PlaybackOutcome.Completed, r.Outcome);
        var down = Assert.Single(io.Downs);
        Assert.Equal((400, 244, ReachPressMs), (down.X, down.Y, down.TimestampMs)); // no grace spent: the outline was there
        Assert.Contains($"step 1 'Spot N' outline seen ({count} near-white px over a baseline of 0, needs 60)", log);
        Assert.Contains("step 1 'Spot N' pressed 0.2 s over 1 hold(s), released: reached its limit", log);
    }

    [Fact]
    public async Task Lava_without_a_white_frame_skips_the_hold_and_the_next_step_runs()
    {
        var log = new List<string>();
        var io = new FakeIo { Screen = (_, x, y) => LavaAt(x, y) };
        var r = await StepRunner.RunAsync(new MacroStep[] { ReachHold(maxMs: 10_000), new PointStep(0, "p9", null, 5, 5) }, Ctx(log), io, default);

        Assert.Equal(PlaybackOutcome.Completed, r.Outcome); // a skip, never a failure
        var down = Assert.Single(io.Downs);
        Assert.Equal((5, 5), (down.X, down.Y));
        Assert.Contains("step 1 'Spot N' no outline, skipped (0 near-white px over a baseline of 0, needs 60)", log);
        Assert.DoesNotContain(log, l => l.Contains("held"));
    }

    [Fact]
    public async Task Reach_waits_its_grace_for_the_outline_to_draw()
    {
        // Parked and counted by 150 ms, the pointer lands by 300; the game draws the outline at 450.
        // Checks run at 300, 400, 500.
        var log = new List<string>();
        var io = new FakeIo { Screen = Framed(400, 244, shown: t => t >= 450) };
        await StepRunner.RunAsync(new MacroStep[] { ReachHold(maxMs: 250) }, Ctx(log), io, default);

        Assert.Equal(500, Assert.Single(io.Downs).TimestampMs);
        Assert.Contains("step 1 'Spot N' outline seen (224 near-white px over a baseline of 0, needs 60)", log);
    }

    /// <summary>The game as the live tests found it: the outline shows only while the pointer is
    /// on the block and no button is held. <paramref name="frameAfter"/> gives the outline frame
    /// (centre and half-size) after a number of holds, or null for no outline: a block that broke
    /// with nothing lit behind it. A break that reveals the next block down returns a smaller
    /// frame somewhere else.</summary>
    private static FakeIo HoldBlock(Func<int, (int Cx, int Cy, int Half)?> frameAfter, Func<FakeIo, Rgb>? inside = null)
    {
        var io = new FakeIo();
        io.Screen = (t, x, y) =>
        {
            if (frameAfter(io.Ups.Count()) is not { } f) return Rock;
            var c = Framed(f.Cx, f.Cy, f.Half, shown: _ => Hovered(io, 400, 244) && !io.ButtonDown)(t, x, y);
            return c == Ore && inside is not null ? inside(io) : c;
        };
        return io;
    }

    /// <summary>One block at 400,244 that breaks after <paramref name="breaksAfter"/> holds and shows
    /// no outline from then on. Null never breaks.</summary>
    private static FakeIo LiveBlock(int? breaksAfter = null, Func<FakeIo, Rgb>? inside = null)
        => HoldBlock(holds => breaksAfter is { } n && holds >= n ? null : (400, 244, 28), inside);

    /// <summary>When a reach hold presses first: parked and counted at LookSettleMs, then the jump.</summary>
    private const long ReachPressMs = StepTiming.LookSettleMs + StepTiming.JumpWiggleMs;

    /// <summary>The growing holds of a block that never breaks: 300, 600, 1200, 2400, then 3000 each.</summary>
    private static readonly long[] HoldLadderMs = { 300, 600, 1200, 2400, 3000, 3000 };

    /// <summary>How long each press lasted, in order.</summary>
    private static long[] HeldMs(FakeIo io)
    {
        var ups = io.Ups.ToList();
        return io.Downs.Select((d, k) => ups[k].TimestampMs - d.TimestampMs).ToArray();
    }

    [Fact]
    public async Task A_block_that_breaks_on_the_first_hold_is_held_once_for_300_ms()
    {
        var log = new List<string>();
        var io = LiveBlock(breaksAfter: 1);
        var r = await StepRunner.RunAsync(new MacroStep[] { ReachHold() }, Ctx(log), io, default);

        Assert.Equal(PlaybackOutcome.Completed, r.Outcome);
        Assert.False(r.SkippedByReach); // it mined: a plain finish
        Assert.Equal(new long[] { 300 }, HeldMs(io));
        Assert.Equal(ReachPressMs, Assert.Single(io.Downs).TimestampMs);
        Assert.Empty(io.Released); // the hold let go itself
        Assert.Contains("step 1 'Spot N' broke after 1 hold(s), 0.3 s held", log);
        Assert.DoesNotContain(log, l => l.Contains("moved"));
    }

    [Fact]
    public async Task A_block_that_needs_four_holds_is_held_twice_as_long_each_time()
    {
        // Mining progress does not carry across releases on hard blocks, so each hit that leaves
        // the same box doubles the next hold: 300, 600, 1200, 2400, then broke.
        var log = new List<string>();
        var io = LiveBlock(breaksAfter: 4);
        var r = await StepRunner.RunAsync(new MacroStep[] { ReachHold() }, Ctx(log), io, default);

        Assert.Equal(PlaybackOutcome.Completed, r.Outcome);
        Assert.Equal(new long[] { 300, 600, 1200, 2400 }, HeldMs(io));
        Assert.Empty(io.Released);
        // Each look waits LookSettleMs after the release and sees the outline at once.
        var downs = io.Downs.Select(d => d.TimestampMs).ToArray();
        var ups = io.Ups.Select(u => u.TimestampMs).ToArray();
        for (int k = 1; k < downs.Length; k++) Assert.Equal(ups[k - 1] + StepTiming.LookSettleMs, downs[k]);
        Assert.Contains("step 1 'Spot N' broke after 4 hold(s), 4.5 s held", log);
        Assert.Single(log, l => l.Contains("outline seen")); // the pre-press check only, no line per look
    }

    [Fact]
    public async Task The_hold_stops_growing_at_its_cap_and_MaxMs_cuts_the_last()
    {
        // An unbreakable block with MaxMs 10000: 300, 600, 1200, 2400, 3000, then the last cut to
        // the 2500 left, and the step ends at the limit without a look.
        var log = new List<string>();
        var io = LiveBlock(); // never breaks
        var r = await StepRunner.RunAsync(new MacroStep[] { ReachHold(maxMs: 10_000) }, Ctx(log), io, default);

        Assert.Equal(PlaybackOutcome.Completed, r.Outcome);
        Assert.Equal(new long[] { 300, 600, 1200, 2400, 3000, 2500 }, HeldMs(io));
        Assert.Equal(io.Ups.Last().TimestampMs, io.NowMs); // no look after the limit
        Assert.Contains("step 1 'Spot N' pressed 10.0 s over 6 hold(s), released: reached its limit", log);
    }

    [Fact]
    public async Task Without_MaxMs_an_unbreakable_block_keeps_holding_at_the_cap()
    {
        // No time limit (decision 5): past 2400 every hold is the 3000 cap. Stopped from outside.
        var io = LiveBlock();
        using var cts = new CancellationTokenSource();
        io.OnDelay = () => { if (io.Ups.Count() >= HoldLadderMs.Length) cts.Cancel(); };
        var r = await StepRunner.RunAsync(new MacroStep[] { ReachHold() }, Ctx(), io, cts.Token);

        Assert.Equal("Playback cancelled.", r.Reason);
        Assert.Equal(HoldLadderMs, HeldMs(io).Take(HoldLadderMs.Length));
    }

    [Fact]
    public async Task A_break_that_reveals_the_block_below_ends_the_step_without_pressing_it()
    {
        // Hold 1 breaks the block; the next one down shows through the hole: a smaller outline
        // 40 px up, still inside the 80 px reach box.
        var log = new List<string>();
        var io = HoldBlock(holds => holds == 0 ? (400, 244, 28) : (400, 204, 20));
        var r = await StepRunner.RunAsync(new MacroStep[] { ReachHold() }, Ctx(log), io, default);

        Assert.Equal(PlaybackOutcome.Completed, r.Outcome);
        Assert.Equal(new long[] { 300 }, HeldMs(io));
        Assert.Contains("step 1 'Spot N' broke after 1 hold(s), 0.3 s held (outline moved to the next block)", log);
    }

    [Theory]
    [InlineData(6, 2, "broke after 2 hold(s), 0.9 s held")]                                   // jitter: the same block, hold again
    [InlineData(7, 1, "broke after 1 hold(s), 0.3 s held (outline moved to the next block)")] // past the tolerance: a new block
    public async Task An_outline_that_shifts_within_the_tolerance_is_the_same_block(int shift, int holds, string end)
    {
        var io = HoldBlock(t => t switch { 0 => (400, 244, 28), 1 => (400 + shift, 244, 28), _ => null });
        var log = new List<string>();
        await StepRunner.RunAsync(new MacroStep[] { ReachHold() }, Ctx(log), io, default);

        Assert.Equal(holds, io.Downs.Count());
        Assert.Contains($"step 1 'Spot N' {end}", log);
    }

    [Fact]
    public async Task Colour_drift_during_a_hold_does_not_end_a_reach_hold()
    {
        // A hit darkens the block while the button is down (pink to dark red on the live run), and
        // the outline vanishes the moment the button goes down; neither cuts a hold short.
        var log = new List<string>();
        var io = LiveBlock(breaksAfter: 2, inside: f => f.ButtonDown ? Rock : Ore);
        await StepRunner.RunAsync(new MacroStep[] { ReachHold() }, Ctx(log), io, default);

        Assert.Equal(new long[] { 300, 600 }, HeldMs(io));
        Assert.Contains("step 1 'Spot N' broke after 2 hold(s), 0.9 s held", log);
        Assert.DoesNotContain(log, l => l.Contains("colour moved"));
    }

    [Fact]
    public async Task Losing_the_foreground_mid_hold_aborts_and_releases_through_finally()
    {
        // Hold 3 goes down after holds of 300 and 600; the foreground goes 100 ms into it.
        var log = new List<string>();
        var io = LiveBlock();
        io.OnDelay = () => { if (io.Downs.Count() == 3 && io.NowMs >= io.Downs.Last().TimestampMs + 100) io.Foreground = false; };
        var r = await StepRunner.RunAsync(new MacroStep[] { ReachHold() }, Ctx(log), io, default);

        Assert.Equal(PlaybackOutcome.Aborted, r.Outcome);
        Assert.Equal("Foreground shifted away from CElCPapa at step 1/1.", r.Reason);
        Assert.Null(r.StepIndex);
        Assert.Equal(new[] { 1 }, io.Released);
        Assert.Contains("step 1 'Spot N' pressed 1.0 s over 3 hold(s), then the playback ended", log);
    }

    [Fact]
    public async Task A_capture_failure_on_a_look_stops_could_not_see_the_window()
    {
        var log = new List<string>();
        var io = LiveBlock();
        io.OnDelay = () => { if (io.Ups.Any()) io.CaptureWorks = false; };
        var r = await StepRunner.RunAsync(new MacroStep[] { ReachHold(), new PointStep(0, "p9", null, 5, 5) }, Ctx(log), io, default);

        Assert.Equal("CElCPapa: step 1 'Spot N' could not see the window.", r.Reason);
        Assert.Equal(0, r.StepIndex);
        Assert.Single(io.Downs); // the first hold, then the look failed
        Assert.Empty(io.Released); // the button was already up
        Assert.DoesNotContain(log, l => l.Contains("outline gone"));
    }

    [Fact]
    public async Task Losing_the_foreground_during_the_reach_check_aborts_rather_than_skips()
    {
        var log = new List<string>();
        var io = new FakeIo { Screen = (_, x, y) => LavaAt(x, y) };
        io.OnDelay = () => { if (io.NowMs >= ReachPressMs) io.Foreground = false; }; // right after the jump
        var r = await StepRunner.RunAsync(new MacroStep[] { ReachHold(), new PointStep(0, "p9", null, 5, 5) }, Ctx(log), io, default);

        Assert.Equal(PlaybackOutcome.Aborted, r.Outcome);
        Assert.Equal("Foreground shifted away from CElCPapa at step 1/2.", r.Reason);
        Assert.Null(r.StepIndex);
        Assert.Empty(io.Downs);
        Assert.DoesNotContain(log, l => l.Contains("skipped"));
    }

    [Fact]
    public async Task A_reach_check_that_cannot_see_the_window_stops_instead_of_skipping()
    {
        var log = new List<string>();
        var io = new FakeIo { CaptureWorks = false };
        var r = await StepRunner.RunAsync(new MacroStep[] { ReachHold(), new PointStep(0, "p9", null, 5, 5) }, Ctx(log), io, default);

        Assert.Equal("CElCPapa: step 1 'Spot N' could not see the window.", r.Reason);
        Assert.Equal(0, r.StepIndex);
        Assert.Empty(io.Downs);
        Assert.DoesNotContain(log, l => l.Contains("skipped"));
    }

    // ---------- reach baseline (controller ruling 2026-09-28) ----------

    private static readonly Rgb Quartz = new(236, 236, 240); // white quartz: every channel at 225 or more

    /// <summary>A white quartz block at 400,244: quartz inside (55x55 = 3025 near-white px with no
    /// hover), the hover outline 1 px at 28 out (224 px, only while the pointer is on the block and
    /// no button is down, and only when <paramref name="outlined"/>), rock beyond. After
    /// <paramref name="breaksAfter"/> releases the block is gone: rock.</summary>
    private static FakeIo QuartzBlock(bool outlined, int? breaksAfter = null)
    {
        var io = new FakeIo();
        io.Screen = (_, x, y) =>
        {
            if (breaksAfter is { } n && io.Ups.Count() >= n) return Rock;
            var m = Math.Max(Math.Abs(x - 400), Math.Abs(y - 244));
            if (m > 28) return Rock;
            if (m == 28) return outlined && Hovered(io, 400, 244) && !io.ButtonDown ? White : Rock;
            return Quartz;
        };
        return io;
    }

    [Fact]
    public async Task White_quartz_without_an_outline_is_skipped_against_its_baseline()
    {
        // 3025 near-white px is far over minCount 60, but all of it is baseline: out of reach, no press.
        var log = new List<string>();
        var io = QuartzBlock(outlined: false);
        var r = await StepRunner.RunAsync(new MacroStep[] { ReachHold(maxMs: 10_000) }, Ctx(log), io, default);

        Assert.Equal(PlaybackOutcome.Completed, r.Outcome);
        Assert.True(r.SkippedByReach);
        Assert.Empty(io.Downs);
        Assert.Contains("step 1 'Spot N' no outline, skipped (3025 near-white px over a baseline of 3025, needs 60)", log);
    }

    [Fact]
    public async Task White_quartz_with_an_outline_presses_and_ends_when_the_block_breaks()
    {
        var log = new List<string>();
        var io = QuartzBlock(outlined: true, breaksAfter: 2);
        var r = await StepRunner.RunAsync(new MacroStep[] { ReachHold() }, Ctx(log), io, default);

        Assert.Equal(PlaybackOutcome.Completed, r.Outcome);
        Assert.False(r.SkippedByReach);
        Assert.Equal(2, io.Downs.Count());
        Assert.Contains("step 1 'Spot N' outline seen (3249 near-white px over a baseline of 3025, needs 60)", log);
        Assert.Contains("step 1 'Spot N' broke after 2 hold(s), 0.9 s held", log);
    }

    [Fact]
    public async Task The_baseline_is_counted_with_the_pointer_parked_on_the_title_bar()
    {
        // The live finding: the spot one box-width away lands on the neighbouring block, which
        // lights up with its outline edge inside this block's box (a 3 px white line, 240 px).
        // Counted there, the baseline swallowed the hover outline and the hold skipped. The title
        // bar lights nothing.
        var log = new List<string>();
        var io = new FakeIo();
        var own = Framed(400, 244, shown: _ => Hovered(io, 400, 244) && !io.ButtonDown);
        io.Screen = (t, x, y) =>
            Hovered(io, 520, 244) && x >= 436 && x <= 438 && y >= 204 && y <= 283 ? White : own(t, x, y);
        io.Cursor = (410, 250); // already on the block, as the old move-away assumed
        var r = await StepRunner.RunAsync(new MacroStep[] { ReachHold(maxMs: 250) }, Ctx(log), io, default);

        Assert.Equal(PlaybackOutcome.Completed, r.Outcome);
        // Client centre x, 12 px above the client area, at the start; the count waits LookSettleMs.
        Assert.Equal((400, -12, 0L), Assert.Single(io.Parks));
        Assert.Equal(((360, 204, 80, 80), (long)StepTiming.LookSettleMs), (io.CaptureRects[0], io.CaptureTimes[0]));
        Assert.DoesNotContain(io.Sent, e => e.Y < 0); // the park is a move only, never a click
        Assert.Contains("step 1 'Spot N' outline seen (224 near-white px over a baseline of 0, needs 60)", log);
        Assert.Equal(ReachPressMs, Assert.Single(io.Downs).TimestampMs);
    }

    [Fact]
    public async Task A_window_with_no_title_bar_takes_the_baseline_beside_the_box()
    {
        // A borderless window has no frame above its client area, so the park refuses. The pointer
        // is on the block, so it moves one box-width right of the 80 px box (x 360..439, so x 520 at
        // middle height 244), waits LookSettleMs, then counts.
        var log = new List<string>();
        var io = LiveBlock(breaksAfter: 1);
        io.ParkWorks = false;
        io.Cursor = (410, 250);
        await StepRunner.RunAsync(new MacroStep[] { ReachHold() }, Ctx(log), io, default);

        Assert.Single(io.Parks);
        var first = io.Sent[0];
        Assert.Equal((MacroEventKind.MouseMove, 520, 244, 0L), (first.Kind, first.X, first.Y, first.TimestampMs));
        Assert.Equal(((360, 204, 80, 80), (long)StepTiming.LookSettleMs), (io.CaptureRects[0], io.CaptureTimes[0]));
        Assert.Equal(ReachPressMs, io.Downs.Single().TimestampMs);
        Assert.Contains("step 1 'Spot N' could not park the pointer on the title bar; took the baseline beside the box", log);
        Assert.Contains("step 1 'Spot N' outline seen (224 near-white px over a baseline of 0, needs 60)", log);
    }

    [Fact]
    public async Task The_park_checks_the_foreground_before_it_moves()
    {
        // The foreground goes during the hold's own delay, after the step-boundary check.
        var io = LiveBlock();
        io.OnDelay = () => { if (io.NowMs >= 100) io.Foreground = false; };
        var r = await StepRunner.RunAsync(new MacroStep[] { ReachHold() with { DelayMs = 100 } }, Ctx(), io, default);

        Assert.Equal(PlaybackOutcome.Aborted, r.Outcome);
        Assert.Equal("Foreground shifted away from CElCPapa at step 1/1.", r.Reason);
        Assert.Empty(io.Parks);
    }

    [Fact]
    public async Task A_reach_box_outside_the_window_refuses_before_any_input()
    {
        // The colour box at (398,28) fits; the 80x80 reach box would start at y = -10.
        var io = new FakeIo();
        var r = await StepRunner.RunAsync(new MacroStep[] { ReachHold(x: 400, y: 30) }, Ctx(), io, default);
        Assert.Equal("CElCPapa: step 1 'Spot N' checks a reach box outside the window.", r.Reason);
        Assert.Equal(0, r.StepIndex);
        Assert.Empty(io.Sent);
    }

    [Fact]
    public async Task Reach_scales_with_the_window()
    {
        // (400,244) in 800x599 lands at (500,305) in 1000x749; the box grows to 100x100 and the
        // threshold to 75. The frame is drawn at the scaled size (35 px out, 280 px).
        var log = new List<string>();
        var io = new FakeIo { Client = (1000, 749) };
        io.Screen = Framed(500, 305, half: 35, shown: _ => Hovered(io, 500, 305, 35));
        await StepRunner.RunAsync(new MacroStep[] { ReachHold(maxMs: 250) }, Ctx(log, actual: (1000, 749)), io, default);

        Assert.Contains((450, 255, 100, 100), io.CaptureRects);
        Assert.Equal((500, 305), (io.Downs.Single().X, io.Downs.Single().Y));
        Assert.Contains("step 1 'Spot N' outline seen (280 near-white px over a baseline of 0, needs 75)", log);
    }

    [Fact]
    public async Task A_point_with_an_outline_presses_once_without_a_second_jump()
    {
        var log = new List<string>();
        var io = new FakeIo { Screen = Framed(400, 244) };
        var r = await StepRunner.RunAsync(new MacroStep[] { new PointStep(0, "p1", "Ore", 400, 244, Reach: Reach80) }, Ctx(log), io, default);

        Assert.Equal(PlaybackOutcome.Completed, r.Outcome);
        var down = Assert.Single(io.Downs);
        Assert.Equal((400, 244, 150L), (down.X, down.Y, down.TimestampMs));
        Assert.Equal(150 + StepTiming.PressHoldMs, Assert.Single(io.Sent, e => e.Kind == MacroEventKind.MouseUp).TimestampMs);
        Assert.Equal(3, io.Sent.Count(e => e.Kind == MacroEventKind.MouseMove)); // one jump, not two
        Assert.Contains("step 1 'Ore' outline seen (224 near-white px, needs 60)", log);
    }

    [Fact]
    public async Task A_point_without_an_outline_is_skipped()
    {
        var log = new List<string>();
        var io = new FakeIo { Screen = (_, x, y) => LavaAt(x, y) };
        var r = await StepRunner.RunAsync(new MacroStep[]
        {
            new PointStep(0, "p1", "Ore", 400, 244, Reach: Reach80),
            new PointStep(0, "p9", null, 5, 5),
        }, Ctx(log), io, default);

        Assert.Equal(PlaybackOutcome.Completed, r.Outcome);
        Assert.Equal((5, 5), (io.Downs.Single().X, io.Downs.Single().Y));
        Assert.Contains("step 1 'Ore' no outline, skipped (0 near-white px, needs 60)", log);
    }

    [Fact]
    public async Task A_point_reach_box_outside_the_window_refuses_before_any_input()
    {
        var io = new FakeIo();
        var r = await StepRunner.RunAsync(new MacroStep[] { new PointStep(0, "p1", "Ore", 400, 30, Reach: Reach80) }, Ctx(), io, default);
        Assert.Equal("CElCPapa: step 1 'Ore' checks a reach box outside the window.", r.Reason);
        Assert.Equal(0, r.StepIndex);
        Assert.Empty(io.Sent);
    }

    // ---------- a run that pressed nothing ----------

    [Fact]
    public async Task A_run_whose_every_press_was_skipped_by_reach_completes_as_skipped()
    {
        var io = new FakeIo { Screen = (_, x, y) => LavaAt(x, y) };
        var r = await StepRunner.RunAsync(new MacroStep[]
        {
            ReachHold(),
            new PointStep(0, "p1", "Ore", 456, 300, Reach: Reach80),
            new WaitStep(100),
        }, Ctx(), io, default);

        Assert.Equal(PlaybackOutcome.Completed, r.Outcome);
        Assert.True(r.SkippedByReach);
        Assert.Null(r.Reason);
        Assert.Empty(io.Downs);
    }

    [Theory]
    [InlineData("key")]
    [InlineData("point")]
    [InlineData("mined")]
    [InlineData("raw")]
    [InlineData("drag")]
    public async Task A_run_that_pressed_anything_is_a_plain_finish(string kind)
    {
        var io = new FakeIo();
        io.Screen = kind == "mined" ? Framed(400, 244, shown: _ => Hovered(io, 400, 244)) : (_, x, y) => LavaAt(x, y);
        var steps = kind switch
        {
            "key" => new MacroStep[] { ReachHold(), new KeyStep(0, 0x41, true), new KeyStep(0, 0x41, false) },
            "point" => new MacroStep[] { ReachHold(), new PointStep(0, "p9", null, 5, 5) },
            // RawStep and DragStep press through the same PressWatchIo wrapper as every other
            // step kind (Send/ReleaseButton), with no per-kind bookkeeping in the "pressed
            // anything" theory. Both come after a reach skip, on the lava screen, so the only
            // press in the run is the raw/drag one (I2, controller ruling 2026-09-28).
            "raw" => new MacroStep[] { ReachHold(), new RawStep(0, new[]
            {
                new MacroEvent(0, MacroEventKind.KeyDown, 0x41, 0, 0, 0, 0),
                new MacroEvent(50, MacroEventKind.KeyUp, 0x41, 0, 0, 0, 0),
            }, "test") },
            "drag" => new MacroStep[] { ReachHold(), new DragStep(0, 1, 5, 5, 10, 0, 50) },
            _ => new MacroStep[] { ReachHold(maxMs: 500) },
        };
        var r = await StepRunner.RunAsync(steps, Ctx(), io, default);

        Assert.Equal(PlaybackOutcome.Completed, r.Outcome);
        Assert.False(r.SkippedByReach);
    }

    [Fact]
    public async Task A_run_with_no_reach_skip_is_a_plain_finish_even_if_it_pressed_nothing()
    {
        var r = await StepRunner.RunAsync(new MacroStep[] { new WaitStep(100) }, Ctx(), new FakeIo(), default);
        Assert.Equal(PlaybackOutcome.Completed, r.Outcome);
        Assert.False(r.SkippedByReach);
    }

    // ---------- first match: skipIfOther ----------

    private static readonly Rgb DotGreen = new(125, 246, 13);
    private static readonly Rgb DotRed = new(255, 19, 90);

    /// <summary>"Auto Mine off (checked)": press the pickaxe only when the dot is green.</summary>
    private static FirstMatchStep AutoMineOff() => new(0, "am-off", "Auto Mine off", new[]
    {
        new PointStep(0, "am-off-dot", "dot", 40, 300, Check: new ColorCheck(new CheckBox(15, -11, 3, 3), DotGreen, DotRed)),
    }, NoMatchAction.SkipIfOther);

    [Fact]
    public async Task SkipIfOther_skips_when_every_candidate_shows_its_other_state()
    {
        // Auto Mine is already off: the dot is red. Skip at once and carry on.
        var io = new FakeIo { Screen = (_, _, _) => DotRed };
        var r = await StepRunner.RunAsync(new MacroStep[] { AutoMineOff(), new PointStep(0, "p9", null, 5, 5) }, Ctx(), io, default);
        Assert.Equal(PlaybackOutcome.Completed, r.Outcome);
        var down = Assert.Single(io.Downs);
        Assert.Equal((5, 5), (down.X, down.Y));
        Assert.True(down.TimestampMs < StepTiming.CheckGraceMs); // no wait for the ceiling
    }

    [Fact]
    public async Task SkipIfOther_presses_when_the_candidate_matches()
    {
        var io = new FakeIo { Screen = (_, _, _) => DotGreen };
        var r = await StepRunner.RunAsync(new MacroStep[] { AutoMineOff() }, Ctx(), io, default);
        Assert.Equal(PlaybackOutcome.Completed, r.Outcome);
        Assert.Equal((40, 300), (io.Downs.Single().X, io.Downs.Single().Y));
    }

    [Fact]
    public async Task SkipIfOther_stops_when_a_candidate_shows_neither_state()
    {
        // A popup or captcha dims the screen: the dot is neither red nor green. Nothing after
        // this step may run, because the next step in a mine macro holds the middle of the screen.
        var io = new FakeIo { Screen = (_, _, _) => DarkBlue };
        var r = await StepRunner.RunAsync(new MacroStep[] { AutoMineOff(), new PointStep(0, "p9", null, 5, 5) }, Ctx(), io, default);
        Assert.Equal(PlaybackOutcome.Aborted, r.Outcome);
        Assert.Equal(0, r.StepIndex);
        Assert.StartsWith("CElCPapa: step 1 'Auto Mine off' found no candidate that matched after", r.Reason);
        Assert.Empty(io.Downs);
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

    // ---------- ClearAt (the bridge call's synthetic reach holds) ----------

    private static IReadOnlyList<MacroStep> ClearAtSteps(int? maxMsPerPoint, params (int X, int Y, string Label)[] points)
        => ClearAtMacro.Build(new ClearAtRequest(
            "1.0", "ClearAt", "626labs.ur-ocr", "123456789", new ClearAtClient(800, 599),
            points.Select(p => new ClearAtPoint(p.X, p.Y, p.Label)).ToList(),
            new ClearAtOutline(80, 80, 60), maxMsPerPoint), "clearat-test").Steps!;

    [Fact]
    public async Task ClearAt_steps_mine_an_outlined_point_and_skip_one_out_of_reach()
    {
        var log = new List<string>();
        var io = LiveBlock(breaksAfter: 2); // an outlined block at 400,244; rock everywhere else
        var r = await StepRunner.RunAsync(ClearAtSteps(null, (400, 244, "ore 1"), (150, 150, "stone 2")), Ctx(log), io, default);

        Assert.Equal(PlaybackOutcome.Completed, r.Outcome);
        Assert.False(r.SkippedByReach); // one point mined: a plain finish
        Assert.Equal(2, io.Downs.Count());
        Assert.All(io.Downs, d => Assert.Equal((400, 244), (d.X, d.Y)));
        Assert.Contains("step 1 'ore 1' broke after 2 hold(s), 0.9 s held", log);
        Assert.Contains("step 2 'stone 2' no outline, skipped (0 near-white px over a baseline of 0, needs 60)", log);
    }

    [Fact]
    public async Task ClearAt_steps_that_all_miss_the_outline_read_skipped_by_reach()
    {
        var io = new FakeIo { Screen = (_, x, y) => LavaAt(x, y) };
        var r = await StepRunner.RunAsync(ClearAtSteps(null, (400, 244, "ore 1"), (500, 300, "stone 2")), Ctx(), io, default);

        Assert.Equal(PlaybackOutcome.Completed, r.Outcome);
        Assert.True(r.SkippedByReach);
        Assert.Empty(io.Downs);
    }

    [Fact]
    public async Task ClearAt_points_and_box_scale_from_the_measured_client_to_the_live_one()
    {
        // Display-scale slack: measured at 800x599, played at 1000x749. 400,244 lands at 500,305,
        // the 80 px box grows to 100 and the threshold to 75. The frame is drawn at the scaled size
        // (35 px out) and only on hover, so the baseline is 0.
        var log = new List<string>();
        var io = new FakeIo { Client = (1000, 749) };
        io.Screen = Framed(500, 305, half: 35, shown: _ => Hovered(io, 500, 305, 35));
        var r = await StepRunner.RunAsync(ClearAtSteps(250, (400, 244, "ore 1")), Ctx(log, actual: (1000, 749)), io, default);

        Assert.Equal(PlaybackOutcome.Completed, r.Outcome);
        Assert.Contains((450, 255, 100, 100), io.CaptureRects);
        Assert.Equal((500, 305), (io.Downs.Single().X, io.Downs.Single().Y));
        Assert.Contains("step 1 'ore 1' outline seen (280 near-white px over a baseline of 0, needs 75)", log);
    }

    // ---------- ClearAt: one shared baseline per pass (live finding 2026-09-28 21:00) ----------

    /// <summary>Five points in a row, 100 px apart. Their 80 px boxes span x 60..539, y 60..139.</summary>
    private static readonly (int X, int Y, string Label)[] Row5 =
        { (100, 100, "a"), (200, 100, "b"), (300, 100, "c"), (400, 100, "d"), (500, 100, "e") };
    private static readonly (int X, int Y, int W, int H) Row5Union = (60, 60, 480, 80);

    /// <summary>Blocks centred on the given points, each with the hover outline of
    /// <see cref="Framed"/> (only while the pointer is on it and no button is down). Each breaks
    /// after its own number of holds (null never does) and is rock from then on; rock everywhere
    /// else.</summary>
    private static FakeIo Blocks(params (int Cx, int Cy, int? BreaksAfter)[] blocks)
    {
        var io = new FakeIo();
        io.Screen = (t, x, y) =>
        {
            foreach (var b in blocks)
            {
                if (Math.Max(Math.Abs(x - b.Cx), Math.Abs(y - b.Cy)) > 28) continue;
                if (b.BreaksAfter is { } n && io.Ups.Count(u => (u.X, u.Y) == (b.Cx, b.Cy)) >= n) return Rock;
                return Framed(b.Cx, b.Cy, shown: _ => Hovered(io, b.Cx, b.Cy) && !io.ButtonDown)(t, x, y);
            }
            return Rock;
        };
        return io;
    }

    [Fact]
    public async Task A_ClearAt_pass_that_skips_every_point_parks_and_counts_the_background_once()
    {
        var log = new List<string>();
        var io = Blocks(); // nothing in reach
        var r = await StepRunner.RunAsync(ClearAtSteps(null, Row5), Ctx(log, shared: true), io, default);

        Assert.Equal(PlaybackOutcome.Completed, r.Outcome);
        Assert.True(r.SkippedByReach);
        Assert.Empty(io.Downs);
        Assert.Equal((400, -12, 0L), Assert.Single(io.Parks));
        // One capture of the union of the boxes, at LookSettleMs; every other capture is a point's own look.
        Assert.Equal((Row5Union, (long)StepTiming.LookSettleMs), (io.CaptureRects[0], io.CaptureTimes[0]));
        Assert.Single(io.CaptureRects, c => c == Row5Union);
        Assert.All(io.CaptureRects.Skip(1), c => Assert.Equal((80, 80), (c.W, c.H)));
        Assert.Contains("step 5 'e' no outline, skipped (0 near-white px over a baseline of 0, needs 60)", log);
        Assert.Contains("ClearAt: shared baseline (1 park for 5 points)", log);
    }

    [Fact]
    public async Task A_ClearAt_pass_parks_again_only_after_a_break()
    {
        // Points 1 and 3 break (after 1 and 2 holds); 2, 4 and 5 are out of reach. Parks: the start,
        // after the break at 1, after the break at 3. A break at the last point would need none.
        var log = new List<string>();
        var io = Blocks((100, 100, 1), (300, 100, 2));
        var r = await StepRunner.RunAsync(ClearAtSteps(null, Row5), Ctx(log, shared: true), io, default);

        Assert.Equal(PlaybackOutcome.Completed, r.Outcome);
        Assert.Equal(3, io.Downs.Count());
        Assert.Equal(3, io.Parks.Count);
        Assert.Equal(3, io.CaptureRects.Count(c => c == Row5Union));
        var ups = io.Ups.Select(u => u.TimestampMs).ToArray();
        Assert.True(io.Parks[1].T > ups[0]);  // after point 1 broke
        Assert.True(io.Parks[2].T > ups[2]);  // after point 3 broke, not after its first hold
        Assert.Contains("step 1 'a' broke after 1 hold(s), 0.3 s held", log);
        Assert.Contains("step 3 'c' broke after 2 hold(s), 0.9 s held", log);
        Assert.Contains("ClearAt: shared baseline (3 parks for 5 points)", log);
    }

    [Fact]
    public async Task The_shared_baseline_still_skips_white_quartz_without_an_outline()
    {
        var log = new List<string>();
        var io = QuartzBlock(outlined: false);
        var r = await StepRunner.RunAsync(ClearAtSteps(10_000, (400, 244, "quartz 1"), (150, 150, "stone 2")),
            Ctx(log, shared: true), io, default);

        Assert.True(r.SkippedByReach);
        Assert.Empty(io.Downs);
        Assert.Single(io.Parks);
        Assert.Contains("step 1 'quartz 1' no outline, skipped (3025 near-white px over a baseline of 3025, needs 60)", log);
        Assert.Contains("step 2 'stone 2' no outline, skipped (0 near-white px over a baseline of 0, needs 60)", log);
    }

    [Fact]
    public async Task White_quartz_with_an_outline_presses_against_the_shared_baseline()
    {
        var log = new List<string>();
        var io = QuartzBlock(outlined: true, breaksAfter: 1);
        var r = await StepRunner.RunAsync(ClearAtSteps(null, (400, 244, "quartz 1")), Ctx(log, shared: true), io, default);

        Assert.False(r.SkippedByReach);
        Assert.Single(io.Downs);
        Assert.Contains("step 1 'quartz 1' outline seen (3249 near-white px over a baseline of 3025, needs 60)", log);
    }

    [Fact]
    public async Task Recorded_reach_holds_still_park_before_every_step()
    {
        var log = new List<string>();
        var io = Blocks();
        var r = await StepRunner.RunAsync(new MacroStep[]
            { ReachHold(), ReachHold(x: 150, y: 150) with { Id = "spot-2" }, ReachHold(x: 600, y: 400) with { Id = "spot-3" } },
            Ctx(log), io, default);

        Assert.True(r.SkippedByReach);
        Assert.Equal(3, io.Parks.Count);
        Assert.DoesNotContain(log, l => l.StartsWith("ClearAt:"));
    }

    [Fact]
    public async Task A_shared_pass_with_the_park_refused_takes_the_frame_beside_every_box()
    {
        // Borderless: the park refuses. The pointer is inside the union of the boxes (x 60..539), so
        // it moves one box-width right of the union (539 + 1 + 80 = 620) at its middle height (100).
        var log = new List<string>();
        var io = Blocks();
        io.ParkWorks = false;
        io.Cursor = (210, 100);
        await StepRunner.RunAsync(ClearAtSteps(null, Row5), Ctx(log, shared: true), io, default);

        Assert.Single(io.Parks);
        Assert.Equal((MacroEventKind.MouseMove, 620, 100, 0L), (io.Sent[0].Kind, io.Sent[0].X, io.Sent[0].Y, io.Sent[0].TimestampMs));
        Assert.Equal((Row5Union, (long)StepTiming.LookSettleMs), (io.CaptureRects[0], io.CaptureTimes[0]));
        Assert.Single(log, l => l.Contains("could not park the pointer on the title bar"));
        Assert.Contains("ClearAt: shared baseline (1 park for 5 points)", log);
    }

    [Theory]
    [InlineData(12, 1)]
    [InlineData(13, 2)] // the 13th point without a break takes a fresh frame
    [InlineData(24, 2)]
    [InlineData(25, 3)]
    public async Task A_shared_pass_refreshes_its_frame_every_12_points_without_a_break(int points, int parks)
    {
        // A grid 100 px apart, 7 across, rows at y 100, 200, 300, 400. Nothing in reach.
        var grid = Enumerable.Range(0, points).Select(k => (100 + k % 7 * 100, 100 + k / 7 * 100, $"p{k + 1}")).ToArray();
        var log = new List<string>();
        var io = Blocks();
        await StepRunner.RunAsync(ClearAtSteps(null, grid), Ctx(log, shared: true), io, default);

        Assert.Equal(parks, io.Parks.Count);
        Assert.Contains($"ClearAt: shared baseline ({parks} parks for {points} points)".Replace("(1 parks", "(1 park"), log);
    }

    [Fact]
    public async Task A_long_hold_checks_again_on_a_fresh_baseline_and_stops_when_the_outline_was_the_background()
    {
        // The shared frame is taken at LookSettleMs over plain rock. Right after, a quartz-white patch
        // (20x20 = 400 px) shows inside the box and stays; there is no outline at all. Against the
        // stale baseline of 0 it reads as an outline that never breaks. Before the 3rd hold the
        // point parks again: the fresh baseline is 400, nothing rises over it, and the point stops.
        var log = new List<string>();
        var io = new FakeIo { Screen = (t, x, y) => t > StepTiming.LookSettleMs && x is >= 60 and < 80 && y is >= 60 and < 80 ? Quartz : Rock };
        // MaxMs bounds a regression (it would press to the limit) so it fails instead of hanging.
        var r = await StepRunner.RunAsync(ClearAtSteps(20_000, (100, 100, "a")), Ctx(log, shared: true), io, default);

        Assert.Equal(PlaybackOutcome.Completed, r.Outcome);
        Assert.False(r.SkippedByReach); // it pressed: not a skip
        Assert.Equal(new long[] { 300, 600 }, HeldMs(io));
        Assert.Equal(2, io.Parks.Count);
        Assert.True(io.Parks[1].T > io.Ups.Last().TimestampMs);
        Assert.Contains("step 1 'a' outline seen (400 near-white px over a baseline of 0, needs 60)", log);
        Assert.Contains("step 1 'a' no outline on a fresh baseline, stopped", log);
        Assert.DoesNotContain(log, l => l.Contains("broke"));
    }

    [Fact]
    public async Task A_long_hold_that_still_shows_its_outline_on_a_fresh_baseline_keeps_holding()
    {
        var log = new List<string>();
        var io = Blocks((100, 100, 4));
        await StepRunner.RunAsync(ClearAtSteps(null, (100, 100, "a")), Ctx(log, shared: true), io, default);

        Assert.Equal(new long[] { 300, 600, 1200, 2400 }, HeldMs(io));
        Assert.Equal(2, io.Parks.Count); // the start, then the check before hold 3 only
        Assert.Contains("step 1 'a' broke after 4 hold(s), 4.5 s held", log);
    }

    [Fact]
    public async Task A_shared_pass_keeps_MaxMs_and_reaching_the_limit_is_not_a_break()
    {
        var log = new List<string>();
        var io = Blocks((100, 100, null), (200, 100, null));
        var r = await StepRunner.RunAsync(ClearAtSteps(10_000, (100, 100, "a"), (200, 100, "b")), Ctx(log, shared: true), io, default);

        Assert.Equal(PlaybackOutcome.Completed, r.Outcome);
        Assert.Equal(new long[] { 300, 600, 1200, 2400, 3000, 2500, 300, 600, 1200, 2400, 3000, 2500 }, HeldMs(io));
        Assert.Equal(3, io.Parks.Count); // the start and one check before each point's 3rd hold; the limit is no break
        Assert.Contains("step 1 'a' pressed 10.0 s over 6 hold(s), released: reached its limit", log);
        Assert.Contains("step 2 'b' pressed 10.0 s over 6 hold(s), released: reached its limit", log);
    }

    [Fact]
    public async Task A_shared_pass_that_loses_the_foreground_mid_hold_aborts_and_releases()
    {
        var log = new List<string>();
        var io = Blocks((100, 100, null));
        io.OnDelay = () => { if (io.Downs.Count() == 2 && io.ButtonDown) io.Foreground = false; };
        var r = await StepRunner.RunAsync(ClearAtSteps(null, (100, 100, "a"), (200, 100, "b")), Ctx(log, shared: true), io, default);

        Assert.Equal(PlaybackOutcome.Aborted, r.Outcome);
        Assert.Equal("Foreground shifted away from CElCPapa at step 1/2.", r.Reason);
        Assert.Equal(new[] { 1 }, io.Released);
        Assert.Contains("ClearAt: shared baseline (1 park for 2 points)", log);
    }

    [Fact]
    public async Task A_shared_pass_is_cancelled_like_any_playback()
    {
        var log = new List<string>();
        var io = Blocks((100, 100, 1), (200, 100, null));
        using var cts = new CancellationTokenSource();
        io.OnDelay = () => { if (io.Downs.Count() == 2 && io.ButtonDown) cts.Cancel(); };
        var r = await StepRunner.RunAsync(ClearAtSteps(null, (100, 100, "a"), (200, 100, "b")), Ctx(log, shared: true), io, cts.Token);

        Assert.Equal("Playback cancelled.", r.Reason);
        Assert.Equal(new[] { 1 }, io.Released);
        Assert.Equal(2, io.Parks.Count); // the start, then the refresh after 'a' broke
        Assert.Contains("ClearAt: shared baseline (2 parks for 2 points)", log);
    }

    // ---------- ClearAt: the guard (live safety bug 2026-09-28 23:49) ----------

    /// <summary>The Auto Mine dot while clearing (Auto Mine off, so red), 3x3 at 55,289.</summary>
    private static readonly ScreenGuard Guard = new(55, 289, 3, 3, DotRed, 30);

    private const string GuardStop = "ClearAt stopped: the guard at (55,289) isn't the expected colour (saw #F5F5F5); "
        + "something may be over the game (a menu or a player's profile).";

    /// <summary>Paints the guard spot over <paramref name="io"/>'s screen: the red dot while
    /// <paramref name="red"/> holds, white (a popup over the game) once it does not.</summary>
    private static FakeIo Guarded(FakeIo io, Func<FakeIo, bool> red)
    {
        var under = io.Screen;
        io.Screen = (t, x, y) => x is >= 55 and < 58 && y is >= 289 and < 292 ? (red(io) ? DotRed : White) : under(t, x, y);
        return io;
    }

    /// <summary>True when nothing at all was sent after the last button release.</summary>
    private static bool NothingSentAfterTheLastRelease(FakeIo io)
        => io.Sent.Count > 0 && io.Sent[^1].Kind == MacroEventKind.MouseUp && !io.ButtonDown;

    [Fact]
    public async Task A_guard_that_keeps_its_colour_lets_the_pass_run_as_before()
    {
        var log = new List<string>();
        var io = Guarded(Blocks((100, 100, 1), (300, 100, 2)), _ => true);
        var r = await StepRunner.RunAsync(ClearAtSteps(null, Row5), Ctx(log, shared: true, guard: Guard), io, default);

        Assert.Equal(PlaybackOutcome.Completed, r.Outcome);
        Assert.Equal(3, io.Downs.Count());
        Assert.Contains("step 1 'a' broke after 1 hold(s), 0.3 s held", log);
        Assert.Contains("step 3 'c' broke after 2 hold(s), 0.9 s held", log);
        Assert.Contains("ClearAt: shared baseline (3 parks for 5 points)", log);
        // Sampled at every point and before every hold: 5 points, 3 holds.
        Assert.True(io.CaptureRects.Count(c => c == (55, 289, 3, 3)) >= 8);
    }

    [Fact]
    public async Task A_guard_that_turns_white_before_point_3_stops_with_no_input_after_it()
    {
        var log = new List<string>();
        var io = Guarded(Blocks((100, 100, 1), (200, 100, 1), (300, 100, 1)), io => io.Ups.Count() < 2);
        var r = await StepRunner.RunAsync(ClearAtSteps(null, Row5), Ctx(log, shared: true, guard: Guard), io, default);

        Assert.Equal(PlaybackOutcome.Aborted, r.Outcome);
        Assert.Equal(2, r.StepIndex); // a check failure at step 3: check-failed on the wire
        Assert.Equal(GuardStop, r.Reason);
        Assert.Equal(new[] { (100, 100), (200, 100) }, io.Downs.Select(d => (d.X, d.Y)));
        Assert.True(NothingSentAfterTheLastRelease(io)); // point 3 never moved, hovered or pressed
        Assert.Equal((200, 100), (io.Sent[^1].X, io.Sent[^1].Y));
        Assert.Equal(2, io.Parks.Count); // point 3 never parked either
        Assert.Empty(io.Released);
    }

    [Fact]
    public async Task A_guard_that_fails_between_holds_stops_before_the_next_hold()
    {
        var log = new List<string>();
        var io = Guarded(Blocks((100, 100, null)), io => io.Ups.Count() < 1);
        var r = await StepRunner.RunAsync(ClearAtSteps(null, (100, 100, "a"), (200, 100, "b")),
            Ctx(log, shared: true, guard: Guard), io, default);

        Assert.Equal(PlaybackOutcome.Aborted, r.Outcome);
        Assert.Equal((0, GuardStop), (r.StepIndex, r.Reason));
        Assert.Equal(new long[] { 300 }, HeldMs(io)); // one hold, released, and no second
        Assert.True(NothingSentAfterTheLastRelease(io));
        Assert.Contains("step 1 'a' pressed 0.3 s over 1 hold(s), then the playback ended", log);
    }

    [Fact]
    public async Task A_guard_that_fails_before_a_fresh_baseline_stops_without_parking_again()
    {
        var io = Guarded(Blocks((100, 100, null)), io => io.Ups.Count() < 2);
        var r = await StepRunner.RunAsync(ClearAtSteps(null, (100, 100, "a")), Ctx(shared: true, guard: Guard), io, default);

        Assert.Equal((0, GuardStop), (r.StepIndex, r.Reason));
        Assert.Equal(new long[] { 300, 600 }, HeldMs(io));
        Assert.Single(io.Parks); // the 3rd hold's fresh-baseline park never happened
        Assert.True(NothingSentAfterTheLastRelease(io));
    }

    [Fact]
    public async Task A_guard_already_covered_at_the_start_sends_nothing_at_all()
    {
        var io = Guarded(Blocks((100, 100, 1)), _ => false);
        var r = await StepRunner.RunAsync(ClearAtSteps(null, (100, 100, "a")), Ctx(shared: true, guard: Guard), io, default);

        Assert.Equal((0, GuardStop), (r.StepIndex, r.Reason));
        Assert.Empty(io.Sent);
        Assert.Empty(io.Parks);
    }

    [Fact]
    public async Task A_guard_colour_within_its_tolerance_passes()
    {
        // #FF135A seen as #EB1E6E: distance sqrt(20^2 + 11^2 + 20^2) = 30.3, inside 31, outside 30.
        var io = new FakeIo();
        var near = new Rgb(255 - 20, 19 + 11, 90 + 20);
        io.Screen = (_, x, y) => x is >= 55 and < 58 && y is >= 289 and < 292 ? near : Rock;
        var r = await StepRunner.RunAsync(ClearAtSteps(null, (100, 100, "a")), Ctx(shared: true, guard: Guard with { Tolerance = 31 }), io, default);
        Assert.Equal(PlaybackOutcome.Completed, r.Outcome);

        var r2 = await StepRunner.RunAsync(ClearAtSteps(null, (100, 100, "a")), Ctx(shared: true, guard: Guard), new FakeIo { Screen = io.Screen }, default);
        Assert.Equal("ClearAt stopped: the guard at (55,289) isn't the expected colour (saw #EB1E6E); "
            + "something may be over the game (a menu or a player's profile).", r2.Reason);
    }

    [Fact]
    public async Task The_guard_scales_with_the_window_like_the_points()
    {
        // Measured at 800x599, played at 1000x749: 55,289 lands at 69,361 and the 3 px box grows to 4.
        var io = new FakeIo { Client = (1000, 749), Screen = (_, _, _) => DotRed };
        await StepRunner.RunAsync(ClearAtSteps(null, (400, 244, "a")), Ctx(shared: true, actual: (1000, 749), guard: Guard), io, default);

        Assert.Contains((69, 361, 4, 4), io.CaptureRects);
        Assert.DoesNotContain((55, 289, 3, 3), io.CaptureRects);
    }

    [Fact]
    public async Task Without_a_guard_a_white_dot_changes_nothing()
    {
        var io = Guarded(Blocks((100, 100, 1)), _ => false);
        var r = await StepRunner.RunAsync(ClearAtSteps(null, (100, 100, "a")), Ctx(shared: true), io, default);

        Assert.Equal(PlaybackOutcome.Completed, r.Outcome);
        Assert.Single(io.Downs);
        Assert.DoesNotContain(io.CaptureRects, c => c == (55, 289, 3, 3));
    }

    // ---------- sweep (ore-stop sweep spec, "The Ur Task side") ----------

    private static SweepStep Sweep((int X, int Y)[] path, int dwellMs = 400)
        => new(0, path.Select(p => new SweepPoint(p.X, p.Y)).ToList(), dwellMs);

    /// <summary>Ring 1 around 400,300 at 50 px, from the east block and back to it: 9 points.</summary>
    private static readonly (int X, int Y)[] Ring1 =
    {
        (450, 300), (450, 250), (400, 250), (350, 250), (350, 300), (350, 350), (400, 350), (450, 350), (450, 300),
    };

    private const string SweepGuardStop = "SweepPath stopped: the guard at (55,289) isn't the expected colour (saw #F5F5F5); "
        + "something may be over the game (a menu or a player's profile).";

    [Fact]
    public async Task A_sweep_presses_on_the_start_moves_through_every_point_and_releases_on_the_start()
    {
        // Jump 0..150, press at 150, dwell 400 at each point but the last: moves at 550, 950, 1350,
        // and the release right on arrival at the closing point.
        var log = new List<string>();
        var io = new FakeIo { Screen = (_, _, _) => Rock };
        var r = await StepRunner.RunAsync(new MacroStep[] { Sweep(new[] { (450, 300), (450, 250), (400, 250), (450, 300) }) },
            Ctx(log), io, default);

        Assert.Equal(PlaybackOutcome.Completed, r.Outcome);
        Assert.False(r.SkippedByReach);
        var down = Assert.Single(io.Downs);
        Assert.Equal((450, 300, 150L, 1), (down.X, down.Y, down.TimestampMs, down.MouseButton));
        Assert.Equal(new[] { (450, 250, 550L), (400, 250, 950L), (450, 300, 1350L) },
            io.Sent.Where(e => e.Kind == MacroEventKind.MouseMove && e.TimestampMs >= 150).Select(e => (e.X, e.Y, e.TimestampMs)));
        var up = Assert.Single(io.Ups);
        Assert.Equal((450, 300, 1350L), (up.X, up.Y, up.TimestampMs));
        Assert.Empty(io.Released); // it let go itself; the finally had nothing to do
        Assert.Contains("swept 4 points in 1.2 s", log);
    }

    [Fact]
    public async Task A_guard_change_mid_sweep_goes_back_to_the_start_releases_there_and_stops()
    {
        // Pressed at 150, moves at 550 and 950; the dot turns white at 1000 and the sample before
        // point 4 (at 1350) sees it. Point 4 (350,250) is never reached.
        var log = new List<string>();
        var io = Guarded(new FakeIo { Screen = (_, _, _) => Rock }, io => io.NowMs < 1000);
        var r = await StepRunner.RunAsync(new MacroStep[] { Sweep(Ring1) }, Ctx(log, guard: Guard), io, default);

        Assert.Equal(PlaybackOutcome.Aborted, r.Outcome);
        Assert.Equal((0, SweepGuardStop), (r.StepIndex, r.Reason));
        Assert.Equal((450, 300), (io.Downs.Single().X, io.Downs.Single().Y));
        Assert.Equal(new[] { (MacroEventKind.MouseMove, 450, 300), (MacroEventKind.MouseUp, 450, 300) },
            io.Sent.TakeLast(2).Select(e => (e.Kind, e.X, e.Y)));
        Assert.DoesNotContain(io.Sent, e => e.Kind == MacroEventKind.MouseMove && (e.X, e.Y) == (350, 250));
        Assert.Empty(io.Released);
        Assert.Contains("swept 3 of 9 points in 1.2 s, then the playback ended; released on the start block", log);
    }

    [Fact]
    public async Task A_guard_covered_before_the_press_sends_nothing_at_all()
    {
        var io = Guarded(new FakeIo { Screen = (_, _, _) => Rock }, _ => false);
        var r = await StepRunner.RunAsync(new MacroStep[] { Sweep(Ring1) }, Ctx(guard: Guard), io, default);

        Assert.Equal((0, SweepGuardStop), (r.StepIndex, r.Reason));
        Assert.Empty(io.Sent);
    }

    [Fact]
    public async Task The_guard_is_sampled_every_3_points_while_held()
    {
        // Twice before the press, then before points 4 and 7 of 9.
        var io = Guarded(new FakeIo { Screen = (_, _, _) => Rock }, _ => true);
        var r = await StepRunner.RunAsync(new MacroStep[] { Sweep(Ring1) }, Ctx(guard: Guard), io, default);

        Assert.Equal(PlaybackOutcome.Completed, r.Outcome);
        Assert.Equal(4, io.CaptureRects.Count(c => c == (55, 289, 3, 3)));
        Assert.Equal((450, 300), (io.Ups.Single().X, io.Ups.Single().Y));
    }

    [Fact]
    public async Task A_long_dwell_still_samples_the_guard_every_1500_ms()
    {
        // At a 5 s dwell the every-3-points sample would first come at 10150. The dot turns white
        // at 2000; the timed sample inside the first dwell (due at 1650, then 3150) sees it by 3150,
        // goes back to the start and lets go there. Point 3 (400,250) is never reached.
        var log = new List<string>();
        var io = Guarded(new FakeIo { Screen = (_, _, _) => Rock }, io => io.NowMs < 2000);
        var r = await StepRunner.RunAsync(new MacroStep[] { Sweep(Ring1, dwellMs: 5000) }, Ctx(log, guard: Guard), io, default);

        Assert.Equal((0, SweepGuardStop), (r.StepIndex, r.Reason));
        Assert.True(io.Ups.Single().TimestampMs <= 3150 + StepTiming.PollMs, $"released at {io.Ups.Single().TimestampMs}");
        Assert.Equal((450, 300), (io.Ups.Single().X, io.Ups.Single().Y));
        Assert.DoesNotContain(io.Sent, e => e.Kind == MacroEventKind.MouseMove && (e.X, e.Y) == (400, 250));
    }

    [Fact]
    public async Task Losing_the_foreground_mid_sweep_releases_in_place_and_aborts()
    {
        // At (450,250) from 550; the dwell's poll at 750 finds another window in front.
        var log = new List<string>();
        var io = new FakeIo { Screen = (_, _, _) => Rock };
        io.OnDelay = () => { if (io.NowMs >= 700) io.Foreground = false; };
        var r = await StepRunner.RunAsync(new MacroStep[] { Sweep(Ring1) }, Ctx(log), io, default);

        Assert.Equal(PlaybackOutcome.Aborted, r.Outcome);
        Assert.Equal("Foreground shifted away from CElCPapa at step 1/1.", r.Reason);
        Assert.Equal(new[] { 1 }, io.Released); // RunAsync's finally, where the pointer is
        Assert.Equal((450, 250), (io.Ups.Single().X, io.Ups.Single().Y));
        Assert.Contains("swept 2 of 9 points in 0.6 s, then the playback ended", log);
    }

    [Fact]
    public async Task Esc_mid_sweep_goes_back_to_the_start_block_to_release()
    {
        using var cts = new CancellationTokenSource();
        var log = new List<string>();
        var io = new FakeIo { Screen = (_, _, _) => Rock };
        io.OnDelay = () => { if (io.NowMs >= 700) cts.Cancel(); };
        var r = await StepRunner.RunAsync(new MacroStep[] { Sweep(Ring1) }, Ctx(log), io, cts.Token);

        Assert.Equal(PlaybackOutcome.Aborted, r.Outcome);
        Assert.Equal("Playback cancelled.", r.Reason);
        Assert.Equal(new[] { (MacroEventKind.MouseMove, 450, 300), (MacroEventKind.MouseUp, 450, 300) },
            io.Sent.TakeLast(2).Select(e => (e.Kind, e.X, e.Y)));
        Assert.Empty(io.Released);
        Assert.Contains("swept 2 of 9 points in 0.6 s, then the playback ended; released on the start block", log);
    }

    [Fact]
    public async Task A_capture_that_fails_mid_sweep_goes_back_to_the_start_to_release()
    {
        // Only the capture fails, from 1000: the sample before point 4 (at 1350) cannot see the
        // window, but the target is still in front and sends still work, so it can go back to release.
        var log = new List<string>();
        var io = Guarded(new FakeIo { Screen = (_, _, _) => Rock }, _ => true);
        io.OnDelay = () => { if (io.NowMs >= 1000) io.CaptureWorks = false; };
        var r = await StepRunner.RunAsync(new MacroStep[] { Sweep(Ring1) }, Ctx(log, guard: Guard), io, default);

        Assert.Equal(PlaybackOutcome.Aborted, r.Outcome);
        Assert.Equal((0, "CElCPapa: step 1 'SweepPath' could not see the window."), (r.StepIndex, r.Reason));
        Assert.Equal(new[] { (MacroEventKind.MouseMove, 450, 300), (MacroEventKind.MouseUp, 450, 300) },
            io.Sent.TakeLast(2).Select(e => (e.Kind, e.X, e.Y)));
        Assert.Empty(io.Released);
        Assert.Contains("swept 3 of 9 points in 1.2 s, then the playback ended; released on the start block", log);
    }

    [Fact]
    public async Task Sweep_points_scale_with_the_window()
    {
        // Measured at 800x599, played at 1000x749: 400,300 lands at 500,375 and 480,240 at 600,300.
        var io = new FakeIo { Client = (1000, 749), Screen = (_, _, _) => Rock };
        await StepRunner.RunAsync(new MacroStep[] { Sweep(new[] { (400, 300), (400, 240), (480, 240), (400, 300) }) },
            Ctx(actual: (1000, 749)), io, default);

        Assert.Equal((500, 375), (io.Downs.Single().X, io.Downs.Single().Y));
        Assert.Equal(new[] { (500, 300), (600, 300), (500, 375) },
            io.Sent.Where(e => e.Kind == MacroEventKind.MouseMove && e.TimestampMs >= StepTiming.JumpWiggleMs).Select(e => (e.X, e.Y)));
        Assert.Equal((500, 375), (io.Ups.Single().X, io.Ups.Single().Y));
    }

    [Fact]
    public async Task A_sweep_point_outside_the_live_window_stops_before_any_input()
    {
        var io = new FakeIo { Client = (500, 599), Screen = (_, _, _) => Rock };
        var r = await StepRunner.RunAsync(new MacroStep[] { Sweep(new[] { (450, 300), (550, 300), (450, 300) }) }, Ctx(), io, default);

        Assert.Equal(PlaybackOutcome.Aborted, r.Outcome);
        Assert.Equal((0, "CElCPapa: step 1 'SweepPath' has a point outside the window."), (r.StepIndex, r.Reason));
        Assert.Empty(io.Sent);
    }

    [Fact]
    public async Task A_sweep_with_fewer_than_2_points_is_refused_before_playing()
    {
        var r = await StepRunner.RunAsync(new MacroStep[] { Sweep(new[] { (450, 300) }) }, Ctx(), new FakeIo(), default);

        Assert.Equal(PlaybackOutcome.Refused, r.Outcome);
        Assert.Equal("Step 1 is a sweep with fewer than 2 points.", r.Reason);
    }
}
