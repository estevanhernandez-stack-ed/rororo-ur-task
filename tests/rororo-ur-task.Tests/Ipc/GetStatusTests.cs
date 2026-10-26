using System.IO.Pipes;
using System.Text.Json;
using Labs626.UrTask.Ipc;
using Labs626.UrTask.Macros;
using Labs626.UrTask.PluginHost;

namespace Labs626.UrTask.Tests.Ipc;

public class GetStatusTests
{
    private static readonly DateTimeOffset Start = new(2026, 10, 25, 12, 0, 0, TimeSpan.FromHours(-5));

    private sealed class Rig
    {
        public long Mono = 10_000_000;
        public bool Breakglass, Running, Turbo;
        public HashSet<long> Active = new();
        public readonly KeepAliveLedger Ledger;
        public readonly ForegroundGate Gate;
        public readonly PlaybackRegistry Playbacks = new();
        public readonly StatusProvider Provider;

        public Rig()
        {
            Ledger = new KeepAliveLedger(() => Mono, () => Start + TimeSpan.FromMilliseconds(Mono - 10_000_000));
            Gate = new ForegroundGate(() => Mono, _ => { });
            Provider = new StatusProvider(Ledger, Gate, Playbacks, () => Breakglass, () => Running, () => Turbo, () => Active);
        }

        public void Track(long id, string name, int deadlineMin = 14)
            => Ledger.Track(new AccountRegistry.AccountInfo((int)id, id, name, $"acct-{id}"),
                new KeepAliveLimit(TimeSpan.FromMinutes(deadlineMin + 6), TimeSpan.FromMinutes(deadlineMin), false));
    }

    [Fact]
    public void Account_ReportsIdleDeadlineLimitAndSource()
    {
        var rig = new Rig();
        rig.Track(7, "Alt Seven");
        rig.Ledger.StampLanded(7, InputSource.Macro);
        rig.Mono += 125_000; // 2 min 5 s idle
        var s = rig.Provider.GetStatus();
        Assert.True(s.Ok);
        var a = Assert.Single(s.Accounts!);
        Assert.Equal("7", a.UserId);
        Assert.Equal("Alt Seven", a.Name);
        Assert.Equal("keep-alive", a.Role);
        Assert.Equal("macro", a.LastInputSource);
        Assert.Equal(125, a.IdleSeconds);
        Assert.Equal(20, a.LimitMinutes);
        Assert.Equal(0, a.Misses);
        Assert.Equal("waiting", a.State);
        Assert.Equal(Start.ToString("O"), a.LastInputAt);
        Assert.Equal(Start.AddMinutes(14).ToString("O"), a.DeadlineAt);
        Assert.Null(a.LastMiss);
    }

    [Fact]
    public void UnknownInput_HasNullIdleAndSource()
    {
        var rig = new Rig();
        rig.Track(7, "A");
        var a = Assert.Single(rig.Provider.GetStatus().Accounts!);
        Assert.Null(a.IdleSeconds);
        Assert.Null(a.LastInputAt);
        Assert.Null(a.LastInputSource);
    }

    [Fact]
    public void OffAccount_AppearsWithRoleOff()
    {
        var rig = new Rig();
        rig.Track(7, "A");
        rig.Ledger.SetOff(7, true);
        rig.Active.Add(7); // off wins over active
        var a = Assert.Single(rig.Provider.GetStatus().Accounts!);
        Assert.Equal("off", a.Role);
        Assert.Equal("off", a.State);
    }

    [Fact]
    public void ActiveAssignment_HasRoleActive()
    {
        var rig = new Rig();
        rig.Track(7, "A");
        rig.Track(8, "B");
        rig.Active.Add(7);
        var accounts = rig.Provider.GetStatus().Accounts!;
        Assert.Equal("active", accounts.Single(x => x.UserId == "7").Role);
        Assert.Equal("keep-alive", accounts.Single(x => x.UserId == "8").Role);
    }

    [Fact]
    public void LastMiss_UsesTheRunnersCauseText()
    {
        var rig = new Rig();
        rig.Track(7, "A");
        var miss = new Miss(MissCause.OtherInFront, "'Chrome' (chrome)");
        rig.Ledger.RecordMiss(7, miss);
        var a = Assert.Single(rig.Provider.GetStatus().Accounts!);
        Assert.Equal(1, a.Misses);
        Assert.Equal(MissReporter.CauseText(miss), a.LastMiss);
    }

    [Theory]
    [InlineData(false, false, "stopped")]
    [InlineData(false, true, "running")]
    [InlineData(true, true, "breakglass")]
    [InlineData(true, false, "breakglass")]
    public void KeepAliveState(bool breakglass, bool running, string expected)
    {
        var rig = new Rig { Breakglass = breakglass, Running = running, Turbo = true };
        var s = rig.Provider.GetStatus();
        Assert.Equal(expected, s.KeepAlive!.State);
        Assert.True(s.KeepAlive.Turbo);
    }

    [Fact]
    public void Playbacks_ListRunningOnes_AndPausedWhenGateParkedOnTheirTarget()
    {
        var rig = new Rig();
        rig.Playbacks.Started("pb1", new long[] { 7 });
        rig.Playbacks.Started("pb2", new long[] { 9 });
        rig.Playbacks.Started("done", new long[] { 7 });
        rig.Playbacks.Finished("done", PlaybackState.Finished, null, null, null);

        using var lease = rig.Gate.EnterPlayback(7, "pb1");
        _ = rig.Gate.RequestBreakAsync("Alt Nine", TimeSpan.FromMinutes(5), default);
        _ = rig.Gate.AtBoundaryAsync(0, 3, default); // parks

        var pbs = rig.Provider.GetStatus().Playbacks!;
        Assert.Equal(2, pbs.Count);
        var p1 = pbs.Single(p => p.PlaybackId == "pb1");
        Assert.True(p1.Paused);
        Assert.Equal("Alt Nine", p1.PausedFor);
        var p2 = pbs.Single(p => p.PlaybackId == "pb2");
        Assert.False(p2.Paused);
        Assert.Null(p2.PausedFor);
    }

    [Fact]
    public void Json_UsesTheAgreedCamelCaseKeys()
    {
        var rig = new Rig { Running = true };
        rig.Track(7, "A");
        rig.Ledger.StampLanded(7, InputSource.KeepAlive);
        rig.Playbacks.Started("pb1", new long[] { 7 });
        var json = JsonSerializer.Serialize(rig.Provider.GetStatus(), BridgeContract.Json);
        foreach (var key in new[] { "\"ok\":true", "\"keepAlive\":{", "\"state\":\"running\"", "\"turbo\":false", "\"accounts\":[",
                     "\"userId\":\"7\"", "\"name\":\"A\"", "\"role\":\"keep-alive\"", "\"lastInputAt\":", "\"lastInputSource\":\"keep-alive\"",
                     "\"idleSeconds\":0", "\"deadlineAt\":", "\"limitMinutes\":20", "\"misses\":0", "\"playbacks\":[",
                     "\"playbackId\":\"pb1\"", "\"paused\":false" })
            Assert.Contains(key, json);
        Assert.DoesNotContain("pausedFor", json); // null is not written
        Assert.DoesNotContain("lastMiss", json);
        var back = JsonSerializer.Deserialize<GetStatusResponse>(json, BridgeContract.Json)!;
        Assert.Equal("7", back.Accounts![0].UserId);
    }

    // ---- server ----

    private sealed class NoInvoker : IMacroRunInvoker
    {
        public Task<RunMacroResponse> RunAsync(RunMacroRequest request, CancellationToken ct) => throw new NotSupportedException();
        public Task<RunMacroResponse> ClearAtAsync(ClearAtRequest request, CancellationToken ct) => throw new NotSupportedException();
        public Task<RunMacroResponse> SweepPathAsync(SweepPathRequest request, CancellationToken ct) => throw new NotSupportedException();
        public IReadOnlyList<MacroSummary> ListMacros() => throw new NotSupportedException();
        public StopMacroResponse StopMacro(StopMacroRequest request) => throw new NotSupportedException();
        public GetPlaybackResponse GetPlayback(GetPlaybackRequest request) => throw new NotSupportedException();
    }

    private static async Task<string> RoundTripAsync(MacroRunnerServer server, string json)
    {
        var name = "626labs-ur-task-test-" + Guid.NewGuid().ToString("N");
        await using var srv = new NamedPipeServerStream(name, PipeDirection.InOut, 1, PipeTransmissionMode.Byte, PipeOptions.Asynchronous);
        await using var cli = new NamedPipeClientStream(".", name, PipeDirection.InOut, PipeOptions.Asynchronous);
        var waitConnect = srv.WaitForConnectionAsync();
        await cli.ConnectAsync(2000);
        await waitConnect;
        var serverSide = server.HandleConnectionAsync(srv, default);
        await FrameCodec.WriteFrameAsync(cli, System.Text.Encoding.UTF8.GetBytes(json), default);
        var resp = await FrameCodec.ReadFrameAsync(cli, default);
        await serverSide;
        return System.Text.Encoding.UTF8.GetString(resp!);
    }

    [Fact]
    public async Task Server_AnswersGetStatus()
    {
        var rig = new Rig();
        rig.Track(7, "A");
        var server = new MacroRunnerServer(new NoInvoker(), status: rig.Provider);
        var raw = await RoundTripAsync(server, """{"contractVersion":"1.0","method":"GetStatus","callerPluginId":"626labs.ur-mcp"}""");
        var r = JsonSerializer.Deserialize<GetStatusResponse>(raw, BridgeContract.Json)!;
        Assert.True(r.Ok);
        Assert.Single(r.Accounts!);
    }

    [Fact]
    public async Task Server_MissingCaller_Refused()
    {
        var server = new MacroRunnerServer(new NoInvoker(), status: new Rig().Provider);
        var r = JsonSerializer.Deserialize<GetStatusResponse>(
            await RoundTripAsync(server, """{"contractVersion":"1.0","method":"GetStatus"}"""), BridgeContract.Json)!;
        Assert.False(r.Ok);
        Assert.Equal("refused", r.Reason);
        Assert.Equal("Missing callerPluginId.", r.Detail);
    }

    [Fact]
    public async Task Server_WithoutProvider_Refused()
    {
        var server = new MacroRunnerServer(new NoInvoker());
        var r = JsonSerializer.Deserialize<GetStatusResponse>(
            await RoundTripAsync(server, """{"contractVersion":"1.0","method":"GetStatus","callerPluginId":"x"}"""), BridgeContract.Json)!;
        Assert.False(r.Ok);
        Assert.Equal("refused", r.Reason);
        Assert.Equal("GetStatus isn't available.", r.Detail);
    }

    [Fact]
    public async Task Server_UnknownMethod_StillRefused()
    {
        var server = new MacroRunnerServer(new NoInvoker(), status: new Rig().Provider);
        var r = JsonSerializer.Deserialize<GetStatusResponse>(
            await RoundTripAsync(server, """{"contractVersion":"1.0","method":"Nope","callerPluginId":"x"}"""), BridgeContract.Json)!;
        Assert.Equal("refused", r.Reason);
        Assert.Contains("Unknown method", r.Detail);
    }

    // ---- RunMacro note ----

    private static Macro MacroOfMillis(long ms) => new(
        SchemaVersion: 2, Id: "m1", Name: "m", RecordMode: "PerWindow",
        RecordedAgainstUserId: null, RecordedAgainstDisplayName: null,
        InterAltDelayMs: null, RecordedAtUnixMs: 0,
        Events: new List<MacroEvent> { new(ms, MacroEventKind.KeyDown, 0x20, 0, 0, 0, 0) });

    private static AccountRegistry.AccountInfo Alt(long id) => new((int)id, id, $"alt-{id}", $"acct-{id}");

    private static MacroRunInvoker InvokerWith(Macro macro, params KeepAliveDeadline[] deadlines)
        => new MacroRunInvoker(
            load: () => new MacroStore.LoadResult(new[] { macro }, Array.Empty<MacroStore.LoadFailure>()),
            snapshot: () => new[] { Alt(1) },
            resolveForegroundUserId: () => 1,
            isBusy: () => false,
            playWithResult: (m, t, d, c) => Task.FromResult<SequenceResult?>(null),
            deadlines: () => deadlines,
            now: () => Start);

    private static RunMacroRequest Req() => new("1.0", "RunMacro", "m1", new[] { "1" }, null, "x");

    [Fact]
    public async Task RunMacro_NotesTheEarliestOtherDeadlineItCrosses()
    {
        var inv = InvokerWith(MacroOfMillis(10 * 60_000),
            new KeepAliveDeadline(1, "self", Start.AddMinutes(2), false),          // own target: ignored
            new KeepAliveDeadline(3, "later", Start.AddMinutes(8), false),
            new KeepAliveDeadline(2, "sooner", Start.AddMinutes(5), false),
            new KeepAliveDeadline(4, "off one", Start.AddMinutes(1), true),        // off: ignored
            new KeepAliveDeadline(5, "beyond", Start.AddMinutes(30), false));
        var r = await inv.RunAsync(Req(), default);
        Assert.True(r.Ok);
        Assert.Equal("This run (about 10 min) crosses sooner's keep-alive deadline at 12:05; keep-alive will pause or stop it there for a Space.", r.Note);
    }

    [Fact]
    public async Task RunMacro_NoNote_WhenNoDeadlineIsCrossed()
    {
        var inv = InvokerWith(MacroOfMillis(3 * 60_000), new KeepAliveDeadline(2, "other", Start.AddMinutes(10), false));
        Assert.Null((await inv.RunAsync(Req(), default)).Note);
    }

    [Fact]
    public async Task RunMacro_Note_RoundsDurationUp()
    {
        var inv = InvokerWith(MacroOfMillis(90_000), new KeepAliveDeadline(2, "o", Start.AddMinutes(1), false));
        Assert.Contains("(about 2 min)", (await inv.RunAsync(Req(), default)).Note);
    }
    [Fact]
    public async Task RunMacro_NoNote_ForADeadlineAlreadyPast()
    {
        var inv = InvokerWith(MacroOfMillis(10 * 60_000), new KeepAliveDeadline(2, "overdue", Start.AddMinutes(-1), false));
        Assert.Null((await inv.RunAsync(Req(), default)).Note);
    }

    [Fact]
    public void Deadlines_AreEmpty_WhileKeepAliveIsStopped()
    {
        var rig = new Rig();
        rig.Track(7, "A");
        Assert.Empty(rig.Provider.Deadlines());
        rig.Running = true;
        Assert.Single(rig.Provider.Deadlines());
    }

    [Fact]
    public void Deadlines_LeaveOutActiveAccounts()
    {
        var rig = new Rig { Running = true };
        rig.Track(7, "A");
        rig.Track(8, "B");
        rig.Active.Add(7);
        Assert.Equal(new[] { 8L }, rig.Provider.Deadlines().Select(d => d.UserId));
    }
}
