using System.IO;
using Labs626.UrTask.Diagnostics;
using Labs626.UrTask.Macros;
using Labs626.UrTask.PluginHost;

namespace Labs626.UrTask.Tests;

// EnsureClientSize now writes DiagLog lines — join the shared "DiagLog" collection
// (see DiagLogTests) and redirect Directory to a scratch temp dir so this suite
// never touches the real %LOCALAPPDATA% log and never races other DiagLog-touching
// test classes.
[Collection("DiagLog")]
public class MacroPlayerClientSpaceTests : IDisposable
{
    private readonly string _dir;

    public MacroPlayerClientSpaceTests()
    {
        _dir = Path.Combine(Path.GetTempPath(), "urtask-macroplayer-" + Guid.NewGuid().ToString("N"));
        DiagLog.Directory = _dir;
        DiagLog.ResetForTests();
    }

    public void Dispose()
    {
        try { Directory.Delete(_dir, recursive: true); } catch { }
    }

    private sealed class FakeForeground : IForegroundWatcher
    {
        public AccountRegistry.AccountInfo? Current { get; set; }
        public AccountRegistry.AccountInfo? ResolveForegroundAccount() => Current;
    }

    private sealed class FakeMetrics : IWindowMetrics
    {
        public IntPtr Hwnd = new(0x1234);
        public (int W, int H)? Client;
        public (int X, int Y, int W, int H)? Outer;
        public (int W, int H)? ClientAfterResize;
        public List<(int x, int y, int w, int h)> SetCalls = new();
        public bool SetResult = true;
        // Defaults to (0,0) — the "normal" case. Tests that need the mapping step to
        // abort (so a real mouse event never reaches SendInput) set this to null.
        public (int X, int Y)? Origin = (0, 0);
        // Counts HwndForPid calls — keyboard-only client macros must never touch this
        // (no coordinates means no reason to resolve the target window at all).
        public int HwndForPidCalls;

        // Maximize/RestoreDown bookkeeping, mirroring EnsureClientSize's real
        // try-fit-then-maximize flow. IsMaximizedFlag only flips which size
        // ClientSize reports — it never mutates Client/Outer. RestoreDown just
        // clears the flag and "leaves size as last-set" (whatever
        // Client/ClientAfterResize already held before Maximize was called), same
        // as Windows restoring a maximized window back to its pre-maximize rect.
        public bool IsMaximizedFlag;
        public int MaximizeCalls;
        public int RestoreDownCalls;
        // Configurable "maximized client size" a test can pin exactly. Left null
        // to fall back to the fake's work area minus the (Outer - Client) chrome
        // delta — mirrors ShowWindow(SW_MAXIMIZE) blowing past the max-track
        // ceiling to fill the monitor's work area.
        public (int W, int H)? MaximizedClient;
        // Simulates the real OS's max-track ceiling on SetWindowPos: SetOuterRect
        // silently grants at most this outer size no matter what was requested.
        // Defaults to a monitor-sized bound (matching the default WorkAreaFor) so
        // a windowed-fit request for a full-screen recording's client size gets
        // capped short — the exact mechanism behind Este's bug.
        public (int W, int H) MaxOuter = (2560, 1440);

        private bool _resized;

        public IntPtr HwndForPid(int pid) { HwndForPidCalls++; return Hwnd; }
        public (int X, int Y)? ClientOrigin(IntPtr hwnd) => Origin;

        public (int W, int H)? ClientSize(IntPtr hwnd)
        {
            if (IsMaximizedFlag)
            {
                if (MaximizedClient is { } mc) return mc;
                var work = WorkAreaFor(hwnd);
                if (Outer is { } o && Client is { } c)
                    return (work.W - (o.W - c.W), work.H - (o.H - c.H));
                return (work.W, work.H);
            }
            return _resized ? (ClientAfterResize ?? Client) : Client;
        }

        public (int X, int Y, int W, int H)? OuterRect(IntPtr hwnd) => Outer;
        public bool SetOuterRect(IntPtr hwnd, int x, int y, int w, int h)
        {
            SetCalls.Add((x, y, w, h));
            // OS max-track cap: grant at most MaxOuter, then derive the resulting
            // client size via the (constant) chrome delta — same assumption
            // WindowSpaceMath.OuterSizeForClient makes on the real Win32 side.
            int grantedW = Math.Min(w, MaxOuter.W);
            int grantedH = Math.Min(h, MaxOuter.H);
            ClientAfterResize = Outer is { } o && Client is { } c
                ? (grantedW - (o.W - c.W), grantedH - (o.H - c.H))
                : (grantedW, grantedH);
            _resized = true;
            return SetResult;
        }
        public bool Minimize(IntPtr hwnd) => true;
        public bool Restore(IntPtr hwnd) => true;
        public (int X, int Y, int W, int H) WorkAreaFor(IntPtr hwnd) => (0, 0, 2560, 1440);
        public void Maximize(IntPtr hwnd) { MaximizeCalls++; IsMaximizedFlag = true; }
        public void RestoreDown(IntPtr hwnd) { RestoreDownCalls++; IsMaximizedFlag = false; }
        public bool IsMaximized(IntPtr hwnd) => IsMaximizedFlag;
    }

    // Single mouse event used to trip the "macro has mouse events" gate in tests that
    // need the client-space preflight to actually run. TimestampMs 0 so no Task.Delay
    // is awaited before the loop reaches it.
    private static readonly MacroEvent OneMouseMove =
        new(TimestampMs: 0, Kind: MacroEventKind.MouseMove, VirtualKeyCode: 0, X: 5, Y: 5, MouseButton: 0, WheelDelta: 0);

    private static Macro ClientMacro(int w = 816, int h = 638, IReadOnlyList<MacroEvent>? events = null) => new(
        SchemaVersion: Macro.CurrentSchemaVersion,
        Id: Guid.NewGuid().ToString(), Name: "t", RecordMode: "PerWindow",
        RecordedAgainstUserId: null, RecordedAgainstDisplayName: null,
        InterAltDelayMs: null, RecordedAtUnixMs: 1,
        // Zero events by default — the keyboard-only test relies on this to prove the
        // gate skips preflight entirely. Tests that need to exercise the resize
        // preflight itself pass events: new[] { OneMouseMove }.
        Events: events ?? new List<MacroEvent>(),
        CoordSpace: Macro.CoordSpaceClient, RecordedClientW: w, RecordedClientH: h);

    private static readonly AccountRegistry.AccountInfo Target = new(Pid: 111, RobloxUserId: 42, DisplayName: "Alt", AccountId: "a1");

    /// <summary>
    /// Client-space preflight only runs when the macro actually has mouse events
    /// (see MacroPlayer.PlayAsync's gate) — a bare-timestamp macro would otherwise
    /// take the inert screen-macro path and this test would be exercising nothing.
    /// One MouseMove event trips the gate; ClientOrigin() = null then aborts the
    /// mapping step at inject time — before SendMacroEvent ever runs — so this still
    /// proves "sizes already matched, no resize call" without synthesizing real input.
    /// </summary>
    [Fact]
    public async Task ClientMacro_SizeAlreadyMatches_NoResizeCall_AbortsBeforeInject()
    {
        var metrics = new FakeMetrics { Client = (816, 638), Origin = null };
        var player = new MacroPlayer(new FakeForeground { Current = Target }, metrics);
        var result = await player.PlayAsync(ClientMacro(events: new[] { OneMouseMove }), targetUserId: 42);
        Assert.Equal(PlaybackOutcome.Aborted, result.Outcome);
        Assert.Contains("vanished", result.Reason);
        Assert.Empty(metrics.SetCalls);
    }

    /// <summary>
    /// THE REGRESSION GUARD for Este's bug. A macro recorded in a maximized/
    /// full-screen window has a recorded client size at the monitor's work-area
    /// magnitude; the outer rect a windowed fit would need (client + chrome) is
    /// wider than the monitor, so the real OS's SetWindowPos silently caps the
    /// attempt short — MaxOuter (2500x1400, below the 2554x1432 outer this test
    /// requests) simulates exactly that. The old algorithm predicted this ceiling
    /// instead of asking the OS, got the prediction wrong, and left the window
    /// stuck small (confirmed live: "went full screen, then back to the
    /// pre-fullscreen size, still failed"). The new algorithm tries the windowed
    /// fit, notices the OS capped it short of the recorded size, and falls back to
    /// maximize-and-leave — which reaches the recorded size via MaximizedClient.
    /// Asserts the window ends up MAXIMIZED at the recorded size, not stuck at the
    /// OS-capped small size.
    /// </summary>
    [Fact]
    public async Task ClientMacro_MaximizedRecording_WindowedFitCapped_FallsBackToMaximizeAndLeave()
    {
        var metrics = new FakeMetrics
        {
            Client = (700, 500),
            Outer = (10, 20, 714, 542),        // chrome = 14 x 42
            MaxOuter = (2500, 1400),           // OS caps the windowed-fit attempt short
            MaximizedClient = (2540, 1390),    // maximize reaches the recorded size exactly
            Origin = null,                     // abort at inject time, not send time
        };
        var player = new MacroPlayer(new FakeForeground { Current = Target }, metrics);
        var result = await player.PlayAsync(ClientMacro(2540, 1390, events: new[] { OneMouseMove }), targetUserId: 42);

        Assert.Equal(PlaybackOutcome.Aborted, result.Outcome);
        Assert.Contains("vanished", result.Reason); // proves EnsureClientSize returned null (proceed)
        Assert.Equal(1, metrics.MaximizeCalls);
        Assert.Single(metrics.SetCalls);             // windowed fit was attempted once before falling back
        Assert.Equal(0, metrics.RestoreDownCalls);    // window wasn't maximized yet when the fit was attempted
        Assert.True(metrics.IsMaximizedFlag);         // ends maximized, not stuck at the capped size
        Assert.Equal((2540, 1390), metrics.ClientSize(metrics.Hwnd)!.Value);
    }

    /// <summary>
    /// The common case: a normal windowed recording whose recorded client size is
    /// comfortably reachable via a windowed fit (MaxOuter left at its large
    /// default). EnsureClientSize returns as soon as the fit lands within Slop —
    /// no maximize flash. Reconciles the old
    /// ClientMacro_SizeMismatch_ResizesByClientDelta_... test's intent ("mismatch
    /// resizes") against the new try-fit-first flow — the fit itself is the whole
    /// story now, no maximize/restore round-trip needed.
    /// </summary>
    [Fact]
    public async Task ClientMacro_NormalRecording_WindowedFitSucceeds_NoMaximize()
    {
        var metrics = new FakeMetrics
        {
            Client = (700, 500),
            Outer = (10, 20, 714, 542),      // chrome = 14 x 42
            Origin = null,                   // abort at inject time, not send time
        };
        var player = new MacroPlayer(new FakeForeground { Current = Target }, metrics);
        var result = await player.PlayAsync(ClientMacro(816, 638, events: new[] { OneMouseMove }), targetUserId: 42);

        Assert.Equal(PlaybackOutcome.Aborted, result.Outcome);
        Assert.Contains("vanished", result.Reason);
        Assert.Equal(0, metrics.MaximizeCalls);
        Assert.Equal(0, metrics.RestoreDownCalls);
        var call = Assert.Single(metrics.SetCalls);
        // Anchored at the work area's top-left (0,0), not the window's original
        // position (10,20) — max room to grow into. Outer = client + chrome.
        Assert.Equal((0, 0, 830, 680), call);
        Assert.Equal((816, 638), metrics.ClientSize(metrics.Hwnd)!.Value);
        Assert.False(metrics.IsMaximizedFlag);
    }

    /// <summary>
    /// Recorded on a bigger monitor than this one: the recorded client size is
    /// unreachable both ways — the windowed-fit outer rect is bigger than the work
    /// area (never attempted), and maximize alone can't produce it either (past
    /// Tol). EnsureClientSize refuses rather than leave the window silently
    /// undersized. Reconciles the old ClientMacro_ResizeDoesNotStick_... test's
    /// intent ("unreachable → sensible outcome") for the genuinely-unreachable
    /// case — distinct from the OS-capped-but-maximize-reachable case above, which
    /// now succeeds instead of refusing.
    /// </summary>
    [Fact]
    public async Task ClientMacro_RecordedLargerThanThisMonitor_Refuses()
    {
        var metrics = new FakeMetrics
        {
            Client = (700, 500),
            Outer = (10, 20, 714, 542),      // chrome = 14 x 42
        };
        var player = new MacroPlayer(new FakeForeground { Current = Target }, metrics);
        bool started = false;
        player.Started += (_, _) => started = true;
        var result = await player.PlayAsync(ClientMacro(5000, 3000, events: new[] { OneMouseMove }), targetUserId: 42);

        Assert.Equal(PlaybackOutcome.Refused, result.Outcome);
        Assert.Contains("larger than this monitor", result.Reason);
        Assert.Equal(1, metrics.MaximizeCalls);
        Assert.Empty(metrics.SetCalls); // outer rect exceeds the work area — windowed fit never attempted
        Assert.False(started);
    }

    /// <summary>
    /// A macro explicitly stamped RecordedMaximized (record-time IsMaximized was
    /// true) skips the windowed-fit attempt entirely and goes straight to
    /// maximize-and-leave — no point spending a SetOuterRect call that's
    /// guaranteed to get capped for a full-screen recording. Distinguishes this
    /// from the regression-guard test above (no stamp — null — which DOES attempt
    /// and observe a capped windowed fit first) by asserting zero SetOuterRect
    /// calls here.
    /// </summary>
    [Fact]
    public async Task ClientMacro_RecordedMaximizedFlag_SkipsWindowedFit_GoesStraightToMaximize()
    {
        var metrics = new FakeMetrics
        {
            Client = (700, 500),
            Outer = (10, 20, 714, 542),
            MaximizedClient = (2540, 1390),
            Origin = null,
        };
        var macro = ClientMacro(2540, 1390, events: new[] { OneMouseMove }) with { RecordedMaximized = true };
        var player = new MacroPlayer(new FakeForeground { Current = Target }, metrics);
        var result = await player.PlayAsync(macro, targetUserId: 42);

        Assert.Equal(PlaybackOutcome.Aborted, result.Outcome);
        Assert.Contains("vanished", result.Reason);
        Assert.Equal(1, metrics.MaximizeCalls);
        Assert.Empty(metrics.SetCalls);       // windowed-fit attempt skipped entirely
        Assert.Equal(0, metrics.RestoreDownCalls);
        Assert.True(metrics.IsMaximizedFlag);
    }

    [Fact]
    public async Task ClientMacro_MissingRecordedSize_Refuses()
    {
        var macro = ClientMacro(events: new[] { OneMouseMove }) with { RecordedClientW = null, RecordedClientH = null };
        var player = new MacroPlayer(new FakeForeground { Current = Target }, new FakeMetrics { Client = (816, 638) });
        var result = await player.PlayAsync(macro, targetUserId: 42);
        Assert.Equal(PlaybackOutcome.Refused, result.Outcome);
    }

    /// <summary>
    /// The merge-blocker regression test: a client-tagged macro with zero mouse
    /// events (e.g. a keyboard-only recording, belt-and-suspenders against a stale
    /// save) must be completely inert with respect to coordinate space — no
    /// HwndForPid lookup, no resize, no refusal. Plays exactly like a screen macro.
    /// </summary>
    [Fact]
    public async Task ClientMacro_KeyboardOnly_SkipsPreflightAndPlays()
    {
        var metrics = new FakeMetrics { Client = (816, 638) };
        var player = new MacroPlayer(new FakeForeground { Current = Target }, metrics);
        var result = await player.PlayAsync(ClientMacro(), targetUserId: 42); // zero events (default)
        Assert.Equal(PlaybackOutcome.Completed, result.Outcome);
        Assert.Empty(metrics.SetCalls);
        Assert.Equal(0, metrics.HwndForPidCalls);
    }

    [Fact]
    public async Task ScreenMacro_NeverTouchesMetrics()
    {
        var metrics = new FakeMetrics { Client = (1, 1) };
        var screenMacro = ClientMacro() with { CoordSpace = Macro.CoordSpaceScreen, RecordedClientW = null, RecordedClientH = null };
        var player = new MacroPlayer(new FakeForeground { Current = Target }, metrics);
        var result = await player.PlayAsync(screenMacro, targetUserId: 42);
        Assert.Equal(PlaybackOutcome.Completed, result.Outcome);
        Assert.Empty(metrics.SetCalls); // legacy path is metrics-blind
    }
}
