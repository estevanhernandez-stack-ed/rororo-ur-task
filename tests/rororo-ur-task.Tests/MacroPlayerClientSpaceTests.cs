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
        private bool _resized;

        public IntPtr HwndForPid(int pid) => Hwnd;
        public (int X, int Y)? ClientOrigin(IntPtr hwnd) => (0, 0);
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

    private static Macro ClientMacro(int w = 816, int h = 638) => new(
        SchemaVersion: Macro.CurrentSchemaVersion,
        Id: Guid.NewGuid().ToString(), Name: "t", RecordMode: "PerWindow",
        RecordedAgainstUserId: null, RecordedAgainstDisplayName: null,
        InterAltDelayMs: null, RecordedAtUnixMs: 1,
        Events: new List<MacroEvent>(), // zero events — success path must not synthesize input
        CoordSpace: Macro.CoordSpaceClient, RecordedClientW: w, RecordedClientH: h);

    private static readonly AccountRegistry.AccountInfo Target = new(Pid: 111, RobloxUserId: 42, DisplayName: "Alt", AccountId: "a1");

    [Fact]
    public async Task ClientMacro_SizeAlreadyMatches_PlaysWithoutResize()
    {
        var metrics = new FakeMetrics { Client = (816, 638) };
        var player = new MacroPlayer(new FakeForeground { Current = Target }, metrics);
        var result = await player.PlayAsync(ClientMacro(), targetUserId: 42);
        Assert.Equal(PlaybackOutcome.Completed, result.Outcome);
        Assert.Empty(metrics.SetCalls);
    }

    [Fact]
    public async Task ClientMacro_SizeMismatch_ResizesByClientDelta_ThenPlays()
    {
        var metrics = new FakeMetrics
        {
            Client = (700, 500),
            Outer = (10, 20, 714, 542),          // chrome = 14 x 42
            ClientAfterResize = (816, 638),      // resize verified
        };
        var player = new MacroPlayer(new FakeForeground { Current = Target }, metrics);
        var result = await player.PlayAsync(ClientMacro(816, 638), targetUserId: 42);
        Assert.Equal(PlaybackOutcome.Completed, result.Outcome);
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
        var result = await player.PlayAsync(ClientMacro(816, 638), targetUserId: 42);
        Assert.Equal(PlaybackOutcome.Refused, result.Outcome);
        Assert.Contains("recorded client size", result.Reason);
    }

    [Fact]
    public async Task ClientMacro_MissingRecordedSize_Refuses()
    {
        var macro = ClientMacro() with { RecordedClientW = null, RecordedClientH = null };
        var player = new MacroPlayer(new FakeForeground { Current = Target }, new FakeMetrics { Client = (816, 638) });
        var result = await player.PlayAsync(macro, targetUserId: 42);
        Assert.Equal(PlaybackOutcome.Refused, result.Outcome);
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
