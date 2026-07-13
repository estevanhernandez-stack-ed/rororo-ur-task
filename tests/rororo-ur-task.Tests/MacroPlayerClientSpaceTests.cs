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
        // Monitor work area returned by WorkAreaFor. Defaults to a plain 2560x1440
        // monitor; the ultrawide regression test overrides it to 3440x1392 (Este's
        // real monitor) to reproduce the work-area-overhang bug exactly.
        public (int X, int Y, int W, int H) WorkArea = (0, 0, 2560, 1440);

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
        public (int X, int Y, int W, int H) WorkAreaFor(IntPtr hwnd) => WorkArea;
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
    /// Test A' — THE REGRESSION GUARD for Este's real ultrawide bug. Work area
    /// 3440x1392 (his monitor), chrome 16x39, recorded client 1718x1360 — the
    /// outer rect a windowed fit needs is 1734x1399, exactly 7px TALLER than the
    /// work area (matches the live diag log verbatim: "windowed outer 1734x1399
    /// exceeds work area 3440x1392"). The old algorithm treated any outer rect
    /// that didn't fully fit the work area as unreachable and fell back to
    /// maximize-and-leave, blowing the window up to 3440-wide and scattering every
    /// recorded click. The new algorithm doesn't care whether the outer rect fits
    /// the work area — only whether the OS actually grants the requested CLIENT
    /// size (clicks are client-relative) — so with MaxOuter big enough that
    /// SetOuterRect grants the full request, it succeeds as a normal window, a
    /// few px taller than the work area, and never maximizes.
    /// </summary>
    [Fact]
    public async Task ClientMacro_UltrawideWorkAreaOverhang_WindowedFitSucceeds_NotMaximized()
    {
        var metrics = new FakeMetrics
        {
            Client = (700, 500),
            Outer = (10, 20, 716, 539),         // chrome = 16 x 39
            WorkArea = (0, 0, 3440, 1392),      // Este's real ultrawide work area
            MaxOuter = (3440, 1450),            // OS grants the requested 1734x1399 outer in full
            Origin = null,                      // abort at inject time, not send time
        };
        var player = new MacroPlayer(new FakeForeground { Current = Target }, metrics);
        var result = await player.PlayAsync(ClientMacro(1718, 1360, events: new[] { OneMouseMove }), targetUserId: 42);

        Assert.Equal(PlaybackOutcome.Aborted, result.Outcome);
        Assert.Contains("vanished", result.Reason); // proves EnsureClientSize returned null (proceed)
        var call = Assert.Single(metrics.SetCalls);
        Assert.Equal((1734, 1399), (call.w, call.h));   // outer requested = client + chrome, uncapped by work-area fit
        Assert.Equal((10, 0), (call.x, call.y));        // X kept from current position, Y pinned to work-area top
        Assert.Equal((1718, 1360), metrics.ClientSize(metrics.Hwnd)!.Value);
        Assert.Equal(0, metrics.MaximizeCalls);
        Assert.False(metrics.IsMaximizedFlag);
    }

    /// <summary>
    /// Test B — the common case: a normal windowed recording whose recorded
    /// client size is comfortably reachable via a windowed fit. EnsureClientSize
    /// returns as soon as the fit lands within Slop — no maximize flash. The
    /// window keeps its current X (10) rather than snapping to the work area's
    /// left edge — only Y is pinned to the work-area top.
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
        // X kept at the window's current position (10), Y pinned to the work
        // area's top (0) — max vertical room to grow into. Outer = client + chrome.
        Assert.Equal((10, 0, 830, 680), call);
        Assert.Equal((816, 638), metrics.ClientSize(metrics.Hwnd)!.Value);
        Assert.False(metrics.IsMaximizedFlag);
    }

    /// <summary>
    /// Test E — recorded on a bigger monitor than this one: even a free windowed
    /// resize can't reach the recorded client size — the OS (MaxOuter, standing in
    /// for the real max-track ceiling) caps the SetOuterRect request short, and
    /// the resulting client size lands well past Slop from what was recorded.
    /// EnsureClientSize refuses with actionable advice rather than leave the
    /// window silently undersized (and clicks silently off-target). Never calls
    /// Maximize — this macro has no RecordedMaximized stamp, so only the windowed
    /// path is attempted.
    /// </summary>
    [Fact]
    public async Task ClientMacro_RecordedLargerThanThisMonitor_WindowedFitCapped_Refuses()
    {
        var metrics = new FakeMetrics
        {
            Client = (700, 500),
            Outer = (10, 20, 714, 542),      // chrome = 14 x 42
            // MaxOuter left at its default (2560x1440) — well short of the outer
            // rect (5014x3042) a 5000x3000 client would need.
        };
        var player = new MacroPlayer(new FakeForeground { Current = Target }, metrics);
        bool started = false;
        player.Started += (_, _) => started = true;
        var result = await player.PlayAsync(ClientMacro(5000, 3000, events: new[] { OneMouseMove }), targetUserId: 42);

        Assert.Equal(PlaybackOutcome.Refused, result.Outcome);
        Assert.Contains("recorded on a larger screen", result.Reason);
        Assert.Contains("re-record the macro on this monitor", result.Reason);
        Assert.Equal(0, metrics.MaximizeCalls);   // no RecordedMaximized stamp — windowed path only
        Assert.Single(metrics.SetCalls);          // the windowed-fit attempt WAS made — just came up short
        Assert.False(started);
    }

    /// <summary>
    /// Test C — a macro explicitly stamped RecordedMaximized (record-time
    /// IsMaximized was true) skips the windowed-fit attempt entirely and goes
    /// straight to maximize-and-leave — no point spending a SetOuterRect call
    /// that's guaranteed to get capped for a full-screen recording. This monitor's
    /// maximized client size matches the recorded size, so playback proceeds.
    /// </summary>
    [Fact]
    public async Task ClientMacro_RecordedMaximizedFlag_MonitorMatches_SkipsWindowedFit_LeavesMaximized()
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

    /// <summary>
    /// Test D — a macro stamped RecordedMaximized, but THIS monitor maximizes to a
    /// different client size than what was recorded (different monitor/DPI since
    /// record time). Maximizing can't reproduce the recorded size no matter what,
    /// so EnsureClientSize refuses with the "recorded on a maximized ... re-record"
    /// message rather than silently playing back at the wrong size.
    /// </summary>
    [Fact]
    public async Task ClientMacro_RecordedMaximizedFlag_MonitorDiffers_Refuses()
    {
        var metrics = new FakeMetrics
        {
            Client = (700, 500),
            Outer = (10, 20, 714, 542),
            MaximizedClient = (2000, 1200), // this monitor's maximize doesn't match the recording
        };
        var macro = ClientMacro(2540, 1390, events: new[] { OneMouseMove }) with { RecordedMaximized = true };
        var player = new MacroPlayer(new FakeForeground { Current = Target }, metrics);
        bool started = false;
        player.Started += (_, _) => started = true;
        var result = await player.PlayAsync(macro, targetUserId: 42);

        Assert.Equal(PlaybackOutcome.Refused, result.Outcome);
        Assert.Contains("recorded on a maximized", result.Reason);
        Assert.Contains("Re-record it on this monitor", result.Reason);
        Assert.Equal(1, metrics.MaximizeCalls);
        Assert.Empty(metrics.SetCalls);   // windowed-fit attempt skipped entirely — stamped RecordedMaximized
        Assert.False(started);
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

    /// <summary>
    /// CRITICAL 2 (MacroPlayer half): <see cref="MacroEvent.TimestampMs"/> is a bare
    /// <c>long</c> off user-editable on-disk JSON with no upstream bound. A
    /// screen-space macro never touches <see cref="IWindowMetrics"/> at all, so this
    /// exercises the playback loop's own <c>wait</c>-to-<c>int</c> cast directly. A
    /// timestamp above <c>int.MaxValue</c> ms (~24.8 days) used to truncate to a
    /// NEGATIVE int on an unclamped <c>(int)wait</c> cast, and <c>Task.Delay</c>
    /// throws <c>ArgumentOutOfRangeException</c> for anything less than -1 — an
    /// exception that escaped <c>PlayAsync</c> uncaught (its own catch only handles
    /// <c>OperationCanceledException</c>), which in turn escaped
    /// <c>AssignmentRunner.RunAsync</c> entirely (see
    /// <c>CadenceRunnerTests.ExceptionEscapingLoopBody_StillEmitsStoppedAndClearsIsRunning</c>).
    /// The clamp caps the wait at <c>int.MaxValue</c> ms instead — proven here by
    /// cancelling almost immediately and observing a clean cancellation-driven
    /// Abort, not an unhandled exception thrown before the delay is ever entered.
    ///
    /// Verified by hand: reverting the clamp (back to a bare <c>(int)wait</c> cast)
    /// turns this RED — <c>PlayAsync</c> throws <c>ArgumentOutOfRangeException</c>
    /// instead of returning a result at all.
    /// </summary>
    [Fact]
    public async Task ScreenMacro_PathologicallyLargeTimestamp_ClampsInsteadOfThrowing()
    {
        var hugeTimestampEvent = new MacroEvent(
            TimestampMs: (long)int.MaxValue + 5_000_000L, Kind: MacroEventKind.KeyDown,
            VirtualKeyCode: 0x20, X: 0, Y: 0, MouseButton: 0, WheelDelta: 0);
        var screenMacro = new Macro(
            SchemaVersion: Macro.CurrentSchemaVersion, Id: Guid.NewGuid().ToString(), Name: "huge-ts",
            RecordMode: "PerWindow", RecordedAgainstUserId: null, RecordedAgainstDisplayName: null,
            InterAltDelayMs: null, RecordedAtUnixMs: 1, Events: new[] { hugeTimestampEvent },
            CoordSpace: Macro.CoordSpaceScreen);

        var player = new MacroPlayer(new FakeForeground { Current = Target }, new FakeMetrics());

        using var cts = new CancellationTokenSource(TimeSpan.FromMilliseconds(50));
        var result = await player.PlayAsync(screenMacro, targetUserId: 42, cts.Token);

        Assert.Equal(PlaybackOutcome.Aborted, result.Outcome);
        Assert.Equal("Playback cancelled.", result.Reason);
    }
}
