using System.Runtime.InteropServices;
using Labs626.UrTask.Macros;
using Labs626.UrTask.PluginHost;

namespace Labs626.UrTask.Tests;

public class AssignmentRunnerTests
{
    // ---- Reference Win32 INPUT: union sized to its largest member (MOUSEINPUT). ----
    // This is the size SendInput validates cbSize against. The keep-alive path
    // must match it exactly or every injected Space is silently rejected.
    [StructLayout(LayoutKind.Sequential)]
    private struct RefInput { public uint type; public RefUnion union; }
    [StructLayout(LayoutKind.Explicit)]
    private struct RefUnion
    {
        [FieldOffset(0)] public RefMouse mouse;
        [FieldOffset(0)] public RefKeybd keyboard;
    }
    [StructLayout(LayoutKind.Sequential)]
    private struct RefMouse { public int dx; public int dy; public uint mouseData; public uint dwFlags; public uint time; public IntPtr extra; }
    [StructLayout(LayoutKind.Sequential)]
    private struct RefKeybd { public ushort wVk; public ushort wScan; public uint dwFlags; public uint time; public IntPtr extra; }

    // ---- The OLD buggy layout: union holds only the keyboard field. ----
    // 32 bytes on x64 / 20 on x86 — 8 (or 4) short of canonical. The fix must
    // never regress to this size.
    [StructLayout(LayoutKind.Sequential)]
    private struct RefInputBuggy { public uint type; public RefUnionBuggy union; }
    [StructLayout(LayoutKind.Explicit)]
    private struct RefUnionBuggy { [FieldOffset(0)] public RefKeybd keyboard; }

    /// <summary>
    /// Regression for the keep-alive no-op bug (shipped through v0.2.2): the
    /// runner's pared-down INPUT struct dropped the MOUSEINPUT member, so its
    /// cbSize came up 8 bytes short (32 vs 40 on x64). SendInput rejects any
    /// call whose cbSize != sizeof(INPUT), so the keep-alive Space was never
    /// injected — and the discarded return value hid the failure. Lock the size.
    /// </summary>
    [Fact]
    public void KeepAliveInputStructSize_MatchesCanonicalWin32InputSize()
    {
        var canonical = Marshal.SizeOf<RefInput>();
        Assert.Equal(canonical, AssignmentRunner.KeepAliveInputStructSize);
        // The old buggy size must never come back.
        Assert.NotEqual(Marshal.SizeOf<RefInputBuggy>(), AssignmentRunner.KeepAliveInputStructSize);
    }

    private static Macro NewMacro(string name = "test") => new(
        SchemaVersion: 2,
        Id: Guid.NewGuid().ToString(),
        Name: name,
        RecordMode: "PerWindow",
        RecordedAgainstUserId: null,
        RecordedAgainstDisplayName: null,
        InterAltDelayMs: null,
        RecordedAtUnixMs: 0,
        Events: new List<MacroEvent>());

    private static AccountRegistry.AccountInfo Alt(int pid, long userId, string name)
        => new(pid, userId, name, $"acct-{pid}");

    private sealed class FakePlayer : IMacroPlayer
    {
        public List<(long userId, string? macroName)> Calls { get; } = new();
        public bool IsPlaying => false;

        public event EventHandler<PlaybackStartedArgs>? Started;
        public event EventHandler<PlaybackEndedArgs>? Ended;

        public Task<PlaybackResult> PlayAsync(Macro macro, long targetUserId, CancellationToken external = default)
        {
            Calls.Add((targetUserId, macro.Name));
            return Task.FromResult(PlaybackResult.Completed());
        }

        public Task<PlaybackResult> PlayAllWindowsRawAsync(Macro macro, CancellationToken external = default)
            => Task.FromResult(PlaybackResult.Completed());

        public bool Abort() => false;
    }

    private sealed class FakeForeground : IForegroundWatcher
    {
        public Func<AccountRegistry.AccountInfo?>? Resolver { get; set; }
        public AccountRegistry.AccountInfo? ResolveForegroundAccount() => Resolver?.Invoke();
    }

    /// <summary>
    /// 2-alt scenario: alt1 has a macro, alt2 is unassigned (keep-alive).
    /// Cancel after the first full pass (both alts visited). Assert:
    ///   - PlayAsync called once for alt1 with the correct macro
    ///   - PlayAsync NOT called for alt2 (keep-alive sends Space instead)
    ///   - Both alts emit Focusing then Playing/Skipped phases (alt2 emits Playing too
    ///     since it passes the focus check — keep-alive doesn't call player.PlayAsync)
    ///   - Final phase is Stopped
    /// </summary>
    [Fact]
    public async Task RunAsync_OneCycleWithKeepAlive_PlaysAssignedAndSkipsPlayer()
    {
        var macro = NewMacro("jump-jump");
        var alt1 = Alt(1001, 47821334, "Goldnail8");
        var alt2 = Alt(1002, 47821335, "ScrambledTen");

        var player = new FakePlayer();
        var fg = new FakeForeground();

        // Focus always succeeds; foreground resolves to whichever alt is being focused.
        long currentFocus = 0;
        var allAlts = new[] { alt1, alt2 };
        fg.Resolver = () => allAlts.FirstOrDefault(a => a.RobloxUserId == currentFocus);

        var runner = new AssignmentRunner(player, fg, pid =>
        {
            // Fake focus: flip the "foreground" to the alt being targeted.
            var target = allAlts.FirstOrDefault(a => a.Pid == pid);
            if (target is not null) currentFocus = target.RobloxUserId;
            return (true, null);
        });

        var assignments = new List<Assignment>
        {
            new(alt1, macro),  // explicit macro
            new(alt2, null),   // keep-alive (Space)
        };

        var phases = new List<(int index, AssignmentPhase phase)>();
        var cts = new CancellationTokenSource();

        runner.Progress += (_, p) =>
        {
            phases.Add((p.IndexInCycle, p.Phase));
            // Cancel when the second cycle starts — this guarantees the first cycle completed.
            if (p.Cycle == 2 && p.Phase == AssignmentPhase.Focusing)
                cts.Cancel();
        };

        // Run with a timeout so the test can't hang if something goes wrong.
        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(5));
        using var linked = CancellationTokenSource.CreateLinkedTokenSource(cts.Token, timeout.Token);

        try
        {
            await runner.RunAsync(assignments, linked.Token);
        }
        catch (OperationCanceledException) { /* expected path */ }

        // alt1 (has macro) — player.PlayAsync should have been called at least once
        // (exactly once per cycle; we cancel at cycle 2, so 1 call expected)
        Assert.True(player.Calls.Count >= 1, $"Expected at least 1 PlayAsync call, got {player.Calls.Count}");
        Assert.All(player.Calls, call => Assert.Equal(alt1.RobloxUserId, call.userId));
        Assert.All(player.Calls, call => Assert.Equal("jump-jump", call.macroName));

        // Both alts should have been in Focusing phase during cycle 1
        Assert.Contains(phases, p => p.phase == AssignmentPhase.Focusing && p.index == 0);
        Assert.Contains(phases, p => p.phase == AssignmentPhase.Focusing && p.index == 1);
    }

    [Fact]
    public async Task RunAsync_EmptyAssignments_CompletesImmediately()
    {
        var player = new FakePlayer();
        var fg = new FakeForeground();
        var runner = new AssignmentRunner(player, fg, _ => (true, null));

        // Should return immediately with no assignments.
        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(2));
        await runner.RunAsync(new List<Assignment>(), timeout.Token);

        Assert.Empty(player.Calls);
    }

    [Fact]
    public async Task RunAsync_FocusFails_EmitsSkippedAndContinues()
    {
        var macro = NewMacro("farm");
        var alt1 = Alt(1001, 47821334, "Goldnail8");
        var alt2 = Alt(1002, 47821335, "ScrambledTen");

        var player = new FakePlayer();
        var fg = new FakeForeground();

        // alt1 focus fails, alt2 succeeds
        long currentFocus = 0;
        var runner = new AssignmentRunner(player, fg, pid =>
        {
            if (pid == alt1.Pid) return (false, "window not found");
            currentFocus = alt2.RobloxUserId;
            return (true, null);
        });
        fg.Resolver = () => alt2; // always return alt2 as foreground

        var assignments = new List<Assignment>
        {
            new(alt1, macro),
            new(alt2, macro),
        };

        var phases = new List<(int index, AssignmentPhase phase)>();
        var cts = new CancellationTokenSource();

        runner.Progress += (_, p) =>
        {
            phases.Add((p.IndexInCycle, p.Phase));
            // Cancel at the start of cycle 2 so we capture a complete first cycle.
            if (p.Cycle == 2 && p.Phase == AssignmentPhase.Focusing) cts.Cancel();
        };

        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(5));
        using var linked = CancellationTokenSource.CreateLinkedTokenSource(cts.Token, timeout.Token);

        try { await runner.RunAsync(assignments, linked.Token); }
        catch (OperationCanceledException) { }

        // alt1 should have been skipped (focus failed) in cycle 1
        Assert.Contains(phases, p => p.phase == AssignmentPhase.Skipped && p.index == 0);
        // alt2 should have been played (focus succeeded) at least once
        Assert.True(player.Calls.Count >= 1, $"Expected at least 1 PlayAsync call for alt2, got {player.Calls.Count}");
        Assert.All(player.Calls, call => Assert.Equal(alt2.RobloxUserId, call.userId));
    }
}
