// tests/rororo-ur-task.Tests/Ipc/MacroRunnerServerAcceptLoopTests.cs
using System.IO.Pipes;
using System.Text;
using System.Text.Json;
using Labs626.UrTask.Ipc;

namespace Labs626.UrTask.Tests.Ipc;

/// <summary>
/// The accept loop serves one connection at a time on a single pipe instance (L4-03, L4-04). One
/// peer that stalled held that instance for good, so nobody else could even send StopMacro, and
/// three faults ended the loop for the session with no trace in a Release build.
/// </summary>
public class MacroRunnerServerAcceptLoopTests
{
    private sealed class ListingInvoker : IMacroRunInvoker
    {
        public IReadOnlyList<MacroSummary> Macros { get; set; } = new[] { new MacroSummary("id-a", "Farm") };
        public Func<IReadOnlyList<MacroSummary>>? OnList { get; set; }

        public IReadOnlyList<MacroSummary> ListMacros() => OnList?.Invoke() ?? Macros;
        public Task<RunMacroResponse> RunAsync(RunMacroRequest request, CancellationToken ct)
            => Task.FromResult(RunMacroResponse.Accepted("01TEST"));
        public Task<RunMacroResponse> ClearAtAsync(ClearAtRequest request, CancellationToken ct)
            => Task.FromResult(RunMacroResponse.Accepted("01TEST"));
        public Task<RunMacroResponse> SweepPathAsync(SweepPathRequest request, CancellationToken ct)
            => Task.FromResult(RunMacroResponse.Accepted("01TEST"));
        public StopMacroResponse StopMacro(StopMacroRequest request) => StopMacroResponse.Done(0);
        public GetPlaybackResponse GetPlayback(GetPlaybackRequest request) => new(true, "finished", null, null, null);
    }

    private const string ListMacrosJson = """{"contractVersion":"1.0","method":"ListMacros","callerPluginId":"626labs.ur-mcp"}""";

    // Short enough that a stalled peer costs a test about a second, long enough that a real
    // client's request still lands inside it when seven suites share the machine.
    private static readonly TimeSpan ShortIo = TimeSpan.FromSeconds(1);

    private static string NewPipeName() => "626labs-ur-task-test-" + Guid.NewGuid().ToString("N");

    private static async Task<NamedPipeClientStream> ConnectAsync(string pipeName, int timeoutMs = 5000)
    {
        var cli = new NamedPipeClientStream(".", pipeName, PipeDirection.InOut, PipeOptions.Asynchronous);
        await cli.ConnectAsync(timeoutMs);
        return cli;
    }

    /// <summary>One whole ListMacros round trip on a fresh connection, bounded so a hung bridge fails the test.</summary>
    private static async Task<ListMacrosResponse> ListMacrosAsync(string pipeName, int timeoutMs = 5000)
    {
        using var deadline = new CancellationTokenSource(timeoutMs);
        await using var cli = new NamedPipeClientStream(".", pipeName, PipeDirection.InOut, PipeOptions.Asynchronous);
        await cli.ConnectAsync(deadline.Token);
        await FrameCodec.WriteFrameAsync(cli, Encoding.UTF8.GetBytes(ListMacrosJson), deadline.Token);
        var resp = await FrameCodec.ReadFrameAsync(cli, deadline.Token);
        return JsonSerializer.Deserialize<ListMacrosResponse>(resp!, BridgeContract.Json)!;
    }

    private static async Task StopAsync(CancellationTokenSource cts, Task loop)
    {
        cts.Cancel();
        Assert.Same(loop, await Task.WhenAny(loop, Task.Delay(TimeSpan.FromSeconds(5))));
        await loop; // an unexpected exception surfaces here
    }

    [Fact]
    public async Task A_client_that_connects_and_sends_nothing_is_dropped_and_the_next_one_is_served()
    {
        var pipeName = NewPipeName();
        var log = new List<string>();
        var server = new MacroRunnerServer(new ListingInvoker(), pipeName, log: l => { lock (log) log.Add(l); }) { IoTimeout = ShortIo };
        using var cts = new CancellationTokenSource();
        var loop = server.RunAcceptLoopAsync(cts.Token);

        await using var silent = await ConnectAsync(pipeName); // holds the only pipe instance, says nothing

        var resp = await ListMacrosAsync(pipeName);

        Assert.True(resp.Ok);
        Assert.Equal("id-a", Assert.Single(resp.Macros!).Id);
        lock (log) Assert.Contains(log, l => l.Contains("sent no request", StringComparison.Ordinal));
        await StopAsync(cts, loop);
    }

    [Fact]
    public async Task A_client_that_never_reads_its_reply_is_dropped_and_the_next_one_is_served()
    {
        var pipeName = NewPipeName();
        // A reply far bigger than any pipe buffer, so the write cannot complete until it is read.
        var big = new ListingInvoker { Macros = Enumerable.Range(0, 900).Select(i => new MacroSummary($"id-{i:D4}", new string('x', 40))).ToList() };
        var log = new List<string>();
        var server = new MacroRunnerServer(big, pipeName, log: l => { lock (log) log.Add(l); }) { IoTimeout = ShortIo };
        using var cts = new CancellationTokenSource();
        var loop = server.RunAcceptLoopAsync(cts.Token);

        await using var deaf = await ConnectAsync(pipeName);
        await FrameCodec.WriteFrameAsync(deaf, Encoding.UTF8.GetBytes(ListMacrosJson), default); // asks, then never reads

        var resp = await ListMacrosAsync(pipeName);

        Assert.True(resp.Ok);
        Assert.Equal(900, resp.Macros!.Count);
        await StopAsync(cts, loop);
    }

    [Fact]
    public async Task The_default_deadline_for_a_request_and_for_a_reply_is_about_two_seconds()
    {
        var server = new MacroRunnerServer(new ListingInvoker(), NewPipeName());
        Assert.Equal(TimeSpan.FromSeconds(2), server.IoTimeout);
        Assert.Equal(TimeSpan.FromSeconds(2), MacroRunnerServer.DefaultIoTimeout);
        await Task.CompletedTask;
    }

    [Fact]
    public async Task A_cancellation_thrown_from_inside_a_handler_does_not_end_the_bridge()
    {
        // `catch (OperationCanceledException) { break; }` had no filter: any stray cancellation
        // from a handler read as "the bridge is shutting down" and ended it for the session.
        var pipeName = NewPipeName();
        int calls = 0;
        var invoker = new ListingInvoker();
        invoker.OnList = () => Interlocked.Increment(ref calls) == 1
            ? throw new OperationCanceledException("a token that is not the bridge's")
            : invoker.Macros;
        var log = new List<string>();
        var server = new MacroRunnerServer(invoker, pipeName, log: l => { lock (log) log.Add(l); }) { IoTimeout = ShortIo };
        using var cts = new CancellationTokenSource();
        var loop = server.RunAcceptLoopAsync(cts.Token);

        // The first caller hits the throwing handler: its connection is dropped without a reply.
        await Assert.ThrowsAnyAsync<Exception>(() => ListMacrosAsync(pipeName, 3000));

        var resp = await ListMacrosAsync(pipeName);
        Assert.True(resp.Ok);
        Assert.False(loop.IsCompleted);
        lock (log) Assert.Single(log, l => l.StartsWith("bridge pipe: dropped a connection", StringComparison.Ordinal));
        await StopAsync(cts, loop);
    }

    [Fact]
    public async Task A_malformed_frame_is_logged_once_and_the_next_client_is_served()
    {
        var pipeName = NewPipeName();
        var log = new List<string>();
        var server = new MacroRunnerServer(new ListingInvoker(), pipeName, log: l => { lock (log) log.Add(l); }) { IoTimeout = ShortIo };
        using var cts = new CancellationTokenSource();
        var loop = server.RunAcceptLoopAsync(cts.Token);

        await using (var hostile = await ConnectAsync(pipeName))
        {
            await hostile.WriteAsync(new byte[] { 0x7F, 0xFF, 0xFF, 0xFF }); // a 2 GB length prefix
            await hostile.FlushAsync();
        }

        var resp = await ListMacrosAsync(pipeName);
        Assert.True(resp.Ok);
        lock (log) Assert.Single(log, l => l.StartsWith("bridge pipe: dropped a connection", StringComparison.Ordinal));
        await StopAsync(cts, loop);
    }

    [Fact]
    public async Task A_pipe_that_cannot_be_created_is_retried_with_a_growing_wait_instead_of_killing_the_loop()
    {
        // Creating the pipe over a squatter with different settings throws
        // UnauthorizedAccessException, which only `catch (IOException)` guarded: the discarded
        // task faulted and the bridge was gone with no trace.
        var pipeName = NewPipeName();
        var log = new List<string>();
        var waits = new List<TimeSpan>();
        int attempts = 0;
        var server = new MacroRunnerServer(new ListingInvoker(), pipeName, log: l => { lock (log) log.Add(l); },
            delay: (wait, ct) => { lock (waits) waits.Add(wait); return Task.CompletedTask; })
        { IoTimeout = ShortIo };
        server.CreatePipe = real => Interlocked.Increment(ref attempts) <= 6
            ? throw new UnauthorizedAccessException("Access to the path is denied.")
            : real();
        using var cts = new CancellationTokenSource();
        var loop = server.RunAcceptLoopAsync(cts.Token);

        var resp = await ListMacrosAsync(pipeName);

        Assert.True(resp.Ok);
        lock (waits) Assert.Equal(new[] { 1, 2, 4, 8, 16, 30 }, waits.Select(w => (int)w.TotalSeconds));
        lock (log)
        {
            Assert.Equal(6, log.Count(l => l.StartsWith("bridge pipe: could not create it", StringComparison.Ordinal)));
            Assert.Contains(log, l => l.Contains("UnauthorizedAccessException", StringComparison.Ordinal));
        }
        await StopAsync(cts, loop);
    }

    [Fact]
    public async Task The_retry_wait_starts_over_once_the_pipe_has_been_created()
    {
        var pipeName = NewPipeName();
        var waits = new List<TimeSpan>();
        int attempts = 0;
        var server = new MacroRunnerServer(new ListingInvoker(), pipeName,
            delay: (wait, ct) => { lock (waits) waits.Add(wait); return Task.CompletedTask; })
        { IoTimeout = ShortIo };
        // Fails twice, works once (one client served), fails once more, then works.
        server.CreatePipe = real => Interlocked.Increment(ref attempts) is 1 or 2 or 4
            ? throw new UnauthorizedAccessException("denied")
            : real();
        using var cts = new CancellationTokenSource();
        var loop = server.RunAcceptLoopAsync(cts.Token);

        Assert.True((await ListMacrosAsync(pipeName)).Ok);
        Assert.True((await ListMacrosAsync(pipeName)).Ok);

        lock (waits) Assert.Equal(new[] { 1, 2, 1 }, waits.Select(w => (int)w.TotalSeconds));
        await StopAsync(cts, loop);
    }

    [Fact]
    public async Task A_pipe_another_instance_owns_stops_the_loop_and_says_so_once()
    {
        var pipeName = NewPipeName();
        await using var holder = new NamedPipeServerStream(
            pipeName, PipeDirection.InOut, maxNumberOfServerInstances: 1,
            PipeTransmissionMode.Byte, PipeOptions.Asynchronous | PipeOptions.CurrentUserOnly);
        var log = new List<string>();
        var server = new MacroRunnerServer(new ListingInvoker(), pipeName, log: l => { lock (log) log.Add(l); });
        using var cts = new CancellationTokenSource();

        var loop = server.RunAcceptLoopAsync(cts.Token);

        Assert.Same(loop, await Task.WhenAny(loop, Task.Delay(TimeSpan.FromSeconds(5))));
        await loop;
        lock (log) Assert.StartsWith("bridge pipe: another Ur Task already owns it", Assert.Single(log));
    }
}
