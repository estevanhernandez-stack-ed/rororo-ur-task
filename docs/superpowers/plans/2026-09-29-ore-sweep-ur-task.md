# Ore Sweep, Ur Task SweepPath, Implementation Plan

> **For agentic workers:** REQUIRED SUB-SKILL: Use superpowers:subagent-driven-development (recommended) or superpowers:executing-plans to implement this plan task-by-task. Steps use checkbox (`- [ ]`) syntax for tracking.

**Goal:** Add `SweepPath` to Ur Task's action bridge: one continuous left-button hold that starts on a block beside the character, walks a path Ur OCR sends one real mouse move at a time with a per-point dwell, watches the Auto Mine dot while held, and lets go only back on the block it started on. Merge `main` (0.9.1 and the host #223 test fix) first, and ship the feature as 0.12.0.

**Architecture:** A new in-memory step kind, `SweepStep` (never saved, so it has no JSON kind), played by `StepRunner.PlaySweepAsync`: jump, press on `Path[0]`, dwell, one `MouseMove` per later point, dwell, and release on the last point, which the bridge guarantees is `Path[0]` again. The guard (`StepContext.Guard`, the existing ClearAt guard plumbing) is sampled before the press and every `StepTiming.SweepGuardEveryPoints` points while held; a change, Esc, `StopMacro` or a capture that fails moves the pointer back to the start block and releases there, while a lost foreground releases in place through `RunAsync`'s finally, as every hold does. A pure `SweepPathMacro` (like `ClearAtMacro`) validates a `SweepPathRequest` and builds the one-step macro; `MacroRunInvoker.SweepPathAsync` plays it through the same `Start` path as `ClearAtAsync`, so single-flight, `GetPlayback`, `StopMacro` and Esc need nothing new; `MacroRunnerServer` dispatches the method.

**Tech Stack:** C# / .NET 10 WPF (`net10.0-windows10.0.19041.0`), System.Text.Json positional records (camelCase), xUnit 2.9, in-process named pipes for the server tests.

**Spec:** `docs/superpowers/specs/2026-09-29-ore-stop-sweep-design.md` (the sweep, "The Ur Task side" and "Safety", plus "Later inputs" 5 and 6), building on `docs/superpowers/specs/2026-09-28-ore-stop-pulse-design.md` (ClearAt, the guard). The Ur OCR half is `..\Ur-OCR\docs\superpowers\plans\2026-09-29-ore-sweep-ur-ocr.md`; the wire shape below is pinned identically in both.

## Global Constraints

- **The request** (spec: `{ target, client, path: [{x,y}], dwellMs, guard }`, plus `step`, see Deviation 1), camelCase like every other method, exactly as Ur OCR sends it:
  `{"contractVersion":"1.0","method":"SweepPath","callerPluginId":"626labs.ur-ocr","target":"123456789","client":{"w":800,"h":599},"path":[{"x":450,"y":300},{"x":450,"y":250},{"x":400,"y":250},{"x":450,"y":300}],"step":50,"dwellMs":400,"guard":{"x":55,"y":289,"w":3,"h":3,"expect":{"r":255,"g":19,"b":90},"tolerance":30}}`
- "It plays as one playback (single-flight, GetPlayback, StopMacro, Esc) and uses real mouse-move input for every step (Roblox ignores a bare cursor jump)." Every point after the first is one `MouseMove` event through `IStepIo.Send` (SendInput, absolute), including a hop over HUD points: "the move is a single straight input".
- "**The button comes up only on the start block** (the first-ring block the sweep began on). A release is a click, and a click on a player or a chest opens a popup."
- Guard: "With the button held, the guard pixel (the Auto Mine dot) is sampled every few points. If it changes, move back to the start block (the first-ring block) and release there, then stop the playback." Every few = `StepTiming.SweepGuardEveryPoints` = **3**. Also sampled twice before the press (before the jump and after it), like a ClearAt point. Stop sentence: `SweepPath stopped: the guard at (X,Y) isn't the expected colour (saw #RRGGBB); something may be over the game (a menu or a player's profile).` A guard stop is a failed check (`StepIndex` 0, `check-failed` on the wire).
- "The foreground check happens before the press and at every point. On focus loss, release, as every hold does today." Every input goes through `SendGuarded`; a dwell polls the foreground every `StepTiming.PollMs` (100). Focus loss releases in place (`RunAsync`'s finally, `IStepIo.ReleaseButton`).
- Esc, `StopMacro`, and a capture that fails mid-hold go back to the start block and release there when the target is still in front (a best-effort move and up, no waits); otherwise the finally releases in place.
- "The window is sized to the measured client first, as for ClearAt." The macro's `RecordedClientW/H` are `client.w/h`; `MacroPlayer.PlayStepsAsync` does the rest. Points are placed with `PointMath.Place` (display-scale slack), and a placed point outside the live window stops the step before any input.
- The dwell is waited at `Path[0]` and at every later point except the last: the last point is where it lets go.
- Logs: the start line `bridge playback <id> 'SweepPath (N points)' on <account>: from X,Y, S px steps, D ms a point`, the guard line `SweepPath guard at (X,Y), expecting #RRGGBB ±T`, on completion `<account>: swept N points in S.S s`, on an early end `<account>: swept K of N points in S.S s, then the playback ended` (plus `; released on the start block` when it went back), then the usual bridge end line. N counts every point of the path, the closing one included.
- Refusals, each one sentence, before the busy check, in this order: target (the ClearAt rules, with "SweepPath" in the sentence), `client` missing or empty, **3 to 256** points, `step` **8 to 240** px, `dwellMs` **50 to 5000**, a missing guard, the ClearAt guard rules, then per point: empty, outside `client`, not a whole number of `step` px from `Path[0]` on both axes, the same as the point before it; last, a path that does not end on `Path[0]`. Then `busy` while any playback runs, `no-targets-resolved` when the account is not running.
- **Deviation 1, `step`:** the brief asks to "refuse a path whose points are not one step apart", but the spec's path must jump over HUD points and return to the start block, so hops longer than one step are required. The rule implemented: every point lies on the `step` lattice anchored at `Path[0]` and no point repeats the one before it, so every hop is a whole number of steps, at least one. That needs the step, so the request carries `step` (additive; Ur OCR sends its integer block size).
- **Deviation 2, the guard is required.** ClearAt's guard is optional; a held sweep's whole safety story rests on it, so a SweepPath without one is refused. Ur OCR only sweeps with a finder that has a guard.
- **Deviation 3, the path closes.** The start block is sent twice, first and last, so "release only on the start block" is visible on the wire and checked. Ur Task still releases on the placed `Path[0]` after an early end.
- The synthetic macro's id is `sweep-<playbackId>`, it is never saved or listed, and `ClearAtMacro.SharesBaseline` is false for it.
- No host change. The contract stays a NuGet `PackageReference`; the bridge stays contract 1.0 (the method is additive).
- Build the project, never the solution: `dotnet build rororo-ur-task.csproj`.
- **Close Ur Task before any build or test step.** It runs from `bin\Debug` and holds its DLLs; the test project builds the app project too.
- Fast tests: `dotnet test tests/rororo-ur-task.Tests/rororo-ur-task.Tests.csproj -p:StandaloneTestsOnly=true`. Task 0 records the post-merge baseline as **B**. Expected after this plan: **B + 43** (Task 1 +9, Task 2 +23, Task 3 +7, Task 4 +4).
- The host-integration suite (without the flag) needs `ROROROblox` beside this repo at a commit with RoRoRo #223 (`5b5c9e9` or later). CI's `host-integration` job builds it against `ROROROblox` main; a local run against an older sibling checkout fails to compile `NoOpLauncher` and proves nothing.
- Run every command from the repo root. Check with `git rev-parse --show-toplevel` after any `cd`.
- Never `git add` the untracked `.claude/626labs-context.md`, `.gitnexus/` or `AGENTS.md`. Add files by path.
- Commits: conventional commits; every message ends with the line `Co-Authored-By: Claude Opus 5.5 <noreply@anthropic.com>` (name the model that actually implemented the task). Never bypass the pre-commit hooks.
- No absolute user-profile paths (drive letter plus Users folder) in any committed file. The pre-commit local-path guard rejects them.
- **Version: 0.12.0** in `rororo-ur-task.csproj` and `manifest.json` (Task 4), both files, together; `VersionConsistencyTests` pins it. Nothing is released. The CHANGELOG entry goes under a new `## 0.12.0 — unreleased`.
- Log and sentence numbers are invariant culture.
- Do not push. The controller pushes.

## Review Focus

1. **Focus lost in the middle of a sweep** (alt-tab, a toast or another window taking the foreground while the button is down): the button comes up at once and the playback reads aborted; it is never left held. Pinned in Task 1 by `Losing_the_foreground_mid_sweep_releases_in_place_and_aborts`.
2. **Esc or StopMacro in the middle of a sweep:** the release lands on the start block, not wherever the pointer happens to be (a release is a click). Pinned in Task 1 by `Esc_mid_sweep_goes_back_to_the_start_block_to_release` and in Task 3 by `SweepPath_is_stopped_by_StopMacro_like_any_playback`.
3. **A menu or a player's profile opens between two guard samples:** the pointer goes back to the start block, lets go there, and nothing more is sent; GetPlayback reads `failed` / `check-failed`. Pinned in Task 1 by `A_guard_change_mid_sweep_goes_back_to_the_start_releases_there_and_stops` and `The_guard_is_sampled_every_3_points_while_held`.
4. **A path Ur OCR got wrong** (not closed, off the step lattice, a repeated point, a point at or past the border, too long): refused before any input with one sentence Ur OCR can log. Pinned in Task 2 by `Validate_refuses_with_one_sentence` and in Task 4 by `SweepPath_over_the_pipe_refuses_a_path_that_does_not_end_on_its_start`.
5. **Display-scale slack** (the window came out 1000x749 for an 800x599 path): every point scales with the window, and a point that rounds outside the live window stops the step before the press. Pinned in Task 1 by `Sweep_points_scale_with_the_window` and `A_sweep_point_outside_the_live_window_stops_before_any_input`.

Also pinned: a SweepPath while a ClearAt runs is refused `busy` (Task 3, `SweepPath_and_ClearAt_share_the_single_flight_rule`).

---

## File map

| File | Change | Responsibility |
| --- | --- | --- |
| `CHANGELOG.md`, `manifest.json`, `rororo-ur-task.csproj` | merge (Task 0), modify (Task 4) | merge resolution; 0.12.0 and its entry |
| `src/Macros/Steps/MacroStep.cs` | modify | `SweepPoint`, `SweepStep`; `StepTiming.SweepGuardEveryPoints`, `EstimateMs`; `StepValidator` sweep rule |
| `src/Macros/Steps/StepRunner.cs` | modify | `PlaySweepAsync`, `DwellAsync`, `ReleaseOnStart`; `CheckGuard` split into `GuardBroken` + `GuardStopText` |
| `src/Ipc/BridgeContract.cs` | modify | `SweepPathRequest`, `BridgeContract.MethodSweepPath` |
| `src/Ipc/ClearAtMacro.cs` | modify | `TargetProblem` and `ValidateGuard` shared with SweepPath (ClearAt sentences unchanged) |
| `src/Ipc/SweepPathMacro.cs` | create | pure: `Validate`, `Build`, `NameFor`, `StartLine`, `GuardLine` |
| `src/Ipc/IMacroRunInvoker.cs`, `src/Ipc/MacroRunInvoker.cs` | modify | `SweepPathAsync` |
| `src/Ipc/MacroRunnerServer.cs` | modify | dispatch `SweepPath` |
| `tests/rororo-ur-task.Tests/Steps/StepRunnerTests.cs` | modify | sweep playback tests |
| `tests/rororo-ur-task.Tests/Ipc/SweepPathMacroTests.cs` | create | deserialization, build, validation, log lines |
| `tests/rororo-ur-task.Tests/Ipc/MacroRunInvokerTests.cs` | modify | invoker behaviour |
| `tests/rororo-ur-task.Tests/Ipc/MacroRunnerServerTests.cs` | modify | `FakeInvoker.SweepPathAsync`; dispatch tests |
| `tests/rororo-ur-task.Tests/MacroRunnerServerPipeBusyTests.cs` | modify | `NoopInvoker.SweepPathAsync` |

---

### Task 0: Merge main into feat/ore-stop-pulse

**Files:**
- Merge: `CHANGELOG.md`, `manifest.json`, `rororo-ur-task.csproj` (conflicts), and main's `src/Macros/MacroV1Migrator.cs`, `tests/rororo-ur-task.Tests/MacroSchemaVersionTests.cs`, `tests/rororo-ur-task.Tests/PluginClientIntegrationTests.cs` (clean).

**Interfaces:**
- Consumes: `origin/main` at `5f5be31` or later (Ur Task 0.9.1: a macro with a newer schema is refused at load in `MacroV1Migrator`; `PluginClientIntegrationTests.NoOpLauncher` returns 4-tuples with a `reasonCode`, for RoRoRo #223).
- Produces: a merge commit on `feat/ore-stop-pulse`, version still **0.11.0**, and the post-merge baseline **B** every later task counts from.

- [ ] **Step 1: Check where you are**

Run: `git rev-parse --show-toplevel` (the rororo-ur-task root), `git rev-parse --abbrev-ref HEAD` (`feat/ore-stop-pulse`), `git status --short`.
Expected: only `?? .claude/626labs-context.md`, `?? .gitnexus/`, `?? AGENTS.md`. Anything else: stop and ask the controller.

- [ ] **Step 2: Merge**

Run: `git fetch origin` then `git merge --no-ff origin/main`
Expected: `CONFLICT (content)` in exactly `CHANGELOG.md`, `manifest.json` and `rororo-ur-task.csproj`; everything else merges clean. Any other conflict: stop and ask.

- [ ] **Step 3: Resolve the version conflicts**

The branch is ahead of main (0.11.0 unreleased against main's released 0.9.1), so the branch's version stands here; Task 4 bumps it to 0.12.0 with the feature. In `rororo-ur-task.csproj` keep exactly:

```xml
    <Version>0.11.0</Version>
```

In `manifest.json` keep exactly:

```json
  "version": "0.11.0",
```

Delete the conflict markers and main's `0.9.1` lines in both files.

- [ ] **Step 4: Resolve the CHANGELOG conflict**

The conflict block runs from `<<<<<<< HEAD` (just after the intro paragraph) to `>>>>>>> origin/main`. Keep the HEAD side's `## 0.11.0 — unreleased` and `## 0.10.0 — unreleased` sections exactly as they are, drop the HEAD side's last line `## 0.9.0 — unreleased`, then keep main's side whole. The result, from the intro paragraph down to the start of 0.9.0's body, reads:

```markdown
## 0.11.0 — unreleased

(the 0.11.0 section, unchanged)

## 0.10.0 — unreleased

(the 0.10.0 section, unchanged, ending with the "Every playback's ending is in `ur-task.log`." bullet)

## 0.9.1 — 2026-09-29

### Fixed

- **A macro from a newer Ur Task is refused instead of run.** It shows as a load failure that
  says to update Ur Task. Before, an older Ur Task loaded it as an empty macro, started playing
  it and never finished, so a keep-alive looked like it was running while it kept nobody awake.

## 0.9.0 — 2026-09-27

### Added
```

Then check: `git grep -n -e "^<<<<<<<" -e "^=======" -e "^>>>>>>>" -- CHANGELOG.md manifest.json rororo-ur-task.csproj` prints nothing, and `git grep -c "^## 0.9.0" -- CHANGELOG.md` prints `CHANGELOG.md:1`.

- [ ] **Step 5: Build and run the fast suite**

Close Ur Task. Run: `dotnet build rororo-ur-task.csproj`
Expected: Build succeeded, 0 errors.

Run: `dotnet test tests/rororo-ur-task.Tests/rororo-ur-task.Tests.csproj -p:StandaloneTestsOnly=true`
Expected: 0 failed. The three `MacroSchemaVersionTests` from main are in the count. Write the passed count down as **B**; every later task states its count as B plus its tests.

Do not run the suite without the flag here: `PluginClientIntegrationTests` now needs the host at RoRoRo #223, and CI's `host-integration` job builds it against `ROROROblox` main.

- [ ] **Step 6: Commit the merge**

```bash
git add CHANGELOG.md manifest.json rororo-ur-task.csproj
git commit -m "chore: merge main (0.9.1, host #223 test fix) into feat/ore-stop-pulse

Co-Authored-By: Claude Opus 5.5 <noreply@anthropic.com>"
```

Run: `git log -1 --format=%P` prints two parents.

---

### Task 1: SweepStep and its playback

**Files:**
- Modify: `src/Macros/Steps/MacroStep.cs` (after `HoldStep`; `StepTiming`; `StepValidator.Validate`)
- Modify: `src/Macros/Steps/StepRunner.cs` (`RunAsync` switch; the guard section; a new sweep section)
- Modify: `tests/rororo-ur-task.Tests/Steps/StepRunnerTests.cs` (append inside the class)

**Interfaces:**
- Consumes (existing): `StepContext.Guard` (`ScreenGuard?`), `SendGuarded`, `GuardForeground`, `SampleGuarded`, `JumpAsync`, `Place`, `StopException`, `InputBlockedException`, `StepTiming.PollMs` (100), `StepTiming.JumpWiggleMs` (150); in the tests `FakeIo`, `Ctx`, `Guarded`, `Guard` (the 3x3 `ScreenGuard` at 55,289), `DotRed`, `White`, `Rock`.
- Produces:
  - `public sealed record SweepPoint(int X, int Y);` (namespace `Labs626.UrTask.Macros.Steps`, used on the wire by Task 2)
  - `public sealed record SweepStep(int DelayMs, IReadOnlyList<SweepPoint> Path, int DwellMs, int Button = 1) : MacroStep(DelayMs);`
  - `StepTiming.SweepGuardEveryPoints = 3`
  - Log lines `swept N points in S.S s` and `swept K of N points in S.S s, then the playback ended[; released on the start block]`; the guard stop sentence `SweepPath stopped: the guard at (X,Y) isn't the expected colour (saw #RRGGBB); something may be over the game (a menu or a player's profile).`

- [ ] **Step 1: Write the failing tests**

In `tests/rororo-ur-task.Tests/Steps/StepRunnerTests.cs`, append inside the class, just before its final closing brace:

```csharp
    // ---------- sweep (ore-stop sweep spec, "The Ur Task side") ----------

    private static SweepStep Sweep((int X, int Y)[] path, int dwellMs = 400)
        => new(0, path.Select(p => new SweepPoint(p.X, p.Y)).ToList(), dwellMs);

    /// <summary>Ring 1 around 400,300 at 50 px, from the east block and back to it: 9 points.</summary>
    private static readonly (int X, int Y)[] Ring1 =
    {
        (450, 300), (450, 250), (400, 250), (350, 250), (350, 300), (350, 350), (400, 350), (450, 350), (450, 300),
    };

    private const string SweepGuardStop = "SweepPath stopped: the guard at (55,289) isn't the expected colour (saw #F5F5F5); "
        + "something may be over the game (a menu or a player's profile).";

    [Fact]
    public async Task A_sweep_presses_on_the_start_moves_through_every_point_and_releases_on_the_start()
    {
        // Jump 0..150, press at 150, dwell 400 at each point but the last: moves at 550, 950, 1350,
        // and the release right on arrival at the closing point.
        var log = new List<string>();
        var io = new FakeIo { Screen = (_, _, _) => Rock };
        var r = await StepRunner.RunAsync(new MacroStep[] { Sweep(new[] { (450, 300), (450, 250), (400, 250), (450, 300) }) },
            Ctx(log), io, default);

        Assert.Equal(PlaybackOutcome.Completed, r.Outcome);
        Assert.False(r.SkippedByReach);
        var down = Assert.Single(io.Downs);
        Assert.Equal((450, 300, 150L, 1), (down.X, down.Y, down.TimestampMs, down.MouseButton));
        Assert.Equal(new[] { (450, 250, 550L), (400, 250, 950L), (450, 300, 1350L) },
            io.Sent.Where(e => e.Kind == MacroEventKind.MouseMove && e.TimestampMs >= 150).Select(e => (e.X, e.Y, e.TimestampMs)));
        var up = Assert.Single(io.Ups);
        Assert.Equal((450, 300, 1350L), (up.X, up.Y, up.TimestampMs));
        Assert.Empty(io.Released); // it let go itself; the finally had nothing to do
        Assert.Contains("swept 4 points in 1.2 s", log);
    }

    [Fact]
    public async Task A_guard_change_mid_sweep_goes_back_to_the_start_releases_there_and_stops()
    {
        // Pressed at 150, moves at 550 and 950; the dot turns white at 1000 and the sample before
        // point 4 (at 1350) sees it. Point 4 (350,250) is never reached.
        var log = new List<string>();
        var io = Guarded(new FakeIo { Screen = (_, _, _) => Rock }, io => io.NowMs < 1000);
        var r = await StepRunner.RunAsync(new MacroStep[] { Sweep(Ring1) }, Ctx(log, guard: Guard), io, default);

        Assert.Equal(PlaybackOutcome.Aborted, r.Outcome);
        Assert.Equal((0, SweepGuardStop), (r.StepIndex, r.Reason));
        Assert.Equal((450, 300), (io.Downs.Single().X, io.Downs.Single().Y));
        Assert.Equal(new[] { (MacroEventKind.MouseMove, 450, 300), (MacroEventKind.MouseUp, 450, 300) },
            io.Sent.TakeLast(2).Select(e => (e.Kind, e.X, e.Y)));
        Assert.DoesNotContain(io.Sent, e => e.Kind == MacroEventKind.MouseMove && (e.X, e.Y) == (350, 250));
        Assert.Empty(io.Released);
        Assert.Contains("swept 3 of 9 points in 1.2 s, then the playback ended; released on the start block", log);
    }

    [Fact]
    public async Task A_guard_covered_before_the_press_sends_nothing_at_all()
    {
        var io = Guarded(new FakeIo { Screen = (_, _, _) => Rock }, _ => false);
        var r = await StepRunner.RunAsync(new MacroStep[] { Sweep(Ring1) }, Ctx(guard: Guard), io, default);

        Assert.Equal((0, SweepGuardStop), (r.StepIndex, r.Reason));
        Assert.Empty(io.Sent);
    }

    [Fact]
    public async Task The_guard_is_sampled_every_3_points_while_held()
    {
        // Twice before the press, then before points 4 and 7 of 9.
        var io = Guarded(new FakeIo { Screen = (_, _, _) => Rock }, _ => true);
        var r = await StepRunner.RunAsync(new MacroStep[] { Sweep(Ring1) }, Ctx(guard: Guard), io, default);

        Assert.Equal(PlaybackOutcome.Completed, r.Outcome);
        Assert.Equal(4, io.CaptureRects.Count(c => c == (55, 289, 3, 3)));
        Assert.Equal((450, 300), (io.Ups.Single().X, io.Ups.Single().Y));
    }

    [Fact]
    public async Task Losing_the_foreground_mid_sweep_releases_in_place_and_aborts()
    {
        // At (450,250) from 550; the dwell's poll at 750 finds another window in front.
        var log = new List<string>();
        var io = new FakeIo { Screen = (_, _, _) => Rock };
        io.OnDelay = () => { if (io.NowMs >= 700) io.Foreground = false; };
        var r = await StepRunner.RunAsync(new MacroStep[] { Sweep(Ring1) }, Ctx(log), io, default);

        Assert.Equal(PlaybackOutcome.Aborted, r.Outcome);
        Assert.Equal("Foreground shifted away from CElCPapa at step 1/1.", r.Reason);
        Assert.Equal(new[] { 1 }, io.Released); // RunAsync's finally, where the pointer is
        Assert.Equal((450, 250), (io.Ups.Single().X, io.Ups.Single().Y));
        Assert.Contains("swept 2 of 9 points in 0.6 s, then the playback ended", log);
    }

    [Fact]
    public async Task Esc_mid_sweep_goes_back_to_the_start_block_to_release()
    {
        using var cts = new CancellationTokenSource();
        var log = new List<string>();
        var io = new FakeIo { Screen = (_, _, _) => Rock };
        io.OnDelay = () => { if (io.NowMs >= 700) cts.Cancel(); };
        var r = await StepRunner.RunAsync(new MacroStep[] { Sweep(Ring1) }, Ctx(log), io, cts.Token);

        Assert.Equal(PlaybackOutcome.Aborted, r.Outcome);
        Assert.Equal("Playback cancelled.", r.Reason);
        Assert.Equal(new[] { (MacroEventKind.MouseMove, 450, 300), (MacroEventKind.MouseUp, 450, 300) },
            io.Sent.TakeLast(2).Select(e => (e.Kind, e.X, e.Y)));
        Assert.Empty(io.Released);
        Assert.Contains("swept 2 of 9 points in 0.6 s, then the playback ended; released on the start block", log);
    }

    [Fact]
    public async Task Sweep_points_scale_with_the_window()
    {
        // Measured at 800x599, played at 1000x749: 400,300 lands at 500,375 and 480,240 at 600,300.
        var io = new FakeIo { Client = (1000, 749), Screen = (_, _, _) => Rock };
        await StepRunner.RunAsync(new MacroStep[] { Sweep(new[] { (400, 300), (400, 240), (480, 240), (400, 300) }) },
            Ctx(actual: (1000, 749)), io, default);

        Assert.Equal((500, 375), (io.Downs.Single().X, io.Downs.Single().Y));
        Assert.Equal(new[] { (500, 300), (600, 300), (500, 375) },
            io.Sent.Where(e => e.Kind == MacroEventKind.MouseMove && e.TimestampMs >= StepTiming.JumpWiggleMs).Select(e => (e.X, e.Y)));
        Assert.Equal((500, 375), (io.Ups.Single().X, io.Ups.Single().Y));
    }

    [Fact]
    public async Task A_sweep_point_outside_the_live_window_stops_before_any_input()
    {
        var io = new FakeIo { Client = (500, 599), Screen = (_, _, _) => Rock };
        var r = await StepRunner.RunAsync(new MacroStep[] { Sweep(new[] { (450, 300), (550, 300), (450, 300) }) }, Ctx(), io, default);

        Assert.Equal(PlaybackOutcome.Aborted, r.Outcome);
        Assert.Equal((0, "CElCPapa: step 1 'SweepPath' has a point outside the window."), (r.StepIndex, r.Reason));
        Assert.Empty(io.Sent);
    }

    [Fact]
    public async Task A_sweep_with_fewer_than_2_points_is_refused_before_playing()
    {
        var r = await StepRunner.RunAsync(new MacroStep[] { Sweep(new[] { (450, 300) }) }, Ctx(), new FakeIo(), default);

        Assert.Equal(PlaybackOutcome.Refused, r.Outcome);
        Assert.Equal("Step 1 is a sweep with fewer than 2 points.", r.Reason);
    }
```

- [ ] **Step 2: Run the tests to verify they fail**

Close Ur Task. Run: `dotnet test tests/rororo-ur-task.Tests/rororo-ur-task.Tests.csproj -p:StandaloneTestsOnly=true --filter "FullyQualifiedName~StepRunnerTests"`
Expected: build FAILS with `CS0246: The type or namespace name 'SweepStep' could not be found` (and `SweepPoint`).

- [ ] **Step 3: Add the step, its timing and its validator rule**

In `src/Macros/Steps/MacroStep.cs`, insert after the `HoldStep` record and before `DragStep`:

```csharp
/// <summary>A point of a <see cref="SweepStep"/> path, in the macro's recorded client pixels. The
/// SweepPath bridge call sends these as <c>{ "x": .., "y": .. }</c>.</summary>
public sealed record SweepPoint(int X, int Y);

/// <summary>
/// One continuous hold along a path (ore-stop sweep spec, "The Ur Task side"): the button goes down
/// on Path[0], the pointer moves to each later point with one real mouse move and waits DwellMs
/// there, and the button comes up on the last point, which the bridge made sure is Path[0] again.
/// Whatever block is under the pointer while the button is down gets mined, so nothing is looked at.
/// Built in memory for a SweepPath call and never saved, so it has no JSON kind.
/// </summary>
public sealed record SweepStep(int DelayMs, IReadOnlyList<SweepPoint> Path, int DwellMs, int Button = 1) : MacroStep(DelayMs);
```

In `StepTiming`, add after `OutlineMoveTolerancePx`:

```csharp
    /// <summary>While a sweep holds the button, the guard is sampled before every this-many-th point
    /// (spec: "sampled every few points"). At the default 400 ms dwell that is every 1.2 s.</summary>
    public const int SweepGuardEveryPoints = 3;
```

In `StepTiming.EstimateMs`, add a case before `DragStep d => ...`:

```csharp
        SweepStep w => JumpWiggleMs + (long)w.DwellMs * Math.Max(0, (w.Path?.Count ?? 0) - 1),
```

In `StepValidator.Validate`, add a case after the `HoldStep h` case:

```csharp
                case SweepStep s:
                {
                    if (s.Path is null || s.Path.Count < 2) return $"Step {n} is a sweep with fewer than 2 points.";
                    if (s.Path.Any(p => p is null)) return $"Step {n} is a sweep with an empty point.";
                    if (s.DwellMs < 1) return $"Step {n} is a sweep with a dwell below 1 ms.";
                    break;
                }
```

- [ ] **Step 4: Play it in StepRunner**

In `src/Macros/Steps/StepRunner.cs`, `RunAsync`, add after the `case HoldStep h:` block:

```csharp
                    case SweepStep s:
                        await PlaySweepAsync(s, i, ctx, io, heldButtons, ct);
                        break;
```

Replace the whole `CheckGuard` method with:

```csharp
    private static void CheckGuard(StepContext ctx, IStepIo io, int index, string name, string who = "ClearAt")
    {
        if (GuardBroken(ctx, io, index, name) is { } seen) throw new StopException(GuardStopText(who, ctx.Guard!, seen), index);
    }

    /// <summary>The colour the guard box shows when it has moved past its tolerance; null when it
    /// holds or there is no guard. The box is placed and scaled like a reach box. A box it cannot
    /// see stops the step, as any capture does.</summary>
    private static Rgb? GuardBroken(StepContext ctx, IStepIo io, int index, string name)
    {
        if (ctx.Guard is not { } g) return null;
        var rect = PointMath.ScaledRect(Place(ctx, (g.X, g.Y)), new CheckBox(0, 0, g.W, g.H), ctx.RecordedClient, ctx.ActualClient);
        var seen = SampleGuarded(io, rect) ?? throw new StopException($"{name} could not see the window.", index);
        return seen.DistanceTo(g.Expect) <= g.Tolerance ? null : seen;
    }

    private static string GuardStopText(string who, ScreenGuard g, Rgb seen) => string.Create(CultureInfo.InvariantCulture,
        $"{who} stopped: the guard at ({g.X},{g.Y}) isn't the expected colour (saw {seen.Hex}); something may be over the game (a menu or a player's profile).");
```

(The ClearAt sentence is unchanged: every existing `CheckGuard` call passes no `who`.)

Then insert this section right after the guard section and before `// ---------- reach (the white outline) ----------`:

```csharp
    // ---------- sweep (ore-stop sweep spec, "The Ur Task side") ----------

    /// <summary>
    /// One continuous hold along a path. Whatever block is under the pointer while the button is down
    /// gets mined, so there is no per-point look, baseline or park. After the usual jump the button
    /// goes down on Path[0], the start block beside the character; the pointer moves to each later
    /// point with one real mouse move (Roblox ignores a bare cursor jump; a hop over the HUD is the
    /// same single straight input) and waits DwellMs there; the button comes up on the last point,
    /// which the bridge made sure is Path[0] again. A release is a click, and the start block is the
    /// one place a click can't land on a player or a chest.
    /// <para>The guard is sampled before the press and before every
    /// <see cref="StepTiming.SweepGuardEveryPoints"/>-th point while held. A change goes back to the
    /// start block, lets go there, and stops the playback as a failed check. The foreground is checked
    /// before every input and every <see cref="StepTiming.PollMs"/> of a dwell; losing it lets go in
    /// place through RunAsync's finally, as every hold does. Esc, StopMacro and a window it can no
    /// longer see also go back to the start block to let go, when the target is still in front.</para>
    /// </summary>
    private static async Task PlaySweepAsync(SweepStep s, int index, StepContext ctx, IStepIo io, HashSet<int> heldButtons, CancellationToken ct)
    {
        const string who = "SweepPath";
        var name = $"{ctx.AccountName}: step {index + 1} '{who}'";
        var client = io.ClientSize() ?? throw new StopException($"{name} could not see the window.", index);
        var path = s.Path.Select(p => Place(ctx, (p.X, p.Y))).ToList();
        if (path.Any(p => p.X < 0 || p.Y < 0 || p.X >= client.W || p.Y >= client.H))
            throw new StopException($"{name} has a point outside the window.", index);
        var start = path[0];

        await io.Delay(s.DelayMs, ct);
        CheckGuard(ctx, io, index, name, who);   // nothing is down yet: a covered game gets no input at all
        await JumpAsync(io, start, ct);          // Roblox drops a press after focus unless it saw movement
        CheckGuard(ctx, io, index, name, who);   // the jump took a wiggle: look again before the press
        SendGuarded(io, new MacroEvent(0, MacroEventKind.MouseDown, 0, start.X, start.Y, s.Button, 0));
        heldButtons.Add(s.Button);
        var pressedAt = io.NowMs;
        var reached = 1;
        try
        {
            await DwellAsync(io, s.DwellMs, ct);
            for (int k = 1; k < path.Count; k++)
            {
                if (k % StepTiming.SweepGuardEveryPoints == 0 && GuardBroken(ctx, io, index, name) is { } seen)
                    throw new StopException(GuardStopText(who, ctx.Guard!, seen), index);
                SendGuarded(io, new MacroEvent(0, MacroEventKind.MouseMove, 0, path[k].X, path[k].Y, 0, 0));
                reached++;
                if (k < path.Count - 1) await DwellAsync(io, s.DwellMs, ct); // the last point is where it lets go
            }
            SendGuarded(io, new MacroEvent(0, MacroEventKind.MouseUp, 0, path[^1].X, path[^1].Y, s.Button, 0));
            heldButtons.Remove(s.Button);
        }
        catch (Exception e)
        {
            // A lost foreground lets RunAsync's finally release in place, as every hold does.
            var onStart = e is not InputBlockedException && ReleaseOnStart(io, start, s.Button, heldButtons);
            ctx.Log(string.Create(CultureInfo.InvariantCulture,
                $"swept {reached} of {path.Count} points in {(io.NowMs - pressedAt) / 1000.0:F1} s, then the playback ended{(onStart ? "; released on the start block" : "")}"));
            throw;
        }
        ctx.Log(string.Create(CultureInfo.InvariantCulture, $"swept {path.Count} points in {(io.NowMs - pressedAt) / 1000.0:F1} s"));
    }

    /// <summary>Waits <paramref name="ms"/> with the button down, checking the foreground every
    /// <see cref="StepTiming.PollMs"/>.</summary>
    private static async Task DwellAsync(IStepIo io, int ms, CancellationToken ct)
    {
        var start = io.NowMs;
        while (io.NowMs - start < ms)
        {
            await io.Delay((int)Math.Min(StepTiming.PollMs, ms - (io.NowMs - start)), ct);
            GuardForeground(io);
        }
    }

    /// <summary>Best effort and never a wait (the token may already be cancelled): with the target
    /// still in front, move to the start block and let go there. False when it could not; RunAsync's
    /// finally then lets go wherever the pointer is.</summary>
    private static bool ReleaseOnStart(IStepIo io, (int X, int Y) start, int button, HashSet<int> heldButtons)
    {
        if (!heldButtons.Contains(button) || !io.TargetInForeground()) return false;
        if (!io.Send(new MacroEvent(0, MacroEventKind.MouseMove, 0, start.X, start.Y, 0, 0))) return false;
        if (!io.Send(new MacroEvent(0, MacroEventKind.MouseUp, 0, start.X, start.Y, button, 0))) return false;
        heldButtons.Remove(button);
        return true;
    }
```

- [ ] **Step 5: Run the tests to verify they pass**

Run: `dotnet test tests/rororo-ur-task.Tests/rororo-ur-task.Tests.csproj -p:StandaloneTestsOnly=true --filter "FullyQualifiedName~StepRunnerTests"`
Expected: PASS, the 9 new sweep tests and every existing guard test (their sentence still reads `ClearAt stopped: ...`).

- [ ] **Step 6: Run the full fast suite**

Run: `dotnet test tests/rororo-ur-task.Tests/rororo-ur-task.Tests.csproj -p:StandaloneTestsOnly=true`
Expected: **B + 9 passed**, 0 failed.

- [ ] **Step 7: Commit**

```bash
git add src/Macros/Steps/MacroStep.cs src/Macros/Steps/StepRunner.cs tests/rororo-ur-task.Tests/Steps/StepRunnerTests.cs
git commit -m "feat(steps): a sweep step, one hold along a path that lets go on its start block

Co-Authored-By: Claude Opus 5.5 <noreply@anthropic.com>"
```

---

### Task 2: SweepPath request and the synthetic macro

**Files:**
- Modify: `src/Ipc/BridgeContract.cs` (record after `ClearAtRequest`; constant in `BridgeContract`)
- Modify: `src/Ipc/ClearAtMacro.cs` (`TargetProblem` and `ValidateGuard` become `internal` and take `who`)
- Create: `src/Ipc/SweepPathMacro.cs`
- Create: `tests/rororo-ur-task.Tests/Ipc/SweepPathMacroTests.cs`

**Interfaces:**
- Consumes (Task 1): `SweepPoint`, `SweepStep`, `StepValidator.Validate`.
- Consumes (existing): `ClearAtClient`, `ClearAtGuard(int X, int Y, int W, int H, Rgb? Expect, int Tolerance)`, `ScreenGuard`, `Macro`, `Macro.CurrentSchemaVersion`, `Macro.CoordSpaceClient`, `OutlineCheck.MaxSide` (240), `ClearAtMacro.SharesBaseline`.
- Produces:
  - `public sealed record SweepPathRequest(string ContractVersion, string Method, string? CallerPluginId, string? Target, ClearAtClient? Client, IReadOnlyList<SweepPoint>? Path, int Step, int DwellMs, ClearAtGuard? Guard);`
  - `BridgeContract.MethodSweepPath = "SweepPath"`
  - `internal static string? ClearAtMacro.TargetProblem(string? target, string who)`, `internal static string? ClearAtMacro.ValidateGuard(ClearAtGuard g, ClearAtClient client, string who = "ClearAt")`
  - `internal static class SweepPathMacro` with `MaxPoints = 256`, `MinStep = 8`, `MaxStep = 240`, `MinDwellMs = 50`, `MaxDwellMs = 5000`, `IdPrefix = "sweep-"`, `static string? Validate(SweepPathRequest r)`, `static Macro Build(SweepPathRequest r, string macroId)` (only after `Validate` returned null), `static string NameFor(int points)`, `static string StartLine(string playbackId, Macro macro, string account, SweepPathRequest r)`, `static string GuardLine(SweepPathRequest r)`.

- [ ] **Step 1: Write the failing tests**

Create `tests/rororo-ur-task.Tests/Ipc/SweepPathMacroTests.cs`:

```csharp
// tests/rororo-ur-task.Tests/Ipc/SweepPathMacroTests.cs
using System.Text.Json;
using Labs626.UrTask.Ipc;
using Labs626.UrTask.Macros;
using Labs626.UrTask.Macros.Steps;

namespace Labs626.UrTask.Tests.Ipc;

public class SweepPathMacroTests
{
    // The wire shape Ur OCR's SweepPathClientTests pins, byte for byte apart from the target.
    private const string ContractJson = """
        { "contractVersion": "1.0", "method": "SweepPath", "callerPluginId": "626labs.ur-ocr",
          "target": "123456789",
          "client": { "w": 800, "h": 599 },
          "path": [ { "x": 450, "y": 300 }, { "x": 450, "y": 250 }, { "x": 400, "y": 250 }, { "x": 450, "y": 300 } ],
          "step": 50, "dwellMs": 400,
          "guard": { "x": 55, "y": 289, "w": 3, "h": 3, "expect": { "r": 255, "g": 19, "b": 90 }, "tolerance": 30 } }
        """;

    private static SweepPathRequest Contract() => JsonSerializer.Deserialize<SweepPathRequest>(ContractJson, BridgeContract.Json)!;

    private static readonly ClearAtGuard Dot = new(55, 289, 3, 3, new Rgb(255, 19, 90), 30);

    /// <summary>Ring 1 around 400,300 at 50 px, from the east block and back to it.</summary>
    private static readonly (int X, int Y)[] Ring1 =
    {
        (450, 300), (450, 250), (400, 250), (350, 250), (350, 300), (350, 350), (400, 350), (450, 350), (450, 300),
    };

    private static SweepPathRequest Valid(IEnumerable<(int X, int Y)>? path = null, int step = 50, int dwellMs = 400) => new(
        "1.0", "SweepPath", "626labs.ur-ocr", "123", new ClearAtClient(800, 599),
        (path ?? Ring1).Select(p => new SweepPoint(p.X, p.Y)).ToList(), step, dwellMs, Dot);

    /// <summary><paramref name="n"/> points on the 50 px lattice from 100,100: back and forth along a
    /// row, then one block down and back up to the start, so it closes.</summary>
    private static IEnumerable<(int X, int Y)> Long(int n)
        => Enumerable.Range(0, n - 2).Select(i => (100 + i % 2 * 50, 100)).Append((100, 150)).Append((100, 100));

    [Fact]
    public void The_contract_example_deserializes()
    {
        var r = Contract();
        Assert.Equal(("SweepPath", "123456789", 800, 599, 50, 400), (r.Method, r.Target, r.Client!.W, r.Client.H, r.Step, r.DwellMs));
        Assert.Equal(new[] { new SweepPoint(450, 300), new SweepPoint(450, 250), new SweepPoint(400, 250), new SweepPoint(450, 300) }, r.Path);
        Assert.Equal(Dot, r.Guard);
    }

    [Fact]
    public void Build_makes_one_unsaved_sweep_step_with_the_guard()
    {
        var m = SweepPathMacro.Build(Contract(), "sweep-pb1");

        Assert.Equal(("sweep-pb1", "SweepPath (4 points)", Macro.CoordSpaceClient, (int?)800, (int?)599),
            (m.Id, m.Name, m.CoordSpace, m.RecordedClientW, m.RecordedClientH));
        Assert.Empty(m.Events);
        var s = Assert.IsType<SweepStep>(Assert.Single(m.Steps!));
        Assert.Equal((0, 400, 1), (s.DelayMs, s.DwellMs, s.Button));
        Assert.Equal(Contract().Path, s.Path);
        Assert.Equal(new ScreenGuard(55, 289, 3, 3, new Rgb(255, 19, 90), 30), m.Guard);
        Assert.False(ClearAtMacro.SharesBaseline(m.Id));
    }

    [Fact]
    public void Build_names_the_macro_by_its_point_count()
        => Assert.Equal("SweepPath (9 points)", SweepPathMacro.Build(Valid(), "sweep-x").Name);

    [Fact]
    public void The_built_step_passes_the_step_validator()
        => Assert.Null(StepValidator.Validate(SweepPathMacro.Build(Valid(Long(256)), "sweep-x").Steps!));

    [Fact]
    public void The_log_lines_name_the_start_the_step_the_dwell_and_the_guard()
    {
        var r = Contract();
        var m = SweepPathMacro.Build(r, "sweep-pb1");

        Assert.Equal("bridge playback pb1 'SweepPath (4 points)' on alt-1: from 450,300, 50 px steps, 400 ms a point",
            SweepPathMacro.StartLine("pb1", m, "alt-1", r));
        Assert.Equal("SweepPath guard at (55,289), expecting #FF135A ±30", SweepPathMacro.GuardLine(r));
    }

    [Fact]
    public void Validate_accepts_the_contract_example_ring_1_and_256_points()
    {
        Assert.Null(SweepPathMacro.Validate(Contract()));
        Assert.Null(SweepPathMacro.Validate(Valid()));
        Assert.Null(SweepPathMacro.Validate(Valid(Long(256))));
    }

    [Theory]
    [InlineData("no-target", "SweepPath needs a target account.")]
    [InlineData("zero-target", "SweepPath target '0' is not a decimal user id.")]
    [InlineData("foreground-target", "SweepPath needs a decimal user id; got an invalid target.")]
    [InlineData("no-client", "SweepPath needs the client size the path was measured in.")]
    [InlineData("2-points", "SweepPath takes 3 to 256 points; got 2.")]
    [InlineData("257-points", "SweepPath takes 3 to 256 points; got 257.")]
    [InlineData("step-7", "SweepPath needs a step of 8 to 240 px; got 7.")]
    [InlineData("step-241", "SweepPath needs a step of 8 to 240 px; got 241.")]
    [InlineData("dwell-49", "SweepPath needs a dwellMs of 50 to 5000; got 49.")]
    [InlineData("dwell-5001", "SweepPath needs a dwellMs of 50 to 5000; got 5001.")]
    [InlineData("no-guard", "SweepPath needs a guard: the pixel it watches while the button is down.")]
    [InlineData("guard-no-colour", "SweepPath has a guard with no expected colour.")]
    [InlineData("null-point", "SweepPath point 2 is empty.")]
    [InlineData("point-outside", "SweepPath point 2 at 800,300 is outside the 800x599 client.")]
    [InlineData("off-lattice", "SweepPath point 2 at 475,300 is not a whole number of 50 px steps from the start at 450,300.")]
    [InlineData("repeat", "SweepPath point 3 repeats point 2; every move must go to another block.")]
    [InlineData("open", "SweepPath must end on its start block at 450,300, where the button comes up; it ends at 450,350.")]
    public void Validate_refuses_with_one_sentence(string @case, string sentence)
    {
        var ok = Valid();
        IReadOnlyList<SweepPoint> With(int index, SweepPoint? p)
        {
            var list = ok.Path!.ToList();
            list[index] = p!;
            return list;
        }
        var r = @case switch
        {
            "no-target" => ok with { Target = null },
            "zero-target" => ok with { Target = "0" },
            "foreground-target" => ok with { Target = "foreground" },
            "no-client" => ok with { Client = null },
            "2-points" => Valid(new[] { (450, 300), (450, 250) }),
            "257-points" => Valid(Long(257)),
            "step-7" => ok with { Step = 7 },
            "step-241" => ok with { Step = 241 },
            "dwell-49" => ok with { DwellMs = 49 },
            "dwell-5001" => ok with { DwellMs = 5001 },
            "no-guard" => ok with { Guard = null },
            "guard-no-colour" => ok with { Guard = Dot with { Expect = null } },
            "null-point" => ok with { Path = With(1, null) },
            "point-outside" => ok with { Path = With(1, new SweepPoint(800, 300)) },
            "off-lattice" => ok with { Path = With(1, new SweepPoint(475, 300)) },
            "repeat" => Valid(new[] { (450, 300), (450, 250), (450, 250), (450, 300) }),
            "open" => Valid(Ring1.Take(8)),
            _ => throw new ArgumentOutOfRangeException(nameof(@case)),
        };
        Assert.Equal(sentence, SweepPathMacro.Validate(r));
    }
}
```

- [ ] **Step 2: Run the tests to verify they fail**

Close Ur Task. Run: `dotnet test tests/rororo-ur-task.Tests/rororo-ur-task.Tests.csproj -p:StandaloneTestsOnly=true --filter "FullyQualifiedName~SweepPathMacroTests"`
Expected: build FAILS with `CS0246: The type or namespace name 'SweepPathRequest' could not be found` (and `SweepPathMacro`).

- [ ] **Step 3: Add the request and the method constant**

In `src/Ipc/BridgeContract.cs`, insert after the `ClearAtRequest` record and before `internal static class BridgeContract`:

```csharp
/// <summary>
/// One continuous hold along a path (bridge 1.x, additive; ore-stop sweep spec, "The Ur Task side").
/// Path is in Client pixels, on a Step px lattice anchored at Path[0] (the start block beside the
/// character), and ends on Path[0] again, where the button comes up. DwellMs is the wait at each
/// point. Guard is the pixel that must keep its colour while the button is down; it is required.
/// Answered with a <see cref="RunMacroResponse"/>; GetPlayback, StopMacro and Esc treat it like any
/// RunMacro playback.
/// </summary>
public sealed record SweepPathRequest(
    string ContractVersion,
    string Method,
    string? CallerPluginId,
    string? Target,                    // decimal user id
    ClearAtClient? Client,
    IReadOnlyList<SweepPoint>? Path,
    int Step,
    int DwellMs,
    ClearAtGuard? Guard);
```

In `internal static class BridgeContract`, add below `MethodClearAt`:

```csharp
    public const string MethodSweepPath = "SweepPath";
```

- [ ] **Step 4: Share the target and guard rules in ClearAtMacro**

In `src/Ipc/ClearAtMacro.cs`, `Validate`, replace

```csharp
        if (string.IsNullOrWhiteSpace(r.Target)) return "ClearAt needs a target account.";
        if (!long.TryParse(r.Target, NumberStyles.None, CultureInfo.InvariantCulture, out var userId) || userId <= 0)
            return r.Target.Length <= MaxEchoedTargetLength && r.Target.All(char.IsAsciiDigit)
                ? $"ClearAt target '{r.Target}' is not a decimal user id."
                : "ClearAt needs a decimal user id; got an invalid target.";
```

with

```csharp
        if (TargetProblem(r.Target, "ClearAt") is { } targetProblem) return targetProblem;
```

Add this method after `Validate`:

```csharp
    /// <summary>The target rules every one-account bridge call shares, with <paramref name="who"/>
    /// naming the call in the sentence. A refused target is echoed only when it is all digits and
    /// short; anything else could carry a forged log line.</summary>
    internal static string? TargetProblem(string? target, string who)
    {
        if (string.IsNullOrWhiteSpace(target)) return $"{who} needs a target account.";
        if (!long.TryParse(target, NumberStyles.None, CultureInfo.InvariantCulture, out var userId) || userId <= 0)
            return target.Length <= MaxEchoedTargetLength && target.All(char.IsAsciiDigit)
                ? $"{who} target '{target}' is not a decimal user id."
                : $"{who} needs a decimal user id; got an invalid target.";
        return null;
    }
```

Replace the whole `ValidateGuard` method with:

```csharp
    /// <summary>The guard's own rules: a colour, a box of 1 to <see cref="CheckBox.MaxSide"/> px a side
    /// inside the client, and a tolerance of 1 to <see cref="MaxGuardTolerance"/>. Shared with
    /// SweepPath; <paramref name="who"/> names the call in the sentence.</summary>
    internal static string? ValidateGuard(ClearAtGuard g, ClearAtClient client, string who = "ClearAt")
    {
        if (g.Expect is not { } e) return $"{who} has a guard with no expected colour.";
        if (e.R is < 0 or > 255 || e.G is < 0 or > 255 || e.B is < 0 or > 255) return $"{who} has a guard colour outside 0 to 255.";
        if (g.W is < 1 or > CheckBox.MaxSide || g.H is < 1 or > CheckBox.MaxSide)
            return Inv($"{who} has a guard box outside 1 to {CheckBox.MaxSide} px a side.");
        if (g.Tolerance is < 1 or > MaxGuardTolerance) return Inv($"{who} has a guard tolerance outside 1 to {MaxGuardTolerance}.");
        if (!PointMath.InsideClient((g.X, g.Y, g.W, g.H), (client.W, client.H)))
            return Inv($"{who} has a guard at {g.X},{g.Y} outside the {client.W}x{client.H} client.");
        return null;
    }
```

- [ ] **Step 5: Create the builder**

Create `src/Ipc/SweepPathMacro.cs`:

```csharp
// src/Ipc/SweepPathMacro.cs
using Labs626.UrTask.Macros;
using Labs626.UrTask.Macros.Steps;

namespace Labs626.UrTask.Ipc;

/// <summary>
/// The SweepPath bridge call as a macro (ore-stop sweep spec, "The Ur Task side"): one
/// <see cref="SweepStep"/>, played through the same path as a saved step macro and never saved.
/// Pure, so the refusals and the step are tested without a pipe or a window.
/// </summary>
internal static class SweepPathMacro
{
    /// <summary>Four rings around the character are 80 points; the near-side rows and the detours
    /// round the centre add some. Ur OCR caps its path at the same number.</summary>
    public const int MaxPoints = 256;

    /// <summary>The block size range: a block is about 22 px on the open surface and 170 px down a
    /// one-block shaft; 240 is the largest outline box Ur Task takes.</summary>
    public const int MinStep = 8;
    public const int MaxStep = OutlineCheck.MaxSide;

    /// <summary>The main breaks a bottom-layer block in about 0.3 s; the default dwell is 400 ms.</summary>
    public const int MinDwellMs = 50;
    public const int MaxDwellMs = 5000;

    /// <summary>The synthetic macro's id is this plus the playback id, so it can never match a saved
    /// macro, a saved point adjustment, or ClearAt's shared-baseline prefix.</summary>
    public const string IdPrefix = "sweep-";

    /// <summary>Null when the call can play; otherwise one sentence naming the first problem, before
    /// the ack. Every hop must be a whole number of steps (the lattice anchored at the start) and go
    /// somewhere, and the path must end on its start block, where the button comes up.</summary>
    public static string? Validate(SweepPathRequest r)
    {
        if (ClearAtMacro.TargetProblem(r.Target, "SweepPath") is { } targetProblem) return targetProblem;
        if (r.Client is not { W: >= 1, H: >= 1 } client) return "SweepPath needs the client size the path was measured in.";
        var count = r.Path?.Count ?? 0;
        if (count is < 3 or > MaxPoints) return Inv($"SweepPath takes 3 to {MaxPoints} points; got {count}.");
        if (r.Step is < MinStep or > MaxStep) return Inv($"SweepPath needs a step of {MinStep} to {MaxStep} px; got {r.Step}.");
        if (r.DwellMs is < MinDwellMs or > MaxDwellMs)
            return Inv($"SweepPath needs a dwellMs of {MinDwellMs} to {MaxDwellMs}; got {r.DwellMs}.");
        if (r.Guard is not { } g) return "SweepPath needs a guard: the pixel it watches while the button is down.";
        if (ClearAtMacro.ValidateGuard(g, client, "SweepPath") is { } guardProblem) return guardProblem;

        var path = r.Path!;
        var start = path[0];
        for (int i = 0; i < count; i++)
        {
            var p = path[i];
            if (p is null) return Inv($"SweepPath point {i + 1} is empty.");
            if (p.X < 0 || p.Y < 0 || p.X >= client.W || p.Y >= client.H)
                return Inv($"SweepPath point {i + 1} at {p.X},{p.Y} is outside the {client.W}x{client.H} client.");
            if ((p.X - start.X) % r.Step != 0 || (p.Y - start.Y) % r.Step != 0)
                return Inv($"SweepPath point {i + 1} at {p.X},{p.Y} is not a whole number of {r.Step} px steps from the start at {start.X},{start.Y}.");
            if (i > 0 && path[i - 1] is { } prev && prev.X == p.X && prev.Y == p.Y)
                return Inv($"SweepPath point {i + 1} repeats point {i}; every move must go to another block.");
        }
        var last = path[^1];
        if (last.X != start.X || last.Y != start.Y)
            return Inv($"SweepPath must end on its start block at {start.X},{start.Y}, where the button comes up; it ends at {last.X},{last.Y}.");
        return null;
    }

    /// <summary>The in-memory macro. Call only after <see cref="Validate"/> returned null.</summary>
    public static Macro Build(SweepPathRequest r, string macroId)
    {
        var g = r.Guard!;
        return new Macro(
            SchemaVersion: Macro.CurrentSchemaVersion, Id: macroId, Name: NameFor(r.Path!.Count),
            RecordMode: "PerWindow", RecordedAgainstUserId: null, RecordedAgainstDisplayName: null,
            InterAltDelayMs: null, RecordedAtUnixMs: 0, Events: Array.Empty<MacroEvent>(),
            CoordSpace: Macro.CoordSpaceClient, RecordedClientW: r.Client!.W, RecordedClientH: r.Client.H,
            AllGames: true, Steps: new MacroStep[] { new SweepStep(0, r.Path.ToList(), r.DwellMs) })
        {
            Guard = new ScreenGuard(g.X, g.Y, g.W, g.H, g.Expect!, g.Tolerance),
        };
    }

    public static string NameFor(int points) => Inv($"SweepPath ({points} points)");

    /// <summary>The ur-task.log line when a SweepPath is accepted. The end line is the usual bridge line.</summary>
    public static string StartLine(string playbackId, Macro macro, string account, SweepPathRequest r)
        => Inv($"bridge playback {playbackId} '{macro.Name}' on {account}: from {r.Path![0].X},{r.Path[0].Y}, {r.Step} px steps, {r.DwellMs} ms a point");

    /// <summary>The guard it watches. Call only after <see cref="Validate"/> returned null.</summary>
    public static string GuardLine(SweepPathRequest r)
    {
        var g = r.Guard!;
        return Inv($"SweepPath guard at ({g.X},{g.Y}), expecting {g.Expect!.Hex} ±{g.Tolerance}");
    }

    private static string Inv(FormattableString s) => FormattableString.Invariant(s);
}
```

- [ ] **Step 6: Run the tests to verify they pass**

Run: `dotnet test tests/rororo-ur-task.Tests/rororo-ur-task.Tests.csproj -p:StandaloneTestsOnly=true --filter "FullyQualifiedName~SweepPathMacroTests|FullyQualifiedName~ClearAt"`
Expected: PASS, 23 new tests (6 facts and 17 theory cases) and every existing ClearAt test (the target and guard sentences are unchanged for ClearAt).

- [ ] **Step 7: Run the full fast suite**

Run: `dotnet test tests/rororo-ur-task.Tests/rororo-ur-task.Tests.csproj -p:StandaloneTestsOnly=true`
Expected: **B + 32 passed**, 0 failed.

- [ ] **Step 8: Commit**

```bash
git add src/Ipc/BridgeContract.cs src/Ipc/ClearAtMacro.cs src/Ipc/SweepPathMacro.cs tests/rororo-ur-task.Tests/Ipc/SweepPathMacroTests.cs
git commit -m "feat(ipc): SweepPath request and its one-step sweep macro

Co-Authored-By: Claude Opus 5.5 <noreply@anthropic.com>"
```

---

### Task 3: MacroRunInvoker plays a SweepPath as one playback

**Files:**
- Modify: `src/Ipc/IMacroRunInvoker.cs`
- Modify: `src/Ipc/MacroRunInvoker.cs` (a new `SweepPathAsync` after `ClearAtAsync`)
- Modify: `tests/rororo-ur-task.Tests/Ipc/MacroRunInvokerTests.cs` (append)
- Modify: `tests/rororo-ur-task.Tests/Ipc/MacroRunnerServerTests.cs` (`FakeInvoker`)
- Modify: `tests/rororo-ur-task.Tests/MacroRunnerServerPipeBusyTests.cs` (`NoopInvoker`)

**Interfaces:**
- Consumes (Task 2): `SweepPathRequest`, `SweepPathMacro.Validate`, `.Build`, `.IdPrefix`, `.StartLine`, `.GuardLine`; (Task 1) `SweepStep`, `SweepPoint`.
- Consumes (existing, in the tests): `ClearAtInvoker`, `ClearAt`, `Status`, `NewMacro`, `WaitUntilAsync`, `Alt`.
- Produces: `Task<RunMacroResponse> IMacroRunInvoker.SweepPathAsync(SweepPathRequest request, CancellationToken ct)`; `FakeInvoker.SeenSweep` (`SweepPathRequest?`) for Task 4.

- [ ] **Step 1: Write the failing invoker tests**

In `tests/rororo-ur-task.Tests/Ipc/MacroRunInvokerTests.cs`, append inside the class, before its final closing brace:

```csharp
    // ---------- SweepPath ----------

    private static SweepPathRequest SweepAt(string target = "123", int step = 50) => new(
        "1.0", "SweepPath", "626labs.ur-ocr", target, new ClearAtClient(800, 599),
        new[] { new SweepPoint(450, 300), new SweepPoint(450, 250), new SweepPoint(400, 250), new SweepPoint(450, 300) },
        step, 400, new ClearAtGuard(55, 289, 3, 3, new Rgb(255, 19, 90), 30));

    [Fact]
    public async Task SweepPath_refuses_a_malformed_call_before_the_busy_check()
    {
        var inv = ClearAtInvoker((_, _, _, _) => Task.FromResult<SequenceResult?>(null), busy: true);
        var r = await inv.SweepPathAsync(SweepAt(step: 7), default);
        Assert.Equal((false, "refused", "SweepPath needs a step of 8 to 240 px; got 7."), (r.Ok, r.Reason, r.Detail));
    }

    [Fact]
    public async Task SweepPath_refuses_while_busy()
    {
        var inv = ClearAtInvoker((_, _, _, _) => Task.FromResult<SequenceResult?>(null), busy: true);
        var r = await inv.SweepPathAsync(SweepAt(), default);
        Assert.Equal((false, "busy"), (r.Ok, r.Reason));
        Assert.Equal(0, inv.ActivePlaybackCount);
    }

    [Fact]
    public async Task SweepPath_refuses_an_account_that_is_not_running()
    {
        var inv = ClearAtInvoker((_, _, _, _) => Task.FromResult<SequenceResult?>(null));
        var r = await inv.SweepPathAsync(SweepAt(target: "999"), default);
        Assert.Equal((false, "no-targets-resolved", "Account 999 is not running."), (r.Ok, r.Reason, r.Detail));
    }

    [Fact]
    public async Task SweepPath_plays_one_unsaved_sweep_on_the_target()
    {
        Macro? played = null;
        IReadOnlyList<AccountRegistry.AccountInfo>? on = null;
        var inv = ClearAtInvoker(
            (m, t, _, _) => { played = m; on = t; return Task.FromResult<SequenceResult?>(null); },
            saved: new[] { NewMacro("saved", "Farm") });

        var r = await inv.SweepPathAsync(SweepAt(target: "456"), default);
        await WaitUntilAsync(() => inv.ActivePlaybackCount == 0);

        Assert.True(r.Ok);
        Assert.Equal(new long[] { 456 }, on!.Select(a => a.RobloxUserId));
        Assert.Equal(("SweepPath (4 points)", SweepPathMacro.IdPrefix + r.PlaybackId), (played!.Name, played.Id));
        Assert.Equal(400, Assert.IsType<SweepStep>(Assert.Single(played.Steps!)).DwellMs);
        Assert.NotNull(played.Guard);
        Assert.Equal(new[] { "saved" }, inv.ListMacros().Select(m => m.Id)); // never saved, never listed
        Assert.Equal("finished", Status(inv, r.PlaybackId).State);
    }

    [Fact]
    public async Task SweepPath_is_stopped_by_StopMacro_like_any_playback()
    {
        var inv = ClearAtInvoker(async (_, _, _, ct) => { await Task.Delay(Timeout.Infinite, ct); return null; });

        var r = await inv.SweepPathAsync(SweepAt(), default);
        Assert.Equal("running", Status(inv, r.PlaybackId).State);
        inv.StopMacro(new StopMacroRequest("1.0", "StopMacro", r.PlaybackId, null, "626labs.ur-ocr"));
        await WaitUntilAsync(() => inv.ActivePlaybackCount == 0);

        Assert.Equal("stopped", Status(inv, r.PlaybackId).State);
    }

    [Fact]
    public async Task SweepPath_and_ClearAt_share_the_single_flight_rule()
    {
        var gate = new TaskCompletionSource();
        var inv = ClearAtInvoker(async (_, _, _, _) => { await gate.Task; return null; });

        var sweep = await inv.SweepPathAsync(SweepAt(), default);
        var clear = await inv.ClearAtAsync(ClearAt(), default);
        var again = await inv.SweepPathAsync(SweepAt(), default);

        Assert.True(sweep.Ok);
        Assert.Equal(("busy", "busy"), (clear.Reason, again.Reason));
        Assert.Equal(1, inv.ActivePlaybackCount);
        gate.TrySetResult();
        await WaitUntilAsync(() => inv.ActivePlaybackCount == 0);
    }

    [Fact]
    public async Task SweepPath_logs_its_start_its_guard_and_how_it_ended()
    {
        var lines = new List<string>();
        var inv = ClearAtInvoker((_, _, _, _) => Task.FromResult<SequenceResult?>(null), log: lines);

        var r = await inv.SweepPathAsync(SweepAt(), default);
        await WaitUntilAsync(() => inv.ActivePlaybackCount == 0);

        Assert.Equal(new[]
        {
            $"bridge playback {r.PlaybackId} 'SweepPath (4 points)' on alt-123: from 450,300, 50 px steps, 400 ms a point",
            "SweepPath guard at (55,289), expecting #FF135A ±30",
            $"bridge playback {r.PlaybackId} 'SweepPath (4 points)': finished",
        }, lines);
    }
```

- [ ] **Step 2: Run the tests to verify they fail**

Close Ur Task. Run: `dotnet test tests/rororo-ur-task.Tests/rororo-ur-task.Tests.csproj -p:StandaloneTestsOnly=true --filter "FullyQualifiedName~MacroRunInvokerTests"`
Expected: build FAILS with `CS1061: 'MacroRunInvoker' does not contain a definition for 'SweepPathAsync'`.

- [ ] **Step 3: Add the interface method**

In `src/Ipc/IMacroRunInvoker.cs`, add below `ClearAtAsync`:

```csharp
    /// <summary>One continuous hold along a path on one account, as ONE playback (bridge 1.x,
    /// additive). Same single-flight rule, playback id, StopMacro and GetPlayback as
    /// <see cref="RunAsync"/>; the macro is built in memory and never saved.</summary>
    Task<RunMacroResponse> SweepPathAsync(SweepPathRequest request, CancellationToken ct);
```

- [ ] **Step 4: Implement it in MacroRunInvoker**

In `src/Ipc/MacroRunInvoker.cs`, add after `ClearAtAsync`:

```csharp
    /// <summary>
    /// The SweepPath bridge call (ore-stop sweep spec): the path becomes an in-memory macro of one
    /// sweep step, played through <see cref="Start"/> exactly like a saved macro, so the single-flight
    /// rule, GetPlayback, StopMacro and Esc need nothing new. Never written to the store, so ListMacros
    /// never shows it. Order: malformed → busy → account not running → play.
    /// </summary>
    public Task<RunMacroResponse> SweepPathAsync(SweepPathRequest request, CancellationToken ct)
    {
        // Shape first: a malformed call is refused as malformed even while something is playing.
        if (SweepPathMacro.Validate(request) is { } problem)
            return Task.FromResult(RunMacroResponse.Refused("refused", problem));
        if (_isBusy() || !_playbacks.IsEmpty)
            return Task.FromResult(RunMacroResponse.Refused("busy", "A sequence is already running."));

        var targets = ResolveTargets(new[] { request.Target! });
        if (targets.Count == 0)
            return Task.FromResult(RunMacroResponse.Refused("no-targets-resolved", $"Account {request.Target} is not running."));

        var playbackId = Guid.NewGuid().ToString("N");
        var macro = SweepPathMacro.Build(request, SweepPathMacro.IdPrefix + playbackId);
        _log(SweepPathMacro.StartLine(playbackId, macro, targets[0].DisplayName, request));
        _log(SweepPathMacro.GuardLine(request));
        return Task.FromResult(Start(playbackId, macro, targets, interAltDelayMs: null, repeat: false, ct));
    }
```

- [ ] **Step 5: Give the two test fakes the new method**

In `tests/rororo-ur-task.Tests/Ipc/MacroRunnerServerTests.cs`, inside `FakeInvoker`, add below `ClearAtAsync`:

```csharp
        public SweepPathRequest? SeenSweep { get; private set; }

        public Task<RunMacroResponse> SweepPathAsync(SweepPathRequest request, CancellationToken ct)
        {
            SeenSweep = request;
            return Task.FromResult(Next);
        }
```

In `tests/rororo-ur-task.Tests/MacroRunnerServerPipeBusyTests.cs`, inside `NoopInvoker`, add below `ClearAtAsync`:

```csharp
        public Task<RunMacroResponse> SweepPathAsync(SweepPathRequest request, CancellationToken ct)
            => throw new InvalidOperationException("no connections expected in this test");
```

- [ ] **Step 6: Run the tests to verify they pass**

Run: `dotnet test tests/rororo-ur-task.Tests/rororo-ur-task.Tests.csproj -p:StandaloneTestsOnly=true --filter "FullyQualifiedName~MacroRunInvokerTests"`
Expected: PASS, the 7 new `SweepPath_*` tests and every existing invoker test.

- [ ] **Step 7: Run the full fast suite**

Run: `dotnet test tests/rororo-ur-task.Tests/rororo-ur-task.Tests.csproj -p:StandaloneTestsOnly=true`
Expected: **B + 39 passed**, 0 failed.

- [ ] **Step 8: Commit**

```bash
git add src/Ipc/IMacroRunInvoker.cs src/Ipc/MacroRunInvoker.cs tests/rororo-ur-task.Tests/Ipc/MacroRunInvokerTests.cs tests/rororo-ur-task.Tests/Ipc/MacroRunnerServerTests.cs tests/rororo-ur-task.Tests/MacroRunnerServerPipeBusyTests.cs
git commit -m "feat(ipc): play a SweepPath as one bridge playback

Co-Authored-By: Claude Opus 5.5 <noreply@anthropic.com>"
```

---

### Task 4: The server dispatches SweepPath; 0.12.0 and the changelog

**Files:**
- Modify: `src/Ipc/MacroRunnerServer.cs` (doc comment and `DispatchAsync`)
- Modify: `tests/rororo-ur-task.Tests/Ipc/MacroRunnerServerTests.cs` (append)
- Modify: `CHANGELOG.md`, `rororo-ur-task.csproj`, `manifest.json`

**Interfaces:**
- Consumes: `BridgeContract.MethodSweepPath`, `SweepPathRequest` (Task 2); `IMacroRunInvoker.SweepPathAsync`, `FakeInvoker.SeenSweep` (Task 3); `MacroRunInvoker`'s main (`playWithResult`) constructor; `RoundTripJsonAsync`.
- Produces: `SweepPath` on the wire at `\\.\pipe\626labs-ur-task`; version 0.12.0.

- [ ] **Step 1: Write the failing server tests**

In `tests/rororo-ur-task.Tests/Ipc/MacroRunnerServerTests.cs`, append inside the class, before its final closing brace:

```csharp
    // ---------- SweepPath ----------

    private const string SweepJson =
        "{\"contractVersion\":\"1.0\",\"method\":\"SweepPath\",\"callerPluginId\":\"626labs.ur-ocr\",\"target\":\"123\"," +
        "\"client\":{\"w\":800,\"h\":599},\"path\":[{\"x\":450,\"y\":300},{\"x\":450,\"y\":250},{\"x\":400,\"y\":250},{\"x\":450,\"y\":300}]," +
        "\"step\":50,\"dwellMs\":400,\"guard\":{\"x\":55,\"y\":289,\"w\":3,\"h\":3,\"expect\":{\"r\":255,\"g\":19,\"b\":90},\"tolerance\":30}}";

    [Fact]
    public async Task SweepPath_Dispatches_AndReturnsAck()
    {
        var invoker = new FakeInvoker { Next = RunMacroResponse.Accepted("01SWP") };

        var respJson = await RoundTripJsonAsync(new MacroRunnerServer(invoker), SweepJson);

        Assert.Equal("{\"ok\":true,\"playbackId\":\"01SWP\",\"queued\":false}", respJson);
        Assert.Equal(("123", 4, 50, 400), (invoker.SeenSweep!.Target, invoker.SeenSweep.Path!.Count, invoker.SeenSweep.Step, invoker.SeenSweep.DwellMs));
        Assert.Null(invoker.Seen);          // not routed as a RunMacro
        Assert.Null(invoker.SeenClearAt);   // nor as a ClearAt
    }

    [Fact]
    public async Task SweepPath_MissingCallerPluginId_RefusedWithoutDispatch()
    {
        var invoker = new FakeInvoker();

        var respJson = await RoundTripJsonAsync(new MacroRunnerServer(invoker),
            SweepJson.Replace("\"callerPluginId\":\"626labs.ur-ocr\",", ""));
        var resp = JsonSerializer.Deserialize<RunMacroResponse>(respJson, BridgeContract.Json)!;

        Assert.Equal((false, "refused", "Missing callerPluginId."), (resp.Ok, resp.Reason, resp.Detail));
        Assert.Null(invoker.SeenSweep);
    }

    [Fact]
    public async Task SweepPath_UnsupportedVersion_RefusedVersionMismatch()
    {
        var invoker = new FakeInvoker();

        var respJson = await RoundTripJsonAsync(new MacroRunnerServer(invoker), SweepJson.Replace("\"1.0\"", "\"2.0\""));
        var resp = JsonSerializer.Deserialize<RunMacroResponse>(respJson, BridgeContract.Json)!;

        Assert.Equal((false, "version-mismatch"), (resp.Ok, resp.Reason));
        Assert.Null(invoker.SeenSweep);
    }

    // End to end with the real invoker: a path that does not close is refused on the wire with the
    // sentence Ur OCR logs, and nothing starts.
    [Fact]
    public async Task SweepPath_over_the_pipe_refuses_a_path_that_does_not_end_on_its_start()
    {
        var alt = new AccountRegistry.AccountInfo(1123, 123, "alt-123", "acct-123");
        var invoker = new MacroRunInvoker(
            loadMacros: Array.Empty<Macro>,
            snapshot: () => new[] { alt },
            resolveForegroundUserId: () => alt.RobloxUserId,
            isBusy: () => false,
            playWithResult: (_, _, _, _) => Task.FromResult<SequenceResult?>(null));

        var respJson = await RoundTripJsonAsync(new MacroRunnerServer(invoker),
            SweepJson.Replace("{\"x\":450,\"y\":300}],", "{\"x\":450,\"y\":350}],"));
        var resp = JsonSerializer.Deserialize<RunMacroResponse>(respJson, BridgeContract.Json)!;

        Assert.Equal((false, "refused", "SweepPath must end on its start block at 450,300, where the button comes up; it ends at 450,350."),
            (resp.Ok, resp.Reason, resp.Detail));
        Assert.Equal(0, invoker.ActivePlaybackCount);
    }
```

(If `AccountRegistry.AccountInfo`'s constructor has changed since the ClearAt server test was written, copy the `alt` line from `ClearAt_over_the_pipe_refuses_a_point_outside_the_client` in the same file.)

- [ ] **Step 2: Run the tests to verify they fail**

Close Ur Task. Run: `dotnet test tests/rororo-ur-task.Tests/rororo-ur-task.Tests.csproj -p:StandaloneTestsOnly=true --filter "FullyQualifiedName~MacroRunnerServerTests.SweepPath"`
Expected: FAIL. `SweepPath_Dispatches_AndReturnsAck` and `SweepPath_over_the_pipe_...` get `Unknown method 'SweepPath'.`; `SweepPath_MissingCallerPluginId_...` fails on the detail. `SweepPath_UnsupportedVersion_RefusedVersionMismatch` already passes (the version check runs before dispatch), which is correct.

- [ ] **Step 3: Dispatch the method**

In `src/Ipc/MacroRunnerServer.cs`, `DispatchAsync`, add after the `MethodClearAt` case and before `default:`:

```csharp
            case BridgeContract.MethodSweepPath:
            {
                var req = JsonSerializer.Deserialize<SweepPathRequest>(frame, BridgeContract.Json);
                if (req is null)
                    return Bytes(RunMacroResponse.Refused("refused", "Empty request."));
                if (string.IsNullOrWhiteSpace(req.CallerPluginId))
                    return Bytes(RunMacroResponse.Refused("refused", "Missing callerPluginId."));
                return Bytes(await _invoker.SweepPathAsync(req, ct).ConfigureAwait(false));
            }
```

In the `HandleConnectionAsync` doc comment, replace `(ListMacros/StopMacro/GetPlayback/ClearAt beside RunMacro)` with `(ListMacros/StopMacro/GetPlayback/ClearAt/SweepPath beside RunMacro)`.

- [ ] **Step 4: Run the server tests to verify they pass**

Run: `dotnet test tests/rororo-ur-task.Tests/rororo-ur-task.Tests.csproj -p:StandaloneTestsOnly=true --filter "FullyQualifiedName~MacroRunnerServerTests"`
Expected: PASS, including the 4 new tests and `UnknownMethod_RefusedWithThePinnedDetail`.

- [ ] **Step 5: Bump to 0.12.0 and add the changelog entry**

A new bridge method is a feature: minor bump. In `rororo-ur-task.csproj`, replace `<Version>0.11.0</Version>` with `<Version>0.12.0</Version>`. In `manifest.json`, replace `"version": "0.11.0",` with `"version": "0.12.0",`. Both, in this step (`VersionConsistencyTests` fails when they drift).

In `CHANGELOG.md`, insert above `## 0.11.0 — unreleased`:

```markdown
## 0.12.0 — unreleased

### Added

- **Ur OCR can sweep the stone around you in one hold.** Clearing stone block by block paid a
  look before and after every block. `SweepPath`, a new bridge call, presses the left button on a
  block beside your character, moves the pointer one block at a time along a path Ur OCR sends
  (a real mouse move each time, waiting `dwellMs` on every block), and lets go only back on the
  block it started on, so the release can't click a player or a chest. While the button is down
  it checks the Auto Mine dot every 3 blocks: if a menu or a profile covers it, the pointer goes
  back to the start block, lets go there, and the playback stops as `check-failed`. Losing focus
  lets go at once, as every hold does; Esc and `StopMacro` go back to the start block first. It
  rides the same single-flight rule, playback id, `GetPlayback`, `StopMacro` and Esc as
  `RunMacro`, is never saved or listed, and logs "SweepPath (N points)" and "swept N points in
  S s". A path must start and end on the same block, move a whole number of `step` px at a time,
  stay inside the client, carry a guard and hold 3 to 256 points; anything else is refused with
  a sentence. Additive on bridge contract 1.0.
```

- [ ] **Step 6: Build and run the full fast suite**

Run: `dotnet build rororo-ur-task.csproj`
Expected: Build succeeded, 0 errors.

Run: `dotnet test tests/rororo-ur-task.Tests/rororo-ur-task.Tests.csproj -p:StandaloneTestsOnly=true`
Expected: **B + 43 passed**, 0 failed (`VersionConsistencyTests` among them).

- [ ] **Step 7: Commit**

```bash
git add src/Ipc/MacroRunnerServer.cs tests/rororo-ur-task.Tests/Ipc/MacroRunnerServerTests.cs CHANGELOG.md rororo-ur-task.csproj manifest.json
git commit -m "feat(ipc): dispatch SweepPath on the bridge; Ur Task 0.12.0

Co-Authored-By: Claude Opus 5.5 <noreply@anthropic.com>"
```

---

### Task 5: Live verification on Dunder-MiffLan (controller only)

**Run by the controller on the live rig, never by a subagent.** Este plays; the controller sends calls, watches `ur-task.log` and reads frames. No code changes; nothing is committed unless a finding needs a follow-up task.

**Interfaces:**
- Consumes: Tasks 0 to 4 on `feat/ore-stop-pulse`; the main standing at the target layer; the guard box and colour from the Mine #8 finder in the measured file (`%LOCALAPPDATA%\626Labs\ore-stop-sweep\current.txt` names the folder); the block size Ur OCR last logged (`block size N px` in `%LOCALAPPDATA%\626Labs\rororo-ur-ocr\` logs, or read off a calm frame).
- Produces: a pass/fail line per check below, reported to Este and to the Ur OCR plan's live task.

- [ ] **Before Este plays**
  - [ ] Read the rig's live display scale first (Settings, Display) and say it out loud. The measured client is 800x599 at 100%; anything else, stop and ask.
  - [ ] Close Ur Task, `dotnet build rororo-ur-task.csproj`, start Ur Task the way the earlier live passes on this branch did. `ur-task.log` shows the new version (0.12.0) at startup.
- [ ] **Send a sweep by hand.** Este: the main at the target layer, Auto Mine off, camera top-down, Hide My Pets on, no popup. With the main in front, in `pwsh`, fill `$target` (the main's user id, never written to a file), `$p` (the block size), the centre (`$cx`, `$cy`, about 400,310) and the guard, then send ring 1 from the east block:

```powershell
$target = '<main user id>'; $p = 160; $cx = 400; $cy = 310
$cells = @(@(1,0),@(1,-1),@(0,-1),@(-1,-1),@(-1,0),@(-1,1),@(0,1),@(1,1),@(1,0))
$path = $cells | ForEach-Object { @{ x = $cx + $_[0] * $p; y = $cy + $_[1] * $p } }
$req = @{ contractVersion = '1.0'; method = 'SweepPath'; callerPluginId = '626labs.controller'; target = $target
          client = @{ w = 800; h = 599 }; path = $path; step = $p; dwellMs = 400
          guard = @{ x = 55; y = 289; w = 3; h = 3; expect = @{ r = 255; g = 19; b = 90 }; tolerance = 30 } } |
       ConvertTo-Json -Depth 5 -Compress
$pipe = [System.IO.Pipes.NamedPipeClientStream]::new('.', '626labs-ur-task', [System.IO.Pipes.PipeDirection]::InOut)
$pipe.Connect(2000)
$body = [Text.Encoding]::UTF8.GetBytes($req)
$len = [BitConverter]::GetBytes([int]$body.Length); [Array]::Reverse($len)   # 4-byte big-endian length prefix
$pipe.Write($len, 0, 4); $pipe.Write($body, 0, $body.Length); $pipe.Flush()
$hdr = [byte[]]::new(4); [void]$pipe.Read($hdr, 0, 4); [Array]::Reverse($hdr)
$resp = [byte[]]::new([BitConverter]::ToInt32($hdr, 0)); [void]$pipe.Read($resp, 0, $resp.Length)
[Text.Encoding]::UTF8.GetString($resp); $pipe.Dispose()
```

  Use the guard values from the measured finder, not the example ones. Drop any cell whose point falls in the HUD (y above 470 at 100%) or outside 12..787 x 12..586 before sending; the path must still start and end on `@(1,0)`.
  - [ ] The reply is `{"ok":true,"playbackId":"…","queued":false}`.
  - [ ] Este sees the button go down on the block right of the character, the pointer walk the ring, and come up back on that block. No popup opens.
  - [ ] `ur-task.log`: `bridge playback … 'SweepPath (N points)' on …: from X,Y, P px steps, 400 ms a point`, the guard line, `…: swept N points in S s` (S near (N - 1) x 0.4 plus the jump), then `…: finished`.
  - [ ] The block under the character was never pressed (the character did not drop).
- [ ] **Guard stop.** Send the same call with `dwellMs = 1500`. Mid-sweep, Este opens a menu from the keyboard. Expected: the pointer goes back to the start block and lets go there; the log reads `swept K of N points …, then the playback ended; released on the start block`, then `failed (check-failed). SweepPath stopped: the guard at (…) isn't the expected colour …`. Nothing inside the menu is clicked.
- [ ] **Esc.** Send it again with `dwellMs = 1500`; Este presses Esc mid-sweep. Expected: released on the start block, playback `stopped`.
- [ ] **Focus loss.** Send it again; Este alt-tabs mid-sweep. Expected: the button comes up at once (the log line has no "released on the start block"), playback `failed (aborted)`, and nothing in the other window is dragged.
- [ ] **A refusal.** Send a path whose second point is off the lattice (`x = $cx + $p + 7`). Expected: `{"ok":false,…,"reason":"refused","detail":"SweepPath point 2 at … is not a whole number of … px steps from the start at …."}` and no input.
- [ ] **Report** each check pass or fail to Este and hand the live numbers (block size used, S per sweep, anything a release touched) to the Ur OCR plan's live task.

---

## Spec notes for the reviewer

- **The four deviations** (a `step` field; the lattice reading of "one step apart"; a required guard; a closed path) are in Global Constraints. Each keeps the spec's intent and makes its rule checkable before the ack.
- **Focus delay.** `SequencePlayer` focuses the account and waits its default 500 ms before playing, as for any ClearAt. The spec names no value.
- **Why a step kind and not a hold per point.** A hold per point would release between points; the spec's whole point is one continuous hold with no look. The step is never saved, so it gets no JSON discriminator; the store and bundles never see a synthetic macro.
- **Esc versus focus loss.** The spec says focus loss releases "as every hold does today" (in place), and says nothing about Esc. This plan sends Esc, StopMacro and a failed capture back to the start block when the target is still in front, because a release is a click and the start block is the one safe place for it.
