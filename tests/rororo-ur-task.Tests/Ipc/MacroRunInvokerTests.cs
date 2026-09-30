// tests/rororo-ur-task.Tests/Ipc/MacroRunInvokerTests.cs
using System.IO;
using Labs626.UrTask.Ipc;
using Labs626.UrTask.Macros;
using Labs626.UrTask.Macros.Steps;
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

    [Theory]
    [InlineData(true, "Aborted (StopMacro from 626labs.ur-ocr).")]
    [InlineData(false, "Abort ignored — nothing playing (StopMacro from 626labs.ur-ocr).")]
    public void StopMacro_logs_the_caller(bool playing, string line)
    {
        var lines = new List<string>();
        var invoker = new MacroRunInvoker(
            loadMacros: Array.Empty<Macro>,
            snapshot: Array.Empty<AccountRegistry.AccountInfo>,
            resolveForegroundUserId: () => null,
            isBusy: () => false,
            playWithResult: (m, t, d, ct) => Task.FromResult<SequenceResult?>(null),
            abort: () => playing,
            log: lines.Add);

        invoker.StopMacro(new StopMacroRequest("1.0", "StopMacro", null, null, "626labs.ur-ocr"));

        Assert.Equal(new[] { line }, lines);
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
        var status = inv.GetPlayback(new GetPlaybackRequest("1.0", "GetPlayback", run.PlaybackId, "626labs.ur-mcp"));
        Assert.Equal(("finished", (string?)null), (status.State, status.Reason)); // a normal finish has no reason
    }

    [Fact]
    public async Task GetPlayback_reports_finished_skipped_when_every_alt_was_skipped_by_reach()
    {
        var m = NewMacro(Guid.NewGuid().ToString(), "Clear spot N");
        var alt = Alt(123);
        var skipped = new SequenceResult(new[] { new AltOutcome(alt, PlaybackOutcome.Completed, null, SkippedByReach: true) }, 1, 0, 0, TimeSpan.FromSeconds(1));
        var inv = BuildWithResult(m, alt, (_, _, _, _) => Task.FromResult<SequenceResult?>(skipped));

        var run = await inv.RunAsync(new RunMacroRequest("1.0", "RunMacro", m.Id, new[] { "123" }, null, "626labs.ur-ocr"), default);
        await WaitUntilAsync(() => inv.ActivePlaybackCount == 0);

        var status = inv.GetPlayback(new GetPlaybackRequest("1.0", "GetPlayback", run.PlaybackId, "626labs.ur-ocr"));
        Assert.True(status.Ok);
        Assert.Equal(("finished", "skipped", (string?)null, (int?)null), (status.State, status.Reason, status.Detail, status.StepIndex));
    }

    [Fact]
    public async Task GetPlayback_has_no_reason_when_any_alt_pressed()
    {
        var m = NewMacro(Guid.NewGuid().ToString(), "Clear spot N");
        var alt = Alt(123);
        var mixed = new SequenceResult(new[]
        {
            new AltOutcome(alt, PlaybackOutcome.Completed, null, SkippedByReach: true),
            new AltOutcome(Alt(124), PlaybackOutcome.Completed, null),
        }, 2, 0, 0, TimeSpan.FromSeconds(1));
        var inv = BuildWithResult(m, alt, (_, _, _, _) => Task.FromResult<SequenceResult?>(mixed));

        var run = await inv.RunAsync(new RunMacroRequest("1.0", "RunMacro", m.Id, new[] { "123" }, null, "626labs.ur-ocr"), default);
        await WaitUntilAsync(() => inv.ActivePlaybackCount == 0);

        var status = inv.GetPlayback(new GetPlaybackRequest("1.0", "GetPlayback", run.PlaybackId, "626labs.ur-ocr"));
        Assert.Equal(("finished", (string?)null), (status.State, status.Reason));
    }

    [Fact]
    public async Task A_skipped_bridge_playback_logs_finished_skipped()
    {
        var m = NewMacro(Guid.NewGuid().ToString(), "Clear spot N");
        var alt = Alt(123);
        var lines = new List<string>();
        var skipped = new SequenceResult(new[] { new AltOutcome(alt, PlaybackOutcome.Completed, null, SkippedByReach: true) }, 1, 0, 0, TimeSpan.FromSeconds(1));
        var inv = new MacroRunInvoker(
            loadMacros: () => new[] { m },
            snapshot: () => new[] { alt },
            resolveForegroundUserId: () => alt.RobloxUserId,
            isBusy: () => false,
            playWithResult: (_, _, _, _) => Task.FromResult<SequenceResult?>(skipped),
            log: lines.Add);

        var run = await inv.RunAsync(new RunMacroRequest("1.0", "RunMacro", m.Id, new[] { "123" }, null, "626labs.ur-ocr"), default);
        await WaitUntilAsync(() => inv.ActivePlaybackCount == 0);

        Assert.Equal($"bridge playback {run.PlaybackId} 'Clear spot N': finished (skipped)", Assert.Single(lines));
    }

    [Fact]
    public async Task GetPlayback_reports_stopped_when_the_sequence_was_aborted_before_an_alt()
    {
        // Esc during the focus delay: SequencePlayer ends that alt as PlaybackOutcome.Skipped
        // ("Sequence aborted."), not SkippedByReach. It never played, so Ur OCR must not read it
        // as a cleared spot (controller ruling, 2026-09-28). A repeat ends there too.
        var m = NewMacro(Guid.NewGuid().ToString(), "Clear spot N");
        var alt = Alt(123);
        int passes = 0;
        var aborted = new SequenceResult(new[] { new AltOutcome(alt, PlaybackOutcome.Skipped, "Sequence aborted.") }, 0, 0, 1, TimeSpan.FromSeconds(1));
        var inv = BuildWithResult(m, alt, (_, _, _, _) => { passes++; return Task.FromResult<SequenceResult?>(aborted); });

        var run = await inv.RunAsync(new RunMacroRequest("1.0", "RunMacro", m.Id, new[] { "123" }, null, "626labs.ur-ocr", Repeat: true), default);
        await WaitUntilAsync(() => inv.ActivePlaybackCount == 0);

        var status = inv.GetPlayback(new GetPlaybackRequest("1.0", "GetPlayback", run.PlaybackId, "626labs.ur-ocr"));
        Assert.True(status.Ok);
        Assert.Equal(("stopped", (string?)null, (string?)null, (int?)null), (status.State, status.Reason, status.Detail, status.StepIndex));
        Assert.Equal(1, passes);
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
    public async Task A_lost_single_flight_claim_reads_failed_refused_not_a_clean_finish()
    {
        // SequencePlayer.PlayAsync answers a lost CompareExchange claim with an empty PerAlt on a
        // non-null result. That must not read as a plain "finished" — Ur OCR's MacroCall maps plain
        // finished on a Clear spot to Done ("cleared"), and a clear that never ran would count as
        // mined (controller ruling, 2026-09-28).
        var m = NewMacro(Guid.NewGuid().ToString());
        var alt = Alt(123);
        int passes = 0;
        var lost = new SequenceResult(Array.Empty<AltOutcome>(), 0, 0, 0, TimeSpan.Zero);
        var inv = BuildWithResult(m, alt, (_, _, _, _) => { Interlocked.Increment(ref passes); return Task.FromResult<SequenceResult?>(lost); });

        var run = await inv.RunAsync(
            new RunMacroRequest("1.0", "RunMacro", m.Id, new[] { "123" }, null, "626labs.ur-ocr", Repeat: true), default);
        await WaitUntilAsync(() => inv.ActivePlaybackCount == 0);

        var status = inv.GetPlayback(new GetPlaybackRequest("1.0", "GetPlayback", run.PlaybackId, "626labs.ur-ocr"));
        Assert.Equal(("failed", "refused", "Another playback took the sequence.", (int?)null),
            (status.State, status.Reason, status.Detail, status.StepIndex));
        // The break kills the hot spin: a repeat with a losing PlayAsync that completes
        // synchronously must not spin — exactly one pass.
        Assert.Equal(1, passes);
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

    // ---------- ClearAt ----------

    private static ClearAtRequest ClearAt(string target = "123", int points = 2) => new(
        "1.0", "ClearAt", "626labs.ur-ocr", target, new ClearAtClient(800, 599),
        Enumerable.Range(0, points).Select(i => new ClearAtPoint(400 + i * 50, 300, $"ore {i + 1}")).ToList(),
        new ClearAtOutline(50, 50, 60));

    private static MacroRunInvoker ClearAtInvoker(
        Func<Macro, IReadOnlyList<AccountRegistry.AccountInfo>, int?, CancellationToken, Task<SequenceResult?>> play,
        bool busy = false, IReadOnlyList<Macro>? saved = null, List<string>? log = null,
        Func<IReadOnlyList<Macro>>? loadMacros = null)
        => new MacroRunInvoker(
            loadMacros: loadMacros ?? (() => saved ?? Array.Empty<Macro>()),
            snapshot: () => new[] { Alt(123), Alt(456) },
            resolveForegroundUserId: () => 123L,
            isBusy: () => busy,
            playWithResult: play,
            log: log is null ? null : new Action<string>(log.Add));

    private static GetPlaybackResponse Status(MacroRunInvoker inv, string? id)
        => inv.GetPlayback(new GetPlaybackRequest("1.0", "GetPlayback", id, "626labs.ur-ocr"));

    [Fact]
    public async Task ClearAt_refuses_a_malformed_call_before_the_busy_check()
    {
        var inv = ClearAtInvoker((_, _, _, _) => Task.FromResult<SequenceResult?>(null), busy: true);
        var r = await inv.ClearAtAsync(ClearAt(points: 0), default);
        Assert.Equal((false, "refused", "ClearAt takes 1 to 64 points; got 0."), (r.Ok, r.Reason, r.Detail));
    }

    [Fact]
    public async Task ClearAt_refuses_while_busy()
    {
        var inv = ClearAtInvoker((_, _, _, _) => Task.FromResult<SequenceResult?>(null), busy: true);
        var r = await inv.ClearAtAsync(ClearAt(), default);
        Assert.Equal((false, "busy"), (r.Ok, r.Reason));
        Assert.Equal(0, inv.ActivePlaybackCount);
    }

    [Fact]
    public async Task ClearAt_refuses_an_account_that_is_not_running()
    {
        var inv = ClearAtInvoker((_, _, _, _) => Task.FromResult<SequenceResult?>(null));
        var r = await inv.ClearAtAsync(ClearAt(target: "999"), default);
        Assert.Equal((false, "no-targets-resolved", "Account 999 is not running."), (r.Ok, r.Reason, r.Detail));
    }

    [Fact]
    public async Task ClearAt_plays_one_unsaved_macro_of_reach_holds_on_the_target()
    {
        Macro? played = null;
        IReadOnlyList<AccountRegistry.AccountInfo>? on = null;
        var inv = ClearAtInvoker(
            (m, t, _, _) => { played = m; on = t; return Task.FromResult<SequenceResult?>(null); },
            saved: new[] { NewMacro("saved", "Farm") });

        var r = await inv.ClearAtAsync(ClearAt(target: "456"), default);
        await WaitUntilAsync(() => inv.ActivePlaybackCount == 0);

        Assert.True(r.Ok);
        Assert.Equal(new long[] { 456 }, on!.Select(a => a.RobloxUserId));
        Assert.Equal(("ClearAt (2 points)", ClearAtMacro.IdPrefix + r.PlaybackId), (played!.Name, played.Id));
        Assert.Equal(new[] { "ore 1", "ore 2" }, played.Steps!.Cast<HoldStep>().Select(h => h.Label));
        Assert.Equal(new[] { "saved" }, inv.ListMacros().Select(m => m.Id)); // never saved, never listed
        Assert.Equal("finished", Status(inv, r.PlaybackId).State);
    }

    [Fact]
    public async Task ClearAt_never_writes_to_the_macro_store_or_shows_in_ListMacros()
    {
        var dir = Path.Combine(Path.GetTempPath(), "urtask-clearat-" + Guid.NewGuid().ToString("N"));
        try
        {
            var store = new MacroStore(dir);
            var savedId = Guid.NewGuid().ToString();
            store.Save(NewMacro(savedId, "Farm"));
            var before = Directory.GetFiles(dir).Select(Path.GetFileName).OrderBy(n => n).ToList();
            var gate = new TaskCompletionSource();
            var inv = ClearAtInvoker(async (_, _, _, _) => { await gate.Task; return null; },
                loadMacros: () => store.LoadAll().Macros);

            var r = await inv.ClearAtAsync(ClearAt(), default);
            Assert.True(r.Ok);
            Assert.Equal(new[] { savedId }, inv.ListMacros().Select(m => m.Id)); // not listed while it plays
            gate.TrySetResult();
            await WaitUntilAsync(() => inv.ActivePlaybackCount == 0);

            Assert.Equal(new[] { savedId }, inv.ListMacros().Select(m => m.Id)); // nor after it ends
            Assert.Equal(before, Directory.GetFiles(dir).Select(Path.GetFileName).OrderBy(n => n).ToList());
        }
        finally { try { Directory.Delete(dir, true); } catch { } }
    }

    [Fact]
    public async Task ClearAt_reads_finished_skipped_when_every_point_was_skipped()
    {
        var alt = Alt(123);
        var skipped = new SequenceResult(new[] { new AltOutcome(alt, PlaybackOutcome.Completed, null, SkippedByReach: true) }, 1, 0, 0, TimeSpan.Zero);
        var inv = ClearAtInvoker((_, _, _, _) => Task.FromResult<SequenceResult?>(skipped));

        var r = await inv.ClearAtAsync(ClearAt(), default);
        await WaitUntilAsync(() => inv.ActivePlaybackCount == 0);

        var status = Status(inv, r.PlaybackId);
        Assert.Equal(("finished", "skipped"), (status.State, status.Reason));
    }

    [Fact]
    public async Task A_finished_ClearAt_names_the_points_that_showed_no_outline()
    {
        var alt = Alt(123);
        var pass = new SequenceResult(new[] { new AltOutcome(alt, PlaybackOutcome.Completed, null, NoOutline: new[] { 1, 3 }) }, 1, 0, 0, TimeSpan.Zero);
        var inv = ClearAtInvoker((_, _, _, _) => Task.FromResult<SequenceResult?>(pass));

        var r = await inv.ClearAtAsync(ClearAt(points: 3), default);
        await WaitUntilAsync(() => inv.ActivePlaybackCount == 0);

        var status = Status(inv, r.PlaybackId);
        Assert.Equal(("finished", (string?)null), (status.State, status.Reason));
        Assert.Equal(new[] { 1, 3 }, status.NoOutline);
    }

    [Fact]
    public async Task A_finished_ClearAt_with_every_point_mined_names_an_empty_list()
    {
        var alt = Alt(123);
        var pass = new SequenceResult(new[] { new AltOutcome(alt, PlaybackOutcome.Completed, null) }, 1, 0, 0, TimeSpan.Zero);
        var inv = ClearAtInvoker((_, _, _, _) => Task.FromResult<SequenceResult?>(pass));

        var r = await inv.ClearAtAsync(ClearAt(), default);
        await WaitUntilAsync(() => inv.ActivePlaybackCount == 0);

        Assert.Equal(Array.Empty<int>(), Status(inv, r.PlaybackId).NoOutline);
    }

    [Fact]
    public async Task A_failed_ClearAt_names_no_points()
    {
        // Only a finished pass says which spots were empty; a stopped one never looked at the rest.
        var alt = Alt(123);
        var pass = new SequenceResult(new[] { new AltOutcome(alt, PlaybackOutcome.Aborted, "guard moved", 1, NoOutline: new[] { 1 }) }, 0, 1, 0, TimeSpan.Zero);
        var inv = ClearAtInvoker((_, _, _, _) => Task.FromResult<SequenceResult?>(pass));

        var r = await inv.ClearAtAsync(ClearAt(), default);
        await WaitUntilAsync(() => inv.ActivePlaybackCount == 0);

        var status = Status(inv, r.PlaybackId);
        Assert.Equal("failed", status.State);
        Assert.Null(status.NoOutline);
    }

    [Fact]
    public async Task A_saved_macro_never_names_no_outline_points()
    {
        var alt = Alt(123);
        var m = NewMacro(Guid.NewGuid().ToString(), "Mine spot N");
        var pass = new SequenceResult(new[] { new AltOutcome(alt, PlaybackOutcome.Completed, null, NoOutline: new[] { 1 }) }, 1, 0, 0, TimeSpan.Zero);
        var inv = BuildWithResult(m, alt, (_, _, _, _) => Task.FromResult<SequenceResult?>(pass));

        var run = await inv.RunAsync(new RunMacroRequest("1.0", "RunMacro", m.Id, new[] { "123" }, null, "626labs.ur-ocr"), default);
        await WaitUntilAsync(() => inv.ActivePlaybackCount == 0);

        Assert.Null(Status(inv, run.PlaybackId).NoOutline);
    }

    [Fact]
    public async Task ClearAt_logs_its_guard_once_at_the_start_and_plays_it()
    {
        Macro? played = null;
        var log = new List<string>();
        var inv = ClearAtInvoker((m, _, _, _) => { played = m; return Task.FromResult<SequenceResult?>(null); }, log: log);

        var r = await inv.ClearAtAsync(ClearAt() with { Guard = new ClearAtGuard(55, 289, 3, 3, new Rgb(255, 19, 90), 30) }, default);
        await WaitUntilAsync(() => inv.ActivePlaybackCount == 0);

        Assert.True(r.Ok);
        Assert.Single(log, l => l == "ClearAt guard at (55,289), expecting #FF135A ±30");
        Assert.Equal(new ScreenGuard(55, 289, 3, 3, new Rgb(255, 19, 90), 30), played!.Guard);
    }

    [Fact]
    public async Task ClearAt_without_a_guard_logs_none()
    {
        var log = new List<string>();
        var inv = ClearAtInvoker((_, _, _, _) => Task.FromResult<SequenceResult?>(null), log: log);
        await inv.ClearAtAsync(ClearAt(), default);
        await WaitUntilAsync(() => inv.ActivePlaybackCount == 0);
        Assert.DoesNotContain(log, l => l.Contains("guard"));
    }

    [Fact]
    public async Task A_ClearAt_stopped_by_its_guard_reads_failed_check_failed_with_the_sentence()
    {
        const string stop = "ClearAt stopped: the guard at (55,289) isn't the expected colour (saw #F5F5F5); something may be over the game (a menu or a player's profile).";
        var failed = new SequenceResult(new[] { new AltOutcome(Alt(123), PlaybackOutcome.Aborted, stop, 2) }, 0, 1, 0, TimeSpan.Zero);
        var inv = ClearAtInvoker((_, _, _, _) => Task.FromResult<SequenceResult?>(failed));

        var r = await inv.ClearAtAsync(ClearAt(points: 3) with { Guard = new ClearAtGuard(55, 289, 3, 3, new Rgb(255, 19, 90), 30) }, default);
        await WaitUntilAsync(() => inv.ActivePlaybackCount == 0);

        var status = Status(inv, r.PlaybackId);
        Assert.Equal(("failed", "check-failed", 3, stop), (status.State, status.Reason, status.StepIndex, status.Detail));
    }

    [Fact]
    public async Task ClearAt_is_stopped_by_StopMacro_like_any_playback()
    {
        var inv = ClearAtInvoker(async (_, _, _, ct) => { await Task.Delay(Timeout.Infinite, ct); return null; });

        var r = await inv.ClearAtAsync(ClearAt(), default);
        Assert.Equal("running", Status(inv, r.PlaybackId).State);
        inv.StopMacro(new StopMacroRequest("1.0", "StopMacro", r.PlaybackId, null, "626labs.ur-ocr"));
        await WaitUntilAsync(() => inv.ActivePlaybackCount == 0);

        Assert.Equal("stopped", Status(inv, r.PlaybackId).State);
    }

    [Fact]
    public async Task ClearAt_and_RunMacro_share_the_single_flight_rule()
    {
        var gate = new TaskCompletionSource();
        var inv = ClearAtInvoker(async (_, _, _, _) => { await gate.Task; return null; }, saved: new[] { NewMacro("m1", "Farm") });

        var clear = await inv.ClearAtAsync(ClearAt(), default);
        var run = await inv.RunAsync(new RunMacroRequest("1.0", "RunMacro", "m1", new[] { "123" }, null, "626labs.ur-mcp"), default);
        var again = await inv.ClearAtAsync(ClearAt(), default);

        Assert.True(clear.Ok);
        Assert.Equal(("busy", "busy"), (run.Reason, again.Reason));
        Assert.Equal(1, inv.ActivePlaybackCount);
        gate.TrySetResult();
        await WaitUntilAsync(() => inv.ActivePlaybackCount == 0);
    }

    [Fact]
    public async Task ClearAt_logs_its_points_by_label_and_how_it_ended()
    {
        var lines = new List<string>();
        var inv = ClearAtInvoker((_, _, _, _) => Task.FromResult<SequenceResult?>(null), log: lines);

        var r = await inv.ClearAtAsync(ClearAt(), default);
        await WaitUntilAsync(() => inv.ActivePlaybackCount == 0);

        Assert.Equal(new[]
        {
            $"bridge playback {r.PlaybackId} 'ClearAt (2 points)' on alt-123: 'ore 1' at 400,300; 'ore 2' at 450,300",
            $"bridge playback {r.PlaybackId} 'ClearAt (2 points)': finished",
        }, lines);
    }

    // ---------- SweepPath ----------

    private static SweepPathRequest SweepAt(string target = "123", int step = 50) => new(
        "1.0", "SweepPath", "626labs.ur-ocr", target, new ClearAtClient(800, 599),
        new[] { new SweepPoint(450, 300), new SweepPoint(450, 250), new SweepPoint(400, 250), new SweepPoint(450, 300) },
        step, 400, new ClearAtGuard(55, 289, 3, 3, new Rgb(255, 19, 90), 30));

    [Fact]
    public async Task SweepPath_refuses_a_malformed_call_before_the_busy_check()
    {
        var inv = ClearAtInvoker((_, _, _, _) => Task.FromResult<SequenceResult?>(null), busy: true);
        var r = await inv.SweepPathAsync(SweepAt(step: 7), default);
        Assert.Equal((false, "refused", "SweepPath needs a step of 8 to 240 px; got 7."), (r.Ok, r.Reason, r.Detail));
    }

    [Fact]
    public async Task SweepPath_refuses_while_busy()
    {
        var inv = ClearAtInvoker((_, _, _, _) => Task.FromResult<SequenceResult?>(null), busy: true);
        var r = await inv.SweepPathAsync(SweepAt(), default);
        Assert.Equal((false, "busy"), (r.Ok, r.Reason));
        Assert.Equal(0, inv.ActivePlaybackCount);
    }

    [Fact]
    public async Task SweepPath_refuses_an_account_that_is_not_running()
    {
        var inv = ClearAtInvoker((_, _, _, _) => Task.FromResult<SequenceResult?>(null));
        var r = await inv.SweepPathAsync(SweepAt(target: "999"), default);
        Assert.Equal((false, "no-targets-resolved", "Account 999 is not running."), (r.Ok, r.Reason, r.Detail));
    }

    [Fact]
    public async Task SweepPath_plays_one_unsaved_sweep_on_the_target()
    {
        Macro? played = null;
        IReadOnlyList<AccountRegistry.AccountInfo>? on = null;
        var inv = ClearAtInvoker(
            (m, t, _, _) => { played = m; on = t; return Task.FromResult<SequenceResult?>(null); },
            saved: new[] { NewMacro("saved", "Farm") });

        var r = await inv.SweepPathAsync(SweepAt(target: "456"), default);
        await WaitUntilAsync(() => inv.ActivePlaybackCount == 0);

        Assert.True(r.Ok);
        Assert.Equal(new long[] { 456 }, on!.Select(a => a.RobloxUserId));
        Assert.Equal(("SweepPath (4 points)", SweepPathMacro.IdPrefix + r.PlaybackId), (played!.Name, played.Id));
        Assert.Equal(400, Assert.IsType<SweepStep>(Assert.Single(played.Steps!)).DwellMs);
        Assert.NotNull(played.Guard);
        Assert.Equal(new[] { "saved" }, inv.ListMacros().Select(m => m.Id)); // never saved, never listed
        Assert.Equal("finished", Status(inv, r.PlaybackId).State);
    }

    [Fact]
    public async Task SweepPath_is_stopped_by_StopMacro_like_any_playback()
    {
        var inv = ClearAtInvoker(async (_, _, _, ct) => { await Task.Delay(Timeout.Infinite, ct); return null; });

        var r = await inv.SweepPathAsync(SweepAt(), default);
        Assert.Equal("running", Status(inv, r.PlaybackId).State);
        inv.StopMacro(new StopMacroRequest("1.0", "StopMacro", r.PlaybackId, null, "626labs.ur-ocr"));
        await WaitUntilAsync(() => inv.ActivePlaybackCount == 0);

        Assert.Equal("stopped", Status(inv, r.PlaybackId).State);
    }

    [Fact]
    public async Task SweepPath_and_ClearAt_share_the_single_flight_rule()
    {
        var gate = new TaskCompletionSource();
        var inv = ClearAtInvoker(async (_, _, _, _) => { await gate.Task; return null; });

        var sweep = await inv.SweepPathAsync(SweepAt(), default);
        var clear = await inv.ClearAtAsync(ClearAt(), default);
        var again = await inv.SweepPathAsync(SweepAt(), default);

        Assert.True(sweep.Ok);
        Assert.Equal(("busy", "busy"), (clear.Reason, again.Reason));
        Assert.Equal(1, inv.ActivePlaybackCount);
        gate.TrySetResult();
        await WaitUntilAsync(() => inv.ActivePlaybackCount == 0);
    }

    [Fact]
    public async Task SweepPath_logs_its_start_its_guard_and_how_it_ended()
    {
        var lines = new List<string>();
        var inv = ClearAtInvoker((_, _, _, _) => Task.FromResult<SequenceResult?>(null), log: lines);

        var r = await inv.SweepPathAsync(SweepAt(), default);
        await WaitUntilAsync(() => inv.ActivePlaybackCount == 0);

        Assert.Equal(new[]
        {
            $"bridge playback {r.PlaybackId} 'SweepPath (4 points)' on alt-123: from 450,300, 50 px steps, 400 ms a point",
            "SweepPath guard at (55,289), expecting #FF135A ±30",
            $"bridge playback {r.PlaybackId} 'SweepPath (4 points)': finished",
        }, lines);
    }
}
