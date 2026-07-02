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
        private bool _resized;

        public IntPtr HwndForPid(int pid) { HwndForPidCalls++; return Hwnd; }
        public (int X, int Y)? ClientOrigin(IntPtr hwnd) => Origin;
        public (int W, int H)? ClientSize(IntPtr hwnd) => _resized ? (ClientAfterResize ?? Client) : Client;
        public (int X, int Y, int W, int H)? OuterRect(IntPtr hwnd) => Outer;
        public bool SetOuterRect(IntPtr hwnd, int x, int y, int w, int h)
        {
            SetCalls.Add((x, y, w, h));
            _resized = true;
            return SetResult;
        }
        public (int X, int Y, int W, int H) WorkAreaFor(IntPtr hwnd) => (0, 0, 2560, 1440);
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

    /// <summary>Same technique as above — proves the resize call fires, then aborts
    /// before real input via ClientOrigin() = null instead of completing the play.</summary>
    [Fact]
    public async Task ClientMacro_SizeMismatch_ResizesByClientDelta_AbortsBeforeInject()
    {
        var metrics = new FakeMetrics
        {
            Client = (700, 500),
            Outer = (10, 20, 714, 542),          // chrome = 14 x 42
            ClientAfterResize = (816, 638),      // resize verified
            Origin = null,                       // abort at inject time, not send time
        };
        var player = new MacroPlayer(new FakeForeground { Current = Target }, metrics);
        var result = await player.PlayAsync(ClientMacro(816, 638, events: new[] { OneMouseMove }), targetUserId: 42);
        Assert.Equal(PlaybackOutcome.Aborted, result.Outcome);
        Assert.Contains("vanished", result.Reason);
        var call = Assert.Single(metrics.SetCalls);
        Assert.Equal((10, 20, 830, 680), call);  // outer grows by client delta, position kept
    }

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
        // Refusal fires during preflight, before the event loop — one mouse event at
        // any timestamp is safe here, it never gets a chance to fire.
        var result = await player.PlayAsync(ClientMacro(816, 638, events: new[] { OneMouseMove }), targetUserId: 42);
        Assert.Equal(PlaybackOutcome.Refused, result.Outcome);
        Assert.Contains("recorded client size", result.Reason);
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
