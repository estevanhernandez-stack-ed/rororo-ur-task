using Labs626.UrTask.Macros;
using Labs626.UrTask.Macros.Steps;

namespace Labs626.UrTask.Tests.Steps;

/// <summary>A playback parks at its next step boundary when keep-alive asks for a break, and picks
/// up at the same step (spec "Pause and pick up").</summary>
public class PauseResumeTests
{
    private static readonly TimeSpan Long = TimeSpan.FromMinutes(5);

    /// <summary>Virtual clock and a flat screen. With <see cref="YieldReal"/> every delay also
    /// yields a real millisecond, so a step that never ends still lets the gate's timer run.</summary>
    private sealed class FakeIo : IStepIo
    {
        public long NowMs { get; set; }
        public readonly List<MacroEvent> Sent = new();
        public readonly List<int> Released = new();
        public bool Foreground = true;
        public bool YieldReal;
        public Action? OnDelay;
        /// <summary>When set, the next delay waits for this task (then it is cleared).</summary>
        public Task? Hold;

        public bool Send(MacroEvent e) { lock (Sent) Sent.Add(e with { TimestampMs = NowMs }); return true; }
        public bool ReleaseButton(int button)
        {
            lock (Sent) { Released.Add(button); Sent.Add(new MacroEvent(NowMs, MacroEventKind.MouseUp, 0, 0, 0, button, 0)); }
            return true;
        }
        public bool MoveRelative(int dx, int dy) => true;
        public bool ParkPointer(int clientX, int clientY) => true;
        public (int X, int Y)? CursorClient() => (0, 0);
        public (int W, int H)? ClientSize() => (800, 599);
        public bool TargetInForeground() => Foreground;
        public PixelBlock? Capture(int x, int y, int w, int h)
            => new(x, y, w, h, Enumerable.Repeat(0xFF1E2350u, w * h).ToArray());
        public Task Delay(int ms, CancellationToken ct)
        {
            ct.ThrowIfCancellationRequested();
            NowMs += ms;
            OnDelay?.Invoke();
            ct.ThrowIfCancellationRequested();
            if (Hold is { } hold) { Hold = null; return hold.WaitAsync(ct); }
            return YieldReal ? Task.Delay(1, ct) : Task.CompletedTask;
        }
        public List<(MacroEventKind Kind, int Vk)> Keys()
        {
            lock (Sent) return Sent.Where(e => e.Kind is MacroEventKind.KeyDown or MacroEventKind.KeyUp)
                .Select(e => (e.Kind, e.VirtualKeyCode)).ToList();
        }
    }

    private static StepContext Ctx(ForegroundGate? gate, List<string>? log = null, Action<InputSource>? landed = null)
        => new("Player", 42, "m1", (800, 599), (800, 599), 100, _ => null, s => { if (log is not null) lock (log) log.Add(s); },
            OnKeyLanded: landed, Gate: gate);

    private static ForegroundGate Gate(FakeIo io) => new(() => io.NowMs, _ => { });

    private static HoldStep Hold(int? maxMs) => new(0, "h1", "Hold", 400, 244, 1, new HoldCheck(new CheckBox()), maxMs);

    /// <summary>Requests a break the first time the virtual clock reaches <paramref name="atMs"/>.</summary>
    private static Func<Task<IDisposable>?> RequestAt(FakeIo io, ForegroundGate gate, long atMs, TimeSpan? preemptAfter = null)
    {
        Task<IDisposable>? request = null;
        io.OnDelay += () => { if (request is null && io.NowMs >= atMs) request = gate.RequestBreakAsync("Due Alt", preemptAfter ?? Long, default); };
        return () => request;
    }

    [Fact]
    public async Task Break_waits_for_the_boundary_and_the_step_finishes_first()
    {
        var io = new FakeIo();
        var gate = Gate(io);
        var log = new List<string>();
        var request = RequestAt(io, gate, 100);
        var steps = new MacroStep[] { Hold(1000), new KeyStep(0, 0x41, true), new KeyStep(0, 0x41, false) };

        using var playback = gate.EnterPlayback(42, "m1");
        var run = StepRunner.RunAsync(steps, Ctx(gate, log), io, default);

        var lease = await request()!.WaitAsync(TimeSpan.FromSeconds(5));
        Assert.True(io.NowMs >= 1000, $"granted at {io.NowMs} ms, before the hold's 1000 ms ran out");
        Assert.Empty(io.Keys()); // step 2 has not run
        Assert.True(gate.Status.Paused);
        await Task.Delay(50);
        Assert.False(run.IsCompleted);
        Assert.Empty(io.Keys());

        io.NowMs += 4400;
        lease.Dispose();
        var r = await run.WaitAsync(TimeSpan.FromSeconds(5));
        Assert.Equal(PlaybackOutcome.Completed, r.Outcome);
        Assert.Equal(new[] { (MacroEventKind.KeyDown, 0x41), (MacroEventKind.KeyUp, 0x41) }, io.Keys());
        Assert.Contains("paused at step 2/3 for Due Alt", log);
        Assert.Contains("resumed at step 2/3 (paused 4 s)", log);
    }

    [Fact]
    public async Task Resume_continues_at_the_same_step_nothing_replayed()
    {
        var steps = new MacroStep[]
        {
            new KeyStep(0, 0x41, true), new KeyStep(0, 0x41, false), new WaitStep(100),
            new KeyStep(0, 0x42, true), new KeyStep(0, 0x42, false),
        };
        var plain = new FakeIo();
        await StepRunner.RunAsync(steps, Ctx(null), plain, default);

        var io = new FakeIo();
        var gate = Gate(io);
        var request = RequestAt(io, gate, 50);
        using var playback = gate.EnterPlayback(42, "m1");
        var run = StepRunner.RunAsync(steps, Ctx(gate), io, default);
        var lease = await request()!.WaitAsync(TimeSpan.FromSeconds(5));
        var before = io.Keys();
        lease.Dispose();
        await run.WaitAsync(TimeSpan.FromSeconds(5));

        Assert.Equal(2, before.Count);
        Assert.Equal(plain.Keys(), io.Keys());
    }

    [Fact]
    public async Task Keys_held_across_the_boundary_are_released_and_pressed_again()
    {
        var io = new FakeIo();
        var gate = Gate(io);
        var log = new List<string>();
        var landed = new List<InputSource>();
        var request = RequestAt(io, gate, 50);
        var steps = new MacroStep[] { new KeyStep(0, 0x57, true), new WaitStep(100), new KeyStep(0, 0x57, false) };

        using var playback = gate.EnterPlayback(42, "m1");
        var run = StepRunner.RunAsync(steps, Ctx(gate, log, landed.Add), io, default);
        var lease = await request()!.WaitAsync(TimeSpan.FromSeconds(5));
        Assert.Equal(new[] { (MacroEventKind.KeyDown, 0x57), (MacroEventKind.KeyUp, 0x57) }, io.Keys()); // released for the break
        lease.Dispose();
        var r = await run.WaitAsync(TimeSpan.FromSeconds(5));

        Assert.Equal(PlaybackOutcome.Completed, r.Outcome);
        Assert.Equal(new[]
        {
            (MacroEventKind.KeyDown, 0x57), (MacroEventKind.KeyUp, 0x57), // the break
            (MacroEventKind.KeyDown, 0x57), (MacroEventKind.KeyUp, 0x57), // pressed again, then step 3
        }, io.Keys());
        Assert.Equal(2, landed.Count); // the re-press credits the account like any key-down
        Assert.Contains("paused at step 3/3 for Due Alt", log);
    }

    [Fact]
    public async Task Held_buttons_are_released_and_not_pressed_again()
    {
        var io = new FakeIo();
        var gate = Gate(io);
        var log = new List<string>();
        var request = RequestAt(io, gate, 50);
        var down = new RawStep(0, new[] { new MacroEvent(0, MacroEventKind.MouseDown, 0, 10, 10, 2, 0) }, "right down");
        var steps = new MacroStep[] { down, new WaitStep(100), new WaitStep(10) };

        using var playback = gate.EnterPlayback(42, "m1");
        var run = StepRunner.RunAsync(steps, Ctx(gate, log), io, default);
        var lease = await request()!.WaitAsync(TimeSpan.FromSeconds(5));
        Assert.Equal(new[] { 2 }, io.Released);
        lease.Dispose();
        await run.WaitAsync(TimeSpan.FromSeconds(5));

        Assert.Single(io.Sent, e => e.Kind == MacroEventKind.MouseDown);
        Assert.Equal(new[] { 2 }, io.Released); // already up: the finally has nothing left to release
        Assert.Contains("resumed without re-pressing button 2", log);
    }

    [Fact]
    public async Task Resume_waits_for_the_window_then_the_foreground_check_ends_it()
    {
        var io = new FakeIo();
        var gate = Gate(io);
        var request = RequestAt(io, gate, 50);
        var steps = new MacroStep[] { new WaitStep(100), new KeyStep(0, 0x41, true) };

        using var playback = gate.EnterPlayback(42, "m1");
        var run = StepRunner.RunAsync(steps, Ctx(gate), io, default);
        var lease = await request()!.WaitAsync(TimeSpan.FromSeconds(5));
        io.Foreground = false;
        var parkedAt = io.NowMs;
        lease.Dispose();
        var r = await run.WaitAsync(TimeSpan.FromSeconds(5));

        Assert.Equal(PlaybackOutcome.Aborted, r.Outcome);
        Assert.Null(r.StepIndex);
        Assert.Equal("Foreground shifted away from Player at step 2/2.", r.Reason);
        Assert.Equal(1000, io.NowMs - parkedAt); // polled every 50 ms for one second
        Assert.Empty(io.Keys());
    }

    [Fact]
    public async Task A_step_longer_than_the_runway_is_preempted()
    {
        var io = new FakeIo { YieldReal = true };
        var gate = Gate(io);
        var log = new List<string>();
        var playback = gate.EnterPlayback(42, "m1");
        var requested = RequestAt(io, gate, 100, TimeSpan.FromMilliseconds(500)); // mid-hold: no boundary ahead
        var r = await StepRunner.RunAsync(new MacroStep[] { Hold(null), new WaitStep(10) }, Ctx(gate, log), io, default)
            .WaitAsync(TimeSpan.FromSeconds(10));
        var request = requested()!;

        Assert.Equal(PlaybackOutcome.Aborted, r.Outcome);
        Assert.Equal("keep-alive-preempted", r.Code);
        Assert.Equal("Stopped for keep-alive: a step ran longer than the keep-alive runway allows. Give long holds a max so keep-alive can take its turn.", r.Reason);
        Assert.Null(r.StepIndex);
        Assert.Equal(new[] { 1 }, io.Released);
        Assert.False(request.IsCompleted); // granted when the playback exits, not mid-release
        Assert.DoesNotContain(log, l => l.StartsWith("paused", StringComparison.Ordinal));
        playback.Dispose();
        (await request.WaitAsync(TimeSpan.FromSeconds(5))).Dispose();
    }

    [Fact]
    public async Task A_preempt_decided_as_the_playback_parks_does_not_end_it()
    {
        // The gap: the timer decides to preempt under the lock and cancels after letting go of it.
        // The playback reaches its boundary and parks inside that gap; it must pick up normally.
        var io = new FakeIo();
        var gate = Gate(io);
        var stepEnds = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        gate.OnPreemptDecided = () =>
        {
            stepEnds.TrySetResult(); // the step finishes right after the decision ...
            SpinWait.SpinUntil(() => gate.Status.Paused, TimeSpan.FromSeconds(5)); // ... and parks before the cancel
        };
        Task<IDisposable>? request = null;
        io.OnDelay = () =>
        {
            if (request is not null) return;
            request = gate.RequestBreakAsync("Due Alt", TimeSpan.FromMilliseconds(20), default);
            io.Hold = stepEnds.Task; // this delay is the step still running
        };
        var steps = new MacroStep[] { new WaitStep(100), new KeyStep(0, 0x41, true), new KeyStep(0, 0x41, false) };

        using var playback = gate.EnterPlayback(42, "m1");
        var run = StepRunner.RunAsync(steps, Ctx(gate), io, default);
        var lease = await request!.WaitAsync(TimeSpan.FromSeconds(5));
        await Task.Delay(50); // the cancel has fired by now
        Assert.False(run.IsCompleted);
        lease.Dispose();
        var r = await run.WaitAsync(TimeSpan.FromSeconds(5));

        Assert.Equal(PlaybackOutcome.Completed, r.Outcome);
        Assert.Null(r.Code);
        Assert.Equal(new[] { (MacroEventKind.KeyDown, 0x41), (MacroEventKind.KeyUp, 0x41) }, io.Keys());
    }

    [Fact]
    public async Task User_cancellation_keeps_its_old_result()
    {
        var io = new FakeIo();
        var gate = Gate(io);
        using var cts = new CancellationTokenSource();
        io.OnDelay = () => { if (io.NowMs >= 50) cts.Cancel(); };
        using var playback = gate.EnterPlayback(42, "m1");
        var r = await StepRunner.RunAsync(new MacroStep[] { new WaitStep(100) }, Ctx(gate), io, cts.Token);
        Assert.Equal(PlaybackResult.Aborted("Playback cancelled."), r);
        Assert.Null(r.Code);
    }

    // ---------- old event-list macros: SequencePlayer ----------

    private sealed class GatedPlayer(ForegroundGate gate) : IMacroPlayer
    {
        public readonly List<long> Played = new();
        public Func<long, Task>? During;
        public bool IsPlaying => false;
        public event EventHandler<PlaybackStartedArgs>? Started { add { } remove { } }
        public event EventHandler<PlaybackEndedArgs>? Ended { add { } remove { } }
        public async Task<PlaybackResult> PlayAsync(Macro macro, long targetUserId, CancellationToken external = default)
        {
            using var playback = gate.EnterPlayback(targetUserId, macro.Name ?? macro.Id);
            lock (Played) Played.Add(targetUserId);
            if (During is not null) await During(targetUserId);
            return PlaybackResult.Completed();
        }
        public Task<PlaybackResult> PlayAllWindowsRawAsync(Macro macro, CancellationToken external = default)
            => Task.FromResult(PlaybackResult.Completed());
        public bool Abort() => false;
        public List<long> Snapshot() { lock (Played) return Played.ToList(); }
    }

    private sealed class FollowFocus : PluginHost.IForegroundWatcher
    {
        public PluginHost.AccountRegistry.AccountInfo? Current;
        public PluginHost.AccountRegistry.AccountInfo? ResolveForegroundAccount() => Current;
    }

    private static Macro EventMacro() => new(2, "ev", "events", "PerWindow", null, null, 0, 0,
        new List<MacroEvent> { new(0, MacroEventKind.KeyDown, 0x41, 0, 0, 0, 0), new(10, MacroEventKind.KeyUp, 0x41, 0, 0, 0, 0) });

    [Fact]
    public async Task Event_list_macros_pause_only_between_passes()
    {
        var gate = new ForegroundGate(() => 0, _ => { });
        var player = new GatedPlayer(gate);
        var fg = new FollowFocus();
        var alts = new[] { new PluginHost.AccountRegistry.AccountInfo(1, 101, "One", "a1"), new PluginHost.AccountRegistry.AccountInfo(2, 102, "Two", "a2") };
        var sequence = new SequencePlayer(player, fg, pid => { fg.Current = alts[pid - 1]; return (true, null); }, gate);

        // Mid-pass on the first alt: the request waits for that playback to end, not a boundary inside it.
        Task<IDisposable>? request = null;
        player.During = id =>
        {
            if (id == 101)
            {
                request = gate.RequestBreakAsync("Due Alt", Long, default);
                Assert.False(request.IsCompleted);
            }
            return Task.CompletedTask;
        };
        var pass = sequence.PlayAsync(EventMacro(), alts, interAltDelayMs: 0);
        var lease = await request!.WaitAsync(TimeSpan.FromSeconds(5));
        await Task.Delay(50);
        Assert.Equal(new long[] { 101 }, player.Snapshot()); // held between targets
        lease.Dispose();
        await pass.WaitAsync(TimeSpan.FromSeconds(5));
        Assert.Equal(new long[] { 101, 102 }, player.Snapshot());

        // Between passes: a break already held keeps the next pass from focusing anything.
        player.During = null;
        var held = await gate.RequestBreakAsync("Due Alt", Long, default);
        var next = sequence.PlayAsync(EventMacro(), alts, interAltDelayMs: 0);
        await Task.Delay(50);
        Assert.Equal(new long[] { 101, 102 }, player.Snapshot());
        held.Dispose();
        await next.WaitAsync(TimeSpan.FromSeconds(5));
        Assert.Equal(new long[] { 101, 102, 101, 102 }, player.Snapshot());
    }

    [Fact]
    public async Task A_break_granted_between_targets_holds_the_next_target_until_it_ends()
    {
        var gate = new ForegroundGate(() => 0, _ => { });
        var player = new GatedPlayer(gate);
        var fg = new FollowFocus();
        var alts = new[] { new PluginHost.AccountRegistry.AccountInfo(1, 101, "One", "a1"), new PluginHost.AccountRegistry.AccountInfo(2, 102, "Two", "a2") };
        var focused = new List<int>();
        Task<IDisposable>? request = null;
        var sequence = new SequencePlayer(player, fg, pid =>
        {
            lock (focused) focused.Add(pid);
            fg.Current = alts[pid - 1];
            // Alt 1 has played and nothing is in: keep-alive gets its break at once, mid inter-alt delay.
            if (pid == 2 && request is null) request = gate.RequestBreakAsync("Due Alt", Long, default);
            return (true, null);
        }, gate);

        var pass = sequence.PlayAsync(EventMacro(), alts, interAltDelayMs: 100);
        Assert.True(SpinWait.SpinUntil(() => Volatile.Read(ref request) is not null, TimeSpan.FromSeconds(5)));
        var lease = await request!.WaitAsync(TimeSpan.FromSeconds(5));
        Assert.True(request.IsCompletedSuccessfully);
        await Task.Delay(300); // past the delay: the sequence sees the held break and waits
        Assert.Equal(new long[] { 101 }, player.Snapshot());
        lock (focused) Assert.Equal(new[] { 1, 2 }, focused); // no focus during the break

        fg.Current = null; // keep-alive moved the foreground
        lease.Dispose();
        var result = await pass.WaitAsync(TimeSpan.FromSeconds(5));
        Assert.Equal(new long[] { 101, 102 }, player.Snapshot());
        lock (focused) Assert.Equal(new[] { 1, 2, 2 }, focused); // focused again after the break
        Assert.Equal(2, result.Completed);
    }
}
