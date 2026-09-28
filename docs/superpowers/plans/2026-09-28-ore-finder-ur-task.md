# Ore Finder, Ur Task ClearAt, Implementation Plan

> **For agentic workers:** REQUIRED SUB-SKILL: Use superpowers:subagent-driven-development (recommended) or superpowers:executing-plans to implement this plan task-by-task. Steps use checkbox (`- [ ]`) syntax for tracking.

**Goal:** Add the `ClearAt` bridge call to Ur Task: an ordered list of points, played on one account as ONE playback of reach holds, so Ur OCR's ore finder can aim the pickaxe anywhere in the frame.

**Architecture:** A pure `ClearAtMacro` (new, `src/Ipc/ClearAtMacro.cs`) validates a `ClearAtRequest` and builds an in-memory v4 `Macro` whose steps are one `HoldStep` per point, each carrying the call's outline as its `Reach` and a synthetic 1x1 colour `Check` on the point itself (the validator requires one; reach holds never read it). `MacroRunInvoker.ClearAtAsync` validates, applies the busy rule, resolves the account, builds the macro under id `clearat-<playbackId>` and starts it through the same register-and-observe path `RunAsync` uses, so `SequencePlayer`'s single-flight claim, `PlaybackRegistry`, `GetPlayback`, `StopMacro` and Esc all apply unchanged. `MacroRunnerServer` dispatches the new method. The macro is never written to `MacroStore`, so it never shows in `ListMacros`.

**Tech Stack:** C# / .NET 10 WPF (`net10.0-windows10.0.19041.0`), System.Text.Json positional records (camelCase), xUnit 2.9, in-process named pipes for the server tests.

**Spec:** `docs/superpowers/specs/2026-09-28-ore-stop-pulse-design.md`, sections "Reach, measured, and the ore finder" and "The ClearAt bridge call (Ur Task bridge 1.x, additive)". The second is the contract; this plan implements it as written, with the two deviations named under Global Constraints.

## Global Constraints

- The request, verbatim from the spec: `{ "contractVersion": "1.0", "method": "ClearAt", "callerPluginId": "...", "target": "<decimal user id>", "client": { "w": 800, "h": 599 }, "points": [ { "x": 412, "y": 288, "label": "ore 1" }, ... ], "outline": { "w": 50, "h": 50, "minCount": 60, "whiteMin": 225 }, "maxMsPerPoint": null }`. camelCase JSON like every other method. `points` in order, **1..64**. `maxMsPerPoint` null = no time limit (spec decision 5). A missing `whiteMin` reads as **225** (`OutlineCheck.DefaultWhiteMin`); a missing `label` reads as null and plays as `point N`.
- "One call carries a whole ordered list of points and plays as ONE playback, so it rides the existing single-flight rule, playback id, GetPlayback, StopMacro and Esc unchanged."
- "Ur Task scales points and the outline box from `client` to the live client size with PointMath, exactly as recorded points are scaled." The macro's `RecordedClientW/H` are `client.w/h`; `StepRunner` scales with `PointMath.Place`, `ScaledRect` and `ScaledCount` as it does for recorded reach holds.
- The outline box is `w` x `h` **centred on the point**: `CheckBox(-(w / 2), -(h / 2), w, h)`.
- "Each point plays as a reach hold: hover, outline check (grace 300 ms), no outline means skip, otherwise hold-release-look beats until the outline is gone (or maxMsPerPoint)." Reuse `StepRunner.PlayHoldAsync` / `PlayBeatsAsync` as they are. No `StepRunner` change.
- Foreground rule as macros: a lost foreground aborts the playback (already `StepRunner`'s behaviour).
- Response is `RunMacroResponse`. Refusal reasons: `version-mismatch` (server, envelope), `refused` + detail sentence for a missing `callerPluginId` (server) and for every shape problem (invoker, `ClearAtMacro.Validate`), `busy` while any playback runs, `no-targets-resolved` when the account is not running. A malformed call is refused as malformed even while busy.
- **Deviation 1, `whiteMin`:** the spec says refuse outside 0..255; `StepValidator` refuses a reach `whiteMin` below 1, so a 0 accepted at the bridge would fail every playback. ClearAt refuses outside **1..255**.
- **Deviation 2, additions the spec is silent on:** refuse `maxMsPerPoint` below 1 (the validator's `maxMs` rule), an outline side over `OutlineCheck.MaxSide` (120), and a `minCount` larger than `w*h`. Each would otherwise be accepted and then refused by `StepValidator` after the ack.
- GetPlayback afterwards: finished (something pressed), finished + reason `skipped` (every point skipped), stopped, failed, exactly as today. Nothing new on the wire there.
- "The synthetic playback is never saved to the macro library and never shows in ListMacros. Its log lines name it "ClearAt (N points)" and each point by its label." One point reads `ClearAt (1 point)`.
- No host change. The contract stays a NuGet `PackageReference`; the bridge stays contract 1.0 (the method is additive).
- Build the project, never the solution: `dotnet build rororo-ur-task.csproj`.
- **Close Ur Task before any build or test step.** It runs from `bin\Debug` and holds its DLLs; the test project builds the app project too.
- Fast tests: `dotnet test tests/rororo-ur-task.Tests/rororo-ur-task.Tests.csproj -p:StandaloneTestsOnly=true`. Baseline: **509 passed**. Expected after this plan: **549 passed** (+28, +8, +4).
- Run every command from the repo root. Check with `git rev-parse --show-toplevel` after any `cd`.
- Commits: conventional commits; every message ends with the line `Co-Authored-By: Claude Opus 5.5 <noreply@anthropic.com>` (name the model that actually implemented the task).
- No absolute user-profile paths (drive letter plus Users folder) in any committed file. The pre-commit local-path guard rejects them.
- Version stays **0.11.0** in `rororo-ur-task.csproj` and `manifest.json`. Nothing is released. The CHANGELOG line goes under `## 0.11.0 — unreleased`.
- Log and sentence numbers are invariant culture.

## Review Focus

1. **A point near the frame edge** (an ore patch centred close to the border): its outline box leaves `client`. Expected: the whole call is refused before any input with a sentence naming the point, never a mid-playback `check-failed`. Pinned in Task 1 by the `box-outside` and `point-outside` cases of `Validate_refuses_with_one_sentence`.
2. **Every point out of reach** (all blocks too far, or the character moved): nothing pressed, GetPlayback `finished` + `skipped`. Pinned in Task 1 by `ClearAt_steps_that_all_miss_the_outline_read_skipped_by_reach` and in Task 2 by `ClearAt_reads_finished_skipped_when_every_point_was_skipped`.
3. **A ClearAt while a RunMacro repeat or an earlier ClearAt is still running:** refused `busy`, nothing plays, the running one is untouched. Pinned in Task 2 by `ClearAt_and_RunMacro_share_the_single_flight_rule`.
4. **Esc or StopMacro in the middle of a ClearAt:** the playback reads `stopped`. Pinned in Task 2 by `ClearAt_is_stopped_by_StopMacro_like_any_playback` (the button release is `StepRunner`'s existing finally, already pinned by `Losing_the_foreground_mid_beat_aborts_and_releases_through_finally`).
5. **The live window is not the size the points were measured in** (125% scale, slack): points and the box scale, the threshold with them. Pinned in Task 1 by `ClearAt_points_and_box_scale_from_the_measured_client_to_the_live_one`.

---

## File map

| File | Change | Responsibility |
| --- | --- | --- |
| `src/Ipc/BridgeContract.cs` | modify | `ClearAtRequest`, `ClearAtClient`, `ClearAtPoint`, `ClearAtOutline`; `BridgeContract.MethodClearAt` |
| `src/Ipc/ClearAtMacro.cs` | create | pure: `Validate`, `Build`, `NameFor`, `StartLine`, the synthetic `PointCheck` |
| `src/Ipc/IMacroRunInvoker.cs` | modify | `ClearAtAsync` |
| `src/Ipc/MacroRunInvoker.cs` | modify | `ClearAtAsync`; `RunAsync`'s accept path extracted to `Start` |
| `src/Ipc/MacroRunnerServer.cs` | modify | dispatch `ClearAt` |
| `tests/rororo-ur-task.Tests/Ipc/ClearAtMacroTests.cs` | create | deserialization, build, validation |
| `tests/rororo-ur-task.Tests/Steps/StepRunnerTests.cs` | modify | built steps played through `StepRunner` |
| `tests/rororo-ur-task.Tests/Ipc/MacroRunInvokerTests.cs` | modify | invoker behaviour |
| `tests/rororo-ur-task.Tests/Ipc/MacroRunnerServerTests.cs` | modify | `FakeInvoker.ClearAtAsync`; dispatch tests |
| `tests/rororo-ur-task.Tests/MacroRunnerServerPipeBusyTests.cs` | modify | `NoopInvoker.ClearAtAsync` |
| `CHANGELOG.md` | modify | one Added line under 0.11.0 |

---

### Task 1: ClearAt request and the synthetic macro

**Files:**
- Modify: `src/Ipc/BridgeContract.cs` (records after `GetPlaybackResponse`; constant in `BridgeContract`)
- Create: `src/Ipc/ClearAtMacro.cs`
- Create: `tests/rororo-ur-task.Tests/Ipc/ClearAtMacroTests.cs`
- Modify: `tests/rororo-ur-task.Tests/Steps/StepRunnerTests.cs` (append inside the class)

**Interfaces:**
- Consumes (existing): `HoldStep(int DelayMs, string Id, string? Label, int X, int Y, int Button = 1, HoldCheck? Check = null, int? MaxMs = null, OutlineCheck? Reach = null)`; `HoldCheck(CheckBox Box, int Tolerance = 15)`; `CheckBox(int OffsetX = -2, int OffsetY = -2, int W = 5, int H = 5)`; `OutlineCheck(CheckBox Box, int MinCount, int WhiteMin = 225)`, `OutlineCheck.MaxSide` (120), `OutlineCheck.DefaultWhiteMin`; `PointMath.BoxRect`, `PointMath.InsideClient`; `Macro` positional record and `Macro.CurrentSchemaVersion`, `Macro.CoordSpaceClient`; `StepValidator.Validate(IReadOnlyList<MacroStep>)`.
- Produces:
  - `public sealed record ClearAtClient(int W, int H);`
  - `public sealed record ClearAtPoint(int X, int Y, string? Label = null);`
  - `public sealed record ClearAtOutline(int W, int H, int MinCount, int WhiteMin = OutlineCheck.DefaultWhiteMin);`
  - `public sealed record ClearAtRequest(string ContractVersion, string Method, string? CallerPluginId, string? Target, ClearAtClient? Client, IReadOnlyList<ClearAtPoint>? Points, ClearAtOutline? Outline, int? MaxMsPerPoint = null);`
  - `BridgeContract.MethodClearAt = "ClearAt"`
  - `internal static class ClearAtMacro` with `const int MaxPoints = 64`, `const string IdPrefix = "clearat-"`, `static readonly HoldCheck PointCheck`, `static string? Validate(ClearAtRequest r)`, `static Macro Build(ClearAtRequest r, string macroId)` (call only after `Validate` returned null), `static string NameFor(int points)`, `static string StartLine(string playbackId, Macro macro, string account)`.

- [ ] **Step 1: Write the failing tests for the request and the builder**

Create `tests/rororo-ur-task.Tests/Ipc/ClearAtMacroTests.cs`:

```csharp
// tests/rororo-ur-task.Tests/Ipc/ClearAtMacroTests.cs
using System.Text.Json;
using Labs626.UrTask.Ipc;
using Labs626.UrTask.Macros;
using Labs626.UrTask.Macros.Steps;

namespace Labs626.UrTask.Tests.Ipc;

public class ClearAtMacroTests
{
    // The spec's example call ("The ClearAt bridge call"), plus a second point with no label.
    private const string SpecJson = """
        { "contractVersion": "1.0", "method": "ClearAt", "callerPluginId": "626labs.ur-ocr",
          "target": "5400534998",
          "client": { "w": 800, "h": 599 },
          "points": [ { "x": 412, "y": 288, "label": "ore 1" }, { "x": 390, "y": 390 } ],
          "outline": { "w": 50, "h": 50, "minCount": 60, "whiteMin": 225 },
          "maxMsPerPoint": null }
        """;

    private static ClearAtRequest Spec() => JsonSerializer.Deserialize<ClearAtRequest>(SpecJson, BridgeContract.Json)!;

    /// <summary>A valid call with <paramref name="points"/> points on a grid, labelled "ore N".</summary>
    private static ClearAtRequest Valid(int points = 2, int? maxMs = null) => new(
        "1.0", "ClearAt", "626labs.ur-ocr", "123", new ClearAtClient(800, 599),
        Enumerable.Range(0, points).Select(i => new ClearAtPoint(100 + i % 8 * 60, 100 + i / 8 * 50, $"ore {i + 1}")).ToList(),
        new ClearAtOutline(50, 50, 60), maxMs);

    [Fact]
    public void The_spec_example_deserializes()
    {
        var r = Spec();
        Assert.Equal(("ClearAt", "5400534998", 800, 599), (r.Method, r.Target, r.Client!.W, r.Client.H));
        Assert.Equal((412, 288, "ore 1"), (r.Points![0].X, r.Points[0].Y, r.Points[0].Label));
        Assert.Null(r.Points[1].Label);
        Assert.Equal((50, 50, 60, 225), (r.Outline!.W, r.Outline.H, r.Outline.MinCount, r.Outline.WhiteMin));
        Assert.Null(r.MaxMsPerPoint);
    }

    [Fact]
    public void A_missing_whiteMin_reads_as_225()
    {
        var json = SpecJson.Replace(", \"whiteMin\": 225", "");
        Assert.Equal(OutlineCheck.DefaultWhiteMin, JsonSerializer.Deserialize<ClearAtRequest>(json, BridgeContract.Json)!.Outline!.WhiteMin);
    }

    [Fact]
    public void Build_makes_one_reach_hold_per_point_in_order()
    {
        var m = ClearAtMacro.Build(Spec(), "clearat-pb1");

        Assert.Equal(("clearat-pb1", Macro.CoordSpaceClient, (int?)800, (int?)599), (m.Id, m.CoordSpace, m.RecordedClientW, m.RecordedClientH));
        Assert.True(m.HasSteps);
        Assert.Empty(m.Events);
        var holds = m.Steps!.Cast<HoldStep>().ToList();
        Assert.Equal(new[] { ("p1", "ore 1", 412, 288), ("p2", "point 2", 390, 390) }, holds.Select(h => (h.Id, h.Label!, h.X, h.Y)));
        Assert.All(holds, h =>
        {
            Assert.Equal((0, 1, (int?)null), (h.DelayMs, h.Button, h.MaxMs));
            Assert.Equal(new OutlineCheck(new CheckBox(-25, -25, 50, 50), 60, 225), h.Reach);
            Assert.Same(ClearAtMacro.PointCheck, h.Check);
        });
    }

    [Theory]
    [InlineData(1, "ClearAt (1 point)")]
    [InlineData(3, "ClearAt (3 points)")]
    public void Build_names_the_macro_by_its_point_count(int points, string name)
        => Assert.Equal(name, ClearAtMacro.Build(Valid(points), "clearat-x").Name);

    [Fact]
    public void Build_carries_maxMsPerPoint_to_every_hold()
        => Assert.All(ClearAtMacro.Build(Valid(3, maxMs: 4000), "clearat-x").Steps!.Cast<HoldStep>(), h => Assert.Equal(4000, h.MaxMs));

    [Fact]
    public void The_built_steps_pass_the_step_validator()
        => Assert.Null(StepValidator.Validate(ClearAtMacro.Build(Valid(64), "clearat-x").Steps!));

    [Fact]
    public void Validate_accepts_the_spec_example_and_64_points()
    {
        Assert.Null(ClearAtMacro.Validate(Spec()));
        Assert.Null(ClearAtMacro.Validate(Valid(64)));
    }

    [Theory]
    [InlineData("no-target", "ClearAt needs a target account.")]
    [InlineData("foreground-target", "ClearAt target 'foreground' is not a decimal user id.")]
    [InlineData("no-client", "ClearAt needs the client size the points were measured in.")]
    [InlineData("zero-client", "ClearAt needs the client size the points were measured in.")]
    [InlineData("no-points", "ClearAt takes 1 to 64 points; got 0.")]
    [InlineData("65-points", "ClearAt takes 1 to 64 points; got 65.")]
    [InlineData("null-point", "ClearAt point 2 is empty.")]
    [InlineData("point-outside", "ClearAt point 2 'ore 2' at 800,300 is outside the 800x599 client.")]
    [InlineData("box-outside", "ClearAt point 2 'ore 2' has an outline box outside the 800x599 client.")]
    [InlineData("no-outline", "ClearAt needs an outline box.")]
    [InlineData("empty-box", "ClearAt has an empty outline box.")]
    [InlineData("big-box", "ClearAt has an outline box larger than 120x120.")]
    [InlineData("minCount-0", "ClearAt has an outline minCount below 1.")]
    [InlineData("minCount-over-box", "ClearAt has an outline minCount larger than its box, so it can never pass.")]
    [InlineData("whiteMin-0", "ClearAt has an outline whiteMin outside 1 to 255.")]
    [InlineData("whiteMin-256", "ClearAt has an outline whiteMin outside 1 to 255.")]
    [InlineData("maxMs-0", "ClearAt has a maxMsPerPoint below 1.")]
    public void Validate_refuses_with_one_sentence(string @case, string sentence)
    {
        var ok = Valid();
        var p = ok.Points!;
        var r = @case switch
        {
            "no-target" => ok with { Target = null },
            "foreground-target" => ok with { Target = "foreground" },
            "no-client" => ok with { Client = null },
            "zero-client" => ok with { Client = new ClearAtClient(0, 599) },
            "no-points" => ok with { Points = Array.Empty<ClearAtPoint>() },
            "65-points" => Valid(65),
            "null-point" => ok with { Points = new[] { p[0], null! } },
            "point-outside" => ok with { Points = new[] { p[0], new ClearAtPoint(800, 300, "ore 2") } },
            "box-outside" => ok with { Points = new[] { p[0], new ClearAtPoint(790, 300, "ore 2") } },
            "no-outline" => ok with { Outline = null },
            "empty-box" => ok with { Outline = new ClearAtOutline(0, 50, 1) },
            "big-box" => ok with { Outline = new ClearAtOutline(121, 50, 60) },
            "minCount-0" => ok with { Outline = new ClearAtOutline(50, 50, 0) },
            "minCount-over-box" => ok with { Outline = new ClearAtOutline(10, 10, 101) },
            "whiteMin-0" => ok with { Outline = new ClearAtOutline(50, 50, 60, 0) },
            "whiteMin-256" => ok with { Outline = new ClearAtOutline(50, 50, 60, 256) },
            "maxMs-0" => ok with { MaxMsPerPoint = 0 },
            _ => throw new ArgumentOutOfRangeException(nameof(@case)),
        };
        Assert.Equal(sentence, ClearAtMacro.Validate(r));
    }
}
```

- [ ] **Step 2: Write the failing tests that play the built steps**

In `tests/rororo-ur-task.Tests/Steps/StepRunnerTests.cs`, add `using Labs626.UrTask.Ipc;` below the two existing `using` lines, then append inside the class, just before its final closing brace:

```csharp
    // ---------- ClearAt (the bridge call's synthetic reach holds) ----------

    private static IReadOnlyList<MacroStep> ClearAtSteps(int? maxMsPerPoint, params (int X, int Y, string Label)[] points)
        => ClearAtMacro.Build(new ClearAtRequest(
            "1.0", "ClearAt", "626labs.ur-ocr", "5400534998", new ClearAtClient(800, 599),
            points.Select(p => new ClearAtPoint(p.X, p.Y, p.Label)).ToList(),
            new ClearAtOutline(80, 80, 60), maxMsPerPoint), "clearat-test").Steps!;

    [Fact]
    public async Task ClearAt_steps_mine_an_outlined_point_and_skip_one_out_of_reach()
    {
        var log = new List<string>();
        var io = LiveBlock(breaksAfter: 2); // an outlined block at 400,244; rock everywhere else
        var r = await StepRunner.RunAsync(ClearAtSteps(null, (400, 244, "ore 1"), (150, 150, "stone 2")), Ctx(log), io, default);

        Assert.Equal(PlaybackOutcome.Completed, r.Outcome);
        Assert.False(r.SkippedByReach); // one point mined: a plain finish
        Assert.Equal(2, io.Downs.Count());
        Assert.All(io.Downs, d => Assert.Equal((400, 244), (d.X, d.Y)));
        Assert.Contains("step 1 'ore 1' held 2.0 s over 2 beat(s), released: outline gone after release", log);
        Assert.Contains("step 2 'stone 2' no outline, skipped (0 near-white px, needs 60)", log);
    }

    [Fact]
    public async Task ClearAt_steps_that_all_miss_the_outline_read_skipped_by_reach()
    {
        var io = new FakeIo { Screen = (_, x, y) => LavaAt(x, y) };
        var r = await StepRunner.RunAsync(ClearAtSteps(null, (400, 244, "ore 1"), (500, 300, "stone 2")), Ctx(), io, default);

        Assert.Equal(PlaybackOutcome.Completed, r.Outcome);
        Assert.True(r.SkippedByReach);
        Assert.Empty(io.Downs);
    }

    [Fact]
    public async Task ClearAt_points_and_box_scale_from_the_measured_client_to_the_live_one()
    {
        // Measured at 800x599, played at 1000x749: 400,244 lands at 500,305, the 80 px box grows
        // to 100 and the threshold to 75. The frame is drawn at the scaled size (35 px out).
        var log = new List<string>();
        var io = new FakeIo { Client = (1000, 749), Screen = Framed(500, 305, half: 35) };
        var r = await StepRunner.RunAsync(ClearAtSteps(500, (400, 244, "ore 1")), Ctx(log, actual: (1000, 749)), io, default);

        Assert.Equal(PlaybackOutcome.Completed, r.Outcome);
        Assert.Contains((450, 255, 100, 100), io.CaptureRects);
        Assert.Equal((500, 305), (io.Downs.Single().X, io.Downs.Single().Y));
        Assert.Contains("step 1 'ore 1' outline seen (280 near-white px, needs 75)", log);
    }
```

- [ ] **Step 3: Run the tests to verify they fail**

Close Ur Task first. Run: `dotnet test tests/rororo-ur-task.Tests/rororo-ur-task.Tests.csproj -p:StandaloneTestsOnly=true --filter "FullyQualifiedName~ClearAt"`
Expected: build FAILS with `CS0246: The type or namespace name 'ClearAtRequest' could not be found` (and the same for `ClearAtMacro`).

- [ ] **Step 4: Add the request records and the method constant**

In `src/Ipc/BridgeContract.cs`, add `using Labs626.UrTask.Macros.Steps;` below `using System.Text.Json.Serialization;`. Insert after the `GetPlaybackResponse` record and before `internal static class BridgeContract`:

```csharp
/// <summary>The client size a ClearAt's points and outline box were measured in.</summary>
public sealed record ClearAtClient(int W, int H);

/// <summary>One ClearAt point in <see cref="ClearAtRequest.Client"/> pixels. A null or blank label
/// plays and logs as "point N".</summary>
public sealed record ClearAtPoint(int X, int Y, string? Label = null);

/// <summary>The outline check every ClearAt point uses: a W x H box centred on the point, passing
/// at MinCount or more pixels whose every channel is at least WhiteMin (see OutlineCheck).</summary>
public sealed record ClearAtOutline(int W, int H, int MinCount, int WhiteMin = OutlineCheck.DefaultWhiteMin);

/// <summary>
/// Press where the caller points (bridge 1.x, additive; ore-stop pulse spec, "The ClearAt bridge
/// call"). Each point, in order, plays as a reach hold on the target account, and the whole list
/// is ONE playback: the answer is a <see cref="RunMacroResponse"/>, and GetPlayback, StopMacro and
/// Esc treat it like any RunMacro playback. MaxMsPerPoint null means no time limit.
/// </summary>
public sealed record ClearAtRequest(
    string ContractVersion,
    string Method,
    string? CallerPluginId,
    string? Target,                    // decimal user id
    ClearAtClient? Client,
    IReadOnlyList<ClearAtPoint>? Points,
    ClearAtOutline? Outline,
    int? MaxMsPerPoint = null);
```

In `internal static class BridgeContract`, add below `MethodGetPlayback`:

```csharp
    public const string MethodClearAt = "ClearAt";
```

- [ ] **Step 5: Create the builder**

Create `src/Ipc/ClearAtMacro.cs`:

```csharp
// src/Ipc/ClearAtMacro.cs
using System.Globalization;
using Labs626.UrTask.Macros;
using Labs626.UrTask.Macros.Steps;

namespace Labs626.UrTask.Ipc;

/// <summary>
/// The ClearAt bridge call as a macro (ore-stop pulse spec, "The ClearAt bridge call"): one reach
/// hold per point, in order, played through the same path as a saved step macro and never saved.
/// Pure, so the refusals and the steps are tested without a pipe or a window.
/// </summary>
internal static class ClearAtMacro
{
    public const int MaxPoints = 64;

    /// <summary>The synthetic macro's id is this plus the playback id, so it can never match a
    /// saved macro or a saved point adjustment.</summary>
    public const string IdPrefix = "clearat-";

    /// <summary>
    /// StepValidator refuses a hold without a colour check, and StepRunner places that box inside
    /// the window before any input. A reach hold never reads it (beats ignore colour drift), so the
    /// synthetic one is the smallest box there is: 1x1 on the point itself, inside the window
    /// exactly when the point is.
    /// </summary>
    public static readonly HoldCheck PointCheck = new(new CheckBox(0, 0, 1, 1));

    /// <summary>Null when the call can play; otherwise one sentence naming the first problem. Every
    /// rule StepValidator would apply to the built steps is checked here first, so a bad call is
    /// refused before the ack instead of failing after it.</summary>
    public static string? Validate(ClearAtRequest r)
    {
        if (string.IsNullOrWhiteSpace(r.Target)) return "ClearAt needs a target account.";
        if (!long.TryParse(r.Target, NumberStyles.None, CultureInfo.InvariantCulture, out var userId) || userId <= 0)
            return $"ClearAt target '{r.Target}' is not a decimal user id.";
        if (r.Client is not { W: >= 1, H: >= 1 } client) return "ClearAt needs the client size the points were measured in.";
        var count = r.Points?.Count ?? 0;
        if (count is < 1 or > MaxPoints) return Inv($"ClearAt takes 1 to {MaxPoints} points; got {count}.");
        if (r.Outline is not { } o) return "ClearAt needs an outline box.";
        if (o.W < 1 || o.H < 1) return "ClearAt has an empty outline box.";
        if (o.W > OutlineCheck.MaxSide || o.H > OutlineCheck.MaxSide)
            return Inv($"ClearAt has an outline box larger than {OutlineCheck.MaxSide}x{OutlineCheck.MaxSide}.");
        if (o.MinCount < 1) return "ClearAt has an outline minCount below 1.";
        if (o.MinCount > o.W * o.H) return "ClearAt has an outline minCount larger than its box, so it can never pass.";
        if (o.WhiteMin is < 1 or > 255) return "ClearAt has an outline whiteMin outside 1 to 255.";
        if (r.MaxMsPerPoint is < 1) return "ClearAt has a maxMsPerPoint below 1.";

        var box = BoxFor(o);
        for (int i = 0; i < count; i++)
        {
            var p = r.Points![i];
            if (p is null) return Inv($"ClearAt point {i + 1} is empty.");
            var name = Inv($"ClearAt point {i + 1} '{LabelFor(p, i)}'");
            if (p.X < 0 || p.Y < 0 || p.X >= client.W || p.Y >= client.H)
                return Inv($"{name} at {p.X},{p.Y} is outside the {client.W}x{client.H} client.");
            if (!PointMath.InsideClient(PointMath.BoxRect((p.X, p.Y), box), (client.W, client.H)))
                return Inv($"{name} has an outline box outside the {client.W}x{client.H} client.");
        }
        return null;
    }

    /// <summary>The in-memory macro. Call only after <see cref="Validate"/> returned null.</summary>
    public static Macro Build(ClearAtRequest r, string macroId)
    {
        var o = r.Outline!;
        var reach = new OutlineCheck(BoxFor(o), o.MinCount, o.WhiteMin);
        var steps = r.Points!
            .Select((p, i) => (MacroStep)new HoldStep(
                DelayMs: 0, Id: Inv($"p{i + 1}"), Label: LabelFor(p, i), X: p.X, Y: p.Y, Button: 1,
                Check: PointCheck, MaxMs: r.MaxMsPerPoint, Reach: reach))
            .ToList();
        return new Macro(
            SchemaVersion: Macro.CurrentSchemaVersion, Id: macroId, Name: NameFor(steps.Count),
            RecordMode: "PerWindow", RecordedAgainstUserId: null, RecordedAgainstDisplayName: null,
            InterAltDelayMs: null, RecordedAtUnixMs: 0, Events: Array.Empty<MacroEvent>(),
            CoordSpace: Macro.CoordSpaceClient, RecordedClientW: r.Client!.W, RecordedClientH: r.Client.H,
            AllGames: true, Steps: steps);
    }

    public static string NameFor(int points) => points == 1 ? "ClearAt (1 point)" : Inv($"ClearAt ({points} points)");

    /// <summary>The ur-task.log line when a ClearAt is accepted: every point by its label and where
    /// it was aimed, in the measured client space. The end line is the usual bridge line.</summary>
    public static string StartLine(string playbackId, Macro macro, string account)
        => $"bridge playback {playbackId} '{macro.Name}' on {account}: "
           + string.Join("; ", macro.Steps!.OfType<HoldStep>().Select(h => Inv($"'{h.Label}' at {h.X},{h.Y}")));

    private static CheckBox BoxFor(ClearAtOutline o) => new(-(o.W / 2), -(o.H / 2), o.W, o.H);

    private static string LabelFor(ClearAtPoint p, int index)
        => string.IsNullOrWhiteSpace(p.Label) ? Inv($"point {index + 1}") : p.Label;

    private static string Inv(FormattableString s) => FormattableString.Invariant(s);
}
```

- [ ] **Step 6: Run the tests to verify they pass**

Run: `dotnet test tests/rororo-ur-task.Tests/rororo-ur-task.Tests.csproj -p:StandaloneTestsOnly=true --filter "FullyQualifiedName~ClearAt"`
Expected: PASS, 28 tests (25 in `ClearAtMacroTests`, 3 `StepRunnerTests.ClearAt_*`).

- [ ] **Step 7: Run the full fast suite**

Run: `dotnet test tests/rororo-ur-task.Tests/rororo-ur-task.Tests.csproj -p:StandaloneTestsOnly=true`
Expected: **537 passed**, 0 failed.

- [ ] **Step 8: Commit**

```bash
git add src/Ipc/BridgeContract.cs src/Ipc/ClearAtMacro.cs tests/rororo-ur-task.Tests/Ipc/ClearAtMacroTests.cs tests/rororo-ur-task.Tests/Steps/StepRunnerTests.cs
git commit -m "feat(ipc): ClearAt request and its synthetic reach-hold macro

Co-Authored-By: Claude Opus 5.5 <noreply@anthropic.com>"
```

---

### Task 2: MacroRunInvoker plays a ClearAt as one playback

**Files:**
- Modify: `src/Ipc/IMacroRunInvoker.cs`
- Modify: `src/Ipc/MacroRunInvoker.cs:86-110` (`RunAsync`) plus a new `ClearAtAsync` and `Start`
- Modify: `tests/rororo-ur-task.Tests/Ipc/MacroRunInvokerTests.cs` (append)
- Modify: `tests/rororo-ur-task.Tests/Ipc/MacroRunnerServerTests.cs:12-36` (`FakeInvoker`)
- Modify: `tests/rororo-ur-task.Tests/MacroRunnerServerPipeBusyTests.cs:19-29` (`NoopInvoker`)

**Interfaces:**
- Consumes (Task 1): `ClearAtRequest`, `ClearAtClient`, `ClearAtPoint`, `ClearAtOutline`; `ClearAtMacro.Validate`, `ClearAtMacro.Build`, `ClearAtMacro.IdPrefix`, `ClearAtMacro.StartLine`.
- Produces: `Task<RunMacroResponse> IMacroRunInvoker.ClearAtAsync(ClearAtRequest request, CancellationToken ct)`, implemented by `MacroRunInvoker`; `FakeInvoker.SeenClearAt` (`ClearAtRequest?`) in the server tests, used by Task 3.

- [ ] **Step 1: Write the failing invoker tests**

In `tests/rororo-ur-task.Tests/Ipc/MacroRunInvokerTests.cs`, add `using Labs626.UrTask.Macros.Steps;` below the existing `using` lines, then append inside the class, before its final closing brace:

```csharp
    // ---------- ClearAt ----------

    private static ClearAtRequest ClearAt(string target = "123", int points = 2) => new(
        "1.0", "ClearAt", "626labs.ur-ocr", target, new ClearAtClient(800, 599),
        Enumerable.Range(0, points).Select(i => new ClearAtPoint(400 + i * 50, 300, $"ore {i + 1}")).ToList(),
        new ClearAtOutline(50, 50, 60));

    private static MacroRunInvoker ClearAtInvoker(
        Func<Macro, IReadOnlyList<AccountRegistry.AccountInfo>, int?, CancellationToken, Task<SequenceResult?>> play,
        bool busy = false, IReadOnlyList<Macro>? saved = null, List<string>? log = null)
        => new MacroRunInvoker(
            loadMacros: () => saved ?? Array.Empty<Macro>(),
            snapshot: () => new[] { Alt(123), Alt(456) },
            resolveForegroundUserId: () => 123L,
            isBusy: () => busy,
            playWithResult: play,
            log: log is null ? null : new Action<string>(log.Add));

    private static GetPlaybackResponse Status(MacroRunInvoker inv, string? id)
        => inv.GetPlayback(new GetPlaybackRequest("1.0", "GetPlayback", id, "626labs.ur-ocr"));

    [Fact]
    public async Task ClearAt_refuses_a_malformed_call_before_the_busy_check()
    {
        var inv = ClearAtInvoker((_, _, _, _) => Task.FromResult<SequenceResult?>(null), busy: true);
        var r = await inv.ClearAtAsync(ClearAt(points: 0), default);
        Assert.Equal((false, "refused", "ClearAt takes 1 to 64 points; got 0."), (r.Ok, r.Reason, r.Detail));
    }

    [Fact]
    public async Task ClearAt_refuses_while_busy()
    {
        var inv = ClearAtInvoker((_, _, _, _) => Task.FromResult<SequenceResult?>(null), busy: true);
        var r = await inv.ClearAtAsync(ClearAt(), default);
        Assert.Equal((false, "busy"), (r.Ok, r.Reason));
        Assert.Equal(0, inv.ActivePlaybackCount);
    }

    [Fact]
    public async Task ClearAt_refuses_an_account_that_is_not_running()
    {
        var inv = ClearAtInvoker((_, _, _, _) => Task.FromResult<SequenceResult?>(null));
        var r = await inv.ClearAtAsync(ClearAt(target: "999"), default);
        Assert.Equal((false, "no-targets-resolved", "Account 999 is not running."), (r.Ok, r.Reason, r.Detail));
    }

    [Fact]
    public async Task ClearAt_plays_one_unsaved_macro_of_reach_holds_on_the_target()
    {
        Macro? played = null;
        IReadOnlyList<AccountRegistry.AccountInfo>? on = null;
        var inv = ClearAtInvoker(
            (m, t, _, _) => { played = m; on = t; return Task.FromResult<SequenceResult?>(null); },
            saved: new[] { NewMacro("saved", "Farm") });

        var r = await inv.ClearAtAsync(ClearAt(target: "456"), default);
        await WaitUntilAsync(() => inv.ActivePlaybackCount == 0);

        Assert.True(r.Ok);
        Assert.Equal(new long[] { 456 }, on!.Select(a => a.RobloxUserId));
        Assert.Equal(("ClearAt (2 points)", ClearAtMacro.IdPrefix + r.PlaybackId), (played!.Name, played.Id));
        Assert.Equal(new[] { "ore 1", "ore 2" }, played.Steps!.Cast<HoldStep>().Select(h => h.Label));
        Assert.Equal(new[] { "saved" }, inv.ListMacros().Select(m => m.Id)); // never saved, never listed
        Assert.Equal("finished", Status(inv, r.PlaybackId).State);
    }

    [Fact]
    public async Task ClearAt_reads_finished_skipped_when_every_point_was_skipped()
    {
        var alt = Alt(123);
        var skipped = new SequenceResult(new[] { new AltOutcome(alt, PlaybackOutcome.Completed, null, SkippedByReach: true) }, 1, 0, 0, TimeSpan.Zero);
        var inv = ClearAtInvoker((_, _, _, _) => Task.FromResult<SequenceResult?>(skipped));

        var r = await inv.ClearAtAsync(ClearAt(), default);
        await WaitUntilAsync(() => inv.ActivePlaybackCount == 0);

        var status = Status(inv, r.PlaybackId);
        Assert.Equal(("finished", "skipped"), (status.State, status.Reason));
    }

    [Fact]
    public async Task ClearAt_is_stopped_by_StopMacro_like_any_playback()
    {
        var inv = ClearAtInvoker(async (_, _, _, ct) => { await Task.Delay(Timeout.Infinite, ct); return null; });

        var r = await inv.ClearAtAsync(ClearAt(), default);
        Assert.Equal("running", Status(inv, r.PlaybackId).State);
        inv.StopMacro(new StopMacroRequest("1.0", "StopMacro", r.PlaybackId, null, "626labs.ur-ocr"));
        await WaitUntilAsync(() => inv.ActivePlaybackCount == 0);

        Assert.Equal("stopped", Status(inv, r.PlaybackId).State);
    }

    [Fact]
    public async Task ClearAt_and_RunMacro_share_the_single_flight_rule()
    {
        var gate = new TaskCompletionSource();
        var inv = ClearAtInvoker(async (_, _, _, _) => { await gate.Task; return null; }, saved: new[] { NewMacro("m1", "Farm") });

        var clear = await inv.ClearAtAsync(ClearAt(), default);
        var run = await inv.RunAsync(new RunMacroRequest("1.0", "RunMacro", "m1", new[] { "123" }, null, "626labs.ur-mcp"), default);
        var again = await inv.ClearAtAsync(ClearAt(), default);

        Assert.True(clear.Ok);
        Assert.Equal(("busy", "busy"), (run.Reason, again.Reason));
        Assert.Equal(1, inv.ActivePlaybackCount);
        gate.TrySetResult();
        await WaitUntilAsync(() => inv.ActivePlaybackCount == 0);
    }

    [Fact]
    public async Task ClearAt_logs_its_points_by_label_and_how_it_ended()
    {
        var lines = new List<string>();
        var inv = ClearAtInvoker((_, _, _, _) => Task.FromResult<SequenceResult?>(null), log: lines);

        var r = await inv.ClearAtAsync(ClearAt(), default);
        await WaitUntilAsync(() => inv.ActivePlaybackCount == 0);

        Assert.Equal(new[]
        {
            $"bridge playback {r.PlaybackId} 'ClearAt (2 points)' on alt-123: 'ore 1' at 400,300; 'ore 2' at 450,300",
            $"bridge playback {r.PlaybackId} 'ClearAt (2 points)': finished",
        }, lines);
    }
```

- [ ] **Step 2: Run the tests to verify they fail**

Run: `dotnet test tests/rororo-ur-task.Tests/rororo-ur-task.Tests.csproj -p:StandaloneTestsOnly=true --filter "FullyQualifiedName~MacroRunInvokerTests"`
Expected: build FAILS with `CS1061: 'MacroRunInvoker' does not contain a definition for 'ClearAtAsync'`.

- [ ] **Step 3: Add the interface method**

In `src/Ipc/IMacroRunInvoker.cs`, add below `RunAsync`:

```csharp
    /// <summary>Play an ordered list of points on one account as reach holds, as ONE playback
    /// (bridge 1.x, additive). Same single-flight rule, playback id, StopMacro and GetPlayback as
    /// <see cref="RunAsync"/>; the macro is built in memory and never saved.</summary>
    Task<RunMacroResponse> ClearAtAsync(ClearAtRequest request, CancellationToken ct);
```

- [ ] **Step 4: Implement it in MacroRunInvoker, sharing RunAsync's accept path**

In `src/Ipc/MacroRunInvoker.cs`, replace the whole `RunAsync` method (lines 86-110) with:

```csharp
    public Task<RunMacroResponse> RunAsync(RunMacroRequest request, CancellationToken ct)
    {
        if (_isBusy() || !_playbacks.IsEmpty)
            return Task.FromResult(RunMacroResponse.Refused("busy", "A sequence is already running."));

        var macro = _loadMacros().FirstOrDefault(m => string.Equals(m.Id, request.MacroId, StringComparison.OrdinalIgnoreCase));
        if (macro is null)
            return Task.FromResult(RunMacroResponse.Refused("unknown-macro", $"No macro with id '{request.MacroId}'."));

        var targets = ResolveTargets(request.Targets);
        if (targets.Count == 0)
            return Task.FromResult(RunMacroResponse.Refused("no-targets-resolved", "None of the requested targets are running."));

        return Task.FromResult(Start(Guid.NewGuid().ToString("N"), macro, targets, request.InterAltDelayMs, request.Repeat, ct));
    }

    /// <summary>
    /// The ClearAt bridge call (ore-stop pulse spec): the points become an in-memory macro of reach
    /// holds, played through <see cref="Start"/> exactly like a saved macro, so the single-flight
    /// rule, GetPlayback, StopMacro and Esc need nothing new. It is never written to the store, so
    /// ListMacros never shows it. Order: malformed → busy → account not running → play.
    /// </summary>
    public Task<RunMacroResponse> ClearAtAsync(ClearAtRequest request, CancellationToken ct)
    {
        // Shape first: a malformed call is refused as malformed even while something is playing.
        if (ClearAtMacro.Validate(request) is { } problem)
            return Task.FromResult(RunMacroResponse.Refused("refused", problem));
        if (_isBusy() || !_playbacks.IsEmpty)
            return Task.FromResult(RunMacroResponse.Refused("busy", "A sequence is already running."));

        var targets = ResolveTargets(new[] { request.Target! });
        if (targets.Count == 0)
            return Task.FromResult(RunMacroResponse.Refused("no-targets-resolved", $"Account {request.Target} is not running."));

        var playbackId = Guid.NewGuid().ToString("N");
        var macro = ClearAtMacro.Build(request, ClearAtMacro.IdPrefix + playbackId);
        _log(ClearAtMacro.StartLine(playbackId, macro, targets[0].DisplayName));
        return Task.FromResult(Start(playbackId, macro, targets, interAltDelayMs: null, repeat: false, ct));
    }

    /// <summary>
    /// Ack-on-accept: start playback fire-and-forget and ack now. The bridge must not block the
    /// caller (Ur-OCR's 5Hz tick) for the macro's full runtime. Exceptions in the detached playback
    /// are swallowed here — they surface on the Ur Task playback side. The playback registers under
    /// its id with a CTS linked to the bridge token, so StopMacro can end it and bridge shutdown
    /// still tears it down.
    /// </summary>
    private RunMacroResponse Start(
        string playbackId, Macro macro, IReadOnlyList<AccountRegistry.AccountInfo> targets,
        int? interAltDelayMs, bool repeat, CancellationToken ct)
    {
        var playbackCts = CancellationTokenSource.CreateLinkedTokenSource(ct);
        _playbacks[playbackId] = playbackCts;
        _registry.Started(playbackId);
        _ = ObservePlaybackAsync(playbackId, macro, targets, interAltDelayMs, repeat, playbackCts);
        return RunMacroResponse.Accepted(playbackId);
    }
```

- [ ] **Step 5: Give the two test fakes the new method**

In `tests/rororo-ur-task.Tests/Ipc/MacroRunnerServerTests.cs`, inside `FakeInvoker`, add below the `SeenStop` property:

```csharp
        public ClearAtRequest? SeenClearAt { get; private set; }

        public Task<RunMacroResponse> ClearAtAsync(ClearAtRequest request, CancellationToken ct)
        {
            SeenClearAt = request;
            return Task.FromResult(Next);
        }
```

In `tests/rororo-ur-task.Tests/MacroRunnerServerPipeBusyTests.cs`, inside `NoopInvoker`, add below `GetPlayback`:

```csharp
        public Task<RunMacroResponse> ClearAtAsync(ClearAtRequest request, CancellationToken ct)
            => throw new InvalidOperationException("no connections expected in this test");
```

- [ ] **Step 6: Run the tests to verify they pass**

Run: `dotnet test tests/rororo-ur-task.Tests/rororo-ur-task.Tests.csproj -p:StandaloneTestsOnly=true --filter "FullyQualifiedName~MacroRunInvokerTests"`
Expected: PASS, including the 8 new `ClearAt_*` tests and every existing `RunAsync` test (the extracted `Start` must not change them).

- [ ] **Step 7: Run the full fast suite**

Run: `dotnet test tests/rororo-ur-task.Tests/rororo-ur-task.Tests.csproj -p:StandaloneTestsOnly=true`
Expected: **545 passed**, 0 failed.

- [ ] **Step 8: Commit**

```bash
git add src/Ipc/IMacroRunInvoker.cs src/Ipc/MacroRunInvoker.cs tests/rororo-ur-task.Tests/Ipc/MacroRunInvokerTests.cs tests/rororo-ur-task.Tests/Ipc/MacroRunnerServerTests.cs tests/rororo-ur-task.Tests/MacroRunnerServerPipeBusyTests.cs
git commit -m "feat(ipc): play a ClearAt as one bridge playback of reach holds

Co-Authored-By: Claude Opus 5.5 <noreply@anthropic.com>"
```

---

### Task 3: The server dispatches ClearAt, and the CHANGELOG says so

**Files:**
- Modify: `src/Ipc/MacroRunnerServer.cs:72-139` (doc comment and `DispatchAsync`)
- Modify: `tests/rororo-ur-task.Tests/Ipc/MacroRunnerServerTests.cs` (append)
- Modify: `CHANGELOG.md` (`## 0.11.0 — unreleased` → `### Added`)

**Interfaces:**
- Consumes: `BridgeContract.MethodClearAt`, `ClearAtRequest` (Task 1); `IMacroRunInvoker.ClearAtAsync`, `FakeInvoker.SeenClearAt` (Task 2); `MacroRunInvoker`'s main (`playWithResult`) constructor.
- Produces: `ClearAt` on the wire at `\\.\pipe\626labs-ur-task`.

- [ ] **Step 1: Write the failing server tests**

In `tests/rororo-ur-task.Tests/Ipc/MacroRunnerServerTests.cs`, append inside the class, before its final closing brace:

```csharp
    // ---------- ClearAt ----------

    private const string ClearAtJson =
        "{\"contractVersion\":\"1.0\",\"method\":\"ClearAt\",\"callerPluginId\":\"626labs.ur-ocr\",\"target\":\"123\"," +
        "\"client\":{\"w\":800,\"h\":599},\"points\":[{\"x\":412,\"y\":288,\"label\":\"ore 1\"}]," +
        "\"outline\":{\"w\":50,\"h\":50,\"minCount\":60,\"whiteMin\":225},\"maxMsPerPoint\":null}";

    [Fact]
    public async Task ClearAt_Dispatches_AndReturnsAck()
    {
        var invoker = new FakeInvoker { Next = RunMacroResponse.Accepted("01CLR") };

        var respJson = await RoundTripJsonAsync(new MacroRunnerServer(invoker), ClearAtJson);

        Assert.Equal("{\"ok\":true,\"playbackId\":\"01CLR\",\"queued\":false}", respJson);
        Assert.Equal("123", invoker.SeenClearAt!.Target);
        Assert.Equal("ore 1", Assert.Single(invoker.SeenClearAt.Points!).Label);
        Assert.Null(invoker.Seen); // not routed as a RunMacro
    }

    [Fact]
    public async Task ClearAt_MissingCallerPluginId_RefusedWithoutDispatch()
    {
        var invoker = new FakeInvoker();

        var respJson = await RoundTripJsonAsync(new MacroRunnerServer(invoker),
            ClearAtJson.Replace("\"callerPluginId\":\"626labs.ur-ocr\",", ""));
        var resp = JsonSerializer.Deserialize<RunMacroResponse>(respJson, BridgeContract.Json)!;

        Assert.Equal((false, "refused", "Missing callerPluginId."), (resp.Ok, resp.Reason, resp.Detail));
        Assert.Null(invoker.SeenClearAt);
    }

    [Fact]
    public async Task ClearAt_UnsupportedVersion_RefusedVersionMismatch()
    {
        var invoker = new FakeInvoker();

        var respJson = await RoundTripJsonAsync(new MacroRunnerServer(invoker), ClearAtJson.Replace("\"1.0\"", "\"2.0\""));
        var resp = JsonSerializer.Deserialize<RunMacroResponse>(respJson, BridgeContract.Json)!;

        Assert.Equal((false, "version-mismatch"), (resp.Ok, resp.Reason));
        Assert.Null(invoker.SeenClearAt);
    }

    // End to end with the real invoker: a bad call is refused on the wire with the sentence Ur OCR
    // shows, and nothing starts.
    [Fact]
    public async Task ClearAt_over_the_pipe_refuses_a_point_outside_the_client()
    {
        var alt = new AccountRegistry.AccountInfo(1123, 123, "alt-123", "acct-123");
        var invoker = new MacroRunInvoker(
            loadMacros: Array.Empty<Macro>,
            snapshot: () => new[] { alt },
            resolveForegroundUserId: () => alt.RobloxUserId,
            isBusy: () => false,
            playWithResult: (_, _, _, _) => Task.FromResult<SequenceResult?>(null));

        var respJson = await RoundTripJsonAsync(new MacroRunnerServer(invoker), ClearAtJson.Replace("\"x\":412", "\"x\":900"));
        var resp = JsonSerializer.Deserialize<RunMacroResponse>(respJson, BridgeContract.Json)!;

        Assert.Equal((false, "refused", "ClearAt point 1 'ore 1' at 900,288 is outside the 800x599 client."),
            (resp.Ok, resp.Reason, resp.Detail));
        Assert.Equal(0, invoker.ActivePlaybackCount);
    }
```

- [ ] **Step 2: Run the tests to verify they fail**

Run: `dotnet test tests/rororo-ur-task.Tests/rororo-ur-task.Tests.csproj -p:StandaloneTestsOnly=true --filter "FullyQualifiedName~MacroRunnerServerTests.ClearAt"`
Expected: FAIL. `ClearAt_Dispatches_AndReturnsAck` and `ClearAt_over_the_pipe_refuses_a_point_outside_the_client` get `Unknown method 'ClearAt'.`; `ClearAt_MissingCallerPluginId_RefusedWithoutDispatch` fails on the detail (`Unknown method 'ClearAt'.` instead of `Missing callerPluginId.`). `ClearAt_UnsupportedVersion_RefusedVersionMismatch` already passes (the version check runs before dispatch), which is correct.

- [ ] **Step 3: Dispatch the method**

In `src/Ipc/MacroRunnerServer.cs`, in `DispatchAsync`, add this case after the `MethodGetPlayback` case and before `default:`:

```csharp
            case BridgeContract.MethodClearAt:
            {
                var req = JsonSerializer.Deserialize<ClearAtRequest>(frame, BridgeContract.Json);
                if (req is null)
                    return Bytes(RunMacroResponse.Refused("refused", "Empty request."));
                if (string.IsNullOrWhiteSpace(req.CallerPluginId))
                    return Bytes(RunMacroResponse.Refused("refused", "Missing callerPluginId."));
                return Bytes(await _invoker.ClearAtAsync(req, ct).ConfigureAwait(false));
            }
```

In the `HandleConnectionAsync` doc comment, replace `heterogeneous methods (ListMacros/StopMacro beside RunMacro)` with `heterogeneous methods (ListMacros/StopMacro/GetPlayback/ClearAt beside RunMacro)`.

- [ ] **Step 4: Run the tests to verify they pass**

Run: `dotnet test tests/rororo-ur-task.Tests/rororo-ur-task.Tests.csproj -p:StandaloneTestsOnly=true --filter "FullyQualifiedName~MacroRunnerServerTests"`
Expected: PASS, including the 4 new `ClearAt_*` tests and `UnknownMethod_RefusedWithThePinnedDetail`.

- [ ] **Step 5: Add the CHANGELOG line**

In `CHANGELOG.md`, under `## 0.11.0 — unreleased` → `### Added`, append after the last bullet of that list (the "A playback that pressed nothing says so." bullet):

```markdown
- **ClearAt, a bridge call that presses where the caller points.** Ur OCR sends one account, the
  client size it measured in, 1 to 64 points in order and one outline box, and Ur Task plays them
  as ONE playback: each point is a reach hold (hover, outline check, hold-release-look beats until
  the outline is gone), skipped when no outline shows. Points and box scale to the live window like
  recorded points. It rides the same single-flight rule, playback id, `GetPlayback`, `StopMacro` and
  Esc as `RunMacro`, is never saved or listed, and logs as "ClearAt (N points)" with each point by
  its label. Additive on bridge contract 1.0.
```

- [ ] **Step 6: Build and run the full fast suite**

Run: `dotnet build rororo-ur-task.csproj`
Expected: Build succeeded, 0 errors.

Run: `dotnet test tests/rororo-ur-task.Tests/rororo-ur-task.Tests.csproj -p:StandaloneTestsOnly=true`
Expected: **549 passed**, 0 failed.

Check the version did not move: `git diff --stat HEAD -- rororo-ur-task.csproj manifest.json` prints nothing.

- [ ] **Step 7: Commit**

```bash
git add src/Ipc/MacroRunnerServer.cs tests/rororo-ur-task.Tests/Ipc/MacroRunnerServerTests.cs CHANGELOG.md
git commit -m "feat(ipc): dispatch ClearAt on the bridge; changelog for 0.11.0

Co-Authored-By: Claude Opus 5.5 <noreply@anthropic.com>"
```

---

## Spec notes for the reviewer

- **Window sizing.** A ClearAt plays through `MacroPlayer.PlayStepsAsync`, which first calls `EnsureClientSize` with the macro's recorded size, here `client`. When the live client already matches (Ur OCR measures in the live frame, so it normally does), that is a no-op and the points map one to one. When it differs by more than 2 px, the window is **resized to `client`** before the points are scaled, rather than the points being scaled to the window as the spec's wording implies. Not changed here: the scale path still covers display-scale slack, and a caller that measures the live frame never meets it. Ur OCR should always send the size it measured in.
- **Focus delay.** `SequencePlayer` focuses the account and waits its default 500 ms before playing, as for any `RunMacro` with no `interAltDelayMs`. The spec does not name a value; ClearAt keeps the default.
- **Rounding at the edge.** A box flush with the client edge at the measured size can round 1 px outside after scaling to a different live size; `StepRunner` then stops that point as `check-failed`. Only possible when the live size differs from `client` (see window sizing).
