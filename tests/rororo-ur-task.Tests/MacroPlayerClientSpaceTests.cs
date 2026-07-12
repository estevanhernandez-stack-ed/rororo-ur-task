using Labs626.UrTask.Macros;
using Labs626.UrTask.PluginHost;

namespace Labs626.UrTask.Tests;

public class MacroPlayerClientSpaceTests
{
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
        // maximize-first flow. IsMaximizedFlag only flips which size ClientSize
        // reports — it never mutates Client/Outer. RestoreDown just clears the
        // flag and "leaves size as last-set" (whatever Client/ClientAfterResize
        // already held before Maximize was called), same as Windows restoring a
        // maximized window back to its pre-maximize rect.
        public bool IsMaximizedFlag;
        public int MaximizeCalls;
        public int RestoreDownCalls;
        // Configurable "maximized client size" a test can pin exactly. Left null
        // to fall back to the fake's work area minus the (Outer - Client) chrome
        // delta — mirrors ShowWindow(SW_MAXIMIZE) blowing past the max-track
        // ceiling to fill the monitor's work area.
        public (int W, int H)? MaximizedClient;

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
    /// Resize path under the maximize-first flow: the fake pins an explicit
    /// maximized client size (2560x1440 — a full monitor) that overshoots the
    /// recorded 816x638 normal-window size, so EnsureClientSize restores back
    /// down and fits the window to the exact recorded client size via the outer
    /// delta. The new flow always anchors the fitted window at the work area's
    /// top-left (0,0 in this fake) rather than the window's original position —
    /// that's the documented tradeoff of maximize-first (it always re-anchors
    /// top-left), so the expected SetOuterRect call reflects (0,0), not the
    /// original (10,20). The resize sticks (ClientAfterResize matches recorded),
    /// so playback proceeds past preflight into the event loop and only then
    /// aborts via ClientOrigin() = null — same technique as the sibling tests,
    /// proving the maximize/restore/resize path actually ran and injection was
    /// reached, without synthesizing real input.
    /// </summary>
    [Fact]
    public async Task ClientMacro_SizeMismatch_ResizesByClientDelta_AbortsBeforeInject()
    {
        var metrics = new FakeMetrics
        {
            Client = (700, 500),
            Outer = (10, 20, 714, 542),          // chrome = 14 x 42
            MaximizedClient = (2560, 1440),      // explicit override — full monitor
            ClientAfterResize = (816, 638),      // resize verified — sticks
            Origin = null,                       // abort at inject time, not send time
        };
        var player = new MacroPlayer(new FakeForeground { Current = Target }, metrics);
        var result = await player.PlayAsync(ClientMacro(816, 638, events: new[] { OneMouseMove }), targetUserId: 42);
        Assert.Equal(PlaybackOutcome.Aborted, result.Outcome);
        Assert.Contains("vanished", result.Reason);
        Assert.Equal(1, metrics.MaximizeCalls);
        Assert.Equal(1, metrics.RestoreDownCalls);
        var call = Assert.Single(metrics.SetCalls);
        // Position is the work area's top-left (0,0), not the window's original
        // position (10,20) — maximize-first always re-anchors there. Size still
        // grows by the client delta: (816-700, 638-500) = (116, 138) added to the
        // original outer (714, 542).
        Assert.Equal((0, 0, 830, 680), call);
    }

    /// <summary>
    /// Resize-doesn't-stick path under the maximize-first flow: MaximizedClient is
    /// left unset here, exercising the fake's *default* maximized-size
    /// computation (work area minus chrome — 2546x1398), which still overshoots
    /// the recorded 816x638 enough to reach the restore-and-fit branch. Restore +
    /// fit-to-size runs, but the window's own minimum fights back and the
    /// post-resize client size (750x520) never reaches the recorded 816x638 —
    /// EnsureClientSize refuses instead of proceeding. Confirms no input was ever
    /// injected by asserting Started never fired (Started only fires once the
    /// resize preflight has succeeded).
    /// </summary>
    [Fact]
    public async Task ClientMacro_ResizeDoesNotStick_Refuses()
    {
        var metrics = new FakeMetrics
        {
            Client = (700, 500),
            Outer = (10, 20, 714, 542),
            ClientAfterResize = (750, 520),      // window's own minimum fought back
        };
        var player = new MacroPlayer(new FakeForeground { Current = Target }, metrics);
        bool started = false;
        player.Started += (_, _) => started = true;
        // Refusal fires during preflight, before the event loop — one mouse event at
        // any timestamp is safe here, it never gets a chance to fire.
        var result = await player.PlayAsync(ClientMacro(816, 638, events: new[] { OneMouseMove }), targetUserId: 42);
        Assert.Equal(PlaybackOutcome.Refused, result.Outcome);
        Assert.Contains("recorded client size", result.Reason);
        Assert.Equal(1, metrics.MaximizeCalls);
        Assert.Equal(1, metrics.RestoreDownCalls);
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
}
