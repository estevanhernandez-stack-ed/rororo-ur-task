using Labs626.UrTask.Macros;
using Labs626.UrTask.PluginHost;

namespace Labs626.UrTask.Tests;

public class CadenceRunnerTests
{
    private const long Min = 60_000;
    private const long TwelveMin = 12 * Min;

    // AccountInfo(int Pid, long RobloxUserId, string DisplayName, string AccountId,
    //             long PlaceId = 0, string PlaceName = "")
    private static AccountRegistry.AccountInfo Alt(int pid) => new(pid, pid, $"alt{pid}", $"acct-{pid}");

    // Macro.Duration == the LAST event's TimestampMs. That is exactly what the
    // active-pass lookahead reads, so a macro's "length" is set by its last event.
    // MacroEvent(long TimestampMs, MacroEventKind Kind, int VirtualKeyCode,
    //            int X, int Y, int MouseButton, int WheelDelta)
    private static Macro MacroOfLength(long durationMs) => new(
        SchemaVersion: 3, Id: Guid.NewGuid().ToString(), Name: $"m{durationMs}",
        RecordMode: "PerWindow", RecordedAgainstUserId: null, RecordedAgainstDisplayName: null,
        InterAltDelayMs: 0, RecordedAtUnixMs: 0,
        Events: new List<MacroEvent> { new(durationMs, MacroEventKind.KeyDown, 0x20, 0, 0, 0, 0) });

    /// Leaps forward when the runner sleeps — a simulated hour costs no real time.
    private sealed class FakeClock
    {
        public long NowMs;
        public long Now() => NowMs;
        public Task Sleep(long durationMs, CancellationToken ct)
        {
            if (durationMs > 0) NowMs += durationMs;   // jump, never actually wait
            return Task.CompletedTask;
        }
    }

    private sealed class FakePlayer : IMacroPlayer
    {
        public List<long> Plays { get; } = new();
        public bool IsPlaying => false;
        public event EventHandler<PlaybackStartedArgs>? Started;
        public event EventHandler<PlaybackEndedArgs>? Ended;
        public Task<PlaybackResult> PlayAsync(Macro macro, long targetUserId, CancellationToken external = default)
        {
            Plays.Add(targetUserId);
            return Task.FromResult(PlaybackResult.Completed());
        }
        public Task<PlaybackResult> PlayAllWindowsRawAsync(Macro macro, CancellationToken external = default)
            => Task.FromResult(PlaybackResult.Completed());
        public bool Abort() => false;
    }

    private sealed class FakeForeground : IForegroundWatcher
    {
        public AccountRegistry.AccountInfo? Current;
        public AccountRegistry.AccountInfo? ResolveForegroundAccount() => Current;
    }

    private sealed record Rig(
        AssignmentRunner Runner, FakeClock Clock, CancellationTokenSource Cts,
        List<int> Taps, List<int> Focused, List<IntPtr> Restored, FakePlayer Player);

    /// A runner whose clock jumps, whose Space is COUNTED not injected, and which
    /// cancels itself once `runForMs` of simulated time has elapsed.
    private static Rig Build(
        IReadOnlyList<Assignment> assignments, long runForMs, long keepAliveIntervalMs = TwelveMin)
    {
        var clock = new FakeClock();
        var fg = new FakeForeground();
        var player = new FakePlayer();
        var cts = new CancellationTokenSource();
        var taps = new List<int>();
        var focused = new List<int>();
        var restored = new List<IntPtr>();
        var currentPid = 0;

        var deps = new CadenceDeps(
            Focus: pid =>
            {
                focused.Add(pid);
                currentPid = pid;
                fg.Current = assignments.First(a => a.Alt.Pid == pid).Alt;   // so the verify passes
                return (true, null);
            },
            ClockMs: clock.Now,
            Sleep: (ms, ct) =>
            {
                var t = clock.Sleep(ms, ct);
                if (clock.NowMs >= runForMs) cts.Cancel();   // end the simulation
                return t;
            },
            CaptureForeground: () => new IntPtr(0xBEEF),     // sentinel: "the user's window"
            RestoreForeground: h => restored.Add(h),
            SendKeepAlive: () => taps.Add(currentPid),       // counted, never injected
            KeepAliveIntervalMs: _ => keepAliveIntervalMs);

        return new Rig(new AssignmentRunner(player, fg, deps), clock, cts, taps, focused, restored, player);
    }

    /// THE regression. One keep-alive alt on a 12-minute interval, one simulated hour.
    /// Correct: ~5 taps. The old spin loop: ~2,880 — one every 1.25s, each stealing the
    /// user's foreground. If this count ever climbs back into the hundreds, the
    /// desktop-hijack bug is back and this test is the tripwire.
    [Fact]
    public async Task SingleKeepAliveAlt_OverASimulatedHour_IsTappedAboutFiveTimes_NotThousands()
    {
        var alt = new Assignment(Alt(1), null, CadenceRole.KeepAlive);
        var rig = Build(new[] { alt }, runForMs: 60 * Min);

        await rig.Runner.RunAsync(new[] { alt }, rig.Cts.Token);

        Assert.InRange(rig.Taps.Count, 4, 6);
        Assert.True(rig.Taps.Count < 20,
            $"keep-alive tapped {rig.Taps.Count}x in a simulated hour — the spin loop is back");
    }

    /// Every foreground steal is paired with a restore, so a keep-alive is a ~1s blip
    /// rather than a hijack.
    [Fact]
    public async Task KeepAliveService_RestoresThePriorForeground()
    {
        var alt = new Assignment(Alt(1), null, CadenceRole.KeepAlive);
        var rig = Build(new[] { alt }, runForMs: 30 * Min);

        await rig.Runner.RunAsync(new[] { alt }, rig.Cts.Token);

        Assert.NotEmpty(rig.Taps);
        Assert.Equal(rig.Taps.Count, rig.Restored.Count);
        Assert.All(rig.Restored, h => Assert.Equal(new IntPtr(0xBEEF), h));
    }

    /// Compat guard: an all-Active squad still round-robins back-to-back, exactly as
    /// v0.6 did. No sleeping, no keep-alive taps.
    [Fact]
    public async Task AllActiveAssignments_RoundRobinBackToBack()
    {
        var a1 = new Assignment(Alt(1), MacroOfLength(1_000), CadenceRole.Active);
        var a2 = new Assignment(Alt(2), MacroOfLength(1_000), CadenceRole.Active);
        var rig = Build(new[] { a1, a2 }, runForMs: 2 * Min);

        await rig.Runner.RunAsync(new[] { a1, a2 }, rig.Cts.Token);

        Assert.Empty(rig.Taps);                       // nothing is on keep-alive
        Assert.Contains(1L, rig.Player.Plays);        // both alts farmed
        Assert.Contains(2L, rig.Player.Plays);
        Assert.True(rig.Player.Plays.Count > 5, "actives must run back-to-back, not sleep");
    }

    /// Gap-fitting end to end: a long Active pass must not starve the keep-alive.
    /// The 5-minute macro means the lookahead sees a keep-alive coming due inside the
    /// next pass and services it FIRST.
    [Fact]
    public async Task LongActivePass_StillLetsTheKeepAliveFire()
    {
        var active = new Assignment(Alt(1), MacroOfLength(5 * Min), CadenceRole.Active);
        var keep = new Assignment(Alt(2), null, CadenceRole.KeepAlive);
        var rig = Build(new[] { active, keep }, runForMs: 60 * Min);

        await rig.Runner.RunAsync(new[] { active, keep }, rig.Cts.Token);

        Assert.NotEmpty(rig.Player.Plays);                    // farming still happened
        Assert.NotEmpty(rig.Taps);                            // and the keep-alive still got fed
        Assert.All(rig.Taps, pid => Assert.Equal(2, pid));    // only the keep-alive alt is tapped
    }
}
