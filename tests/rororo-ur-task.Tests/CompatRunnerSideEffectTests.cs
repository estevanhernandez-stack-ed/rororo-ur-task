using System.IO;
using Labs626.UrTask.Diagnostics;
using Labs626.UrTask.Macros;
using Labs626.UrTask.PluginHost;

namespace Labs626.UrTask.Tests;

// The 3-argument AssignmentRunner ctor is what AssignmentRunnerTests builds every runner
// with, and that class is NOT in the "DiagLog" collection, so it runs in parallel with the
// classes that are. When the ctor inherited the real sweep log, its keep-alive lines landed
// in whatever temp directory a collection class had pointed DiagLog at, and DiagLogTests'
// line counts came up one over. This class joins the collection so it owns the directory
// while it looks.
[Collection("DiagLog")]
public class CompatRunnerSideEffectTests : IDisposable
{
    private readonly string _dir;

    public CompatRunnerSideEffectTests()
    {
        _dir = Path.Combine(Path.GetTempPath(), "urtask-compatrunner-" + Guid.NewGuid().ToString("N"));
        DiagLog.Directory = _dir;
        DiagLog.ResetForTests();
    }

    public void Dispose()
    {
        try { Directory.Delete(_dir, recursive: true); } catch { }
    }

    private sealed class NeverPlays : IMacroPlayer
    {
        public bool IsPlaying => false;
#pragma warning disable CS0067 // the interface requires the events; nothing here raises them
        public event EventHandler<PlaybackStartedArgs>? Started;
        public event EventHandler<PlaybackEndedArgs>? Ended;
#pragma warning restore CS0067
        public Task<PlaybackResult> PlayAsync(Macro macro, long targetUserId, CancellationToken external = default)
            => Task.FromResult(PlaybackResult.Completed());
        public Task<PlaybackResult> PlayAllWindowsRawAsync(Macro macro, CancellationToken external = default)
            => Task.FromResult(PlaybackResult.Completed());
        public bool Abort() => false;
    }

    private sealed class AlwaysInFront(AccountRegistry.AccountInfo alt) : IForegroundWatcher
    {
        public AccountRegistry.AccountInfo? ResolveForegroundAccount() => alt;
    }

    /// <summary>
    /// A keep-alive account serviced by a compat-ctor runner must leave the diagnostics log
    /// alone. The Space "lands" (the ctor fakes the keystroke), which is the exact path that
    /// used to write "keep-alive: Space to {name} ({n} ms to front)" through the real log.
    /// </summary>
    [Fact]
    public async Task CompatCtor_ServicingAKeepAliveAccount_WritesNothingToTheDiagLog()
    {
        var alt = new AccountRegistry.AccountInfo(4242, 55_501_234L, "CompatCtorProbe", "acct-4242");
        var runner = new AssignmentRunner(new NeverPlays(), new AlwaysInFront(alt), _ => (true, null));
        var assignments = new List<Assignment> { Assignment.WithDerivedRole(alt, null) };   // no macro: keep-alive

        var served = false;
        using var cts = new CancellationTokenSource();
        runner.Progress += (_, p) =>
        {
            // Playing is raised just before the Space and its log line, both of which run
            // synchronously after this handler returns. Cancelling here lets the service
            // finish its write and then stops the loop at the next await.
            if (p.Phase != AssignmentPhase.Playing) return;
            served = true;
            cts.Cancel();
        };

        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(10));
        using var linked = CancellationTokenSource.CreateLinkedTokenSource(cts.Token, timeout.Token);
        try { await runner.RunAsync(assignments, linked.Token); }
        catch (OperationCanceledException) { /* the expected way out */ }

        Assert.True(served, "The keep-alive account was never serviced, so this test proved nothing.");

        var written = File.Exists(DiagLog.CurrentLogPath) ? File.ReadAllText(DiagLog.CurrentLogPath) : "";
        Assert.True(written.Length == 0, "The compat ctor wrote to the diagnostics log:" + Environment.NewLine + written);
    }
}
