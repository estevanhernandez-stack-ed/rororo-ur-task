# Recipes (position → loop) Implementation Plan

> **For agentic workers:** REQUIRED SUB-SKILL: Use superpowers:subagent-driven-development (recommended) or superpowers:executing-plans to implement this plan task-by-task. Steps use checkbox (`- [ ]`) syntax for tracking.

**Goal:** Let a user run an ordered recipe — position each alt once, then loop (or keep-alive) — across a selected set of accounts, replacing the manual "select-for-all, watch, then flip to the loop" babysit.

**Architecture:** A recipe is an ordered list of steps `(macroId, RunOnce|Loop|KeepAlive)` against a selected alt set. A thin `RecipeRunner` orchestrates two existing, tested runners: `SequencePlayer` runs each `RunOnce` step once-per-alt (its completion IS the barrier), then `AssignmentRunner` runs the terminal `Loop`/`KeepAlive` step round-robin forever. Persistence mirrors `MacroStore`; the editor is its own window, not dashboard clutter.

**Tech Stack:** .NET 10, WPF (MVVM), System.Text.Json, xUnit 2.9.3 (`[Fact]`, `Assert.*`; the test project has a global `<Using Include="Xunit" />` — no `using Xunit;` needed).

## Global Constraints

- Namespace: model/runner/store live in `Labs626.UrTask.Macros`; UI in `Labs626.UrTask.UI`; focus helper in `Labs626.UrTask.PluginHost`.
- Recipes reference macros **by id** (Guid string); never embed a macro. Macros stay owned by `MacroStore`.
- Recipe persistence: `%LOCALAPPDATA%\626Labs\RoRoRoUrTask\recipes\<id>.json`, one file per recipe, atomic tmp-then-rename (mirror `MacroStore`).
- Position-failure policy: **proceed-with-successes** — advance to the loop with whoever positioned; never block the squad on a stuck alt.
- Terminal step is `Loop` or `KeepAlive`; every earlier step is `RunOnce`. A `KeepAlive` terminal carries no macro (sends Space).
- v1 scope excludes: exclusive/active-loop fan-out, per-alt handoff (barrier only), recipe sharing/slots, Ur Reset.
- Commit after every green task. Do not push (Este pushes).

---

### Task 1: Port the foreground-lock focus fix into Ur Task (prerequisite)

ur-task's `Win32Focus.AttachAndFocus` uses AttachThreadInput + `SetForegroundWindow` only — which silently no-ops while the user is idle (the exact ur-afk v0.5.1→v0.5.2 bug). The position step focuses each alt while the user watches (idle), so it would silently `Refused`. Port ur-afk's hardened body verbatim (identical signature).

**Files:**
- Modify: `src/PluginHost/Win32Focus.cs` (replace the method body + add P/Invokes/constants)
- Reference (source of truth, do not modify): `../rororo-ur-afk/src/PluginHost/Win32Focus.cs`

**Interfaces:**
- Produces: `Win32Focus.AttachAndFocus(int pid) -> (bool ok, string? error)` — signature unchanged; behavior hardened. `SequencePlayer` and `AssignmentRunner` already call it via `Win32Focus.AttachAndFocus`.

- [ ] **Step 1: Replace `Win32Focus.cs` body with the hardened version**

Replace the whole file with (namespace kept as `Labs626.UrTask.PluginHost`):

```csharp
using System.Diagnostics;
using System.Runtime.InteropServices;

namespace Labs626.UrTask.PluginHost;

/// <summary>
/// Forces a target window to the foreground even when the user is idle. The
/// AttachThreadInput trick alone is NOT enough on modern Windows: with no recent
/// user input the foreground-lock timeout makes SetForegroundWindow silently
/// no-op. The remedy layers three moves: attach to the foreground thread's input
/// queue, temporarily zero the system foreground-lock timeout (restored right
/// after), and BringWindowToTop. Callers still verify the foreground actually
/// became the target pid before synthesizing input (the safety invariant), so a
/// focus that still fails degrades to a skipped action, never a stray keystroke.
/// Ported from ur-afk v0.5.2 (rororo-ur-afk/src/PluginHost/Win32Focus.cs).
/// </summary>
internal static class Win32Focus
{
    private const uint SPI_GETFOREGROUNDLOCKTIMEOUT = 0x2000;
    private const uint SPI_SETFOREGROUNDLOCKTIMEOUT = 0x2001;
    private const uint SPIF_SENDCHANGE = 0x02;
    private const int SW_RESTORE = 9;

    public static (bool ok, string? error) AttachAndFocus(int pid)
    {
        try
        {
            var hwnd = Process.GetProcessById(pid).MainWindowHandle;
            if (hwnd == IntPtr.Zero) return (false, "MainWindowHandle is null.");

            if (IsIconic(hwnd)) ShowWindow(hwnd, SW_RESTORE);

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
            }
            finally
            {
                if (loweredLock)
                    SystemParametersInfoSet(SPI_SETFOREGROUNDLOCKTIMEOUT, 0, new IntPtr(savedTimeout), SPIF_SENDCHANGE);
                if (attached) AttachThreadInput(fgThreadId, ourThreadId, false);
            }
            return (true, null);
        }
        catch (ArgumentException) { return (false, "Process not found (pid stale)."); }
        catch (Exception ex) { return (false, ex.Message); }
    }

    [DllImport("user32.dll")] [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool SetForegroundWindow(IntPtr hWnd);
    [DllImport("user32.dll")] [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool BringWindowToTop(IntPtr hWnd);
    [DllImport("user32.dll")]
    private static extern IntPtr GetForegroundWindow();
    [DllImport("user32.dll", CharSet = CharSet.Auto, SetLastError = true)]
    private static extern uint GetWindowThreadProcessId(IntPtr hWnd, out uint lpdwProcessId);
    [DllImport("user32.dll")] [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool AttachThreadInput(uint idAttach, uint idAttachTo, [MarshalAs(UnmanagedType.Bool)] bool fAttach);
    [DllImport("user32.dll")] [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool IsIconic(IntPtr hWnd);
    [DllImport("user32.dll")] [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool ShowWindow(IntPtr hWnd, int nCmdShow);
    [DllImport("user32.dll", EntryPoint = "SystemParametersInfoW", SetLastError = true)] [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool SystemParametersInfoGet(uint uiAction, uint uiParam, ref uint pvParam, uint fWinIni);
    [DllImport("user32.dll", EntryPoint = "SystemParametersInfoW", SetLastError = true)] [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool SystemParametersInfoSet(uint uiAction, uint uiParam, IntPtr pvParam, uint fWinIni);
    [DllImport("kernel32.dll")]
    private static extern uint GetCurrentThreadId();
}
```

- [ ] **Step 2: Build**

Run: `dotnet build src/rororo-ur-task.csproj -c Debug`
Expected: build succeeds (the two callers already use `AttachAndFocus`, signature unchanged).

> Win32 focus can't be unit-tested (no window server in CI). Verification is the build plus the live smoke in Task 7 (position step lands while the machine is idle). Do NOT add a fake test that asserts focus behavior.

- [ ] **Step 3: Commit**

```bash
git add src/PluginHost/Win32Focus.cs
git commit -m "fix(focus): port ur-afk v0.5.2 foreground-lock hardening into ur-task Win32Focus"
```

---

### Task 2: Recipe model

**Files:**
- Create: `src/Macros/Recipe.cs`
- Test: `tests/rororo-ur-task.Tests/RecipeTests.cs`

**Interfaces:**
- Produces:
  - `enum StepIteration { RunOnce, Loop, KeepAlive }`
  - `record RecipeStep(string? MacroId, StepIteration Iteration)`
  - `record Recipe(int SchemaVersion, string Id, string? Name, IReadOnlyList<RecipeStep> Steps, long RecordedAtUnixMs, long? RecordedPlaceId = null, string? RecordedGameName = null)` with `const int CurrentSchemaVersion = 1`, `RecipeStep Terminal => Steps[^1]`, `IEnumerable<RecipeStep> PositionSteps => Steps.Take(Steps.Count - 1)`, and `static (bool ok, string? error) ValidateSteps(IReadOnlyList<RecipeStep> steps)`.

- [ ] **Step 1: Write the failing tests**

Create `tests/rororo-ur-task.Tests/RecipeTests.cs`:

```csharp
using Labs626.UrTask.Macros;

namespace RoRoRo.UrTask.Tests;

public class RecipeTests
{
    private static RecipeStep Pos(string id) => new(id, StepIteration.RunOnce);

    [Fact]
    public void ValidateSteps_TerminalLoop_WithMacro_IsValid()
    {
        var steps = new[] { Pos("a"), new RecipeStep("b", StepIteration.Loop) };
        var (ok, error) = Recipe.ValidateSteps(steps);
        Assert.True(ok);
        Assert.Null(error);
    }

    [Fact]
    public void ValidateSteps_TerminalKeepAlive_NoMacro_IsValid()
    {
        var steps = new[] { Pos("a"), new RecipeStep(null, StepIteration.KeepAlive) };
        Assert.True(Recipe.ValidateSteps(steps).ok);
    }

    [Fact]
    public void ValidateSteps_Empty_IsInvalid()
        => Assert.False(Recipe.ValidateSteps(Array.Empty<RecipeStep>()).ok);

    [Fact]
    public void ValidateSteps_NonTerminalRunOnce_MustHaveMacro()
    {
        var steps = new[] { new RecipeStep(null, StepIteration.RunOnce), new RecipeStep("b", StepIteration.Loop) };
        Assert.False(Recipe.ValidateSteps(steps).ok);
    }

    [Fact]
    public void ValidateSteps_TerminalRunOnce_IsInvalid()
    {
        var steps = new[] { new RecipeStep("a", StepIteration.RunOnce) };
        Assert.False(Recipe.ValidateSteps(steps).ok);
    }

    [Fact]
    public void ValidateSteps_LoopTerminal_WithoutMacro_IsInvalid()
    {
        var steps = new[] { Pos("a"), new RecipeStep(null, StepIteration.Loop) };
        Assert.False(Recipe.ValidateSteps(steps).ok);
    }

    [Fact]
    public void TerminalAndPositionSteps_Partition()
    {
        var steps = new[] { Pos("a"), Pos("b"), new RecipeStep("c", StepIteration.Loop) };
        var recipe = new Recipe(Recipe.CurrentSchemaVersion, Guid.NewGuid().ToString(), "r", steps, 0);
        Assert.Equal(StepIteration.Loop, recipe.Terminal.Iteration);
        Assert.Equal(new[] { "a", "b" }, recipe.PositionSteps.Select(s => s.MacroId));
    }
}
```

- [ ] **Step 2: Run to verify it fails**

Run: `dotnet test tests/rororo-ur-task.Tests/ --filter FullyQualifiedName~RecipeTests`
Expected: FAIL — `Recipe` / `RecipeStep` / `StepIteration` do not exist.

- [ ] **Step 3: Write the model**

Create `src/Macros/Recipe.cs`:

```csharp
namespace Labs626.UrTask.Macros;

/// <summary>How a recipe step is played across the selected alts.</summary>
public enum StepIteration
{
    RunOnce,    // play once per alt (position); the step completes when all are done — the barrier
    Loop,       // round-robin the macro across alts forever (AssignmentRunner)
    KeepAlive,  // no macro; round-robin a Space keep-alive forever
}

/// <summary>One step of a recipe: a macro (by id) and how it iterates.
/// KeepAlive carries no macro.</summary>
public sealed record RecipeStep(string? MacroId, StepIteration Iteration);

/// <summary>
/// An ordered position→loop routine run against a selected alt set. Every step
/// but the last is <see cref="StepIteration.RunOnce"/> (position); the last
/// (terminal) step is <see cref="StepIteration.Loop"/> or
/// <see cref="StepIteration.KeepAlive"/> (the sustained state). Macros are
/// referenced by id and resolved against <see cref="MacroStore"/> at run time.
/// </summary>
public sealed record Recipe(
    int SchemaVersion,
    string Id,
    string? Name,
    IReadOnlyList<RecipeStep> Steps,
    long RecordedAtUnixMs,
    long? RecordedPlaceId = null,
    string? RecordedGameName = null)
{
    public const int CurrentSchemaVersion = 1;

    public RecipeStep Terminal => Steps[^1];
    public IEnumerable<RecipeStep> PositionSteps => Steps.Take(Steps.Count - 1);

    /// <summary>Enforce the shape: non-empty; all-but-last RunOnce with a macro;
    /// last is Loop (with a macro) or KeepAlive (macro optional).</summary>
    public static (bool ok, string? error) ValidateSteps(IReadOnlyList<RecipeStep> steps)
    {
        if (steps is null || steps.Count == 0) return (false, "A recipe needs at least a terminal step.");

        for (int i = 0; i < steps.Count - 1; i++)
        {
            if (steps[i].Iteration != StepIteration.RunOnce)
                return (false, $"Step {i + 1} must be a run-once position step.");
            if (string.IsNullOrEmpty(steps[i].MacroId))
                return (false, $"Position step {i + 1} needs a macro.");
        }

        var last = steps[^1];
        if (last.Iteration == StepIteration.RunOnce)
            return (false, "The last step must be a loop or keep-alive.");
        if (last.Iteration == StepIteration.Loop && string.IsNullOrEmpty(last.MacroId))
            return (false, "The loop step needs a macro.");
        return (true, null);
    }
}
```

- [ ] **Step 4: Run to verify it passes**

Run: `dotnet test tests/rororo-ur-task.Tests/ --filter FullyQualifiedName~RecipeTests`
Expected: PASS (7 tests).

- [ ] **Step 5: Commit**

```bash
git add src/Macros/Recipe.cs tests/rororo-ur-task.Tests/RecipeTests.cs
git commit -m "feat(recipes): Recipe/RecipeStep model + step-shape validation"
```

---

### Task 3: RecipeStore (persistence)

Mirror `MacroStore` exactly — one JSON file per recipe, atomic write, Guid-id guard, string-enum serialization.

**Files:**
- Create: `src/Macros/RecipeStore.cs`
- Test: `tests/rororo-ur-task.Tests/RecipeStoreTests.cs`

**Interfaces:**
- Consumes: `Recipe` (Task 2).
- Produces: `RecipeStore` with `RecipeStore(string directory)`, `static string DefaultDirectory()`, `void Save(Recipe)`, `LoadResult LoadAll()`, `void Delete(string recipeId)`, nested `record LoadResult(IReadOnlyList<Recipe> Recipes, IReadOnlyList<LoadFailure> Failures)` and `record LoadFailure(string Path, string Reason)`.

- [ ] **Step 1: Write the failing tests**

Create `tests/rororo-ur-task.Tests/RecipeStoreTests.cs`:

```csharp
using Labs626.UrTask.Macros;

namespace RoRoRo.UrTask.Tests;

public class RecipeStoreTests
{
    private static Recipe Sample(string id) => new(
        Recipe.CurrentSchemaVersion, id, "walk + mine",
        new[] { new RecipeStep("11111111-1111-1111-1111-111111111111", StepIteration.RunOnce),
                new RecipeStep("22222222-2222-2222-2222-222222222222", StepIteration.Loop) },
        RecordedAtUnixMs: 1000);

    [Fact]
    public void Save_Then_LoadAll_RoundTrips()
    {
        var dir = Path.Combine(Path.GetTempPath(), "urtask-recipes-" + Guid.NewGuid());
        var store = new RecipeStore(dir);
        var id = Guid.NewGuid().ToString();

        store.Save(Sample(id));
        var loaded = store.LoadAll();

        Assert.Empty(loaded.Failures);
        var back = Assert.Single(loaded.Recipes);
        Assert.Equal("walk + mine", back.Name);
        Assert.Equal(2, back.Steps.Count);
        Assert.Equal(StepIteration.Loop, back.Terminal.Iteration);
    }

    [Fact]
    public void Delete_RemovesFile()
    {
        var dir = Path.Combine(Path.GetTempPath(), "urtask-recipes-" + Guid.NewGuid());
        var store = new RecipeStore(dir);
        var id = Guid.NewGuid().ToString();
        store.Save(Sample(id));
        store.Delete(id);
        Assert.Empty(store.LoadAll().Recipes);
    }

    [Fact]
    public void LoadAll_MalformedFile_SurfacesAsFailure()
    {
        var dir = Path.Combine(Path.GetTempPath(), "urtask-recipes-" + Guid.NewGuid());
        Directory.CreateDirectory(dir);
        File.WriteAllText(Path.Combine(dir, "bad.json"), "{ not json");
        var loaded = new RecipeStore(dir).LoadAll();
        Assert.Empty(loaded.Recipes);
        Assert.Single(loaded.Failures);
    }
}
```

- [ ] **Step 2: Run to verify it fails**

Run: `dotnet test tests/rororo-ur-task.Tests/ --filter FullyQualifiedName~RecipeStoreTests`
Expected: FAIL — `RecipeStore` does not exist.

- [ ] **Step 3: Write RecipeStore**

Create `src/Macros/RecipeStore.cs`:

```csharp
using System.IO;
using System.Text.Json;
using System.Text.Json.Serialization;

namespace Labs626.UrTask.Macros;

/// <summary>
/// Disk-backed recipe library at
/// <c>%LOCALAPPDATA%\626Labs\RoRoRoUrTask\recipes\&lt;id&gt;.json</c>. One file per
/// recipe; atomic tmp-then-rename write. Mirrors <see cref="MacroStore"/>. Recipes
/// reference macros by id — resolved against MacroStore at run time.
/// </summary>
public sealed class RecipeStore
{
    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        WriteIndented = true,
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
        Converters = { new JsonStringEnumConverter() },
    };

    private readonly string _directory;

    public RecipeStore() : this(DefaultDirectory()) { }

    public RecipeStore(string directory)
    {
        _directory = directory ?? throw new ArgumentNullException(nameof(directory));
        System.IO.Directory.CreateDirectory(_directory);
    }

    public static string DefaultDirectory() => Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
        "626Labs", "RoRoRoUrTask", "recipes");

    public string Directory => _directory;

    public LoadResult LoadAll()
    {
        var loaded = new List<Recipe>();
        var failures = new List<LoadFailure>();
        foreach (var path in System.IO.Directory.EnumerateFiles(_directory, "*.json"))
        {
            try
            {
                var recipe = JsonSerializer.Deserialize<Recipe>(File.ReadAllText(path), JsonOptions);
                if (recipe is null) { failures.Add(new LoadFailure(path, "Deserialize returned null.")); continue; }
                loaded.Add(recipe);
            }
            catch (Exception ex) { failures.Add(new LoadFailure(path, ex.Message)); }
        }
        return new LoadResult(loaded, failures);
    }

    public void Save(Recipe recipe)
    {
        if (recipe is null) throw new ArgumentNullException(nameof(recipe));
        var target = PathFor(recipe.Id);
        var tmp = target + ".tmp";
        File.WriteAllText(tmp, JsonSerializer.Serialize(recipe, JsonOptions));
        if (File.Exists(target)) File.Delete(target);
        File.Move(tmp, target);
    }

    public void Delete(string recipeId)
    {
        var target = PathFor(recipeId);
        if (File.Exists(target)) File.Delete(target);
    }

    private string PathFor(string recipeId)
    {
        if (!Guid.TryParse(recipeId, out _))
            throw new ArgumentException("Recipe id must be a Guid.", nameof(recipeId));
        return Path.Combine(_directory, $"{recipeId}.json");
    }

    public sealed record LoadResult(IReadOnlyList<Recipe> Recipes, IReadOnlyList<LoadFailure> Failures);
    public sealed record LoadFailure(string Path, string Reason);
}
```

- [ ] **Step 4: Run to verify it passes**

Run: `dotnet test tests/rororo-ur-task.Tests/ --filter FullyQualifiedName~RecipeStoreTests`
Expected: PASS (3 tests).

- [ ] **Step 5: Commit**

```bash
git add src/Macros/RecipeStore.cs tests/rororo-ur-task.Tests/RecipeStoreTests.cs
git commit -m "feat(recipes): RecipeStore disk persistence (mirrors MacroStore)"
```

---

### Task 4: RecipeRunner (orchestration)

The core. Injected delegates keep it unit-testable without real Win32/gRPC — the same seam pattern `SequencePlayer`/`AssignmentRunner` use for focus.

**Files:**
- Create: `src/Macros/RecipeRunner.cs`
- Test: `tests/rororo-ur-task.Tests/RecipeRunnerTests.cs`

**Interfaces:**
- Consumes: `Recipe`, `RecipeStep`, `StepIteration` (Task 2); `SequenceResult`, `AltOutcome`, `PlaybackOutcome` (`SequenceTypes.cs`); `Assignment` (`AssignmentRunner.cs`); `AccountRegistry.AccountInfo`; `Macro`.
- Produces:
  - `delegate Task<SequenceResult> RunOnceDelegate(Macro macro, IReadOnlyList<AccountRegistry.AccountInfo> alts, CancellationToken ct)`
  - `delegate Task RunLoopDelegate(IReadOnlyList<Assignment> assignments, CancellationToken ct)`
  - `record RecipePhaseEvent(string StepLabel, int StepIndex, int TotalSteps, IReadOnlyList<AccountRegistry.AccountInfo> LiveAlts, RecipeRunPhase Phase)`
  - `enum RecipeRunPhase { Positioning, Looping, KeepAlive, AllAltsFailed, Done }`
  - `RecipeRunner(RunOnceDelegate runOnce, RunLoopDelegate runLoop, Func<string, Macro?> resolveMacro)` with `event EventHandler<RecipePhaseEvent>? Progress`, `Task RunAsync(Recipe recipe, IReadOnlyList<AccountRegistry.AccountInfo> selected, CancellationToken ct)`, `bool Abort()`.

- [ ] **Step 1: Write the failing tests**

Create `tests/rororo-ur-task.Tests/RecipeRunnerTests.cs`:

```csharp
using Labs626.UrTask.Macros;
using Labs626.UrTask.PluginHost;

namespace RoRoRo.UrTask.Tests;

public class RecipeRunnerTests
{
    private static AccountRegistry.AccountInfo Alt(int pid, long uid, string name)
        => new(pid, uid, name, Guid.NewGuid().ToString());

    private static Macro MacroWithId(string id)
        => new(Macro.CurrentSchemaVersion, id, id, null, null, null, null, 0, Array.Empty<MacroEvent>());

    // runOnce fake: report every alt Completed unless its uid is in `failUids`.
    private static RecipeRunner.RunOnceDelegate FakeRunOnce(
        List<string> log, HashSet<long>? failUids = null)
        => (macro, alts, ct) =>
        {
            log.Add($"runOnce:{macro.Id}:[{string.Join(",", alts.Select(a => a.RobloxUserId))}]");
            var per = alts.Select(a => new AltOutcome(a,
                failUids != null && failUids.Contains(a.RobloxUserId)
                    ? PlaybackOutcome.Refused : PlaybackOutcome.Completed, null)).ToList();
            int done = per.Count(p => p.Outcome == PlaybackOutcome.Completed);
            return Task.FromResult(new SequenceResult(per, done, per.Count - done, 0, TimeSpan.Zero));
        };

    private static RecipeRunner.RunLoopDelegate FakeRunLoop(List<string> log)
        => (assignments, ct) =>
        {
            log.Add($"loop:[{string.Join(",", assignments.Select(a => $"{a.Alt.RobloxUserId}:{a.Macro?.Id ?? "keepalive"}"))}]");
            return Task.CompletedTask;
        };

    [Fact]
    public async Task Position_Barriers_Then_Loop_Starts()
    {
        var log = new List<string>();
        var alts = new[] { Alt(1, 10, "a"), Alt(2, 20, "b") };
        var recipe = new Recipe(Recipe.CurrentSchemaVersion, Guid.NewGuid().ToString(), "r",
            new[] { new RecipeStep("pos", StepIteration.RunOnce), new RecipeStep("loop", StepIteration.Loop) }, 0);

        var runner = new RecipeRunner(FakeRunOnce(log), FakeRunLoop(log), MacroWithId);
        await runner.RunAsync(recipe, alts, CancellationToken.None);

        Assert.Equal(2, log.Count);
        Assert.Equal("runOnce:pos:[10,20]", log[0]);           // position first, all alts
        Assert.Equal("loop:[10:loop,20:loop]", log[1]);        // then loop, all alts, loop macro
    }

    [Fact]
    public async Task TerminalKeepAlive_PassesNullMacro()
    {
        var log = new List<string>();
        var alts = new[] { Alt(1, 10, "a") };
        var recipe = new Recipe(Recipe.CurrentSchemaVersion, Guid.NewGuid().ToString(), "r",
            new[] { new RecipeStep("pos", StepIteration.RunOnce), new RecipeStep(null, StepIteration.KeepAlive) }, 0);

        var runner = new RecipeRunner(FakeRunOnce(log), FakeRunLoop(log), MacroWithId);
        await runner.RunAsync(recipe, alts, CancellationToken.None);

        Assert.Equal("loop:[10:keepalive]", log[1]);
    }

    [Fact]
    public async Task PositionFailure_ProceedsWithSuccessesOnly()
    {
        var log = new List<string>();
        var alts = new[] { Alt(1, 10, "a"), Alt(2, 20, "b") };
        var recipe = new Recipe(Recipe.CurrentSchemaVersion, Guid.NewGuid().ToString(), "r",
            new[] { new RecipeStep("pos", StepIteration.RunOnce), new RecipeStep("loop", StepIteration.Loop) }, 0);

        var runner = new RecipeRunner(FakeRunOnce(log, failUids: new HashSet<long> { 20 }), FakeRunLoop(log), MacroWithId);
        await runner.RunAsync(recipe, alts, CancellationToken.None);

        Assert.Equal("loop:[10:loop]", log[1]);  // alt 20 dropped, squad not blocked
    }

    [Fact]
    public async Task AllPositionsFail_LoopNeverStarts()
    {
        var log = new List<string>();
        var alts = new[] { Alt(1, 10, "a") };
        var recipe = new Recipe(Recipe.CurrentSchemaVersion, Guid.NewGuid().ToString(), "r",
            new[] { new RecipeStep("pos", StepIteration.RunOnce), new RecipeStep("loop", StepIteration.Loop) }, 0);

        var runner = new RecipeRunner(FakeRunOnce(log, failUids: new HashSet<long> { 10 }), FakeRunLoop(log), MacroWithId);
        await runner.RunAsync(recipe, alts, CancellationToken.None);

        Assert.Single(log);                      // only the runOnce; no loop
        Assert.StartsWith("runOnce", log[0]);
    }
}
```

- [ ] **Step 2: Run to verify it fails**

Run: `dotnet test tests/rororo-ur-task.Tests/ --filter FullyQualifiedName~RecipeRunnerTests`
Expected: FAIL — `RecipeRunner` does not exist.

- [ ] **Step 3: Write RecipeRunner**

Create `src/Macros/RecipeRunner.cs`:

```csharp
using Labs626.UrTask.PluginHost;

namespace Labs626.UrTask.Macros;

public enum RecipeRunPhase { Positioning, Looping, KeepAlive, AllAltsFailed, Done }

public sealed record RecipePhaseEvent(
    string StepLabel, int StepIndex, int TotalSteps,
    IReadOnlyList<AccountRegistry.AccountInfo> LiveAlts, RecipeRunPhase Phase);

/// <summary>
/// Thin orchestrator: run each RunOnce (position) step once-per-alt and BARRIER on
/// its completion, carrying only the alts that positioned forward
/// (proceed-with-successes), then start the terminal Loop/KeepAlive step across the
/// survivors. Owns no Win32 — it drives two injected delegates that in production
/// wrap SequencePlayer (run-once) and AssignmentRunner (loop), so orchestration is
/// unit-testable with fakes.
/// </summary>
internal sealed class RecipeRunner
{
    public delegate Task<SequenceResult> RunOnceDelegate(
        Macro macro, IReadOnlyList<AccountRegistry.AccountInfo> alts, CancellationToken ct);
    public delegate Task RunLoopDelegate(
        IReadOnlyList<Assignment> assignments, CancellationToken ct);

    private readonly RunOnceDelegate _runOnce;
    private readonly RunLoopDelegate _runLoop;
    private readonly Func<string, Macro?> _resolveMacro;
    private CancellationTokenSource? _activeCts;

    public RecipeRunner(RunOnceDelegate runOnce, RunLoopDelegate runLoop, Func<string, Macro?> resolveMacro)
    {
        _runOnce = runOnce ?? throw new ArgumentNullException(nameof(runOnce));
        _runLoop = runLoop ?? throw new ArgumentNullException(nameof(runLoop));
        _resolveMacro = resolveMacro ?? throw new ArgumentNullException(nameof(resolveMacro));
    }

    public event EventHandler<RecipePhaseEvent>? Progress;

    public bool IsRunning => _activeCts is not null;

    public async Task RunAsync(
        Recipe recipe,
        IReadOnlyList<AccountRegistry.AccountInfo> selected,
        CancellationToken external = default)
    {
        if (recipe is null) throw new ArgumentNullException(nameof(recipe));
        if (selected is null || selected.Count == 0) return;

        var cts = CancellationTokenSource.CreateLinkedTokenSource(external);
        if (Interlocked.CompareExchange(ref _activeCts, cts, null) is not null) { cts.Dispose(); return; }
        var ct = cts.Token;

        try
        {
            var live = selected.ToList();
            var positionSteps = recipe.PositionSteps.ToList();

            for (int i = 0; i < positionSteps.Count; i++)
            {
                ct.ThrowIfCancellationRequested();
                var macro = _resolveMacro(positionSteps[i].MacroId!);
                if (macro is null) continue; // unresolved macro id — skip the step, keep the squad

                Emit(new RecipePhaseEvent($"Positioning ({i + 1})", i, recipe.Steps.Count, live, RecipeRunPhase.Positioning));
                var result = await _runOnce(macro, live, ct).ConfigureAwait(false);

                // proceed-with-successes: only alts that Completed carry forward
                live = result.PerAlt
                    .Where(o => o.Outcome == PlaybackOutcome.Completed)
                    .Select(o => o.Alt)
                    .ToList();

                if (live.Count == 0)
                {
                    Emit(new RecipePhaseEvent("All alts failed to position", i, recipe.Steps.Count, live, RecipeRunPhase.AllAltsFailed));
                    return;
                }
            }

            ct.ThrowIfCancellationRequested();
            var terminal = recipe.Terminal;
            var terminalMacro = terminal.Iteration == StepIteration.Loop
                ? _resolveMacro(terminal.MacroId!)
                : null; // KeepAlive → null macro → AssignmentRunner sends Space
            var assignments = live.Select(a => new Assignment(a, terminalMacro)).ToList();

            Emit(new RecipePhaseEvent(
                terminal.Iteration == StepIteration.Loop ? "Looping" : "Keep-alive",
                recipe.Steps.Count - 1, recipe.Steps.Count, live,
                terminal.Iteration == StepIteration.Loop ? RecipeRunPhase.Looping : RecipeRunPhase.KeepAlive));

            await _runLoop(assignments, ct).ConfigureAwait(false); // runs until cancelled
            Emit(new RecipePhaseEvent("Done", recipe.Steps.Count, recipe.Steps.Count, live, RecipeRunPhase.Done));
        }
        catch (OperationCanceledException) { /* aborted — expected */ }
        finally
        {
            cts.Dispose();
            _activeCts = null;
        }
    }

    public bool Abort()
    {
        var cts = _activeCts;
        if (cts is null) return false;
        try { cts.Cancel(); } catch (ObjectDisposedException) { }
        return true;
    }

    private void Emit(RecipePhaseEvent e)
    {
        try { Progress?.Invoke(this, e); } catch { /* subscriber bugs don't kill the run */ }
    }
}
```

> Note: `RecipeRunner` is `internal`; the test project already sees internals of the plugin assembly (existing tests touch `MacroStore`/`AssignmentRunner`). If a fresh `InternalsVisibleTo` is somehow missing, add it to the plugin csproj — but the existing tests prove it's already there.

- [ ] **Step 4: Run to verify it passes**

Run: `dotnet test tests/rororo-ur-task.Tests/ --filter FullyQualifiedName~RecipeRunnerTests`
Expected: PASS (4 tests).

- [ ] **Step 5: Commit**

```bash
git add src/Macros/RecipeRunner.cs tests/rororo-ur-task.Tests/RecipeRunnerTests.cs
git commit -m "feat(recipes): RecipeRunner — barrier position steps then terminal loop/keep-alive"
```

---

### Task 5: Select-all / select-none on the target picker

**Files:**
- Modify: `src/UI/PlaybackTargetPickerViewModel.cs`
- Modify: `src/UI/PlaybackTargetPickerWindow.xaml` (two buttons; mirror existing button styling in that file)
- Test: `tests/rororo-ur-task.Tests/PlaybackTargetPickerViewModelTests.cs`

**Interfaces:**
- Produces: `PlaybackTargetPickerViewModel.SelectAll()` and `.SelectNone()`; both no-op in single-select mode except `SelectNone` which always clears.

- [ ] **Step 1: Write the failing tests**

Create `tests/rororo-ur-task.Tests/PlaybackTargetPickerViewModelTests.cs`:

```csharp
using Labs626.UrTask.PluginHost;
using Labs626.UrTask.UI;

namespace RoRoRo.UrTask.Tests;

public class PlaybackTargetPickerViewModelTests
{
    private static AccountRegistry.AccountInfo Alt(long uid)
        => new((int)uid, uid, $"alt{uid}", Guid.NewGuid().ToString());

    [Fact]
    public void SelectAll_MultiSelect_SelectsEveryAlt()
    {
        var alts = new[] { Alt(1), Alt(2), Alt(3) };
        var vm = new PlaybackTargetPickerViewModel(alts, preferredUserId: null, multiSelect: true);
        vm.SelectAll();
        Assert.Equal(3, vm.SelectedTargets.Count);
        Assert.True(vm.CanPlay);
    }

    [Fact]
    public void SelectNone_ClearsSelection()
    {
        var alts = new[] { Alt(1), Alt(2) };
        var vm = new PlaybackTargetPickerViewModel(alts, preferredUserId: null, multiSelect: true);
        vm.SelectAll();
        vm.SelectNone();
        Assert.Empty(vm.SelectedTargets);
        Assert.False(vm.CanPlay);
    }

    [Fact]
    public void SelectAll_SingleSelect_IsNoOp()
    {
        var alts = new[] { Alt(1), Alt(2) };
        var vm = new PlaybackTargetPickerViewModel(alts, preferredUserId: null, multiSelect: false);
        vm.SelectAll();
        Assert.True(vm.SelectedTargets.Count <= 1);
    }
}
```

- [ ] **Step 2: Run to verify it fails**

Run: `dotnet test tests/rororo-ur-task.Tests/ --filter FullyQualifiedName~PlaybackTargetPickerViewModelTests`
Expected: FAIL — `SelectAll` / `SelectNone` do not exist.

- [ ] **Step 3: Add the methods**

In `src/UI/PlaybackTargetPickerViewModel.cs`, add after `Toggle(...)`:

```csharp
    /// <summary>Select every alt (multi-select only; no-op in single-select).</summary>
    public void SelectAll()
    {
        if (!MultiSelect) return;
        foreach (var alt in Alts)
            if (!_selection.Any(a => a.RobloxUserId == alt.RobloxUserId))
                _selection.Add(alt);
        RaiseSelectionChanged();
    }

    /// <summary>Clear the selection.</summary>
    public void SelectNone()
    {
        if (_selection.Count == 0) return;
        _selection.Clear();
        RaiseSelectionChanged();
    }

    private void RaiseSelectionChanged()
    {
        OnPropertyChanged(nameof(SelectedTargets));
        OnPropertyChanged(nameof(CanPlay));
        OnPropertyChanged(nameof(PlayButtonLabel));
    }
```

Then refactor the three `OnPropertyChanged` calls at the end of `Toggle(...)` to call `RaiseSelectionChanged();` (DRY — same three notifications).

- [ ] **Step 4: Run to verify it passes**

Run: `dotnet test tests/rororo-ur-task.Tests/ --filter FullyQualifiedName~PlaybackTargetPickerViewModelTests`
Expected: PASS (3 tests).

- [ ] **Step 5: Wire buttons into the picker window (visual, no test)**

In `src/UI/PlaybackTargetPickerWindow.xaml`, add a small row above the alt list, visible only in multi-select (bind `Visibility` to `MultiSelect` via the existing bool→Visibility converter used elsewhere in the file):

```xml
<StackPanel Orientation="Horizontal" Margin="0,0,0,6">
    <Button Content="Select all"  Click="SelectAll_Click"  Margin="0,0,6,0"/>
    <Button Content="Select none" Click="SelectNone_Click"/>
</StackPanel>
```

In `src/UI/PlaybackTargetPickerWindow.xaml.cs`, add handlers that call `_vm.SelectAll()` / `_vm.SelectNone()` then refresh the row visuals the same way the existing `Toggle` click path does (call the existing per-row refresh/`OrderTag` update method — mirror the existing item-click handler in that file).

- [ ] **Step 6: Build + commit**

Run: `dotnet build src/rororo-ur-task.csproj -c Debug` → succeeds.

```bash
git add src/UI/PlaybackTargetPickerViewModel.cs src/UI/PlaybackTargetPickerWindow.xaml src/UI/PlaybackTargetPickerWindow.xaml.cs tests/rororo-ur-task.Tests/PlaybackTargetPickerViewModelTests.cs
git commit -m "feat(recipes): select-all/select-none on the multi-select target picker"
```

---

### Task 6: Recipe editor (ViewModel + window)

The isolated authoring surface. The VM holds all logic (testable); the window is boilerplate that mirrors the existing `RecorderWindow` pattern for styling/theming.

**Files:**
- Create: `src/UI/RecipeEditorViewModel.cs`
- Create: `src/UI/RecipeEditorWindow.xaml` + `src/UI/RecipeEditorWindow.xaml.cs`
- Test: `tests/rororo-ur-task.Tests/RecipeEditorViewModelTests.cs`

**Interfaces:**
- Consumes: `Recipe`, `RecipeStep`, `StepIteration`, `Macro`, `MacroGameFilter` (game badge/mismatch).
- Produces: `RecipeEditorViewModel(IReadOnlyList<Macro> library)` with observable `Steps`, `AddPositionStep(string macroId)`, `SetTerminal(StepIteration mode, string? macroId)`, `RemoveStep(int index)`, `bool CanSave` (delegates to `Recipe.ValidateSteps`), `string? ValidationError`, and `Recipe Build(string id, string? name, long nowUnixMs)`.

- [ ] **Step 1: Write the failing tests**

Create `tests/rororo-ur-task.Tests/RecipeEditorViewModelTests.cs`:

```csharp
using Labs626.UrTask.Macros;
using Labs626.UrTask.UI;

namespace RoRoRo.UrTask.Tests;

public class RecipeEditorViewModelTests
{
    private static Macro M(string id, string name)
        => new(Macro.CurrentSchemaVersion, id, name, null, null, null, null, 0, Array.Empty<MacroEvent>());

    [Fact]
    public void CanSave_FalseUntilTerminalSet()
    {
        var vm = new RecipeEditorViewModel(new[] { M("11111111-1111-1111-1111-111111111111", "walk") });
        Assert.False(vm.CanSave);
        vm.AddPositionStep("11111111-1111-1111-1111-111111111111");
        Assert.False(vm.CanSave); // still no terminal
        vm.SetTerminal(StepIteration.KeepAlive, null);
        Assert.True(vm.CanSave);
    }

    [Fact]
    public void Build_ProducesValidRecipe()
    {
        var vm = new RecipeEditorViewModel(new[] {
            M("11111111-1111-1111-1111-111111111111", "walk"),
            M("22222222-2222-2222-2222-222222222222", "mine") });
        vm.AddPositionStep("11111111-1111-1111-1111-111111111111");
        vm.SetTerminal(StepIteration.Loop, "22222222-2222-2222-2222-222222222222");

        var recipe = vm.Build(Guid.NewGuid().ToString(), "walk + mine", nowUnixMs: 123);
        Assert.True(Recipe.ValidateSteps(recipe.Steps).ok);
        Assert.Equal(StepIteration.Loop, recipe.Terminal.Iteration);
        Assert.Equal(123, recipe.RecordedAtUnixMs);
    }
}
```

- [ ] **Step 2: Run to verify it fails**

Run: `dotnet test tests/rororo-ur-task.Tests/ --filter FullyQualifiedName~RecipeEditorViewModelTests`
Expected: FAIL — `RecipeEditorViewModel` does not exist.

- [ ] **Step 3: Write the ViewModel**

Create `src/UI/RecipeEditorViewModel.cs`:

```csharp
using System.Collections.ObjectModel;
using System.ComponentModel;
using System.Runtime.CompilerServices;
using Labs626.UrTask.Macros;

namespace Labs626.UrTask.UI;

/// <summary>Authoring state for one recipe: an ordered list of position steps plus
/// a terminal loop/keep-alive step, built from the macro library. All logic here;
/// the window is a thin view over it.</summary>
internal sealed class RecipeEditorViewModel : INotifyPropertyChanged
{
    private readonly Dictionary<string, Macro> _byId;

    public RecipeEditorViewModel(IReadOnlyList<Macro> library)
    {
        _byId = library.ToDictionary(m => m.Id);
        Library = new ObservableCollection<Macro>(library);
    }

    public ObservableCollection<Macro> Library { get; }
    public ObservableCollection<RecipeStep> Steps { get; } = new();

    public string? Name { get; set; }

    public void AddPositionStep(string macroId)
    {
        // a new position step must land before the terminal step, if one exists
        var step = new RecipeStep(macroId, StepIteration.RunOnce);
        if (Steps.Count > 0 && Steps[^1].Iteration != StepIteration.RunOnce)
            Steps.Insert(Steps.Count - 1, step);
        else
            Steps.Add(step);
        Recompute();
    }

    public void SetTerminal(StepIteration mode, string? macroId)
    {
        if (mode is not (StepIteration.Loop or StepIteration.KeepAlive))
            throw new ArgumentException("Terminal must be Loop or KeepAlive.", nameof(mode));
        var terminal = new RecipeStep(mode == StepIteration.Loop ? macroId : null, mode);
        if (Steps.Count > 0 && Steps[^1].Iteration != StepIteration.RunOnce)
            Steps[^1] = terminal;   // replace existing terminal
        else
            Steps.Add(terminal);
        Recompute();
    }

    public void RemoveStep(int index)
    {
        if (index >= 0 && index < Steps.Count) { Steps.RemoveAt(index); Recompute(); }
    }

    public bool CanSave { get; private set; }
    public string? ValidationError { get; private set; }

    /// <summary>Macro display name for a step (game badge/mismatch reuse existing MacroGameFilter in the row template).</summary>
    public string StepMacroName(RecipeStep step)
        => step.Iteration == StepIteration.KeepAlive ? "Keep-alive (Space)"
         : (step.MacroId is not null && _byId.TryGetValue(step.MacroId, out var m) ? (m.Name ?? "(unnamed)") : "(missing macro)");

    public Recipe Build(string id, string? name, long nowUnixMs)
    {
        var placeStamp = Steps.Select(s => s.MacroId)
            .Where(mid => mid is not null && _byId.ContainsKey(mid))
            .Select(mid => _byId[mid!])
            .FirstOrDefault(m => m.RecordedPlaceId is > 0);
        return new Recipe(Recipe.CurrentSchemaVersion, id, name, Steps.ToList(), nowUnixMs,
            placeStamp?.RecordedPlaceId, placeStamp?.RecordedGameName);
    }

    private void Recompute()
    {
        var (ok, error) = Recipe.ValidateSteps(Steps.ToList());
        CanSave = ok;
        ValidationError = error;
        OnPropertyChanged(nameof(CanSave));
        OnPropertyChanged(nameof(ValidationError));
    }

    public event PropertyChangedEventHandler? PropertyChanged;
    private void OnPropertyChanged([CallerMemberName] string? name = null)
        => PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(name));
}
```

- [ ] **Step 4: Run to verify it passes**

Run: `dotnet test tests/rororo-ur-task.Tests/ --filter FullyQualifiedName~RecipeEditorViewModelTests`
Expected: PASS (2 tests).

- [ ] **Step 5: Build the window (visual, no unit test)**

Create `RecipeEditorWindow.xaml` + `.xaml.cs` mirroring `src/UI/RecorderWindow.xaml` for window chrome, theming (`DynamicResource` brush keys), and close/save buttons. Layout:
- Name textbox (binds `Name`).
- Alt-set selection: reuse the `PlaybackTargetPickerViewModel` multi-select + the Task 5 select-all/none buttons.
- Steps list (`ItemsControl` over `Steps`): each row shows `StepMacroName(step)` + an iteration badge; reuse `AssignmentRow`'s game-badge/mismatch template (bind against the step's resolved macro + selected alts' `PlaceId`).
- "Add position step" (macro picker from `Library`), "Set loop / keep-alive" (terminal picker).
- Save button `IsEnabled="{Binding CanSave}"`; validation text bound to `ValidationError`.
- Run button (wired in Task 7).

Verify by launching (Task 7's smoke covers this) — a WPF window has no unit test.

- [ ] **Step 6: Build + commit**

Run: `dotnet build src/rororo-ur-task.csproj -c Debug` → succeeds.

```bash
git add src/UI/RecipeEditorViewModel.cs src/UI/RecipeEditorWindow.xaml src/UI/RecipeEditorWindow.xaml.cs tests/rororo-ur-task.Tests/RecipeEditorViewModelTests.cs
git commit -m "feat(recipes): recipe editor window + view-model (isolated authoring surface)"
```

---

### Task 7: Entry point + wire the runner to real runners

Wire it together: a `New recipe` entry point that opens the editor; on Run, resolve the real `SequencePlayer`/`AssignmentRunner` into `RecipeRunner`'s delegates and execute against the selected alts; persist recipes via `RecipeStore`.

**Files:**
- Modify: the main host wiring — `src/PluginRuntime.cs` and/or the main window ViewModel (locate where `MacroStore`, `SequencePlayer`, `AssignmentRunner`, and the tray/menu are already constructed; follow that pattern).
- Modify: `src/UI/RecipeEditorWindow.xaml.cs` (Run/Save button handlers).

**Interfaces:**
- Consumes: everything above. `SequencePlayer.PlayAsync(macro, alts, interAltDelayMs: null, ct)`; `AssignmentRunner.RunAsync(assignments, ct)`; `MacroStore.LoadAll()`; `RecipeStore.Save/LoadAll`.

- [ ] **Step 1: Add a `RecipeStore` + `New recipe` entry point**

In the host wiring (where the existing tray menu / main commands live), construct a `RecipeStore` alongside the existing `MacroStore`, and add a `New recipe` command/menu item that opens `RecipeEditorWindow` seeded with `macroStore.LoadAll().Macros` and the live alt set from `AccountRegistry.Snapshot()`.

- [ ] **Step 2: Build `RecipeRunner` from the real runners on Run**

In `RecipeEditorWindow.xaml.cs`'s Run handler, compose the delegates over the existing runner instances:

```csharp
var macros = _macroStore.LoadAll().Macros.ToDictionary(m => m.Id);
var runner = new RecipeRunner(
    runOnce: (macro, alts, ct) => _sequencePlayer.PlayAsync(macro, alts, null, ct),
    runLoop: (assignments, ct) => _assignmentRunner.RunAsync(assignments, ct),
    resolveMacro: id => macros.TryGetValue(id, out var m) ? m : null);
runner.Progress += (_, e) => Dispatcher.Invoke(() => UpdateRecipeStatus(e)); // reuse the existing status-banner pattern
var recipe = _vm.Build(Guid.NewGuid().ToString(), _vm.Name, DateTimeOffset.UtcNow.ToUnixTimeMilliseconds());
_recipeStore.Save(recipe);
_ = runner.RunAsync(recipe, _selectedAlts, _recipeCts.Token);
```

(`_sequencePlayer`, `_assignmentRunner`, `_selectedAlts`, and the status-banner method already exist in the playback path — pass them into the window ctor the same way `PlaybackTargetPickerWindow` receives its collaborators. Abort wires to `runner.Abort()`.)

- [ ] **Step 3: Full build + run the test suite**

Run: `dotnet build src/rororo-ur-task.csproj -c Debug` → succeeds.
Run: `dotnet test tests/rororo-ur-task.Tests/` → all green (existing + the ~19 new tests).

- [ ] **Step 4: Live smoke (the real acceptance)**

Launch the plugin against ≥2 live alts. New recipe → pick a position macro (RunOnce) → set terminal Loop (or KeepAlive) → select all → Run. Confirm: all alts get positioned (barrier), then the loop/keep-alive starts; **step away and stay idle during positioning** to prove the Task 1 focus fix (no silent `Refused`). Abort stops cleanly.

- [ ] **Step 5: Commit**

```bash
git add -A
git commit -m "feat(recipes): New recipe entry point + wire RecipeRunner to SequencePlayer/AssignmentRunner"
```

---

## Self-Review

**Spec coverage:**
- Step model (RunOnce/Loop/KeepAlive) → Task 2. ✓
- Execution = SequencePlayer barrier + AssignmentRunner terminal → Task 4 (+ wiring Task 7). ✓
- Barrier default → Task 4 (position steps awaited before terminal). ✓
- Loop concurrency character (round-robin-able vs exclusive) → runner handles round-robin-able; exclusive = KeepAlive terminal + manual drive, which the model expresses (KeepAlive terminal). ✓
- Select-all/none → Task 5. ✓
- Isolated editor surface → Task 6. ✓
- Persistence (RecipeStore, macros-by-id) → Task 3. ✓
- Game-aware badges → Task 6 (reuse `MacroGameFilter`/AssignmentRow template). ✓
- Position-failure = proceed-with-successes → Task 4 (test `PositionFailure_ProceedsWithSuccessesOnly`). ✓
- Foreground-lock prerequisite → Task 1. ✓

**Placeholder scan:** No TBD/TODO. UI-window steps (Tasks 5 Step 5, 6 Step 5, 7) reference exact existing files to mirror (`RecorderWindow.xaml`, `AssignmentRow`, `PlaybackTargetPickerWindow`) rather than inventing XAML for files not yet read — legitimate "follow existing patterns," with the testable logic fully specified in the VMs.

**Type consistency:** `AttachAndFocus(int)->(bool,string?)` unchanged (Task 1). `SequenceResult.PerAlt`/`AltOutcome.Outcome`/`PlaybackOutcome.Completed` (Task 4) match `SequenceTypes.cs`. `Assignment(Alt, Macro?)` (Task 4) matches `AssignmentRunner.cs`. `Recipe.ValidateSteps` used consistently in Tasks 2/6. `StepIteration` values consistent across all tasks.

## Open verification note (for the implementer)

Two APIs are used from files not fully read during planning and must be confirmed on first use (they are load-bearing but low-risk):
- `AccountRegistry.AccountInfo` constructor arity — assumed `(int Pid, long RobloxUserId, string DisplayName, string AccountId)` (matches usage in `AssignmentRunner`/`AssignmentRow`). Confirm the exact positional shape when writing the test helpers; adjust the `Alt(...)` factories if it differs.
- The host wiring location (Task 7) — find where `SequencePlayer`/`AssignmentRunner`/`MacroStore` are constructed and follow that lifetime/DI pattern.
