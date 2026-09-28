// tests/rororo-ur-task.Tests/Ipc/MacroRunInvokerTests.cs
using Labs626.UrTask.Ipc;
using Labs626.UrTask.Macros;
using Labs626.UrTask.PluginHost;

namespace Labs626.UrTask.Tests.Ipc;

public class MacroRunInvokerTests
{
    private static Macro NewMacro(string id, string? name = "recovery") => new(
        SchemaVersion: 2, Id: id, Name: name, RecordMode: "PerWindow",
        RecordedAgainstUserId: null, RecordedAgainstDisplayName: null,
        InterAltDelayMs: null, RecordedAtUnixMs: 0, Events: new List<MacroEvent>());

    private static AccountRegistry.AccountInfo Alt(long userId)
        => new(1000 + (int)userId, userId, $"alt-{userId}", $"acct-{userId}");

    // Builds an invoker with injected fakes.
    // playStarted TCS is set (with captured targets) as soon as the fake play lambda is entered.
    // playGate TCS controls when the fake play lambda completes (leave null for instant completion).
    private static MacroRunInvoker Build(
        IReadOnlyList<Macro> macros,
        IReadOnlyList<AccountRegistry.AccountInfo> running,
        bool busy,
        List<long>? playedUserIds = null,
        TaskCompletionSource? playStarted = null,
        TaskCompletionSource? playGate = null)
        => new MacroRunInvoker(
            loadMacros: () => macros,
            snapshot: () => running,
            resolveForegroundUserId: () => running.Count > 0 ? running[0].RobloxUserId : (long?)null,
            isBusy: () => busy,
            play: async (macro, targets, delay, ct) =>
            {
                playedUserIds?.AddRange(targets.Select(t => t.RobloxUserId));
                playStarted?.TrySetResult();
                if (playGate is not null)
                    await playGate.Task.ConfigureAwait(false);
            });

    [Fact]
    public async Task UnknownMacro_Refused()
    {
        var inv = Build(macros: Array.Empty<Macro>(), running: new[] { Alt(123) }, busy: false);
        var req = new RunMacroRequest("1.0", "RunMacro", Guid.NewGuid().ToString(), new[] { "123" }, null, "626labs.ur-ocr");
        var r = await inv.RunAsync(req, default);
        Assert.False(r.Ok);
        Assert.Equal("unknown-macro", r.Reason);
    }

    [Fact]
    public async Task Busy_Refused()
    {
        var m = NewMacro(Guid.NewGuid().ToString());
        var inv = Build(new[] { m }, new[] { Alt(123) }, busy: true);
        var req = new RunMacroRequest("1.0", "RunMacro", m.Id, new[] { "123" }, null, "626labs.ur-ocr");
        var r = await inv.RunAsync(req, default);
        Assert.False(r.Ok);
        Assert.Equal("busy", r.Reason);
    }

    [Fact]
    public async Task NoResolvableTargets_Refused()
    {
        var m = NewMacro(Guid.NewGuid().ToString());
        var inv = Build(new[] { m }, running: new[] { Alt(123) }, busy: false);
        var req = new RunMacroRequest("1.0", "RunMacro", m.Id, new[] { "999" }, null, "626labs.ur-ocr"); // 999 not running
        var r = await inv.RunAsync(req, default);
        Assert.False(r.Ok);
        Assert.Equal("no-targets-resolved", r.Reason);
    }

    [Fact]
    public async Task ForegroundSentinel_ResolvesToForegroundAlt_AndPlays()
    {
        var m = NewMacro(Guid.NewGuid().ToString());
        var played = new List<long>();
        var started = new TaskCompletionSource();
        var inv = Build(new[] { m }, running: new[] { Alt(123) }, busy: false, playedUserIds: played, playStarted: started);
        var req = new RunMacroRequest("1.0", "RunMacro", m.Id, new[] { "foreground" }, null, "626labs.ur-ocr");
        var r = await inv.RunAsync(req, default);
        Assert.True(r.Ok);
        Assert.NotNull(r.PlaybackId);
        // Wait for detached playback to actually start (with timeout).
        await started.Task.WaitAsync(TimeSpan.FromSeconds(2));
        Assert.Equal(new long[] { 123 }, played);
    }

    [Fact]
    public async Task NullTargets_TreatedAsForeground()
    {
        var m = NewMacro(Guid.NewGuid().ToString());
        var played = new List<long>();
        var started = new TaskCompletionSource();
        var inv = Build(new[] { m }, running: new[] { Alt(123) }, busy: false, playedUserIds: played, playStarted: started);
        var req = new RunMacroRequest("1.0", "RunMacro", m.Id, null, null, "626labs.ur-ocr");
        var r = await inv.RunAsync(req, default);
        Assert.True(r.Ok);
        await started.Task.WaitAsync(TimeSpan.FromSeconds(2));
        Assert.Equal(new long[] { 123 }, played);
    }

    /// <summary>
    /// Core regression guard: RunAsync must return Accepted immediately even when the
    /// underlying playback never completes. The caller (Ur-OCR 5Hz tick) must not be blocked.
    /// </summary>
    [Fact]
    public async Task AckOnAccept_ReturnsImmediately_BeforePlaybackCompletes()
    {
        var m = NewMacro(Guid.NewGuid().ToString());
        // playGate is never set — the fake play task never completes.
        var neverCompletes = new TaskCompletionSource();
        var started = new TaskCompletionSource();
        var inv = Build(new[] { m }, running: new[] { Alt(123) }, busy: false, playStarted: started, playGate: neverCompletes);
        var req = new RunMacroRequest("1.0", "RunMacro", m.Id, new[] { "123" }, null, "626labs.ur-ocr");

        var response = await inv.RunAsync(req, default);

        // RunAsync must have returned Accepted while the blocking play task is still not done.
        Assert.True(response.Ok, "Expected Accepted response");
        Assert.NotNull(response.PlaybackId);
        Assert.False(neverCompletes.Task.IsCompleted, "Play task must still be running — proves RunAsync did not block on it");
    }

    [Fact]
    public void ListMacros_ReturnsIdAndName_WithUnnamedFallback()
    {
        var inv = Build(
            macros: new[] { NewMacro("id-a", "Farm"), NewMacro("id-b", null) },
            running: Array.Empty<AccountRegistry.AccountInfo>(),
            busy: false);

        var list = inv.ListMacros();

        Assert.Equal(2, list.Count);
        Assert.Equal("Farm", Assert.Single(list, m => m.Id == "id-a").Name);
        Assert.Equal("(unnamed)", Assert.Single(list, m => m.Id == "id-b").Name);
    }

    [Fact]
    public async Task RunAsync_Repeat_LoopsUntilExternalCancel()
    {
        int plays = 0;
        var m = NewMacro("m1", "Farm");
        var invoker = new MacroRunInvoker(
            loadMacros: () => new[] { m },
            snapshot: () => new[] { Alt(1) },
            resolveForegroundUserId: () => 1L,
            isBusy: () => false,
            play: (macro, targets, delay, ct) =>
            {
                Interlocked.Increment(ref plays);
                return Task.CompletedTask;
            });

        var resp = await invoker.RunAsync(
            new RunMacroRequest("1.0", "RunMacro", "m1", new[] { "1" }, null, "626labs.ur-mcp", Repeat: true),
            CancellationToken.None);

        Assert.True(resp.Ok); // ack-on-accept
        // The loop spins on the injected instant-return play; let a few passes land, then stop it.
        Assert.True(SpinWait.SpinUntil(() => Volatile.Read(ref plays) >= 3, 2000));
        var stop = invoker.StopMacro(new StopMacroRequest("1.0", "StopMacro", resp.PlaybackId, null, "626labs.ur-mcp"));
        Assert.True(stop.Ok);
        Assert.True(SpinWait.SpinUntil(() => invoker.ActivePlaybackCount == 0, 2000));
    }

    [Fact]
    public async Task RunAsync_RefusesWhileAPlaybackIsActive()
    {
        var gate = new TaskCompletionSource();
        var m = NewMacro("m1", "Farm");
        var invoker = new MacroRunInvoker(
            loadMacros: () => new[] { m },
            snapshot: () => new[] { Alt(1) },
            resolveForegroundUserId: () => 1L,
            isBusy: () => false,
            play: async (mm, t, d, ct) => { await gate.Task.ConfigureAwait(false); });

        var first = await invoker.RunAsync(
            new RunMacroRequest("1.0", "RunMacro", "m1", new[] { "1" }, null, "x", Repeat: true), CancellationToken.None);
        Assert.True(first.Ok);
        Assert.True(SpinWait.SpinUntil(() => invoker.ActivePlaybackCount == 1, 2000));

        var second = await invoker.RunAsync(
            new RunMacroRequest("1.0", "RunMacro", "m1", new[] { "1" }, null, "x"), CancellationToken.None);
        Assert.False(second.Ok);
        Assert.Equal("busy", second.Reason);

        invoker.StopMacro(new StopMacroRequest("1.0", "StopMacro", null, null, "x"));
        gate.TrySetResult(); // let the first playback finish either way
        Assert.True(SpinWait.SpinUntil(() => invoker.ActivePlaybackCount == 0, 2000));
    }

    [Fact]
    public async Task StopMacro_ByPlaybackId_CancelsThatPlayback_AndAborts()
    {
        int aborts = 0;
        var m = NewMacro("m1", "Farm");
        var invoker = new MacroRunInvoker(
            loadMacros: () => new[] { m },
            snapshot: () => new[] { Alt(1) },
            resolveForegroundUserId: () => 1L,
            isBusy: () => false,
            play: async (mm, t, d, ct) => { await Task.Delay(Timeout.Infinite, ct).ConfigureAwait(false); },
            abort: () => { Interlocked.Increment(ref aborts); return true; });

        var run = await invoker.RunAsync(
            new RunMacroRequest("1.0", "RunMacro", "m1", new[] { "1" }, null, "x", Repeat: true), CancellationToken.None);
        Assert.True(SpinWait.SpinUntil(() => invoker.ActivePlaybackCount == 1, 2000));

        var stop = invoker.StopMacro(new StopMacroRequest("1.0", "StopMacro", run.PlaybackId, null, "x"));

        Assert.True(stop.Ok);
        Assert.Equal(1, stop.Stopped);
        Assert.Equal(1, aborts);
        Assert.True(SpinWait.SpinUntil(() => invoker.ActivePlaybackCount == 0, 2000));
    }

    [Fact]
    public void StopMacro_NoActivePlayback_ReturnsZero()
    {
        var invoker = new MacroRunInvoker(
            loadMacros: Array.Empty<Macro>,
            snapshot: Array.Empty<AccountRegistry.AccountInfo>,
            resolveForegroundUserId: () => null,
            isBusy: () => false,
            play: (m, t, d, ct) => Task.CompletedTask);

        var stop = invoker.StopMacro(new StopMacroRequest("1.0", "StopMacro", null, null, "x"));

        Assert.True(stop.Ok);
        Assert.Equal(0, stop.Stopped);
    }

    private static MacroRunInvoker BuildWithResult(Macro m, AccountRegistry.AccountInfo alt,
        Func<Macro, IReadOnlyList<AccountRegistry.AccountInfo>, int?, CancellationToken, Task<SequenceResult?>> playWithResult)
        => new MacroRunInvoker(
            loadMacros: () => new[] { m },
            snapshot: () => new[] { alt },
            resolveForegroundUserId: () => alt.RobloxUserId,
            isBusy: () => false,
            playWithResult: playWithResult);

    private static async Task WaitUntilAsync(Func<bool> cond)
    {
        for (int i = 0; i < 200 && !cond(); i++) await Task.Delay(10);
        Assert.True(cond());
    }

    [Fact]
    public async Task GetPlayback_reports_a_check_failure_and_repeat_stops_there()
    {
        var m = NewMacro(Guid.NewGuid().ToString());
        var alt = Alt(123);
        int passes = 0;
        var failed = new SequenceResult(new[]
        {
            new AltOutcome(alt, PlaybackOutcome.Aborted, "alt-123: step 2 'Tile' expected green #8BE03A, saw grey #969696 (distance 90) after 3.4 s at 100%.", 1),
        }, 0, 1, 0, TimeSpan.FromSeconds(4));
        var inv = BuildWithResult(m, alt, (_, _, _, _) => { passes++; return Task.FromResult<SequenceResult?>(failed); });

        var run = await inv.RunAsync(new RunMacroRequest("1.0", "RunMacro", m.Id, new[] { "123" }, null, "626labs.ur-mcp", Repeat: true), default);
        await WaitUntilAsync(() => inv.ActivePlaybackCount == 0);

        var status = inv.GetPlayback(new GetPlaybackRequest("1.0", "GetPlayback", run.PlaybackId, "626labs.ur-mcp"));
        Assert.Equal(("failed", "check-failed", 2), (status.State, status.Reason, status.StepIndex)); // AltOutcome index 1 → wire step 2
        Assert.StartsWith("alt-123: step 2 'Tile'", status.Detail);
        Assert.Equal(1, passes);
    }

    [Fact]
    public async Task GetPlayback_reports_finished_for_a_clean_pass()
    {
        var m = NewMacro(Guid.NewGuid().ToString());
        var alt = Alt(123);
        var ok = new SequenceResult(new[] { new AltOutcome(alt, PlaybackOutcome.Completed, null) }, 1, 0, 0, TimeSpan.FromSeconds(1));
        var inv = BuildWithResult(m, alt, (_, _, _, _) => Task.FromResult<SequenceResult?>(ok));

        var run = await inv.RunAsync(new RunMacroRequest("1.0", "RunMacro", m.Id, new[] { "123" }, null, "626labs.ur-mcp"), default);
        await WaitUntilAsync(() => inv.ActivePlaybackCount == 0);
        Assert.Equal("finished", inv.GetPlayback(new GetPlaybackRequest("1.0", "GetPlayback", run.PlaybackId, "626labs.ur-mcp")).State);
    }

    [Fact]
    public async Task GetPlayback_reports_stopped_after_StopMacro()
    {
        var m = NewMacro(Guid.NewGuid().ToString());
        var alt = Alt(123);
        var inv = BuildWithResult(m, alt, async (_, _, _, ct) => { await Task.Delay(Timeout.Infinite, ct); return null; });

        var run = await inv.RunAsync(new RunMacroRequest("1.0", "RunMacro", m.Id, new[] { "123" }, null, "626labs.ur-mcp"), default);
        Assert.Equal("running", inv.GetPlayback(new GetPlaybackRequest("1.0", "GetPlayback", run.PlaybackId, "626labs.ur-mcp")).State);
        inv.StopMacro(new StopMacroRequest("1.0", "StopMacro", run.PlaybackId, null, "626labs.ur-mcp"));
        await WaitUntilAsync(() => inv.ActivePlaybackCount == 0);
        Assert.Equal("stopped", inv.GetPlayback(new GetPlaybackRequest("1.0", "GetPlayback", run.PlaybackId, "626labs.ur-mcp")).State);
    }

    [Fact]
    public async Task A_refused_alt_does_not_end_a_repeat()
    {
        // A refusal or foreground shift with no step index is not a failed check. Repeat keeps
        // looping exactly as in 0.8 until StopMacro, and the playback then reads "stopped".
        var m = NewMacro(Guid.NewGuid().ToString());
        var alt = Alt(123);
        int passes = 0;
        var refused = new SequenceResult(new[] { new AltOutcome(alt, PlaybackOutcome.Refused, "Foreground window is user 9.") }, 0, 1, 0, TimeSpan.Zero);
        var inv = BuildWithResult(m, alt, async (_, _, _, ct) =>
        {
            Interlocked.Increment(ref passes);
            await Task.Delay(5, ct);
            return refused;
        });

        var run = await inv.RunAsync(new RunMacroRequest("1.0", "RunMacro", m.Id, new[] { "123" }, null, "626labs.ur-mcp", Repeat: true), default);
        await WaitUntilAsync(() => Volatile.Read(ref passes) >= 3);
        inv.StopMacro(new StopMacroRequest("1.0", "StopMacro", run.PlaybackId, null, "626labs.ur-mcp"));
        await WaitUntilAsync(() => inv.ActivePlaybackCount == 0);
        Assert.Equal("stopped", inv.GetPlayback(new GetPlaybackRequest("1.0", "GetPlayback", run.PlaybackId, "626labs.ur-mcp")).State);
    }

    [Fact]
    public async Task A_bridge_playback_logs_how_it_ended()
    {
        var m = NewMacro(Guid.NewGuid().ToString(), "Mine spot N");
        var alt = Alt(123);
        var lines = new List<string>();
        var failed = new SequenceResult(new[]
        {
            new AltOutcome(alt, PlaybackOutcome.Aborted, "alt-123: step 2 'Spot N' could not see the window.", 1),
        }, 0, 1, 0, TimeSpan.FromSeconds(1));
        var inv = new MacroRunInvoker(
            loadMacros: () => new[] { m },
            snapshot: () => new[] { alt },
            resolveForegroundUserId: () => alt.RobloxUserId,
            isBusy: () => false,
            playWithResult: (_, _, _, _) => Task.FromResult<SequenceResult?>(failed),
            log: lines.Add);

        var run = await inv.RunAsync(new RunMacroRequest("1.0", "RunMacro", m.Id, new[] { "123" }, null, "626labs.ur-mcp"), default);
        await WaitUntilAsync(() => inv.ActivePlaybackCount == 0);

        Assert.Equal($"bridge playback {run.PlaybackId} 'Mine spot N': failed (check-failed). alt-123: step 2 'Spot N' could not see the window.",
            Assert.Single(lines));
    }
}
