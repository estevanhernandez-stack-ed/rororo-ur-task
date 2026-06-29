// tests/rororo-ur-task.Tests/Ipc/MacroRunnerServerTests.cs
using System.IO.Pipes;
using System.Text.Json;
using Labs626.UrTask.Ipc;

namespace Labs626.UrTask.Tests.Ipc;

public class MacroRunnerServerTests
{
    private sealed class FakeInvoker : IMacroRunInvoker
    {
        public RunMacroResponse Next { get; set; } = RunMacroResponse.Accepted("01TEST");
        public RunMacroRequest? Seen { get; private set; }
        public Task<RunMacroResponse> RunAsync(RunMacroRequest request, CancellationToken ct)
        {
            Seen = request;
            return Task.FromResult(Next);
        }
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
}
