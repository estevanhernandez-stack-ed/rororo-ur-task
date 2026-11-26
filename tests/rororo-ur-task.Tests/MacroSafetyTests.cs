using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using Labs626.UrTask.Macros;
using Labs626.UrTask.Macros.Steps;
using Labs626.UrTask.PluginHost;

namespace Labs626.UrTask.Tests;

/// <summary>
/// A macro can come from anyone: a shared bundle, a file an agent wrote. Some keys are acted on
/// by Windows itself whatever window is in front (the Windows key, the app-launch keys, Sleep),
/// so a macro that presses one is refused at import and at playback, with the same sentence.
/// It still loads, so it stays in the library where the user can see it and delete it.
/// </summary>
public class MacroSafetyTests
{
    private static MacroEvent Key(int vk, bool down = true, long at = 0)
        => new(at, down ? MacroEventKind.KeyDown : MacroEventKind.KeyUp, vk, 0, 0, 0, 0);

    private static Macro EventMacro(params MacroEvent[] events) => new(
        SchemaVersion: Macro.CurrentSchemaVersion, Id: Guid.NewGuid().ToString(), Name: "shared", RecordMode: "PerWindow",
        RecordedAgainstUserId: null, RecordedAgainstDisplayName: null, InterAltDelayMs: null, RecordedAtUnixMs: 1,
        Events: events, CoordSpace: Macro.CoordSpaceScreen);

    private static Macro StepMacro(params MacroStep[] steps) => new(
        SchemaVersion: Macro.CurrentSchemaVersion, Id: Guid.NewGuid().ToString(), Name: "shared", RecordMode: "PerWindow",
        RecordedAgainstUserId: null, RecordedAgainstDisplayName: null, InterAltDelayMs: null, RecordedAtUnixMs: 1,
        Events: Array.Empty<MacroEvent>(), CoordSpace: Macro.CoordSpaceClient, RecordedClientW: 800, RecordedClientH: 599,
        Steps: steps);

    public static IEnumerable<object[]> RefusedKeys() => new[]
    {
        0x5B, 0x5C,                                     // left and right Windows keys
        0x5F,                                           // Sleep
        0xA6, 0xA7, 0xA8, 0xA9, 0xAA, 0xAB, 0xAC,       // browser keys
        0xB4, 0xB5, 0xB6, 0xB7,                         // mail, media select, app 1, app 2
    }.Select(vk => new object[] { vk });

    public static IEnumerable<object[]> AllowedKeys() => new[]
    {
        0x12, 0xA4, 0xA5,       // Alt, left Alt, right Alt
        0x73,                   // F4
        0x09, 0x1B, 0x20, 0x0D, // Tab, Esc, Space, Enter
        0x10, 0x11, 0xA0, 0xA2, // Shift, Ctrl
        0x57, 0x41, 0x45, 0x31, // W, A, E, 1
        0x25, 0x28,             // arrows
        0x5A, 0x5D, 0x5E, 0x60, // the neighbours of the Windows and Sleep keys: Z, Menu, reserved, Numpad 0
        0xA5, 0xAD, 0xAE, 0xAF, // the neighbours of the browser keys: right Alt, volume
        0xB0, 0xB1, 0xB2, 0xB3, // media next, previous, stop, play: the neighbours of the launch keys
        0xB8, 0xBA,
        0x70, 0x7B,             // F1, F12
    }.Select(vk => new object[] { vk });

    // ---------- the keys ----------

    [Theory]
    [MemberData(nameof(RefusedKeys))]
    public void ARefusedKey_IsAProblem_WhereverItSits(int vk)
    {
        Assert.NotNull(MacroSafety.FirstProblem(EventMacro(Key(0x57), Key(vk), Key(vk, down: false))));
        Assert.NotNull(MacroSafety.FirstProblem(EventMacro(Key(vk, down: false)))); // a release alone
        Assert.NotNull(MacroSafety.FirstProblem(StepMacro(new WaitStep(10), new KeyStep(0, vk, Down: true))));
        Assert.NotNull(MacroSafety.FirstProblem(StepMacro(new KeyStep(0, vk, Down: false))));
        Assert.NotNull(MacroSafety.FirstProblem(StepMacro(new RawStep(0, new[] { Key(0x57), Key(vk) }, "kept as recorded"))));
    }

    [Theory]
    [MemberData(nameof(AllowedKeys))]
    public void AnOrdinaryKey_IsNotAProblem(int vk)
    {
        Assert.Null(MacroSafety.FirstProblem(EventMacro(Key(vk), Key(vk, down: false))));
        Assert.Null(MacroSafety.FirstProblem(StepMacro(new KeyStep(0, vk, Down: true), new KeyStep(40, vk, Down: false))));
        Assert.Null(MacroSafety.FirstProblem(StepMacro(new RawStep(0, new[] { Key(vk), Key(vk, down: false) }, "kept as recorded"))));
    }

    /// <summary>A step macro keeps its original recording in Events. Those do not play, but they
    /// are what an export or a hand edit falls back to, so they are held to the same rule.</summary>
    [Fact]
    public void TheKeptOriginalRecording_IsCheckedToo()
    {
        var macro = StepMacro(new WaitStep(10)) with { Events = new[] { Key(0x5B) } };

        Assert.NotNull(MacroSafety.FirstProblem(macro));
    }

    /// <summary>Only a key event's key counts. The key field of a mouse event means nothing
    /// (it is 0 from the recorder), so a number there never refuses a macro.</summary>
    [Fact]
    public void AMouseEvent_IsNeverAKeyProblem()
    {
        var click = new MacroEvent(0, MacroEventKind.MouseDown, 0x5B, 10, 10, 1, 0);

        Assert.Null(MacroSafety.FirstProblem(EventMacro(click)));
    }

    [Fact]
    public void AnEmptyMacro_AndOneWithNoLists_AreFine()
    {
        Assert.Null(MacroSafety.FirstProblem(EventMacro()));
        Assert.Null(MacroSafety.FirstProblem(EventMacro() with { Events = null! })); // hand-written JSON
        Assert.Null(MacroSafety.FirstProblem(StepMacro(new RawStep(0, null!, "no events"))));
    }

    [Fact]
    public void TheSentence_NamesTheKey_SaysWhy_AndWhatToDo()
    {
        Assert.Equal(
            "This macro presses the Windows key. Windows acts on that key even while Roblox is in front, " +
            "so Ur Task will not import or play it. Record it again without that key, or delete it.",
            MacroSafety.FirstProblem(EventMacro(Key(0x5B))));

        Assert.Equal(
            "This macro presses the Sleep key at step 2. Windows acts on that key even while Roblox is in front, " +
            "so Ur Task will not import or play it. Record it again without that key, or delete it.",
            MacroSafety.FirstProblem(StepMacro(new WaitStep(10), new KeyStep(0, 0x5F, Down: true))));

        Assert.Contains("a browser key", MacroSafety.FirstProblem(EventMacro(Key(0xAC))));
        Assert.Contains("the Mail key", MacroSafety.FirstProblem(EventMacro(Key(0xB4))));
        Assert.Contains("the Media key", MacroSafety.FirstProblem(EventMacro(Key(0xB5))));
        Assert.Contains("an app launch key", MacroSafety.FirstProblem(EventMacro(Key(0xB6))));
        Assert.Contains("an app launch key", MacroSafety.FirstProblem(EventMacro(Key(0xB7))));
    }

    // ---------- nothing real is refused ----------

    private static string RepoRoot()
    {
        var dir = new DirectoryInfo(AppContext.BaseDirectory);
        while (dir is not null && !File.Exists(Path.Combine(dir.FullName, "rororo-ur-task.csproj"))) dir = dir.Parent;
        Assert.True(dir is not null, "Could not find the repo root.");
        return dir!.FullName;
    }

    /// <summary>The real recordings and the shipped example macros must all still import and play.</summary>
    [Fact]
    public void EveryFixtureAndExampleMacro_HasNoProblem()
    {
        var files = Directory.GetFiles(Path.Combine(RepoRoot(), "tests", "fixtures"), "*.json")
            .Concat(Directory.GetFiles(Path.Combine(RepoRoot(), "docs", "reference", "events", "macros", "space-mine-ore-stop", "macros"), "*.json"))
            .ToList();
        Assert.True(files.Count >= 20, $"Expected the fixture and example macros, found {files.Count} files.");

        foreach (var file in files)
        {
            var macro = MacroV1Migrator.LoadAndMigrate(File.ReadAllText(file));
            Assert.True(MacroSafety.FirstProblem(macro) is null, $"{Path.GetFileName(file)}: {MacroSafety.FirstProblem(macro)}");
        }
    }

    /// <summary>
    /// Why there is no "click outside the game window" refusal. The recorder takes every mouse
    /// press on the screen and writes it relative to the game window, with no bounds: stop a mouse
    /// recording with the STOP button instead of the hotkey and the macro ends with a press on
    /// Ur Task's own window, outside the recorded size. Those macros exist and play today, so a
    /// point-bounds refusal would refuse real recordings. This pins that they are not refused.
    /// </summary>
    [Fact]
    public void ARecordingStoppedWithTheStopButton_HasAPressOutsideTheWindow_AndIsNotRefused()
    {
        const int WM_LBUTTONDOWN = 0x0201;
        // The game window's client area starts at (100, 100) and is 800 x 599. The STOP button is
        // in another window, at screen (1500, 300).
        var stopClick = MacroRecorder.BuildMouseEvent(WM_LBUTTONDOWN, 1500, 300, 0, timestampMs: 5000, clientOrigin: (100, 100));
        Assert.NotNull(stopClick);
        Assert.True(stopClick!.X >= 800, "The recorder writes a press outside the game window as it is, out of bounds.");

        var recording = EventMacro(new MacroEvent(1000, MacroEventKind.MouseDown, 0, 400, 300, 1, 0),
                                   new MacroEvent(1100, MacroEventKind.MouseUp, 0, 400, 300, 1, 0),
                                   stopClick) with
        {
            CoordSpace = Macro.CoordSpaceClient, RecordedClientW = 800, RecordedClientH = 599,
        };

        Assert.Null(MacroSafety.FirstProblem(recording));
        Assert.Null(MacroSafety.FirstProblem(RecordingFinalizer.WithSteps(recording, displayScale: 100)));
    }

    // ---------- import ----------

    [Fact]
    public void Import_SkipsAMacroThatPressesARefusedKey_AndTakesTheRest()
    {
        var bundle = MacroBundle.Serialize(new[]
        {
            EventMacro(Key(0x57), Key(0x57, down: false)) with { Name = "walk" },
            EventMacro(Key(0x5B), Key(0x52), Key(0x52, down: false), Key(0x5B, down: false)) with { Name = "win+r" },
            EventMacro(Key(0x20), Key(0x20, down: false)) with { Name = "jump" },
        }, exportedAtUnixMs: 1);

        var result = MacroBundle.Parse(bundle);

        Assert.Equal(new[] { "walk", "jump" }, result.Macros.Select(m => m.Name).ToArray());
        var failure = Assert.Single(result.Failures);
        Assert.Equal("win+r", failure.Label);
        Assert.StartsWith("This macro presses the Windows key.", failure.Reason);
    }

    [Fact]
    public void Import_FromAFile_IsOneLineInTheLogPerRefusedMacro()
    {
        var dir = Path.Combine(Path.GetTempPath(), "urtask-tests", Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(dir);
        try
        {
            var bundle = Path.Combine(dir, "share.json");
            File.WriteAllText(bundle, MacroBundle.Serialize(new[]
            {
                EventMacro(Key(0x5F)) with { Name = "sleepy" },
                EventMacro(Key(0x20), Key(0x20, down: false)) with { Name = "jump" },
            }, 1));
            // A single macro file, as it sits in someone's macros folder: no bundle around it.
            var bare = Path.Combine(dir, "bare.json");
            File.WriteAllText(bare, """
                { "schemaVersion": 4, "id": "ffffffff-0000-4000-8000-00000000000a", "name": "start menu",
                  "recordMode": "PerWindow", "recordedAtUnixMs": 1, "coordSpace": "screen", "events": [],
                  "steps": [ { "kind": "key", "delayMs": 0, "virtualKeyCode": 91, "down": true } ] }
                """);

            var fromBundle = MacroBundle.ReadForImport(bundle);
            var fromBare = MacroBundle.ReadForImport(bare);

            Assert.Equal("jump", Assert.Single(fromBundle.Macros).Name);
            Assert.StartsWith("Skipped sleepy in share.json: This macro presses the Sleep key.", Assert.Single(fromBundle.Log));
            Assert.Empty(fromBare.Macros);
            Assert.StartsWith("Nothing was imported from bare.json. This macro presses the Windows key at step 1.", Assert.Single(fromBare.Log));
        }
        finally { try { Directory.Delete(dir, recursive: true); } catch { } }
    }

    // ---------- the store still loads it ----------

    [Fact]
    public void AMacroWithARefusedKey_StillLoads_SoItCanBeSeenAndDeleted()
    {
        var dir = Path.Combine(Path.GetTempPath(), "urtask-tests", Guid.NewGuid().ToString("N"));
        try
        {
            var store = new MacroStore(dir);
            var macro = EventMacro(Key(0x5B));
            store.Save(macro);

            var loaded = store.LoadAll();

            Assert.Empty(loaded.Failures);
            Assert.Equal(macro.Id, Assert.Single(loaded.Macros).Id);
            store.Delete(macro.Id);
            Assert.Empty(store.LoadAll().Macros);
        }
        finally { try { Directory.Delete(dir, recursive: true); } catch { } }
    }
}

/// <summary>
/// The same refusal at playback. MacroPlayer writes a line to the diagnostic log when a playback
/// ends, so these run in the DiagLog collection with the log pointed at a temp folder, like the
/// other MacroPlayer tests. Every test here is built so that SendInput is out of reach even with
/// the safety check missing: a failing run must not press a real Windows key.
/// </summary>
[Collection("DiagLog")]
public class MacroSafetyPlaybackTests : IDisposable
{
    private readonly string _dir = Path.Combine(Path.GetTempPath(), "urtask-safety-" + Guid.NewGuid().ToString("N"));

    public MacroSafetyPlaybackTests()
    {
        Labs626.UrTask.Diagnostics.DiagLog.Directory = Path.Combine(_dir, "logs");
        Labs626.UrTask.Diagnostics.DiagLog.ResetForTests();
    }

    public void Dispose()
    {
        try { Directory.Delete(_dir, recursive: true); } catch { }
    }

    private static MacroEvent Key(int vk, bool down = true, long at = 0)
        => new(at, down ? MacroEventKind.KeyDown : MacroEventKind.KeyUp, vk, 0, 0, 0, 0);

    private static Macro EventMacro(params MacroEvent[] events) => new(
        SchemaVersion: Macro.CurrentSchemaVersion, Id: Guid.NewGuid().ToString(), Name: "shared", RecordMode: "PerWindow",
        RecordedAgainstUserId: null, RecordedAgainstDisplayName: null, InterAltDelayMs: null, RecordedAtUnixMs: 1,
        Events: events, CoordSpace: Macro.CoordSpaceScreen);

    private static Macro StepMacro(params MacroStep[] steps) => new(
        SchemaVersion: Macro.CurrentSchemaVersion, Id: Guid.NewGuid().ToString(), Name: "shared", RecordMode: "PerWindow",
        RecordedAgainstUserId: null, RecordedAgainstDisplayName: null, InterAltDelayMs: null, RecordedAtUnixMs: 1,
        Events: Array.Empty<MacroEvent>(), CoordSpace: Macro.CoordSpaceClient, RecordedClientW: 800, RecordedClientH: 599,
        Steps: steps);

    private MacroPlayer Player(IForegroundWatcher foreground, IWindowMetrics metrics)
        => new(foreground, metrics, adjustments: new PointAdjustmentStore(Path.Combine(_dir, "adjustments.json")));


    /// <summary>No window in front, so even without the safety check this playback is refused at
    /// the preflight and can never reach SendInput: the test must not press a real Windows key.</summary>
    private sealed class NothingInFront : IForegroundWatcher
    {
        public int Resolves;
        public AccountRegistry.AccountInfo? ResolveForegroundAccount() { Interlocked.Increment(ref Resolves); return null; }
    }

    private sealed class NoWindow : IWindowMetrics
    {
        public int Calls;
        public IntPtr HwndForPid(int pid) { Calls++; return IntPtr.Zero; }
        public (int X, int Y)? ClientOrigin(IntPtr hwnd) { Calls++; return null; }
        public (int W, int H)? ClientSize(IntPtr hwnd) { Calls++; return null; }
        public (int X, int Y, int W, int H)? OuterRect(IntPtr hwnd) { Calls++; return null; }
        public bool SetOuterRect(IntPtr hwnd, int x, int y, int w, int h) { Calls++; return false; }
        public bool Minimize(IntPtr hwnd) { Calls++; return false; }
        public bool Restore(IntPtr hwnd) { Calls++; return false; }
        public (int X, int Y, int W, int H) WorkAreaFor(IntPtr hwnd) { Calls++; return (0, 0, 0, 0); }
        public void Maximize(IntPtr hwnd) { Calls++; }
        public void RestoreDown(IntPtr hwnd) { Calls++; }
        public bool IsMaximized(IntPtr hwnd) { Calls++; return false; }
    }

    public static IEnumerable<object[]> UnsafeMacros() => new[]
    {
        new object[] { "events", EventMacro(Key(0x5B), Key(0x5B, down: false)) },
        new object[] { "key step", StepMacro(new KeyStep(0, 0x5C, Down: true)) },
        new object[] { "raw step", StepMacro(new RawStep(0, new[] { Key(0xB6) }, "kept as recorded")) },
    };

    [Theory]
    [MemberData(nameof(UnsafeMacros))]
    public async Task Playback_RefusesWithTheSameSentence_BeforeItLooksAtAnyWindow(string kind, Macro macro)
    {
        var foreground = new NothingInFront();
        var metrics = new NoWindow();
        var started = 0;
        var player = Player(foreground, metrics);
        player.Started += (_, _) => started++;

        var result = await player.PlayAsync(macro, targetUserId: 42);

        Assert.True(result.Outcome == PlaybackOutcome.Refused, kind);
        Assert.Equal(MacroSafety.FirstProblem(macro), result.Reason);
        Assert.NotNull(result.Reason);
        Assert.Equal(0, foreground.Resolves);
        Assert.Equal(0, metrics.Calls);
        Assert.Equal(0, started);
        Assert.False(player.IsPlaying);
    }

    /// <summary>The all-windows path has no foreground preflight to hide behind, so this test
    /// keeps itself away from SendInput another way: the token is already cancelled and the first
    /// event is a minute out, so without the safety check the wait throws before anything is sent.</summary>
    [Fact]
    public async Task AllWindowsPlayback_RefusesWithTheSameSentence()
    {
        var macro = EventMacro(Key(0x5B, at: 60_000), Key(0x5B, down: false, at: 60_050));
        var player = Player(new NothingInFront(), new NoWindow());
        var started = 0;
        player.Started += (_, _) => started++;
        using var cancelled = new CancellationTokenSource();
        cancelled.Cancel();

        var result = await player.PlayAllWindowsRawAsync(macro, cancelled.Token);

        Assert.Equal(PlaybackOutcome.Refused, result.Outcome);
        Assert.Equal(MacroSafety.FirstProblem(macro), result.Reason);
        Assert.Equal(0, started);
    }
}
