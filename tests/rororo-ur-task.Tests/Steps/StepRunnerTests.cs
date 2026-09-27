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
        public Action? OnDelay;

        public bool Send(MacroEvent e) { Sent.Add(e with { TimestampMs = NowMs }); if (e.Kind == MacroEventKind.MouseMove) Cursor = (e.X, e.Y); return true; }
        public bool MoveRelative(int dx, int dy) { Relative.Add((dx, dy)); return true; }
        public (int X, int Y)? CursorClient() => Cursor;
        public (int W, int H)? ClientSize() => Client;
        public bool TargetInForeground() => Foreground;
        public PixelBlock? Capture(int x, int y, int w, int h)
        {
            Captures++;
            CaptureRects.Add((x, y, w, h));
            if (!CaptureWorks) return null;
            var px = new uint[w * h];
            for (int r = 0; r < h; r++)
            for (int c = 0; c < w; c++)
            {
                var rgb = Screen(NowMs, x + c, y + r);
                px[r * w + c] = 0xFF000000u | (uint)(rgb.R << 16) | (uint)(rgb.G << 8) | (uint)rgb.B;
            }
            return new PixelBlock(x, y, w, h, px);
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
