// tests/rororo-ur-task.Tests/Ipc/MacroRunnerServerTests.cs
using System.IO.Pipes;
using System.Text.Json;
using Labs626.UrTask.Ipc;
using Labs626.UrTask.Macros;
using Labs626.UrTask.PluginHost;

namespace Labs626.UrTask.Tests.Ipc;

public class MacroRunnerServerTests
{
    private sealed class FakeInvoker : IMacroRunInvoker
    {
        public RunMacroResponse Next { get; set; } = RunMacroResponse.Accepted("01TEST");
        public RunMacroRequest? Seen { get; private set; }
        public IReadOnlyList<MacroSummary> Macros { get; set; } = Array.Empty<MacroSummary>();
        public StopMacroResponse StopResult { get; set; } = StopMacroResponse.Done(0);
        public StopMacroRequest? SeenStop { get; private set; }

        public Task<RunMacroResponse> RunAsync(RunMacroRequest request, CancellationToken ct)
        {
            Seen = request;
            return Task.FromResult(Next);
        }

        public IReadOnlyList<MacroSummary> ListMacros() => Macros;

        public StopMacroResponse StopMacro(StopMacroRequest request)
        {
            SeenStop = request;
            return StopResult;
        }

        public GetPlaybackResponse GetPlayback(GetPlaybackRequest request)
            => new(true, "finished", null, null, null);
    }

    // Drives one raw JSON payload through HandleConnectionAsync over an in-process named-pipe
    // pair and returns the raw response frame — the heterogeneous-method entry point; the typed
    // overload below keeps the pre-envelope tests unchanged.
    private static async Task<string> RoundTripJsonAsync(MacroRunnerServer server, string requestJson)
    {
        var name = "626labs-ur-task-test-" + Guid.NewGuid().ToString("N");
        await using var srv = new NamedPipeServerStream(name, PipeDirection.InOut, 1, PipeTransmissionMode.Byte, PipeOptions.Asynchronous);
        await using var cli = new NamedPipeClientStream(".", name, PipeDirection.InOut, PipeOptions.Asynchronous);

        var waitConnect = srv.WaitForConnectionAsync();
        await cli.ConnectAsync(2000);
        await waitConnect;

        var serverSide = server.HandleConnectionAsync(srv, default);

        await FrameCodec.WriteFrameAsync(cli, System.Text.Encoding.UTF8.GetBytes(requestJson), default);
        var respBytes = await FrameCodec.ReadFrameAsync(cli, default);
        await serverSide;

        return System.Text.Encoding.UTF8.GetString(respBytes!);
    }

    // Drives one request through HandleConnectionAsync over an in-process named-pipe pair.
    private static async Task<RunMacroResponse> RoundTripAsync(MacroRunnerServer server, RunMacroRequest req)
    {
        var name = "626labs-ur-task-test-" + Guid.NewGuid().ToString("N");
        await using var srv = new NamedPipeServerStream(name, PipeDirection.InOut, 1, PipeTransmissionMode.Byte, PipeOptions.Asynchronous);
        await using var cli = new NamedPipeClientStream(".", name, PipeDirection.InOut, PipeOptions.Asynchronous);

        var waitConnect = srv.WaitForConnectionAsync();
        await cli.ConnectAsync(2000);
        await waitConnect;

        var serverSide = server.HandleConnectionAsync(srv, default);

        var payload = JsonSerializer.SerializeToUtf8Bytes(req, BridgeContract.Json);
        await FrameCodec.WriteFrameAsync(cli, payload, default);
        var respBytes = await FrameCodec.ReadFrameAsync(cli, default);
        await serverSide;

        return JsonSerializer.Deserialize<RunMacroResponse>(respBytes!, BridgeContract.Json)!;
    }

    private static RunMacroRequest Valid(string method = "RunMacro", string version = "1.0", string? caller = "626labs.ur-ocr")
        => new(version, method, Guid.NewGuid().ToString(), new[] { "foreground" }, null, caller);

    [Fact]
    public async Task ValidRequest_DispatchesToInvoker_AndReturnsAck()
    {
        var invoker = new FakeInvoker { Next = RunMacroResponse.Accepted("01ABC") };
        var server = new MacroRunnerServer(invoker);

        var resp = await RoundTripAsync(server, Valid());

        Assert.True(resp.Ok);
        Assert.Equal("01ABC", resp.PlaybackId);
        Assert.NotNull(invoker.Seen);
    }

    [Fact]
    public async Task WrongMethod_RefusedWithoutDispatch()
    {
        var invoker = new FakeInvoker();
        var resp = await RoundTripAsync(new MacroRunnerServer(invoker), Valid(method: "Explode"));
        Assert.False(resp.Ok);
        Assert.Equal("refused", resp.Reason);
        Assert.Null(invoker.Seen);
    }

    [Fact]
    public async Task UnsupportedVersion_RefusedVersionMismatch()
    {
        var resp = await RoundTripAsync(new MacroRunnerServer(new FakeInvoker()), Valid(version: "2.0"));
        Assert.False(resp.Ok);
        Assert.Equal("version-mismatch", resp.Reason);
    }

    [Fact]
    public async Task MissingCallerPluginId_Refused()
    {
        var resp = await RoundTripAsync(new MacroRunnerServer(new FakeInvoker()), Valid(caller: null));
        Assert.False(resp.Ok);
        Assert.Equal("refused", resp.Reason);
    }

    [Fact]
    public async Task BusyInvoker_PropagatesBusyRefusal()
    {
        var invoker = new FakeInvoker { Next = RunMacroResponse.Refused("busy", "Sequence already running.") };
        var resp = await RoundTripAsync(new MacroRunnerServer(invoker), Valid());
        Assert.False(resp.Ok);
        Assert.Equal("busy", resp.Reason);
    }

    [Fact]
    public async Task ListMacros_Dispatches_AndReturnsMacros()
    {
        var invoker = new FakeInvoker { Macros = new[] { new MacroSummary("id-1", "Farm") } };
        var server = new MacroRunnerServer(invoker);

        var respJson = await RoundTripJsonAsync(server, "{\"contractVersion\":\"1.0\",\"method\":\"ListMacros\"}");
        var resp = JsonSerializer.Deserialize<ListMacrosResponse>(respJson, BridgeContract.Json)!;

        Assert.True(resp.Ok);
        Assert.Equal("Farm", Assert.Single(resp.Macros!).Name);
    }

    [Fact]
    public async Task StopMacro_Dispatches_AndReturnsOk()
    {
        var invoker = new FakeInvoker { StopResult = StopMacroResponse.Done(1) };
        var server = new MacroRunnerServer(invoker);

        var respJson = await RoundTripJsonAsync(server,
            "{\"contractVersion\":\"1.0\",\"method\":\"StopMacro\",\"playbackId\":\"pb-1\",\"callerPluginId\":\"x\"}");
        var resp = JsonSerializer.Deserialize<StopMacroResponse>(respJson, BridgeContract.Json)!;

        Assert.True(resp.Ok);
        Assert.Equal(1, resp.Stopped);
        Assert.Equal("pb-1", invoker.SeenStop!.PlaybackId);
    }

    [Fact]
    public async Task StopMacro_MissingCallerPluginId_Refused()
    {
        var server = new MacroRunnerServer(new FakeInvoker());

        var respJson = await RoundTripJsonAsync(server,
            "{\"contractVersion\":\"1.0\",\"method\":\"StopMacro\",\"playbackId\":\"pb-1\"}");
        var resp = JsonSerializer.Deserialize<StopMacroResponse>(respJson, BridgeContract.Json)!;

        Assert.False(resp.Ok);
        Assert.Equal("refused", resp.Reason);
    }

    [Fact]
    public async Task GetPlayback_Dispatches_AndReturnsState()
    {
        var server = new MacroRunnerServer(new FakeInvoker());

        var respJson = await RoundTripJsonAsync(server,
            "{\"contractVersion\":\"1.0\",\"method\":\"GetPlayback\",\"playbackId\":\"x\",\"callerPluginId\":\"t\"}");
        var resp = JsonSerializer.Deserialize<GetPlaybackResponse>(respJson, BridgeContract.Json)!;

        Assert.True(resp.Ok);
        Assert.Equal("finished", resp.State);
    }

    // Pin for Ur MCP: wait_for_macro detects a pre-0.9 Ur Task (no GetPlayback) by this exact
    // unknown-method detail. Do not reword it without coordinating the Ur MCP side.
    [Fact]
    public async Task UnknownMethod_RefusedWithThePinnedDetail()
    {
        var respJson = await RoundTripJsonAsync(new MacroRunnerServer(new FakeInvoker()),
            "{\"contractVersion\":\"1.0\",\"method\":\"NoSuchMethod\",\"callerPluginId\":\"t\"}");
        var resp = JsonSerializer.Deserialize<RunMacroResponse>(respJson, BridgeContract.Json)!;

        Assert.False(resp.Ok);
        Assert.Equal("refused", resp.Reason);
        Assert.Equal("Unknown method 'NoSuchMethod'.", resp.Detail);
    }

    // Pin for Ur OCR's pulse loop (controller ruling, 2026-09-28): a playback that pressed nothing
    // because reach checks skipped reads exactly this on the wire; a normal finish has no reason.
    [Fact]
    public async Task GetPlayback_over_the_pipe_reports_a_skip_and_a_clean_finish_without_a_reason()
    {
        var alt = new AccountRegistry.AccountInfo(1123, 123, "alt-123", "acct-123");
        var macro = new Macro(SchemaVersion: 2, Id: Guid.NewGuid().ToString(), Name: "Clear spot N", RecordMode: "PerWindow",
            RecordedAgainstUserId: null, RecordedAgainstDisplayName: null, InterAltDelayMs: null, RecordedAtUnixMs: 0,
            Events: new List<MacroEvent>());
        var passes = new Queue<SequenceResult>(new[]
        {
            new SequenceResult(new[] { new AltOutcome(alt, PlaybackOutcome.Completed, null, SkippedByReach: true) }, 1, 0, 0, TimeSpan.Zero),
            new SequenceResult(new[] { new AltOutcome(alt, PlaybackOutcome.Completed, null) }, 1, 0, 0, TimeSpan.Zero),
        });
        var invoker = new MacroRunInvoker(
            loadMacros: () => new[] { macro },
            snapshot: () => new[] { alt },
            resolveForegroundUserId: () => alt.RobloxUserId,
            isBusy: () => false,
            playWithResult: (_, _, _, _) => Task.FromResult<SequenceResult?>(passes.Dequeue()));
        var server = new MacroRunnerServer(invoker);

        async Task<string> RunAndReadAsync()
        {
            var run = await invoker.RunAsync(new RunMacroRequest("1.0", "RunMacro", macro.Id, new[] { "123" }, null, "626labs.ur-ocr"), default);
            for (int i = 0; i < 200 && invoker.ActivePlaybackCount > 0; i++) await Task.Delay(10);
            Assert.Equal(0, invoker.ActivePlaybackCount);
            return await RoundTripJsonAsync(server,
                $"{{\"contractVersion\":\"1.0\",\"method\":\"GetPlayback\",\"playbackId\":\"{run.PlaybackId}\",\"callerPluginId\":\"626labs.ur-ocr\"}}");
        }

        Assert.Equal("{\"ok\":true,\"state\":\"finished\",\"reason\":\"skipped\"}", await RunAndReadAsync());
        Assert.Equal("{\"ok\":true,\"state\":\"finished\"}", await RunAndReadAsync());
    }

    // Pin for Ur OCR: a lost single-flight claim (SequencePlayer.PlayAsync's empty-PerAlt refusal)
    // must not read as a plain finish on the wire, or a Clear spot that never ran counts as mined
    // (controller ruling, 2026-09-28).
    [Fact]
    public async Task GetPlayback_over_the_pipe_reports_a_lost_claim_as_failed_refused()
    {
        var alt = new AccountRegistry.AccountInfo(1123, 123, "alt-123", "acct-123");
        var macro = new Macro(SchemaVersion: 2, Id: Guid.NewGuid().ToString(), Name: "Clear spot N", RecordMode: "PerWindow",
            RecordedAgainstUserId: null, RecordedAgainstDisplayName: null, InterAltDelayMs: null, RecordedAtUnixMs: 0,
            Events: new List<MacroEvent>());
        var invoker = new MacroRunInvoker(
            loadMacros: () => new[] { macro },
            snapshot: () => new[] { alt },
            resolveForegroundUserId: () => alt.RobloxUserId,
            isBusy: () => false,
            playWithResult: (_, _, _, _) => Task.FromResult<SequenceResult?>(
                new SequenceResult(Array.Empty<AltOutcome>(), 0, 0, 0, TimeSpan.Zero)));
        var server = new MacroRunnerServer(invoker);

        var run = await invoker.RunAsync(new RunMacroRequest("1.0", "RunMacro", macro.Id, new[] { "123" }, null, "626labs.ur-ocr"), default);
        for (int i = 0; i < 200 && invoker.ActivePlaybackCount > 0; i++) await Task.Delay(10);
        Assert.Equal(0, invoker.ActivePlaybackCount);

        var respJson = await RoundTripJsonAsync(server,
            $"{{\"contractVersion\":\"1.0\",\"method\":\"GetPlayback\",\"playbackId\":\"{run.PlaybackId}\",\"callerPluginId\":\"626labs.ur-ocr\"}}");

        Assert.Equal(
            "{\"ok\":true,\"state\":\"failed\",\"reason\":\"refused\",\"detail\":\"Another playback took the sequence.\"}",
            respJson);
    }
}
