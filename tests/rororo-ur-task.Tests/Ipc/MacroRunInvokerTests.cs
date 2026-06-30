// tests/rororo-ur-task.Tests/Ipc/MacroRunInvokerTests.cs
using Labs626.UrTask.Ipc;
using Labs626.UrTask.Macros;
using Labs626.UrTask.PluginHost;

namespace Labs626.UrTask.Tests.Ipc;

public class MacroRunInvokerTests
{
    private static Macro NewMacro(string id) => new(
        SchemaVersion: 2, Id: id, Name: "recovery", RecordMode: "PerWindow",
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
}
