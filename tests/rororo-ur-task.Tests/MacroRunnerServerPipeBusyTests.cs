using System;
using System.Diagnostics;
using System.IO.Pipes;
using System.Threading;
using System.Threading.Tasks;
using Labs626.UrTask.Ipc;

namespace Labs626.UrTask.Tests;

/// <summary>
/// Regression: launching a second Ur Task instance while one already owned the
/// bridge pipe hard-hung the app windowless. RunAcceptLoopAsync ran
/// synchronously until its first await; pipe creation threw before any await,
/// the catch swallowed it, and the retry loop spun synchronously on the UI
/// thread forever. The fix yields first and gives up cleanly on a busy pipe.
/// </summary>
public class MacroRunnerServerPipeBusyTests
{
    private sealed class NoopInvoker : IMacroRunInvoker
    {
        public Task<RunMacroResponse> RunAsync(RunMacroRequest request, CancellationToken ct)
            => throw new InvalidOperationException("no connections expected in this test");
        public IReadOnlyList<MacroSummary> ListMacros()
            => throw new InvalidOperationException("no connections expected in this test");
        public StopMacroResponse StopMacro(StopMacroRequest request)
            => throw new InvalidOperationException("no connections expected in this test");
    }

    [Fact]
    public async Task RunAcceptLoop_PipeAlreadyOwned_ReturnsPromptlyWithoutBlockingCaller()
    {
        var pipeName = $"626labs-ur-task-test-{Guid.NewGuid():N}";

        // Hold the single pipe instance the way a live sibling instance would.
        await using var holder = new NamedPipeServerStream(
            pipeName, PipeDirection.InOut, maxNumberOfServerInstances: 1,
            PipeTransmissionMode.Byte, PipeOptions.Asynchronous | PipeOptions.CurrentUserOnly);

        var server = new MacroRunnerServer(new NoopInvoker(), pipeName);
        using var cts = new CancellationTokenSource();

        // The call itself must yield immediately — a synchronous hot loop here
        // is exactly the bug (caller was the UI thread at startup).
        var sw = Stopwatch.StartNew();
        var loop = server.RunAcceptLoopAsync(cts.Token);
        sw.Stop();
        Assert.True(sw.ElapsedMilliseconds < 500, $"caller was blocked for {sw.ElapsedMilliseconds}ms");

        // And the loop must give up on the owned pipe rather than spin forever.
        var finished = await Task.WhenAny(loop, Task.Delay(TimeSpan.FromSeconds(5)));
        Assert.Same(loop, finished);
        await loop; // surface any unexpected exception
    }

    [Fact]
    public async Task RunAcceptLoop_PipeFree_StillAcceptsAndStopsOnCancel()
    {
        // Guard the fix against overcorrection: with a free pipe the loop must
        // keep serving (not return immediately) and stop on cancellation.
        var pipeName = $"626labs-ur-task-test-{Guid.NewGuid():N}";
        var server = new MacroRunnerServer(new NoopInvoker(), pipeName);
        using var cts = new CancellationTokenSource();

        var loop = server.RunAcceptLoopAsync(cts.Token);
        var early = await Task.WhenAny(loop, Task.Delay(300));
        Assert.NotSame(loop, early); // still alive, waiting for connections

        cts.Cancel();
        var finished = await Task.WhenAny(loop, Task.Delay(TimeSpan.FromSeconds(5)));
        Assert.Same(loop, finished);
        await loop;
    }
}
