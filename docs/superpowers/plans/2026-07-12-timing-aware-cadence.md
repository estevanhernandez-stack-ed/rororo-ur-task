# Timing-Aware Cadence Scheduler Implementation Plan

> **For agentic workers:** REQUIRED SUB-SKILL: Use superpowers:subagent-driven-development (recommended) or superpowers:executing-plans to implement this plan task-by-task. Steps use checkbox (`- [ ]`) syntax for tracking.

**Goal:** Replace `AssignmentRunner`'s spin loop — which services a keep-alive alt every ~1.25s and steals foreground every time — with a deadline scheduler that services an alt only when its idle deadline actually approaches, and sleeps when nothing is due.

**Architecture:** The scheduling decision becomes a **pure function** `Decide(alts, nowMs, nextActivePassCostMs) -> CadenceDecision` (ServiceKeepAlive | RunActive | SleepUntil), tested with a fake monotonic clock. `AssignmentRunner` becomes a thin shell around it, **preserving its public surface** (`RunAsync` / `Progress` / `Abort` / single-flight) so `PluginRuntime` and `RecipeRunner` don't churn. Keep-alive services capture and restore the user's foreground window.

**Tech Stack:** .NET 10, WPF, MVVM, xUnit. Build: `dotnet build rororo-ur-task.csproj -c Debug` (csproj is at the **repo root**, not `src/`). Tests: `dotnet test tests/rororo-ur-task.Tests/`.

**Spec:** `docs/superpowers/specs/2026-07-12-timing-aware-cadence-design.md` (commit `bb0a5be`)

## Global Constraints

- **Fire intervals, not thresholds.** `KeepAliveIntervals.For()` returns **when we fire**. Headroom under the 20-minute platform idle floor is **already baked in**. **Never apply a second safety margin** to its result.
- **Interval values (pinned):** primary keeper **11 min**, backstop **17 min**, unknown **12 min**.
- **Never invent Roblox PlaceIds.** The shipped table matches on normalized **game name** (from the ur-afk research). The `PlaceId` map starts **empty** and is populated only from *observed* presence data or explicit user override.
- **Monotonic clock only** (`Environment.TickCount64`). Never `DateTime.Now` / wall-clock for scheduling — a DST shift or clock adjustment must not strand an alt or stampede the loop.
- **`Assignment.Role` has NO C# default value.** A `= CadenceRole.Active` default would silently make no-macro assignments Active and spin them back-to-back — recreating the exact bug this plan fixes.
- **Public surface of `AssignmentRunner` is frozen:** `RunAsync(IReadOnlyList<Assignment>, CancellationToken)`, `event EventHandler<AssignmentProgress> Progress`, `bool IsRunning`, `bool Abort()`, `static int KeepAliveInputStructSize`. `PluginRuntime` and `RecipeRunner` must compile unchanged except where this plan says otherwise.
- **Warn, don't block.** An unschedulable alt produces a warning; the run still proceeds (proceed-with-successes, consistent with `RecipeRunner`).
- Commit after every task. Branch: `feat/timing-aware-cadence`.

---

## File Structure

| File | Responsibility |
|---|---|
| `src/Macros/AssignmentRunner.cs` (modify) | `Assignment` record + `CadenceRole` + the runner shell. Spin loop replaced by the Decide loop. |
| `src/Macros/CadenceScheduler.cs` (create) | `ScheduledAlt`, `CadenceDecision`, the pure `Decide` function. No Win32, no timers. |
| `src/Macros/KeepAliveIntervals.cs` (create) | Game → fire-interval table + user override lookup. |
| `src/PluginHost/Win32Focus.cs` (modify) | Add `CaptureForeground()` / `RestoreForeground(hwnd)`; share the foreground-lock core. |
| `src/PluginHost/ClaimFile.cs` (create) | Heartbeat claim file write/refresh/delete (atomic). |
| `src/UI/UserPreferences.cs` (modify) | Per-game interval overrides. |
| `src/UI/AssignmentRow.cs` (modify) | `Role` + next-due countdown for the grid. |
| `src/UI/RecorderViewModel.cs` (modify) | Role toggle command, preset commands, countdown ticker, warning toast. |
| `src/UI/RecorderWindow.xaml` (modify) | Row role toggle, "All equal" / "One focused" preset buttons, next-due text. |
| `src/PluginRuntime.cs` (modify) | Build assignments with roles; start/stop the claim file. |
| `src/Macros/RecipeRunner.cs` (modify) | Build its terminal-loop assignments with an explicit role. |

---

### Task 1: CadenceRole + Assignment.WithDerivedRole

Adding a required field to `Assignment` breaks both construction sites, so they are fixed in this task — it must compile and pass as a unit.

**Files:**
- Modify: `src/Macros/AssignmentRunner.cs:224` (the `Assignment` record)
- Modify: `src/PluginRuntime.cs:541` (construction site)
- Modify: `src/Macros/RecipeRunner.cs:111` (construction site)
- Test: `tests/rororo-ur-task.Tests/CadenceRoleTests.cs` (create)

**Interfaces:**
- Produces: `enum CadenceRole { Active, KeepAlive }`; `record Assignment(AccountRegistry.AccountInfo Alt, Macro? Macro, CadenceRole Role)`; `static Assignment Assignment.WithDerivedRole(AccountRegistry.AccountInfo alt, Macro? macro)`.

- [ ] **Step 1: Write the failing test**

Create `tests/rororo-ur-task.Tests/CadenceRoleTests.cs`:

```csharp
using Labs626.UrTask.Macros;
using Labs626.UrTask.PluginHost;

namespace Labs626.UrTask.Tests;

public class CadenceRoleTests
{
    // AccountInfo(int Pid, long RobloxUserId, string DisplayName, string AccountId,
    //             long PlaceId = 0, string PlaceName = "")
    private static AccountRegistry.AccountInfo Alt(int pid = 1, long userId = 100)
        => new(pid, userId, "Alt", $"acct-{pid}");

    private static Macro NewMacro() => new(
        SchemaVersion: 3, Id: Guid.NewGuid().ToString(), Name: "m",
        RecordMode: "PerWindow", RecordedAgainstUserId: null,
        RecordedAgainstDisplayName: null, InterAltDelayMs: null,
        RecordedAtUnixMs: 0, Events: new List<MacroEvent>());

    [Fact]
    public void WithDerivedRole_MacroPresent_IsActive()
    {
        var a = Assignment.WithDerivedRole(Alt(), NewMacro());
        Assert.Equal(CadenceRole.Active, a.Role);
    }

    /// The load-bearing one: a no-macro assignment means "just keep it alive."
    /// If this ever comes back Active it gets spun back-to-back every ~1.25s,
    /// which is precisely the bug the cadence scheduler exists to kill.
    [Fact]
    public void WithDerivedRole_NoMacro_IsKeepAlive()
    {
        var a = Assignment.WithDerivedRole(Alt(), macro: null);
        Assert.Equal(CadenceRole.KeepAlive, a.Role);
        Assert.Null(a.Macro);
    }

    [Fact]
    public void ExplicitRole_IsHonoured_AndMacroSurvivesBackgrounding()
    {
        // Backgrounding must NOT be destructive — the macro is preserved, paused.
        var macro = NewMacro();
        var a = new Assignment(Alt(), macro, CadenceRole.KeepAlive);
        Assert.Equal(CadenceRole.KeepAlive, a.Role);
        Assert.Same(macro, a.Macro);
    }
}
```

**Note:** the `AccountInfo` constructor arity above is a placeholder shape — open
`src/PluginHost/AccountRegistry.cs`, read the real `AccountInfo` record, and use its
actual positional parameters. Mirror how `AssignmentRunnerTests.cs` already builds one
(it has an `Alt(pid, userId, name)` helper — copy that helper verbatim).

- [ ] **Step 2: Run test to verify it fails**

Run: `dotnet test tests/rororo-ur-task.Tests/ --filter CadenceRoleTests`
Expected: FAIL — compile error, `CadenceRole` does not exist / `Assignment` has no `WithDerivedRole`.

- [ ] **Step 3: Write minimal implementation**

In `src/Macros/AssignmentRunner.cs`, replace the `Assignment` record (line 224):

```csharp
/// <summary>
/// How often an alt gets serviced. Active = run its macro back-to-back (farming).
/// KeepAlive = fire a single Space only when its idle deadline approaches, so the
/// scheduler can sleep instead of stealing foreground every ~1.25s.
/// </summary>
public enum CadenceRole { Active, KeepAlive }

public sealed record Assignment(
    AccountRegistry.AccountInfo Alt,
    Macro? Macro,
    CadenceRole Role)   // NO default value — see WithDerivedRole.
{
    /// <summary>
    /// The legacy/derived rule: a macro means you meant to farm; no macro means you
    /// meant to stay alive. Deliberately a factory rather than a C# default value —
    /// a `= CadenceRole.Active` default would silently make no-macro assignments
    /// Active and spin them back-to-back, recreating the bug the scheduler fixes.
    /// </summary>
    public static Assignment WithDerivedRole(AccountRegistry.AccountInfo alt, Macro? macro)
        => new(alt, macro, macro is null ? CadenceRole.KeepAlive : CadenceRole.Active);
}
```

In `src/PluginRuntime.cs:541`, change:

```csharp
new Assignment(a, _assignments.TryGetValue(a.Pid, out var m) ? m : null)).ToList();
```

to:

```csharp
Assignment.WithDerivedRole(a, _assignments.TryGetValue(a.Pid, out var m) ? m : null)).ToList();
```

In `src/Macros/RecipeRunner.cs:111`, change:

```csharp
var assignments = live.Select(a => new Assignment(a, terminalMacro)).ToList();
```

to:

```csharp
// A recipe's terminal step is an explicit instruction: Loop means farm it (Active),
// KeepAlive means just hold it awake. Derive from the macro the terminal produced.
var assignments = live.Select(a => Assignment.WithDerivedRole(a, terminalMacro)).ToList();
```

- [ ] **Step 4: Run tests to verify they pass**

Run: `dotnet build rororo-ur-task.csproj -c Debug` → 0 errors
Run: `dotnet test tests/rororo-ur-task.Tests/ --filter CadenceRoleTests`
Expected: PASS (3 tests)
Run: `dotnet test tests/rororo-ur-task.Tests/`
Expected: whole suite still green (except the 2 known `HotkeyServiceTests` failures that occur only when a live Ur Task instance holds the global hotkeys — those are environmental).

- [ ] **Step 5: Commit**

```bash
git add src/Macros/AssignmentRunner.cs src/PluginRuntime.cs src/Macros/RecipeRunner.cs tests/rororo-ur-task.Tests/CadenceRoleTests.cs
git commit -m "feat(cadence): add CadenceRole + Assignment.WithDerivedRole (no C# default)"
```

---

### Task 2: KeepAliveIntervals — game → fire-interval table

**Files:**
- Create: `src/Macros/KeepAliveIntervals.cs`
- Modify: `src/UI/UserPreferences.cs`
- Test: `tests/rororo-ur-task.Tests/KeepAliveIntervalsTests.cs` (create)

**Interfaces:**
- Consumes: nothing from Task 1.
- Produces: `static TimeSpan KeepAliveIntervals.For(long? placeId, string? placeName, UserPreferences prefs)`; `const int KeepAliveIntervals.UnknownGameMinutes = 12`; `Dictionary<long,int> UserPreferences.KeepAliveOverridesByPlaceId`.

**Why name-matched:** the ur-afk research table is keyed by game **name**, and no verified Roblox PlaceIds exist on this machine (the macro library carries no game stamps yet). We therefore match on normalized `PlaceName` and keep a `PlaceId` override map that starts **empty**. Do **not** invent PlaceIds.

- [ ] **Step 1: Write the failing test**

Create `tests/rororo-ur-task.Tests/KeepAliveIntervalsTests.cs`:

```csharp
using Labs626.UrTask.Macros;
using Labs626.UrTask.UI;

namespace Labs626.UrTask.Tests;

public class KeepAliveIntervalsTests
{
    private static UserPreferences NoPrefs() => new();

    // Games that ship NO anti-AFK — we are the only thing keeping them alive.
    [Theory]
    [InlineData("Grow a Garden")]
    [InlineData("Adopt Me")]
    [InlineData("Brookhaven RP")]
    [InlineData("Bee Swarm Simulator")]
    [InlineData("Blox Fruits")]
    public void PrimaryKeeperGames_Fire_Every11Minutes(string game)
        => Assert.Equal(TimeSpan.FromMinutes(11), KeepAliveIntervals.For(null, game, NoPrefs()));

    // Games with their OWN anti-AFK (~15 min self-rejoin) — we're only a backstop,
    // so we steal focus less often.
    [Theory]
    [InlineData("Pet Simulator 99")]
    [InlineData("Fisch")]
    [InlineData("Anime Vanguards")]
    [InlineData("Blade Ball")]
    public void BackstopGames_Fire_Every17Minutes(string game)
        => Assert.Equal(TimeSpan.FromMinutes(17), KeepAliveIntervals.For(null, game, NoPrefs()));

    [Fact]
    public void UnknownGame_FallsBackTo12Minutes()
        => Assert.Equal(TimeSpan.FromMinutes(12), KeepAliveIntervals.For(null, "Some Unshipped Game", NoPrefs()));

    /// No game stamp at all (presence hasn't filled identity) must still work —
    /// the feature degrades to the safe default, it does not break.
    [Fact]
    public void NoGameStampAtAll_FallsBackTo12Minutes()
        => Assert.Equal(TimeSpan.FromMinutes(12), KeepAliveIntervals.For(null, null, NoPrefs()));

    [Fact]
    public void NameMatch_IsCaseAndWhitespaceInsensitive()
        => Assert.Equal(TimeSpan.FromMinutes(11), KeepAliveIntervals.For(null, "  grow a garden  ", NoPrefs()));

    [Fact]
    public void UserOverrideByPlaceId_BeatsTheShippedTable()
    {
        var prefs = new UserPreferences();
        prefs.KeepAliveOverridesByPlaceId[999L] = 5;
        // Even though the name says backstop (17), the explicit override wins.
        Assert.Equal(TimeSpan.FromMinutes(5), KeepAliveIntervals.For(999L, "Fisch", prefs));
    }
}
```

- [ ] **Step 2: Run test to verify it fails**

Run: `dotnet test tests/rororo-ur-task.Tests/ --filter KeepAliveIntervalsTests`
Expected: FAIL — `KeepAliveIntervals` does not exist.

- [ ] **Step 3: Write minimal implementation**

Create `src/Macros/KeepAliveIntervals.cs`:

```csharp
using Labs626.UrTask.UI;

namespace Labs626.UrTask.Macros;

/// <summary>
/// How often to fire a keep-alive Space for an alt, by the game it's in.
///
/// NAMED "Intervals", NOT "Thresholds", on purpose: these are FIRE intervals —
/// when we act — with headroom under Roblox's 20-minute idle floor ALREADY baked
/// in. Calling them thresholds invites a caller to helpfully apply a safety margin
/// to a number that already has one. Never multiply these down.
///
/// Sourced from rororo-ur-afk/docs/game-idle-timings.md (2026-07-06):
///  - Roblox disconnects idle players after 20 min. That is a platform FLOOR —
///    games may shorten it, none may extend it. Detection is input-absence; a
///    single Space resets it. Movement does not count.
///  - Games shipping their own anti-AFK (~15 min self-rejoin) only need us as a
///    BACKSTOP -> 17 min. Games with none need us as PRIMARY keeper -> 11 min.
///  - Unknown games assume no help -> 12 min.
///
/// Keyed by game NAME because the research is, and because no verified Roblox
/// PlaceIds are in hand. PlaceId is supported only as an exact user override.
/// DO NOT populate PlaceIds by guessing them.
/// </summary>
internal static class KeepAliveIntervals
{
    public const int UnknownGameMinutes = 12;
    private const int PrimaryKeeperMinutes = 11;
    private const int BackstopMinutes = 17;

    // Games that ship NO anti-AFK — Ur Task is the only thing keeping them alive.
    private static readonly string[] PrimaryKeeperGames =
    [
        "grow a garden", "adopt me", "brookhaven rp", "bee swarm simulator", "blox fruits",
    ];

    // Games with their own anti-AFK teleport/rejoin — we're insurance, not the keeper.
    private static readonly string[] BackstopGames =
    [
        "pet simulator 99", "fisch", "anime vanguards", "blade ball",
    ];

    public static TimeSpan For(long? placeId, string? placeName, UserPreferences prefs)
    {
        // An explicit user override always wins — our table is [community] confidence.
        if (placeId is long id && prefs.KeepAliveOverridesByPlaceId.TryGetValue(id, out var mins))
            return TimeSpan.FromMinutes(mins);

        var key = Normalize(placeName);
        if (key.Length > 0)
        {
            if (Array.Exists(PrimaryKeeperGames, g => g == key)) return TimeSpan.FromMinutes(PrimaryKeeperMinutes);
            if (Array.Exists(BackstopGames, g => g == key)) return TimeSpan.FromMinutes(BackstopMinutes);
        }
        return TimeSpan.FromMinutes(UnknownGameMinutes);
    }

    private static string Normalize(string? name)
        => string.IsNullOrWhiteSpace(name) ? string.Empty : name.Trim().ToLowerInvariant();
}
```

In `src/UI/UserPreferences.cs`, add a property to the `UserPreferences` class (keep the
existing JSON round-trip shape — it is serialized with `System.Text.Json`):

```csharp
    /// <summary>
    /// Per-game keep-alive fire interval overrides, in MINUTES, keyed by Roblox
    /// PlaceId. Beats the shipped table in <see cref="Macros.KeepAliveIntervals"/>.
    /// Empty by default — populated only by the user or from observed presence data,
    /// never by guessing PlaceIds.
    /// </summary>
    public Dictionary<long, int> KeepAliveOverridesByPlaceId { get; set; } = new();
```

- [ ] **Step 4: Run tests to verify they pass**

Run: `dotnet build rororo-ur-task.csproj -c Debug` → 0 errors
Run: `dotnet test tests/rororo-ur-task.Tests/ --filter KeepAliveIntervalsTests`
Expected: PASS (all theory cases + 4 facts)

- [ ] **Step 5: Commit**

```bash
git add src/Macros/KeepAliveIntervals.cs src/UI/UserPreferences.cs tests/rororo-ur-task.Tests/KeepAliveIntervalsTests.cs
git commit -m "feat(cadence): game-aware keep-alive fire intervals + per-game override"
```

---

### Task 3: The pure Decide function (the heart)

**Files:**
- Create: `src/Macros/CadenceScheduler.cs`
- Test: `tests/rororo-ur-task.Tests/CadenceSchedulerTests.cs` (create)

**Interfaces:**
- Consumes: `CadenceRole`, `Assignment` (Task 1).
- Produces:
  - `sealed class ScheduledAlt { Assignment Assignment; long DueAtMs; long IntervalMs; }`
  - `abstract record CadenceDecision` with `ServiceKeepAlive(ScheduledAlt)`, `RunActive(ScheduledAlt)`, `SleepUntil(long WakeAtMs)`
  - `static CadenceDecision CadenceScheduler.Decide(IReadOnlyList<ScheduledAlt> alts, long nowMs, long nextActivePassCostMs)`

No Win32, no timers, no I/O — this is why the hard cases are testable.

- [ ] **Step 1: Write the failing test**

Create `tests/rororo-ur-task.Tests/CadenceSchedulerTests.cs`:

```csharp
using Labs626.UrTask.Macros;
using Labs626.UrTask.PluginHost;

namespace Labs626.UrTask.Tests;

public class CadenceSchedulerTests
{
    private const long Min = 60_000;

    // AccountInfo(int Pid, long RobloxUserId, string DisplayName, string AccountId,
    //             long PlaceId = 0, string PlaceName = "")
    private static AccountRegistry.AccountInfo Alt(int pid, long userId) => new(pid, userId, $"alt{pid}", $"acct-{pid}");

    private static Macro NewMacro() => new(
        SchemaVersion: 3, Id: Guid.NewGuid().ToString(), Name: "m",
        RecordMode: "PerWindow", RecordedAgainstUserId: null,
        RecordedAgainstDisplayName: null, InterAltDelayMs: null,
        RecordedAtUnixMs: 0, Events: new List<MacroEvent>());

    private static ScheduledAlt KeepAlive(int pid, long dueAtMs, long intervalMs = 12 * Min) => new()
    {
        Assignment = new Assignment(Alt(pid, pid), null, CadenceRole.KeepAlive),
        DueAtMs = dueAtMs,
        IntervalMs = intervalMs,
    };

    private static ScheduledAlt Active(int pid) => new()
    {
        Assignment = new Assignment(Alt(pid, pid), NewMacro(), CadenceRole.Active),
        DueAtMs = 0,
        IntervalMs = 0,
    };

    /// THE feature. No active alts and nothing due => sleep. No focus steal.
    /// This is the case that makes a single keep-alive account stop hijacking
    /// the desktop every 1.25 seconds.
    [Fact]
    public void NoActives_NothingDue_SleepsUntilTheEarliestDeadline()
    {
        var alts = new[] { KeepAlive(1, dueAtMs: 10 * Min), KeepAlive(2, dueAtMs: 4 * Min) };

        var d = CadenceScheduler.Decide(alts, nowMs: 0, nextActivePassCostMs: 0);

        var sleep = Assert.IsType<CadenceDecision.SleepUntil>(d);
        Assert.Equal(4 * Min, sleep.WakeAtMs);   // earliest deadline wins
    }

    [Fact]
    public void KeepAliveDue_IsServiced()
    {
        var alts = new[] { KeepAlive(1, dueAtMs: 5 * Min) };

        var d = CadenceScheduler.Decide(alts, nowMs: 5 * Min, nextActivePassCostMs: 0);

        var svc = Assert.IsType<CadenceDecision.ServiceKeepAlive>(d);
        Assert.Equal(1, svc.Alt.Assignment.Alt.Pid);
    }

    /// Gap-fitting: the keep-alive isn't due YET, but it would blow its deadline
    /// if we ran another active pass first. It cuts the line.
    [Fact]
    public void KeepAliveDueWithinTheNextActivePass_IsServicedBeforeTheActive()
    {
        var alts = new ScheduledAlt[] { Active(1), KeepAlive(2, dueAtMs: 3 * Min) };

        // now=0, keep-alive due at 3min, but the next active pass costs 5min:
        // running the active first means servicing the keep-alive at 5min — too late.
        var d = CadenceScheduler.Decide(alts, nowMs: 0, nextActivePassCostMs: 5 * Min);

        var svc = Assert.IsType<CadenceDecision.ServiceKeepAlive>(d);
        Assert.Equal(2, svc.Alt.Assignment.Alt.Pid);
    }

    /// The keep-alive comfortably survives another pass, so farming wins.
    [Fact]
    public void KeepAliveSafelyBeyondTheNextActivePass_ActiveRunsFirst()
    {
        var alts = new ScheduledAlt[] { Active(1), KeepAlive(2, dueAtMs: 30 * Min) };

        var d = CadenceScheduler.Decide(alts, nowMs: 0, nextActivePassCostMs: 5 * Min);

        var run = Assert.IsType<CadenceDecision.RunActive>(d);
        Assert.Equal(1, run.Alt.Assignment.Alt.Pid);
    }

    [Fact]
    public void TwoUrgentKeepAlives_EarliestDeadlineIsServicedFirst()
    {
        var alts = new[] { KeepAlive(1, dueAtMs: 9 * Min), KeepAlive(2, dueAtMs: 2 * Min) };

        var d = CadenceScheduler.Decide(alts, nowMs: 10 * Min, nextActivePassCostMs: 0);

        var svc = Assert.IsType<CadenceDecision.ServiceKeepAlive>(d);
        Assert.Equal(2, svc.Alt.Assignment.Alt.Pid);   // most overdue
    }

    /// Compat guard: an all-Active squad must still round-robin back-to-back,
    /// exactly as the old spin loop did. Actives are always runnable.
    [Fact]
    public void AllActive_AlwaysRunsAnActive_NeverSleeps()
    {
        var alts = new[] { Active(1), Active(2) };

        var d = CadenceScheduler.Decide(alts, nowMs: 0, nextActivePassCostMs: 5 * Min);

        Assert.IsType<CadenceDecision.RunActive>(d);
    }

    [Fact]
    public void NoAltsAtAll_Sleeps()
    {
        var d = CadenceScheduler.Decide(Array.Empty<ScheduledAlt>(), nowMs: 0, nextActivePassCostMs: 0);
        Assert.IsType<CadenceDecision.SleepUntil>(d);
    }
}
```

The helper is local to this file on purpose — `AssignmentRunnerTests`'s copy is `private`,
and duplicating four lines beats coupling two test files together.

- [ ] **Step 2: Run test to verify it fails**

Run: `dotnet test tests/rororo-ur-task.Tests/ --filter CadenceSchedulerTests`
Expected: FAIL — `CadenceScheduler` / `ScheduledAlt` / `CadenceDecision` do not exist.

- [ ] **Step 3: Write minimal implementation**

Create `src/Macros/CadenceScheduler.cs`:

```csharp
namespace Labs626.UrTask.Macros;

/// <summary>Scheduler-internal state for one assignment. Not persisted.</summary>
internal sealed class ScheduledAlt
{
    public required Assignment Assignment { get; set; }

    /// <summary>Monotonic ms at which this alt next needs servicing. Actives ignore this.</summary>
    public long DueAtMs { get; set; }

    /// <summary>KeepAlive: the game's fire interval. Active: 0 (always runnable).</summary>
    public long IntervalMs { get; set; }

    /// <summary>
    /// Consecutive focus failures. Reset to 0 on any successful focus. Lets the runner
    /// tell "transient blip" from "this alt's window is gone" without dropping it on a
    /// single miss.
    /// </summary>
    public int ConsecutiveFocusFailures { get; set; }

    public bool IsKeepAlive => Assignment.Role == CadenceRole.KeepAlive;
}

/// <summary>What the runner should do next.</summary>
internal abstract record CadenceDecision
{
    public sealed record ServiceKeepAlive(ScheduledAlt Alt) : CadenceDecision;
    public sealed record RunActive(ScheduledAlt Alt) : CadenceDecision;
    public sealed record SleepUntil(long WakeAtMs) : CadenceDecision;
}

/// <summary>
/// The scheduling policy, as a PURE function — no Win32, no timers, no I/O — so the
/// hard cases (a keep-alive falling due inside a long macro pass; nothing due at all)
/// are deterministic under a fake clock.
///
/// Foreground is an EXCLUSIVE resource: one window at a time. Two task classes want it:
///   Active    — wants it continuously (farm back-to-back). NO hard deadline; a skipped
///               pass is just less farming.
///   KeepAlive — wants it for ~1s, but on a HARD deadline. Miss it and the game kicks
///               the alt.
/// So keep-alives win ties, but only when they actually need to — which is what lets
/// the loop SLEEP the rest of the time instead of stealing focus every 1.25s.
/// </summary>
internal static class CadenceScheduler
{
    public static CadenceDecision Decide(
        IReadOnlyList<ScheduledAlt> alts, long nowMs, long nextActivePassCostMs)
    {
        ScheduledAlt? urgent = null;
        ScheduledAlt? nextActive = null;
        long earliestDue = long.MaxValue;

        foreach (var alt in alts)
        {
            if (alt.IsKeepAlive)
            {
                if (alt.DueAtMs < earliestDue) earliestDue = alt.DueAtMs;

                // Would this alt miss its deadline if we ran one more active pass first?
                // (With no actives, nextActivePassCostMs is 0 and this is simply "is it due".)
                if (alt.DueAtMs <= nowMs + nextActivePassCostMs)
                {
                    // Earliest deadline first — the most overdue alt is the most at risk.
                    if (urgent is null || alt.DueAtMs < urgent.DueAtMs) urgent = alt;
                }
            }
            else
            {
                nextActive ??= alt;   // round-robin order is the caller's list order
            }
        }

        if (urgent is not null) return new CadenceDecision.ServiceKeepAlive(urgent);
        if (nextActive is not null) return new CadenceDecision.RunActive(nextActive);

        // Nothing active, nothing due: SLEEP. This is the whole feature.
        return new CadenceDecision.SleepUntil(earliestDue == long.MaxValue ? nowMs + 1_000 : earliestDue);
    }
}
```

- [ ] **Step 4: Run tests to verify they pass**

Run: `dotnet build rororo-ur-task.csproj -c Debug` → 0 errors
Run: `dotnet test tests/rororo-ur-task.Tests/ --filter CadenceSchedulerTests`
Expected: PASS (7 tests)

- [ ] **Step 5: Commit**

```bash
git add src/Macros/CadenceScheduler.cs tests/rororo-ur-task.Tests/CadenceSchedulerTests.cs
git commit -m "feat(cadence): pure Decide policy — gap-fit keep-alives, sleep when idle"
```

---

### Task 4: Foreground capture + restore

**Files:**
- Modify: `src/PluginHost/Win32Focus.cs`
- Test: none (pure Win32 interop — no honest unit test without a desktop; it is exercised by Task 5's integration seam and verified live)

**Interfaces:**
- Produces: `static IntPtr Win32Focus.CaptureForeground()`; `static bool Win32Focus.RestoreForeground(IntPtr hwnd)`.

**Why no test:** this is a thin P/Invoke wrapper over `GetForegroundWindow`/`SetForegroundWindow`. A unit test would assert that we call the API we call — a tautology. The behavior that matters (capture-then-restore around a keep-alive) is asserted at the runner seam in Task 5, where focus is an injected delegate.

- [ ] **Step 1: Refactor the focus core so it can target an HWND**

In `src/PluginHost/Win32Focus.cs`, extract the existing foreground-lock body of
`AttachAndFocus` into a private helper and add the two public members. The lock-lowering
dance (attach input queue → zero `SPI_SETFOREGROUNDLOCKTIMEOUT` → `SetForegroundWindow` +
`BringWindowToTop` → restore) is required for the restore path too: putting focus *back*
while the user is idle hits the exact same foreground-lock no-op.

```csharp
    /// <summary>The window that currently owns the foreground. IntPtr.Zero if none.</summary>
    public static IntPtr CaptureForeground() => GetForegroundWindow();

    /// <summary>
    /// Put the foreground back where we found it after a keep-alive tap. Uses the same
    /// foreground-lock dance as AttachAndFocus — restoring focus while the user is idle
    /// hits the identical SetForegroundWindow no-op if you skip it. Best-effort: a failed
    /// restore is annoying, never fatal, so the caller logs and carries on.
    /// </summary>
    public static bool RestoreForeground(IntPtr hwnd)
    {
        if (hwnd == IntPtr.Zero) return false;
        try { return FocusHwnd(hwnd); }
        catch { return false; }
    }

    public static (bool ok, string? error) AttachAndFocus(int pid)
    {
        try
        {
            var hwnd = Process.GetProcessById(pid).MainWindowHandle;
            if (hwnd == IntPtr.Zero) return (false, "MainWindowHandle is null.");
            if (IsIconic(hwnd)) ShowWindow(hwnd, SW_RESTORE);
            FocusHwnd(hwnd);
            return (true, null);
        }
        catch (ArgumentException) { return (false, "Process not found (pid stale)."); }
        catch (Exception ex) { return (false, ex.Message); }
    }

    /// <summary>
    /// Force hwnd to the foreground even with no recent user input. AttachThreadInput
    /// alone is NOT enough on modern Windows — the foreground-lock timeout makes
    /// SetForegroundWindow silently no-op. Attach to the foreground thread's input
    /// queue, temporarily zero the lock timeout (restored right after), BringWindowToTop.
    /// </summary>
    private static bool FocusHwnd(IntPtr hwnd)
    {
        var fgHwnd = GetForegroundWindow();
        var fgThreadId = fgHwnd != IntPtr.Zero ? GetWindowThreadProcessId(fgHwnd, out _) : 0u;
        var ourThreadId = GetCurrentThreadId();
        bool attached = false;
        if (fgThreadId != 0 && fgThreadId != ourThreadId)
            attached = AttachThreadInput(fgThreadId, ourThreadId, true);

        uint savedTimeout = 0;
        bool loweredLock = false;
        try
        {
            if (SystemParametersInfoGet(SPI_GETFOREGROUNDLOCKTIMEOUT, 0, ref savedTimeout, 0))
            {
                SystemParametersInfoSet(SPI_SETFOREGROUNDLOCKTIMEOUT, 0, IntPtr.Zero, SPIF_SENDCHANGE);
                loweredLock = true;
            }
            SetForegroundWindow(hwnd);
            BringWindowToTop(hwnd);
            return true;
        }
        finally
        {
            if (loweredLock)
                SystemParametersInfoSet(SPI_SETFOREGROUNDLOCKTIMEOUT, 0, new IntPtr(savedTimeout), SPIF_SENDCHANGE);
            if (attached) AttachThreadInput(fgThreadId, ourThreadId, false);
        }
    }
```

- [ ] **Step 2: Build and confirm no regression**

Run: `dotnet build rororo-ur-task.csproj -c Debug`
Expected: 0 errors, 0 warnings
Run: `dotnet test tests/rororo-ur-task.Tests/`
Expected: suite green (`AttachAndFocus`'s behavior is unchanged — same calls, same order)

- [ ] **Step 3: Commit**

```bash
git add src/PluginHost/Win32Focus.cs
git commit -m "feat(focus): capture + restore foreground (shared foreground-lock core)"
```

---

### Task 5: Replace the spin loop with the scheduler

The centerpiece. `AssignmentRunner`'s public surface is frozen; only its internals change.

**Files:**
- Modify: `src/Macros/AssignmentRunner.cs`
- Test: `tests/rororo-ur-task.Tests/CadenceRunnerTests.cs` (create)

**Interfaces:**
- Consumes: `CadenceScheduler.Decide`, `ScheduledAlt`, `CadenceDecision` (Task 3); `KeepAliveIntervals.For` (Task 2); `Win32Focus.CaptureForeground/RestoreForeground` (Task 4).
- Produces: `internal sealed record CadenceDeps(...)` + `static CadenceDeps.Real`, and a new
  ctor `internal AssignmentRunner(IMacroPlayer, IForegroundWatcher, CadenceDeps)`.

**Two seams the naive design misses — add both, they are not optional:**

1. **`SendKeepAlive`.** The keep-alive Space goes through the real `SendInput`. A test that
   runs the loop without this seam **injects live keystrokes into the developer's desktop**.
   The tap must be an injectable `Action`.
2. **`KeepAliveIntervalMs`.** Building the schedule otherwise calls `UserPreferences.Load()`,
   which reads from disk inside a unit test. Inject the per-alt interval lookup instead.

```csharp
/// Everything the cadence loop touches that isn't the player or the foreground watcher.
/// Exists so the scheduler can be driven by a fake clock that JUMPS instead of waiting —
/// a simulated hour runs in milliseconds — and so a unit test never injects a real Space
/// into the developer's desktop or reads the user's prefs file.
internal sealed record CadenceDeps(
    Func<int, (bool ok, string? error)> Focus,
    Func<long> ClockMs,
    Func<long, CancellationToken, Task> Sleep,   // arg is a DURATION in ms, not a wake time
    Func<IntPtr> CaptureForeground,
    Action<IntPtr> RestoreForeground,
    Action SendKeepAlive,
    Func<AccountRegistry.AccountInfo, long> KeepAliveIntervalMs)
{
    public static CadenceDeps Real => new(
        Focus: Win32Focus.AttachAndFocus,
        ClockMs: () => Environment.TickCount64,           // MONOTONIC — never wall-clock
        Sleep: (ms, ct) => ms <= 0 ? Task.CompletedTask : Task.Delay((int)ms, ct),
        CaptureForeground: Win32Focus.CaptureForeground,
        RestoreForeground: h => Win32Focus.RestoreForeground(h),
        SendKeepAlive: () => AssignmentRunner.SendSpaceKeepAlive(),
        KeepAliveIntervalMs: alt => (long)KeepAliveIntervals
            .For(alt.PlaceId, alt.PlaceName, UI.UserPreferences.Load()).TotalMilliseconds);
}
```

**Visibility:** `AssignmentRunner.SendSpaceKeepAlive()` is currently `private static` —
`CadenceDeps` is a separate type and cannot reach it. Change it to `internal static`. Do
**not** change its body; the `INPUT` struct size it depends on is locked by
`AssignmentRunnerTests.KeepAliveInputStructSize_MatchesCanonicalWin32InputSize`, which is a
regression guard for the bug that made keep-alive a silent no-op through v0.2.2.

Keep the existing public `AssignmentRunner(IMacroPlayer, IForegroundWatcher)` ctor and the
existing `internal AssignmentRunner(IMacroPlayer, IForegroundWatcher, Func<int,(bool,string?)>)`
ctor — both delegate to the `CadenceDeps` ctor (the 3-arg one overriding only `Focus` on
`CadenceDeps.Real`). This is what keeps `AssignmentRunnerTests` compiling untouched.

- [ ] **Step 1: Write the failing test**

Create `tests/rororo-ur-task.Tests/CadenceRunnerTests.cs`. The fake clock never really
sleeps — it **jumps**, so a simulated hour runs in milliseconds:

```csharp
using Labs626.UrTask.Macros;
using Labs626.UrTask.PluginHost;

namespace Labs626.UrTask.Tests;

public class CadenceRunnerTests
{
    private const long Min = 60_000;
    private const long TwelveMin = 12 * Min;

    // AccountInfo(int Pid, long RobloxUserId, string DisplayName, string AccountId,
    //             long PlaceId = 0, string PlaceName = "")
    private static AccountRegistry.AccountInfo Alt(int pid) => new(pid, pid, $"alt{pid}", $"acct-{pid}");

    // Macro.Duration == the LAST event's TimestampMs. That is exactly what the
    // active-pass lookahead reads, so a macro's "length" is set by its last event.
    // MacroEvent(long TimestampMs, MacroEventKind Kind, int VirtualKeyCode,
    //            int X, int Y, int MouseButton, int WheelDelta)
    private static Macro MacroOfLength(long durationMs) => new(
        SchemaVersion: 3, Id: Guid.NewGuid().ToString(), Name: $"m{durationMs}",
        RecordMode: "PerWindow", RecordedAgainstUserId: null, RecordedAgainstDisplayName: null,
        InterAltDelayMs: 0, RecordedAtUnixMs: 0,
        Events: new List<MacroEvent> { new(durationMs, MacroEventKind.KeyDown, 0x20, 0, 0, 0, 0) });

    /// Leaps forward when the runner sleeps — a simulated hour costs no real time.
    private sealed class FakeClock
    {
        public long NowMs;
        public long Now() => NowMs;
        public Task Sleep(long durationMs, CancellationToken ct)
        {
            if (durationMs > 0) NowMs += durationMs;   // jump, never actually wait
            return Task.CompletedTask;
        }
    }

    private sealed class FakePlayer : IMacroPlayer
    {
        public List<long> Plays { get; } = new();
        public bool IsPlaying => false;
        public event EventHandler<PlaybackStartedArgs>? Started;
        public event EventHandler<PlaybackEndedArgs>? Ended;
        public Task<PlaybackResult> PlayAsync(Macro macro, long targetUserId, CancellationToken external = default)
        {
            Plays.Add(targetUserId);
            return Task.FromResult(PlaybackResult.Completed());
        }
        public Task<PlaybackResult> PlayAllWindowsRawAsync(Macro macro, CancellationToken external = default)
            => Task.FromResult(PlaybackResult.Completed());
        public bool Abort() => false;
    }

    private sealed class FakeForeground : IForegroundWatcher
    {
        public AccountRegistry.AccountInfo? Current;
        public AccountRegistry.AccountInfo? ResolveForegroundAccount() => Current;
    }

    private sealed record Rig(
        AssignmentRunner Runner, FakeClock Clock, CancellationTokenSource Cts,
        List<int> Taps, List<int> Focused, List<IntPtr> Restored, FakePlayer Player);

    /// A runner whose clock jumps, whose Space is COUNTED not injected, and which
    /// cancels itself once `runForMs` of simulated time has elapsed.
    private static Rig Build(
        IReadOnlyList<Assignment> assignments, long runForMs, long keepAliveIntervalMs = TwelveMin)
    {
        var clock = new FakeClock();
        var fg = new FakeForeground();
        var player = new FakePlayer();
        var cts = new CancellationTokenSource();
        var taps = new List<int>();
        var focused = new List<int>();
        var restored = new List<IntPtr>();
        var currentPid = 0;

        var deps = new CadenceDeps(
            Focus: pid =>
            {
                focused.Add(pid);
                currentPid = pid;
                fg.Current = assignments.First(a => a.Alt.Pid == pid).Alt;   // so the verify passes
                return (true, null);
            },
            ClockMs: clock.Now,
            Sleep: (ms, ct) =>
            {
                var t = clock.Sleep(ms, ct);
                if (clock.NowMs >= runForMs) cts.Cancel();   // end the simulation
                return t;
            },
            CaptureForeground: () => new IntPtr(0xBEEF),     // sentinel: "the user's window"
            RestoreForeground: h => restored.Add(h),
            SendKeepAlive: () => taps.Add(currentPid),       // counted, never injected
            KeepAliveIntervalMs: _ => keepAliveIntervalMs);

        return new Rig(new AssignmentRunner(player, fg, deps), clock, cts, taps, focused, restored, player);
    }

    /// THE regression. One keep-alive alt on a 12-minute interval, one simulated hour.
    /// Correct: ~5 taps. The old spin loop: ~2,880 — one every 1.25s, each stealing the
    /// user's foreground. If this count ever climbs back into the hundreds, the
    /// desktop-hijack bug is back and this test is the tripwire.
    [Fact]
    public async Task SingleKeepAliveAlt_OverASimulatedHour_IsTappedAboutFiveTimes_NotThousands()
    {
        var alt = new Assignment(Alt(1), null, CadenceRole.KeepAlive);
        var rig = Build(new[] { alt }, runForMs: 60 * Min);

        await rig.Runner.RunAsync(new[] { alt }, rig.Cts.Token);

        Assert.InRange(rig.Taps.Count, 4, 6);
        Assert.True(rig.Taps.Count < 20,
            $"keep-alive tapped {rig.Taps.Count}x in a simulated hour — the spin loop is back");
    }

    /// Every foreground steal is paired with a restore, so a keep-alive is a ~1s blip
    /// rather than a hijack.
    [Fact]
    public async Task KeepAliveService_RestoresThePriorForeground()
    {
        var alt = new Assignment(Alt(1), null, CadenceRole.KeepAlive);
        var rig = Build(new[] { alt }, runForMs: 30 * Min);

        await rig.Runner.RunAsync(new[] { alt }, rig.Cts.Token);

        Assert.NotEmpty(rig.Taps);
        Assert.Equal(rig.Taps.Count, rig.Restored.Count);
        Assert.All(rig.Restored, h => Assert.Equal(new IntPtr(0xBEEF), h));
    }

    /// Compat guard: an all-Active squad still round-robins back-to-back, exactly as
    /// v0.6 did. No sleeping, no keep-alive taps.
    [Fact]
    public async Task AllActiveAssignments_RoundRobinBackToBack()
    {
        var a1 = new Assignment(Alt(1), MacroOfLength(1_000), CadenceRole.Active);
        var a2 = new Assignment(Alt(2), MacroOfLength(1_000), CadenceRole.Active);
        var rig = Build(new[] { a1, a2 }, runForMs: 2 * Min);

        await rig.Runner.RunAsync(new[] { a1, a2 }, rig.Cts.Token);

        Assert.Empty(rig.Taps);                       // nothing is on keep-alive
        Assert.Contains(1L, rig.Player.Plays);        // both alts farmed
        Assert.Contains(2L, rig.Player.Plays);
        Assert.True(rig.Player.Plays.Count > 5, "actives must run back-to-back, not sleep");
    }

    /// Gap-fitting end to end: a long Active pass must not starve the keep-alive.
    /// The 5-minute macro means the lookahead sees a keep-alive coming due inside the
    /// next pass and services it FIRST.
    [Fact]
    public async Task LongActivePass_StillLetsTheKeepAliveFire()
    {
        var active = new Assignment(Alt(1), MacroOfLength(5 * Min), CadenceRole.Active);
        var keep = new Assignment(Alt(2), null, CadenceRole.KeepAlive);
        var rig = Build(new[] { active, keep }, runForMs: 60 * Min);

        await rig.Runner.RunAsync(new[] { active, keep }, rig.Cts.Token);

        Assert.NotEmpty(rig.Player.Plays);                    // farming still happened
        Assert.NotEmpty(rig.Taps);                            // and the keep-alive still got fed
        Assert.All(rig.Taps, pid => Assert.Equal(2, pid));    // only the keep-alive alt is tapped
    }
}
```

- [ ] **Step 2: Run test to verify it fails**

Run: `dotnet test tests/rororo-ur-task.Tests/ --filter CadenceRunnerTests`
Expected: FAIL — the runner has no clock/sleep/foreground seams.

- [ ] **Step 3: Replace the loop**

In `src/Macros/AssignmentRunner.cs`:

1. Add a `private readonly CadenceDeps _deps;` field. The existing public
   `AssignmentRunner(IMacroPlayer, IForegroundWatcher)` ctor passes `CadenceDeps.Real`;
   the existing `internal AssignmentRunner(IMacroPlayer, IForegroundWatcher, Func<int,(bool,string?)>)`
   ctor passes `CadenceDeps.Real with { Focus = focus }`. Both keep compiling for existing
   callers and existing tests. Add the new `internal AssignmentRunner(IMacroPlayer, IForegroundWatcher, CadenceDeps)`.
2. Replace the `while (!ct.IsCancellationRequested) { for (...) { ... } }` body of
   `RunAsync` (lines ~60–121) with the Decide loop:

```csharp
        var scheduled = assignments.Select(a => new ScheduledAlt
        {
            Assignment = a,
            IntervalMs = a.Role == CadenceRole.KeepAlive ? _deps.KeepAliveIntervalMs(a.Alt) : 0,
            DueAtMs = _deps.ClockMs(),   // every keep-alive is due immediately on start:
                                         // tap once up front, THEN settle into its interval.
        }).ToList();

        var activeCursor = 0;

        try
        {
            while (!ct.IsCancellationRequested)
            {
                var now = _deps.ClockMs();
                var decision = CadenceScheduler.Decide(
                    Rotated(scheduled, activeCursor), now, NextActivePassCostMs(scheduled, activeCursor));

                switch (decision)
                {
                    case CadenceDecision.SleepUntil sleep:
                        // The whole point: nothing to do, so do NOTHING. No focus steal.
                        try { await _deps.Sleep(sleep.WakeAtMs - now, ct).ConfigureAwait(false); }
                        catch (OperationCanceledException) { }
                        break;

                    case CadenceDecision.ServiceKeepAlive svc:
                        await ServiceKeepAliveAsync(svc.Alt, ++cycle, ct).ConfigureAwait(false);
                        // Re-read the clock: the service itself consumed real time.
                        svc.Alt.DueAtMs = _deps.ClockMs() + svc.Alt.IntervalMs;
                        break;

                    case CadenceDecision.RunActive run:
                        await RunActiveAsync(run.Alt, ++cycle, ct).ConfigureAwait(false);
                        activeCursor++;   // advance the round-robin among the actives
                        break;
                }
            }
            EmitProgress(new AssignmentProgress(cycle, -1, assignments.Count, null, AssignmentPhase.Stopped));
        }
        finally
        {
            _activeCts?.Dispose();
            _activeCts = null;
        }
```

`Decide` returns the FIRST Active it encounters, so rotate the actives by the cursor before
handing the list over — that is what makes the actives round-robin instead of the first one
starving the rest:

```csharp
    /// Same alts, with the Active entries rotated so the cursor's alt comes first.
    /// KeepAlives are order-independent (Decide picks by deadline), so they ride along.
    private static IReadOnlyList<ScheduledAlt> Rotated(IReadOnlyList<ScheduledAlt> alts, int cursor)
    {
        var actives = alts.Where(a => !a.IsKeepAlive).ToList();
        if (actives.Count <= 1) return alts;
        var start = cursor % actives.Count;
        var rotated = actives.Skip(start).Concat(actives.Take(start));
        return alts.Where(a => a.IsKeepAlive).Concat(rotated).ToList();
    }
```

3. `ServiceKeepAliveAsync` — capture, focus, verify, Space, **restore**:

```csharp
    private async Task ServiceKeepAliveAsync(ScheduledAlt alt, int cycle, CancellationToken ct)
    {
        var asn = alt.Assignment;
        EmitProgress(new AssignmentProgress(cycle, 0, 1, asn, AssignmentPhase.Focusing));

        var prior = _deps.CaptureForeground();     // whatever the USER was doing

        if (!_deps.Focus(asn.Alt.Pid).ok)
        {
            // Bounded retry — do NOT hammer a window that won't focus. After three
            // straight misses the window is almost certainly gone (alt closed/crashed),
            // so say so loudly instead of silently retrying it forever.
            alt.DueAtMs = _deps.ClockMs() + FocusRetryBackoffMs;
            alt.ConsecutiveFocusFailures++;
            EmitProgress(new AssignmentProgress(
                cycle, 0, 1, asn,
                alt.ConsecutiveFocusFailures >= 3 ? AssignmentPhase.Warning : AssignmentPhase.Skipped,
                alt.ConsecutiveFocusFailures >= 3
                    ? $"{asn.Alt.DisplayName} hasn't been focusable for {alt.ConsecutiveFocusFailures} tries — its window may be gone. Still retrying every 30s."
                    : null));
            return;
        }
        alt.ConsecutiveFocusFailures = 0;   // a good focus clears the streak
        try { await _deps.Sleep(DefaultPerAltDelayMs, ct).ConfigureAwait(false); }
        catch (OperationCanceledException) { return; }

        // Safety invariant (unchanged from v0.6): never synthesize input unless the
        // foreground really is the alt we aimed at.
        var fg = _foreground.ResolveForegroundAccount();
        if (fg is null || fg.RobloxUserId != asn.Alt.RobloxUserId)
        {
            alt.DueAtMs = _deps.ClockMs() + FocusRetryBackoffMs;
            EmitProgress(new AssignmentProgress(cycle, 0, 1, asn, AssignmentPhase.Skipped));
            _deps.RestoreForeground(prior);
            return;
        }

        EmitProgress(new AssignmentProgress(cycle, 0, 1, asn, AssignmentPhase.Playing));
        _deps.SendKeepAlive();
        try { await _deps.Sleep(KeepAliveDelayMs, ct).ConfigureAwait(false); }
        catch (OperationCanceledException) { /* still restore below */ }

        // Hand the desktop back. A keep-alive is a ~1s blip, not a hijack. When an
        // Active alt was farming, this also returns focus to it so farming resumes.
        _deps.RestoreForeground(prior);
    }

    private const int FocusRetryBackoffMs = 30_000;   // spec: bounded 30s retry
```

4. `RunActiveAsync` — today's behavior, unchanged: focus, settle, verify foreground, play,
   and **no** restore (an Active alt should hold focus between its own back-to-back
   passes). Lift the body from the current loop, including the `Refused`/`Aborted`
   progress emit — but route it through the deps (`_deps.Focus`, `_deps.Sleep`,
   `_deps.ClockMs`) rather than calling `Win32Focus` / `Task.Delay` directly, or the fake
   clock can't drive it and `AllActiveAssignments_RoundRobinBackToBack` will hang.

5. `NextActivePassCostMs` — the macro-length lookahead:

```csharp
    /// What one more Active pass will cost, so Decide knows whether a keep-alive can
    /// wait for it. Macro.Duration is already known (last-event timestamp) — this is
    /// what turns cadence from a guess into a computed timeline.
    private static long NextActivePassCostMs(IReadOnlyList<ScheduledAlt> alts, int cursor)
    {
        var actives = alts.Where(a => !a.IsKeepAlive).ToList();
        if (actives.Count == 0) return 0;
        var next = actives[cursor % actives.Count];
        var macro = next.Assignment.Macro;
        var playMs = macro is null ? 0 : (long)macro.Duration.TotalMilliseconds;
        return playMs + DefaultPerAltDelayMs + (macro?.InterAltDelayMs ?? 500);
    }
```

Note `Decide` takes the list in caller order; `RunActive` returns the first Active it sees,
so rotate the actives by `activeCursor` when building the list passed to `Decide` (or pass
the cursor through). Keep it simple: build `scheduled` once, and reorder the Active entries
by `activeCursor` before each `Decide` call.

- [ ] **Step 4: Run tests to verify they pass**

Run: `dotnet build rororo-ur-task.csproj -c Debug` → 0 errors
Run: `dotnet test tests/rororo-ur-task.Tests/ --filter CadenceRunnerTests`
Expected: PASS — **`SingleKeepAliveAlt_...NotThousands` is the one that matters.**
Run: `dotnet test tests/rororo-ur-task.Tests/`
Expected: full suite green — `AssignmentRunnerTests`, `RecipeRunnerTests`, `SequencePlayerTests` all still pass (the public surface did not change).

- [ ] **Step 5: Commit**

```bash
git add src/Macros/AssignmentRunner.cs tests/rororo-ur-task.Tests/CadenceRunnerTests.cs
git commit -m "feat(cadence): replace round-robin spin loop with the deadline scheduler"
```

---

### Task 6: Unschedulable warning

**Files:**
- Modify: `src/Macros/AssignmentRunner.cs` (compute at start, emit via `Progress`)
- Modify: `src/PluginRuntime.cs` (surface to activity log)
- Modify: `src/UI/RecorderViewModel.cs` (surface as a toast)
- Test: `tests/rororo-ur-task.Tests/CadenceRunnerTests.cs` (extend)

**Interfaces:**
- Consumes: `ScheduledAlt`, `NextActivePassCostMs` (Task 5).
- Produces: `AssignmentPhase.Warning` added to the existing enum; `AssignmentProgress.Reason` carries the text (the field already exists).

- [ ] **Step 1: Write the failing test**

Add to `tests/rororo-ur-task.Tests/CadenceRunnerTests.cs`:

```csharp
    /// An alt whose keep-alive interval is SHORTER than one active pass cannot be
    /// kept alive — even firing it the instant a pass ends, the next pass blows its
    /// deadline. We know Macro.Duration and the intervals up front, so say so BEFORE
    /// the alt gets kicked, not after.
    [Fact]
    public async Task KeepAliveIntervalShorterThanActivePass_WarnsAtStart_ButStillRuns()
    {
        // Active alt with a 16-minute macro; keep-alive alt on a 12-minute interval.
        // Expect: an AssignmentPhase.Warning progress event naming the keep-alive alt,
        // AND the run proceeds (warn, don't block).
        var warnings = new List<AssignmentProgress>();
        // ... subscribe to Progress, collect Phase == AssignmentPhase.Warning

        Assert.Single(warnings);
        Assert.Contains("kicked", warnings[0].Reason, StringComparison.OrdinalIgnoreCase);
        // and the run still serviced things — it did not abort
    }
```

- [ ] **Step 2: Run test to verify it fails**

Run: `dotnet test tests/rororo-ur-task.Tests/ --filter KeepAliveIntervalShorterThanActivePass`
Expected: FAIL — no `AssignmentPhase.Warning`.

- [ ] **Step 3: Implement**

In `src/Macros/AssignmentRunner.cs`, extend the enum:

```csharp
public enum AssignmentPhase { Focusing, Playing, Skipped, Refused, Stopped, Warning }
```

At the top of `RunAsync`, right after `scheduled` is built:

```csharp
        // Unschedulable check. The longest active pass sets the worst-case wait any
        // keep-alive can face. If an alt's interval is shorter than that, we cannot
        // guarantee it — say so now rather than letting it get kicked silently.
        var longestActivePassMs = scheduled
            .Where(a => !a.IsKeepAlive)
            .Select(a => (a.Assignment.Macro is null ? 0L : (long)a.Assignment.Macro.Duration.TotalMilliseconds)
                         + DefaultPerAltDelayMs + (a.Assignment.Macro?.InterAltDelayMs ?? 500))
            .DefaultIfEmpty(0L)
            .Max();

        foreach (var alt in scheduled.Where(a => a.IsKeepAlive && a.IntervalMs < longestActivePassMs))
        {
            var mins = alt.IntervalMs / 60_000.0;
            var passMins = longestActivePassMs / 60_000.0;
            EmitProgress(new AssignmentProgress(
                0, -1, assignments.Count, alt.Assignment, AssignmentPhase.Warning,
                $"{alt.Assignment.Alt.DisplayName} may get kicked — its keep-alive is every " +
                $"{mins:F0} min but your active macro's pass is {passMins:F0} min. " +
                $"Shorten the macro, split it, or set this alt to Active."));
        }
```

In `src/PluginRuntime.cs`, in the existing `_runner.Progress` handler (around lines
110–111, where `Refused` is already logged), add the `Warning` phase to what gets
`Log(...)`-ed so it reaches the activity log.

In `src/UI/RecorderViewModel.cs`, in the `_runner.Progress` subscription (around lines
235–236, where the `Refused` toast fires), raise the same themed toast for
`AssignmentPhase.Warning`. Reuse the existing toast path — do not build a new one.

- [ ] **Step 4: Run tests to verify they pass**

Run: `dotnet build rororo-ur-task.csproj -c Debug` → 0 errors
Run: `dotnet test tests/rororo-ur-task.Tests/`
Expected: full suite green

- [ ] **Step 5: Commit**

```bash
git add src/Macros/AssignmentRunner.cs src/PluginRuntime.cs src/UI/RecorderViewModel.cs tests/rororo-ur-task.Tests/CadenceRunnerTests.cs
git commit -m "feat(cadence): warn up front when an alt cannot be kept alive"
```

---

### Task 7: Heartbeat claim file

**Files:**
- Create: `src/PluginHost/ClaimFile.cs`
- Modify: `src/PluginRuntime.cs` (start on run, stop on stop)
- Test: `tests/rororo-ur-task.Tests/ClaimFileTests.cs` (create)

**Interfaces:**
- Produces: `sealed class ClaimFile : IDisposable` with `static string DefaultPath`, `void Start(IEnumerable<long> ownedUserIds)`, `void Stop()`; JSON shape `{plugin, heartbeatUtc, ttlSeconds, ownedUserIds}`.

- [ ] **Step 1: Write the failing test**

Create `tests/rororo-ur-task.Tests/ClaimFileTests.cs`:

```csharp
using System.Text.Json;
using Labs626.UrTask.PluginHost;

namespace Labs626.UrTask.Tests;

public class ClaimFileTests : IDisposable
{
    private readonly string _dir = Path.Combine(Path.GetTempPath(), "urtask-claim-" + Guid.NewGuid().ToString("N"));
    private string Path_ => System.IO.Path.Combine(_dir, "ur-task.json");

    public ClaimFileTests() => Directory.CreateDirectory(_dir);
    public void Dispose() { try { Directory.Delete(_dir, true); } catch { } }

    [Fact]
    public void Start_WritesTheOwnedAccounts()
    {
        using var claim = new ClaimFile(Path_);
        claim.Start(new[] { 111L, 222L });

        var doc = JsonSerializer.Deserialize<JsonElement>(File.ReadAllText(Path_));
        Assert.Equal("ur-task", doc.GetProperty("plugin").GetString());
        Assert.Equal(60, doc.GetProperty("ttlSeconds").GetInt32());
        var owned = doc.GetProperty("ownedUserIds").EnumerateArray().Select(e => e.GetInt64()).ToList();
        Assert.Equal(new[] { 111L, 222L }, owned);
    }

    /// Fails SAFE: if Ur Task stops cleanly, the claim goes away and ur-afk resumes
    /// covering those alts immediately.
    [Fact]
    public void Stop_DeletesTheClaim()
    {
        var claim = new ClaimFile(Path_);
        claim.Start(new[] { 111L });
        Assert.True(File.Exists(Path_));

        claim.Stop();
        Assert.False(File.Exists(Path_));
    }

    [Fact]
    public void Start_IsAtomic_NeverLeavesATornFile()
    {
        using var claim = new ClaimFile(Path_);
        claim.Start(new[] { 111L });
        // A reader must always get valid JSON — written temp-then-move, never in place.
        var json = File.ReadAllText(Path_);
        var doc = JsonSerializer.Deserialize<JsonElement>(json);
        Assert.Equal("ur-task", doc.GetProperty("plugin").GetString());
    }
}
```

- [ ] **Step 2: Run test to verify it fails**

Run: `dotnet test tests/rororo-ur-task.Tests/ --filter ClaimFileTests`
Expected: FAIL — `ClaimFile` does not exist.

- [ ] **Step 3: Implement**

Create `src/PluginHost/ClaimFile.cs`:

```csharp
using System.IO;
using System.Text.Json;
using System.Text.Json.Serialization;

namespace Labs626.UrTask.PluginHost;

/// <summary>
/// Publishes which accounts Ur Task is actively managing, so ur-afk (the fallback
/// keep-alive) stays off them and the two plugins don't both steal foreground to tap
/// the same alt.
///
/// Fails SAFE: a stale heartbeat (we crashed) or a missing file (we're not running)
/// both mean "Ur Task isn't covering these — fallback, take over." Refreshed every
/// 20s against a 60s TTL, so one slow tick never looks like a crash. Deleted on a
/// clean stop.
///
/// Deliberately shaped like the host-brokered claim registry the family will need when
/// Ur Reset lands (plugin / heartbeat / owned) — this file is that registry's first
/// implementation.
/// </summary>
internal sealed class ClaimFile : IDisposable
{
    private const int TtlSeconds = 60;
    private static readonly TimeSpan RefreshEvery = TimeSpan.FromSeconds(20);

    private readonly string _path;
    private readonly object _gate = new();
    private Timer? _heartbeat;
    private long[] _owned = [];

    public ClaimFile() : this(DefaultPath) { }
    public ClaimFile(string path) => _path = path;

    public static string DefaultPath => Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
        "626Labs", "claims", "ur-task.json");

    public void Start(IEnumerable<long> ownedUserIds)
    {
        lock (_gate)
        {
            _owned = ownedUserIds.ToArray();
            Write();
            _heartbeat?.Dispose();
            _heartbeat = new Timer(_ => { try { lock (_gate) Write(); } catch { } },
                                   null, RefreshEvery, RefreshEvery);
        }
    }

    public void Stop()
    {
        lock (_gate)
        {
            _heartbeat?.Dispose();
            _heartbeat = null;
            try { File.Delete(_path); } catch { /* best effort — TTL expiry covers us */ }
        }
    }

    private void Write()
    {
        var dir = Path.GetDirectoryName(_path)!;
        Directory.CreateDirectory(dir);
        var payload = new ClaimPayload("ur-task", DateTime.UtcNow, TtlSeconds, _owned);
        var json = JsonSerializer.Serialize(payload);

        // Temp-then-move: a reader must never catch a half-written file.
        var tmp = _path + ".tmp";
        File.WriteAllText(tmp, json);
        File.Move(tmp, _path, overwrite: true);
    }

    public void Dispose() => Stop();

    private sealed record ClaimPayload(
        [property: JsonPropertyName("plugin")] string Plugin,
        [property: JsonPropertyName("heartbeatUtc")] DateTime HeartbeatUtc,
        [property: JsonPropertyName("ttlSeconds")] int TtlSeconds,
        [property: JsonPropertyName("ownedUserIds")] long[] OwnedUserIds);
}
```

In `src/PluginRuntime.cs`, hold a `ClaimFile` field. Where the round-robin starts (the
same place `_runner.RunAsync` is kicked off, near line 541), call
`_claim.Start(assignments.Select(a => a.Alt.RobloxUserId))`. Where the runner is aborted /
stopped (the `Abort` case near line 538 and the `Stopped` progress phase), call
`_claim.Stop()`. A failed claim write must **never** stop the cadence loop — the whole
class swallows I/O errors by design.

- [ ] **Step 4: Run tests to verify they pass**

Run: `dotnet build rororo-ur-task.csproj -c Debug` → 0 errors
Run: `dotnet test tests/rororo-ur-task.Tests/ --filter ClaimFileTests`
Expected: PASS (3 tests)

- [ ] **Step 5: Commit**

```bash
git add src/PluginHost/ClaimFile.cs src/PluginRuntime.cs tests/rororo-ur-task.Tests/ClaimFileTests.cs
git commit -m "feat(cadence): heartbeat claim file so ur-afk stays off managed alts"
```

---

### Task 8: UI — role toggle, presets, next-due countdown

**Files:**
- Modify: `src/UI/AssignmentRow.cs`
- Modify: `src/UI/RecorderViewModel.cs`
- Modify: `src/UI/RecorderWindow.xaml` (the ASSIGNMENTS section)
- Test: `tests/rororo-ur-task.Tests/AssignmentRowTests.cs` (extend — the file exists)

**Interfaces:**
- Consumes: `CadenceRole` (Task 1).
- Produces: `AssignmentRow.Role` (`CadenceRole`, raises `PropertyChanged`); `AssignmentRow.NextDueText` (`string`, e.g. `"next: 8m"`, empty for Active rows); `RecorderViewModel.SetAllActiveCommand`; `RecorderViewModel.FocusOneCommand`.

- [ ] **Step 1: Write the failing test**

Add to `tests/rororo-ur-task.Tests/AssignmentRowTests.cs`:

```csharp
    // AssignmentRow's real shape: ctor takes the alt; the macro is the settable
    // `AssignedMacro` property (Macro?), NOT an id.
    private static AssignmentRow Row(int pid = 1)
        => new(new AccountRegistry.AccountInfo(pid, pid, $"alt{pid}", $"acct-{pid}"));

    private static Macro NewMacro() => new(
        SchemaVersion: 3, Id: Guid.NewGuid().ToString(), Name: "farm",
        RecordMode: "PerWindow", RecordedAgainstUserId: null,
        RecordedAgainstDisplayName: null, InterAltDelayMs: null,
        RecordedAtUnixMs: 0, Events: new List<MacroEvent>());

    [Fact]
    public void Role_RaisesPropertyChanged()
    {
        var row = Row();
        var raised = new List<string?>();
        row.PropertyChanged += (_, e) => raised.Add(e.PropertyName);

        row.Role = CadenceRole.KeepAlive;

        Assert.Contains(nameof(AssignmentRow.Role), raised);
    }

    /// Backgrounding must NOT be destructive — the macro survives, merely paused.
    /// Flip the row back to Active and it farms again without re-picking anything.
    [Fact]
    public void SettingKeepAlive_DoesNotClearTheAssignedMacro()
    {
        var row = Row();
        var macro = NewMacro();
        row.AssignedMacro = macro;

        row.Role = CadenceRole.KeepAlive;

        Assert.Same(macro, row.AssignedMacro);
        Assert.True(row.HasMacro);
    }

    /// Proof-of-life: a keep-alive row shows when it next fires. Without this the
    /// scheduler is invisible — a quiet screen reads as "broken."
    [Fact]
    public void KeepAliveRow_ShowsNextDueCountdown()
    {
        var row = Row();
        row.Role = CadenceRole.KeepAlive;

        row.SetNextDue(TimeSpan.FromMinutes(8));

        Assert.Equal("next: 8m", row.NextDueText);
    }

    [Fact]
    public void ActiveRow_ShowsNoCountdown()
    {
        var row = Row();
        row.AssignedMacro = NewMacro();
        row.Role = CadenceRole.Active;

        row.SetNextDue(TimeSpan.FromMinutes(8));   // even if set, an Active row shows nothing

        Assert.Equal(string.Empty, row.NextDueText);
    }
```

- [ ] **Step 2: Run test to verify it fails**

Run: `dotnet test tests/rororo-ur-task.Tests/ --filter AssignmentRowTests`
Expected: FAIL — `Role` / `NextDueText` / `SetNextDue` do not exist.

- [ ] **Step 3: Implement**

`src/UI/AssignmentRow.cs` — add, following the file's existing `INotifyPropertyChanged` pattern:

```csharp
    private CadenceRole _role = CadenceRole.Active;
    public CadenceRole Role
    {
        get => _role;
        set { if (_role == value) return; _role = value; OnPropertyChanged(); OnPropertyChanged(nameof(NextDueText)); }
    }

    private TimeSpan? _nextDue;

    /// <summary>Proof-of-life for a sleeping scheduler. Empty for Active rows.</summary>
    public string NextDueText => Role == CadenceRole.KeepAlive && _nextDue is TimeSpan t
        ? $"next: {Math.Max(0, (int)Math.Round(t.TotalMinutes))}m"
        : string.Empty;

    public void SetNextDue(TimeSpan? due)
    {
        _nextDue = due;
        OnPropertyChanged(nameof(NextDueText));
    }
```

`src/UI/RecorderViewModel.cs`:
- Add `SetAllActiveCommand` — sets every `AssignmentRow.Role = CadenceRole.Active`.
- Add `FocusOneCommand(AssignmentRow focused)` — sets `focused.Role = Active` and every
  other row to `KeepAlive`. **Macros are not touched.**
- Drive `SetNextDue` from the existing `_runner.Progress` subscription (a keep-alive
  service resets that row's countdown) plus a low-frequency `DispatcherTimer` (30s is
  plenty — this is a minutes-scale countdown; do not tick it every second).

`src/UI/RecorderWindow.xaml` — in the ASSIGNMENTS section:
- Per row: a small Active/Keep-alive toggle bound to `Role`, and a muted `TextBlock` bound
  to `NextDueText`.
- Above the rows: two buttons, **"All equal"** (`SetAllActiveCommand`) and **"One focused,
  rest background"** (`FocusOneCommand` against the selected row).
- Follow the existing themed control styles in this file (there is a `ThemedComboBox` and
  the established button styling) — do **not** introduce new visual idioms.

- [ ] **Step 4: Run tests + eyeball**

Run: `dotnet build rororo-ur-task.csproj -c Debug` → 0 errors
Run: `dotnet test tests/rororo-ur-task.Tests/`
Expected: full suite green
Manually: launch, assign one alt a macro and leave another with none, confirm the second
shows `next: 12m` and the desktop is **not** being stolen every second.

- [ ] **Step 5: Commit**

```bash
git add src/UI/AssignmentRow.cs src/UI/RecorderViewModel.cs src/UI/RecorderWindow.xaml tests/rororo-ur-task.Tests/AssignmentRowTests.cs
git commit -m "feat(cadence): role toggle, presets, and next-due countdown in the grid"
```

---

## Final verification

- [ ] `dotnet build rororo-ur-task.csproj -c Debug` → 0 errors, 0 warnings
- [ ] `dotnet test tests/rororo-ur-task.Tests/` → green (the 2 `HotkeyServiceTests` failures are environmental — they occur only when a live Ur Task instance already holds the global hotkeys; confirm on CI, which runs clean)
- [ ] **The regression that matters:** `SingleKeepAliveAlt_OverASimulatedHour_IsServicedAboutFiveTimes_NotThousands` passes
- [ ] Live smoke: one alt on keep-alive → desktop is usable, focus is stolen roughly every 12 minutes for ~1 second and handed straight back
- [ ] Live smoke: one Active farming alt + one keep-alive alt → farming runs continuously, the keep-alive slots into a gap and farming resumes
- [ ] Live smoke: all-Active squad → round-robins back-to-back exactly as v0.6 did
- [ ] Claim file appears at `%LOCALAPPDATA%\626Labs\claims\ur-task.json` while running, disappears on stop

## Known follow-ups (out of scope)

- ur-afk's **consumption** of the claim file — companion change in the `rororo-ur-afk` repo.
- Populating real Roblox **PlaceIds** in the override map once we observe them from presence.
- Host-brokered claim registry (arrives with Ur Reset).
