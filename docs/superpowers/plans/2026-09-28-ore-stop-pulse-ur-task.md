# Ore Stop v1 Pulse, Ur Task Half, Implementation Plan

> **For agentic workers:** REQUIRED SUB-SKILL: Use superpowers:subagent-driven-development (recommended) or superpowers:executing-plans to implement this plan task-by-task. Steps use checkbox (`- [ ]`) syntax for tracking.

**Goal:** Give Ur Task a white-outline reach check on hold and point steps, report a playback that pressed nothing as GetPlayback finished/skipped, and generate the pulse-mode "Clear spot" macros from `measured.json`, shipped as 0.11.0.

**Architecture:** `OutlineCheck` is a new optional `Reach` on `HoldStep` and `PointStep`: a box relative to the step point (up to 120x120 at the recorded size), a `MinCount` and a `WhiteMin`. `PixelBlock.CountNearWhite` counts pixels whose every channel is at least `WhiteMin`; `PointMath.ScaledRect` and `PointMath.ScaledCount` scale the box and the count to the actual window like a point. `StepRunner` checks reach after the jump, polling up to `StepTiming.ReachGraceMs` for the outline to draw: no outline skips the step (logged, not a failure); during a hold, the outline gone for `StepTiming.HoldDriftPolls` polls running lets go with "outline gone". `generate.ps1` adds eight "Clear spot" macros (hold plus reach, no Auto Mine toggles) with new fixed ids, and puts reach on the existing "Mine spot" holds.

**Tech Stack:** C# / .NET 10 WPF (`net10.0-windows10.0.19041.0`), System.Text.Json polymorphic records, xUnit 2.9, PowerShell 7 (`pwsh`) for the generator.

**Spec:** `docs/superpowers/specs/2026-09-28-ore-stop-pulse-design.md` (sections "What it does" step 2, "The outline check", "Testing"). It amends `docs/superpowers/specs/2026-09-27-ore-stop-loop-design.md`, whose hold step, checked Auto Mine toggles and example macros (Ur Task 0.10.0, branch `feat/ore-stop`) this plan builds on.

## Global Constraints

- Outline check, verbatim from the spec: "count near-white pixels (each channel >= 225) in a block-sized rectangle around the spot, and pass when the count is at least a threshold." `WhiteMin` defaults to **225** and is a field.
- "The hold uses it twice: before pressing (no outline, no press) and during the hold (the outline gone means the block broke or went out of reach, so let go)."
- No outline before the press is **a skip, not a failure**: the playback continues with the next step and ends `Completed`. Log text contains `no outline, skipped`. A hold released for a missing outline logs `released: outline gone`.
- Outline gone during a hold needs `StepTiming.HoldDriftPolls` (2) polls running, same debounce as colour drift.
- The reach box is relative to the step point, like `CheckBox`, and **scales like points** (by actual over recorded client size). `OutlineCheck.MaxSide` is **120** at the recorded size.
- Ore is never abandoned for taking long (spec decision 5): example holds keep `maxMs` null.
- Pulse mode: the Ur OCR loop owns Auto Mine state. "Clear spot" macros never press the pickaxe.
- Example macro ids are fixed, never regenerated: existing `0e5a0000-0000-4000-8000-0000000000{01..04,11..18}`; new `Clear spot N..NW` are `0e5a0000-0000-4000-8000-0000000000{21..28}` in ring order N NE E SE S SW W NW.
- `measured.json` gains `ring.reach { measuredOn, minCount, whiteMin, w, h }`, a shared default that a spot may replace wholesale with its own `reach { minCount, whiteMin, w, h }`. It stays provisional (`measuredOn: null`) until the live measurement.
- Build the project, never the solution: `dotnet build rororo-ur-task.csproj`.
- Fast tests: `dotnet test tests/rororo-ur-task.Tests/rororo-ur-task.Tests.csproj -p:StandaloneTestsOnly=true`. Baseline on `feat/ore-stop`: **464 passed** with Ur Task closed. While a live Ur Task holds Ctrl+Shift+R, 2 `HotkeyServiceTests` fail for that reason alone; that is not a regression.
- Run every command from the repo root. Check with `git rev-parse --show-toplevel` after any `cd`.
- Commits: conventional commits, and every message ends with the line `Co-Authored-By: Claude Opus 5.5 <noreply@anthropic.com>`.
- No absolute user-profile paths (drive letter plus Users folder) in any committed file. The pre-commit local-path guard rejects them. `%LOCALAPPDATA%` is fine.
- No change to the RoRoRo host. The contract stays a NuGet `PackageReference`.
- **One additive bridge use (controller ruling, 2026-09-28):** a bridge playback that pressed nothing because reach checks skipped reports GetPlayback `{"ok":true,"state":"finished","reason":"skipped"}`. No new wire field, method or state: it fills the existing `reason` on a `finished` run, which Ur MCP ignores for finished, and the contract version stays 1.0. A normal finish keeps `reason` absent. "Pressed nothing" means no `MouseDown` and no `KeyDown` sent at all, with at least one reach skip. The ur-task.log ending line for it reads `playback finished (skipped: no outline): ...`.
- Macro JSON keeps the store's shape: camelCase properties, `"kind"` discriminator, boxes as exactly `{ "offsetX", "offsetY", "w", "h" }`. The new object is `"reach": { "box": {...}, "minCount": n, "whiteMin": n }`.
- Log and failure text uses invariant-culture numbers (`string.Create(CultureInfo.InvariantCulture, ...)`), one decimal for seconds.
- The example macros never press a captcha, the Enchant Machine or an invite popup. Every press is the Auto Mine pickaxe, Go to Top or a ring spot.
- Version: `rororo-ur-task.csproj` `<Version>` and `manifest.json` `"version"` both become `0.11.0` in the last task, with a CHANGELOG entry.

## Review Focus

1. **The outline draws a frame or two after the pointer lands.** A breakable block must not be skipped because the first sample beat the game's render. Pinned in Task 2 by `Reach_waits_its_grace_for_the_outline_to_draw` (the check polls for `ReachGraceMs` = 300 ms before skipping).
2. **One poll without the outline mid-hold** (the pickaxe swing or a hit particle crosses the frame): the hold must keep going. Pinned in Task 2 by `One_poll_without_the_outline_does_not_end_a_hold`.
3. **Bright lava rock with no white frame** (hot magenta, orange, pink, a warm near-white): nothing may be pressed, and the rest of the macro still runs. Pinned in Task 2 by `Lava_without_a_white_frame_skips_the_hold_and_the_next_step_runs`, and in Task 1 by the `whiteMin` boundary in `Counts_pixels_whose_every_channel_reaches_the_threshold`.
4. **The account loses focus or its window cannot be captured during the reach check**: that must end the playback as aborted or stopped, never read as "no outline, skipped" and carry on. Pinned in Task 2 by `Losing_the_foreground_during_the_reach_check_aborts_rather_than_skips` and `A_reach_check_that_cannot_see_the_window_stops_instead_of_skipping`.
5. **A window at 125% or with slack** (1000x749 for an 800x599 recording): the reach box and the count must scale with the point, or edge frames fall outside the box. Pinned in Task 1 by `A_reach_box_scales_like_points` and in Task 2 by `Reach_scales_with_the_window`.

---

## File map

| File | Change | Responsibility |
| --- | --- | --- |
| `src/Macros/Steps/OutlineCheck.cs` | create | `OutlineCheck` record: box, `MinCount`, `WhiteMin`, `MaxSide`, `DefaultWhiteMin` |
| `src/Macros/Steps/PixelBlock.cs` | modify | `CountNearWhite` |
| `src/Macros/Steps/PointMath.cs` | modify | `ScaledRect`, `ScaledCount` |
| `src/Macros/Steps/MacroStep.cs` | modify | `Reach` on `PointStep` and `HoldStep`; `StepTiming.ReachGraceMs`; validator rules |
| `src/Macros/AutoHotkeyExporter.cs` | modify | reach exports as a comment |
| `src/Macros/Steps/StepRunner.cs` | modify | reach before a hold's press and during it; reach on a point; `ClickAsync` split out of `PressAsync`; `RunTally` and `PressWatchIo` for a run that pressed nothing |
| `src/Macros/MacroPlayer.cs` | modify | `PlaybackResult.SkippedByReach`, `CompletedSkippedByReach()` |
| `src/Macros/SequenceTypes.cs`, `src/Macros/SequencePlayer.cs` | modify | `AltOutcome.SkippedByReach`, carried from the play result |
| `src/Macros/PlaybackEndLog.cs` | modify | `playback finished (skipped: no outline): ...` |
| `src/Ipc/MacroRunInvoker.cs`, `src/Ipc/BridgeContract.cs` | modify | reason `skipped` on a finished run when every alt was skipped by reach; doc comment |
| `docs/reference/events/macros/space-mine-ore-stop/measured.json` | modify | `ring.reach`, provisional |
| `docs/reference/events/macros/space-mine-ore-stop/generate.ps1` | modify | reach on every ring hold; `Clear spot <name>` macros with fixed ids |
| `docs/reference/events/macros/space-mine-ore-stop/macros/*.json` | regenerate | 8 modified Mine spot files, 8 new Clear spot files |
| `tests/rororo-ur-task.Tests/Steps/PixelBlockTests.cs` | modify | counting |
| `tests/rororo-ur-task.Tests/Steps/PointMathTests.cs` | modify | scaling |
| `tests/rororo-ur-task.Tests/Steps/MacroV4FileTests.cs` | modify | reach JSON, validator, estimate |
| `tests/rororo-ur-task.Tests/Steps/AutoHotkeyStepExportTests.cs` | modify | reach comment |
| `tests/rororo-ur-task.Tests/Steps/StepRunnerTests.cs` | modify | painted-frame playback tests for holds and points; `SkippedByReach` |
| `tests/rororo-ur-task.Tests/PlaybackEndLogTests.cs`, `tests/rororo-ur-task.Tests/SequencePlayerTests.cs` | modify | skip log lines; the flag through the sequence |
| `tests/rororo-ur-task.Tests/Ipc/MacroRunInvokerTests.cs`, `tests/rororo-ur-task.Tests/Ipc/MacroRunnerServerTests.cs` | modify | finished/skipped through the registry and the pipe; a normal finish has no reason |
| `tests/rororo-ur-task.Tests/Steps/OreStopExampleMacrosTests.cs` | replace | examples incl. Clear spot and reach, held to `measured.json` |
| `rororo-ur-task.csproj`, `manifest.json`, `CHANGELOG.md` | modify | 0.11.0 |

Running test totals (fast suite): 464 → Task 1: 473 → Task 2: 483 → Task 3: 486 → Task 4: 498 → Task 5: 499 → Task 6: 499.

---

### Task 0: Branch and baseline

**Files:** none changed except committing this plan.

**Interfaces:**
- Consumes: branch `feat/ore-stop` (Ur Task 0.10.0 built; head `f5f0cd8` "docs(spec): ore stop pulse approved" or later).
- Produces: branch `feat/ore-stop-pulse`, building clean, 464 tests passing, this plan committed.

- [ ] **Step 1: Check the working tree**

Run: `git rev-parse --show-toplevel` then `git status --short` then `git branch --show-current`
Expected: the repo root; branch `feat/ore-stop`; only untracked files (`.claude/626labs-context.md`, `.gitnexus/`, `AGENTS.md`, and this plan). Leave the first three untracked. If any tracked file shows as modified, stop and ask.

- [ ] **Step 2: Create the branch**

```bash
git switch -c feat/ore-stop-pulse feat/ore-stop
```

- [ ] **Step 3: Build and run the baseline**

Make sure Ur Task is not running (the tray icon is gone), then:

Run: `dotnet build rororo-ur-task.csproj`
Expected: `Build succeeded`, 0 errors.

Run: `dotnet test tests/rororo-ur-task.Tests/rororo-ur-task.Tests.csproj -p:StandaloneTestsOnly=true`
Expected: 464 passed, 0 failed. If the number differs, record the real baseline and use it for every later "Expected" count (add the same deltas).

- [ ] **Step 4: Commit the plan**

```bash
git add docs/superpowers/plans/2026-09-28-ore-stop-pulse-ur-task.md
git commit -m "docs(plan): ore stop pulse, Ur Task half

Co-Authored-By: Claude Opus 5.5 <noreply@anthropic.com>"
```

---

### Task 1: The reach check model: record, count, scaling, JSON, validator, timing, export

**Files:**
- Create: `src/Macros/Steps/OutlineCheck.cs`
- Modify: `src/Macros/Steps/PixelBlock.cs`
- Modify: `src/Macros/Steps/PointMath.cs`
- Modify: `src/Macros/Steps/MacroStep.cs`
- Modify: `src/Macros/AutoHotkeyExporter.cs`
- Test: `tests/rororo-ur-task.Tests/Steps/PixelBlockTests.cs`, `tests/rororo-ur-task.Tests/Steps/PointMathTests.cs`, `tests/rororo-ur-task.Tests/Steps/MacroV4FileTests.cs`, `tests/rororo-ur-task.Tests/Steps/AutoHotkeyStepExportTests.cs`

**Interfaces:**
- Consumes: `CheckBox(int OffsetX = -2, int OffsetY = -2, int W = 5, int H = 5)`, `HoldCheck(CheckBox Box, int Tolerance = 15)`, `PixelBlock`, `StepValidator.Validate(IReadOnlyList<MacroStep>) : string?`, `StepTiming.EstimateMs(MacroStep) : long` (all existing).
- Produces:
  - `public sealed record OutlineCheck(CheckBox Box, int MinCount, int WhiteMin = OutlineCheck.DefaultWhiteMin)` with `public const int DefaultWhiteMin = 225;` and `public const int MaxSide = 120;` (namespace `Labs626.UrTask.Macros.Steps`).
  - `PointStep(int DelayMs, string Id, string? Label, int X, int Y, int Button = 1, ColorCheck? Check = null, bool CheckEnabled = false, OutlineCheck? Reach = null)`.
  - `HoldStep(int DelayMs, string Id, string? Label, int X, int Y, int Button = 1, HoldCheck? Check = null, int? MaxMs = null, OutlineCheck? Reach = null)`.
  - `public int? PixelBlock.CountNearWhite(int x, int y, int w, int h, int min)`: null when the box is not fully inside the block.
  - `public static (int X, int Y, int W, int H) PointMath.ScaledRect((int X, int Y) point, CheckBox box, (int W, int H) recordedClient, (int W, int H) actualClient)`.
  - `public static int PointMath.ScaledCount(int count, (int W, int H) recordedClient, (int W, int H) actualClient)`.
  - `public const int StepTiming.ReachGraceMs = 300;`
  - Validator sentences (exact): `"{who} has a reach check with no box."`, `"{who} has an empty reach box."`, `"{who} has a reach box larger than 120x120."`, `"{who} has a reach minCount below 1."`, `"{who} has a reach minCount larger than its box, so it can never pass."`, `"{who} has a reach whiteMin outside 1 to 255."`, `"{name} has both a colour check and a reach check; use one."`, `"{name}: candidate '{label}' has a reach check, which a first match does not use."`

- [ ] **Step 1: Write the failing tests**

In `tests/rororo-ur-task.Tests/Steps/PixelBlockTests.cs`, add inside the class, before its final closing brace:

```csharp
    [Fact]
    public void Counts_pixels_whose_every_channel_reaches_the_threshold()
    {
        // 4x1 at client (10,20): exactly at the threshold, one channel short, pure white, lava pink.
        var px = new[] { Argb(225, 225, 225), Argb(224, 255, 255), Argb(255, 255, 255), Argb(255, 120, 200) };
        var block = new PixelBlock(10, 20, 4, 1, px);
        Assert.Equal(2, block.CountNearWhite(10, 20, 4, 1, 225));
        Assert.Equal(1, block.CountNearWhite(10, 20, 4, 1, 226));
        Assert.Equal(1, block.CountNearWhite(12, 20, 2, 1, 225));
    }

    [Fact]
    public void A_count_box_outside_the_block_is_null()
    {
        var block = new PixelBlock(10, 20, 4, 2, new uint[8]);
        Assert.Null(block.CountNearWhite(9, 20, 2, 2, 225));
        Assert.Null(block.CountNearWhite(13, 20, 2, 2, 225));
        Assert.Null(block.CountNearWhite(10, 20, 0, 2, 225));
    }
```

In `tests/rororo-ur-task.Tests/Steps/PointMathTests.cs`, add inside the class, before its final closing brace:

```csharp
    [Fact]
    public void A_reach_box_scales_like_points()
    {
        var box = new CheckBox(-40, -40, 80, 80);
        Assert.Equal((360, 204, 80, 80), PointMath.ScaledRect((400, 244), box, (800, 599), (800, 599)));
        // 800x599 recorded, 1000x749 actual: x by 1.25, y by 1.2504.
        Assert.Equal((450, 255, 100, 100), PointMath.ScaledRect((500, 305), box, (800, 599), (1000, 749)));
    }

    [Fact]
    public void A_reach_min_count_scales_with_the_smaller_side_and_never_below_one()
    {
        Assert.Equal(60, PointMath.ScaledCount(60, (800, 599), (800, 599)));
        Assert.Equal(75, PointMath.ScaledCount(60, (800, 599), (1000, 749)));
        Assert.Equal(47, PointMath.ScaledCount(60, (1000, 749), (800, 599))); // 60 * 0.7997, rounded down
        Assert.Equal(1, PointMath.ScaledCount(1, (800, 599), (400, 300)));
    }
```

In `tests/rororo-ur-task.Tests/Steps/MacroV4FileTests.cs`, add inside the class, before its final closing brace:

```csharp
    [Fact]
    public void Reach_round_trips_on_holds_and_points()
    {
        var store = new MacroStore(_dir);
        var m = Sample() with
        {
            Steps = new MacroStep[]
            {
                new HoldStep(0, "spot-N", "Spot N", 400, 244, Check: new HoldCheck(new CheckBox()),
                    Reach: new OutlineCheck(new CheckBox(-40, -40, 80, 80), 60)),
                new PointStep(0, "p1", "Ore", 456, 300, Reach: new OutlineCheck(new CheckBox(-30, -30, 60, 60), 25, 240)),
            },
        };
        store.Save(m);
        var json = File.ReadAllText(Path.Combine(_dir, m.Id + ".json"));
        Assert.Contains("\"reach\": {", json);
        Assert.Contains("\"minCount\": 60", json);
        Assert.Contains("\"whiteMin\": 225", json);

        var loaded = Assert.Single(store.LoadAll().Macros);
        var h = Assert.IsType<HoldStep>(loaded.Steps![0]);
        Assert.Equal(new OutlineCheck(new CheckBox(-40, -40, 80, 80), 60, 225), h.Reach);
        var p = Assert.IsType<PointStep>(loaded.Steps[1]);
        Assert.Equal(new OutlineCheck(new CheckBox(-30, -30, 60, 60), 25, 240), p.Reach);
    }

    [Fact]
    public void An_agent_written_reach_loads_with_its_default_whiteMin()
    {
        Directory.CreateDirectory(_dir);
        var id = Guid.NewGuid().ToString();
        File.WriteAllText(Path.Combine(_dir, id + ".json"), $$"""
        { "schemaVersion": 4, "id": "{{id}}", "recordedAtUnixMs": 1, "events": [],
          "coordSpace": "client", "recordedClientW": 800, "recordedClientH": 599,
          "steps": [ { "kind": "hold", "delayMs": 0, "id": "spot-N", "x": 400, "y": 244,
                       "check": { "box": { "offsetX": -2, "offsetY": -2, "w": 5, "h": 5 } },
                       "reach": { "box": { "offsetX": -40, "offsetY": -40, "w": 80, "h": 80 }, "minCount": 60 } } ] }
        """);
        var m = Assert.Single(new MacroStore(_dir).LoadAll().Macros);
        var h = Assert.IsType<HoldStep>(m.Steps![0]);
        Assert.Equal(OutlineCheck.DefaultWhiteMin, h.Reach!.WhiteMin);
        Assert.Equal(60, h.Reach.MinCount);
        Assert.Null(StepValidator.Validate(m.Steps));
    }

    [Fact]
    public void Validator_refuses_bad_reach_checks_with_a_sentence()
    {
        HoldStep H(OutlineCheck reach) => new(0, "spot-N", "Spot N", 400, 244, Check: new HoldCheck(new CheckBox()), Reach: reach);
        string? V(params MacroStep[] steps) => StepValidator.Validate(steps);
        var box = new CheckBox(-40, -40, 80, 80);

        Assert.Null(V(H(new OutlineCheck(box, 60))));
        Assert.Null(V(H(new OutlineCheck(new CheckBox(-60, -60, 120, 120), 14400, 1))));
        Assert.Equal("Step 1 'Spot N' has a reach check with no box.", V(H(new OutlineCheck(null!, 60))));
        Assert.Equal("Step 1 'Spot N' has an empty reach box.", V(H(new OutlineCheck(new CheckBox(0, 0, 0, 80), 60))));
        Assert.Equal("Step 1 'Spot N' has a reach box larger than 120x120.", V(H(new OutlineCheck(new CheckBox(0, 0, 121, 80), 60))));
        Assert.Equal("Step 1 'Spot N' has a reach minCount below 1.", V(H(new OutlineCheck(box, 0))));
        Assert.Equal("Step 1 'Spot N' has a reach minCount larger than its box, so it can never pass.", V(H(new OutlineCheck(new CheckBox(0, 0, 4, 4), 17))));
        Assert.Equal("Step 1 'Spot N' has a reach whiteMin outside 1 to 255.", V(H(new OutlineCheck(box, 60, 256))));
        Assert.Equal("Step 1 'Spot N' has a reach whiteMin outside 1 to 255.", V(H(new OutlineCheck(box, 60, 0))));

        var green = new ColorCheck(new CheckBox(), new Rgb(139, 224, 58));
        Assert.Null(V(new PointStep(0, "p1", "Ore", 400, 244, Reach: new OutlineCheck(box, 60))));
        Assert.Null(V(new PointStep(0, "p1", "Ore", 400, 244, Check: green, CheckEnabled: false, Reach: new OutlineCheck(box, 60))));
        Assert.Equal("Step 1 'Ore' has a reach minCount below 1.", V(new PointStep(0, "p1", "Ore", 400, 244, Reach: new OutlineCheck(box, 0))));
        Assert.Equal("Step 1 'Ore' has both a colour check and a reach check; use one.",
            V(new PointStep(0, "p1", "Ore", 400, 244, Check: green, CheckEnabled: true, Reach: new OutlineCheck(box, 60))));
        Assert.Equal("Step 1 'Best mine': candidate '#8' has a reach check, which a first match does not use.",
            V(new FirstMatchStep(0, "f1", "Best mine", new[] { new PointStep(0, "f1a", "#8", 1, 1, Check: green, Reach: new OutlineCheck(box, 60)) })));
    }

    [Fact]
    public void A_reach_check_does_not_change_the_estimate()
    {
        // The estimate is a floor: a skipped step can end sooner, a held one later.
        var reach = new OutlineCheck(new CheckBox(-40, -40, 80, 80), 60);
        Assert.Equal(StepTiming.EstimateMs(new HoldStep(300, "h", null, 1, 1)), StepTiming.EstimateMs(new HoldStep(300, "h", null, 1, 1, Reach: reach)));
        Assert.Equal(StepTiming.EstimateMs(new PointStep(300, "p", null, 1, 1)), StepTiming.EstimateMs(new PointStep(300, "p", null, 1, 1, Reach: reach)));
        Assert.Equal(300, StepTiming.ReachGraceMs);
    }
```

In `tests/rororo-ur-task.Tests/Steps/AutoHotkeyStepExportTests.cs`, add inside the class, before its final closing brace:

```csharp
    [Fact]
    public void Reach_exports_as_a_comment_on_holds_and_points()
    {
        var reach = new OutlineCheck(new CheckBox(-40, -40, 80, 80), 60);
        var text = AutoHotkeyExporter.Export(M(
            new HoldStep(0, "spot-N", "Spot N", 400, 244, Check: new HoldCheck(new CheckBox()), Reach: reach),
            new PointStep(0, "p1", "Two\nLines", 456, 300, Reach: reach)), AhkVersion.V1);

        Assert.Contains("; reach: 'Spot N' needs a white outline here (60 near-white px in 80x80); Ur Task skips the step without one (Ur Task only)\r\n; hold: 'Spot N'", text);
        Assert.Contains("; reach: 'Two Lines' needs a white outline here (60 near-white px in 80x80); Ur Task skips the step without one (Ur Task only)\r\nClick, 456, 300, Left", text);
    }
```

- [ ] **Step 2: Run the tests to verify they fail**

Run: `dotnet test tests/rororo-ur-task.Tests/rororo-ur-task.Tests.csproj -p:StandaloneTestsOnly=true --filter "FullyQualifiedName~PixelBlockTests|FullyQualifiedName~PointMathTests|FullyQualifiedName~MacroV4FileTests|FullyQualifiedName~AutoHotkeyStepExportTests"`
Expected: build FAILS with `CS0246: The type or namespace name 'OutlineCheck' could not be found` (and missing `CountNearWhite`, `ScaledRect`, `ScaledCount`, `ReachGraceMs`, `Reach`).

- [ ] **Step 3: Create `OutlineCheck`**

Create `src/Macros/Steps/OutlineCheck.cs`:

```csharp
namespace Labs626.UrTask.Macros.Steps;

/// <summary>
/// The white outline the game draws on a block the pickaxe can break, while the pointer is on it
/// (ore-stop pulse spec, "The outline check"). The line is 1-2 px thick, which an averaged box
/// cannot see, so this counts instead: pixels in <see cref="Box"/> whose every channel is at least
/// <see cref="WhiteMin"/>, passing at <see cref="MinCount"/> or more. The box is relative to the
/// step point like a <see cref="CheckBox"/>, may be block-sized, and scales with the window like a
/// point (the count scales too, see <see cref="PointMath.ScaledCount"/>).
/// <para>Hand- or agent-written JSON: a missing <c>box</c> reads as null and a missing
/// <c>minCount</c> as 0; <c>StepValidator</c> refuses both with a sentence before playback.</para>
/// </summary>
public sealed record OutlineCheck(CheckBox Box, int MinCount, int WhiteMin = OutlineCheck.DefaultWhiteMin)
{
    /// <summary>"Each channel >= 225" (spec). A field, so a live measurement can move it.</summary>
    public const int DefaultWhiteMin = 225;

    /// <summary>Largest side at the recorded size. A Mine #8 block is about 56 px at 100%, and
    /// the box has to hold the whole frame even when the spot sits off the block's centre.</summary>
    public const int MaxSide = 120;
}
```

- [ ] **Step 4: Add `CountNearWhite` to `PixelBlock`**

In `src/Macros/Steps/PixelBlock.cs`, add after the `AverageBox` method (inside the class):

```csharp

    /// <summary>How many pixels of a box (client coordinates) have every channel at or above
    /// <paramref name="min"/>; null if the box is not fully inside.</summary>
    public int? CountNearWhite(int x, int y, int w, int h, int min)
    {
        if (x < X || y < Y || x + w > X + W || y + h > Y + H || w <= 0 || h <= 0) return null;
        int n = 0;
        for (int row = y - Y; row < y - Y + h; row++)
        for (int col = x - X; col < x - X + w; col++)
        {
            var p = _argb[row * W + col];
            if (((p >> 16) & 0xFF) >= min && ((p >> 8) & 0xFF) >= min && (p & 0xFF) >= min) n++;
        }
        return n;
    }
```

- [ ] **Step 5: Add scaling to `PointMath`**

In `src/Macros/Steps/PointMath.cs`, add after the `BoxRect` method:

```csharp

    /// <summary>A box placed at an already-placed point, with its offset and size scaled from
    /// the recorded client to the actual one, the same factors <see cref="Place"/> uses. For the
    /// block-sized reach box; the 5x5 colour boxes stay unscaled as before.</summary>
    public static (int X, int Y, int W, int H) ScaledRect((int X, int Y) point, CheckBox box, (int W, int H) recordedClient, (int W, int H) actualClient)
    {
        double sx = actualClient.W / (double)recordedClient.W, sy = actualClient.H / (double)recordedClient.H;
        return (point.X + (int)Math.Round(box.OffsetX * sx), point.Y + (int)Math.Round(box.OffsetY * sy),
                Math.Max(1, (int)Math.Round(box.W * sx)), Math.Max(1, (int)Math.Round(box.H * sy)));
    }

    /// <summary>A reach threshold scaled with the window: linear in the smaller factor, rounded
    /// down, never below 1. An outline's pixel count grows at least linearly with its size (its
    /// length does; its thickness may too), so linear is the safe side when growing.</summary>
    public static int ScaledCount(int count, (int W, int H) recordedClient, (int W, int H) actualClient)
    {
        var s = Math.Min(actualClient.W / (double)recordedClient.W, actualClient.H / (double)recordedClient.H);
        return Math.Max(1, (int)Math.Floor(count * s));
    }
```

- [ ] **Step 6: Add `Reach` to the step records, the grace constant and the validator rules**

In `src/Macros/Steps/MacroStep.cs`, replace:

```csharp
/// <summary>A click at a client-space point. Id is short and stable (not a list position) so
/// adjustments and candidates survive steps being added or reordered.</summary>
public sealed record PointStep(
    int DelayMs, string Id, string? Label, int X, int Y, int Button = 1,
    ColorCheck? Check = null, bool CheckEnabled = false) : MacroStep(DelayMs);
```

with:

```csharp
/// <summary>A click at a client-space point. Id is short and stable (not a list position) so
/// adjustments and candidates survive steps being added or reordered.
/// <para><see cref="Reach"/>: when set, the pointer moves onto the point and the step presses only
/// if the white outline shows there; otherwise it is skipped, not failed. It cannot be combined
/// with an enabled colour check (the colour check parks the pointer away, the outline needs it on).</para></summary>
public sealed record PointStep(
    int DelayMs, string Id, string? Label, int X, int Y, int Button = 1,
    ColorCheck? Check = null, bool CheckEnabled = false, OutlineCheck? Reach = null) : MacroStep(DelayMs);
```

Replace:

```csharp
/// (ore-stop spec, decision 5). Id, adjustments and scaling work exactly as for a point.</summary>
public sealed record HoldStep(
    int DelayMs, string Id, string? Label, int X, int Y, int Button = 1,
    HoldCheck? Check = null, int? MaxMs = null) : MacroStep(DelayMs);
```

with:

```csharp
/// (ore-stop spec, decision 5). Id, adjustments and scaling work exactly as for a point.
/// <para><see cref="Reach"/>: when set, the hold presses only if the white outline shows with the
/// pointer on the spot (else the step is skipped), and lets go when the outline is gone
/// (ore-stop pulse spec).</para></summary>
public sealed record HoldStep(
    int DelayMs, string Id, string? Label, int X, int Y, int Button = 1,
    HoldCheck? Check = null, int? MaxMs = null, OutlineCheck? Reach = null) : MacroStep(DelayMs);
```

In `StepTiming`, replace:

```csharp
    public const int HoldDriftPolls = 2;
```

with:

```csharp
    public const int HoldDriftPolls = 2;

    /// <summary>How long a reach check keeps looking for the outline after the pointer lands,
    /// before it skips the step. The game draws the outline on hover, which can take a frame or
    /// two; a pass ends the wait at once, so only a skip pays it.</summary>
    public const int ReachGraceMs = 300;
```

In `StepValidator.Validate`, in `case PointStep p:`, replace:

```csharp
                    if (p.Check is { } pc && BoxProblem(pc, name) is { } err) return err;
                    break;
```

with:

```csharp
                    if (p.Check is { } pc && BoxProblem(pc, name) is { } err) return err;
                    if (p.Reach is not null && p.CheckEnabled) return $"{name} has both a colour check and a reach check; use one.";
                    if (ReachProblem(p.Reach, name) is { } rerr) return rerr;
                    break;
```

In `case FirstMatchStep f:`, replace:

```csharp
                        if (c.Check is null) return $"{name}: candidate '{c.Label ?? c.Id}' has no colour to check.";
```

with:

```csharp
                        if (c.Check is null) return $"{name}: candidate '{c.Label ?? c.Id}' has no colour to check.";
                        if (c.Reach is not null) return $"{name}: candidate '{c.Label ?? c.Id}' has a reach check, which a first match does not use.";
```

In `case HoldStep h:`, replace:

```csharp
                    if (h.MaxMs is < 1) return $"{name} has a maxMs below 1.";
                    break;
```

with:

```csharp
                    if (h.MaxMs is < 1) return $"{name} has a maxMs below 1.";
                    if (ReachProblem(h.Reach, name) is { } rerr) return rerr;
                    break;
```

Add this method to `StepValidator`, after the second `BoxProblem` overload:

```csharp

    /// <summary>Null when there is no reach check or it can play. A minCount larger than the box
    /// could never pass, so every step would silently skip; that is refused too.</summary>
    private static string? ReachProblem(OutlineCheck? reach, string who)
    {
        if (reach is null) return null;
        if (reach.Box is null) return $"{who} has a reach check with no box.";
        if (reach.Box.W < 1 || reach.Box.H < 1) return $"{who} has an empty reach box.";
        if (reach.Box.W > OutlineCheck.MaxSide || reach.Box.H > OutlineCheck.MaxSide)
            return $"{who} has a reach box larger than {OutlineCheck.MaxSide}x{OutlineCheck.MaxSide}.";
        if (reach.MinCount < 1) return $"{who} has a reach minCount below 1.";
        if (reach.MinCount > reach.Box.W * reach.Box.H) return $"{who} has a reach minCount larger than its box, so it can never pass.";
        if (reach.WhiteMin is < 1 or > 255) return $"{who} has a reach whiteMin outside 1 to 255.";
        return null;
    }
```

`StepTiming.EstimateMs` does not change.

- [ ] **Step 7: Export reach as a comment**

In `src/Macros/AutoHotkeyExporter.cs`, in `AppendSteps`, replace:

```csharp
                case PointStep p:
                    if (p.CheckEnabled && p.Check is { } c)
                        sb.Append($"; check: '{SanitizeComment(p.Label ?? p.Id)}' expects {c.Expect.Hex} here (Ur Task only)").Append(Nl);
                    sb.Append(ClickAt(p.X, p.Y, p.Button, version)).Append(Nl);
                    break;
```

with:

```csharp
                case PointStep p:
                    if (p.CheckEnabled && p.Check is { } c)
                        sb.Append($"; check: '{SanitizeComment(p.Label ?? p.Id)}' expects {c.Expect.Hex} here (Ur Task only)").Append(Nl);
                    if (p.Reach is { } pr) sb.Append(ReachComment(p.Label ?? p.Id, pr)).Append(Nl);
                    sb.Append(ClickAt(p.X, p.Y, p.Button, version)).Append(Nl);
                    break;
```

and replace:

```csharp
                    var holdMs = h.MaxMs ?? HoldExportMs;
                    var holdLabel = SanitizeComment(h.Label ?? h.Id);
```

with:

```csharp
                    var holdMs = h.MaxMs ?? HoldExportMs;
                    var holdLabel = SanitizeComment(h.Label ?? h.Id);
                    if (h.Reach is { } hr) sb.Append(ReachComment(h.Label ?? h.Id, hr)).Append(Nl);
```

Add this method after `SanitizeComment`:

```csharp

    /// <summary>AutoHotkey cannot see the outline, so the export presses unconditionally and says so.</summary>
    private static string ReachComment(string label, OutlineCheck r)
    {
        var size = r.Box is { } b ? FormattableString.Invariant($"{b.W}x{b.H}") : "no box";
        return FormattableString.Invariant(
            $"; reach: '{SanitizeComment(label)}' needs a white outline here ({r.MinCount} near-white px in {size}); Ur Task skips the step without one (Ur Task only)");
    }
```

- [ ] **Step 8: Run the tests to verify they pass**

Run: `dotnet test tests/rororo-ur-task.Tests/rororo-ur-task.Tests.csproj -p:StandaloneTestsOnly=true --filter "FullyQualifiedName~PixelBlockTests|FullyQualifiedName~PointMathTests|FullyQualifiedName~MacroV4FileTests|FullyQualifiedName~AutoHotkeyStepExportTests"`
Expected: all PASS.

Run: `dotnet test tests/rororo-ur-task.Tests/rororo-ur-task.Tests.csproj -p:StandaloneTestsOnly=true`
Expected: 473 passed, 0 failed.

- [ ] **Step 9: Commit**

```bash
git add src/Macros/Steps/OutlineCheck.cs src/Macros/Steps/PixelBlock.cs src/Macros/Steps/PointMath.cs src/Macros/Steps/MacroStep.cs src/Macros/AutoHotkeyExporter.cs tests/rororo-ur-task.Tests/Steps/PixelBlockTests.cs tests/rororo-ur-task.Tests/Steps/PointMathTests.cs tests/rororo-ur-task.Tests/Steps/MacroV4FileTests.cs tests/rororo-ur-task.Tests/Steps/AutoHotkeyStepExportTests.cs
git commit -m "feat(steps): reach check model, the white-outline count on holds and points

Co-Authored-By: Claude Opus 5.5 <noreply@anthropic.com>"
```

---

### Task 2: Playing a hold with a reach check

**Files:**
- Modify: `src/Macros/Steps/StepRunner.cs` (the `// ---------- hold ----------` section, lines 280-349 on `feat/ore-stop`)
- Test: `tests/rororo-ur-task.Tests/Steps/StepRunnerTests.cs`

**Interfaces:**
- Consumes (Task 1): `HoldStep.Reach : OutlineCheck?`, `OutlineCheck(CheckBox Box, int MinCount, int WhiteMin = 225)`, `PixelBlock.CountNearWhite(int x, int y, int w, int h, int min) : int?`, `PointMath.ScaledRect(...)`, `PointMath.ScaledCount(...)`, `StepTiming.ReachGraceMs`, `StepTiming.HoldDriftPolls`, `StepTiming.PollMs`.
- Produces (private to `StepRunner`, used by Task 3):
  - `private readonly record struct ReachPlan((int X, int Y, int W, int H) Rect, int Need, int WhiteMin);`
  - `private static ReachPlan? ReachFor(OutlineCheck? reach, (int X, int Y) at, (int W, int H) client, StepContext ctx, string name, int index)`: null for no reach; throws the step's `StopException` "`{name} checks a reach box outside the window.`" when the scaled box leaves the client.
  - `private static async Task<(bool Seen, int Count)> AwaitOutlineAsync(ReachPlan r, IStepIo io, string name, int index, CancellationToken ct)`
  - `private static void LogOutline(StepContext ctx, int index, string label, bool seen, int count, int need)`: logs `step {n} '{label}' outline seen ({count} near-white px, needs {need})` or `step {n} '{label}' no outline, skipped ({count} near-white px, needs {need})`.
  - `private static int? CountGuarded(IStepIo io, (int X, int Y, int W, int H) r, int whiteMin)`
  - Hold release reason: `outline gone ({count} near-white px, needs {need})`.

- [ ] **Step 1: Write the failing tests**

In `tests/rororo-ur-task.Tests/Steps/StepRunnerTests.cs`, add after the `Hold_log_lines_use_invariant_numbers` test (the end of the hold section, before `// ---------- first match: skipIfOther ----------`):

```csharp
    // ---------- reach (the white outline) ----------

    private static readonly Rgb White = new(245, 245, 245);
    private static readonly OutlineCheck Reach80 = new(new CheckBox(-40, -40, 80, 80), 60);

    /// <summary>Mine #8 rock and lava: navy, hot magenta, orange, pink, and a warm near-white whose
    /// blue sits under 225. None of them has every channel at 225 or more.</summary>
    private static readonly Rgb[] Lava = { new(8, 9, 19), new(235, 9, 116), new(255, 140, 30), new(255, 120, 200), new(250, 230, 180) };
    private static Rgb LavaAt(int x, int y) => Lava[(x * 7 + y * 13) % Lava.Length];

    /// <summary>A block face centred on (cx, cy): ore inside, a white frame <paramref name="thick"/>
    /// px wide at <paramref name="half"/> px out, rock beyond. The ring at distance k holds 8k
    /// pixels, so a 1 px frame at 28 is 224 pixels and a 2 px frame is 224 + 216 = 440.
    /// <paramref name="shown"/> turns the outline off (rock instead of white) at chosen times.</summary>
    private static Func<long, int, int, Rgb> Framed(int cx, int cy, int half = 28, int thick = 1, Func<long, bool>? shown = null)
        => (t, x, y) =>
        {
            var m = Math.Max(Math.Abs(x - cx), Math.Abs(y - cy));
            if (m > half) return Rock;
            if (m > half - thick) return shown is null || shown(t) ? White : Rock;
            return Ore;
        };

    private static HoldStep ReachHold(int? maxMs = null, int x = 400, int y = 244)
        => Hold(maxMs, x: x, y: y) with { Reach = Reach80 };

    [Theory]
    [InlineData(1, 224)]
    [InlineData(2, 440)]
    public async Task A_thin_white_frame_lets_the_hold_press(int thick, int count)
    {
        var log = new List<string>();
        var io = new FakeIo { Screen = Framed(400, 244, thick: thick) };
        var r = await StepRunner.RunAsync(new MacroStep[] { ReachHold(maxMs: 500) }, Ctx(log), io, default);

        Assert.Equal(PlaybackOutcome.Completed, r.Outcome);
        var down = Assert.Single(io.Downs);
        Assert.Equal((400, 244, 150L), (down.X, down.Y, down.TimestampMs)); // no grace spent: the outline was there
        Assert.Contains($"step 1 'Spot N' outline seen ({count} near-white px, needs 60)", log);
        Assert.Contains("step 1 'Spot N' held 0.5 s, released: reached its 500 ms limit", log);
    }

    [Fact]
    public async Task Lava_without_a_white_frame_skips_the_hold_and_the_next_step_runs()
    {
        var log = new List<string>();
        var io = new FakeIo { Screen = (_, x, y) => LavaAt(x, y) };
        var r = await StepRunner.RunAsync(new MacroStep[] { ReachHold(), new PointStep(0, "p9", null, 5, 5) }, Ctx(log), io, default);

        Assert.Equal(PlaybackOutcome.Completed, r.Outcome); // a skip, never a failure
        var down = Assert.Single(io.Downs);
        Assert.Equal((5, 5), (down.X, down.Y));
        Assert.Contains("step 1 'Spot N' no outline, skipped (0 near-white px, needs 60)", log);
        Assert.DoesNotContain(log, l => l.Contains("held"));
    }

    [Fact]
    public async Task Reach_waits_its_grace_for_the_outline_to_draw()
    {
        // The pointer lands at 100 ms; the game draws the outline at 300. Checks run at 150, 250, 350.
        var log = new List<string>();
        var io = new FakeIo { Screen = Framed(400, 244, shown: t => t >= 300) };
        await StepRunner.RunAsync(new MacroStep[] { ReachHold(maxMs: 500) }, Ctx(log), io, default);

        Assert.Equal(350, Assert.Single(io.Downs).TimestampMs);
        Assert.Contains("step 1 'Spot N' outline seen (224 near-white px, needs 60)", log);
    }

    [Fact]
    public async Task A_hold_lets_go_when_the_outline_is_gone_for_two_polls()
    {
        // The ore stays under the colour box, but the outline goes at 1200 (out of reach, or the
        // block behind it is not breakable). Polls at 1250 and 1350 miss it: release at 1350.
        var log = new List<string>();
        var io = new FakeIo { Screen = Framed(400, 244, shown: t => t < 1200) };
        var r = await StepRunner.RunAsync(new MacroStep[] { ReachHold() }, Ctx(log), io, default);

        Assert.Equal(PlaybackOutcome.Completed, r.Outcome);
        Assert.Equal(1350, Assert.Single(io.Sent, e => e.Kind == MacroEventKind.MouseUp).TimestampMs);
        Assert.Contains("step 1 'Spot N' held 1.2 s, released: outline gone (0 near-white px, needs 60)", log);
        Assert.Empty(io.Released);
    }

    [Fact]
    public async Task One_poll_without_the_outline_does_not_end_a_hold()
    {
        // The pickaxe swing crosses the frame for exactly one poll (1050).
        var log = new List<string>();
        var io = new FakeIo { Screen = Framed(400, 244, shown: t => t != 1050) };
        await StepRunner.RunAsync(new MacroStep[] { ReachHold(maxMs: 3000) }, Ctx(log), io, default);
        Assert.Contains("step 1 'Spot N' held 3.0 s, released: reached its 3000 ms limit", log);
    }

    [Fact]
    public async Task Losing_the_foreground_during_the_reach_check_aborts_rather_than_skips()
    {
        var log = new List<string>();
        var io = new FakeIo { Screen = (_, x, y) => LavaAt(x, y) };
        io.OnDelay = () => { if (io.NowMs >= 150) io.Foreground = false; }; // right after the jump
        var r = await StepRunner.RunAsync(new MacroStep[] { ReachHold(), new PointStep(0, "p9", null, 5, 5) }, Ctx(log), io, default);

        Assert.Equal(PlaybackOutcome.Aborted, r.Outcome);
        Assert.Equal("Foreground shifted away from CElCPapa at step 1/2.", r.Reason);
        Assert.Null(r.StepIndex);
        Assert.Empty(io.Downs);
        Assert.DoesNotContain(log, l => l.Contains("skipped"));
    }

    [Fact]
    public async Task A_reach_check_that_cannot_see_the_window_stops_instead_of_skipping()
    {
        var log = new List<string>();
        var io = new FakeIo { CaptureWorks = false };
        var r = await StepRunner.RunAsync(new MacroStep[] { ReachHold(), new PointStep(0, "p9", null, 5, 5) }, Ctx(log), io, default);

        Assert.Equal("CElCPapa: step 1 'Spot N' could not see the window.", r.Reason);
        Assert.Equal(0, r.StepIndex);
        Assert.Empty(io.Downs);
        Assert.DoesNotContain(log, l => l.Contains("skipped"));
    }

    [Fact]
    public async Task A_reach_box_outside_the_window_refuses_before_any_input()
    {
        // The colour box at (398,28) fits; the 80x80 reach box would start at y = -10.
        var io = new FakeIo();
        var r = await StepRunner.RunAsync(new MacroStep[] { ReachHold(x: 400, y: 30) }, Ctx(), io, default);
        Assert.Equal("CElCPapa: step 1 'Spot N' checks a reach box outside the window.", r.Reason);
        Assert.Equal(0, r.StepIndex);
        Assert.Empty(io.Sent);
    }

    [Fact]
    public async Task Reach_scales_with_the_window()
    {
        // (400,244) in 800x599 lands at (500,305) in 1000x749; the box grows to 100x100 and the
        // threshold to 75. The frame is drawn at the scaled size (35 px out, 280 px).
        var log = new List<string>();
        var io = new FakeIo { Client = (1000, 749), Screen = Framed(500, 305, half: 35) };
        await StepRunner.RunAsync(new MacroStep[] { ReachHold(maxMs: 500) }, Ctx(log, actual: (1000, 749)), io, default);

        Assert.Contains((450, 255, 100, 100), io.CaptureRects);
        Assert.Equal((500, 305), (io.Downs.Single().X, io.Downs.Single().Y));
        Assert.Contains("step 1 'Spot N' outline seen (280 near-white px, needs 75)", log);
    }
```

- [ ] **Step 2: Run the tests to verify they fail**

Run: `dotnet test tests/rororo-ur-task.Tests/rororo-ur-task.Tests.csproj -p:StandaloneTestsOnly=true --filter "FullyQualifiedName~StepRunnerTests"`
Expected: the new tests FAIL (the runner ignores `Reach`: for example `Lava_without_a_white_frame_skips_the_hold_and_the_next_step_runs` sees two downs, and `A_reach_box_outside_the_window_refuses_before_any_input` sees input sent). The existing StepRunner tests still pass.

- [ ] **Step 3: Implement reach in the hold**

In `src/Macros/Steps/StepRunner.cs`, replace the whole `PlayHoldAsync` method (from its `/// <summary>` through its closing brace, just before the `SampleGuarded` summary) with:

```csharp
    /// <summary>
    /// Press and keep holding while the box stays within tolerance of the colour it showed just
    /// before the press. The starting sample is taken after the jump, so the pointer's own hover
    /// tint is already in it. Lets go when the colour has moved for
    /// <see cref="StepTiming.HoldDriftPolls"/> polls in a row, or at MaxMs. With no MaxMs there is
    /// no time limit: ore is never abandoned for taking long (ore-stop spec, decision 5).
    /// <para>With a reach check (ore-stop pulse spec): after the jump, no white outline within
    /// <see cref="StepTiming.ReachGraceMs"/> skips the step without pressing. During the hold, the
    /// outline gone for <see cref="StepTiming.HoldDriftPolls"/> polls running lets go: the block
    /// broke or is out of reach.</para>
    /// </summary>
    private static async Task PlayHoldAsync(HoldStep h, int index, StepContext ctx, IStepIo io, HashSet<int> heldButtons, CancellationToken ct)
    {
        var label = h.Label ?? h.Id;
        var name = $"{ctx.AccountName}: step {index + 1} '{label}'";
        var adj = ctx.Adjust(h.Id);
        var at = Place(ctx, adj is null ? (h.X, h.Y) : (adj.X, adj.Y));
        var check = h.Check!; // StepValidator refuses a hold without one
        var box = PointMath.BoxRect(at, check.Box);
        var client = io.ClientSize() ?? throw new StopException($"{name} could not see the window.", index);
        if (!PointMath.InsideClient(box, client)) throw new StopException($"{name} checks a box outside the window.", index);
        var reach = ReachFor(h.Reach, at, client, ctx, name, index);

        await io.Delay(h.DelayMs, ct);
        await JumpAsync(io, at, ct);
        if (reach is { } r0)
        {
            var (seen0, count0) = await AwaitOutlineAsync(r0, io, name, index, ct);
            LogOutline(ctx, index, label, seen0, count0, r0.Need);
            if (!seen0) return;
        }
        var start = SampleGuarded(io, box) ?? throw new StopException($"{name} could not see the window.", index);

        SendGuarded(io, new MacroEvent(0, MacroEventKind.MouseDown, 0, at.X, at.Y, h.Button, 0));
        heldButtons.Add(h.Button);
        var pressedAt = io.NowMs;
        long? limit = h.MaxMs;
        string why;
        try
        {
            int drifted = 0, gone = 0;
            while (true)
            {
                var elapsed = io.NowMs - pressedAt;
                if (elapsed >= limit) { why = $"reached its {limit} ms limit"; break; }
                await io.Delay((int)Math.Min(StepTiming.PollMs, (limit ?? long.MaxValue) - elapsed), ct);
                if (io.NowMs - pressedAt >= limit) continue; // the limit lets go at the top, without another sample
                var seen = SampleGuarded(io, box) ?? throw new StopException($"{name} could not see the window.", index);
                var d = seen.DistanceTo(start);
                drifted = d <= check.Tolerance ? 0 : drifted + 1;
                if (drifted >= StepTiming.HoldDriftPolls)
                {
                    why = string.Create(CultureInfo.InvariantCulture,
                        $"colour moved from {ColorNamer.Describe(start)} to {ColorNamer.Describe(seen)} (distance {d:F0})");
                    break;
                }
                if (reach is { } r)
                {
                    var count = CountGuarded(io, r.Rect, r.WhiteMin) ?? throw new StopException($"{name} could not see the window.", index);
                    gone = count >= r.Need ? 0 : gone + 1;
                    if (gone >= StepTiming.HoldDriftPolls)
                    {
                        why = string.Create(CultureInfo.InvariantCulture, $"outline gone ({count} near-white px, needs {r.Need})");
                        break;
                    }
                }
            }
            SendGuarded(io, new MacroEvent(0, MacroEventKind.MouseUp, 0, at.X, at.Y, h.Button, 0));
            heldButtons.Remove(h.Button);
        }
        catch
        {
            // Esc, StopMacro, a focus change or a window it can no longer see. RunAsync's finally
            // releases the button, which is still in heldButtons. The log still says how long it
            // held, so an ore that never broke is visible in ur-task.log.
            ctx.Log(string.Create(CultureInfo.InvariantCulture,
                $"step {index + 1} '{label}' held {(io.NowMs - pressedAt) / 1000.0:F1} s, then the playback ended"));
            throw;
        }
        ctx.Log(string.Create(CultureInfo.InvariantCulture,
            $"step {index + 1} '{label}' held {(io.NowMs - pressedAt) / 1000.0:F1} s, released: {why}"));
    }

    // ---------- reach (the white outline) ----------

    /// <summary>A reach check placed in the actual window: the box, the scaled threshold, and the
    /// white floor.</summary>
    private readonly record struct ReachPlan((int X, int Y, int W, int H) Rect, int Need, int WhiteMin);

    /// <summary>Null when the step has no reach check. The box scales like the point; one that
    /// leaves the window stops the step before any input, like a colour box does.</summary>
    private static ReachPlan? ReachFor(OutlineCheck? reach, (int X, int Y) at, (int W, int H) client, StepContext ctx, string name, int index)
    {
        if (reach is null) return null;
        var rect = PointMath.ScaledRect(at, reach.Box, ctx.RecordedClient, ctx.ActualClient);
        if (!PointMath.InsideClient(rect, client)) throw new StopException($"{name} checks a reach box outside the window.", index);
        return new ReachPlan(rect, PointMath.ScaledCount(reach.MinCount, ctx.RecordedClient, ctx.ActualClient), reach.WhiteMin);
    }

    /// <summary>With the pointer already on the spot: count until the outline shows, for at most
    /// <see cref="StepTiming.ReachGraceMs"/>. A lost foreground aborts and a failed capture stops
    /// the step; neither ever reads as "no outline".</summary>
    private static async Task<(bool Seen, int Count)> AwaitOutlineAsync(ReachPlan r, IStepIo io, string name, int index, CancellationToken ct)
    {
        var start = io.NowMs;
        while (true)
        {
            var count = CountGuarded(io, r.Rect, r.WhiteMin) ?? throw new StopException($"{name} could not see the window.", index);
            if (count >= r.Need) return (true, count);
            if (io.NowMs - start >= StepTiming.ReachGraceMs) return (false, count);
            await io.Delay(StepTiming.PollMs, ct);
        }
    }

    /// <summary>Both outcomes are logged with the count, so the threshold can be tuned from real runs.</summary>
    private static void LogOutline(StepContext ctx, int index, string label, bool seen, int count, int need)
        => ctx.Log(string.Create(CultureInfo.InvariantCulture, seen
            ? $"step {index + 1} '{label}' outline seen ({count} near-white px, needs {need})"
            : $"step {index + 1} '{label}' no outline, skipped ({count} near-white px, needs {need})"));

    /// <summary>A near-white count guarded like <see cref="SampleGuarded"/>. Null when the capture
    /// fails or comes back the wrong shape.</summary>
    private static int? CountGuarded(IStepIo io, (int X, int Y, int W, int H) r, int whiteMin)
    {
        GuardForeground(io);
        return io.Capture(r.X, r.Y, r.W, r.H)?.CountNearWhite(r.X, r.Y, r.W, r.H, whiteMin);
    }
```

The colour-drift loop is restructured (`drifted = ... ? 0 : drifted + 1`) but behaves exactly as before: reset on a clean poll, release on the second drifted poll in a row.

- [ ] **Step 4: Run the tests to verify they pass**

Run: `dotnet test tests/rororo-ur-task.Tests/rororo-ur-task.Tests.csproj -p:StandaloneTestsOnly=true --filter "FullyQualifiedName~StepRunnerTests"`
Expected: all PASS, including every pre-existing hold test.

Run: `dotnet test tests/rororo-ur-task.Tests/rororo-ur-task.Tests.csproj -p:StandaloneTestsOnly=true`
Expected: 483 passed, 0 failed.

- [ ] **Step 5: Commit**

```bash
git add src/Macros/Steps/StepRunner.cs tests/rororo-ur-task.Tests/Steps/StepRunnerTests.cs
git commit -m "feat(steps): a hold presses only on the white outline and lets go when it is gone

Co-Authored-By: Claude Opus 5.5 <noreply@anthropic.com>"
```

---

### Task 3: Playing a point with a reach check

**Files:**
- Modify: `src/Macros/Steps/StepRunner.cs` (`PlayPointAsync`, `PressAsync`)
- Test: `tests/rororo-ur-task.Tests/Steps/StepRunnerTests.cs`

**Interfaces:**
- Consumes (Task 1): `PointStep.Reach : OutlineCheck?`. (Task 2): `ReachPlan`, `ReachFor(OutlineCheck?, (int X, int Y), (int W, int H), StepContext, string, int) : ReachPlan?`, `AwaitOutlineAsync(ReachPlan, IStepIo, string, int, CancellationToken) : Task<(bool Seen, int Count)>`, `LogOutline(StepContext, int, string, bool, int, int)`. Test helpers from Task 2 in `StepRunnerTests`: `Reach80`, `LavaAt(int, int)`, `Framed(int cx, int cy, int half = 28, int thick = 1, Func<long, bool>? shown = null)`.
- Produces: `private static async Task ClickAsync(IStepIo io, (int X, int Y) at, int button, HashSet<int> heldButtons, CancellationToken ct)` (down, `PressHoldMs`, up, no jump); `PressAsync` becomes jump plus `ClickAsync`.

- [ ] **Step 1: Write the failing tests**

In `tests/rororo-ur-task.Tests/Steps/StepRunnerTests.cs`, add after `Reach_scales_with_the_window`:

```csharp
    [Fact]
    public async Task A_point_with_an_outline_presses_once_without_a_second_jump()
    {
        var log = new List<string>();
        var io = new FakeIo { Screen = Framed(400, 244) };
        var r = await StepRunner.RunAsync(new MacroStep[] { new PointStep(0, "p1", "Ore", 400, 244, Reach: Reach80) }, Ctx(log), io, default);

        Assert.Equal(PlaybackOutcome.Completed, r.Outcome);
        var down = Assert.Single(io.Downs);
        Assert.Equal((400, 244, 150L), (down.X, down.Y, down.TimestampMs));
        Assert.Equal(150 + StepTiming.PressHoldMs, Assert.Single(io.Sent, e => e.Kind == MacroEventKind.MouseUp).TimestampMs);
        Assert.Equal(3, io.Sent.Count(e => e.Kind == MacroEventKind.MouseMove)); // one jump, not two
        Assert.Contains("step 1 'Ore' outline seen (224 near-white px, needs 60)", log);
    }

    [Fact]
    public async Task A_point_without_an_outline_is_skipped()
    {
        var log = new List<string>();
        var io = new FakeIo { Screen = (_, x, y) => LavaAt(x, y) };
        var r = await StepRunner.RunAsync(new MacroStep[]
        {
            new PointStep(0, "p1", "Ore", 400, 244, Reach: Reach80),
            new PointStep(0, "p9", null, 5, 5),
        }, Ctx(log), io, default);

        Assert.Equal(PlaybackOutcome.Completed, r.Outcome);
        Assert.Equal((5, 5), (io.Downs.Single().X, io.Downs.Single().Y));
        Assert.Contains("step 1 'Ore' no outline, skipped (0 near-white px, needs 60)", log);
    }

    [Fact]
    public async Task A_point_reach_box_outside_the_window_refuses_before_any_input()
    {
        var io = new FakeIo();
        var r = await StepRunner.RunAsync(new MacroStep[] { new PointStep(0, "p1", "Ore", 400, 30, Reach: Reach80) }, Ctx(), io, default);
        Assert.Equal("CElCPapa: step 1 'Ore' checks a reach box outside the window.", r.Reason);
        Assert.Equal(0, r.StepIndex);
        Assert.Empty(io.Sent);
    }
```

- [ ] **Step 2: Run the tests to verify they fail**

Run: `dotnet test tests/rororo-ur-task.Tests/rororo-ur-task.Tests.csproj -p:StandaloneTestsOnly=true --filter "FullyQualifiedName~StepRunnerTests"`
Expected: the three new tests FAIL (the point ignores `Reach`: it presses on lava, sends input for the out-of-window box, and logs no outline line).

- [ ] **Step 3: Implement reach on points**

In `src/Macros/Steps/StepRunner.cs`, in `PlayPointAsync`, replace:

```csharp
        var (recorded, check) = Resolve(p, ctx);
        var at = Place(ctx, recorded);

        if (!p.CheckEnabled || check is null)
```

with:

```csharp
        var (recorded, check) = Resolve(p, ctx);
        var at = Place(ctx, recorded);

        if (p.Reach is not null)
        {
            // StepValidator refuses a reach check together with an enabled colour check.
            await PlayReachedPointAsync(p, at, index, ctx, io, heldButtons, ct);
            return;
        }

        if (!p.CheckEnabled || check is null)
```

Add this method directly after `PlayPointAsync`:

```csharp

    /// <summary>A point behind a reach check: wait its delay, move onto the point, and press only
    /// if the white outline shows there (ore-stop pulse spec). No outline skips the step.</summary>
    private static async Task PlayReachedPointAsync(PointStep p, (int X, int Y) at, int index, StepContext ctx, IStepIo io, HashSet<int> heldButtons, CancellationToken ct)
    {
        var label = p.Label ?? p.Id;
        var name = $"{ctx.AccountName}: step {index + 1} '{label}'";
        var client = io.ClientSize() ?? throw new StopException($"{name} could not see the window.", index);
        var reach = ReachFor(p.Reach, at, client, ctx, name, index)!.Value;

        await io.Delay(p.DelayMs, ct);
        await JumpAsync(io, at, ct);
        var (seen, count) = await AwaitOutlineAsync(reach, io, name, index, ct);
        LogOutline(ctx, index, label, seen, count, reach.Need);
        if (!seen) return;
        await ClickAsync(io, at, p.Button, heldButtons, ct); // already on the point: no second jump
    }
```

Replace the `PressAsync` method:

```csharp
    private static async Task PressAsync(IStepIo io, (int X, int Y) at, int button, HashSet<int> heldButtons, CancellationToken ct)
    {
        await JumpAsync(io, at, ct);
        SendGuarded(io, new MacroEvent(0, MacroEventKind.MouseDown, 0, at.X, at.Y, button, 0));
        heldButtons.Add(button);
        await io.Delay(StepTiming.PressHoldMs, ct);
        SendGuarded(io, new MacroEvent(0, MacroEventKind.MouseUp, 0, at.X, at.Y, button, 0));
        heldButtons.Remove(button);
    }
```

with:

```csharp
    private static async Task PressAsync(IStepIo io, (int X, int Y) at, int button, HashSet<int> heldButtons, CancellationToken ct)
    {
        await JumpAsync(io, at, ct);
        await ClickAsync(io, at, button, heldButtons, ct);
    }

    /// <summary>Down, <see cref="StepTiming.PressHoldMs"/>, up, with the pointer already on the point.</summary>
    private static async Task ClickAsync(IStepIo io, (int X, int Y) at, int button, HashSet<int> heldButtons, CancellationToken ct)
    {
        SendGuarded(io, new MacroEvent(0, MacroEventKind.MouseDown, 0, at.X, at.Y, button, 0));
        heldButtons.Add(button);
        await io.Delay(StepTiming.PressHoldMs, ct);
        SendGuarded(io, new MacroEvent(0, MacroEventKind.MouseUp, 0, at.X, at.Y, button, 0));
        heldButtons.Remove(button);
    }
```

- [ ] **Step 4: Run the tests to verify they pass**

Run: `dotnet test tests/rororo-ur-task.Tests/rororo-ur-task.Tests.csproj -p:StandaloneTestsOnly=true --filter "FullyQualifiedName~StepRunnerTests"`
Expected: all PASS.

Run: `dotnet test tests/rororo-ur-task.Tests/rororo-ur-task.Tests.csproj -p:StandaloneTestsOnly=true`
Expected: 486 passed, 0 failed.

- [ ] **Step 5: Commit**

```bash
git add src/Macros/Steps/StepRunner.cs tests/rororo-ur-task.Tests/Steps/StepRunnerTests.cs
git commit -m "feat(steps): a point behind a reach check presses only on the white outline

Co-Authored-By: Claude Opus 5.5 <noreply@anthropic.com>"
```

---

### Task 4: A playback that pressed nothing reports finished/skipped

Controller ruling (2026-09-28, resolving the Ur OCR plan's question): a bridge playback that pressed nothing because reach checks skipped reports GetPlayback `{"ok":true,"state":"finished","reason":"skipped"}`, so Ur OCR's pulse loop can tell a skipped Clear spot from a mined one. Additive: `reason` on a finished run, which Ur MCP ignores for finished.

**Files:**
- Modify: `src/Macros/MacroPlayer.cs` (`PlaybackResult`, bottom of the file)
- Modify: `src/Macros/SequenceTypes.cs` (`AltOutcome`)
- Modify: `src/Macros/SequencePlayer.cs` (the `perAlt.Add` after a play)
- Modify: `src/Macros/Steps/StepRunner.cs` (`RunAsync`, `PlayPointAsync`, `PlayReachedPointAsync`, `PlayHoldAsync`)
- Modify: `src/Macros/PlaybackEndLog.cs`
- Modify: `src/Ipc/MacroRunInvoker.cs` (`ObservePlaybackAsync`)
- Modify: `src/Ipc/BridgeContract.cs` (doc comment only)
- Test: `tests/rororo-ur-task.Tests/Steps/StepRunnerTests.cs`, `tests/rororo-ur-task.Tests/PlaybackEndLogTests.cs`, `tests/rororo-ur-task.Tests/SequencePlayerTests.cs`, `tests/rororo-ur-task.Tests/Ipc/MacroRunInvokerTests.cs`, `tests/rororo-ur-task.Tests/Ipc/MacroRunnerServerTests.cs`

**Interfaces:**
- Consumes (Task 2): `PlayHoldAsync` with the reach skip `if (!seen0) return;`; test helpers `ReachHold(int? maxMs = null, int x = 400, int y = 244)`, `Reach80`, `LavaAt`, `Framed`. (Task 3): `PlayReachedPointAsync` with `if (!seen) return;`.
- Produces:
  - `PlaybackResult.SkippedByReach : bool` (init-only, default false) and `public static PlaybackResult CompletedSkippedByReach()`. **Definition:** true only on a `Completed` run of a step macro in which at least one step was skipped by its reach check AND no mouse button and no key went down at all (no `MouseDown` or `KeyDown` event was sent, by any step kind, raw steps included). Pointer moves, waits and wheel notches do not count as presses. A run with no reach skip is never `SkippedByReach`, even if it pressed nothing (a wait-only macro is a plain finish). The existing `PlaybackOutcome.Skipped` ("sequence aborted before this alt") is a different thing and is untouched.
  - `AltOutcome(AccountInfo Alt, PlaybackOutcome Outcome, string? Reason, int? StepIndex = null, bool SkippedByReach = false)`.
  - GetPlayback for a bridge playback: `state` `finished`, `reason` `skipped` when the last pass's `PerAlt` is non-empty and every entry is `Completed` with `SkippedByReach`; otherwise a finished run keeps `reason` null.
  - Log lines: `playback finished (skipped: no outline): '<name>' on <account> in <secs> s.` per account; `bridge playback <id> '<name>': finished (skipped)` per bridge playback.

- [ ] **Step 1: Write the failing tests**

In `tests/rororo-ur-task.Tests/Steps/StepRunnerTests.cs`, add after `A_point_reach_box_outside_the_window_refuses_before_any_input` (Task 3):

```csharp
    // ---------- a run that pressed nothing ----------

    [Fact]
    public async Task A_run_whose_every_press_was_skipped_by_reach_completes_as_skipped()
    {
        var io = new FakeIo { Screen = (_, x, y) => LavaAt(x, y) };
        var r = await StepRunner.RunAsync(new MacroStep[]
        {
            ReachHold(),
            new PointStep(0, "p1", "Ore", 456, 300, Reach: Reach80),
            new WaitStep(100),
        }, Ctx(), io, default);

        Assert.Equal(PlaybackOutcome.Completed, r.Outcome);
        Assert.True(r.SkippedByReach);
        Assert.Null(r.Reason);
        Assert.Empty(io.Downs);
    }

    [Theory]
    [InlineData("key")]
    [InlineData("point")]
    [InlineData("mined")]
    public async Task A_run_that_pressed_anything_is_a_plain_finish(string kind)
    {
        var io = new FakeIo { Screen = kind == "mined" ? Framed(400, 244) : (_, x, y) => LavaAt(x, y) };
        var steps = kind switch
        {
            "key" => new MacroStep[] { ReachHold(), new KeyStep(0, 0x41, true), new KeyStep(0, 0x41, false) },
            "point" => new MacroStep[] { ReachHold(), new PointStep(0, "p9", null, 5, 5) },
            _ => new MacroStep[] { ReachHold(maxMs: 500) },
        };
        var r = await StepRunner.RunAsync(steps, Ctx(), io, default);

        Assert.Equal(PlaybackOutcome.Completed, r.Outcome);
        Assert.False(r.SkippedByReach);
    }

    [Fact]
    public async Task A_run_with_no_reach_skip_is_a_plain_finish_even_if_it_pressed_nothing()
    {
        var r = await StepRunner.RunAsync(new MacroStep[] { new WaitStep(100) }, Ctx(), new FakeIo(), default);
        Assert.Equal(PlaybackOutcome.Completed, r.Outcome);
        Assert.False(r.SkippedByReach);
    }
```

In `tests/rororo-ur-task.Tests/PlaybackEndLogTests.cs`, add inside the class, before its final closing brace:

```csharp
    [Fact]
    public void A_run_skipped_by_reach_reads_as_finished_skipped()
        => Assert.Equal("playback finished (skipped: no outline): 'Clear spot N' on CElCPapa in 12.4 s.",
            PlaybackEndLog.Line("Clear spot N", "CElCPapa", PlaybackResult.CompletedSkippedByReach(), T));

    [Fact]
    public void The_bridge_line_for_a_skip_carries_the_reason()
        => Assert.Equal("bridge playback abc 'Clear spot N': finished (skipped)",
            PlaybackEndLog.BridgeLine("abc", "Clear spot N", "finished", "skipped", null));
```

In `tests/rororo-ur-task.Tests/SequencePlayerTests.cs`, add inside the class, before its final closing brace:

```csharp
    [Fact]
    public async Task PlayAsync_CarriesTheReachSkipIntoEachAltOutcome()
    {
        var player = new FakePlayer();
        player.Results.Enqueue(PlaybackResult.CompletedSkippedByReach());
        player.Results.Enqueue(PlaybackResult.Completed());
        var fg = new FakeForeground();
        var sequence = new SequencePlayer(player, fg, _ => (true, null));
        var targets = new[] { Alt(1001, 47821334, "Goldnail8"), Alt(1002, 47821335, "ScrambledTen") };
        long lastFocused = 0;
        fg.Resolver = () => targets.FirstOrDefault(a => a.RobloxUserId == lastFocused);
        sequence.Progress += (_, prog) =>
        {
            if (prog.Phase == SequencePhase.Focusing && prog.CurrentAlt is not null)
                lastFocused = prog.CurrentAlt.RobloxUserId;
        };

        var result = await sequence.PlayAsync(NewMacro(), targets, interAltDelayMs: 0);

        Assert.Equal(new[] { true, false }, result.PerAlt.Select(a => a.SkippedByReach));
        Assert.All(result.PerAlt, a => Assert.Equal(PlaybackOutcome.Completed, a.Outcome));
    }
```

In `tests/rororo-ur-task.Tests/Ipc/MacroRunInvokerTests.cs`, in `GetPlayback_reports_finished_for_a_clean_pass`, replace:

```csharp
        Assert.Equal("finished", inv.GetPlayback(new GetPlaybackRequest("1.0", "GetPlayback", run.PlaybackId, "626labs.ur-mcp")).State);
    }

    [Fact]
    public async Task GetPlayback_reports_stopped_after_StopMacro()
```

with:

```csharp
        var status = inv.GetPlayback(new GetPlaybackRequest("1.0", "GetPlayback", run.PlaybackId, "626labs.ur-mcp"));
        Assert.Equal(("finished", (string?)null), (status.State, status.Reason)); // a normal finish has no reason
    }

    [Fact]
    public async Task GetPlayback_reports_finished_skipped_when_every_alt_was_skipped_by_reach()
    {
        var m = NewMacro(Guid.NewGuid().ToString(), "Clear spot N");
        var alt = Alt(123);
        var skipped = new SequenceResult(new[] { new AltOutcome(alt, PlaybackOutcome.Completed, null, SkippedByReach: true) }, 1, 0, 0, TimeSpan.FromSeconds(1));
        var inv = BuildWithResult(m, alt, (_, _, _, _) => Task.FromResult<SequenceResult?>(skipped));

        var run = await inv.RunAsync(new RunMacroRequest("1.0", "RunMacro", m.Id, new[] { "123" }, null, "626labs.ur-ocr"), default);
        await WaitUntilAsync(() => inv.ActivePlaybackCount == 0);

        var status = inv.GetPlayback(new GetPlaybackRequest("1.0", "GetPlayback", run.PlaybackId, "626labs.ur-ocr"));
        Assert.True(status.Ok);
        Assert.Equal(("finished", "skipped", (string?)null, (int?)null), (status.State, status.Reason, status.Detail, status.StepIndex));
    }

    [Fact]
    public async Task GetPlayback_has_no_reason_when_any_alt_pressed()
    {
        var m = NewMacro(Guid.NewGuid().ToString(), "Clear spot N");
        var alt = Alt(123);
        var mixed = new SequenceResult(new[]
        {
            new AltOutcome(alt, PlaybackOutcome.Completed, null, SkippedByReach: true),
            new AltOutcome(Alt(124), PlaybackOutcome.Completed, null),
        }, 2, 0, 0, TimeSpan.FromSeconds(1));
        var inv = BuildWithResult(m, alt, (_, _, _, _) => Task.FromResult<SequenceResult?>(mixed));

        var run = await inv.RunAsync(new RunMacroRequest("1.0", "RunMacro", m.Id, new[] { "123" }, null, "626labs.ur-ocr"), default);
        await WaitUntilAsync(() => inv.ActivePlaybackCount == 0);

        var status = inv.GetPlayback(new GetPlaybackRequest("1.0", "GetPlayback", run.PlaybackId, "626labs.ur-ocr"));
        Assert.Equal(("finished", (string?)null), (status.State, status.Reason));
    }

    [Fact]
    public async Task A_skipped_bridge_playback_logs_finished_skipped()
    {
        var m = NewMacro(Guid.NewGuid().ToString(), "Clear spot N");
        var alt = Alt(123);
        var lines = new List<string>();
        var skipped = new SequenceResult(new[] { new AltOutcome(alt, PlaybackOutcome.Completed, null, SkippedByReach: true) }, 1, 0, 0, TimeSpan.FromSeconds(1));
        var inv = new MacroRunInvoker(
            loadMacros: () => new[] { m },
            snapshot: () => new[] { alt },
            resolveForegroundUserId: () => alt.RobloxUserId,
            isBusy: () => false,
            playWithResult: (_, _, _, _) => Task.FromResult<SequenceResult?>(skipped),
            log: lines.Add);

        var run = await inv.RunAsync(new RunMacroRequest("1.0", "RunMacro", m.Id, new[] { "123" }, null, "626labs.ur-ocr"), default);
        await WaitUntilAsync(() => inv.ActivePlaybackCount == 0);

        Assert.Equal($"bridge playback {run.PlaybackId} 'Clear spot N': finished (skipped)", Assert.Single(lines));
    }

    [Fact]
    public async Task GetPlayback_reports_stopped_after_StopMacro()
```

In `tests/rororo-ur-task.Tests/Ipc/MacroRunnerServerTests.cs`, replace the usings at the top:

```csharp
using System.IO.Pipes;
using System.Text.Json;
using Labs626.UrTask.Ipc;
```

with:

```csharp
using System.IO.Pipes;
using System.Text.Json;
using Labs626.UrTask.Ipc;
using Labs626.UrTask.Macros;
using Labs626.UrTask.PluginHost;
```

and add inside the class, before its final closing brace:

```csharp
    // Pin for Ur OCR's pulse loop (controller ruling, 2026-09-28): a playback that pressed nothing
    // because reach checks skipped reads exactly this on the wire; a normal finish has no reason.
    [Fact]
    public async Task GetPlayback_over_the_pipe_reports_a_skip_and_a_clean_finish_without_a_reason()
    {
        var alt = new AccountRegistry.AccountInfo(1123, 123, "alt-123", "acct-123");
        var macro = new Macro(SchemaVersion: 2, Id: Guid.NewGuid().ToString(), Name: "Clear spot N", RecordMode: "PerWindow",
            RecordedAgainstUserId: null, RecordedAgainstDisplayName: null, InterAltDelayMs: null, RecordedAtUnixMs: 0,
            Events: new List<MacroEvent>());
        var passes = new Queue<SequenceResult>(new[]
        {
            new SequenceResult(new[] { new AltOutcome(alt, PlaybackOutcome.Completed, null, SkippedByReach: true) }, 1, 0, 0, TimeSpan.Zero),
            new SequenceResult(new[] { new AltOutcome(alt, PlaybackOutcome.Completed, null) }, 1, 0, 0, TimeSpan.Zero),
        });
        var invoker = new MacroRunInvoker(
            loadMacros: () => new[] { macro },
            snapshot: () => new[] { alt },
            resolveForegroundUserId: () => alt.RobloxUserId,
            isBusy: () => false,
            playWithResult: (_, _, _, _) => Task.FromResult<SequenceResult?>(passes.Dequeue()));
        var server = new MacroRunnerServer(invoker);

        async Task<string> RunAndReadAsync()
        {
            var run = await invoker.RunAsync(new RunMacroRequest("1.0", "RunMacro", macro.Id, new[] { "123" }, null, "626labs.ur-ocr"), default);
            for (int i = 0; i < 200 && invoker.ActivePlaybackCount > 0; i++) await Task.Delay(10);
            Assert.Equal(0, invoker.ActivePlaybackCount);
            return await RoundTripJsonAsync(server,
                $"{{\"contractVersion\":\"1.0\",\"method\":\"GetPlayback\",\"playbackId\":\"{run.PlaybackId}\",\"callerPluginId\":\"626labs.ur-ocr\"}}");
        }

        Assert.Equal("{\"ok\":true,\"state\":\"finished\",\"reason\":\"skipped\"}", await RunAndReadAsync());
        Assert.Equal("{\"ok\":true,\"state\":\"finished\"}", await RunAndReadAsync());
    }
```

- [ ] **Step 2: Run the tests to verify they fail**

Run: `dotnet test tests/rororo-ur-task.Tests/rororo-ur-task.Tests.csproj -p:StandaloneTestsOnly=true`
Expected: build FAILS with `CS1061: 'PlaybackResult' does not contain a definition for 'SkippedByReach'` (and `CompletedSkippedByReach`, and `AltOutcome` has no parameter `SkippedByReach`).

- [ ] **Step 3: `PlaybackResult` and `AltOutcome` carry the skip**

In `src/Macros/MacroPlayer.cs`, replace:

```csharp
    public static PlaybackResult Skipped(string reason) => new(PlaybackOutcome.Skipped, reason);
```

with:

```csharp
    public static PlaybackResult Skipped(string reason) => new(PlaybackOutcome.Skipped, reason);

    /// <summary>
    /// A Completed run that pressed nothing because reach checks skipped: at least one step was
    /// skipped by its reach check, and no mouse button and no key went down at all (no MouseDown
    /// or KeyDown was sent by any step, raw steps included; pointer moves, waits and wheel notches
    /// are not presses). Only StepRunner sets it. Not <see cref="PlaybackOutcome.Skipped"/>, which
    /// means the sequence aborted before this alt. The bridge reports it as finished/skipped so
    /// Ur OCR's pulse loop can tell a skipped Clear spot from a mined one.
    /// </summary>
    public bool SkippedByReach { get; init; }

    public static PlaybackResult CompletedSkippedByReach() => new(PlaybackOutcome.Completed, null) { SkippedByReach = true };
```

In `src/Macros/SequenceTypes.cs`, replace:

```csharp
    int? StepIndex = null);                      // 0-based step that stopped a v4 macro
```

with:

```csharp
    int? StepIndex = null,                       // 0-based step that stopped a v4 macro
    bool SkippedByReach = false);                // Completed, but pressed nothing: reach checks skipped (PlaybackResult.SkippedByReach)
```

In `src/Macros/SequencePlayer.cs`, replace:

```csharp
                perAlt.Add(new AltOutcome(target, playResult.Outcome, playResult.Reason, playResult.StepIndex));
```

with:

```csharp
                perAlt.Add(new AltOutcome(target, playResult.Outcome, playResult.Reason, playResult.StepIndex, playResult.SkippedByReach));
```

- [ ] **Step 4: `StepRunner` tallies presses and reach skips**

In `src/Macros/Steps/StepRunner.cs`, inside `StepRunner`, add after the `InputBlockedException` class:

```csharp

    /// <summary>What a run did, for <see cref="PlaybackResult.SkippedByReach"/>.</summary>
    private sealed class RunTally
    {
        public bool Pressed;
        public int ReachSkips;
    }

    /// <summary>Forwards everything to the real IO and notes any button or key going down, so a
    /// press by any step kind (raw steps included) counts. Releases in RunAsync's finally are ups,
    /// never downs.</summary>
    private sealed class PressWatchIo(IStepIo inner, RunTally tally) : IStepIo
    {
        public bool Send(MacroEvent clientEvent)
        {
            if (clientEvent.Kind is MacroEventKind.MouseDown or MacroEventKind.KeyDown) tally.Pressed = true;
            return inner.Send(clientEvent);
        }
        public bool ReleaseButton(int button) => inner.ReleaseButton(button);
        public bool MoveRelative(int dx, int dy) => inner.MoveRelative(dx, dy);
        public (int X, int Y)? CursorClient() => inner.CursorClient();
        public (int W, int H)? ClientSize() => inner.ClientSize();
        public PixelBlock? Capture(int clientX, int clientY, int w, int h) => inner.Capture(clientX, clientY, w, h);
        public bool TargetInForeground() => inner.TargetInForeground();
        public Task Delay(int ms, CancellationToken ct) => inner.Delay(ms, ct);
        public long NowMs => inner.NowMs;
    }
```

In `RunAsync`, replace:

```csharp
        if (invalid is not null) return PlaybackResult.Refused(invalid);

        var heldKeys = new HashSet<int>();
```

with:

```csharp
        if (invalid is not null) return PlaybackResult.Refused(invalid);

        var tally = new RunTally();
        io = new PressWatchIo(io, tally);
        var heldKeys = new HashSet<int>();
```

replace:

```csharp
                    case PointStep p:
                        await PlayPointAsync(p, i, ctx, io, heldButtons, ct);
                        break;
```

with:

```csharp
                    case PointStep p:
                        await PlayPointAsync(p, i, ctx, io, heldButtons, tally, ct);
                        break;
```

replace:

```csharp
                    case HoldStep h:
                        await PlayHoldAsync(h, i, ctx, io, heldButtons, ct);
                        break;
```

with:

```csharp
                    case HoldStep h:
                        await PlayHoldAsync(h, i, ctx, io, heldButtons, tally, ct);
                        break;
```

and replace:

```csharp
            return PlaybackResult.Completed();
        }
        catch (InputBlockedException b)
```

with:

```csharp
            return tally.ReachSkips > 0 && !tally.Pressed ? PlaybackResult.CompletedSkippedByReach() : PlaybackResult.Completed();
        }
        catch (InputBlockedException b)
```

In `PlayPointAsync`, replace its signature line:

```csharp
    private static async Task PlayPointAsync(PointStep p, int index, StepContext ctx, IStepIo io, HashSet<int> heldButtons, CancellationToken ct)
```

with:

```csharp
    private static async Task PlayPointAsync(PointStep p, int index, StepContext ctx, IStepIo io, HashSet<int> heldButtons, RunTally tally, CancellationToken ct)
```

and replace (Task 3's call):

```csharp
            await PlayReachedPointAsync(p, at, index, ctx, io, heldButtons, ct);
```

with:

```csharp
            await PlayReachedPointAsync(p, at, index, ctx, io, heldButtons, tally, ct);
```

In `PlayReachedPointAsync` (Task 3), replace its signature line:

```csharp
    private static async Task PlayReachedPointAsync(PointStep p, (int X, int Y) at, int index, StepContext ctx, IStepIo io, HashSet<int> heldButtons, CancellationToken ct)
```

with:

```csharp
    private static async Task PlayReachedPointAsync(PointStep p, (int X, int Y) at, int index, StepContext ctx, IStepIo io, HashSet<int> heldButtons, RunTally tally, CancellationToken ct)
```

and replace:

```csharp
        if (!seen) return;
```

with:

```csharp
        if (!seen) { tally.ReachSkips++; return; }
```

In `PlayHoldAsync` (Task 2), replace its signature line:

```csharp
    private static async Task PlayHoldAsync(HoldStep h, int index, StepContext ctx, IStepIo io, HashSet<int> heldButtons, CancellationToken ct)
```

with:

```csharp
    private static async Task PlayHoldAsync(HoldStep h, int index, StepContext ctx, IStepIo io, HashSet<int> heldButtons, RunTally tally, CancellationToken ct)
```

and replace:

```csharp
            if (!seen0) return;
```

with:

```csharp
            if (!seen0) { tally.ReachSkips++; return; }
```

- [ ] **Step 5: The log lines and the bridge state**

In `src/Macros/PlaybackEndLog.cs`, replace:

```csharp
        if (result.Outcome == PlaybackOutcome.Completed) return $"playback finished: '{name}' on {account} in {secs} s.";
```

with:

```csharp
        if (result.Outcome == PlaybackOutcome.Completed)
            return result.SkippedByReach
                ? $"playback finished (skipped: no outline): '{name}' on {account} in {secs} s."
                : $"playback finished: '{name}' on {account} in {secs} s.";
```

In `src/Ipc/MacroRunInvoker.cs`, in `ObservePlaybackAsync`, replace:

```csharp
                var failure = last?.PerAlt.FirstOrDefault(a => a.StepIndex is not null)
                           ?? last?.PerAlt.FirstOrDefault(a => a.Outcome is PlaybackOutcome.Aborted or PlaybackOutcome.Refused);
```

with:

```csharp
                var failure = last?.PerAlt.FirstOrDefault(a => a.StepIndex is not null)
                           ?? last?.PerAlt.FirstOrDefault(a => a.Outcome is PlaybackOutcome.Aborted or PlaybackOutcome.Refused);
                // Pressed nothing on any alt because reach checks skipped: finished/skipped, so Ur
                // OCR's pulse loop can tell a skipped Clear spot from a mined one (controller
                // ruling, 2026-09-28). Additive: a finished run carried no reason before.
                if (failure is null && !playbackCts.IsCancellationRequested
                    && last?.PerAlt is { Count: > 0 } alts
                    && alts.All(a => a.Outcome == PlaybackOutcome.Completed && a.SkippedByReach))
                    reason = "skipped";
```

`_registry.Finished(playbackId, state, reason, detail, stepIndex)` and the bridge log line in the `finally` already carry `reason`, so nothing else changes there.

In `src/Ipc/BridgeContract.cs`, replace:

```csharp
/// <summary>State is running | finished | stopped | failed. On failed, Reason is a short code
/// (check-failed, refused, aborted, error) and Detail is the sentence Claude shows.</summary>
```

with:

```csharp
/// <summary>State is running | finished | stopped | failed. On failed, Reason is a short code
/// (check-failed, refused, aborted, error) and Detail is the sentence Claude shows. On finished,
/// Reason is null, or "skipped" when the playback pressed nothing because reach checks skipped
/// on every alt (0.11.0, additive; Ur MCP ignores the reason of a finished run).</summary>
```

- [ ] **Step 6: Run the tests to verify they pass**

Run: `dotnet test tests/rororo-ur-task.Tests/rororo-ur-task.Tests.csproj -p:StandaloneTestsOnly=true --filter "FullyQualifiedName~StepRunnerTests|FullyQualifiedName~PlaybackEndLogTests|FullyQualifiedName~SequencePlayerTests|FullyQualifiedName~MacroRunInvokerTests|FullyQualifiedName~MacroRunnerServerTests"`
Expected: all PASS.

Run: `dotnet test tests/rororo-ur-task.Tests/rororo-ur-task.Tests.csproj -p:StandaloneTestsOnly=true`
Expected: 498 passed, 0 failed.

- [ ] **Step 7: Commit**

```bash
git add src/Macros/MacroPlayer.cs src/Macros/SequenceTypes.cs src/Macros/SequencePlayer.cs src/Macros/Steps/StepRunner.cs src/Macros/PlaybackEndLog.cs src/Ipc/MacroRunInvoker.cs src/Ipc/BridgeContract.cs tests/rororo-ur-task.Tests/Steps/StepRunnerTests.cs tests/rororo-ur-task.Tests/PlaybackEndLogTests.cs tests/rororo-ur-task.Tests/SequencePlayerTests.cs tests/rororo-ur-task.Tests/Ipc/MacroRunInvokerTests.cs tests/rororo-ur-task.Tests/Ipc/MacroRunnerServerTests.cs
git commit -m "feat(bridge): a playback that pressed nothing reports finished/skipped

Co-Authored-By: Claude Opus 5.5 <noreply@anthropic.com>"
```

---

### Task 5: Pulse macros: Clear spot N..NW, and reach on every ring hold

**Files:**
- Modify: `docs/reference/events/macros/space-mine-ore-stop/measured.json`
- Modify: `docs/reference/events/macros/space-mine-ore-stop/generate.ps1`
- Regenerate: `docs/reference/events/macros/space-mine-ore-stop/macros/*.json`
- Replace: `tests/rororo-ur-task.Tests/Steps/OreStopExampleMacrosTests.cs`

**Interfaces:**
- Consumes (Task 1): `HoldStep.Reach`, `OutlineCheck(CheckBox Box, int MinCount, int WhiteMin = 225)` with value equality; `StepValidator.Validate` accepting reach boxes up to 120x120.
- Produces: 20 committed example macros. New: `Clear spot <name>` for N NE E SE S SW W NW with ids `0e5a0000-0000-4000-8000-0000000000{21..28}` in that order, each exactly one `hold` step: `delayMs` 0, `id` `spot-<name>`, `label` `Spot <name>`, `button` 1, `check` from `ring.box`/`ring.tolerance`, `maxMs` absent, `reach` from `ring.reach` (or the spot's own `reach`). The reach box is centred: `offsetX = -floor(w/2)`, `offsetY = -floor(h/2)`. `Mine spot <name>` keep their ids and three steps, and their hold gains the same `reach`. For Ur OCR: the pulse loop calls `Clear spot <name>` by name; a Clear spot whose outline check skipped reports GetPlayback `finished` with reason `skipped` (Task 4), a mined one `finished` with no reason.

- [ ] **Step 1: Replace the example test with one that expects the pulse macros**

Replace the whole of `tests/rororo-ur-task.Tests/Steps/OreStopExampleMacrosTests.cs` with:

```csharp
using System.IO;
using System.Text.Json;
using Labs626.UrTask.Macros;
using Labs626.UrTask.Macros.Steps;

namespace Labs626.UrTask.Tests.Steps;

/// <summary>
/// The ore-stop example macros are written by generate.ps1 from measured.json. These tests read
/// the committed files the way Ur Task does and hold them to the measured values, so an edit to
/// measured.json without a regenerate fails here, and so does any press outside the named points.
/// </summary>
public class OreStopExampleMacrosTests
{
    private static readonly string[] RingNames = { "N", "NE", "E", "SE", "S", "SW", "W", "NW" };

    private static string ExampleDir()
    {
        var dir = new DirectoryInfo(AppContext.BaseDirectory);
        while (dir is not null && !File.Exists(Path.Combine(dir.FullName, "rororo-ur-task.csproj"))) dir = dir.Parent;
        Assert.True(dir is not null, "Could not find the repo root (walked up from the test binary looking for rororo-ur-task.csproj).");
        return Path.Combine(dir!.FullName, "docs", "reference", "events", "macros", "space-mine-ore-stop");
    }

    private static (IReadOnlyList<Macro> Macros, JsonElement Measured) Load()
    {
        var dir = ExampleDir();
        var macrosDir = Path.Combine(dir, "macros");
        Assert.True(Directory.Exists(macrosDir), $"No generated macros at {macrosDir}. Run generate.ps1.");
        var result = new MacroStore(macrosDir).LoadAll();
        Assert.Empty(result.Failures);
        using var doc = JsonDocument.Parse(File.ReadAllText(Path.Combine(dir, "measured.json")));
        return (result.Macros, doc.RootElement.Clone());
    }

    private static (int X, int Y) Xy(JsonElement e) => (e.GetProperty("x").GetInt32(), e.GetProperty("y").GetInt32());
    private static Rgb Colour(JsonElement e) => new(e.GetProperty("r").GetInt32(), e.GetProperty("g").GetInt32(), e.GetProperty("b").GetInt32());
    private static CheckBox BoxOf(JsonElement e) => new(e.GetProperty("offsetX").GetInt32(), e.GetProperty("offsetY").GetInt32(), e.GetProperty("w").GetInt32(), e.GetProperty("h").GetInt32());

    /// <summary>The spot's own reach when it has one, else the ring's shared default; the box is
    /// centred on the spot (generate.ps1's rule).</summary>
    private static OutlineCheck ReachOf(JsonElement ring, JsonElement spot)
    {
        var r = spot.TryGetProperty("reach", out var own) && own.ValueKind != JsonValueKind.Null ? own : ring.GetProperty("reach");
        int w = r.GetProperty("w").GetInt32(), h = r.GetProperty("h").GetInt32();
        return new OutlineCheck(new CheckBox(-(w / 2), -(h / 2), w, h), r.GetProperty("minCount").GetInt32(), r.GetProperty("whiteMin").GetInt32());
    }

    /// <summary>A ring hold: on the spot, the ring's colour box and tolerance, no time limit
    /// (spec decision 5), and the outline check.</summary>
    private static void AssertSpotHold(HoldStep hold, JsonElement ring, JsonElement spot)
    {
        var n = spot.GetProperty("name").GetString();
        Assert.Equal($"spot-{n}", hold.Id);
        Assert.Equal(Xy(spot), (hold.X, hold.Y));
        Assert.Equal(1, hold.Button);
        Assert.Null(hold.MaxMs);
        Assert.Equal(BoxOf(ring.GetProperty("box")), hold.Check!.Box);
        Assert.Equal(ring.GetProperty("tolerance").GetInt32(), hold.Check.Tolerance);
        Assert.Equal(ReachOf(ring, spot), hold.Reach);
    }

    /// <summary>The pickaxe press, gated on the dot: skipped only when the dot shows the other
    /// state, so a screen covered by a popup or captcha stops the macro before it presses.</summary>
    private static void AssertDot(FirstMatchStep f, JsonElement am, bool expectGreen)
    {
        Assert.Equal(NoMatchAction.SkipIfOther, f.OnNoMatch);
        var c = Assert.Single(f.Candidates);
        Assert.Equal(Xy(am.GetProperty("pickaxe")), (c.X, c.Y));
        Assert.Equal(BoxOf(am.GetProperty("dotBox")), c.Check!.Box);
        var green = Colour(am.GetProperty("green"));
        var red = Colour(am.GetProperty("red"));
        Assert.Equal(expectGreen ? green : red, c.Check.Expect);
        Assert.Equal(expectGreen ? red : green, c.Check.Other);
        Assert.Equal(am.GetProperty("tolerance").GetInt32(), c.Check.Tolerance);
    }

    [Fact]
    public void Every_example_loads_validates_and_is_saved_as_its_id()
    {
        var (macros, m) = Load();
        var expected = new[] { "Auto Mine off (checked)", "Auto Mine on (checked)", "Camera top-down", "Go to Top" }
            .Concat(RingNames.Select(n => $"Mine spot {n}"))
            .Concat(RingNames.Select(n => $"Clear spot {n}"))
            .OrderBy(s => s, StringComparer.Ordinal);
        Assert.Equal(expected, macros.Select(x => x.Name!).OrderBy(s => s, StringComparer.Ordinal));

        var client = m.GetProperty("client");
        foreach (var macro in macros)
        {
            Assert.Null(StepValidator.Validate(macro.Steps!));
            Assert.Equal(Macro.CurrentSchemaVersion, macro.SchemaVersion);
            Assert.True(macro.IsClientSpace);
            Assert.Empty(macro.Events);
            Assert.Equal((client.GetProperty("w").GetInt32(), client.GetProperty("h").GetInt32()), (macro.RecordedClientW!.Value, macro.RecordedClientH!.Value));
            Assert.Equal(client.GetProperty("displayScale").GetInt32(), macro.RecordedDisplayScale);
            Assert.True(File.Exists(Path.Combine(ExampleDir(), "macros", macro.Id + ".json")), $"'{macro.Name}' is not saved as <id>.json");
        }
    }

    [Fact]
    public void Each_mine_spot_turns_Auto_Mine_off_holds_its_ring_spot_and_turns_it_back_on()
    {
        var (macros, m) = Load();
        var ring = m.GetProperty("ring");
        var am = m.GetProperty("autoMine");
        foreach (var spot in ring.GetProperty("spots").EnumerateArray())
        {
            var n = spot.GetProperty("name").GetString();
            var macro = Assert.Single(macros, x => x.Name == $"Mine spot {n}");
            Assert.Equal(3, macro.Steps!.Count);
            AssertDot(Assert.IsType<FirstMatchStep>(macro.Steps[0]), am, expectGreen: true);
            var hold = Assert.IsType<HoldStep>(macro.Steps[1]);
            AssertSpotHold(hold, ring, spot);
            Assert.Equal(ring.GetProperty("holdDelayMs").GetInt32(), hold.DelayMs);
            AssertDot(Assert.IsType<FirstMatchStep>(macro.Steps[2]), am, expectGreen: false);
        }
    }

    [Fact]
    public void Each_clear_spot_holds_its_ring_spot_behind_the_outline_and_leaves_Auto_Mine_alone()
    {
        // Pulse mode (ore stop v1): the Ur OCR loop owns Auto Mine, so a clear macro is the hold
        // and nothing else. Ids are fixed so Ur OCR and point adjustments survive a regenerate.
        var (macros, m) = Load();
        var ring = m.GetProperty("ring");
        var k = 0;
        foreach (var spot in ring.GetProperty("spots").EnumerateArray())
        {
            var n = spot.GetProperty("name").GetString();
            Assert.Equal(RingNames[k], n);
            var macro = Assert.Single(macros, x => x.Name == $"Clear spot {n}");
            Assert.Equal($"0e5a0000-0000-4000-8000-0000000000{21 + k}", macro.Id);
            var hold = Assert.IsType<HoldStep>(Assert.Single(macro.Steps!));
            AssertSpotHold(hold, ring, spot);
            Assert.Equal(0, hold.DelayMs); // the loop has already paused for effects to settle
            k++;
        }
    }

    [Fact]
    public void The_checked_Auto_Mine_toggles_press_only_on_the_right_dot()
    {
        var (macros, m) = Load();
        var am = m.GetProperty("autoMine");
        AssertDot(Assert.IsType<FirstMatchStep>(Assert.Single(Assert.Single(macros, x => x.Name == "Auto Mine off (checked)").Steps!)), am, expectGreen: true);
        AssertDot(Assert.IsType<FirstMatchStep>(Assert.Single(Assert.Single(macros, x => x.Name == "Auto Mine on (checked)").Steps!)), am, expectGreen: false);
    }

    [Fact]
    public void Camera_top_down_drags_past_the_pitch_limit_then_counts_back()
    {
        var (macros, m) = Load();
        var cam = m.GetProperty("camera");
        var steps = Assert.Single(macros, x => x.Name == "Camera top-down").Steps!;
        var start = Xy(cam.GetProperty("start"));
        // The 0.9.0 camera margin, on the vertical component only.
        var dy = (int)Math.Ceiling(cam.GetProperty("pitchTravelPx").GetInt32() * StepConverter.CameraDragMargin);

        var down = Assert.IsType<DragStep>(steps[0]);
        Assert.Equal((2, start.X, start.Y, 0, dy), (down.Button, down.StartX, down.StartY, down.Dx, down.Dy));
        Assert.True(down.StartY + down.Dy < m.GetProperty("client").GetProperty("h").GetInt32(), "The drag leaves the client area.");

        var countBack = cam.GetProperty("countBackPx").GetInt32();
        if (countBack == 0) { Assert.Single(steps); return; }
        var back = Assert.IsType<DragStep>(steps[1]);
        Assert.Equal((2, start.X, start.Y + dy, 0, -countBack), (back.Button, back.StartX, back.StartY, back.Dx, back.Dy));
    }

    [Fact]
    public void Go_to_Top_presses_the_button_waits_then_turns_Auto_Mine_on()
    {
        var (macros, m) = Load();
        var g = m.GetProperty("goToTop");
        var steps = Assert.Single(macros, x => x.Name == "Go to Top").Steps!;
        Assert.Equal(3, steps.Count);
        var press = Assert.IsType<PointStep>(steps[0]);
        Assert.Equal(Xy(g), (press.X, press.Y));
        var check = g.GetProperty("check");
        Assert.Equal(check.ValueKind != JsonValueKind.Null, press.CheckEnabled);
        if (check.ValueKind != JsonValueKind.Null)
        {
            // Ur OCR fills this in once the sweep measures it; an edit to measured.json without a
            // regenerate must fail here, same as every other measured value in this file.
            Assert.Equal(BoxOf(check.GetProperty("box")), press.Check!.Box);
            Assert.Equal(Colour(check.GetProperty("expect")), press.Check.Expect);
            Assert.Equal(check.GetProperty("tolerance").GetInt32(), press.Check.Tolerance);
            Assert.Null(press.Check.Other); // generate.ps1 never writes an "other" for Go to Top
        }
        Assert.Equal(g.GetProperty("settleMs").GetInt32(), Assert.IsType<WaitStep>(steps[1]).DelayMs);
        AssertDot(Assert.IsType<FirstMatchStep>(steps[2]), m.GetProperty("autoMine"), expectGreen: false);
    }

    [Fact]
    public void Nothing_presses_outside_the_named_points()
    {
        // Never a captcha, the Enchant Machine or an invite popup (ore-stop spec). Every press lands
        // on the pickaxe, Go to Top or a ring spot; the only other input is the right-button drag.
        var (macros, m) = Load();
        var allowed = new HashSet<(int, int)> { Xy(m.GetProperty("autoMine").GetProperty("pickaxe")), Xy(m.GetProperty("goToTop")) };
        foreach (var s in m.GetProperty("ring").GetProperty("spots").EnumerateArray()) allowed.Add(Xy(s));

        foreach (var macro in macros)
        foreach (var step in macro.Steps!)
        {
            switch (step)
            {
                case PointStep p: Assert.Contains((p.X, p.Y), allowed); break;
                case HoldStep h: Assert.Contains((h.X, h.Y), allowed); break;
                case FirstMatchStep f: Assert.All(f.Candidates, c => Assert.Contains((c.X, c.Y), allowed)); break;
                case DragStep d: Assert.Equal(2, d.Button); break;
                case WaitStep: break;
                default: Assert.Fail($"'{macro.Name}' has a {step.GetType().Name}; the examples use only points, holds, first matches, right drags and waits."); break;
            }
        }
    }
}
```

- [ ] **Step 2: Run the tests to verify they fail**

Run: `dotnet test tests/rororo-ur-task.Tests/rororo-ur-task.Tests.csproj -p:StandaloneTestsOnly=true --filter "FullyQualifiedName~OreStopExampleMacrosTests"`
Expected: `Every_example_loads_validates_and_is_saved_as_its_id` FAILS (no Clear spot macros), `Each_mine_spot_...` and `Each_clear_spot_...` FAIL (`KeyNotFoundException` for `ring.reach`, or no Clear spot macro).

- [ ] **Step 3: Add the reach group to `measured.json`**

Replace the whole of `docs/reference/events/macros/space-mine-ore-stop/measured.json` with:

```json
{
  "about": "Ore-stop macro inputs: client-space pixels in an 800x599 client at 100% display scale. Edit, then run generate.ps1. A group whose measuredOn is null is provisional; the Ur OCR capture sweep measures ring and camera, and the live outline measurement (pointer on and off a breakable block) measures ring.reach. ring.reach is the shared default for every spot; a spot may carry its own reach { minCount, whiteMin, w, h }, which replaces the default wholesale.",
  "recordedAtUnixMs": 1790467200000,
  "client": { "w": 800, "h": 599, "displayScale": 100 },
  "game": { "placeId": 8737899170 },
  "autoMine": {
    "measuredOn": "2026-09-27",
    "pickaxe": { "x": 40, "y": 300 },
    "dotBox": { "offsetX": 15, "offsetY": -11, "w": 3, "h": 3 },
    "red": { "r": 255, "g": 19, "b": 90 },
    "green": { "r": 125, "g": 246, "b": 13 },
    "tolerance": 15
  },
  "goToTop": { "measuredOn": "2026-09-27", "x": 400, "y": 50, "settleMs": 3000, "check": null },
  "ring": {
    "measuredOn": null,
    "holdDelayMs": 300,
    "box": { "offsetX": -2, "offsetY": -2, "w": 5, "h": 5 },
    "tolerance": 15,
    "reach": { "measuredOn": null, "minCount": 60, "whiteMin": 225, "w": 80, "h": 80 },
    "spots": [
      { "name": "N",  "x": 400, "y": 244 },
      { "name": "NE", "x": 456, "y": 244 },
      { "name": "E",  "x": 456, "y": 300 },
      { "name": "SE", "x": 456, "y": 356 },
      { "name": "S",  "x": 400, "y": 356 },
      { "name": "SW", "x": 344, "y": 356 },
      { "name": "W",  "x": 344, "y": 300 },
      { "name": "NW", "x": 344, "y": 244 }
    ]
  },
  "camera": {
    "measuredOn": null,
    "start": { "x": 400, "y": 150 },
    "pitchTravelPx": 280,
    "dragMs": 400,
    "countBackPx": 40,
    "countBackMs": 200
  }
}
```

The only changes are the `about` sentence and the `ring.reach` line; every other value is unchanged.

- [ ] **Step 4: Update the generator**

Replace the whole of `docs/reference/events/macros/space-mine-ore-stop/generate.ps1` with:

```powershell
#Requires -Version 7
<#
.SYNOPSIS
  Writes the ore-stop example macros (agent-authored v4) from measured.json.

.DESCRIPTION
  One file of measured values drives every macro, so a re-measure is one edit and one run:

      pwsh -NoProfile -File docs/reference/events/macros/space-mine-ore-stop/generate.ps1

  The Ur OCR capture sweep fills the "ring" and "camera" groups and sets their "measuredOn"; the
  live outline measurement fills "ring.reach". A group whose measuredOn is null is provisional:
  the script warns and writes it anyway.

  Two families of ring macros, one per spot:
    - "Mine spot <name>": Auto Mine off, hold the spot, Auto Mine on (watch-while-riding).
    - "Clear spot <name>": hold the spot and nothing else (ore stop v1, pulse). The Ur OCR loop
      owns Auto Mine in pulse mode, so these never touch the pickaxe.
  Every ring hold carries a reach check: with the pointer on the spot, no white outline means no
  press, and the outline gone mid-hold lets go.

  Install: copy macros\*.json into %LOCALAPPDATA%\626Labs\RoRoRoUrTask\macros, then restart
  Ur Task. Each file is named <id>.json, the store's own convention, so deleting a macro from the
  library still works. The ids are fixed below so Ur OCR triggers and point adjustments survive a
  regenerate.

  Nothing here presses anything but the Auto Mine pickaxe, Go to Top, the eight ring spots, and a
  right-button camera drag. Never add a step that clicks a captcha, the Enchant Machine or an
  invite popup; OreStopExampleMacrosTests fails on any press outside those points.
#>
$ErrorActionPreference = 'Stop'
Set-StrictMode -Version Latest

$m = Get-Content -Raw -Path (Join-Path $PSScriptRoot 'measured.json') | ConvertFrom-Json
$out = Join-Path $PSScriptRoot 'macros'
New-Item -ItemType Directory -Force -Path $out | Out-Null
Get-ChildItem -Path $out -Filter '*.json' | Remove-Item

# Same margin as StepConverter.CameraDragMargin (0.9.0): overshoot the pitch limit, vertical only.
$CameraDragMargin = 1.5
$RingNames = @('N', 'NE', 'E', 'SE', 'S', 'SW', 'W', 'NW')

$Ids = @{
    'Auto Mine off (checked)' = '0e5a0000-0000-4000-8000-000000000001'
    'Auto Mine on (checked)'  = '0e5a0000-0000-4000-8000-000000000002'
    'Camera top-down'         = '0e5a0000-0000-4000-8000-000000000003'
    'Go to Top'               = '0e5a0000-0000-4000-8000-000000000004'
    'Mine spot N'             = '0e5a0000-0000-4000-8000-000000000011'
    'Mine spot NE'            = '0e5a0000-0000-4000-8000-000000000012'
    'Mine spot E'             = '0e5a0000-0000-4000-8000-000000000013'
    'Mine spot SE'            = '0e5a0000-0000-4000-8000-000000000014'
    'Mine spot S'             = '0e5a0000-0000-4000-8000-000000000015'
    'Mine spot SW'            = '0e5a0000-0000-4000-8000-000000000016'
    'Mine spot W'             = '0e5a0000-0000-4000-8000-000000000017'
    'Mine spot NW'            = '0e5a0000-0000-4000-8000-000000000018'
    'Clear spot N'            = '0e5a0000-0000-4000-8000-000000000021'
    'Clear spot NE'           = '0e5a0000-0000-4000-8000-000000000022'
    'Clear spot E'            = '0e5a0000-0000-4000-8000-000000000023'
    'Clear spot SE'           = '0e5a0000-0000-4000-8000-000000000024'
    'Clear spot S'            = '0e5a0000-0000-4000-8000-000000000025'
    'Clear spot SW'           = '0e5a0000-0000-4000-8000-000000000026'
    'Clear spot W'            = '0e5a0000-0000-4000-8000-000000000027'
    'Clear spot NW'           = '0e5a0000-0000-4000-8000-000000000028'
}

foreach ($group in 'autoMine', 'goToTop', 'ring', 'camera') {
    if ($null -eq $m.$group.measuredOn) {
        Write-Warning "$group is provisional (measuredOn is null). The Ur OCR capture sweep measures it; regenerate after."
    }
}
if ($null -eq $m.ring.reach.measuredOn) {
    Write-Warning "ring.reach is provisional (measuredOn is null). Measure the outline live with the pointer on and off a breakable block; regenerate after."
}
if ($null -eq $m.goToTop.check) {
    Write-Warning "goToTop.check is null: the Go to Top press is unchecked until the Ur OCR capture sweep measures it; regenerate after."
}

function Rgb($c) { [ordered]@{ r = [int]$c.r; g = [int]$c.g; b = [int]$c.b } }
function Box($b) { [ordered]@{ offsetX = [int]$b.offsetX; offsetY = [int]$b.offsetY; w = [int]$b.w; h = [int]$b.h } }

# The outline check for one spot: the spot's own reach if it has one, else the ring's shared
# default, replaced wholesale. The box is centred on the spot.
function Reach($spot) {
    $r = $m.ring.reach
    $own = $spot.PSObject.Properties['reach']
    if ($null -ne $own -and $null -ne $own.Value) { $r = $own.Value }
    $w = [int]$r.w
    $h = [int]$r.h
    [ordered]@{
        box      = [ordered]@{ offsetX = 0 - [int][math]::Floor($w / 2); offsetY = 0 - [int][math]::Floor($h / 2); w = $w; h = $h }
        minCount = [int]$r.minCount
        whiteMin = [int]$r.whiteMin
    }
}

# A ring hold: press on the spot while its colour holds, behind the outline check. No maxMs: ore
# is never abandoned for taking long (spec decision 5); the outline going is the way out.
function SpotHold($spot, [int]$delayMs) {
    $ring = $m.ring
    [ordered]@{
        kind    = 'hold'
        delayMs = $delayMs
        id      = "spot-$($spot.name)"
        label   = "Spot $($spot.name)"
        x       = [int]$spot.x
        y       = [int]$spot.y
        button  = 1
        check   = [ordered]@{ box = (Box $ring.box); tolerance = [int]$ring.tolerance }
        reach   = (Reach $spot)
    }
}

# The pickaxe press, gated on the dot. skipIfOther: skip when the dot already shows the other
# state; stop with a report when it shows neither (a popup or captcha over the screen).
function DotCheck([string]$id, [string]$label, [string]$want) {
    $a = $m.autoMine
    $green = Rgb $a.green
    $red = Rgb $a.red
    if ($want -eq 'green') { $expect = $green; $other = $red; $candLabel = 'Auto Mine is on (green dot)' }
    else { $expect = $red; $other = $green; $candLabel = 'Auto Mine is off (red dot)' }
    [ordered]@{
        kind       = 'firstMatch'
        delayMs    = 0
        id         = $id
        label      = $label
        candidates = @(
            [ordered]@{
                delayMs      = 0
                id           = "$id-dot"
                label        = $candLabel
                x            = [int]$a.pickaxe.x
                y            = [int]$a.pickaxe.y
                button       = 1
                check        = [ordered]@{ box = (Box $a.dotBox); expect = $expect; other = $other; tolerance = [int]$a.tolerance }
                checkEnabled = $false
            }
        )
        onNoMatch  = 'skipIfOther'
    }
}

function Macro([string]$name, [object[]]$steps) {
    if (-not $Ids.ContainsKey($name)) { throw "No fixed id for macro '$name'." }
    [ordered]@{
        schemaVersion              = 4
        id                         = $Ids[$name]
        name                       = $name
        recordMode                 = 'PerWindow'
        recordedAgainstUserId      = $null
        recordedAgainstDisplayName = 'agent'
        interAltDelayMs            = $null
        recordedAtUnixMs           = [long]$m.recordedAtUnixMs
        events                     = @()
        coordSpace                 = 'client'
        recordedClientW            = [int]$m.client.w
        recordedClientH            = [int]$m.client.h
        recordedMaximized          = $false
        recordedPlaceId            = [long]$m.game.placeId
        allGames                   = $false
        recordedDisplayScale       = [int]$m.client.displayScale
        steps                      = $steps
    }
}

function Write-Macro($macro) {
    $file = $macro.id + '.json'
    $json = $macro | ConvertTo-Json -Depth 20
    [System.IO.File]::WriteAllText((Join-Path $out $file), $json + "`n", [System.Text.UTF8Encoding]::new($false))
    Write-Host "wrote $file  $($macro.name)"
}

# ---- Auto Mine toggles ----
Write-Macro (Macro 'Auto Mine off (checked)' @(DotCheck 'am-off' 'Auto Mine off' 'green'))
Write-Macro (Macro 'Auto Mine on (checked)' @(DotCheck 'am-on' 'Auto Mine on' 'red'))

# ---- The ring ----
$ring = $m.ring
$names = @($ring.spots | ForEach-Object { $_.name })
if ($names.Count -ne 8 -or @(Compare-Object $names $RingNames).Count -ne 0) {
    throw "measured.json ring.spots must name each of N, NE, E, SE, S, SW, W, NW once; it names: $($names -join ', ')."
}

# Mine spot N .. NW: Auto Mine off, hold the spot, Auto Mine on.
foreach ($spot in $ring.spots) {
    Write-Macro (Macro "Mine spot $($spot.name)" @((DotCheck 'am-off' 'Auto Mine off' 'green'), (SpotHold $spot ([int]$ring.holdDelayMs)), (DotCheck 'am-on' 'Auto Mine on' 'red')))
}

# Clear spot N .. NW: the hold alone, no delay. The pulse loop has already stopped Auto Mine and
# paused for effects to settle before it calls these.
foreach ($spot in $ring.spots) {
    Write-Macro (Macro "Clear spot $($spot.name)" @(SpotHold $spot 0))
}

# ---- Camera top-down: right-drag down past the pitch limit, then count back ----
$cam = $m.camera
$sx = [int]$cam.start.x
$sy = [int]$cam.start.y
$dy = [int][math]::Ceiling([double]$cam.pitchTravelPx * $CameraDragMargin)
if ($sy + $dy -ge [int]$m.client.h) {
    throw "The camera drag would end at y=$($sy + $dy), outside the $($m.client.h)-pixel client. Move camera.start up or shorten pitchTravelPx."
}
$camSteps = [System.Collections.Generic.List[object]]::new()
$camSteps.Add([ordered]@{ kind = 'drag'; delayMs = 0; button = 2; startX = $sx; startY = $sy; dx = 0; dy = $dy; durationMs = [int]$cam.dragMs })
if ([int]$cam.countBackPx -gt 0) {
    $camSteps.Add([ordered]@{ kind = 'drag'; delayMs = 150; button = 2; startX = $sx; startY = $sy + $dy; dx = 0; dy = -([int]$cam.countBackPx); durationMs = [int]$cam.countBackMs })
}
Write-Macro (Macro 'Camera top-down' $camSteps.ToArray())

# ---- Go to Top: press it, let the teleport land, Auto Mine on ----
$g = $m.goToTop
$press = [ordered]@{ kind = 'point'; delayMs = 0; id = 'go-to-top'; label = 'Go to Top'; x = [int]$g.x; y = [int]$g.y; button = 1 }
if ($null -ne $g.check) {
    $press.check = [ordered]@{ box = (Box $g.check.box); expect = (Rgb $g.check.expect); tolerance = [int]$g.check.tolerance }
    $press.checkEnabled = $true
}
Write-Macro (Macro 'Go to Top' @($press, [ordered]@{ kind = 'wait'; delayMs = [int]$g.settleMs }, (DotCheck 'am-on' 'Auto Mine on' 'red')))
```

- [ ] **Step 5: Regenerate the macros**

Run: `pwsh -NoProfile -File docs/reference/events/macros/space-mine-ore-stop/generate.ps1`
Expected: four warnings (`ring`, `camera`, `ring.reach` provisional; `goToTop.check` null), then 20 `wrote <id>.json  <name>` lines, including `wrote 0e5a0000-0000-4000-8000-000000000021.json  Clear spot N` through `...028.json  Clear spot NW`.

Run: `git status --short docs/reference/events/macros/space-mine-ore-stop/`
Expected: `M` on `measured.json`, `generate.ps1` and the eight `...001[1-8].json` Mine spot files; `??` on the eight new `...002[1-8].json` files; the four other macro files unchanged.

Open `docs/reference/events/macros/space-mine-ore-stop/macros/0e5a0000-0000-4000-8000-000000000021.json` and check its single step holds `"reach": { "box": { "offsetX": -40, "offsetY": -40, "w": 80, "h": 80 }, "minCount": 60, "whiteMin": 225 }` (whitespace may differ).

- [ ] **Step 6: Run the tests to verify they pass**

Run: `dotnet test tests/rororo-ur-task.Tests/rororo-ur-task.Tests.csproj -p:StandaloneTestsOnly=true --filter "FullyQualifiedName~OreStopExampleMacrosTests"`
Expected: 8 passed.

Run: `dotnet test tests/rororo-ur-task.Tests/rororo-ur-task.Tests.csproj -p:StandaloneTestsOnly=true`
Expected: 499 passed, 0 failed.

- [ ] **Step 7: Commit**

```bash
git add docs/reference/events/macros/space-mine-ore-stop/ tests/rororo-ur-task.Tests/Steps/OreStopExampleMacrosTests.cs
git commit -m "feat(examples): Clear spot macros for the pulse loop, reach on every ring hold

Co-Authored-By: Claude Opus 5.5 <noreply@anthropic.com>"
```

---

### Task 6: Version 0.11.0 and the changelog

**Files:**
- Modify: `rororo-ur-task.csproj`
- Modify: `manifest.json`
- Modify: `CHANGELOG.md`

**Interfaces:**
- Consumes: everything above, shipped.
- Produces: version `0.11.0` in both version files (`VersionConsistencyTests` compares them) and a CHANGELOG section.

- [ ] **Step 1: Bump the version in both files**

In `rororo-ur-task.csproj`, replace `    <Version>0.10.0</Version>` with `    <Version>0.11.0</Version>`.

In `manifest.json`, replace `  "version": "0.10.0",` with `  "version": "0.11.0",`.

- [ ] **Step 2: Add the changelog section**

In `CHANGELOG.md`, replace:

```markdown
## 0.10.0 — unreleased
```

with:

```markdown
## 0.11.0 — unreleased

### Added

- **Reach checks.** A hold or a point can carry a `reach`: with the pointer on the spot, it
  counts the near-white pixels (every channel at 225 or more) in a block-sized box around it and
  needs at least `minCount`. That is the white outline the game draws on a block your pickaxe can
  break. No outline within 300 ms: the step is skipped, not failed, and the log says
  "no outline, skipped" with the count. During a hold, the outline gone for two polls running lets
  go ("outline gone"). The box and the count scale with the window like points.
- **Clear spot macros** for ore stop v1 (pulse) in `docs/reference/events/macros/space-mine-ore-stop/`:
  `Clear spot N` through `Clear spot NW`, each one hold behind a reach check and nothing else,
  because the Ur OCR loop owns Auto Mine in pulse mode.
- **A playback that pressed nothing says so.** When reach checks skipped and no button or key
  went down, `ur-task.log` reads "playback finished (skipped: no outline)", and a bridge playback
  reports `GetPlayback` state `finished` with reason `skipped`. A normal finish has no reason, as
  before.

### Changed

- **Mine spot macros check reach too.** A spot looking through a hole at ore out of reach no
  longer holds forever: no outline, no press, and Auto Mine goes back on.
- The reach values in `measured.json` are provisional until the live outline measurement.

## 0.10.0 — unreleased
```

- [ ] **Step 3: Build and run the full fast suite**

Run: `dotnet build rororo-ur-task.csproj`
Expected: `Build succeeded`, 0 errors.

Run: `dotnet test tests/rororo-ur-task.Tests/rororo-ur-task.Tests.csproj -p:StandaloneTestsOnly=true`
Expected: 499 passed, 0 failed, `VersionConsistencyTests` included. If the count differs from 499 but nothing fails, record the real number in the commit body; the running total assumes every earlier task added exactly the tests it lists.

- [ ] **Step 4: Commit**

```bash
git add rororo-ur-task.csproj manifest.json CHANGELOG.md
git commit -m "chore(release): 0.11.0, reach checks and the pulse clear-spot macros

Co-Authored-By: Claude Opus 5.5 <noreply@anthropic.com>"
```

- [ ] **Step 5: Report**

Report the branch (`feat/ore-stop-pulse`), the final test count, and what the live pass on Dunder-MiffLan still owes: measure `ring.reach` (count with the pointer on and off a breakable block, per spot if they differ), confirm the outline stays drawn while the button is held, then set `ring.reach.measuredOn` and rerun `generate.ps1`.

---

## Self-review

**1. Spec coverage.**
- "The outline check (new in Ur Task)": count near-white pixels (each channel >= 225, a field) in a block-sized rectangle, pass at a threshold: Task 1 (`OutlineCheck`, `CountNearWhite`, validator, JSON, export) and Task 2 (playback).
- "The hold uses it twice": before pressing (Task 2, `Lava_without_a_white_frame_skips_the_hold_and_the_next_step_runs`) and during the hold (Task 2, `A_hold_lets_go_when_the_outline_is_gone_for_two_polls`).
- Hold with no time limit (decision 5) unchanged: examples keep `maxMs` null (Task 5, `AssertSpotHold`).
- "Pause, then clear ... outlined: hold until it breaks; not outlined: skip": Clear spot macros (Task 5) plus the skip (Task 2). Per-spot iteration and ore-then-stone order belong to the Ur OCR loop.
- The rectangle scales like points: Task 1 (`ScaledRect`, `ScaledCount`) and Task 2 (`Reach_scales_with_the_window`).
- Reach on a point step (caller scope): Task 3.
- Testing, Ur Task line: "a thin white frame passes, averaged lava does not; the hold's before and during use": Task 2.
- Measured values per spot, provisional: Task 5 (`ring.reach`, per-spot override, generator warning).
- Controller ruling, finished/skipped on the bridge for a playback that pressed nothing: Task 4 (`PlaybackResult.SkippedByReach`, `AltOutcome.SkippedByReach`, `MacroRunInvoker` reason, log lines), pinned through the registry (`GetPlayback_reports_finished_skipped_when_every_alt_was_skipped_by_reach`) and the pipe (`GetPlayback_over_the_pipe_reports_a_skip_and_a_clean_finish_without_a_reason`), with a normal finish still reasonless.
- Version and changelog: Task 6.
- Not Ur Task, left to the Ur OCR plan: the ride, the layer vote on calm frames, per-account target layer, "top" or "one above", the ride burst, the rock cap, Go to Top past the target, the pause length, and choosing which Clear spot macros to run in which order.

**2. Placeholder scan.** No TBD, TODO or "similar to". Every code step carries its code. The provisional reach numbers (60, 225, 80x80) are data with `measuredOn: null`, flagged by the generator.

**3. Type consistency.** `OutlineCheck(CheckBox Box, int MinCount, int WhiteMin = 225)`, `OutlineCheck.MaxSide = 120`, `OutlineCheck.DefaultWhiteMin = 225` match in Tasks 1-5. `PointStep(..., ColorCheck? Check = null, bool CheckEnabled = false, OutlineCheck? Reach = null)` and `HoldStep(..., HoldCheck? Check = null, int? MaxMs = null, OutlineCheck? Reach = null)` match every construction. `PixelBlock.CountNearWhite(int, int, int, int, int) : int?`, `PointMath.ScaledRect(...)`, `PointMath.ScaledCount(...)`, `StepTiming.ReachGraceMs` are defined in Task 1 and used in Task 2. `ReachPlan`, `ReachFor`, `AwaitOutlineAsync`, `LogOutline`, `CountGuarded` are defined in Task 2 and used in Task 3. Test helpers `Reach80`, `LavaAt`, `Framed`, `ReachHold` are defined in Task 2 and used in Tasks 3 and 4. Task 4 adds a `RunTally tally` parameter before `CancellationToken ct` on `PlayPointAsync`, `PlayReachedPointAsync` and `PlayHoldAsync`, and edits exactly the lines Tasks 2 and 3 wrote (`if (!seen0) return;`, `if (!seen) return;`, the `PlayReachedPointAsync` call). `PlaybackResult.SkippedByReach` / `CompletedSkippedByReach()` and `AltOutcome(..., int? StepIndex = null, bool SkippedByReach = false)` match between code and tests. Log strings: `outline seen (N near-white px, needs M)`, `no outline, skipped (N near-white px, needs M)`, `released: outline gone (N near-white px, needs M)` are identical in code and tests. Generator reach box `0 - floor(w/2)` matches the test's `-(w / 2)` for positive sizes.

**4. Review Focus.** Each of the five lines names its test in the task that owns the code. Checked and left out as lower risk: a reach JSON with no box or no minCount (Task 1 validator), reach on a first-match candidate (refused, Task 1), a reach point with an enabled colour check (refused, Task 1), a skipped step inside a repeating playback (a skip is `Completed`, so repeats carry on as for any clean pass).
