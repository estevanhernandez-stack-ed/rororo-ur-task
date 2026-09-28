# Point clicks engine — implementation plan

> **For agentic workers:** REQUIRED SUB-SKILL: Use superpowers:subagent-driven-development (recommended) or superpowers:executing-plans to implement this plan task-by-task. Steps use checkbox (`- [ ]`) syntax for tracking.

**Goal:** Mouse recordings play as point steps that can check a colour before pressing, branch with a first-match step, honour per-account adjustments, and report how a bridge playback ended.

**Architecture:** A macro gains a schema v4 `steps` list beside its untouched `events`. A pure converter turns events into steps at save. A new `StepRunner` plays steps through a small I/O seam (`IStepIo`), so every rule (wait for colour, nearby search, first match, reports) is unit-tested without a window. `MacroPlayer` routes macros that have steps to the runner; v3 macros keep today's event path unchanged. The action bridge gains `GetPlayback` backed by a playback registry that keeps finished results for 10 minutes.

**Tech Stack:** C# / .NET 10 WPF plugin, System.Text.Json polymorphic records, Win32 GDI capture (BitBlt + GetDIBits) and SendInput, xUnit.

**Spec:** `docs/superpowers/specs/2026-09-27-point-clicks-and-checks-design.md` (approved 2026-09-27). Read it before starting.

**This is plan 1 of 2.** It builds the engine. Plan 2, written once this ships, builds the points overlay: dragging points, enabling checks with a fresh sample, "Make first match", and the "Convert to points" button for old macros. Until plan 2, checks and first-match steps are authored in the macro JSON (by an agent), which the spec allows.

## Global Constraints

- Build `rororo-ur-task.csproj`, never the `.sln` (it drags in the host repo and fails while RoRoRo runs).
- Fast test command, used by every task: `dotnet test tests/rororo-ur-task.Tests/rororo-ur-task.Tests.csproj -p:StandaloneTestsOnly=true`
- **Test gate is "no new failures".** The baseline has 2 environmental `HotkeyServiceTests` failures (Win32 error 1409, hotkey already registered: a live Ur Task holds Ctrl+Shift+R). Wherever a step below says "all pass", read it as "all pass except those 2, and nothing new fails".
- Schema: `Macro.CurrentSchemaVersion` becomes `4`. v3 playback is unchanged: a macro without `steps` plays through the existing event path exactly as before. The only v3-visible change is the size-refusal wording (spec §5), in Task 7.
- `events` is never dropped from a v4 file. It is the original recording.
- Bridge contract: add fields and methods only. Keep every existing reply field (`ok`, `playbackId`, `queued`, `reason`, `detail`, `stopped`, `macros[].id/name`). Keep accepting contract version `"1.0"`. `RunMacro` still returns when playback starts. The unknown-method answer (`reason: "refused"`, `detail: "Unknown method '<name>'."`) must not change: Ur MCP detects a pre-0.9 Ur Task by that detail. `GetPlayback`'s `stepIndex` is 1-based on the wire; the in-process `PlaybackResult.StepIndex` stays 0-based.
- Adjustments live at `%LOCALAPPDATA%\626Labs\RoRoRoUrTask\adjustments.json`, never inside `macros\` (Ur Task and Ur OCR read every `.json` there as a macro).
- Defaults, verbatim from the spec: check box 5x5, at most 9x9; tolerance 15 (Euclidean RGB); wait ceiling = the step's delay plus 3 s; nearby search radius about 24 px, scaled like the point; same-spot threshold 8 px (spec amended from 4 px: the real "Mining Z8 Egg" recording has a click whose down and up are 6 px apart); jump and wiggle about 0.15 s; finished playbacks kept 10 minutes.
- Version: `rororo-ur-task.csproj` `<Version>` and `manifest.json` `"version"` must agree (a test enforces it). Target `0.9.0`.
- Commits: conventional commits. Run `powershell -ExecutionPolicy Bypass -File .claude/hooks/install.ps1` once per clone so the secret scan and local-path guard run.
- Copy: sentences, sentence case, no emoji. Failure text names the account, step, label, expected and seen colour, distance, wait, and display scale.

## Deviations from the spec, decided while planning

1. **No record-time colour sample.** The spec lets every point store a sample at record time but says checks are off by default and enabling one takes a fresh sample with the pointer parked. A record-time sample is therefore never used, so it is not taken. `PointStep.Check` stays null until someone enables a check. Saves a capture per click in the recorder's hook path.
2. **`PointerMove` is played but not yet detected in recordings.** The step exists, plays as relative mouse movement, and exports. Detecting pointer-locked movement from a recording needs a real first-person recording to test against, and none exists yet.
3. **An older Ur Task opening a v4 file plays the raw `events`.** The spec expected it to say which version it needs; old code cannot be taught that. Playing the original recording is the honest fallback and works.
4. **`GetPlayback` reply:** `reason` is a short code (`check-failed`, `refused`, `aborted`, `error`) and `detail` is the full sentence. The Ur MCP session was told both would be sentences; tell it this when the task ships.
5. **First match waits until the candidates ahead of a match are settled**, rather than giving the wait to the first candidate only. A candidate is settled when it shows its expected or its other colour. So a locked #8 (grey) lets #7 be pressed on the first poll, while a Teleport window still sliding in (neither colour yet) is waited for, up to the step's delay plus 3 s. This is the live 2026-09-27 failure the spec records, and the plain "first candidate only" rule would have pressed #7 before #8 had drawn.
6. **The overlay's file lock is plan 2.** It belongs with the overlay that takes it.
7. **A key is two steps, a down and an up, not one step with a held duration.** The spec's `Key` holds "how long it is held"; here the held time is the up step's `delayMs`, measured from the down. Separate records carry the real held time and still play overlapping holds (the egg recording holds A, then W, then A again), and Tasks 6 and 10 consume them as-is. Plan 2's overlay reads a key's held time from its up step's delay.

## Review Focus

1. **Hand-edited or agent-written v4 JSON**: `kind` not first, an unknown `kind`, a missing field. Expect: nothing crashes and other macros load. An unknown `kind` or a missing `expect` makes the file a listed load failure with a reason. A missing check `box` or point `id` is refused by `StepValidator` with a sentence before any input. Tests in Task 2.
2. **A check box that falls partly outside the window**, including nearby-search offsets that cross the edge. Expect: never sample pixels outside the client area; a box outside refuses with "outside the window"; search offsets that cross the edge are skipped. Tests in Tasks 4 and 6.
3. **A first-match step with no candidates, or a candidate without a check.** Expect: playback refuses before the first input with a sentence naming the step. Tests in Tasks 2 and 6.
4. **`repeat: true` on a macro whose check fails.** Expect: the repeat loop stops at the failure instead of retrying forever, and `GetPlayback` reports `failed` with the sentence. Test in Task 9.
5. **Esc or `StopMacro` while a check is waiting for its colour.** Expect: the wait ends at the next poll, input stops, held keys and buttons are released. Test in Task 6.

---

## File map

Create:

| File | Responsibility |
|---|---|
| `src/Macros/Steps/ColorCheck.cs` | `Rgb`, `CheckBox`, `ColorCheck`, `ColorMatcher`, `ColorNamer` (pure) |
| `src/Macros/Steps/MacroStep.cs` | Step records, polymorphic JSON, `StepValidator`, `StepTiming` |
| `src/Macros/Steps/StepConverter.cs` | Events to steps (pure) |
| `src/Macros/Steps/PointMath.cs` | Target size, placement, search offsets, Roblox minimum (pure) |
| `src/Macros/Steps/PointAdjustmentStore.cs` | `adjustments.json` read/write |
| `src/Macros/Steps/PixelBlock.cs` | Captured pixels plus box averaging (pure) |
| `src/Macros/Steps/StepRunner.cs` | Plays steps through `IStepIo`; all check rules and reports |
| `src/Macros/Steps/RealStepIo.cs` | `IStepIo` over SendInput, `IWindowMetrics`, `IScreenSampler` |
| `src/Macros/Steps/RecordingFinalizer.cs` | Adds steps and display scale to a new recording (pure) |
| `src/PluginHost/IScreenSampler.cs`, `ScreenSampler.cs` | GDI capture of a client rect |
| `src/PluginHost/IDisplayScale.cs`, `DisplayScale.cs` | Display scale per window |
| `src/Ipc/PlaybackRegistry.cs` | Playback states with 10-minute retention |
| `tests/rororo-ur-task.Tests/Steps/*.cs` | Tests per unit |
| `tests/fixtures/mine-zone-8.json`, `mining-z8-egg.json` | Real recordings, anonymised (repo convention: `tests/fixtures`, linked into the test output) |

Modify: `src/Macros/Macro.cs`, `src/Macros/MacroV1Migrator.cs`, `src/Macros/MacroStore.cs`, `src/Macros/MacroBundle.cs`, `src/Macros/MacroPlayer.cs`, `src/Macros/SequenceTypes.cs`, `src/Macros/SequencePlayer.cs`, `src/Macros/AutoHotkeyExporter.cs`, `src/PluginRuntime.cs`, `src/Ipc/BridgeContract.cs`, `src/Ipc/IMacroRunInvoker.cs`, `src/Ipc/MacroRunInvoker.cs`, `src/Ipc/MacroRunnerServer.cs`, test fakes of `IMacroRunInvoker`, `tests/rororo-ur-task.Tests/MacroV1MigrationTests.cs` and `MacroV3MigrationTests.cs` (schema-3 asserts), `rororo-ur-task.csproj`, `manifest.json`, `CHANGELOG.md`, `docs/BACKLOG.md`, `docs/display-scale-findings.md`, `tests/rororo-ur-task.Tests/rororo-ur-task.Tests.csproj`.

---

### Task 0: Branch and baseline

**Files:** none changed.

> **Done, 2026-09-27.** Branch `feat/point-clicks` exists and the docs merge is commit `d75cbdf` ("merge: event-authoring docs, spec and plan for point clicks"). Do not re-run Step 1; `git checkout -b` would fail on the existing branch. On a fresh clone, `git checkout feat/point-clicks` and run Step 2.
>
> **Baseline:** the suite has 2 environmental `HotkeyServiceTests` failures (Win32 error 1409: a live Ur Task already holds Ctrl+Shift+R). They are not caused by this work. The gate for every later task is "no new failures", not a fully green run.

- [x] **Step 1: Branch from main and bring the spec and plan along** (done: `d75cbdf`)

Run from the repo root (check with `git rev-parse --show-toplevel`; stuck working directories have bitten this estate before):

```bash
git fetch origin
git checkout -b feat/point-clicks origin/main
git merge --no-ff docs/event-authoring-sept-2026 -m "merge: event-authoring docs, spec and plan for point clicks"
```

If the docs branch only exists on the remote (a fresh PC), merge `origin/docs/event-authoring-sept-2026` instead. Expected: a clean merge (the docs branch touches only `docs/`, `tools/`, `.gitignore`).

- [x] **Step 2: Install the guard hooks and run the baseline** (done; baseline has the 2 known `HotkeyServiceTests` failures above)

```bash
powershell -ExecutionPolicy Bypass -File .claude/hooks/install.ps1
dotnet build rororo-ur-task.csproj
dotnet test tests/rororo-ur-task.Tests/rororo-ur-task.Tests.csproj -p:StandaloneTestsOnly=true
```

Expected: build succeeds, and every test passes except the 2 environmental `HotkeyServiceTests` failures. Record the pass count. Every later task must keep it, add to it, and introduce no new failures.

---

### Task 1: Colours and matching

**Files:**
- Create: `src/Macros/Steps/ColorCheck.cs`
- Test: `tests/rororo-ur-task.Tests/Steps/ColorCheckTests.cs`

**Interfaces:**
- Produces: `Rgb(int R, int G, int B)` with `DistanceTo(Rgb)` and `Hex`; `CheckBox(int OffsetX = -2, int OffsetY = -2, int W = 5, int H = 5)` with `IsValid`, `MaxSide = 9`; `ColorCheck(CheckBox Box, Rgb Expect, Rgb? Other = null, int Tolerance = 15)`; `ColorVerdict(bool Matched, double Distance, double? DistanceToOther)`; `ColorMatcher.Evaluate(Rgb seen, ColorCheck check)`; `ColorMatcher.ShowsOther(Rgb seen, ColorCheck check)` (the one "shows its other state" rule, used by both check paths in Task 6); `ColorNamer.Describe(Rgb)` returning e.g. `"green #8BE03A"`.

- [ ] **Step 1: Write the failing tests**

```csharp
// tests/rororo-ur-task.Tests/Steps/ColorCheckTests.cs
using Labs626.UrTask.Macros.Steps;

namespace Labs626.UrTask.Tests.Steps;

public class ColorCheckTests
{
    private static readonly Rgb Green = new(139, 224, 58);
    private static readonly Rgb Grey = new(128, 128, 128);

    [Fact]
    public void Distance_is_euclidean_rgb()
        => Assert.Equal(5.0, new Rgb(0, 3, 4).DistanceTo(new Rgb(0, 0, 0)), 3);

    [Fact]
    public void A_colour_is_stored_as_exactly_r_g_b()
    {
        // The shape Ur OCR matches. A stray "hex" key here is the bug this pins.
        var opts = new System.Text.Json.JsonSerializerOptions { PropertyNamingPolicy = System.Text.Json.JsonNamingPolicy.CamelCase };
        Assert.Equal("{\"r\":139,\"g\":224,\"b\":58}", System.Text.Json.JsonSerializer.Serialize(Green, opts));
        Assert.Equal("{\"offsetX\":-2,\"offsetY\":-2,\"w\":5,\"h\":5}", System.Text.Json.JsonSerializer.Serialize(new CheckBox(), opts));
    }

    [Fact]
    public void Within_tolerance_matches()
    {
        var v = ColorMatcher.Evaluate(new Rgb(135, 220, 60), new ColorCheck(new CheckBox(), Green));
        Assert.True(v.Matched);
    }

    [Fact]
    public void Beyond_tolerance_does_not_match()
    {
        var v = ColorMatcher.Evaluate(Grey, new ColorCheck(new CheckBox(), Green));
        Assert.False(v.Matched);
        Assert.True(v.Distance > 15);
    }

    [Fact]
    public void Other_state_wins_when_closer_even_inside_tolerance()
    {
        // A wide tolerance alone would accept this; the other-state rule must not.
        var check = new ColorCheck(new CheckBox(), Expect: new Rgb(120, 140, 120), Other: Grey, Tolerance: 40);
        var v = ColorMatcher.Evaluate(new Rgb(126, 130, 126), check);
        Assert.False(v.Matched);
        Assert.NotNull(v.DistanceToOther);
    }

    [Fact]
    public void Shows_other_only_when_within_tolerance_of_other_and_nearer_it()
    {
        var check = new ColorCheck(new CheckBox(), Green, Other: Grey);
        Assert.True(ColorMatcher.ShowsOther(new Rgb(130, 128, 126), check));   // grey, 2.8 from Other
        Assert.False(ColorMatcher.ShowsOther(Green, check));                   // the expected state
        Assert.False(ColorMatcher.ShowsOther(new Rgb(20, 30, 90), check));     // neither state yet
        Assert.False(ColorMatcher.ShowsOther(Grey, new ColorCheck(new CheckBox(), Green))); // no Other set
    }

    [Theory]
    [InlineData(5, 5, true)]
    [InlineData(9, 9, true)]
    [InlineData(10, 5, false)]
    [InlineData(0, 5, false)]
    public void Box_size_limits(int w, int h, bool valid)
        => Assert.Equal(valid, new CheckBox(0, 0, w, h).IsValid);

    [Theory]
    [InlineData(139, 224, 58, "green #8BE03A")]
    [InlineData(128, 128, 128, "grey #808080")]
    [InlineData(230, 40, 70, "red #E62846")]
    [InlineData(20, 30, 90, "dark blue #141E5A")]
    [InlineData(250, 250, 250, "white #FAFAFA")]
    [InlineData(5, 5, 5, "black #050505")]
    public void Names_colours_for_reports(int r, int g, int b, string expected)
        => Assert.Equal(expected, ColorNamer.Describe(new Rgb(r, g, b)));
}
```

- [ ] **Step 2: Run to verify it fails**

Run: `dotnet test tests/rororo-ur-task.Tests/rororo-ur-task.Tests.csproj -p:StandaloneTestsOnly=true --filter FullyQualifiedName~ColorCheckTests`
Expected: build error, `Rgb` and the other types do not exist.

- [ ] **Step 3: Implement**

```csharp
// src/Macros/Steps/ColorCheck.cs
using System.Text.Json.Serialization;

namespace Labs626.UrTask.Macros.Steps;

/// <summary>An average colour. Serialized as exactly { "r", "g", "b" } — the shape Ur OCR matches.</summary>
public readonly record struct Rgb(int R, int G, int B)
{
    public double DistanceTo(Rgb o)
    {
        var dr = R - o.R; var dg = G - o.G; var db = B - o.B;
        return Math.Sqrt(dr * dr + dg * dg + db * db);
    }

    /// <summary>Not stored: System.Text.Json writes public get-only properties, and a "hex" key
    /// would break the shared { r, g, b } shape (caught by the Ur OCR session, 2026-09-27).</summary>
    [JsonIgnore]
    public string Hex => $"#{R:X2}{G:X2}{B:X2}";
}

/// <summary>
/// The box a check averages, as an offset from its point plus a size. The same box is
/// used when sampling and when checking — Ur OCR samples the clicked pixel but checks the
/// region centre, and this design deliberately does not repeat that.
/// </summary>
public sealed record CheckBox(int OffsetX = -2, int OffsetY = -2, int W = 5, int H = 5)
{
    public const int MaxSide = 9;

    [JsonIgnore] // same reason as Rgb.Hex: keep the stored box to { offsetX, offsetY, w, h }
    public bool IsValid => W >= 1 && H >= 1 && W <= MaxSide && H <= MaxSide;
}

/// <summary>
/// What a point expects to see. <see cref="Other"/> is the colour of the other state
/// (the grey of a locked tile, the red of "Off"): greys drift 20–40 under darkening and
/// hover, so a fixed tolerance alone cannot tell green from grey.
/// <para>Hand- or agent-written JSON: <c>expect</c> is required, because <see cref="Rgb"/> is a value
/// type and a missing one would silently read as black. A file without it fails to load, with the
/// serializer's reason. A missing <c>box</c> reads as null and <c>StepValidator</c> refuses it
/// with a sentence before playback.</para>
/// </summary>
public sealed record ColorCheck(
    CheckBox Box,
    [property: JsonRequired] Rgb Expect,
    Rgb? Other = null,
    int Tolerance = ColorCheck.DefaultTolerance)
{
    /// <summary>Unproven default (Ur OCR only ever shipped single-pixel checks). Checks log
    /// their measured distance so this can be tuned from real runs.</summary>
    public const int DefaultTolerance = 15;
}

public readonly record struct ColorVerdict(bool Matched, double Distance, double? DistanceToOther);

public static class ColorMatcher
{
    /// <summary>Matched = within tolerance of Expect AND, when Other is set, closer to Expect than to Other.</summary>
    public static ColorVerdict Evaluate(Rgb seen, ColorCheck check)
    {
        var d = seen.DistanceTo(check.Expect);
        double? dOther = check.Other is { } o ? seen.DistanceTo(o) : null;
        var matched = d <= check.Tolerance && (dOther is null || d < dOther.Value);
        return new ColorVerdict(matched, d, dOther);
    }

    /// <summary>True when the box shows the check's OTHER state: within tolerance of Other and
    /// nearer it than Expect. The single definition of "settled without matching" — first match
    /// uses it to decide whether a candidate ahead of a match can be skipped.</summary>
    public static bool ShowsOther(Rgb seen, ColorCheck check)
        => check.Other is { } o
           && seen.DistanceTo(o) <= check.Tolerance
           && seen.DistanceTo(o) < seen.DistanceTo(check.Expect);
}

/// <summary>Plain-language colour names for failure reports, with the hex for precision.</summary>
public static class ColorNamer
{
    public static string Describe(Rgb c)
    {
        double r = c.R / 255.0, g = c.G / 255.0, b = c.B / 255.0;
        double max = Math.Max(r, Math.Max(g, b)), min = Math.Min(r, Math.Min(g, b));
        double l = (max + min) / 2, d = max - min;
        double s = d == 0 ? 0 : d / (1 - Math.Abs(2 * l - 1));

        string name;
        if (s < 0.2 || d < 0.08)
        {
            name = l < 0.15 ? "black" : l > 0.85 ? "white" : "grey";
        }
        else
        {
            double h = max == r ? 60 * (((g - b) / d) % 6)
                     : max == g ? 60 * ((b - r) / d + 2)
                     : 60 * ((r - g) / d + 4);
            if (h < 0) h += 360;
            name = h < 15 || h >= 345 ? "red"
                 : h < 40 ? "orange"
                 : h < 70 ? "yellow"
                 : h < 165 ? "green"
                 : h < 195 ? "cyan"
                 : h < 255 ? "blue"
                 : h < 290 ? "purple"
                 : "pink";
            if (l < 0.3) name = "dark " + name;
        }
        return $"{name} {c.Hex}";
    }
}
```

- [ ] **Step 4: Run to verify it passes**

Run: `dotnet test tests/rororo-ur-task.Tests/rororo-ur-task.Tests.csproj -p:StandaloneTestsOnly=true --filter FullyQualifiedName~ColorCheckTests`
Expected: all pass.

- [ ] **Step 5: Commit**

```bash
git add src/Macros/Steps/ColorCheck.cs tests/rororo-ur-task.Tests/Steps/ColorCheckTests.cs
git commit -m "feat(steps): colour checks with a two-state rule and readable colour names"
```

---

### Task 2: The v4 macro file

**Files:**
- Create: `src/Macros/Steps/MacroStep.cs`
- Modify: `src/Macros/Macro.cs`, `src/Macros/MacroV1Migrator.cs`, `src/Macros/MacroStore.cs`, `src/Macros/MacroBundle.cs`
- Modify (existing tests pinned to schema 3): `tests/rororo-ur-task.Tests/MacroV1MigrationTests.cs` (lines 16 and 44), `tests/rororo-ur-task.Tests/MacroV3MigrationTests.cs` (line 31)
- Test: `tests/rororo-ur-task.Tests/Steps/MacroV4FileTests.cs`

**Interfaces:**
- Consumes: `ColorCheck`, `CheckBox`, `Rgb` (Task 1).
- Produces: `abstract record MacroStep(int DelayMs)`; `KeyStep(int DelayMs, int VirtualKeyCode, bool Down)`; `PointStep(int DelayMs, string Id, string? Label, int X, int Y, int Button = 1, ColorCheck? Check = null, bool CheckEnabled = false)`; `DragStep(int DelayMs, int Button, int StartX, int StartY, int Dx, int Dy, int DurationMs)`; `WheelStep(int DelayMs, int X, int Y, int Delta)`; `PointerMoveStep(int DelayMs, int Dx, int Dy, int DurationMs)`; `WaitStep(int DelayMs)`; `enum NoMatchAction { Skip, StopAndReport }`, stored as `"skip"` / `"stopAndReport"` per the spec; `FirstMatchStep(int DelayMs, string Id, string? Label, IReadOnlyList<PointStep> Candidates, NoMatchAction OnNoMatch = NoMatchAction.StopAndReport)`; `RawStep(int DelayMs, IReadOnlyList<MacroEvent> Events, string Note)`; `StepValidator.Validate(IReadOnlyList<MacroStep>) : string?`; `StepTiming` constants `JumpWiggleMs = 150`, `PressHoldMs = 80`, `PollMs = 100`, `CheckGraceMs = 3000`, and `StepTiming.EstimateMs(MacroStep)`; `Macro.RecordedDisplayScale` (`int?`), `Macro.Steps` (`IReadOnlyList<MacroStep>?`), `Macro.HasSteps`, `Macro.CurrentSchemaVersion = 4`.

- [ ] **Step 1: Write the failing tests**

```csharp
// tests/rororo-ur-task.Tests/Steps/MacroV4FileTests.cs
using System.IO;
using Labs626.UrTask.Macros;
using Labs626.UrTask.Macros.Steps;

namespace Labs626.UrTask.Tests.Steps;

public class MacroV4FileTests : IDisposable
{
    private readonly string _dir = Path.Combine(Path.GetTempPath(), "urtask-v4-" + Guid.NewGuid().ToString("N"));
    public void Dispose() { try { Directory.Delete(_dir, true); } catch { } }

    private static Macro Sample() => new(
        SchemaVersion: Macro.CurrentSchemaVersion,
        Id: Guid.NewGuid().ToString(),
        Name: "v4 sample",
        RecordMode: "PerWindow",
        RecordedAgainstUserId: 1,
        RecordedAgainstDisplayName: "fixture",
        InterAltDelayMs: null,
        RecordedAtUnixMs: 1,
        Events: new[] { new MacroEvent(10, MacroEventKind.MouseDown, 0, 5, 6, 1, 0), new MacroEvent(90, MacroEventKind.MouseUp, 0, 5, 6, 1, 0) },
        CoordSpace: Macro.CoordSpaceClient,
        RecordedClientW: 800,
        RecordedClientH: 599,
        RecordedDisplayScale: 100,
        Steps: new MacroStep[]
        {
            new KeyStep(0, 0x41, true),
            new PointStep(1500, "p1", "Teleport opener", 42, 398,
                Check: new ColorCheck(new CheckBox(), new Rgb(139, 224, 58), new Rgb(128, 128, 128)), CheckEnabled: true),
            new FirstMatchStep(400, "f1", "Best mine", new[]
            {
                new PointStep(0, "f1a", "#8", 137, 390, Check: new ColorCheck(new CheckBox(), new Rgb(139, 224, 58), new Rgb(150, 150, 160))),
                new PointStep(0, "f1b", "#7", 312, 390, Check: new ColorCheck(new CheckBox(), new Rgb(139, 224, 58), new Rgb(150, 150, 160))),
            }, NoMatchAction.Skip),
            new DragStep(0, 2, 400, 300, 0, 300, 400),
            new WheelStep(0, 400, 300, -120),
            new PointerMoveStep(0, 0, 200, 300),
            new WaitStep(250),
            new RawStep(0, new[] { new MacroEvent(0, MacroEventKind.MouseDown, 0, 1, 1, 1, 0) }, "test"),
        });

    [Fact]
    public void Round_trips_every_step_kind_through_the_store()
    {
        var store = new MacroStore(_dir);
        var m = Sample();
        store.Save(m);
        var loaded = Assert.Single(store.LoadAll().Macros);
        Assert.Equal(4, loaded.SchemaVersion);
        Assert.Equal(100, loaded.RecordedDisplayScale);
        Assert.Equal(m.Steps!.Count, loaded.Steps!.Count);
        var p = Assert.IsType<PointStep>(loaded.Steps[1]);
        Assert.Equal("Teleport opener", p.Label);
        Assert.True(p.CheckEnabled);
        Assert.Equal(new Rgb(128, 128, 128), p.Check!.Other);
        var fm = Assert.IsType<FirstMatchStep>(loaded.Steps[2]);
        Assert.Equal(NoMatchAction.Skip, fm.OnNoMatch);
        Assert.Equal(2, fm.Candidates.Count);
        Assert.IsType<RawStep>(loaded.Steps[^1]);
    }

    [Fact]
    public void V4_file_still_carries_the_original_events()
    {
        var store = new MacroStore(_dir);
        var m = Sample();
        store.Save(m);
        var json = File.ReadAllText(Path.Combine(_dir, m.Id + ".json"));
        Assert.Contains("\"events\"", json);
        Assert.Contains("\"steps\"", json);
        Assert.Contains("\"kind\": \"point\"", json);
        Assert.Contains("\"onNoMatch\": \"skip\"", json); // spec casing, not the C# member name
    }

    [Fact]
    public void A_check_without_a_box_loads_and_is_refused_with_a_sentence()
    {
        Directory.CreateDirectory(_dir);
        var id = Guid.NewGuid().ToString();
        File.WriteAllText(Path.Combine(_dir, id + ".json"), $$"""
        { "schemaVersion": 4, "id": "{{id}}", "recordedAtUnixMs": 1, "events": [],
          "steps": [ { "kind": "point", "delayMs": 0, "id": "p1", "x": 10, "y": 20, "checkEnabled": true,
                       "check": { "expect": { "r": 1, "g": 2, "b": 3 } } } ] }
        """);
        var m = Assert.Single(new MacroStore(_dir).LoadAll().Macros);
        Assert.Equal("Step 1 'p1' has a check with no box.", StepValidator.Validate(m.Steps!));
    }

    [Fact]
    public void A_check_without_an_expected_colour_is_a_listed_failure()
    {
        Directory.CreateDirectory(_dir);
        var id = Guid.NewGuid().ToString();
        File.WriteAllText(Path.Combine(_dir, id + ".json"), $$"""
        { "schemaVersion": 4, "id": "{{id}}", "recordedAtUnixMs": 1, "events": [],
          "steps": [ { "kind": "point", "delayMs": 0, "id": "p1", "x": 10, "y": 20,
                       "check": { "box": { "offsetX": -2, "offsetY": -2, "w": 5, "h": 5 } } } ] }
        """);
        var result = new MacroStore(_dir).LoadAll();
        Assert.Empty(result.Macros);
        Assert.Contains(result.Failures, f => f.Path.EndsWith(id + ".json") && f.Reason.Contains("expect"));
    }

    [Fact]
    public void V3_file_loads_with_no_steps()
    {
        var store = new MacroStore(_dir);
        var v3 = Sample() with { Steps = null, RecordedDisplayScale = null };
        store.Save(v3);
        var loaded = Assert.Single(store.LoadAll().Macros);
        Assert.False(loaded.HasSteps);
    }

    [Fact]
    public void Kind_does_not_have_to_come_first()
    {
        Directory.CreateDirectory(_dir);
        var id = Guid.NewGuid().ToString();
        File.WriteAllText(Path.Combine(_dir, id + ".json"), $$"""
        { "schemaVersion": 4, "id": "{{id}}", "name": "agent written", "recordedAtUnixMs": 1,
          "events": [], "coordSpace": "client", "recordedClientW": 800, "recordedClientH": 599,
          "steps": [ { "delayMs": 0, "id": "p1", "x": 10, "y": 20, "kind": "point" } ] }
        """);
        var result = new MacroStore(_dir).LoadAll();
        Assert.Empty(result.Failures);
        Assert.IsType<PointStep>(Assert.Single(result.Macros).Steps![0]);
    }

    [Fact]
    public void Unknown_kind_is_a_listed_failure_not_a_crash()
    {
        var store = new MacroStore(_dir);
        store.Save(Sample());
        var bad = Guid.NewGuid().ToString();
        File.WriteAllText(Path.Combine(_dir, bad + ".json"), $$"""
        { "schemaVersion": 4, "id": "{{bad}}", "recordedAtUnixMs": 1, "events": [],
          "steps": [ { "kind": "teleport", "delayMs": 0 } ] }
        """);
        var result = store.LoadAll();
        Assert.Single(result.Macros);
        Assert.Contains(result.Failures, f => f.Path.EndsWith(bad + ".json"));
    }

    [Fact]
    public void Negative_delays_are_clamped_on_load()
    {
        Directory.CreateDirectory(_dir);
        var id = Guid.NewGuid().ToString();
        File.WriteAllText(Path.Combine(_dir, id + ".json"), $$"""
        { "schemaVersion": 4, "id": "{{id}}", "recordedAtUnixMs": 1, "events": [],
          "steps": [ { "kind": "wait", "delayMs": -500 } ] }
        """);
        var m = Assert.Single(new MacroStore(_dir).LoadAll().Macros);
        Assert.Equal(0, m.Steps![0].DelayMs);
    }

    [Fact]
    public void Bundle_import_keeps_steps()
    {
        var json = MacroBundle.Serialize(new[] { Sample() }, 1);
        var parsed = Assert.Single(MacroBundle.Parse(json).Macros);
        var imported = MacroBundle.PrepareForImport(parsed, new HashSet<string>(StringComparer.OrdinalIgnoreCase));
        Assert.True(imported.HasSteps);
        Assert.Equal(Macro.CurrentSchemaVersion, imported.SchemaVersion);
    }

    [Fact]
    public void Validator_accepts_a_good_list() => Assert.Null(StepValidator.Validate(Sample().Steps!));

    [Fact]
    public void Validator_refuses_first_match_without_candidates()
    {
        var err = StepValidator.Validate(new MacroStep[] { new FirstMatchStep(0, "f1", "Best mine", Array.Empty<PointStep>()) });
        Assert.Equal("Step 1 'Best mine' is a first match with no candidates.", err);
    }

    [Fact]
    public void Validator_refuses_a_candidate_without_a_check()
    {
        var err = StepValidator.Validate(new MacroStep[]
        {
            new FirstMatchStep(0, "f1", "Best mine", new[] { new PointStep(0, "f1a", "#8", 1, 1) }),
        });
        Assert.Equal("Step 1 'Best mine': candidate '#8' has no colour to check.", err);
    }

    [Fact]
    public void Validator_refuses_an_enabled_check_with_no_sample()
    {
        var err = StepValidator.Validate(new MacroStep[] { new PointStep(0, "p1", null, 1, 1, CheckEnabled: true) });
        Assert.Equal("Step 1 'p1' has its check on but no colour sample.", err);
    }

    [Fact]
    public void Validator_refuses_duplicate_point_ids()
    {
        var err = StepValidator.Validate(new MacroStep[] { new PointStep(0, "p1", null, 1, 1), new PointStep(0, "p1", null, 2, 2) });
        Assert.Equal("Step 2 reuses point id 'p1'.", err);
    }

    [Fact]
    public void Validator_refuses_an_oversized_box()
    {
        var err = StepValidator.Validate(new MacroStep[]
        {
            new PointStep(0, "p1", "Big", 1, 1, Check: new ColorCheck(new CheckBox(0, 0, 12, 12), new Rgb(0, 0, 0)), CheckEnabled: true),
        });
        Assert.Equal("Step 1 'Big' has a check box larger than 9x9.", err);
    }

    [Fact]
    public void Validator_refuses_an_empty_box()
    {
        var err = StepValidator.Validate(new MacroStep[]
        {
            new PointStep(0, "p1", "Flat", 1, 1, Check: new ColorCheck(new CheckBox(0, 0, 0, 5), new Rgb(0, 0, 0)), CheckEnabled: true),
        });
        Assert.Equal("Step 1 'Flat' has an empty check box.", err);
    }

    [Fact]
    public void Validator_refuses_a_point_with_no_id()
    {
        Assert.Equal("Step 1 has no point id.", StepValidator.Validate(new MacroStep[] { new PointStep(0, null!, null, 1, 1) }));
        Assert.Equal("Step 1 'Best mine': a candidate has no point id.", StepValidator.Validate(new MacroStep[]
        {
            new FirstMatchStep(0, "f1", "Best mine", new[] { new PointStep(0, " ", "#8", 1, 1, Check: new ColorCheck(new CheckBox(), new Rgb(0, 0, 0))) }),
        }));
    }

    [Fact]
    public void Validator_refuses_a_candidate_check_with_no_box()
    {
        var err = StepValidator.Validate(new MacroStep[]
        {
            new FirstMatchStep(0, "f1", "Best mine", new[] { new PointStep(0, "f1a", "#8", 1, 1, Check: new ColorCheck(null!, new Rgb(0, 0, 0))) }),
        });
        Assert.Equal("Step 1 'Best mine': candidate '#8' has a check with no box.", err);
    }
}
```

- [ ] **Step 2: Run to verify it fails**

Run: `dotnet test tests/rororo-ur-task.Tests/rororo-ur-task.Tests.csproj -p:StandaloneTestsOnly=true --filter FullyQualifiedName~MacroV4FileTests`
Expected: build errors (step types, `Macro.Steps` missing).

- [ ] **Step 3: Implement the step records**

```csharp
// src/Macros/Steps/MacroStep.cs
using System.Text.Json.Serialization;

namespace Labs626.UrTask.Macros.Steps;

/// <summary>One step of a v4 macro. <see cref="DelayMs"/> is the wait before the step; on a
/// point whose check is on, it becomes a ceiling rather than a wait (see StepRunner).</summary>
[JsonPolymorphic(TypeDiscriminatorPropertyName = "kind")]
[JsonDerivedType(typeof(KeyStep), "key")]
[JsonDerivedType(typeof(PointStep), "point")]
[JsonDerivedType(typeof(DragStep), "drag")]
[JsonDerivedType(typeof(WheelStep), "wheel")]
[JsonDerivedType(typeof(PointerMoveStep), "pointerMove")]
[JsonDerivedType(typeof(WaitStep), "wait")]
[JsonDerivedType(typeof(FirstMatchStep), "firstMatch")]
[JsonDerivedType(typeof(RawStep), "raw")]
public abstract record MacroStep(int DelayMs);

public sealed record KeyStep(int DelayMs, int VirtualKeyCode, bool Down) : MacroStep(DelayMs);

/// <summary>A click at a client-space point. Id is short and stable (not a list position) so
/// adjustments and candidates survive steps being added or reordered.</summary>
public sealed record PointStep(
    int DelayMs, string Id, string? Label, int X, int Y, int Button = 1,
    ColorCheck? Check = null, bool CheckEnabled = false) : MacroStep(DelayMs);

/// <summary>Button held while the mouse moves by (Dx, Dy) from the start point.</summary>
public sealed record DragStep(int DelayMs, int Button, int StartX, int StartY, int Dx, int Dy, int DurationMs) : MacroStep(DelayMs);

public sealed record WheelStep(int DelayMs, int X, int Y, int Delta) : MacroStep(DelayMs);

/// <summary>Relative movement while Roblox holds the pointer (first person, shift-lock).</summary>
public sealed record PointerMoveStep(int DelayMs, int Dx, int Dy, int DurationMs) : MacroStep(DelayMs);

public sealed record WaitStep(int DelayMs) : MacroStep(DelayMs);

/// <summary>Stored as the spec spells it ("skip", "stopAndReport"). The store's global
/// JsonStringEnumConverter honours these member names.</summary>
public enum NoMatchAction
{
    [JsonStringEnumMemberName("skip")] Skip,
    [JsonStringEnumMemberName("stopAndReport")] StopAndReport,
}

/// <summary>Candidates are checked in order; the first that matches is pressed.</summary>
public sealed record FirstMatchStep(
    int DelayMs, string Id, string? Label, IReadOnlyList<PointStep> Candidates,
    NoMatchAction OnNoMatch = NoMatchAction.StopAndReport) : MacroStep(DelayMs);

/// <summary>A stretch the converter could not understand, replayed as recorded. Event
/// timestamps are relative to the start of the step.</summary>
public sealed record RawStep(int DelayMs, IReadOnlyList<MacroEvent> Events, string Note) : MacroStep(DelayMs);

public static class StepTiming
{
    public const int JumpWiggleMs = 150;
    public const int PressHoldMs = 80;
    public const int PollMs = 100;
    public const int CheckGraceMs = 3000;

    /// <summary>Rough playing time of a step, for Macro.Duration and UI only.</summary>
    public static long EstimateMs(MacroStep s) => s.DelayMs + s switch
    {
        PointStep => JumpWiggleMs + PressHoldMs,
        FirstMatchStep => JumpWiggleMs + PressHoldMs,
        DragStep d => JumpWiggleMs + d.DurationMs,
        WheelStep => JumpWiggleMs,
        PointerMoveStep p => p.DurationMs,
        RawStep r => r.Events.Count == 0 ? 0 : r.Events[^1].TimestampMs,
        _ => 0,
    };
}

public static class StepValidator
{
    /// <summary>Null when the list can play; otherwise one sentence naming the first problem.
    /// Runs before any input, and null-guards what hand- or agent-written JSON can leave out
    /// (point id, check box), so a bad file ends in a sentence, never an exception.</summary>
    public static string? Validate(IReadOnlyList<MacroStep> steps)
    {
        var ids = new HashSet<string>(StringComparer.Ordinal);
        for (int i = 0; i < steps.Count; i++)
        {
            var n = i + 1;
            switch (steps[i])
            {
                case PointStep p:
                {
                    if (string.IsNullOrWhiteSpace(p.Id)) return $"Step {n} has no point id.";
                    if (!ids.Add(p.Id)) return $"Step {n} reuses point id '{p.Id}'.";
                    var name = $"Step {n} '{p.Label ?? p.Id}'";
                    if (p.CheckEnabled && p.Check is null) return $"{name} has its check on but no colour sample.";
                    if (p.Check is { } pc && BoxProblem(pc, name) is { } err) return err;
                    break;
                }
                case FirstMatchStep f:
                {
                    if (string.IsNullOrWhiteSpace(f.Id)) return $"Step {n} has no point id.";
                    var name = $"Step {n} '{f.Label ?? f.Id}'";
                    if (!ids.Add(f.Id)) return $"Step {n} reuses point id '{f.Id}'.";
                    if (f.Candidates is null || f.Candidates.Count == 0) return $"{name} is a first match with no candidates.";
                    foreach (var c in f.Candidates)
                    {
                        if (string.IsNullOrWhiteSpace(c.Id)) return $"{name}: a candidate has no point id.";
                        if (!ids.Add(c.Id)) return $"{name} reuses point id '{c.Id}'.";
                        if (c.Check is null) return $"{name}: candidate '{c.Label ?? c.Id}' has no colour to check.";
                        if (BoxProblem(c.Check, $"{name}: candidate '{c.Label ?? c.Id}'") is { } err) return err;
                    }
                    break;
                }
            }
        }
        return null;
    }

    /// <summary>A missing box (null from JSON), an empty one, and an oversized one each get
    /// their own sentence.</summary>
    private static string? BoxProblem(ColorCheck check, string who)
    {
        if (check.Box is null) return $"{who} has a check with no box.";
        if (check.Box.W < 1 || check.Box.H < 1) return $"{who} has an empty check box.";
        if (check.Box.W > CheckBox.MaxSide || check.Box.H > CheckBox.MaxSide) return $"{who} has a check box larger than 9x9.";
        return null;
    }
}
```

- [ ] **Step 4: Extend `Macro`**

In `src/Macros/Macro.cs`: add `using Labs626.UrTask.Macros.Steps;` at the top. Append two parameters after `bool AllGames = false`:

```csharp
    bool AllGames = false,              // user override: usable everywhere regardless of the stamp
    [property: JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    int? RecordedDisplayScale = null,   // percent (100, 125…) at record time; v4. Null = unknown (pre-v4).
    [property: JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    IReadOnlyList<MacroStep>? Steps = null) // v4 point steps; when present these play, Events is the kept original
```

Change `CurrentSchemaVersion` to `4` and update its doc comment ("v4 adds point steps and the recorded display scale; a macro without steps plays exactly as v3"). Add below `IsGameScoped`:

```csharp
    /// <summary>True when this macro plays through the step runner (v4 point steps).</summary>
    public bool HasSteps => Steps is { Count: > 0 };
```

Replace `Duration` with:

```csharp
    public TimeSpan Duration => HasSteps
        ? TimeSpan.FromMilliseconds(Steps!.Sum(StepTiming.EstimateMs))
        : Events.Count == 0 ? TimeSpan.Zero : TimeSpan.FromMilliseconds(Events[^1].TimestampMs);
```

- [ ] **Step 5: Allow `kind` anywhere and clamp step delays**

In each of `MacroStore.JsonOptions`, `MacroV1Migrator.JsonOptions` and `MacroBundle.JsonOptions`, add `AllowOutOfOrderMetadataProperties = true,` to the initializer (agent-written JSON may not put `kind` first).

In `MacroV1Migrator.LoadAndMigrate`, inside the `schemaVersion >= 2` branch, extend the `with` block:

```csharp
                Events = SanitizeTimestamps(m.Events ?? []),
                Steps = m.Steps is null ? null : SanitizeSteps(m.Steps),
```

and add beside `SanitizeTimestamps`:

```csharp
    /// <summary>Same defence as SanitizeTimestamps for v4 steps: a hand-edited negative delay
    /// becomes 0 rather than reaching Task.Delay.</summary>
    private static IReadOnlyList<Steps.MacroStep> SanitizeSteps(IReadOnlyList<Steps.MacroStep> steps)
        => steps.Select(s => s.DelayMs < 0 ? s with { DelayMs = 0 } : s).ToList();
```

Update the class doc comment's "returns a v3 Macro" to "returns a current-schema Macro".

- [ ] **Step 6: Run to verify it passes, then the full suite**

Run: `dotnet test tests/rororo-ur-task.Tests/rororo-ur-task.Tests.csproj -p:StandaloneTestsOnly=true`
Before running, update the three existing asserts that pin schema 3. They go red the moment `CurrentSchemaVersion` becomes 4:

- `tests/rororo-ur-task.Tests/MacroV1MigrationTests.cs` line 16: `Assert.Equal(3, macro.SchemaVersion);` becomes `Assert.Equal(Macro.CurrentSchemaVersion, macro.SchemaVersion);`
- same file, line 44: `Assert.Equal(3, result.SchemaVersion);` becomes `Assert.Equal(Macro.CurrentSchemaVersion, result.SchemaVersion);`
- `tests/rororo-ur-task.Tests/MacroV3MigrationTests.cs` line 31: `Assert.Equal(3, macro.SchemaVersion);` becomes `Assert.Equal(Macro.CurrentSchemaVersion, macro.SchemaVersion);`

Change nothing else in those files, and no behaviour assertion.

Expected: the new tests and every pre-existing test pass, except the 2 known environmental `HotkeyServiceTests` failures (Win32 1409, a live Ur Task holds Ctrl+Shift+R). The gate is no new failures.

- [ ] **Step 7: Commit**

```bash
git add src/Macros/Steps/MacroStep.cs src/Macros/Macro.cs src/Macros/MacroV1Migrator.cs src/Macros/MacroStore.cs src/Macros/MacroBundle.cs tests/rororo-ur-task.Tests/Steps/MacroV4FileTests.cs tests/rororo-ur-task.Tests/MacroV1MigrationTests.cs tests/rororo-ur-task.Tests/MacroV3MigrationTests.cs
git commit -m "feat(macros): schema v4 with point steps beside the kept recording"
```

---

### Task 3: Converting a recording to steps

**Files:**
- Create: `src/Macros/Steps/StepConverter.cs`
- Create: `tests/fixtures/mine-zone-8.json`, `tests/fixtures/mining-z8-egg.json` (the repo's fixture folder, beside `macro-v1.json`)
- Modify: `tests/rororo-ur-task.Tests/rororo-ur-task.Tests.csproj`
- Test: `tests/rororo-ur-task.Tests/Steps/StepConverterTests.cs`

**Interfaces:**
- Consumes: step records (Task 2), `MacroEvent`.
- Produces: `StepConverter.Convert(IReadOnlyList<MacroEvent> events) : IReadOnlyList<MacroStep>`; constants `SameSpotPx = 8`, `TravelGapMs = 150`, `CameraDragMargin = 1.5`.

- [ ] **Step 1: Add the fixtures, anonymised**

Copy the two real recordings from `%LOCALAPPDATA%\626Labs\RoRoRoUrTask\macros\` (`82791a3c-ac2c-4eb2-bdc8-6e70fc2ed810.json` is "Mine Zone 8", `b7bd7845-9cfb-4d60-81cd-0b6b6b46d83e.json` is "Mining Z8 Egg"). This repo is public, so replace the account fields before committing:

```powershell
$src = "$env:LOCALAPPDATA\626Labs\RoRoRoUrTask\macros"
$dst = "tests\fixtures"
New-Item -ItemType Directory -Force $dst | Out-Null
foreach ($pair in @(@('82791a3c-ac2c-4eb2-bdc8-6e70fc2ed810','mine-zone-8'), @('b7bd7845-9cfb-4d60-81cd-0b6b6b46d83e','mining-z8-egg'))) {
  $j = Get-Content "$src\$($pair[0]).json" -Raw | ConvertFrom-Json
  $j.recordedAgainstUserId = 1
  $j.recordedAgainstDisplayName = "fixture-main"
  $j | ConvertTo-Json -Depth 6 | Set-Content -Encoding utf8 "$dst\$($pair[1]).json"
}
```

If the macros are missing on this PC, stop and ask Este to copy them over. Do not invent fixtures: the travel numbers below were measured on the real files.

In the test csproj, add these two items to the existing `<ItemGroup>` that already links `..\fixtures\macro-v1.json`, following its pattern:

```xml
    <None Include="..\fixtures\mine-zone-8.json">
      <Link>fixtures\mine-zone-8.json</Link>
      <CopyToOutputDirectory>PreserveNewest</CopyToOutputDirectory>
    </None>
    <None Include="..\fixtures\mining-z8-egg.json">
      <Link>fixtures\mining-z8-egg.json</Link>
      <CopyToOutputDirectory>PreserveNewest</CopyToOutputDirectory>
    </None>
```

- [ ] **Step 2: Write the failing tests**

```csharp
// tests/rororo-ur-task.Tests/Steps/StepConverterTests.cs
using System.IO;
using Labs626.UrTask.Macros;
using Labs626.UrTask.Macros.Steps;

namespace Labs626.UrTask.Tests.Steps;

public class StepConverterTests
{
    private static Macro Fixture(string name)
        => MacroV1Migrator.LoadAndMigrate(File.ReadAllText(Path.Combine(AppContext.BaseDirectory, "fixtures", name)));

    private static MacroEvent Ev(long t, MacroEventKind k, int x = 0, int y = 0, int vk = 0, int btn = 0, int wheel = 0)
        => new(t, k, vk, x, y, btn, wheel);

    [Fact]
    public void Mine_zone_8_collapses_to_five_points()
    {
        var steps = StepConverter.Convert(Fixture("mine-zone-8.json").Events);
        var points = steps.OfType<PointStep>().ToList();
        Assert.Equal(steps.Count, points.Count); // orphan key-ups dropped, modifier tail trimmed
        Assert.Equal(new[] { (472, 317), (42, 398), (100, 172), (134, 373), (302, 430) }, points.Select(p => (p.X, p.Y)));
        Assert.Equal(new[] { "p1", "p2", "p3", "p4", "p5" }, points.Select(p => p.Id));
    }

    [Fact]
    public void Mine_zone_8_keeps_still_time_and_drops_travel()
    {
        // Measured 2026-09-27: 19.4 s between clicks, 5.7 s of it hand travel.
        var points = StepConverter.Convert(Fixture("mine-zone-8.json").Events).OfType<PointStep>().ToList();
        var expected = new[] { 1500, 400, 4800, 1900, 5200 };
        for (int i = 0; i < expected.Length; i++)
            Assert.InRange(points[i].DelayMs, expected[i] - 250, expected[i] + 250);
        Assert.InRange(points.Sum(p => p.DelayMs), 13_300, 14_300);
    }

    [Fact]
    public void Egg_macro_collapses_auto_repeat_into_single_key_downs()
    {
        var steps = StepConverter.Convert(Fixture("mining-z8-egg.json").Events);
        // 8 clicks. One moves 6 px between down (578,420) and up (581,426). At the old 4 px
        // threshold it became a drag, which is why the threshold is 8 px.
        Assert.Equal(8, steps.OfType<PointStep>().Count());
        Assert.Empty(steps.OfType<DragStep>());
        Assert.Contains(steps.OfType<PointStep>(), p => (p.X, p.Y) == (578, 420));
        Assert.IsType<PointStep>(steps[0]);
        Assert.Equal(new KeyStep(steps[1].DelayMs, 0x41, true), steps[1]); // A pressed right after the wake-up click
        Assert.Equal(1, steps.OfType<KeyStep>().Count(k => k.VirtualKeyCode == 0x57 && k.Down)); // W: 142 recorded downs, one press
        Assert.Equal(1, steps.OfType<KeyStep>().Count(k => k.VirtualKeyCode == 0x45 && k.Down)); // E

        // The property that matters, whatever the fixture's key-ups look like:
        // no key is ever pressed again while it is still held.
        var held = new HashSet<int>();
        foreach (var k in steps.OfType<KeyStep>())
        {
            if (k.Down) Assert.True(held.Add(k.VirtualKeyCode), $"vk {k.VirtualKeyCode:X2} pressed while held");
            else held.Remove(k.VirtualKeyCode);
        }
    }

    [Fact]
    public void Egg_macro_drops_the_stop_hotkey_tail()
    {
        // The recording ends LCtrl down, LShift down, LShift up, LShift down (the stop hotkey).
        // Every modifier down with no later up goes, so Ctrl can never be left held. The
        // Shift down/up pair is a complete press and stays.
        var steps = StepConverter.Convert(Fixture("mining-z8-egg.json").Events);
        var keys = steps.OfType<KeyStep>().ToList();
        Assert.DoesNotContain(keys, k => k.VirtualKeyCode == 0xA2); // LCtrl, the key the old trim left held
        for (int i = 0; i < keys.Count; i++)
        {
            var k = keys[i];
            if (k.Down && k.VirtualKeyCode is 0x10 or 0x11 or 0x12 or 0xA0 or 0xA1 or 0xA2 or 0xA3 or 0xA4 or 0xA5)
                Assert.Contains(keys.Skip(i + 1), u => !u.Down && u.VirtualKeyCode == k.VirtualKeyCode);
        }
    }

    [Fact]
    public void Mine_zone_8_drops_its_stop_hotkey_ctrl()
        => Assert.DoesNotContain(StepConverter.Convert(Fixture("mine-zone-8.json").Events).OfType<KeyStep>(), k => k.VirtualKeyCode == 0xA2);

    [Fact]
    public void A_key_keeps_its_real_held_time_while_the_mouse_moves()
    {
        // W held 1100 ms while the hand moves the mouse for 900 ms of it. Travel is dropped only
        // before clicks; a key's up delay is its real held time.
        var evs = new List<MacroEvent> { Ev(0, MacroEventKind.KeyDown, vk: 0x57) };
        for (int i = 0; i < 19; i++) evs.Add(Ev(100 + i * 50, MacroEventKind.MouseMove, i * 10, 0));
        evs.Add(Ev(1100, MacroEventKind.KeyUp, vk: 0x57));
        var up = Assert.IsType<KeyStep>(StepConverter.Convert(evs)[1]);
        Assert.False(up.Down);
        Assert.Equal(1100, up.DelayMs);
    }

    [Fact]
    public void Down_and_up_within_eight_pixels_is_a_point()
    {
        // 7 px and 6 px of hand jitter: still one click (the real egg recording has a 6 px click).
        var steps = StepConverter.Convert(new[] { Ev(100, MacroEventKind.MouseDown, 50, 50, btn: 1), Ev(180, MacroEventKind.MouseUp, 57, 56, btn: 1) });
        var p = Assert.IsType<PointStep>(Assert.Single(steps));
        Assert.Equal((50, 50), (p.X, p.Y));
        Assert.Equal(100, p.DelayMs);
    }

    [Fact]
    public void Right_drag_gets_the_camera_margin()
    {
        var steps = StepConverter.Convert(new[] { Ev(0, MacroEventKind.MouseDown, 400, 300, btn: 2), Ev(400, MacroEventKind.MouseUp, 400, 500, btn: 2) });
        var d = Assert.IsType<DragStep>(Assert.Single(steps));
        Assert.Equal((2, 400, 300, 0, 300, 400), (d.Button, d.StartX, d.StartY, d.Dx, d.Dy, d.DurationMs));
    }

    [Fact]
    public void Left_drag_keeps_its_exact_distance()
    {
        var steps = StepConverter.Convert(new[] { Ev(0, MacroEventKind.MouseDown, 100, 100, btn: 1), Ev(300, MacroEventKind.MouseUp, 180, 100, btn: 1) });
        var d = Assert.IsType<DragStep>(Assert.Single(steps));
        Assert.Equal((80, 0), (d.Dx, d.Dy));
    }

    [Fact]
    public void Wheel_becomes_a_wheel_step()
    {
        var w = Assert.IsType<WheelStep>(Assert.Single(StepConverter.Convert(new[] { Ev(50, MacroEventKind.MouseWheel, 10, 20, wheel: -120) })));
        Assert.Equal((50, 10, 20, -120), (w.DelayMs, w.X, w.Y, w.Delta));
    }

    [Fact]
    public void A_button_never_released_stays_raw()
    {
        var steps = StepConverter.Convert(new[] { Ev(0, MacroEventKind.MouseDown, 10, 10, btn: 1), Ev(50, MacroEventKind.MouseMove, 20, 20) });
        var raw = Assert.IsType<RawStep>(Assert.Single(steps));
        Assert.Equal("A mouse button was pressed and never released.", raw.Note);
    }

    [Fact]
    public void Two_buttons_held_at_once_stay_raw()
    {
        var steps = StepConverter.Convert(new[]
        {
            Ev(0, MacroEventKind.MouseDown, 10, 10, btn: 1),
            Ev(50, MacroEventKind.MouseDown, 10, 10, btn: 2),
            Ev(90, MacroEventKind.MouseUp, 10, 10, btn: 2),
            Ev(120, MacroEventKind.MouseUp, 10, 10, btn: 1),
            Ev(500, MacroEventKind.MouseDown, 30, 30, btn: 1),
            Ev(560, MacroEventKind.MouseUp, 30, 30, btn: 1),
        });
        var raw = Assert.IsType<RawStep>(steps[0]);
        Assert.Equal(4, raw.Events.Count);
        Assert.Equal(0, raw.Events[0].TimestampMs);
        Assert.Equal("Two mouse buttons were held at once.", raw.Note);
        Assert.IsType<PointStep>(steps[1]);
    }

    [Fact]
    public void Travel_is_only_counted_while_the_mouse_moves()
    {
        // 300 ms of motion in 50 ms hops, then a 1 s still, then the click.
        var evs = new List<MacroEvent>();
        for (int i = 0; i <= 6; i++) evs.Add(Ev(i * 50, MacroEventKind.MouseMove, i * 10, 0));
        evs.Add(Ev(1300, MacroEventKind.MouseDown, 60, 0, btn: 1));
        evs.Add(Ev(1380, MacroEventKind.MouseUp, 60, 0, btn: 1));
        var p = Assert.IsType<PointStep>(Assert.Single(StepConverter.Convert(evs)));
        Assert.InRange(p.DelayMs, 950, 1050);
    }
}
```

- [ ] **Step 3: Run to verify it fails**

Run: `dotnet test tests/rororo-ur-task.Tests/rororo-ur-task.Tests.csproj -p:StandaloneTestsOnly=true --filter FullyQualifiedName~StepConverterTests`
Expected: build error, `StepConverter` missing.

- [ ] **Step 4: Implement**

```csharp
// src/Macros/Steps/StepConverter.cs
namespace Labs626.UrTask.Macros.Steps;

/// <summary>
/// Collapses a recording into steps. The path between clicks is dropped; a click's delay is
/// the time the mouse sat still before it (time spent moving is hand travel, replaced at
/// playback by a fixed jump and wiggle). Drags, the wheel and keys keep their meaning.
/// </summary>
public static class StepConverter
{
    public const int SameSpotPx = 8;   // spec §2, amended from 4: the real egg recording has a 6 px click
    public const int TravelGapMs = 150;
    public const double CameraDragMargin = 1.5;

    private static readonly HashSet<int> Modifiers = new() { 0x10, 0x11, 0x12, 0xA0, 0xA1, 0xA2, 0xA3, 0xA4, 0xA5 };

    public static IReadOnlyList<MacroStep> Convert(IReadOnlyList<MacroEvent> events)
    {
        var steps = new List<MacroStep>();
        var heldKeys = new HashSet<int>();
        long anchorMs = 0;        // delays are measured from here
        long travelMs = 0;        // motion since the anchor
        (int X, int Y)? lastMove = null;
        long lastMoveMs = 0;
        int nextId = 1;

        MacroEvent? down = null;  // the pending press
        int downIndex = -1;
        long downDelay = 0;

        List<MacroEvent>? raw = null; // collecting a raw stretch
        HashSet<int>? rawButtons = null;
        long rawDelay = 0;
        string rawNote = "";

        // Travel is dropped only from the gap before a mouse press or wheel (the hand moving to
        // the spot). Keys keep their real timing, so a key's up delay is its real held time.
        int Delay(long at, bool dropTravel = true) => (int)Math.Clamp(at - anchorMs - (dropTravel ? travelMs : 0), 0, int.MaxValue);
        void Anchor(long at) { anchorMs = at; travelMs = 0; }

        for (int i = 0; i < events.Count; i++)
        {
            var e = events[i];

            if (raw is not null)
            {
                raw.Add(e);
                if (e.Kind == MacroEventKind.MouseDown) rawButtons!.Add(e.MouseButton);
                if (e.Kind == MacroEventKind.MouseUp) rawButtons!.Remove(e.MouseButton);
                if (rawButtons!.Count == 0)
                {
                    steps.Add(Raw(rawDelay, raw, rawNote));
                    Anchor(e.TimestampMs);
                    raw = null; rawButtons = null;
                }
                continue;
            }

            switch (e.Kind)
            {
                case MacroEventKind.MouseMove:
                    if (down is null && lastMove is { } lm && (lm.X != e.X || lm.Y != e.Y)
                        && e.TimestampMs - lastMoveMs <= TravelGapMs && lastMoveMs >= anchorMs)
                        travelMs += e.TimestampMs - lastMoveMs;
                    lastMove = (e.X, e.Y);
                    lastMoveMs = e.TimestampMs;
                    break;

                case MacroEventKind.KeyDown:
                    if (!heldKeys.Add(e.VirtualKeyCode)) break; // auto-repeat
                    steps.Add(new KeyStep(Delay(e.TimestampMs, dropTravel: false), e.VirtualKeyCode, true));
                    Anchor(e.TimestampMs);
                    break;

                case MacroEventKind.KeyUp:
                    if (!heldKeys.Remove(e.VirtualKeyCode)) break; // orphan (e.g. the record hotkey's release)
                    steps.Add(new KeyStep(Delay(e.TimestampMs, dropTravel: false), e.VirtualKeyCode, false));
                    Anchor(e.TimestampMs);
                    break;

                case MacroEventKind.MouseWheel:
                    steps.Add(new WheelStep(Delay(e.TimestampMs), e.X, e.Y, e.WheelDelta));
                    Anchor(e.TimestampMs);
                    break;

                case MacroEventKind.MouseDown:
                    if (down is not null)
                    {
                        raw = new List<MacroEvent>();
                        for (int j = downIndex; j <= i; j++) raw.Add(events[j]);
                        rawButtons = new HashSet<int> { down.MouseButton, e.MouseButton };
                        rawDelay = downDelay;
                        rawNote = "Two mouse buttons were held at once.";
                        down = null;
                        break;
                    }
                    down = e;
                    downIndex = i;
                    downDelay = Delay(e.TimestampMs);
                    break;

                case MacroEventKind.MouseUp:
                    if (down is null || down.MouseButton != e.MouseButton) break; // orphan release
                    var dx = e.X - down.X;
                    var dy = e.Y - down.Y;
                    if (Math.Abs(dx) <= SameSpotPx && Math.Abs(dy) <= SameSpotPx)
                    {
                        steps.Add(new PointStep((int)downDelay, $"p{nextId++}", null, down.X, down.Y, down.MouseButton));
                    }
                    else
                    {
                        var m = down.MouseButton == 2 ? CameraDragMargin : 1.0;
                        steps.Add(new DragStep((int)downDelay, down.MouseButton, down.X, down.Y,
                            (int)Math.Round(dx * m), (int)Math.Round(dy * m), (int)(e.TimestampMs - down.TimestampMs)));
                    }
                    Anchor(e.TimestampMs);
                    down = null;
                    break;
            }
        }

        if (raw is not null) steps.Add(Raw(rawDelay, raw, rawNote));
        else if (down is not null)
        {
            var rest = new List<MacroEvent>();
            for (int j = downIndex; j < events.Count; j++) rest.Add(events[j]);
            steps.Add(Raw(downDelay, rest, "A mouse button was pressed and never released."));
        }

        // The stop hotkey leaves modifier presses at the very end (the egg ends LCtrl down,
        // LShift down, LShift up, LShift down). In the trailing run of modifier key steps, drop
        // every down that has no later up, so Ctrl can never be left held. A complete down/up
        // pair in the run is a real press and stays. Walk backwards so indices stay valid.
        int runStart = steps.Count;
        while (runStart > 0 && steps[runStart - 1] is KeyStep rk && Modifiers.Contains(rk.VirtualKeyCode))
            runStart--;
        for (int j = steps.Count - 1; j >= runStart; j--)
        {
            if (steps[j] is KeyStep { Down: true } d
                && !steps.Skip(j + 1).Any(s => s is KeyStep { Down: false } u && u.VirtualKeyCode == d.VirtualKeyCode))
                steps.RemoveAt(j);
        }

        return steps;
    }

    private static RawStep Raw(long delay, List<MacroEvent> evs, string note)
    {
        var t0 = evs[0].TimestampMs;
        return new RawStep((int)delay, evs.Select(x => x with { TimestampMs = x.TimestampMs - t0 }).ToList(), note);
    }
}
```

- [ ] **Step 5: Run to verify it passes**

Run: `dotnet test tests/rororo-ur-task.Tests/rororo-ur-task.Tests.csproj -p:StandaloneTestsOnly=true --filter FullyQualifiedName~StepConverterTests`
Expected: all pass. This was verified before execution by porting this exact converter to a script and running it on both real recordings:

- "Mine Zone 8" gives 5 points at (472,317) (42,398) (100,172) (134,373) (302,430), delays 1509 / 478 / 4773 / 1870 / 5233 ms (sum 13 863), and no key steps: the trailing LCtrl is trimmed.
- "Mining Z8 Egg" gives 18 steps: 8 points, 0 drags, A and W and E once each (A twice, as two separate holds). It ends with LShift down then LShift up, and has no LCtrl.

If a number differs, print the steps and compare them with these values before touching a tolerance. The travel rule is the thing under test.

- [ ] **Step 6: Commit**

```bash
git add src/Macros/Steps/StepConverter.cs tests/rororo-ur-task.Tests/Steps/StepConverterTests.cs tests/fixtures/mine-zone-8.json tests/fixtures/mining-z8-egg.json tests/rororo-ur-task.Tests/rororo-ur-task.Tests.csproj
git commit -m "feat(steps): collapse recordings to point steps, keeping still time and dropping travel"
```

---

### Task 4: Point geometry and the adjustments store

**Files:**
- Create: `src/Macros/Steps/PointMath.cs`, `src/Macros/Steps/PointAdjustmentStore.cs`
- Test: `tests/rororo-ur-task.Tests/Steps/PointMathTests.cs`, `tests/rororo-ur-task.Tests/Steps/PointAdjustmentStoreTests.cs`

**Interfaces:**
- Consumes: `CheckBox`, `Rgb`.
- Produces: `PointMath.TargetClientSize((int W,int H) recorded, int? recordedScale, int currentScale)`; `PointMath.Place((int X,int Y) recordedPoint, (int W,int H) recordedClient, (int W,int H) actualClient)`; `PointMath.ScaledRadius(recordedClient, actualClient)`; `PointMath.SearchOffsets(int radius)` nearest first, excluding (0,0); `PointMath.BoxRect((int X,int Y) point, CheckBox box)`; `PointMath.InsideClient((int X,int Y,int W,int H) rect, (int W,int H) client)`; `PointMath.RobloxMinOuter(int scalePercent)`; `PointAdjustment(int X, int Y, Rgb? Expect = null, Rgb? Other = null)`; `PointAdjustmentStore(string path)` with `static string DefaultPath()`, `Get(string macroId, string pointId, long userId, int scale)`, `Set(...)`, `Remove(...)`.

- [ ] **Step 1: Write the failing tests**

```csharp
// tests/rororo-ur-task.Tests/Steps/PointMathTests.cs
using Labs626.UrTask.Macros.Steps;

namespace Labs626.UrTask.Tests.Steps;

public class PointMathTests
{
    [Fact]
    public void Same_scale_keeps_the_recorded_size()
        => Assert.Equal((800, 599), PointMath.TargetClientSize((800, 599), 100, 100));

    [Fact]
    public void Unknown_recorded_scale_keeps_the_recorded_size()
        => Assert.Equal((800, 599), PointMath.TargetClientSize((800, 599), null, 125));

    [Fact]
    public void Scale_change_scales_the_target()
        => Assert.Equal((1000, 749), PointMath.TargetClientSize((800, 599), 100, 125));

    [Fact]
    public void Points_scale_with_the_actual_client()
        => Assert.Equal((500, 375), PointMath.Place((400, 300), (800, 599), (1000, 749)));

    [Fact]
    public void Radius_scales_like_a_point()
        => Assert.Equal(30, PointMath.ScaledRadius((800, 599), (1000, 749)));

    [Fact]
    public void Search_offsets_are_nearest_first_and_inside_the_radius()
    {
        var offs = PointMath.SearchOffsets(24);
        Assert.DoesNotContain((0, 0), offs);
        Assert.All(offs, o => Assert.True(o.Dx * o.Dx + o.Dy * o.Dy <= 24 * 24));
        var d = offs.Select(o => o.Dx * o.Dx + o.Dy * o.Dy).ToList();
        Assert.Equal(d.OrderBy(x => x), d);
        Assert.Equal(4, Math.Abs(offs[0].Dx) + Math.Abs(offs[0].Dy)); // first ring is 4 px away
    }

    [Fact]
    public void Box_rect_applies_the_offset()
        => Assert.Equal((98, 198, 5, 5), PointMath.BoxRect((100, 200), new CheckBox()));

    [Theory]
    [InlineData(0, 0, 5, 5, true)]
    [InlineData(-1, 0, 5, 5, false)]
    [InlineData(796, 594, 5, 5, false)]
    [InlineData(795, 594, 5, 5, true)]
    public void Inside_client_is_strict(int x, int y, int w, int h, bool inside)
        => Assert.Equal(inside, PointMath.InsideClient((x, y, w, h), (800, 599)));

    [Theory]
    [InlineData(100, 816, 638)]
    [InlineData(125, 1020, 797)]
    [InlineData(150, 1224, 957)]
    public void Roblox_minimum_window_scales(int scale, int w, int h)
        => Assert.Equal((w, h), PointMath.RobloxMinOuter(scale));
}
```

```csharp
// tests/rororo-ur-task.Tests/Steps/PointAdjustmentStoreTests.cs
using System.IO;
using Labs626.UrTask.Macros;
using Labs626.UrTask.Macros.Steps;

namespace Labs626.UrTask.Tests.Steps;

public class PointAdjustmentStoreTests : IDisposable
{
    private readonly string _dir = Path.Combine(Path.GetTempPath(), "urtask-adj-" + Guid.NewGuid().ToString("N"));
    private string PathIn => Path.Combine(_dir, "adjustments.json");
    public void Dispose() { try { Directory.Delete(_dir, true); } catch { } }

    [Fact]
    public void Set_then_get_round_trips()
    {
        var s = new PointAdjustmentStore(PathIn);
        s.Set("m1", "p2", 5400534998, 100, new PointAdjustment(48, 401, new Rgb(1, 2, 3)));
        Assert.Equal(new PointAdjustment(48, 401, new Rgb(1, 2, 3)), new PointAdjustmentStore(PathIn).Get("m1", "p2", 5400534998, 100));
    }

    [Fact]
    public void Other_account_and_other_scale_are_untouched()
    {
        var s = new PointAdjustmentStore(PathIn);
        s.Set("m1", "p2", 5400534998, 100, new PointAdjustment(48, 401));
        Assert.Null(s.Get("m1", "p2", 1647274201, 100));
        Assert.Null(s.Get("m1", "p2", 5400534998, 125));
        Assert.Null(s.Get("m1", "p3", 5400534998, 100));
    }

    [Fact]
    public void Remove_restores_the_recorded_point()
    {
        var s = new PointAdjustmentStore(PathIn);
        s.Set("m1", "p2", 1, 100, new PointAdjustment(1, 1));
        s.Remove("m1", "p2", 1, 100);
        Assert.Null(s.Get("m1", "p2", 1, 100));
    }

    [Fact]
    public void A_corrupt_file_reads_as_empty_and_is_kept_aside_on_write()
    {
        Directory.CreateDirectory(_dir);
        File.WriteAllText(PathIn, "{ not json");
        var s = new PointAdjustmentStore(PathIn);
        Assert.Null(s.Get("m1", "p1", 1, 100));
        s.Set("m1", "p1", 1, 100, new PointAdjustment(2, 2));
        Assert.True(File.Exists(PathIn + ".bad"));
        Assert.NotNull(new PointAdjustmentStore(PathIn).Get("m1", "p1", 1, 100));
    }

    [Fact]
    public void A_file_locked_by_a_writer_reads_as_no_adjustment()
    {
        // The points overlay (plan 2) saves while a playback reads. A read that loses the race
        // must play the recorded point, not throw into the step runner.
        var s = new PointAdjustmentStore(PathIn);
        s.Set("m1", "p1", 1, 100, new PointAdjustment(2, 2));
        using var hold = new FileStream(PathIn, FileMode.Open, FileAccess.ReadWrite, FileShare.None);
        Assert.Null(new PointAdjustmentStore(PathIn).Get("m1", "p1", 1, 100));
    }

    [Fact]
    public void Default_path_is_outside_the_macros_folder()
    {
        var adj = Path.GetDirectoryName(PointAdjustmentStore.DefaultPath())!;
        Assert.NotEqual(Path.GetFullPath(MacroStore.DefaultDirectory()), Path.GetFullPath(adj));
        Assert.EndsWith(Path.Combine("626Labs", "RoRoRoUrTask"), adj);
    }
}
```

- [ ] **Step 2: Run to verify it fails**

Run: `dotnet test tests/rororo-ur-task.Tests/rororo-ur-task.Tests.csproj -p:StandaloneTestsOnly=true --filter "FullyQualifiedName~PointMathTests|FullyQualifiedName~PointAdjustmentStoreTests"`
Expected: build errors.

- [ ] **Step 3: Implement `PointMath`**

```csharp
// src/Macros/Steps/PointMath.cs
namespace Labs626.UrTask.Macros.Steps;

/// <summary>Pure geometry for point steps. All points are client-space pixels.</summary>
public static class PointMath
{
    public const int SearchRadiusPx = 24;
    public const int SearchStepPx = 4;

    /// <summary>The client size a v4 macro asks for: the recorded size times current over
    /// recorded display scale (display-scale findings, proposal 3). Unknown scale: unchanged.</summary>
    public static (int W, int H) TargetClientSize((int W, int H) recorded, int? recordedScale, int currentScale)
        => recordedScale is int rs && rs > 0 && currentScale > 0 && rs != currentScale
            ? ((int)Math.Round(recorded.W * currentScale / (double)rs), (int)Math.Round(recorded.H * currentScale / (double)rs))
            : recorded;

    /// <summary>Where a recorded point lands in the window as it actually is. Covers both a
    /// display-scale change and any slack in the reached client size.</summary>
    public static (int X, int Y) Place((int X, int Y) recordedPoint, (int W, int H) recordedClient, (int W, int H) actualClient)
        => ((int)Math.Round(recordedPoint.X * actualClient.W / (double)recordedClient.W),
            (int)Math.Round(recordedPoint.Y * actualClient.H / (double)recordedClient.H));

    public static int ScaledRadius((int W, int H) recordedClient, (int W, int H) actualClient)
        => (int)Math.Round(SearchRadiusPx * actualClient.W / (double)recordedClient.W);

    /// <summary>Grid offsets within the radius, nearest first; (0,0) excluded because the
    /// point itself was already checked.</summary>
    public static IReadOnlyList<(int Dx, int Dy)> SearchOffsets(int radius)
    {
        var list = new List<(int Dx, int Dy)>();
        for (int dy = -radius; dy <= radius; dy += SearchStepPx)
        for (int dx = -radius; dx <= radius; dx += SearchStepPx)
        {
            if (dx == 0 && dy == 0) continue;
            if (dx * dx + dy * dy <= radius * radius) list.Add((dx, dy));
        }
        return list.OrderBy(o => o.Dx * o.Dx + o.Dy * o.Dy).ThenBy(o => o.Dy).ThenBy(o => o.Dx).ToList();
    }

    public static (int X, int Y, int W, int H) BoxRect((int X, int Y) point, CheckBox box)
        => (point.X + box.OffsetX, point.Y + box.OffsetY, box.W, box.H);

    public static bool InsideClient((int X, int Y, int W, int H) r, (int W, int H) client)
        => r.X >= 0 && r.Y >= 0 && r.X + r.W <= client.W && r.Y + r.H <= client.H;

    /// <summary>Roblox's smallest outer window: 816x638 times the display scale, rounded down.
    /// Measured at 100% and 125% (docs/display-scale-findings.md).</summary>
    public static (int W, int H) RobloxMinOuter(int scalePercent)
        => (816 * scalePercent / 100, 638 * scalePercent / 100);
}
```

- [ ] **Step 4: Implement the store**

```csharp
// src/Macros/Steps/PointAdjustmentStore.cs
using System.IO;
using System.Text.Json;

namespace Labs626.UrTask.Macros.Steps;

/// <summary>A per-account, per-display-scale replacement for one point, in the macro's
/// recorded client frame. Expect/Other replace the check's colours when re-sampled.</summary>
public sealed record PointAdjustment(int X, int Y, Rgb? Expect = null, Rgb? Other = null);

/// <summary>
/// adjustments.json: macro id → point id → Roblox user id → display scale → adjustment.
/// Lives in Ur Task's data folder, never in macros\ (Ur Task and Ur OCR read every .json there
/// as a macro). Re-read when the file changes, so an open overlay's edits reach playback.
/// </summary>
public sealed class PointAdjustmentStore
{
    private static readonly JsonSerializerOptions Json = new() { WriteIndented = true, PropertyNamingPolicy = JsonNamingPolicy.CamelCase };

    private readonly string _path;
    private readonly object _gate = new();
    private Dictionary<string, Dictionary<string, Dictionary<string, Dictionary<string, PointAdjustment>>>> _data = new();
    private DateTime _loadedStamp = DateTime.MinValue;
    private bool _corrupt;

    public PointAdjustmentStore(string path) => _path = path ?? throw new ArgumentNullException(nameof(path));

    public static string DefaultPath() => Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "626Labs", "RoRoRoUrTask", "adjustments.json");

    public PointAdjustment? Get(string macroId, string pointId, long userId, int scale)
    {
        lock (_gate)
        {
            try
            {
                Refresh();
            }
            catch (IOException ex)
            {
                // A write racing this read (the overlay saves while a playback reads). Missing one
                // run's adjustment is better than failing the playback.
                Labs626.UrTask.Diagnostics.DiagLog.Write($"adjustments.json unreadable, playing recorded points: {ex.Message}");
                return null;
            }
            return _data.TryGetValue(macroId, out var pts) && pts.TryGetValue(pointId, out var accts)
                && accts.TryGetValue(userId.ToString(), out var scales) && scales.TryGetValue(scale.ToString(), out var a)
                ? a : null;
        }
    }

    public void Set(string macroId, string pointId, long userId, int scale, PointAdjustment adjustment)
    {
        lock (_gate)
        {
            Refresh();
            var pts = GetOrAdd(_data, macroId);
            var accts = GetOrAdd(pts, pointId);
            var scales = GetOrAdd(accts, userId.ToString());
            scales[scale.ToString()] = adjustment;
            Write();
        }
    }

    public void Remove(string macroId, string pointId, long userId, int scale)
    {
        lock (_gate)
        {
            Refresh();
            if (_data.TryGetValue(macroId, out var pts) && pts.TryGetValue(pointId, out var accts)
                && accts.TryGetValue(userId.ToString(), out var scales) && scales.Remove(scale.ToString()))
                Write();
        }
    }

    private void Refresh()
    {
        if (!File.Exists(_path)) { _data = new(); _corrupt = false; return; }
        var stamp = File.GetLastWriteTimeUtc(_path);
        if (stamp == _loadedStamp) return;
        try
        {
            _data = JsonSerializer.Deserialize<Dictionary<string, Dictionary<string, Dictionary<string, Dictionary<string, PointAdjustment>>>>>(
                File.ReadAllText(_path), Json) ?? new();
            _corrupt = false;
        }
        catch (JsonException)
        {
            _data = new();
            _corrupt = true;
        }
        _loadedStamp = stamp;
    }

    private void Write()
    {
        Directory.CreateDirectory(Path.GetDirectoryName(_path)!);
        if (_corrupt && File.Exists(_path)) { File.Copy(_path, _path + ".bad", overwrite: true); _corrupt = false; }
        var tmp = _path + ".tmp";
        File.WriteAllText(tmp, JsonSerializer.Serialize(_data, Json));
        File.Move(tmp, _path, overwrite: true);
        _loadedStamp = File.GetLastWriteTimeUtc(_path);
    }

    private static Dictionary<string, T> GetOrAdd<T>(Dictionary<string, Dictionary<string, T>> d, string key) where T : notnull
    {
        if (!d.TryGetValue(key, out var v)) d[key] = v = new Dictionary<string, T>();
        return v;
    }
}
```

- [ ] **Step 5: Run to verify it passes**

Run: `dotnet test tests/rororo-ur-task.Tests/rororo-ur-task.Tests.csproj -p:StandaloneTestsOnly=true --filter "FullyQualifiedName~PointMathTests|FullyQualifiedName~PointAdjustmentStoreTests"`
Expected: all pass.

- [ ] **Step 6: Commit**

```bash
git add src/Macros/Steps/PointMath.cs src/Macros/Steps/PointAdjustmentStore.cs tests/rororo-ur-task.Tests/Steps/PointMathTests.cs tests/rororo-ur-task.Tests/Steps/PointAdjustmentStoreTests.cs
git commit -m "feat(steps): point placement, nearby-search offsets, and per-account adjustments"
```

---

### Task 5: Pixels and display scale

**Files:**
- Create: `src/Macros/Steps/PixelBlock.cs`, `src/PluginHost/IScreenSampler.cs`, `src/PluginHost/ScreenSampler.cs`, `src/PluginHost/IDisplayScale.cs`, `src/PluginHost/DisplayScale.cs`
- Test: `tests/rororo-ur-task.Tests/Steps/PixelBlockTests.cs`

**Interfaces:**
- Consumes: `Rgb`, `IWindowMetrics.ClientOrigin`.
- Produces: `PixelBlock(int x, int y, int w, int h, uint[] argb)` in client coordinates with `Rgb? AverageBox(int x, int y, int w, int h)`; `IScreenSampler.Capture(IntPtr hwnd, int clientX, int clientY, int w, int h) : PixelBlock?`; `IDisplayScale.ScalePercentFor(IntPtr hwnd) : int`.

- [ ] **Step 1: Write the failing test**

```csharp
// tests/rororo-ur-task.Tests/Steps/PixelBlockTests.cs
using Labs626.UrTask.Macros.Steps;

namespace Labs626.UrTask.Tests.Steps;

public class PixelBlockTests
{
    private static uint Argb(int r, int g, int b) => 0xFF000000u | (uint)(r << 16) | (uint)(g << 8) | (uint)b;

    [Fact]
    public void Averages_a_box_in_client_coordinates()
    {
        // 4x2 block at client (10,20): left half red, right half blue.
        var px = new[] { Argb(200, 0, 0), Argb(200, 0, 0), Argb(0, 0, 100), Argb(0, 0, 100),
                         Argb(200, 0, 0), Argb(200, 0, 0), Argb(0, 0, 100), Argb(0, 0, 100) };
        var block = new PixelBlock(10, 20, 4, 2, px);
        Assert.Equal(new Rgb(200, 0, 0), block.AverageBox(10, 20, 2, 2));
        Assert.Equal(new Rgb(100, 0, 50), block.AverageBox(10, 20, 4, 2));
    }

    [Fact]
    public void A_box_outside_the_block_is_null()
    {
        var block = new PixelBlock(10, 20, 4, 2, new uint[8]);
        Assert.Null(block.AverageBox(9, 20, 2, 2));
        Assert.Null(block.AverageBox(13, 20, 2, 2));
    }

    [Fact]
    public void Rejects_a_pixel_array_of_the_wrong_length()
        => Assert.Throws<ArgumentException>(() => new PixelBlock(0, 0, 3, 3, new uint[8]));
}
```

- [ ] **Step 2: Run to verify it fails**

Run: `dotnet test tests/rororo-ur-task.Tests/rororo-ur-task.Tests.csproj -p:StandaloneTestsOnly=true --filter FullyQualifiedName~PixelBlockTests`
Expected: build error.

- [ ] **Step 3: Implement `PixelBlock` and the seams**

```csharp
// src/Macros/Steps/PixelBlock.cs
namespace Labs626.UrTask.Macros.Steps;

/// <summary>A captured rectangle of the client area. Pixels are 0xAARRGGBB, row-major.</summary>
public sealed class PixelBlock
{
    private readonly uint[] _argb;
    public int X { get; }
    public int Y { get; }
    public int W { get; }
    public int H { get; }

    public PixelBlock(int x, int y, int w, int h, uint[] argb)
    {
        if (argb is null || argb.Length != w * h) throw new ArgumentException("Pixel count does not match the block size.", nameof(argb));
        (X, Y, W, H, _argb) = (x, y, w, h, argb);
    }

    /// <summary>Average colour of a box given in client coordinates; null if it is not fully inside.</summary>
    public Rgb? AverageBox(int x, int y, int w, int h)
    {
        if (x < X || y < Y || x + w > X + W || y + h > Y + H || w <= 0 || h <= 0) return null;
        long r = 0, g = 0, b = 0;
        for (int row = y - Y; row < y - Y + h; row++)
        for (int col = x - X; col < x - X + w; col++)
        {
            var p = _argb[row * W + col];
            r += (p >> 16) & 0xFF; g += (p >> 8) & 0xFF; b += p & 0xFF;
        }
        long n = (long)w * h;
        return new Rgb((int)(r / n), (int)(g / n), (int)(b / n));
    }
}
```

```csharp
// src/PluginHost/IScreenSampler.cs
using Labs626.UrTask.Macros.Steps;

namespace Labs626.UrTask.PluginHost;

/// <summary>Reads what is on screen over a window's client area. It sees whatever is on top,
/// so callers bring the target to the front first. Null = window gone, minimised, or capture failed.</summary>
public interface IScreenSampler
{
    PixelBlock? Capture(IntPtr hwnd, int clientX, int clientY, int w, int h);
}
```

```csharp
// src/PluginHost/ScreenSampler.cs
using System.Runtime.InteropServices;
using Labs626.UrTask.Macros.Steps;

namespace Labs626.UrTask.PluginHost;

/// <summary>GDI capture from the desktop DC (BitBlt + GetDIBits), the same approach as Ur OCR.</summary>
internal sealed class ScreenSampler : IScreenSampler
{
    private readonly IWindowMetrics _metrics;
    public ScreenSampler(IWindowMetrics metrics) => _metrics = metrics;

    public PixelBlock? Capture(IntPtr hwnd, int clientX, int clientY, int w, int h)
    {
        if (w <= 0 || h <= 0) return null;
        if (IsIconic(hwnd)) return null;
        var origin = _metrics.ClientOrigin(hwnd);
        if (origin is null) return null;
        int sx = origin.Value.X + clientX, sy = origin.Value.Y + clientY;

        IntPtr screen = GetDC(IntPtr.Zero);
        if (screen == IntPtr.Zero) return null;
        IntPtr mem = CreateCompatibleDC(screen);
        IntPtr bmp = CreateCompatibleBitmap(screen, w, h);
        IntPtr old = SelectObject(mem, bmp);
        bool selected = true;
        try
        {
            if (!BitBlt(mem, 0, 0, w, h, screen, sx, sy, SRCCOPY)) return null;
            SelectObject(mem, old); // GetDIBits needs the bitmap out of any DC
            selected = false;
            var info = new BITMAPINFO
            {
                biSize = Marshal.SizeOf<BITMAPINFO>(), biWidth = w, biHeight = -h, // negative = top-down rows
                biPlanes = 1, biBitCount = 32, biCompression = 0,
            };
            var px = new uint[w * h];
            if (GetDIBits(mem, bmp, 0, (uint)h, px, ref info, 0) == 0) return null;
            for (int i = 0; i < px.Length; i++) px[i] |= 0xFF000000u;
            return new PixelBlock(clientX, clientY, w, h, px);
        }
        finally
        {
            // A bitmap still selected into a DC cannot be deleted. Without this, every failed
            // BitBlt would leak one GDI bitmap, once per poll.
            if (selected) SelectObject(mem, old);
            DeleteObject(bmp);
            DeleteDC(mem);
            ReleaseDC(IntPtr.Zero, screen);
        }
    }

    private const uint SRCCOPY = 0x00CC0020;

    [StructLayout(LayoutKind.Sequential)]
    private struct BITMAPINFO
    {
        public int biSize; public int biWidth; public int biHeight; public short biPlanes; public short biBitCount;
        public int biCompression; public int biSizeImage; public int biXPelsPerMeter; public int biYPelsPerMeter;
        public int biClrUsed; public int biClrImportant;
    }

    [DllImport("user32.dll")] private static extern bool IsIconic(IntPtr hWnd);
    [DllImport("user32.dll")] private static extern IntPtr GetDC(IntPtr hWnd);
    [DllImport("user32.dll")] private static extern int ReleaseDC(IntPtr hWnd, IntPtr hDC);
    [DllImport("gdi32.dll")] private static extern IntPtr CreateCompatibleDC(IntPtr hdc);
    [DllImport("gdi32.dll")] private static extern IntPtr CreateCompatibleBitmap(IntPtr hdc, int w, int h);
    [DllImport("gdi32.dll")] private static extern IntPtr SelectObject(IntPtr hdc, IntPtr obj);
    [DllImport("gdi32.dll")] private static extern bool BitBlt(IntPtr dst, int x, int y, int w, int h, IntPtr src, int sx, int sy, uint rop);
    [DllImport("gdi32.dll")] private static extern int GetDIBits(IntPtr hdc, IntPtr bmp, uint start, uint lines, [Out] uint[] bits, ref BITMAPINFO info, uint usage);
    [DllImport("gdi32.dll")] private static extern bool DeleteObject(IntPtr obj);
    [DllImport("gdi32.dll")] private static extern bool DeleteDC(IntPtr hdc);
}
```

```csharp
// src/PluginHost/IDisplayScale.cs
namespace Labs626.UrTask.PluginHost;

/// <summary>Windows display scale for the monitor hosting a window, in percent (100, 125, 150…).</summary>
public interface IDisplayScale
{
    int ScalePercentFor(IntPtr hwnd);
}
```

```csharp
// src/PluginHost/DisplayScale.cs
using System.Runtime.InteropServices;

namespace Labs626.UrTask.PluginHost;

/// <summary>GetDpiForWindow; the process is PerMonitorV2-aware (app.manifest), so this is the
/// window's own monitor. Falls back to 100 when the window is gone.</summary>
internal sealed class DisplayScale : IDisplayScale
{
    public int ScalePercentFor(IntPtr hwnd)
    {
        var dpi = hwnd == IntPtr.Zero ? 0u : GetDpiForWindow(hwnd);
        return dpi == 0 ? 100 : (int)Math.Round(dpi * 100 / 96.0);
    }

    [DllImport("user32.dll")] private static extern uint GetDpiForWindow(IntPtr hwnd);
}
```

- [ ] **Step 4: Run to verify it passes, and build**

Run: `dotnet build rororo-ur-task.csproj` then `dotnet test tests/rororo-ur-task.Tests/rororo-ur-task.Tests.csproj -p:StandaloneTestsOnly=true --filter FullyQualifiedName~PixelBlockTests`
Expected: build clean, tests pass. `ScreenSampler` and `DisplayScale` are verified live in Task 11.

- [ ] **Step 5: Commit**

```bash
git add src/Macros/Steps/PixelBlock.cs src/PluginHost/IScreenSampler.cs src/PluginHost/ScreenSampler.cs src/PluginHost/IDisplayScale.cs src/PluginHost/DisplayScale.cs tests/rororo-ur-task.Tests/Steps/PixelBlockTests.cs
git commit -m "feat(steps): capture a client rect and read the display scale"
```

---

### Task 6: The step runner

**Files:**
- Create: `src/Macros/Steps/StepRunner.cs`
- Modify: `src/Macros/MacroPlayer.cs` (only the `PlaybackResult` record at the bottom)
- Test: `tests/rororo-ur-task.Tests/Steps/StepRunnerTests.cs`

**Interfaces:**
- Consumes: every type from Tasks 1–5.
- Produces:
  - `PlaybackResult(PlaybackOutcome Outcome, string? Reason, int? StepIndex = null)` plus `static PlaybackResult AbortedAt(string reason, int stepIndex)`.
  - `internal interface IStepIo { bool Send(MacroEvent clientEvent); bool MoveRelative(int dx, int dy); (int X, int Y)? CursorClient(); (int W, int H)? ClientSize(); PixelBlock? Capture(int clientX, int clientY, int w, int h); bool TargetInForeground(); Task Delay(int ms, CancellationToken ct); long NowMs { get; } }`
  - `internal sealed record StepContext(string AccountName, long UserId, string MacroId, (int W, int H) RecordedClient, (int W, int H) ActualClient, int DisplayScale, Func<string, PointAdjustment?> Adjust, Action<string> Log)`
  - `internal static class StepRunner { static Task<PlaybackResult> RunAsync(IReadOnlyList<MacroStep> steps, StepContext ctx, IStepIo io, CancellationToken ct); }`

- [ ] **Step 1: Add the step index to `PlaybackResult`**

In `src/Macros/MacroPlayer.cs` replace the record at the bottom with:

```csharp
public sealed record PlaybackResult(PlaybackOutcome Outcome, string? Reason, int? StepIndex = null)
{
    public static PlaybackResult Refused(string reason) => new(PlaybackOutcome.Refused, reason);
    public static PlaybackResult Completed() => new(PlaybackOutcome.Completed, null);
    public static PlaybackResult Aborted(string reason) => new(PlaybackOutcome.Aborted, reason);
    public static PlaybackResult Skipped(string reason) => new(PlaybackOutcome.Skipped, reason);

    /// <summary>Stopped by a check: a colour that never showed, a window it could not see, or a
    /// box outside the window. Only check failures carry a StepIndex (0-based), so GetPlayback's
    /// failed/check-failed always means a check. Foreground loss and cancellation use
    /// <see cref="Aborted"/> without an index.</summary>
    public static PlaybackResult AbortedAt(string reason, int stepIndex) => new(PlaybackOutcome.Aborted, reason, stepIndex);
}
```

- [ ] **Step 2: Write the failing tests**

```csharp
// tests/rororo-ur-task.Tests/Steps/StepRunnerTests.cs
using Labs626.UrTask.Macros;
using Labs626.UrTask.Macros.Steps;

namespace Labs626.UrTask.Tests.Steps;

public class StepRunnerTests
{
    private static readonly Rgb Green = new(139, 224, 58);
    private static readonly Rgb Grey = new(150, 150, 160);
    private static readonly Rgb DarkBlue = new(20, 30, 90);

    /// <summary>Virtual clock; the screen is a function of (time, x, y).</summary>
    private sealed class FakeIo : IStepIo
    {
        public long NowMs { get; private set; }
        public List<MacroEvent> Sent = new();
        public List<(int, int)> Relative = new();
        public Func<long, int, int, Rgb> Screen = (_, _, _) => new Rgb(0, 0, 0);
        public (int W, int H)? Client = (800, 599);
        public bool Foreground = true;
        public bool CaptureWorks = true;
        public (int X, int Y) Cursor = (0, 0);
        public int Captures;
        public List<(int X, int Y, int W, int H)> CaptureRects = new();
        public Action? OnDelay;

        public bool Send(MacroEvent e) { Sent.Add(e with { TimestampMs = NowMs }); if (e.Kind == MacroEventKind.MouseMove) Cursor = (e.X, e.Y); return true; }
        public bool MoveRelative(int dx, int dy) { Relative.Add((dx, dy)); return true; }
        public (int X, int Y)? CursorClient() => Cursor;
        public (int W, int H)? ClientSize() => Client;
        public bool TargetInForeground() => Foreground;
        public PixelBlock? Capture(int x, int y, int w, int h)
        {
            Captures++;
            CaptureRects.Add((x, y, w, h));
            if (!CaptureWorks) return null;
            var px = new uint[w * h];
            for (int r = 0; r < h; r++)
            for (int c = 0; c < w; c++)
            {
                var rgb = Screen(NowMs, x + c, y + r);
                px[r * w + c] = 0xFF000000u | (uint)(rgb.R << 16) | (uint)(rgb.G << 8) | (uint)rgb.B;
            }
            return new PixelBlock(x, y, w, h, px);
        }
        public Task Delay(int ms, CancellationToken ct)
        {
            ct.ThrowIfCancellationRequested();
            NowMs += ms;
            OnDelay?.Invoke();
            ct.ThrowIfCancellationRequested();
            return Task.CompletedTask;
        }
        public IEnumerable<MacroEvent> Downs => Sent.Where(e => e.Kind == MacroEventKind.MouseDown);
    }

    private static StepContext Ctx(List<string>? log = null, Func<string, PointAdjustment?>? adjust = null,
        (int, int)? actual = null)
        => new("CElCPapa", 5400534998, "m1", (800, 599), actual ?? (800, 599), 100,
            adjust ?? (_ => null), s => log?.Add(s));

    private static ColorCheck GreenCheck(Rgb? other = null) => new(new CheckBox(), Green, other);

    [Fact]
    public async Task Unchecked_point_waits_its_delay_then_presses()
    {
        var io = new FakeIo();
        var r = await StepRunner.RunAsync(new MacroStep[] { new PointStep(1500, "p1", null, 42, 398) }, Ctx(), io, default);
        Assert.Equal(PlaybackOutcome.Completed, r.Outcome);
        var down = Assert.Single(io.Downs);
        Assert.Equal((42, 398), (down.X, down.Y));
        Assert.True(down.TimestampMs >= 1500);
        Assert.Contains(io.Sent, e => e.Kind == MacroEventKind.MouseUp && e.X == 42);
    }

    [Fact]
    public async Task Points_scale_to_the_actual_window()
    {
        var io = new FakeIo { Client = (1000, 749) };
        await StepRunner.RunAsync(new MacroStep[] { new PointStep(0, "p1", null, 400, 300) }, Ctx(actual: (1000, 749)), io, default);
        Assert.Equal((500, 375), (io.Downs.Single().X, io.Downs.Single().Y));
    }

    [Fact]
    public async Task An_adjustment_replaces_the_recorded_point()
    {
        var io = new FakeIo();
        await StepRunner.RunAsync(new MacroStep[] { new PointStep(0, "p2", null, 42, 398) },
            Ctx(adjust: id => id == "p2" ? new PointAdjustment(48, 401) : null), io, default);
        Assert.Equal((48, 401), (io.Downs.Single().X, io.Downs.Single().Y));
    }

    [Fact]
    public async Task Checked_point_presses_as_soon_as_the_colour_shows()
    {
        var io = new FakeIo { Screen = (t, _, _) => t >= 1000 ? Green : DarkBlue };
        var step = new PointStep(5200, "p1", "Tile", 100, 100, Check: GreenCheck(), CheckEnabled: true);
        var r = await StepRunner.RunAsync(new MacroStep[] { step }, Ctx(), io, default);
        Assert.Equal(PlaybackOutcome.Completed, r.Outcome);
        var down = io.Downs.Single();
        Assert.InRange(down.TimestampMs, 1000, 1000 + StepTiming.PollMs + StepTiming.JumpWiggleMs);
    }

    [Fact]
    public async Task Nearby_search_finds_a_shifted_button_and_logs_it()
    {
        var log = new List<string>();
        // Green band x 84..90: the box at 12 px left (86..90) is all green, the one at 8 px left (90..94) is not.
        var io = new FakeIo { Screen = (_, x, _) => x is >= 84 and <= 90 ? Green : DarkBlue };
        var step = new PointStep(0, "p1", "Opener", 100, 100, Check: GreenCheck(), CheckEnabled: true);
        var r = await StepRunner.RunAsync(new MacroStep[] { step }, Ctx(log), io, default);
        Assert.Equal(PlaybackOutcome.Completed, r.Outcome);
        Assert.Equal((88, 100), (io.Downs.Single().X, io.Downs.Single().Y));
        Assert.Contains(log, l => l.Contains("'Opener' found 12 px left"));
    }

    [Fact]
    public async Task A_check_that_never_matches_stops_with_a_readable_report()
    {
        var io = new FakeIo { Screen = (_, _, _) => DarkBlue };
        var steps = new MacroStep[]
        {
            new PointStep(0, "p1", null, 10, 10),
            new PointStep(2800, "p2", "Teleport opener", 100, 100, Check: GreenCheck(), CheckEnabled: true),
        };
        var r = await StepRunner.RunAsync(steps, Ctx(), io, default);
        Assert.Equal(PlaybackOutcome.Aborted, r.Outcome);
        Assert.Equal(1, r.StepIndex);
        Assert.Equal("CElCPapa: step 2 'Teleport opener' expected green #8BE03A, saw dark blue #141E5A (distance 230) after 5.8 s at 100%.", r.Reason);
        Assert.Single(io.Downs); // only step 1 pressed
    }

    [Fact]
    public async Task First_match_skips_a_locked_tile_without_waiting()
    {
        // #8 grey (locked), #7 green: every candidate resolves on the first poll.
        var io = new FakeIo { Screen = (_, x, _) => x < 200 ? Grey : Green };
        var fm = new FirstMatchStep(400, "f1", "Best mine", new[]
        {
            new PointStep(0, "t8", "#8", 137, 390, Check: GreenCheck(Grey)),
            new PointStep(0, "t7", "#7", 312, 390, Check: GreenCheck(Grey)),
        });
        var r = await StepRunner.RunAsync(new MacroStep[] { fm }, Ctx(), io, default);
        Assert.Equal(PlaybackOutcome.Completed, r.Outcome);
        Assert.Equal((312, 390), (io.Downs.Single().X, io.Downs.Single().Y));
        Assert.True(io.Downs.Single().TimestampMs < 400 + 3000);
    }

    [Fact]
    public async Task First_match_waits_for_a_window_still_sliding_in()
    {
        // Nothing resolves until t=1200 (the window fades in), then #8 is green.
        var io = new FakeIo { Screen = (t, _, _) => t >= 1200 ? Green : DarkBlue };
        var fm = new FirstMatchStep(0, "f1", "Best mine", new[]
        {
            new PointStep(0, "t8", "#8", 137, 390, Check: GreenCheck(Grey)),
            new PointStep(0, "t7", "#7", 312, 390, Check: GreenCheck(Grey)),
        });
        await StepRunner.RunAsync(new MacroStep[] { fm }, Ctx(), io, default);
        var down = io.Downs.Single();
        Assert.Equal((137, 390), (down.X, down.Y));
        Assert.True(down.TimestampMs >= 1200);
    }

    [Fact]
    public async Task First_match_with_no_match_skips_or_stops_as_told()
    {
        var io = new FakeIo { Screen = (_, _, _) => Grey };
        PointStep[] cands = { new(0, "t8", "#8", 137, 390, Check: GreenCheck(Grey)), new(0, "t7", "#7", 312, 390, Check: GreenCheck(Grey)) };

        var skip = await StepRunner.RunAsync(new MacroStep[] { new FirstMatchStep(0, "f1", "Best mine", cands, NoMatchAction.Skip), new PointStep(0, "p9", null, 5, 5) }, Ctx(), io, default);
        Assert.Equal(PlaybackOutcome.Completed, skip.Outcome);
        Assert.Equal((5, 5), (io.Downs.Single().X, io.Downs.Single().Y));

        var io2 = new FakeIo { Screen = (_, _, _) => Grey };
        var stop = await StepRunner.RunAsync(new MacroStep[] { new FirstMatchStep(0, "f1", "Best mine", cands) }, Ctx(), io2, default);
        Assert.Equal(PlaybackOutcome.Aborted, stop.Outcome);
        Assert.StartsWith("CElCPapa: step 1 'Best mine' found no candidate that matched after", stop.Reason);
        // Full colour names plus expected colour and distance, per the failure-text constraint.
        Assert.Contains("'#8' expected green #8BE03A, saw grey #9696A0 (distance 126)", stop.Reason);
        Assert.Equal(0, stop.StepIndex);
    }

    [Fact]
    public async Task First_match_parks_the_pointer_off_every_candidate()
    {
        // The pointer rests on #8 after an earlier press, and hover would tint it.
        var io = new FakeIo { Cursor = (137, 390), Screen = (_, _, _) => Green };
        var fm = new FirstMatchStep(0, "f1", "Best mine", new[]
        {
            new PointStep(0, "t8", "#8", 137, 390, Check: GreenCheck(Grey)),
            new PointStep(0, "t7", "#7", 312, 390, Check: GreenCheck(Grey)),
        });
        await StepRunner.RunAsync(new MacroStep[] { fm }, Ctx(), io, default);
        var park = io.Sent.First(e => e.Kind == MacroEventKind.MouseMove);
        Assert.True(Math.Abs(park.X - 137) > 10 || Math.Abs(park.Y - 390) > 10);
        Assert.True(Math.Abs(park.X - 312) > 10 || Math.Abs(park.Y - 390) > 10);
    }

    [Fact]
    public async Task A_window_covered_while_waiting_for_its_colour_is_not_a_colour_mismatch()
    {
        // Another window comes to the front 300 ms into the wait. What the capture sees then is
        // not the target, so the stop must say so rather than report a wrong colour.
        var io = new FakeIo { Screen = (_, _, _) => DarkBlue };
        io.OnDelay = () => { if (io.NowMs >= 300) io.Foreground = false; };
        var step = new PointStep(1000, "p1", "Tile", 100, 100, Check: GreenCheck(), CheckEnabled: true);
        var r = await StepRunner.RunAsync(new MacroStep[] { step }, Ctx(), io, default);
        Assert.Equal("CElCPapa: step 1 'Tile' could not see the window.", r.Reason);
        Assert.Equal(0, r.StepIndex);
        Assert.Empty(io.Downs);
    }

    [Fact]
    public async Task First_match_covered_while_waiting_could_not_see_the_window()
    {
        var io = new FakeIo { Screen = (_, _, _) => DarkBlue };
        io.OnDelay = () => { if (io.NowMs >= 300) io.Foreground = false; };
        var fm = new FirstMatchStep(0, "f1", "Best mine", new[] { new PointStep(0, "t8", "#8", 137, 390, Check: GreenCheck(Grey)) });
        var r = await StepRunner.RunAsync(new MacroStep[] { fm }, Ctx(), io, default);
        Assert.Equal("CElCPapa: step 1 'Best mine' could not see the window.", r.Reason);
        Assert.Empty(io.Downs);
    }

    [Fact]
    public async Task A_check_box_outside_the_window_refuses()
    {
        var io = new FakeIo();
        var step = new PointStep(0, "p1", "Edge", 799, 598, Check: GreenCheck(), CheckEnabled: true);
        var r = await StepRunner.RunAsync(new MacroStep[] { step }, Ctx(), io, default);
        Assert.Equal("CElCPapa: step 1 'Edge' checks a box outside the window.", r.Reason);
        Assert.Empty(io.Downs);
    }

    [Fact]
    public async Task Search_never_samples_outside_the_window()
    {
        // The point sits 4 px from the left edge and its button is 8 px right (green at x 10..14).
        // The search must skip offsets whose box would cross x < 0, still find the button, and
        // never capture a pixel outside the 800x599 client area.
        var io = new FakeIo { Screen = (_, x, _) => x is >= 10 and <= 14 ? Green : DarkBlue };
        var step = new PointStep(0, "p1", "Edge", 4, 100, Check: GreenCheck(), CheckEnabled: true);
        var r = await StepRunner.RunAsync(new MacroStep[] { step }, Ctx(), io, default);
        Assert.Equal(PlaybackOutcome.Completed, r.Outcome);
        Assert.Equal((12, 100), (io.Downs.Single().X, io.Downs.Single().Y));
        Assert.NotEmpty(io.CaptureRects);
        Assert.All(io.CaptureRects, c => Assert.True(c.X >= 0 && c.Y >= 0 && c.X + c.W <= 800 && c.Y + c.H <= 599, $"captured {c} outside the window"));
    }

    [Fact]
    public async Task A_window_that_cannot_be_seen_is_not_a_colour_mismatch()
    {
        var io = new FakeIo { CaptureWorks = false };
        var step = new PointStep(0, "p1", "Tile", 100, 100, Check: GreenCheck(), CheckEnabled: true);
        var r = await StepRunner.RunAsync(new MacroStep[] { step }, Ctx(), io, default);
        Assert.Equal("CElCPapa: step 1 'Tile' could not see the window.", r.Reason);
    }

    [Fact]
    public async Task Losing_the_foreground_aborts()
    {
        var io = new FakeIo { Foreground = false };
        var r = await StepRunner.RunAsync(new MacroStep[] { new PointStep(0, "p1", null, 1, 1) }, Ctx(), io, default);
        Assert.Equal(PlaybackOutcome.Aborted, r.Outcome);
        Assert.Equal("Foreground shifted away from CElCPapa at step 1/1.", r.Reason);
        Assert.Null(r.StepIndex); // not a check failure, so GetPlayback must not call it check-failed
    }

    [Fact]
    public async Task Cancelling_during_a_wait_for_colour_stops_and_releases_keys()
    {
        using var cts = new CancellationTokenSource();
        var io = new FakeIo { Screen = (_, _, _) => DarkBlue };
        io.OnDelay = () => { if (io.NowMs >= 500) cts.Cancel(); };
        var steps = new MacroStep[]
        {
            new KeyStep(0, 0x41, true),
            new PointStep(0, "p1", "Tile", 100, 100, Check: GreenCheck(), CheckEnabled: true),
        };
        var r = await StepRunner.RunAsync(steps, Ctx(), io, cts.Token);
        Assert.Equal("Playback cancelled.", r.Reason);
        Assert.Null(r.StepIndex); // Esc is not a check failure
        Assert.True(io.NowMs < 1000);
        Assert.Contains(io.Sent, e => e.Kind == MacroEventKind.KeyUp && e.VirtualKeyCode == 0x41);
    }

    [Fact]
    public async Task Held_keys_are_released_at_the_end()
    {
        var io = new FakeIo();
        await StepRunner.RunAsync(new MacroStep[] { new KeyStep(0, 0x57, true) }, Ctx(), io, default);
        Assert.Equal(MacroEventKind.KeyUp, io.Sent[^1].Kind);
        Assert.Equal(0x57, io.Sent[^1].VirtualKeyCode);
    }

    [Fact]
    public async Task Right_drag_presses_moves_and_releases()
    {
        var io = new FakeIo();
        await StepRunner.RunAsync(new MacroStep[] { new DragStep(0, 2, 400, 300, 0, 300, 400) }, Ctx(), io, default);
        var down = io.Sent.First(e => e.Kind == MacroEventKind.MouseDown);
        var up = io.Sent.Last(e => e.Kind == MacroEventKind.MouseUp);
        Assert.Equal((2, 400, 300), (down.MouseButton, down.X, down.Y));
        Assert.Equal((400, 600), (up.X, up.Y));
    }

    [Fact]
    public async Task Pointer_move_sends_relative_movement()
    {
        var io = new FakeIo();
        await StepRunner.RunAsync(new MacroStep[] { new PointerMoveStep(0, 0, 200, 300) }, Ctx(), io, default);
        Assert.Equal(200, io.Relative.Sum(m => m.Item2));
    }

    [Fact]
    public async Task The_cursor_is_parked_off_a_box_before_checking()
    {
        var io = new FakeIo { Cursor = (100, 100), Screen = (_, _, _) => Green };
        var step = new PointStep(0, "p1", "Tile", 100, 100, Check: GreenCheck(), CheckEnabled: true);
        await StepRunner.RunAsync(new MacroStep[] { step }, Ctx(), io, default);
        var firstMove = io.Sent.First(e => e.Kind == MacroEventKind.MouseMove);
        Assert.True(Math.Abs(firstMove.X - 100) > 10 || Math.Abs(firstMove.Y - 100) > 10);
    }

    [Fact]
    public async Task Invalid_steps_refuse_before_any_input()
    {
        var io = new FakeIo();
        var r = await StepRunner.RunAsync(new MacroStep[] { new FirstMatchStep(0, "f1", "Best mine", Array.Empty<PointStep>()) }, Ctx(), io, default);
        Assert.Equal(PlaybackOutcome.Refused, r.Outcome);
        Assert.Empty(io.Sent);
    }
}
```

- [ ] **Step 3: Run to verify it fails**

Run: `dotnet test tests/rororo-ur-task.Tests/rororo-ur-task.Tests.csproj -p:StandaloneTestsOnly=true --filter FullyQualifiedName~StepRunnerTests`
Expected: build errors.

- [ ] **Step 4: Implement**

```csharp
// src/Macros/Steps/StepRunner.cs
using System.Globalization;

namespace Labs626.UrTask.Macros.Steps;

/// <summary>Everything the runner needs from the outside world. Coordinates are client-space.</summary>
internal interface IStepIo
{
    bool Send(MacroEvent clientEvent);
    bool MoveRelative(int dx, int dy);
    (int X, int Y)? CursorClient();
    (int W, int H)? ClientSize();
    PixelBlock? Capture(int clientX, int clientY, int w, int h);
    bool TargetInForeground();
    Task Delay(int ms, CancellationToken ct);
    long NowMs { get; }
}

internal sealed record StepContext(
    string AccountName, long UserId, string MacroId,
    (int W, int H) RecordedClient, (int W, int H) ActualClient, int DisplayScale,
    Func<string, PointAdjustment?> Adjust, Action<string> Log);

/// <summary>
/// Plays v4 steps. A point with its check on treats its delay as a ceiling: it presses as soon
/// as the box shows the expected colour, then searches nearby, then stops with a report.
/// </summary>
internal static class StepRunner
{
    private sealed class StopException(string reason, int stepIndex) : Exception(reason)
    {
        public int StepIndex { get; } = stepIndex;
    }

    public static async Task<PlaybackResult> RunAsync(IReadOnlyList<MacroStep> steps, StepContext ctx, IStepIo io, CancellationToken ct)
    {
        var invalid = StepValidator.Validate(steps);
        if (invalid is not null) return PlaybackResult.Refused(invalid);

        var heldKeys = new HashSet<int>();
        var heldButtons = new HashSet<int>();
        try
        {
            for (int i = 0; i < steps.Count; i++)
            {
                // Not a check failure: no StepIndex, so GetPlayback reports "aborted", not "check-failed".
                if (!io.TargetInForeground())
                    return PlaybackResult.Aborted($"Foreground shifted away from {ctx.AccountName} at step {i + 1}/{steps.Count}.");

                switch (steps[i])
                {
                    case KeyStep k:
                        await io.Delay(k.DelayMs, ct);
                        io.Send(new MacroEvent(0, k.Down ? MacroEventKind.KeyDown : MacroEventKind.KeyUp, k.VirtualKeyCode, 0, 0, 0, 0));
                        if (k.Down) heldKeys.Add(k.VirtualKeyCode); else heldKeys.Remove(k.VirtualKeyCode);
                        break;
                    case PointStep p:
                        await PlayPointAsync(p, i, ctx, io, heldButtons, ct);
                        break;
                    case FirstMatchStep f:
                        await PlayFirstMatchAsync(f, i, ctx, io, heldButtons, ct);
                        break;
                    case DragStep d:
                        await PlayDragAsync(d, ctx, io, heldButtons, ct);
                        break;
                    case WheelStep w:
                        await io.Delay(w.DelayMs, ct);
                        var wp = Place(ctx, (w.X, w.Y));
                        await JumpAsync(io, wp, ct);
                        io.Send(new MacroEvent(0, MacroEventKind.MouseWheel, 0, wp.X, wp.Y, 0, w.Delta));
                        break;
                    case PointerMoveStep m:
                        await io.Delay(m.DelayMs, ct);
                        await PlayRelativeAsync(m, io, ct);
                        break;
                    case WaitStep w:
                        await io.Delay(w.DelayMs, ct);
                        break;
                    case RawStep r:
                        await io.Delay(r.DelayMs, ct);
                        await PlayRawAsync(r, ctx, io, heldKeys, heldButtons, ct);
                        break;
                }
            }
            return PlaybackResult.Completed();
        }
        catch (StopException s)
        {
            return PlaybackResult.AbortedAt(s.Message, s.StepIndex);
        }
        catch (OperationCanceledException)
        {
            return PlaybackResult.Aborted("Playback cancelled."); // Esc or StopMacro, not a check failure
        }
        finally
        {
            foreach (var vk in heldKeys) io.Send(new MacroEvent(0, MacroEventKind.KeyUp, vk, 0, 0, 0, 0));
            var c = io.CursorClient() ?? (0, 0);
            foreach (var b in heldButtons) io.Send(new MacroEvent(0, MacroEventKind.MouseUp, 0, c.X, c.Y, b, 0));
        }
    }

    // ---------- points ----------

    private static async Task PlayPointAsync(PointStep p, int index, StepContext ctx, IStepIo io, HashSet<int> heldButtons, CancellationToken ct)
    {
        var (recorded, check) = Resolve(p, ctx);
        var at = Place(ctx, recorded);

        if (!p.CheckEnabled || check is null)
        {
            await io.Delay(p.DelayMs, ct);
            await PressAsync(io, at, p.Button, heldButtons, ct);
            return;
        }

        var name = $"{ctx.AccountName}: step {index + 1} '{p.Label ?? p.Id}'";
        var box = PointMath.BoxRect(at, check.Box);
        var client = io.ClientSize() ?? throw new StopException($"{name} could not see the window.", index);
        if (!PointMath.InsideClient(box, client)) throw new StopException($"{name} checks a box outside the window.", index);

        await ParkCursorAsync(io, new[] { box }, client, ct);
        var start = io.NowMs;
        var ceiling = p.DelayMs + StepTiming.CheckGraceMs;
        Rgb seen;
        while (true)
        {
            // Every poll: a window that came to the front covers the target, and the capture
            // would read its pixels as a false colour (spec §2 and §5).
            if (!io.TargetInForeground()) throw new StopException($"{name} could not see the window.", index);
            var block = io.Capture(box.X, box.Y, box.W, box.H) ?? throw new StopException($"{name} could not see the window.", index);
            seen = block.AverageBox(box.X, box.Y, box.W, box.H)!.Value;
            var v = ColorMatcher.Evaluate(seen, check);
            if (v.Matched)
            {
                // Logged so the default tolerance can be tuned from real runs (spec, section 1).
                ctx.Log($"step {index + 1} '{p.Label ?? p.Id}' matched at distance {v.Distance:F0} after {(io.NowMs - start) / 1000.0:F1} s");
                await PressAsync(io, at, p.Button, heldButtons, ct);
                return;
            }
            if (io.NowMs - start >= ceiling) break;
            await io.Delay(StepTiming.PollMs, ct);
        }

        var found = Search(io, at, check, client, ctx);
        if (found is { } hit)
        {
            ctx.Log($"step {index + 1} '{p.Label ?? p.Id}' found {Describe(hit.X - at.X, hit.Y - at.Y)}");
            await PressAsync(io, hit, p.Button, heldButtons, ct);
            return;
        }

        var d = seen.DistanceTo(check.Expect);
        var secs = (io.NowMs - start) / 1000.0;
        throw new StopException(string.Create(CultureInfo.InvariantCulture,
            $"{name} expected {ColorNamer.Describe(check.Expect)}, saw {ColorNamer.Describe(seen)} (distance {d:F0}) after {secs:F1} s at {ctx.DisplayScale}%."), index);
    }

    private static (int X, int Y)? Search(IStepIo io, (int X, int Y) at, ColorCheck check, (int W, int H) client, StepContext ctx)
    {
        var radius = PointMath.ScaledRadius(ctx.RecordedClient, ctx.ActualClient);
        var b0 = PointMath.BoxRect(at, check.Box);
        int x0 = Math.Max(0, b0.X - radius), y0 = Math.Max(0, b0.Y - radius);
        int x1 = Math.Min(client.W, b0.X + b0.W + radius), y1 = Math.Min(client.H, b0.Y + b0.H + radius);
        if (x1 <= x0 || y1 <= y0) return null;
        var block = io.Capture(x0, y0, x1 - x0, y1 - y0);
        if (block is null) return null;
        foreach (var (dx, dy) in PointMath.SearchOffsets(radius))
        {
            var cand = (X: at.X + dx, Y: at.Y + dy);
            var r = PointMath.BoxRect(cand, check.Box);
            if (!PointMath.InsideClient(r, client)) continue;
            var seen = block.AverageBox(r.X, r.Y, r.W, r.H);
            if (seen is { } s && ColorMatcher.Evaluate(s, check).Matched) return cand;
        }
        return null;
    }

    private static string Describe(int dx, int dy)
    {
        var parts = new List<string>();
        if (dx != 0) parts.Add($"{Math.Abs(dx)} px {(dx < 0 ? "left" : "right")}");
        if (dy != 0) parts.Add($"{Math.Abs(dy)} px {(dy < 0 ? "up" : "down")}");
        return string.Join(" and ", parts);
    }

    // ---------- first match ----------

    private static async Task PlayFirstMatchAsync(FirstMatchStep f, int index, StepContext ctx, IStepIo io, HashSet<int> heldButtons, CancellationToken ct)
    {
        var name = $"{ctx.AccountName}: step {index + 1} '{f.Label ?? f.Id}'";
        var client = io.ClientSize() ?? throw new StopException($"{name} could not see the window.", index);
        var cands = f.Candidates.Select(c =>
        {
            var (rec, check) = Resolve(c, ctx);
            var at = Place(ctx, rec);
            return (Step: c, At: at, Check: check!, Box: PointMath.BoxRect(at, check!.Box));
        }).ToList();
        foreach (var c in cands)
            if (!PointMath.InsideClient(c.Box, client))
                throw new StopException($"{name}: candidate '{c.Step.Label ?? c.Step.Id}' checks a box outside the window.", index);

        // Hover tints a button, so the pointer leaves every candidate box before the first sample.
        await ParkCursorAsync(io, cands.Select(c => c.Box).ToList(), client, ct);
        var start = io.NowMs;
        var ceiling = f.DelayMs + StepTiming.CheckGraceMs;
        var seen = new Rgb[cands.Count];
        while (true)
        {
            if (!io.TargetInForeground()) throw new StopException($"{name} could not see the window.", index);
            bool allResolved = true;
            int firstMatch = -1;
            for (int k = 0; k < cands.Count; k++)
            {
                var b = cands[k].Box;
                var block = io.Capture(b.X, b.Y, b.W, b.H) ?? throw new StopException($"{name} could not see the window.", index);
                seen[k] = block.AverageBox(b.X, b.Y, b.W, b.H)!.Value;
                if (ColorMatcher.Evaluate(seen[k], cands[k].Check).Matched) { if (firstMatch < 0) firstMatch = k; continue; }
                // Resolved without matching only when it shows its other state.
                if (!ColorMatcher.ShowsOther(seen[k], cands[k].Check)) allResolved = false;
            }
            var expired = io.NowMs - start >= ceiling;
            if (firstMatch >= 0 && (allResolved || expired || AllBeforeResolved(cands, seen, firstMatch)))
            {
                ctx.Log($"step {index + 1} '{f.Label ?? f.Id}' chose '{cands[firstMatch].Step.Label ?? cands[firstMatch].Step.Id}'");
                await PressAsync(io, cands[firstMatch].At, cands[firstMatch].Step.Button, heldButtons, ct);
                return;
            }
            if (expired || (allResolved && firstMatch < 0)) break;
            await io.Delay(StepTiming.PollMs, ct);
        }

        if (f.OnNoMatch == NoMatchAction.Skip)
        {
            ctx.Log($"step {index + 1} '{f.Label ?? f.Id}' found no match and skipped");
            return;
        }
        var secs = (io.NowMs - start) / 1000.0;
        // Full colour names (never cut: "dark blue" must not become "dark"), plus expected colour
        // and distance for each candidate, per the failure-text constraint.
        var detail = string.Join("; ", cands.Select((c, k) => string.Create(CultureInfo.InvariantCulture,
            $"'{c.Step.Label ?? c.Step.Id}' expected {ColorNamer.Describe(c.Check.Expect)}, saw {ColorNamer.Describe(seen[k])} (distance {seen[k].DistanceTo(c.Check.Expect):F0})")));
        throw new StopException(string.Create(CultureInfo.InvariantCulture,
            $"{name} found no candidate that matched after {secs:F1} s at {ctx.DisplayScale}% ({detail})."), index);
    }

    /// <summary>A match may be pressed early when every candidate ahead of it shows its other state.</summary>
    private static bool AllBeforeResolved(List<(PointStep Step, (int X, int Y) At, ColorCheck Check, (int X, int Y, int W, int H) Box)> cands, Rgb[] seen, int firstMatch)
    {
        for (int k = 0; k < firstMatch; k++)
            if (!ColorMatcher.ShowsOther(seen[k], cands[k].Check)) return false;
        return true;
    }

    // ---------- movement ----------

    private static async Task PressAsync(IStepIo io, (int X, int Y) at, int button, HashSet<int> heldButtons, CancellationToken ct)
    {
        await JumpAsync(io, at, ct);
        io.Send(new MacroEvent(0, MacroEventKind.MouseDown, 0, at.X, at.Y, button, 0));
        heldButtons.Add(button);
        await io.Delay(StepTiming.PressHoldMs, ct);
        io.Send(new MacroEvent(0, MacroEventKind.MouseUp, 0, at.X, at.Y, button, 0));
        heldButtons.Remove(button);
    }

    /// <summary>Jump then wiggle: Roblox drops the first click after focus unless it sees movement.</summary>
    private static async Task JumpAsync(IStepIo io, (int X, int Y) at, CancellationToken ct)
    {
        var third = StepTiming.JumpWiggleMs / 3;
        io.Send(new MacroEvent(0, MacroEventKind.MouseMove, 0, at.X - 3, at.Y - 2, 0, 0));
        await io.Delay(third, ct);
        io.Send(new MacroEvent(0, MacroEventKind.MouseMove, 0, at.X + 2, at.Y + 1, 0, 0));
        await io.Delay(third, ct);
        io.Send(new MacroEvent(0, MacroEventKind.MouseMove, 0, at.X, at.Y, 0, 0));
        await io.Delay(StepTiming.JumpWiggleMs - 2 * third, ct);
    }

    private const int ParkMargin = 8;

    private static bool NearBox((int X, int Y) c, (int X, int Y, int W, int H) b)
        => c.X >= b.X - ParkMargin && c.X <= b.X + b.W + ParkMargin && c.Y >= b.Y - ParkMargin && c.Y <= b.Y + b.H + ParkMargin;

    /// <summary>Hover changes a button's colour, so the pointer leaves every box before a check.
    /// Tries the four corners 30 px outside each box and takes the first spot inside the client
    /// area that is near none of the boxes.</summary>
    private static async Task ParkCursorAsync(IStepIo io, IReadOnlyList<(int X, int Y, int W, int H)> boxes, (int W, int H) client, CancellationToken ct)
    {
        if (io.CursorClient() is not { } c) return;
        if (!boxes.Any(b => NearBox(c, b))) return;
        foreach (var b in boxes)
        {
            var spots = new[]
            {
                (X: b.X + b.W + 30, Y: b.Y + b.H + 30), (X: b.X - 30, Y: b.Y + b.H + 30),
                (X: b.X + b.W + 30, Y: b.Y - 30),       (X: b.X - 30, Y: b.Y - 30),
            };
            foreach (var s in spots)
            {
                var q = (X: Math.Clamp(s.X, 0, client.W - 1), Y: Math.Clamp(s.Y, 0, client.H - 1));
                if (boxes.Any(bb => NearBox(q, bb))) continue;
                io.Send(new MacroEvent(0, MacroEventKind.MouseMove, 0, q.X, q.Y, 0, 0));
                await io.Delay(0, ct);
                return;
            }
        }
    }

    private static async Task PlayDragAsync(DragStep d, StepContext ctx, IStepIo io, HashSet<int> heldButtons, CancellationToken ct)
    {
        await io.Delay(d.DelayMs, ct);
        var from = Place(ctx, (d.StartX, d.StartY));
        var to = Place(ctx, (d.StartX + d.Dx, d.StartY + d.Dy));
        await JumpAsync(io, from, ct);
        io.Send(new MacroEvent(0, MacroEventKind.MouseDown, 0, from.X, from.Y, d.Button, 0));
        heldButtons.Add(d.Button);
        const int slices = 10;
        for (int s = 1; s <= slices; s++)
        {
            await io.Delay(Math.Max(1, d.DurationMs / slices), ct);
            io.Send(new MacroEvent(0, MacroEventKind.MouseMove, 0,
                from.X + (to.X - from.X) * s / slices, from.Y + (to.Y - from.Y) * s / slices, 0, 0));
        }
        io.Send(new MacroEvent(0, MacroEventKind.MouseUp, 0, to.X, to.Y, d.Button, 0));
        heldButtons.Remove(d.Button);
    }

    private static async Task PlayRelativeAsync(PointerMoveStep m, IStepIo io, CancellationToken ct)
    {
        const int slices = 10;
        int sentX = 0, sentY = 0;
        for (int s = 1; s <= slices; s++)
        {
            await io.Delay(Math.Max(1, m.DurationMs / slices), ct);
            int tx = m.Dx * s / slices, ty = m.Dy * s / slices;
            io.MoveRelative(tx - sentX, ty - sentY);
            (sentX, sentY) = (tx, ty);
        }
    }

    private static async Task PlayRawAsync(RawStep r, StepContext ctx, IStepIo io, HashSet<int> heldKeys, HashSet<int> heldButtons, CancellationToken ct)
    {
        long last = 0;
        foreach (var e in r.Events)
        {
            var wait = (int)Math.Clamp(e.TimestampMs - last, 0, int.MaxValue);
            if (wait > 0) await io.Delay(wait, ct);
            last = e.TimestampMs;
            var ev = e;
            if (e.Kind is MacroEventKind.MouseMove or MacroEventKind.MouseDown or MacroEventKind.MouseUp or MacroEventKind.MouseWheel)
            {
                var at = Place(ctx, (e.X, e.Y));
                ev = e with { X = at.X, Y = at.Y };
            }
            io.Send(ev);
            switch (ev.Kind)
            {
                case MacroEventKind.KeyDown: heldKeys.Add(ev.VirtualKeyCode); break;
                case MacroEventKind.KeyUp: heldKeys.Remove(ev.VirtualKeyCode); break;
                case MacroEventKind.MouseDown: heldButtons.Add(ev.MouseButton); break;
                case MacroEventKind.MouseUp: heldButtons.Remove(ev.MouseButton); break;
            }
        }
    }

    // ---------- helpers ----------

    private static ((int X, int Y) Recorded, ColorCheck? Check) Resolve(PointStep p, StepContext ctx)
    {
        var adj = ctx.Adjust(p.Id);
        var check = p.Check;
        if (check is not null && adj is not null)
            check = check with { Expect = adj.Expect ?? check.Expect, Other = adj.Other ?? check.Other };
        return (adj is null ? (p.X, p.Y) : (adj.X, adj.Y), check);
    }

    private static (int X, int Y) Place(StepContext ctx, (int X, int Y) recorded)
        => PointMath.Place(recorded, ctx.RecordedClient, ctx.ActualClient);
}
```

- [ ] **Step 5: Run to verify it passes**

Run: `dotnet test tests/rororo-ur-task.Tests/rororo-ur-task.Tests.csproj -p:StandaloneTestsOnly=true --filter FullyQualifiedName~StepRunnerTests`
Expected: all pass. The exact report string in `A_check_that_never_matches_stops_with_a_readable_report` is the contract Ur MCP shows to Claude; fix the code, not the string, if they differ (the distance between `#8BE03A` and `#141E5A` is 229.8, printed as 230).

- [ ] **Step 6: Run the full suite, then commit**

Run: `dotnet test tests/rororo-ur-task.Tests/rororo-ur-task.Tests.csproj -p:StandaloneTestsOnly=true`
Expected: all pass.

```bash
git add src/Macros/Steps/StepRunner.cs src/Macros/MacroPlayer.cs tests/rororo-ur-task.Tests/Steps/StepRunnerTests.cs
git commit -m "feat(steps): the step runner — wait for the colour, search nearby, first match, readable stops"
```

---

### Task 7: Playing steps from `MacroPlayer`

**Files:**
- Create: `src/Macros/Steps/RealStepIo.cs`
- Modify: `src/Macros/MacroPlayer.cs`, `src/Macros/SequenceTypes.cs`, `src/Macros/SequencePlayer.cs`
- Test: `tests/rororo-ur-task.Tests/Steps/StepPlaybackTests.cs`

**Interfaces:**
- Consumes: `StepRunner`, `IStepIo`, `StepContext`, `PointMath`, `PointAdjustmentStore`, `IScreenSampler`, `IDisplayScale`.
- Produces: `MacroPlayer(IForegroundWatcher foreground, IWindowMetrics metrics, IScreenSampler? sampler = null, IDisplayScale? displayScale = null, PointAdjustmentStore? adjustments = null)`; `internal static string SizeRefusalText((int W, int H) wanted, (int W, int H)? got, int scalePercent)`; `AltOutcome(..., int? StepIndex = null)`.

- [ ] **Step 1: Write the failing tests**

```csharp
// tests/rororo-ur-task.Tests/Steps/StepPlaybackTests.cs
using Labs626.UrTask.Macros;
using Labs626.UrTask.Macros.Steps;

namespace Labs626.UrTask.Tests.Steps;

public class StepPlaybackTests
{
    [Fact]
    public void Window_came_out_larger_names_the_display_scale_and_roblox_minimum()
    {
        var text = MacroPlayer.SizeRefusalText((800, 599), (1002, 750), 125);
        Assert.Equal("Roblox wouldn't shrink this window to 800x599; it stayed 1002x750. At 125% display scale Roblox's smallest window is 1020x797. Record this macro at 125%, or play it on a PC set to the scale it was recorded at.", text);
    }

    [Fact]
    public void Window_came_out_smaller_keeps_todays_advice()
    {
        var text = MacroPlayer.SizeRefusalText((1718, 1360), (1718, 1300), 100);
        Assert.StartsWith("Couldn't size this window to the macro's recorded 1718x1360 (got 1718x1300).", text);
    }

    [Fact]
    public void Alt_outcome_carries_the_step_index()
    {
        var o = new AltOutcome(new Labs626.UrTask.PluginHost.AccountRegistry.AccountInfo(1001, 1, "alt-1", "acct-1"), PlaybackOutcome.Aborted, "x", 3);
        Assert.Equal(3, o.StepIndex);
    }
}
```

- [ ] **Step 2: Run to verify it fails**

Run: `dotnet test tests/rororo-ur-task.Tests/rororo-ur-task.Tests.csproj -p:StandaloneTestsOnly=true --filter FullyQualifiedName~StepPlaybackTests`
Expected: build errors.

- [ ] **Step 3: Carry the step index through the sequence**

In `src/Macros/SequenceTypes.cs` change `AltOutcome` to:

```csharp
public sealed record AltOutcome(
    AccountRegistry.AccountInfo Alt,
    PlaybackOutcome Outcome,                     // Completed | Refused | Aborted | Skipped (sequence aborted)
    string? Reason,
    int? StepIndex = null);                      // 0-based step that stopped a v4 macro
```

In `src/Macros/SequencePlayer.cs`, the line `perAlt.Add(new AltOutcome(target, playResult.Outcome, playResult.Reason));` becomes:

```csharp
                perAlt.Add(new AltOutcome(target, playResult.Outcome, playResult.Reason, playResult.StepIndex));
```

- [ ] **Step 4: Make the input helpers reachable and add relative movement**

In `src/Macros/MacroPlayer.cs`: change `private static void SendMacroEvent(MacroEvent evt)` to `internal static void SendMacroEvent(MacroEvent evt)`, and add below `SendMouseMove`:

```csharp
    /// <summary>Relative movement (no ABSOLUTE flag): what Roblox reads as camera turn while it
    /// holds the pointer in first person or shift-lock.</summary>
    internal static void SendMouseRelative(int dx, int dy)
    {
        var input = new INPUT
        {
            type = INPUT_MOUSE,
            union = new InputUnion { mouse = new MOUSEINPUT { dx = dx, dy = dy, dwFlags = MOUSEEVENTF_MOVE } },
        };
        SendOne(ref input);
    }
```

Also make `GetCurrentCursorPos` `internal static`.

- [ ] **Step 5: Implement `RealStepIo`**

```csharp
// src/Macros/Steps/RealStepIo.cs
using Labs626.UrTask.PluginHost;

namespace Labs626.UrTask.Macros.Steps;

/// <summary>IStepIo over the real window: client→screen at send time, so a window moved
/// mid-playback stays correct (the same rule as the v3 event path).</summary>
internal sealed class RealStepIo : IStepIo
{
    private readonly IntPtr _hwnd;
    private readonly IWindowMetrics _metrics;
    private readonly IScreenSampler _sampler;
    private readonly IForegroundWatcher _foreground;
    private readonly long _targetUserId;
    private readonly System.Diagnostics.Stopwatch _clock = System.Diagnostics.Stopwatch.StartNew();

    public RealStepIo(IntPtr hwnd, IWindowMetrics metrics, IScreenSampler sampler, IForegroundWatcher foreground, long targetUserId)
        => (_hwnd, _metrics, _sampler, _foreground, _targetUserId) = (hwnd, metrics, sampler, foreground, targetUserId);

    public long NowMs => _clock.ElapsedMilliseconds;

    public bool Send(MacroEvent e)
    {
        if (e.Kind is MacroEventKind.KeyDown or MacroEventKind.KeyUp) { MacroPlayer.SendMacroEvent(e); return true; }
        var origin = _metrics.ClientOrigin(_hwnd);
        if (origin is null) return false;
        var (sx, sy) = WindowSpaceMath.ToScreen((e.X, e.Y), origin.Value);
        MacroPlayer.SendMacroEvent(e with { X = sx, Y = sy });
        return true;
    }

    public bool MoveRelative(int dx, int dy) { MacroPlayer.SendMouseRelative(dx, dy); return true; }

    public (int X, int Y)? CursorClient()
    {
        var origin = _metrics.ClientOrigin(_hwnd);
        if (origin is null) return null;
        var (x, y) = MacroPlayer.GetCurrentCursorPos();
        return WindowSpaceMath.ToClient((x, y), origin.Value);
    }

    public (int W, int H)? ClientSize() => _metrics.ClientSize(_hwnd) is { W: > 0, H: > 0 } s ? s : null;

    public PixelBlock? Capture(int x, int y, int w, int h) => _sampler.Capture(_hwnd, x, y, w, h);

    public bool TargetInForeground() => _foreground.ResolveForegroundAccount()?.RobloxUserId == _targetUserId;

    public Task Delay(int ms, CancellationToken ct) => ms <= 0 ? Task.CompletedTask : Task.Delay(ms, ct);
}
```

- [ ] **Step 6: Route step macros and generalise the size check**

In `MacroPlayer`:

1. Constructor:

```csharp
    private readonly IScreenSampler _sampler;
    private readonly IDisplayScale _displayScale;
    private readonly PointAdjustmentStore _adjustments;

    public MacroPlayer(IForegroundWatcher foreground, IWindowMetrics metrics,
        IScreenSampler? sampler = null, IDisplayScale? displayScale = null, PointAdjustmentStore? adjustments = null)
    {
        _foreground = foreground ?? throw new ArgumentNullException(nameof(foreground));
        _metrics = metrics ?? throw new ArgumentNullException(nameof(metrics));
        _sampler = sampler ?? new ScreenSampler(metrics);
        _displayScale = displayScale ?? new DisplayScale();
        _adjustments = adjustments ?? new PointAdjustmentStore(PointAdjustmentStore.DefaultPath());
    }
```

Add `using Labs626.UrTask.Macros.Steps;` at the top.

2. In `PlayAsync`, directly after the foreground pre-flight (after the `preflight.RobloxUserId != targetUserId` refusal), insert:

```csharp
        if (macro.HasSteps)
            return await PlayStepsAsync(macro, preflight, targetUserId, external).ConfigureAwait(false);
```

3. Replace `EnsureClientSize(IntPtr hwnd, Macro macro)` with a version taking the target, slop and scale. Keep the v3 call site's behaviour identical by passing the recorded size, slop 2 and the current scale:

```csharp
    private PlaybackResult? EnsureClientSize(IntPtr hwnd, Macro macro)
    {
        if (macro.RecordedClientW is not int rw || macro.RecordedClientH is not int rh)
            return PlaybackResult.Refused("Client-space macro is missing its recorded client size — re-record it.");
        return EnsureClientSize(hwnd, macro, (rw, rh), slop: 2, _displayScale.ScalePercentFor(hwnd));
    }

    private PlaybackResult? EnsureClientSize(IntPtr hwnd, Macro macro, (int W, int H) target, int slop, int scalePercent)
```

Inside the new overload, replace every use of `rw`/`rh` with `target.W`/`target.H` and `Slop` with `slop`. Keep `MaximizeAndLeave(hwnd, target.W, target.H, slop)`. Replace the final refusal with:

```csharp
        return PlaybackResult.Refused(SizeRefusalText(target, after, scalePercent));
```

4. Add:

```csharp
    /// <summary>Refusal wording. When the window came out larger than asked, the cause is Roblox's
    /// scaled minimum size, not a smaller screen (display-scale findings, proposal 4).</summary>
    internal static string SizeRefusalText((int W, int H) wanted, (int W, int H)? got, int scalePercent)
    {
        if (got is { } g && g.W >= wanted.W && g.H >= wanted.H)
        {
            var min = PointMath.RobloxMinOuter(scalePercent);
            return $"Roblox wouldn't shrink this window to {wanted.W}x{wanted.H}; it stayed {g.W}x{g.H}. " +
                   $"At {scalePercent}% display scale Roblox's smallest window is {min.W}x{min.H}. " +
                   $"Record this macro at {scalePercent}%, or play it on a PC set to the scale it was recorded at.";
        }
        return $"Couldn't size this window to the macro's recorded {wanted.W}x{wanted.H} (got {got?.W}x{got?.H}). It looks recorded on a larger screen — move the window fully on-screen and retry, or re-record the macro on this monitor.";
    }

    private async Task<PlaybackResult> PlayStepsAsync(Macro macro, AccountRegistry.AccountInfo preflight, long targetUserId, CancellationToken external)
    {
        if (!macro.IsClientSpace || macro.RecordedClientW is not int rw || macro.RecordedClientH is not int rh)
            return PlaybackResult.Refused("Point macros need a window-relative recording with its size — re-record it.");

        var hwnd = _metrics.HwndForPid(preflight.Pid);
        if (hwnd == IntPtr.Zero) return PlaybackResult.Refused("Target window handle unavailable.");

        var scale = _displayScale.ScalePercentFor(hwnd);
        var target = PointMath.TargetClientSize((rw, rh), macro.RecordedDisplayScale, scale);
        var slop = macro.RecordedDisplayScale is int rs && rs != scale ? 6 : 2;
        if (macro.RecordedDisplayScale is int r0 && r0 != scale)
            Diagnostics.DiagLog.Write($"Display scale differs: recorded at {r0}%, playing at {scale}% — scaling to {target.W}x{target.H}.");
        var refusal = EnsureClientSize(hwnd, macro, target, slop, scale);
        if (refusal is not null) return refusal;
        var actual = _metrics.ClientSize(hwnd) ?? target;

        _activeCts = CancellationTokenSource.CreateLinkedTokenSource(external);
        Started?.Invoke(this, new PlaybackStartedArgs(macro, preflight, targetUserId));
        try
        {
            var ctx = new StepContext(preflight.DisplayName, targetUserId, macro.Id, (rw, rh), actual, scale,
                pointId => _adjustments.Get(macro.Id, pointId, targetUserId, scale),
                line => Diagnostics.DiagLog.Write($"{preflight.DisplayName}: {line}"));
            var io = new RealStepIo(hwnd, _metrics, _sampler, _foreground, targetUserId);
            return await StepRunner.RunAsync(macro.Steps!, ctx, io, _activeCts.Token).ConfigureAwait(false);
        }
        finally
        {
            Ended?.Invoke(this, new PlaybackEndedArgs(macro));
            _activeCts?.Dispose();
            _activeCts = null;
        }
    }
```

- [ ] **Step 7: Run the tests and the full suite**

Run: `dotnet test tests/rororo-ur-task.Tests/rororo-ur-task.Tests.csproj -p:StandaloneTestsOnly=true`
Expected: all pass, including every `MacroPlayerClientSpaceTests` case (the v3 size path is unchanged; its refusal text only differs when the window came out larger, which no existing test exercises — if one does, it now asserts the new wording on purpose; update that assertion and say so in the commit body).

- [ ] **Step 8: Commit**

```bash
git add src/Macros/Steps/RealStepIo.cs src/Macros/MacroPlayer.cs src/Macros/SequenceTypes.cs src/Macros/SequencePlayer.cs tests/rororo-ur-task.Tests/Steps/StepPlaybackTests.cs
git commit -m "feat(player): play point macros through the step runner, scaled to the display"
```

---

### Task 8: Recordings save as points

**Files:**
- Create: `src/Macros/Steps/RecordingFinalizer.cs`
- Modify: `src/PluginRuntime.cs`
- Test: `tests/rororo-ur-task.Tests/Steps/RecordingFinalizerTests.cs`

**Interfaces:**
- Consumes: `StepConverter`, `Macro`, `IDisplayScale`.
- Produces: `RecordingFinalizer.WithSteps(Macro recording, int? displayScale) : Macro`. Plan 2's "Convert to points" button calls the same method.

- [ ] **Step 1: Write the failing tests**

```csharp
// tests/rororo-ur-task.Tests/Steps/RecordingFinalizerTests.cs
using Labs626.UrTask.Macros;
using Labs626.UrTask.Macros.Steps;

namespace Labs626.UrTask.Tests.Steps;

public class RecordingFinalizerTests
{
    private static Macro Rec(string coordSpace, params MacroEvent[] evs) => new(
        Macro.CurrentSchemaVersion, Guid.NewGuid().ToString(), "r", "PerWindow", 1, "a", null, 1, evs,
        CoordSpace: coordSpace, RecordedClientW: 800, RecordedClientH: 599);

    private static readonly MacroEvent Down = new(100, MacroEventKind.MouseDown, 0, 5, 5, 1, 0);
    private static readonly MacroEvent Up = new(180, MacroEventKind.MouseUp, 0, 5, 5, 1, 0);
    private static readonly MacroEvent KeyA = new(10, MacroEventKind.KeyDown, 0x41, 0, 0, 0, 0);

    [Fact]
    public void A_client_recording_with_clicks_gains_steps_and_scale()
    {
        var m = RecordingFinalizer.WithSteps(Rec(Macro.CoordSpaceClient, Down, Up), 125);
        Assert.True(m.HasSteps);
        Assert.Equal(125, m.RecordedDisplayScale);
        Assert.Equal(2, m.Events.Count); // the original is kept
    }

    [Fact]
    public void Keyboard_only_recordings_stay_on_the_event_path()
    {
        // Client space on purpose: the screen-space guard must not be what keeps it off the step
        // path. It is the "no mouse press" rule.
        var m = RecordingFinalizer.WithSteps(Rec(Macro.CoordSpaceClient, KeyA), 100);
        Assert.False(m.HasSteps);
        Assert.Null(m.RecordedDisplayScale);
    }

    [Fact]
    public void Screen_space_recordings_are_left_alone()
    {
        var m = RecordingFinalizer.WithSteps(Rec(Macro.CoordSpaceScreen, Down, Up), 100);
        Assert.False(m.HasSteps);
    }

    [Fact]
    public void Converting_twice_does_not_rename_points()
    {
        var once = RecordingFinalizer.WithSteps(Rec(Macro.CoordSpaceClient, Down, Up), 100);
        var twice = RecordingFinalizer.WithSteps(once, 100);
        Assert.Same(once.Steps, twice.Steps);
    }
}
```

- [ ] **Step 2: Run to verify it fails**

Run: `dotnet test tests/rororo-ur-task.Tests/rororo-ur-task.Tests.csproj -p:StandaloneTestsOnly=true --filter FullyQualifiedName~RecordingFinalizerTests`
Expected: build error.

- [ ] **Step 3: Implement**

```csharp
// src/Macros/Steps/RecordingFinalizer.cs
namespace Labs626.UrTask.Macros.Steps;

/// <summary>Turns a window-relative recording with clicks into a v4 point macro, keeping the
/// original events. Keyboard-only and screen-space recordings are returned unchanged: the
/// keyboard round-robin path is the dominant use and must not move.</summary>
public static class RecordingFinalizer
{
    public static Macro WithSteps(Macro recording, int? displayScale)
    {
        if (recording.HasSteps) return recording;
        if (!recording.IsClientSpace) return recording;
        if (!recording.Events.Any(e => e.Kind == MacroEventKind.MouseDown)) return recording;
        var steps = StepConverter.Convert(recording.Events);
        if (steps.Count == 0) return recording;
        return recording with
        {
            SchemaVersion = Macro.CurrentSchemaVersion,
            Steps = steps,
            RecordedDisplayScale = displayScale ?? recording.RecordedDisplayScale,
        };
    }
}
```

- [ ] **Step 4: Record the display scale and convert at save**

In `src/PluginRuntime.cs`:

1. Beside `private bool? _recordingMaximized;` add `private int? _recordingDisplayScale;` and a field `private readonly IDisplayScale _displayScale = new DisplayScale();` (add `using Labs626.UrTask.Macros.Steps;` if missing).
2. Beside line `_recordingMaximized = anchorHwnd != IntPtr.Zero ? _metrics.IsMaximized(anchorHwnd) : null;` add:

```csharp
            _recordingDisplayScale = anchorHwnd != IntPtr.Zero ? _displayScale.ScalePercentFor(anchorHwnd) : null;
```

3. Keep `Store.Save(macro);` (the save after `var macro = new Macro(...)`) exactly where it is, so the raw recording is on disk first. Directly after it, add:

```csharp
            // Convert only after the raw recording is safe on disk. A converter bug must never
            // cost a recording: on failure the v3 file stays, and the log says why.
            try
            {
                var converted = RecordingFinalizer.WithSteps(macro, isClientSpace ? _recordingDisplayScale : null);
                if (!ReferenceEquals(converted, macro))
                {
                    Store.Save(converted);
                    macro = converted;
                }
            }
            catch (Exception ex)
            {
                Log($"Kept as a plain recording; converting it to points failed: {ex.Message}");
            }
            _recordingDisplayScale = null;
```

`macro` is declared with `var`, so it is reassignable. The later `_lastMacro = macro` and `PromptRename(macro)` then see the converted macro when there is one. Then extend the "Saved macro" log line so it says what happened:

```csharp
            Log(macro.HasSteps
                ? $"Saved macro: {macro.Steps!.Count} steps from {events.Count} events, about {macro.Duration.TotalSeconds:F1}s, at {macro.RecordedDisplayScale}% display scale."
                : $"Saved macro: {events.Count} events, duration {macro.Duration.TotalSeconds:F1}s.");
```

- [ ] **Step 5: Run the tests and build**

Run: `dotnet build rororo-ur-task.csproj` then `dotnet test tests/rororo-ur-task.Tests/rororo-ur-task.Tests.csproj -p:StandaloneTestsOnly=true`
Expected: clean build, all pass.

- [ ] **Step 6: Commit**

```bash
git add src/Macros/Steps/RecordingFinalizer.cs src/PluginRuntime.cs tests/rororo-ur-task.Tests/Steps/RecordingFinalizerTests.cs
git commit -m "feat(recorder): save click recordings as point macros with their display scale"
```

---

### Task 9: `GetPlayback` on the action bridge

**Files:**
- Create: `src/Ipc/PlaybackRegistry.cs`
- Modify: `src/Ipc/BridgeContract.cs`, `src/Ipc/IMacroRunInvoker.cs`, `src/Ipc/MacroRunInvoker.cs`, `src/Ipc/MacroRunnerServer.cs`, `tests/rororo-ur-task.Tests/Ipc/MacroRunnerServerTests.cs` (FakeInvoker), `tests/rororo-ur-task.Tests/MacroRunnerServerPipeBusyTests.cs` (NoopInvoker)
- Test: `tests/rororo-ur-task.Tests/Ipc/PlaybackRegistryTests.cs`, additions to `tests/rororo-ur-task.Tests/Ipc/MacroRunInvokerTests.cs`

**Interfaces:**
- Consumes: `SequenceResult`, `AltOutcome.StepIndex` (set only by a failed check, per Task 6's `PlaybackResult.AbortedAt`, so a non-null index alone means `check-failed`).
- Produces: `GetPlaybackRequest(string ContractVersion, string Method, string? PlaybackId, string? CallerPluginId)`; `GetPlaybackResponse(bool Ok, string? State, string? Reason, string? Detail, int? StepIndex)`; `BridgeContract.MethodGetPlayback = "GetPlayback"`; `enum PlaybackState { Running, Finished, Stopped, Failed }`; `PlaybackRegistry` with `Started(id)`, `Finished(id, PlaybackState, string? reason, string? detail, int? stepIndex)`, `Get(string? id) : GetPlaybackResponse`, `static TimeSpan Retention`; `IMacroRunInvoker.GetPlayback(GetPlaybackRequest)`.

- [ ] **Step 1: Write the failing tests**

```csharp
// tests/rororo-ur-task.Tests/Ipc/PlaybackRegistryTests.cs
using Labs626.UrTask.Ipc;

namespace Labs626.UrTask.Tests.Ipc;

public class PlaybackRegistryTests
{
    private DateTimeOffset _now = new(2026, 9, 27, 15, 0, 0, TimeSpan.Zero);

    [Fact]
    public void Reports_running_then_failed_with_the_sentence()
    {
        var reg = new PlaybackRegistry(() => _now);
        reg.Started("a");
        Assert.Equal("running", reg.Get("a").State);
        reg.Finished("a", PlaybackState.Failed, "check-failed", "CElCPapa: step 3 'Teleport opener' expected green.", 2);
        var r = reg.Get("a");
        Assert.True(r.Ok);
        Assert.Equal(("failed", "check-failed", 2), (r.State, r.Reason, r.StepIndex));
        Assert.StartsWith("CElCPapa: step 3", r.Detail);
    }

    [Fact]
    public void Finished_playbacks_expire_after_ten_minutes()
    {
        var reg = new PlaybackRegistry(() => _now);
        reg.Started("a");
        reg.Finished("a", PlaybackState.Finished, null, null, null);
        _now += TimeSpan.FromMinutes(9);
        Assert.True(reg.Get("a").Ok);
        _now += TimeSpan.FromMinutes(2);
        var gone = reg.Get("a");
        Assert.False(gone.Ok);
        Assert.Equal("unknown-playback", gone.Reason);
        Assert.Equal("No playback with id 'a'. Finished playbacks are kept for 10 minutes.", gone.Detail);
    }

    [Fact]
    public void Running_playbacks_never_expire()
    {
        var reg = new PlaybackRegistry(() => _now);
        reg.Started("a");
        _now += TimeSpan.FromHours(3);
        Assert.Equal("running", reg.Get("a").State);
    }

    [Fact]
    public void A_missing_id_is_refused_with_a_sentence()
    {
        var r = new PlaybackRegistry(() => _now).Get(null);
        Assert.False(r.Ok);
        Assert.Equal("Name the playback id that RunMacro returned.", r.Detail);
    }
}
```

Add to `tests/rororo-ur-task.Tests/Ipc/MacroRunInvokerTests.cs`, which already has `NewMacro(string id)` and `Alt(long userId)`:

```csharp
    private static MacroRunInvoker BuildWithResult(Macro m, AccountRegistry.AccountInfo alt,
        Func<Macro, IReadOnlyList<AccountRegistry.AccountInfo>, int?, CancellationToken, Task<SequenceResult?>> playWithResult)
        => new MacroRunInvoker(
            loadMacros: () => new[] { m },
            snapshot: () => new[] { alt },
            resolveForegroundUserId: () => alt.RobloxUserId,
            isBusy: () => false,
            playWithResult: playWithResult);

    private static async Task WaitUntilAsync(Func<bool> cond)
    {
        for (int i = 0; i < 200 && !cond(); i++) await Task.Delay(10);
        Assert.True(cond());
    }

    [Fact]
    public async Task GetPlayback_reports_a_check_failure_and_repeat_stops_there()
    {
        var m = NewMacro(Guid.NewGuid().ToString());
        var alt = Alt(123);
        int passes = 0;
        var failed = new SequenceResult(new[]
        {
            new AltOutcome(alt, PlaybackOutcome.Aborted, "alt-123: step 2 'Tile' expected green #8BE03A, saw grey #969696 (distance 90) after 3.4 s at 100%.", 1),
        }, 0, 1, 0, TimeSpan.FromSeconds(4));
        var inv = BuildWithResult(m, alt, (_, _, _, _) => { passes++; return Task.FromResult<SequenceResult?>(failed); });

        var run = await inv.RunAsync(new RunMacroRequest("1.0", "RunMacro", m.Id, new[] { "123" }, null, "626labs.ur-mcp", Repeat: true), default);
        await WaitUntilAsync(() => inv.ActivePlaybackCount == 0);

        var status = inv.GetPlayback(new GetPlaybackRequest("1.0", "GetPlayback", run.PlaybackId, "626labs.ur-mcp"));
        Assert.Equal(("failed", "check-failed", 2), (status.State, status.Reason, status.StepIndex)); // AltOutcome index 1 → wire step 2
        Assert.StartsWith("alt-123: step 2 'Tile'", status.Detail);
        Assert.Equal(1, passes);
    }

    [Fact]
    public async Task GetPlayback_reports_finished_for_a_clean_pass()
    {
        var m = NewMacro(Guid.NewGuid().ToString());
        var alt = Alt(123);
        var ok = new SequenceResult(new[] { new AltOutcome(alt, PlaybackOutcome.Completed, null) }, 1, 0, 0, TimeSpan.FromSeconds(1));
        var inv = BuildWithResult(m, alt, (_, _, _, _) => Task.FromResult<SequenceResult?>(ok));

        var run = await inv.RunAsync(new RunMacroRequest("1.0", "RunMacro", m.Id, new[] { "123" }, null, "626labs.ur-mcp"), default);
        await WaitUntilAsync(() => inv.ActivePlaybackCount == 0);
        Assert.Equal("finished", inv.GetPlayback(new GetPlaybackRequest("1.0", "GetPlayback", run.PlaybackId, "626labs.ur-mcp")).State);
    }

    [Fact]
    public async Task GetPlayback_reports_stopped_after_StopMacro()
    {
        var m = NewMacro(Guid.NewGuid().ToString());
        var alt = Alt(123);
        var inv = BuildWithResult(m, alt, async (_, _, _, ct) => { await Task.Delay(Timeout.Infinite, ct); return null; });

        var run = await inv.RunAsync(new RunMacroRequest("1.0", "RunMacro", m.Id, new[] { "123" }, null, "626labs.ur-mcp"), default);
        Assert.Equal("running", inv.GetPlayback(new GetPlaybackRequest("1.0", "GetPlayback", run.PlaybackId, "626labs.ur-mcp")).State);
        inv.StopMacro(new StopMacroRequest("1.0", "StopMacro", run.PlaybackId, null, "626labs.ur-mcp"));
        await WaitUntilAsync(() => inv.ActivePlaybackCount == 0);
        Assert.Equal("stopped", inv.GetPlayback(new GetPlaybackRequest("1.0", "GetPlayback", run.PlaybackId, "626labs.ur-mcp")).State);
    }

    [Fact]
    public async Task A_refused_alt_does_not_end_a_repeat()
    {
        // A refusal or foreground shift with no step index is not a failed check. Repeat keeps
        // looping exactly as in 0.8 until StopMacro, and the playback then reads "stopped".
        var m = NewMacro(Guid.NewGuid().ToString());
        var alt = Alt(123);
        int passes = 0;
        var refused = new SequenceResult(new[] { new AltOutcome(alt, PlaybackOutcome.Refused, "Foreground window is user 9.") }, 0, 1, 0, TimeSpan.Zero);
        var inv = BuildWithResult(m, alt, async (_, _, _, ct) =>
        {
            Interlocked.Increment(ref passes);
            await Task.Delay(5, ct);
            return refused;
        });

        var run = await inv.RunAsync(new RunMacroRequest("1.0", "RunMacro", m.Id, new[] { "123" }, null, "626labs.ur-mcp", Repeat: true), default);
        await WaitUntilAsync(() => Volatile.Read(ref passes) >= 3);
        inv.StopMacro(new StopMacroRequest("1.0", "StopMacro", run.PlaybackId, null, "626labs.ur-mcp"));
        await WaitUntilAsync(() => inv.ActivePlaybackCount == 0);
        Assert.Equal("stopped", inv.GetPlayback(new GetPlaybackRequest("1.0", "GetPlayback", run.PlaybackId, "626labs.ur-mcp")).State);
    }
```

- [ ] **Step 2: Run to verify it fails**

Run: `dotnet test tests/rororo-ur-task.Tests/rororo-ur-task.Tests.csproj -p:StandaloneTestsOnly=true --filter "FullyQualifiedName~PlaybackRegistryTests|FullyQualifiedName~MacroRunInvokerTests"`
Expected: build errors.

- [ ] **Step 3: Contract additions**

Append to `src/Ipc/BridgeContract.cs` (before `internal static class BridgeContract`):

```csharp
public sealed record GetPlaybackRequest(
    string ContractVersion,
    string Method,
    string? PlaybackId,
    string? CallerPluginId);

/// <summary>State is running | finished | stopped | failed. On failed, Reason is a short code
/// (check-failed, refused, aborted, error) and Detail is the sentence Claude shows.</summary>
public sealed record GetPlaybackResponse(
    bool Ok,
    string? State,
    string? Reason,
    string? Detail,
    int? StepIndex)
{
    public static GetPlaybackResponse Refused(string reason, string? detail = null) => new(false, null, reason, detail, null);
}
```

and inside `BridgeContract` add `public const string MethodGetPlayback = "GetPlayback";`.

- [ ] **Step 4: The registry**

```csharp
// src/Ipc/PlaybackRegistry.cs
using System.Collections.Concurrent;

namespace Labs626.UrTask.Ipc;

internal enum PlaybackState { Running, Finished, Stopped, Failed }

/// <summary>How each bridge playback ended. Finished entries are kept for <see cref="Retention"/>
/// so a caller polling after the fact can still read them.</summary>
internal sealed class PlaybackRegistry
{
    public static readonly TimeSpan Retention = TimeSpan.FromMinutes(10);

    private sealed record Entry(PlaybackState State, string? Reason, string? Detail, int? StepIndex, DateTimeOffset? EndedAt);

    private readonly Func<DateTimeOffset> _now;
    private readonly ConcurrentDictionary<string, Entry> _entries = new();

    public PlaybackRegistry(Func<DateTimeOffset>? now = null) => _now = now ?? (() => DateTimeOffset.UtcNow);

    public void Started(string id) => _entries[id] = new Entry(PlaybackState.Running, null, null, null, null);

    public void Finished(string id, PlaybackState state, string? reason, string? detail, int? stepIndex)
        => _entries[id] = new Entry(state, reason, detail, stepIndex, _now());

    public GetPlaybackResponse Get(string? id)
    {
        Prune();
        if (string.IsNullOrWhiteSpace(id))
            return GetPlaybackResponse.Refused("refused", "Name the playback id that RunMacro returned.");
        if (!_entries.TryGetValue(id, out var e))
            return GetPlaybackResponse.Refused("unknown-playback", $"No playback with id '{id}'. Finished playbacks are kept for 10 minutes.");
        return new GetPlaybackResponse(true, e.State.ToString().ToLowerInvariant(), e.Reason, e.Detail, e.StepIndex);
    }

    private void Prune()
    {
        var cutoff = _now() - Retention;
        foreach (var kv in _entries)
            if (kv.Value.EndedAt is { } t && t < cutoff) _entries.TryRemove(kv.Key, out _);
    }
}
```

- [ ] **Step 5: The invoker**

In `src/Ipc/IMacroRunInvoker.cs` add:

```csharp
    /// <summary>How a playback is going or how it ended (kept 10 minutes after it ends).</summary>
    GetPlaybackResponse GetPlayback(GetPlaybackRequest request);
```

In `src/Ipc/MacroRunInvoker.cs`:

1. **One play delegate.** Change the `_play` field's type to return the pass result:
   `private readonly Func<Macro, IReadOnlyList<AccountRegistry.AccountInfo>, int?, CancellationToken, Task<SequenceResult?>> _play;`. Also add `private readonly PlaybackRegistry _registry;`. There is no second delegate field.
2. **Main ctor.** Replace the test ctor with this one. It is the only ctor that assigns fields:

```csharp
    // Main ctor: the play delegate reports how each pass ended (production, and GetPlayback tests).
    internal MacroRunInvoker(
        Func<IReadOnlyList<Macro>> loadMacros,
        Func<IReadOnlyList<AccountRegistry.AccountInfo>> snapshot,
        Func<long?> resolveForegroundUserId,
        Func<bool> isBusy,
        Func<Macro, IReadOnlyList<AccountRegistry.AccountInfo>, int?, CancellationToken, Task<SequenceResult?>> playWithResult,
        Func<bool>? abort = null,
        PlaybackRegistry? registry = null)
    {
        _loadMacros = loadMacros;
        _snapshot = snapshot;
        _resolveForegroundUserId = resolveForegroundUserId;
        _isBusy = isBusy;
        _play = playWithResult;
        _abort = abort ?? (() => false);
        _registry = registry ?? new PlaybackRegistry();
    }

    // Test ctor, unchanged signature: fakes that only need to run. Adapts into the one delegate.
    internal MacroRunInvoker(
        Func<IReadOnlyList<Macro>> loadMacros,
        Func<IReadOnlyList<AccountRegistry.AccountInfo>> snapshot,
        Func<long?> resolveForegroundUserId,
        Func<bool> isBusy,
        Func<Macro, IReadOnlyList<AccountRegistry.AccountInfo>, int?, CancellationToken, Task> play,
        Func<bool>? abort = null)
        : this(loadMacros, snapshot, resolveForegroundUserId, isBusy,
               playWithResult: async (m, t, d, c) => { await play(m, t, d, c).ConfigureAwait(false); return null; },
               abort: abort)
    { }
```

3. **Production ctor.** In its `: this(...)` call, replace `play: (macro, targets, delay, ct) => player.PlayAsync(macro, targets, delay, ct),` with `playWithResult: async (macro, targets, delay, ct) => await player.PlayAsync(macro, targets, delay, ct),`.

Existing tests pass only `play:` (a named argument that exists only on the test ctor), so they keep working unchanged.
4. In `RunAsync`, directly before `_ = ObservePlaybackAsync(...)`, add `_registry.Started(playbackId);`.
5. Add:

```csharp
    public GetPlaybackResponse GetPlayback(GetPlaybackRequest request) => _registry.Get(request.PlaybackId);
```

6. Replace the body of `ObservePlaybackAsync`'s `try`/`catch`/`finally` with:

```csharp
        SequenceResult? last = null;
        var state = PlaybackState.Finished;
        string? reason = null, detail = null;
        int? stepIndex = null;
        try
        {
            do
            {
                last = await _play(macro, targets, interAltDelayMs, playbackCts.Token).ConfigureAwait(false);
                // Each pass reports its own ending; a later clean pass clears an earlier refusal.
                state = PlaybackState.Finished;
                reason = null; detail = null; stepIndex = null;
                // A StepIndex is set only by a failed check (PlaybackResult.AbortedAt), so it
                // alone means check-failed. Prefer it over any other failed alt in the pass.
                var failure = last?.PerAlt.FirstOrDefault(a => a.StepIndex is not null)
                           ?? last?.PerAlt.FirstOrDefault(a => a.Outcome is PlaybackOutcome.Aborted or PlaybackOutcome.Refused);
                if (failure is not null && !playbackCts.IsCancellationRequested)
                {
                    state = PlaybackState.Failed;
                    reason = failure.StepIndex is not null ? "check-failed"
                           : failure.Outcome == PlaybackOutcome.Refused ? "refused" : "aborted";
                    detail = failure.Reason;
                    stepIndex = failure.StepIndex + 1; // 1-based on the wire, same as "step N" in the detail
                    // A failed check ends a repeat: retrying it forever helps nobody. Only step
                    // macros can fail a check, so v3 repeat behaviour is unchanged: a refused or
                    // aborted v3 pass loops on as it did in 0.8.
                    if (failure.StepIndex is not null) break;
                }
            }
            while (repeat && !playbackCts.IsCancellationRequested);
            if (playbackCts.IsCancellationRequested) state = PlaybackState.Stopped;
        }
        catch (OperationCanceledException) { state = PlaybackState.Stopped; }
        catch (Exception ex) { state = PlaybackState.Failed; reason = "error"; detail = ex.Message; }
        finally
        {
            _registry.Finished(playbackId, state, reason, detail, stepIndex);
            _playbacks.TryRemove(playbackId, out _);
            playbackCts.Dispose();
        }
```

- [ ] **Step 6: Server dispatch and the test fakes**

In `MacroRunnerServer.DispatchAsync`, add a case before `default:`:

```csharp
            case BridgeContract.MethodGetPlayback:
            {
                var req = JsonSerializer.Deserialize<GetPlaybackRequest>(frame, BridgeContract.Json);
                if (req is null)
                    return Bytes(GetPlaybackResponse.Refused("refused", "Empty request."));
                if (string.IsNullOrWhiteSpace(req.CallerPluginId))
                    return Bytes(GetPlaybackResponse.Refused("refused", "Missing callerPluginId."));
                return Bytes(_invoker.GetPlayback(req));
            }
```

Add to `FakeInvoker` in `tests/rororo-ur-task.Tests/Ipc/MacroRunnerServerTests.cs` and to `NoopInvoker` in `tests/rororo-ur-task.Tests/MacroRunnerServerPipeBusyTests.cs`:

```csharp
        public GetPlaybackResponse GetPlayback(GetPlaybackRequest request)
            => new(true, "finished", null, null, null);
```

Add two dispatch tests to `MacroRunnerServerTests`, following that file's existing request helper:

- a `{"contractVersion":"1.0","method":"GetPlayback","playbackId":"x","callerPluginId":"t"}` frame returns `"state":"finished"`;
- **a pin for Ur MCP:** a frame with `"method":"NoSuchMethod"` returns `"ok":false`, `"reason":"refused"`, `"detail":"Unknown method 'NoSuchMethod'."` exactly. Ur MCP's `wait_for_macro` detects a pre-0.9 Ur Task by this detail; the test's comment should say so.

The existing `RunMacro`, `ListMacros` and `StopMacro` tests must still pass unmodified.

- [ ] **Step 7: Run everything**

Run: `dotnet test tests/rororo-ur-task.Tests/rororo-ur-task.Tests.csproj -p:StandaloneTestsOnly=true`
Expected: all pass.

- [ ] **Step 8: Commit**

```bash
git add src/Ipc tests/rororo-ur-task.Tests/Ipc tests/rororo-ur-task.Tests/MacroRunnerServerPipeBusyTests.cs
git commit -m "feat(bridge): GetPlayback reports how a playback ended; a failed check ends a repeat"
```

---

### Task 10: AutoHotkey export of point macros

**Files:**
- Modify: `src/Macros/AutoHotkeyExporter.cs`
- Test: `tests/rororo-ur-task.Tests/Steps/AutoHotkeyStepExportTests.cs`

**Interfaces:**
- Consumes: step records.
- Produces: `AutoHotkeyExporter.Export` handles `macro.HasSteps`.

- [ ] **Step 1: Write the failing tests**

```csharp
// tests/rororo-ur-task.Tests/Steps/AutoHotkeyStepExportTests.cs
using Labs626.UrTask.Macros;
using Labs626.UrTask.Macros.Steps;

namespace Labs626.UrTask.Tests.Steps;

public class AutoHotkeyStepExportTests
{
    private static Macro M(params MacroStep[] steps) => new(
        Macro.CurrentSchemaVersion, Guid.NewGuid().ToString(), "Mine Zone 8", "PerWindow", 1, "a", null, 1,
        Array.Empty<MacroEvent>(), CoordSpace: Macro.CoordSpaceClient, RecordedClientW: 800, RecordedClientH: 599,
        RecordedDisplayScale: 100, Steps: steps);

    private static readonly ColorCheck Green = new(new CheckBox(), new Rgb(139, 224, 58));

    [Fact]
    public void Points_export_as_clicks_with_their_delays()
    {
        var v1 = AutoHotkeyExporter.Export(M(new PointStep(1500, "p1", null, 472, 317)), AhkVersion.V1);
        Assert.Contains("CoordMode, Mouse, Client", v1);
        Assert.Contains("Sleep, 1500", v1);
        Assert.Contains("Click, 472, 317, Left", v1);

        var v2 = AutoHotkeyExporter.Export(M(new PointStep(1500, "p1", null, 472, 317)), AhkVersion.V2);
        Assert.Contains("Click \"472 317 Left\"", v2);
    }

    [Fact]
    public void Checks_and_first_match_export_as_comments()
    {
        var text = AutoHotkeyExporter.Export(M(
            new PointStep(0, "p2", "Teleport opener", 42, 398, Check: Green, CheckEnabled: true),
            new FirstMatchStep(0, "f1", "Best mine", new[] { new PointStep(0, "t8", "#8", 137, 390, Check: Green), new PointStep(0, "t7", "#7", 312, 390, Check: Green) })),
            AhkVersion.V1);
        Assert.Contains("; check: 'Teleport opener' expects #8BE03A here (Ur Task only)", text);
        Assert.Contains("; first match 'Best mine': Ur Task presses the first candidate whose colour matches; exported as '#8'", text);
        Assert.Contains("Click, 137, 390, Left", text);
    }

    [Fact]
    public void Keys_drags_and_camera_moves_export()
    {
        var text = AutoHotkeyExporter.Export(M(
            new KeyStep(0, 0x41, true), new KeyStep(3000, 0x41, false),
            new DragStep(0, 2, 400, 300, 0, 300, 400),
            new PointerMoveStep(0, 0, 200, 300)), AhkVersion.V1);
        Assert.Contains("Send, {vk41 down}", text);
        Assert.Contains("Sleep, 3000", text);
        Assert.Contains("MouseClickDrag, Right, 400, 300, 400, 600", text);
        Assert.Contains("DllCall(\"mouse_event\", \"UInt\", 1, \"Int\", 0, \"Int\", 200, \"UInt\", 0, \"UPtr\", 0)", text);
    }

    [Fact]
    public void V3_macros_export_exactly_as_before()
    {
        var v3 = M() with { Steps = null, Events = new[] { new MacroEvent(0, MacroEventKind.MouseDown, 0, 1, 2, 1, 0) } };
        Assert.Contains("Click, 1, 2, Left, , D", AutoHotkeyExporter.Export(v3, AhkVersion.V1));
    }
}
```

- [ ] **Step 2: Run to verify it fails**

Run: `dotnet test tests/rororo-ur-task.Tests/rororo-ur-task.Tests.csproj -p:StandaloneTestsOnly=true --filter FullyQualifiedName~AutoHotkeyStepExportTests`
Expected: the step tests fail (no step export yet); the v3 test passes.

- [ ] **Step 3: Implement**

In `AutoHotkeyExporter`:

1. `Export`: replace `AppendEvents(sb, macro, version);` with

```csharp
        if (macro.HasSteps) AppendSteps(sb, macro, version);
        else AppendEvents(sb, macro, version);
```

2. `AppendHeader`: after the caveat lines add

```csharp
        if (macro.HasSteps)
            sb.Append("; Exported from point steps. Colour checks and first-match choices run in Ur Task only and appear as comments.").Append(Nl);
```

3. `AppendDirectives`: change `hasMouseEvent` to also count steps: `var hasMouseEvent = macro.HasSteps || macro.Events.Any(...)` (keep the existing `Any` expression after `||`).

4. Add (the `using Labs626.UrTask.Macros.Steps;` goes at the top):

```csharp
    private static void AppendSteps(StringBuilder sb, Macro macro, AhkVersion version)
    {
        foreach (var step in macro.Steps!)
        {
            if (step.DelayMs > 0) sb.Append(Sleep(step.DelayMs, version)).Append(Nl);
            switch (step)
            {
                case KeyStep k:
                    sb.Append(FormatKey(k.VirtualKeyCode, k.Down, version)).Append(Nl);
                    break;
                case PointStep p:
                    if (p.CheckEnabled && p.Check is { } c)
                        sb.Append($"; check: '{p.Label ?? p.Id}' expects {c.Expect.Hex} here (Ur Task only)").Append(Nl);
                    sb.Append(ClickAt(p.X, p.Y, p.Button, version)).Append(Nl);
                    break;
                case FirstMatchStep f:
                    var first = f.Candidates[0];
                    sb.Append($"; first match '{f.Label ?? f.Id}': Ur Task presses the first candidate whose colour matches; exported as '{first.Label ?? first.Id}'").Append(Nl);
                    sb.Append(ClickAt(first.X, first.Y, first.Button, version)).Append(Nl);
                    break;
                case DragStep d:
                    var btn = ButtonName(d.Button);
                    sb.Append(version == AhkVersion.V1
                        ? FormattableString.Invariant($"MouseClickDrag, {btn}, {d.StartX}, {d.StartY}, {d.StartX + d.Dx}, {d.StartY + d.Dy}")
                        : FormattableString.Invariant($"MouseClickDrag \"{btn}\", {d.StartX}, {d.StartY}, {d.StartX + d.Dx}, {d.StartY + d.Dy}")).Append(Nl);
                    break;
                case WheelStep w:
                    sb.Append(version == AhkVersion.V1
                        ? FormattableString.Invariant($"MouseMove, {w.X}, {w.Y}")
                        : FormattableString.Invariant($"MouseMove {w.X}, {w.Y}")).Append(Nl);
                    sb.Append(FormatWheel(new MacroEvent(0, MacroEventKind.MouseWheel, 0, w.X, w.Y, 0, w.Delta), version)).Append(Nl);
                    break;
                case PointerMoveStep m:
                    sb.Append(FormattableString.Invariant($"DllCall(\"mouse_event\", \"UInt\", 1, \"Int\", {m.Dx}, \"Int\", {m.Dy}, \"UInt\", 0, \"UPtr\", 0)")).Append(Nl);
                    break;
                case RawStep r:
                    for (int i = 0; i < r.Events.Count; i++)
                    {
                        if (i > 0 && r.Events[i].TimestampMs > r.Events[i - 1].TimestampMs)
                            sb.Append(Sleep(r.Events[i].TimestampMs - r.Events[i - 1].TimestampMs, version)).Append(Nl);
                        AppendEvent(sb, r.Events[i], version);
                    }
                    break;
            }
        }
    }

    private static string Sleep(long ms, AhkVersion version)
        => version == AhkVersion.V1 ? FormattableString.Invariant($"Sleep, {ms}") : FormattableString.Invariant($"Sleep {ms}");

    private static string ClickAt(int x, int y, int button, AhkVersion version)
    {
        var btn = ButtonName(button);
        return version == AhkVersion.V1
            ? FormattableString.Invariant($"Click, {x}, {y}, {btn}")
            : FormattableString.Invariant($"Click \"{x} {y} {btn}\"");
    }
```

- [ ] **Step 4: Run to verify it passes, plus the existing exporter tests**

Run: `dotnet test tests/rororo-ur-task.Tests/rororo-ur-task.Tests.csproj -p:StandaloneTestsOnly=true`
Expected: all pass.

- [ ] **Step 5: Commit**

```bash
git add src/Macros/AutoHotkeyExporter.cs tests/rororo-ur-task.Tests/Steps/AutoHotkeyStepExportTests.cs
git commit -m "feat(export): AutoHotkey export of point macros, checks as comments"
```

---

### Task 11: Version, changelog, and the live pass on Dunder-MiffLan

**Files:**
- Modify: `rororo-ur-task.csproj`, `manifest.json`, `CHANGELOG.md`, `docs/BACKLOG.md`, `docs/display-scale-findings.md` (Step 5 adds the 125% results)

- [ ] **Step 1: Bump to 0.9.0**

Open `CHANGELOG.md`. If an unreleased section exists above `0.8.0`, fold this work into it and use the version it names; otherwise set `<Version>0.9.0</Version>` in `rororo-ur-task.csproj` and `"version": "0.9.0"` in `manifest.json`, and add at the top:

```markdown
## 0.9.0 — unreleased

### Added

- **Mouse recordings play as point clicks.** A recording with clicks saves as named points; the
  path between clicks is dropped and the time the mouse sat still is kept. The original
  recording stays in the file.
- **Points can check a colour before pressing.** A point waits for its colour to show, looks a
  few pixels around if the button moved, and otherwise stops with a sentence that says what it
  expected and what it saw.
- **First match.** One step checks a list of spots in order and presses the first that matches:
  the best unlocked mine, or Auto Mine only when it is off.
- **Display scale travels with the macro.** A macro recorded at 100% and played at 125% sizes the
  window and its points for 125%, and says so in the log.
- **`GetPlayback` on the action bridge.** A caller can ask how a playback is going or how it
  ended, for 10 minutes after it ends. A failed check ends a repeating playback.

### Changed

- When Roblox will not shrink a window to the size a macro wants, the refusal names the display
  scale and Roblox's smallest window instead of suggesting a larger screen.
```

Run the suite; `VersionConsistencyTests` must pass.

- [ ] **Step 2: Mark the backlog**

In `docs/BACKLOG.md`, under "Expose and drag a macro's click points", add a first line: `**Engine shipped in 0.9.0** (point steps, checks, first match, GetPlayback). The overlay is plan 2.` Under "Shared mouse macros ignore the Windows display-scale setting" add: `**Proposals 1–4 shipped in 0.9.0** for point macros. Proposal 5 (arrange floor) and 6 (exporter sizing helper) remain.`

- [ ] **Step 3: Build and install the plugin locally**

Run the branch build, not the installed plugin. This is the route in `docs/smoke/2026-08-11-timing-aware-cadence-smoke.md` (README's "Install" section only covers installing a published release by URL, so it is not the route here).

1. `dotnet build rororo-ur-task.csproj`. Never build the `.sln`.
2. Quit the installed Ur Task from its tray icon. RoRoRo autostarts it from `%LOCALAPPDATA%\ROROROblox\plugins\626labs.ur-task\`, and it holds the action-bridge pipe.
3. Launch `bin\Debug\net10.0-windows\626labs.ur-task.exe`.
4. Check that the log's first line reads `=== RoRoRo Ur Task v0.9.0 starting ===` (log: `%LOCALAPPDATA%\626Labs\RoRoRoUrTask\logs\ur-task.log`). If it shows 0.8.0, you are measuring the released build and the run is void.

- [ ] **Step 4: Live pass at 100% on Dunder-MiffLan** (manual; Este watches)

1. Record a short mouse macro on one alt: Go to Top, then the pickaxe. Check its JSON in `%LOCALAPPDATA%\626Labs\RoRoRoUrTask\macros\`: it has `"schemaVersion": 4`, `"recordedDisplayScale": 100`, a `steps` list of points, and the original `events`.
2. Play it on another alt through Ur MCP `run_macro`. It clicks the same buttons, visibly quicker than the recording because travel time is gone.
3. Author a first-match step in a copy of "Mine Zone 8" (agent-written JSON, per the spec): after the robot Teleport click, one `firstMatch` over tiles #8 to #1 at the brief's coordinates, each `expect` green and `other` grey. Sample both colours from a `tools/grid-capture.ps1` capture (average a 5x5 box in the PNG). Run it on all four accounts. Each lands at its best mine: estehernandez #8, CElCPapa #7, the other two #6.
4. Add a checked point on the Auto Mine dot at about (55, 288): `expect` red, `other` green, then the pickaxe press. Run it twice on one alt: the first run turns Auto Mine on; the second stops with a report saying it expected red and saw green, and Auto Mine stays on.
5. Call `GetPlayback` for each run (Ur MCP's tool if it has shipped, otherwise a raw pipe request). Finished runs say `finished`; the second Auto Mine run says `failed` with the sentence and step index.

- [ ] **Step 5: Live pass at 125%** (manual)

Switch Dunder-MiffLan to 125% (Settings > Display > Scale), sign out and back in if Roblox windows do not rescale, and play the step 1 macro and the step 3 first-match copy again. The log shows "Display scale differs: recorded at 100%, playing at 125%", the window sizes to about 1000x749 client, and the clicks land. Record what happened for each point in `docs/display-scale-findings.md` under a new "2026-09 point macros at 125%" heading: this answers test 4 of the 2026-09-27 handoff.

- [ ] **Step 6: Commit and open the PR**

```bash
git add rororo-ur-task.csproj manifest.json CHANGELOG.md docs/BACKLOG.md docs/display-scale-findings.md
git commit -m "chore: 0.9.0 — point clicks engine, live-checked at 100% and 125%"
git push -u origin feat/point-clicks
```

Open a PR against `main` with the changelog entry as the body, and tell the Ur MCP session the `GetPlayback` shape is live, noting deviation 4 (`reason` is a code, `detail` the sentence).
