// tests/rororo-ur-task.Tests/Ipc/MacroRunInvokerRepeatTests.cs
using Labs626.UrTask.Ipc;
using Labs626.UrTask.Macros;
using Labs626.UrTask.PluginHost;

namespace Labs626.UrTask.Tests.Ipc;

/// <summary>
/// A repeat that cannot make progress (L2-01). With the target's window gone SequencePlayer's whole
/// pass completes synchronously, so the repeat loop ran with no await at all: one core pegged, the
/// log rolled over in seconds, and no gap for Esc to land in.
/// </summary>
public class MacroRunInvokerRepeatTests
{
    private static Macro NewMacro() => new(
        SchemaVersion: 2, Id: Guid.NewGuid().ToString(), Name: "Farm", RecordMode: "PerWindow",
        RecordedAgainstUserId: null, RecordedAgainstDisplayName: null,
        InterAltDelayMs: null, RecordedAtUnixMs: 0, Events: new List<MacroEvent>());

    private static AccountRegistry.AccountInfo Alt(long userId)
        => new(1000 + (int)userId, userId, $"alt-{userId}", $"acct-{userId}");

    private sealed class CompletingPlayer : IMacroPlayer
    {
        public int Plays;
        public bool IsPlaying => false;
#pragma warning disable CS0067 // the interface needs them; nothing here raises them
        public event EventHandler<PlaybackStartedArgs>? Started;
        public event EventHandler<PlaybackEndedArgs>? Ended;
#pragma warning restore CS0067
        public Task<PlaybackResult> PlayAsync(Macro macro, long targetUserId, CancellationToken external = default)
        {
            Interlocked.Increment(ref Plays);
            return Task.FromResult(PlaybackResult.Completed());
        }
        public Task<PlaybackResult> PlayAllWindowsRawAsync(Macro macro, CancellationToken external = default)
            => Task.FromResult(PlaybackResult.Completed());
        public bool Abort() => false;
    }

    private sealed class Foreground : IForegroundWatcher
    {
        public AccountRegistry.AccountInfo? InFront;
        public AccountRegistry.AccountInfo? ResolveForegroundAccount() => InFront;
    }

    /// <summary>The invoker's wait between passes, held: each requested wait is recorded and stays
    /// pending until the test lets it go, so a test counts passes without sleeping real seconds.</summary>
    private sealed class HeldDelay
    {
        private readonly object _lock = new();
        private readonly List<TimeSpan> _asked = new();
        private TaskCompletionSource _pending = new(TaskCreationOptions.RunContinuationsAsynchronously);

        public IReadOnlyList<TimeSpan> Asked { get { lock (_lock) return _asked.ToArray(); } }

        public async Task Wait(TimeSpan wait, CancellationToken ct)
        {
            Task pending;
            lock (_lock) { _asked.Add(wait); pending = _pending.Task; }
            await pending.WaitAsync(ct).ConfigureAwait(false);
        }

        public void ReleaseOne()
        {
            lock (_lock)
            {
                _pending.TrySetResult();
                _pending = new(TaskCreationOptions.RunContinuationsAsynchronously);
            }
        }
    }

    private static MacroRunInvoker Invoker(Macro m, IReadOnlyList<AccountRegistry.AccountInfo> alts, SequencePlayer sequence,
        Func<TimeSpan, CancellationToken, Task> delay, List<string>? log = null)
        => new MacroRunInvoker(
            load: () => new MacroStore.LoadResult(new[] { m }, Array.Empty<MacroStore.LoadFailure>()),
            snapshot: () => alts,
            resolveForegroundUserId: () => alts[0].RobloxUserId,
            isBusy: () => sequence.IsRunning,
            playWithResult: async (macro, targets, d, ct) => await sequence.PlayAsync(macro, targets, d, ct),
            abort: sequence.Abort,
            log: log is null ? null : new Action<string>(l => { lock (log) log.Add(l); }),
            delay: delay);

    private static async Task WaitUntilAsync(Func<bool> cond)
    {
        for (int i = 0; i < 300 && !cond(); i++) await Task.Delay(10);
        Assert.True(cond());
    }

    private static GetPlaybackResponse Status(MacroRunInvoker inv, string? id)
        => inv.GetPlayback(new GetPlaybackRequest("1.0", "GetPlayback", id, "626labs.ur-mcp"));

    [Fact]
    public async Task A_repeat_ends_as_failed_when_its_only_targets_window_is_gone()
    {
        var m = NewMacro();
        var alt = Alt(123);
        int focusCalls = 0;
        var sequence = new SequencePlayer(new CompletingPlayer(), new Foreground(),
            _ => { Interlocked.Increment(ref focusCalls); return new FocusAttempt(false, MissCause.WindowGone, "Process not found (pid stale)."); });
        var delay = new HeldDelay();
        var inv = Invoker(m, new[] { alt }, sequence, delay.Wait);

        var run = await inv.RunAsync(new RunMacroRequest("1.0", "RunMacro", m.Id, new[] { "123" }, 0, "626labs.ur-mcp", Repeat: true), default);
        try
        {
            await WaitUntilAsync(() => inv.ActivePlaybackCount == 0);
        }
        finally { inv.StopAllForUserAbort(); } // a red run must not leave a spinning thread behind

        var status = Status(inv, run.PlaybackId);
        Assert.Equal(("failed", "refused"), (status.State, status.Reason));
        Assert.Equal("Stopped repeating: alt-123's game window is gone. Run the macro again once the account is back in a game.", status.Detail);
        Assert.Equal(1, Volatile.Read(ref focusCalls)); // one pass, not a spin
        Assert.Empty(delay.Asked);
    }

    [Fact]
    public async Task A_repeat_ends_as_failed_when_every_targets_window_is_gone_and_names_them()
    {
        var m = NewMacro();
        var alts = new[] { Alt(1), Alt(2) };
        var sequence = new SequencePlayer(new CompletingPlayer(), new Foreground(),
            _ => new FocusAttempt(false, MissCause.WindowGone, "MainWindowHandle is null."));
        var inv = Invoker(m, alts, sequence, new HeldDelay().Wait);

        var run = await inv.RunAsync(new RunMacroRequest("1.0", "RunMacro", m.Id, new[] { "1", "2" }, 0, "626labs.ur-mcp", Repeat: true), default);
        try { await WaitUntilAsync(() => inv.ActivePlaybackCount == 0); }
        finally { inv.StopAllForUserAbort(); }

        var status = Status(inv, run.PlaybackId);
        Assert.Equal(("failed", "refused"), (status.State, status.Reason));
        Assert.Equal("Stopped repeating: the game windows for alt-1 and alt-2 are gone. Run the macro again once the accounts are back in a game.", status.Detail);
    }

    [Fact]
    public async Task A_repeat_keeps_going_while_one_target_still_has_a_window()
    {
        // Only a pass where EVERY target's window is gone ends the repeat. One gone window among
        // live ones is a refused alt, and the others still play.
        var m = NewMacro();
        var alts = new[] { Alt(1), Alt(2) };
        var player = new CompletingPlayer();
        var fg = new Foreground { InFront = alts[1] };
        var sequence = new SequencePlayer(player, fg,
            pid => pid == alts[0].Pid ? new FocusAttempt(false, MissCause.WindowGone, "gone") : new FocusAttempt(true, null, null));
        var delay = new HeldDelay();
        var inv = Invoker(m, alts, sequence, delay.Wait);

        var run = await inv.RunAsync(new RunMacroRequest("1.0", "RunMacro", m.Id, new[] { "1", "2" }, 0, "626labs.ur-mcp", Repeat: true), default);
        await WaitUntilAsync(() => Volatile.Read(ref player.Plays) >= 3);

        Assert.Equal("running", Status(inv, run.PlaybackId).State);
        Assert.Empty(delay.Asked); // each pass completed something: no wait between passes
        inv.StopMacro(new StopMacroRequest("1.0", "StopMacro", run.PlaybackId, null, "626labs.ur-mcp"));
        await WaitUntilAsync(() => inv.ActivePlaybackCount == 0);
    }

    [Fact]
    public async Task A_repeat_whose_pass_completes_nothing_waits_longer_each_time_up_to_a_cap()
    {
        // The window exists but never comes to the front, so every pass is refused. With a zero
        // inter-alt delay the real SequencePlayer finishes each pass without yielding.
        var m = NewMacro();
        var alt = Alt(123);
        int focusCalls = 0;
        var sequence = new SequencePlayer(new CompletingPlayer(), new Foreground(),
            _ => { Interlocked.Increment(ref focusCalls); return new FocusAttempt(false, MissCause.FocusRefused, "Windows refused focus"); });
        var delay = new HeldDelay();
        var inv = Invoker(m, new[] { alt }, sequence, delay.Wait);

        var run = await inv.RunAsync(new RunMacroRequest("1.0", "RunMacro", m.Id, new[] { "123" }, 0, "626labs.ur-mcp", Repeat: true), default);
        try
        {
            // One pass, then it waits. However long the test idles here, no second pass starts.
            await WaitUntilAsync(() => delay.Asked.Count == 1);
            await Task.Delay(100);
            Assert.Equal(1, Volatile.Read(ref focusCalls));
            Assert.Equal("running", Status(inv, run.PlaybackId).State);

            for (int released = 1; released <= 6; released++)
            {
                delay.ReleaseOne();
                await WaitUntilAsync(() => delay.Asked.Count == released + 1);
            }
            Assert.Equal(7, Volatile.Read(ref focusCalls));
            Assert.Equal(new[] { 1, 2, 4, 8, 16, 30, 30 }, delay.Asked.Select(t => (int)t.TotalSeconds));
        }
        finally { inv.StopAllForUserAbort(); }

        // The wait is cancellable: a stop lands inside it, without waiting it out.
        await WaitUntilAsync(() => inv.ActivePlaybackCount == 0);
        Assert.Equal("stopped", Status(inv, run.PlaybackId).State);
    }

    [Fact]
    public async Task The_wait_between_passes_resets_after_a_pass_that_completed_something()
    {
        var m = NewMacro();
        var alt = Alt(123);
        var fg = new Foreground();
        var sequence = new SequencePlayer(new CompletingPlayer(), fg, _ => new FocusAttempt(true, null, null));
        var delay = new HeldDelay();
        var inv = Invoker(m, new[] { alt }, sequence, delay.Wait);

        // Not in front: refused passes, waits of 1 s then 2 s.
        var run = await inv.RunAsync(new RunMacroRequest("1.0", "RunMacro", m.Id, new[] { "123" }, 0, "626labs.ur-mcp", Repeat: true), default);
        try
        {
            await WaitUntilAsync(() => delay.Asked.Count == 1);
            delay.ReleaseOne();
            await WaitUntilAsync(() => delay.Asked.Count == 2);

            // The window comes forward for exactly one pass, then is lost again. The next pass
            // completes and the one after is refused, so the following wait starts over at 1 s.
            int resolves = 0;
            sequence.Progress += (_, p) =>
            {
                if (p.Phase == SequencePhase.Focusing) fg.InFront = Interlocked.Increment(ref resolves) == 1 ? alt : null;
            };
            delay.ReleaseOne();
            await WaitUntilAsync(() => delay.Asked.Count == 3);

            Assert.Equal(new[] { 1, 2, 1 }, delay.Asked.Select(t => (int)t.TotalSeconds));
        }
        finally { inv.StopAllForUserAbort(); }
        await WaitUntilAsync(() => inv.ActivePlaybackCount == 0);
    }
}
