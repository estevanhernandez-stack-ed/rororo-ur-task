using System.IO;
using Labs626.UrTask.Diagnostics;
using Labs626.UrTask.Macros;
using Labs626.UrTask.Macros.Steps;
using Labs626.UrTask.PluginHost;

namespace Labs626.UrTask.Tests;

/// <summary>
/// MacroPlayer's point-macro path (PlayStepsAsync) against a fake window that enforces Roblox's
/// minimum size, as spec §6 asks: a harness without that clamp passed once and then failed on real
/// hardware. SendInput cannot be faked, so every macro here either plays wait steps only or stops
/// on a check before any input is sent.
/// </summary>
[Collection("DiagLog")]
public class MacroPlayerStepPathTests : IDisposable
{
    private readonly string _dir = Path.Combine(Path.GetTempPath(), "urtask-steppath-" + Guid.NewGuid().ToString("N"));

    public MacroPlayerStepPathTests()
    {
        DiagLog.Directory = Path.Combine(_dir, "logs");
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

    private sealed class FakeScale(int percent) : IDisplayScale
    {
        public int Calls;
        public int ScalePercentFor(IntPtr hwnd) { Calls++; return percent; }
    }

    /// <summary>Returns nothing, so a checked point stops with "could not see the window" before
    /// it presses. Records what was asked for, which is where the point was placed.</summary>
    private sealed class BlindSampler : IScreenSampler
    {
        public List<(int X, int Y, int W, int H)> Rects = new();
        public PixelBlock? Capture(IntPtr hwnd, int clientX, int clientY, int w, int h)
        {
            Rects.Add((clientX, clientY, w, h));
            return null;
        }
    }

    /// <summary>
    /// A window whose outer size Roblox will not let go below <see cref="MinOuter"/> (its scaled
    /// minimum) nor above the monitor. The client follows the outer size through a constant chrome.
    /// </summary>
    private sealed class FakeMetrics : IWindowMetrics
    {
        public IntPtr Hwnd = new(0x1234);
        public (int W, int H) Client = (700, 500);
        public (int X, int Y, int W, int H) Outer = (10, 20, 716, 539); // chrome 16 x 39
        public (int W, int H) MinOuter = PointMath.RobloxMinOuter(100);
        public (int W, int H) MaxOuter = (2560, 1440);
        public (int X, int Y, int W, int H) WorkArea = (0, 0, 2560, 1440);
        // Null: no client origin, so a client-space mouse event could never reach SendInput.
        public (int X, int Y)? Origin = null;
        public List<(int x, int y, int w, int h)> SetCalls = new();
        public int HwndForPidCalls;

        public IntPtr HwndForPid(int pid) { HwndForPidCalls++; return Hwnd; }
        public (int X, int Y)? ClientOrigin(IntPtr hwnd) => Origin;
        public (int W, int H)? ClientSize(IntPtr hwnd) => Client;
        public (int X, int Y, int W, int H)? OuterRect(IntPtr hwnd) => Outer;
        public bool SetOuterRect(IntPtr hwnd, int x, int y, int w, int h)
        {
            SetCalls.Add((x, y, w, h));
            int gw = Math.Clamp(w, MinOuter.W, MaxOuter.W), gh = Math.Clamp(h, MinOuter.H, MaxOuter.H);
            Client = (gw - (Outer.W - Client.W), gh - (Outer.H - Client.H));
            Outer = (x, y, gw, gh);
            return true;
        }
        public bool Minimize(IntPtr hwnd) => true;
        public bool Restore(IntPtr hwnd) => true;
        public (int X, int Y, int W, int H) WorkAreaFor(IntPtr hwnd) => WorkArea;
        public void Maximize(IntPtr hwnd) { }
        public void RestoreDown(IntPtr hwnd) { }
        public bool IsMaximized(IntPtr hwnd) => false;
    }

    private static readonly AccountRegistry.AccountInfo Target = new(Pid: 111, RobloxUserId: 42, DisplayName: "Alt", AccountId: "a1");

    private static readonly MacroEvent OneMouseMove = new(0, MacroEventKind.MouseMove, 0, 5, 5, 0, 0);

    private static Macro PointMacro(int w, int h, int recordedScale, IReadOnlyList<MacroStep> steps) => new(
        SchemaVersion: Macro.CurrentSchemaVersion, Id: "m1", Name: "points", RecordMode: "PerWindow",
        RecordedAgainstUserId: null, RecordedAgainstDisplayName: null, InterAltDelayMs: null, RecordedAtUnixMs: 1,
        // The kept original. If the v3 path ran instead, this move with no client origin would abort "vanished".
        Events: new[] { OneMouseMove },
        CoordSpace: Macro.CoordSpaceClient, RecordedClientW: w, RecordedClientH: h,
        RecordedDisplayScale: recordedScale, Steps: steps);

    private MacroPlayer Player(FakeMetrics metrics, IDisplayScale scale, IScreenSampler? sampler = null)
        => new(new FakeForeground { Current = Target }, metrics, sampler ?? new BlindSampler(), scale,
            new PointAdjustmentStore(Path.Combine(_dir, "adjustments.json")));

    [Fact]
    public async Task A_macro_with_steps_plays_the_steps_not_the_recorded_events()
    {
        var metrics = new FakeMetrics { Client = (816, 638), Outer = (10, 20, 832, 677) };
        var scale = new FakeScale(100);
        var player = Player(metrics, scale);
        bool started = false;
        player.Started += (_, _) => started = true;

        var result = await player.PlayAsync(PointMacro(816, 638, 100, new MacroStep[] { new WaitStep(0) }), targetUserId: 42);

        Assert.Equal(PlaybackOutcome.Completed, result.Outcome); // v3 would have aborted "vanished"
        Assert.True(started);
        Assert.Equal(1, metrics.HwndForPidCalls);
        Assert.Equal(1, scale.Calls);
        Assert.Empty(metrics.SetCalls); // already the recorded size
        Assert.False(player.IsPlaying);
    }

    [Fact]
    public async Task Recorded_at_100_and_played_at_125_sizes_the_window_to_one_and_a_quarter()
    {
        var metrics = new FakeMetrics { MinOuter = PointMath.RobloxMinOuter(125) };
        var player = Player(metrics, new FakeScale(125));

        var result = await player.PlayAsync(PointMacro(1000, 800, 100, new MacroStep[] { new WaitStep(0) }), targetUserId: 42);

        Assert.Equal(PlaybackOutcome.Completed, result.Outcome);
        var call = Assert.Single(metrics.SetCalls);
        Assert.Equal((1250 + 16, 1000 + 39), (call.w, call.h));
        Assert.Equal((1250, 1000), metrics.Client);
    }

    [Fact]
    public async Task A_target_below_Roblox_minimum_size_refuses_before_playing()
    {
        // 800x599 at 100% wants 1000x749 at 125%: an outer 1016x788, under Roblox's 1020x797 there.
        // The window stops at the minimum, 1004x758, which is 9 px taller than slop allows.
        var metrics = new FakeMetrics { MinOuter = PointMath.RobloxMinOuter(125) };
        var player = Player(metrics, new FakeScale(125));
        bool started = false;
        player.Started += (_, _) => started = true;

        var result = await player.PlayAsync(PointMacro(800, 599, 100, new MacroStep[] { new WaitStep(0) }), targetUserId: 42);

        Assert.Equal(PlaybackOutcome.Refused, result.Outcome);
        Assert.Equal((1016, 788), (metrics.SetCalls.Single().w, metrics.SetCalls.Single().h));
        Assert.Equal((1004, 758), metrics.Client);
        Assert.StartsWith("Roblox wouldn't shrink this window to 1000x749; it stayed 1004x758. At 125% display scale Roblox's smallest window is 1020x797.", result.Reason);
        Assert.False(started);
    }

    [Fact]
    public async Task A_check_that_stops_the_macro_reports_its_step_to_the_alt_outcome()
    {
        // Recorded at 100%, played at 125%: the point (400, 300) lands at (500, 375) in the reached
        // window, so its 5x5 box is sampled at (498, 373). The sampler sees nothing, so step 2
        // stops before pressing and its index travels through SequencePlayer into AltOutcome.
        var metrics = new FakeMetrics { MinOuter = PointMath.RobloxMinOuter(125) };
        var sampler = new BlindSampler();
        var fg = new FakeForeground { Current = Target };
        var player = new MacroPlayer(fg, metrics, sampler, new FakeScale(125),
            new PointAdjustmentStore(Path.Combine(_dir, "adjustments.json")));
        var check = new ColorCheck(new CheckBox(), new Rgb(139, 224, 58));
        var macro = PointMacro(1000, 800, 100, new MacroStep[]
        {
            new WaitStep(0),
            new PointStep(0, "p1", "Tile", 400, 300, Check: check, CheckEnabled: true),
        });
        var sequence = new SequencePlayer(player, fg, _ => (true, null));

        var result = await sequence.PlayAsync(macro, new[] { Target }, interAltDelayMs: 0);

        var alt = Assert.Single(result.PerAlt);
        Assert.Equal(PlaybackOutcome.Aborted, alt.Outcome);
        Assert.Equal(1, alt.StepIndex);
        Assert.Equal("Alt: step 2 'Tile' could not see the window.", alt.Reason);
        Assert.Equal((498, 373, 5, 5), sampler.Rects.Single());
    }

    [Fact]
    public async Task A_ClearAt_macro_hands_its_guard_to_the_step_runner()
    {
        // The guard is the first thing a ClearAt point samples, before any park or input. The
        // blind sampler sees nothing there, so the point stops before any input is sent.
        var metrics = new FakeMetrics { Client = (816, 638), Outer = (10, 20, 832, 677) };
        var sampler = new BlindSampler();
        var player = Player(metrics, new FakeScale(100), sampler);
        var request = new Labs626.UrTask.Ipc.ClearAtRequest("1.0", "ClearAt", "626labs.ur-ocr", "42",
            new Labs626.UrTask.Ipc.ClearAtClient(816, 638), new[] { new Labs626.UrTask.Ipc.ClearAtPoint(400, 300, "ore 1") },
            new Labs626.UrTask.Ipc.ClearAtOutline(50, 50, 60), Guard: new Labs626.UrTask.Ipc.ClearAtGuard(55, 289, 3, 3, new Rgb(255, 19, 90), 30));
        var macro = Labs626.UrTask.Ipc.ClearAtMacro.Build(request, "clearat-pb1");

        var result = await player.PlayAsync(macro, targetUserId: 42);

        Assert.Equal((PlaybackOutcome.Aborted, (int?)0), (result.Outcome, result.StepIndex));
        Assert.Equal((55, 289, 3, 3), sampler.Rects[0]);
    }

    [Fact]
    public async Task A_clean_finish_is_logged()
    {
        var metrics = new FakeMetrics { Client = (816, 638), Outer = (10, 20, 832, 677) };
        var player = Player(metrics, new FakeScale(100));

        await player.PlayAsync(PointMacro(816, 638, 100, new MacroStep[] { new WaitStep(0) }), targetUserId: 42);

        Assert.Contains(File.ReadAllLines(DiagLog.CurrentLogPath),
            l => l.Contains("playback finished: 'points' on Alt in ") && l.EndsWith(" s."));
    }

    [Fact]
    public async Task A_stop_sentence_is_logged()
    {
        var metrics = new FakeMetrics { MinOuter = PointMath.RobloxMinOuter(125) };
        var player = Player(metrics, new FakeScale(125));
        var check = new ColorCheck(new CheckBox(), new Rgb(139, 224, 58));
        var macro = PointMacro(1000, 800, 100, new MacroStep[]
        {
            new WaitStep(0),
            new PointStep(0, "p1", "Tile", 400, 300, Check: check, CheckEnabled: true),
        });

        await player.PlayAsync(macro, targetUserId: 42);

        Assert.Contains(File.ReadAllLines(DiagLog.CurrentLogPath),
            l => l.Contains("playback stopped at step 2: 'points' on Alt after ")
                 && l.EndsWith(" s. Alt: step 2 'Tile' could not see the window."));
    }

    [Fact]
    public async Task A_v3_playback_logs_its_finish_too()
    {
        // No events and no client space: the v3 path runs and sends nothing.
        var v3 = new Macro(SchemaVersion: 3, Id: "m3", Name: "keys only", RecordMode: "PerWindow",
            RecordedAgainstUserId: null, RecordedAgainstDisplayName: null, InterAltDelayMs: null,
            RecordedAtUnixMs: 1, Events: Array.Empty<MacroEvent>());
        var player = Player(new FakeMetrics(), new FakeScale(100));

        var r = await player.PlayAsync(v3, targetUserId: 42);

        Assert.Equal(PlaybackOutcome.Completed, r.Outcome);
        Assert.Contains(File.ReadAllLines(DiagLog.CurrentLogPath), l => l.Contains("playback finished: 'keys only' on Alt in "));
    }
}
