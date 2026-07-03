# Crash Diagnostics Implementation Plan

> **For agentic workers:** REQUIRED SUB-SKILL: Use superpowers:subagent-driven-development (recommended) or superpowers:executing-plans to implement this plan task-by-task. Steps use checkbox (`- [ ]`) syntax for tracking.

**Goal:** File-based crash/hang evidence for the plugin — `DiagLog` sink, startup breadcrumbs + watchdog, log-then-crash-loud exception handlers, activity tee, tray "Open log folder", README truth fix.

**Architecture:** A static never-throw append-only sink (`DiagLog`) is the single evidence channel. `App.OnStartup` registers three exception handlers and a 30-second startup watchdog *before* any construction, then breadcrumbs each construction step; `PluginRuntime.Log` tees the existing activity stream into the same file. Spec: `docs/superpowers/specs/2026-07-03-crash-diagnostics-design.md`.

**Tech Stack:** .NET 10 / WPF, xunit 2.9.3, zero new NuGet dependencies.

## Global Constraints

- **Zero new NuGet dependencies.** Hand-rolled sink only.
- **Diagnostics never take the plugin down:** every `DiagLog` filesystem operation is wrapped; `Write` never throws. A failed write is lost (next write retries); an uncreatable directory disables the sink for the session.
- **Crash loud:** exception handlers log and never set `Handled` / never suppress. `TaskScheduler.UnobservedTaskException` is the one exception — it calls `SetObserved()` (behavior-preserving in .NET Core).
- **Log path:** `%LOCALAPPDATA%\626Labs\RoRoRoUrTask\logs\ur-task.log`, rollover at 1 MB to `ur-task.1.log` (old `.1` deleted; 2 files max).
- **Line format:** `yyyy-MM-dd HH:mm:ss.fff  <message>` (two spaces between timestamp and message).
- **Watchdog defaults:** 30 s threshold, repeat line every 60 s, background thread, no disposal.
- **CI runs `-p:StandaloneTestsOnly=true`** (no ROROROblox sibling checkout) — new tests must depend only on the plugin project.
- **Never construct `PluginRuntime` in a unit test.** Its ctor conditionally starts the real bridge pipe accept loop (`UserPreferences` has no test seam, `AcceptPluginRunRequests` defaults true) — the #20 liveness-hazard class inside the test runner.
- **Static-state discipline:** every test class that touches `DiagLog` carries `[Collection("DiagLog")]` so xunit serializes them.
- Branch: `feat/crash-diagnostics` (exists, spec committed). Conventional commits.

---

### Task 1: `DiagLog` — the sink

**Files:**
- Create: `src/Diagnostics/DiagLog.cs`
- Test: `tests/rororo-ur-task.Tests/DiagLogTests.cs`

**Interfaces:**
- Consumes: nothing (leaf).
- Produces: `internal static class DiagLog` in namespace `Labs626.UrTask.Diagnostics` with `static string Directory { get; set; }`, `static string CurrentLogPath { get; }`, `static string RolledLogPath { get; }`, `static void Write(string message)`, `internal static void ResetForTests()`. Tasks 2–5 call `DiagLog.Write` / `DiagLog.Directory`.

- [ ] **Step 1: Write the failing tests**

Create `tests/rororo-ur-task.Tests/DiagLogTests.cs`:

```csharp
using System.IO;
using Labs626.UrTask.Diagnostics;

namespace Labs626.UrTask.Tests;

// DiagLog holds static state (Directory + session-disable flag). Every test
// class that touches it joins this collection so xunit serializes them.
[CollectionDefinition("DiagLog")]
public class DiagLogCollection { }

[Collection("DiagLog")]
public class DiagLogTests : IDisposable
{
    private readonly string _dir;

    public DiagLogTests()
    {
        _dir = Path.Combine(Path.GetTempPath(), "urtask-diaglog-" + Guid.NewGuid().ToString("N"));
        DiagLog.Directory = _dir;
        DiagLog.ResetForTests();
    }

    public void Dispose()
    {
        try { Directory.Delete(_dir, recursive: true); } catch { }
    }

    [Fact]
    public void Write_CreatesFileWithTimestampPrefixedLine()
    {
        DiagLog.Write("hello diagnostics");

        var lines = File.ReadAllLines(DiagLog.CurrentLogPath);
        var line = Assert.Single(lines);
        Assert.Matches(@"^\d{4}-\d{2}-\d{2} \d{2}:\d{2}:\d{2}\.\d{3}  hello diagnostics$", line);
    }

    [Fact]
    public void Write_PastThreshold_RollsCurrentToDotOneAndStartsFresh()
    {
        Directory.CreateDirectory(_dir);
        File.WriteAllText(DiagLog.RolledLogPath, "old rolled content");
        File.WriteAllText(DiagLog.CurrentLogPath, new string('x', 1_000_001));

        DiagLog.Write("first line after roll");

        // Previous current file became the new .1; the stale .1 is gone.
        Assert.Equal(new string('x', 1_000_001), File.ReadAllText(DiagLog.RolledLogPath));
        var line = Assert.Single(File.ReadAllLines(DiagLog.CurrentLogPath));
        Assert.EndsWith("first line after roll", line);
    }

    [Fact]
    public void Write_UncreatableDirectory_DisablesForSessionWithoutThrowing()
    {
        // A path nested under a FILE can never be created as a directory.
        var blocker = Path.Combine(Path.GetTempPath(), "urtask-blocker-" + Guid.NewGuid().ToString("N"));
        File.WriteAllText(blocker, "not a directory");
        try
        {
            DiagLog.Directory = Path.Combine(blocker, "logs");
            DiagLog.Write("lost");            // must not throw; disables the session

            DiagLog.Directory = _dir;         // even a good dir stays dark until reset
            DiagLog.Write("still disabled");

            Assert.False(File.Exists(DiagLog.CurrentLogPath));
        }
        finally
        {
            try { File.Delete(blocker); } catch { }
        }
    }

    [Fact]
    public void Write_ConcurrentWriters_EveryLineIntact()
    {
        const int threads = 8, perThread = 50;
        Parallel.For(0, threads, t =>
        {
            for (var i = 0; i < perThread; i++) DiagLog.Write($"t{t} line {i}");
        });

        var lines = File.ReadAllLines(DiagLog.CurrentLogPath);
        Assert.Equal(threads * perThread, lines.Length);
        Assert.All(lines, l =>
            Assert.Matches(@"^\d{4}-\d{2}-\d{2} \d{2}:\d{2}:\d{2}\.\d{3}  t\d line \d+$", l));
    }
}
```

- [ ] **Step 2: Run tests to verify they fail**

Run: `dotnet test tests/rororo-ur-task.Tests/rororo-ur-task.Tests.csproj -p:StandaloneTestsOnly=true --filter "FullyQualifiedName~DiagLogTests"`
Expected: build FAILURE — `Labs626.UrTask.Diagnostics` / `DiagLog` do not exist.

- [ ] **Step 3: Write the implementation**

Create `src/Diagnostics/DiagLog.cs`:

```csharp
using System.IO;

namespace Labs626.UrTask.Diagnostics;

/// <summary>
/// Append-only diagnostics sink under %LOCALAPPDATA%\626Labs\RoRoRoUrTask\logs.
/// Open-append-close per write — no held handle, so users can copy the file
/// while the plugin runs and a crash never truncates it. Rolls at 1 MB into
/// ur-task.1.log (2 MB worst case on disk). Never throws: a failed write is
/// lost and the next one tries again; if the directory itself can't be
/// created, the sink disables itself for the session. Static because it must
/// be callable from App.OnStartup before any object exists and from exception
/// handlers when everything is broken.
/// </summary>
internal static class DiagLog
{
    private const long RollThresholdBytes = 1_000_000;
    private static readonly object Gate = new();
    private static bool _disabled;

    /// <summary>Log directory. Settable so tests repoint at a temp dir.</summary>
    public static string Directory { get; set; } = Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
        "626Labs", "RoRoRoUrTask", "logs");

    public static string CurrentLogPath => Path.Combine(Directory, "ur-task.log");
    public static string RolledLogPath => Path.Combine(Directory, "ur-task.1.log");

    public static void Write(string message)
    {
        lock (Gate)
        {
            if (_disabled) return;
            try
            {
                System.IO.Directory.CreateDirectory(Directory);
                RollIfNeeded();
                File.AppendAllText(CurrentLogPath,
                    $"{DateTime.Now:yyyy-MM-dd HH:mm:ss.fff}  {message}{Environment.NewLine}");
            }
            catch
            {
                // A failed write is lost — the next write tries again. If the
                // directory itself is uncreatable, disable for the session.
                if (!System.IO.Directory.Exists(Directory)) _disabled = true;
            }
        }
    }

    private static void RollIfNeeded()
    {
        var current = new FileInfo(CurrentLogPath);
        if (!current.Exists || current.Length <= RollThresholdBytes) return;
        File.Delete(RolledLogPath); // no-op when absent
        File.Move(CurrentLogPath, RolledLogPath);
    }

    /// <summary>Test seam — clears session-disable after tests repoint Directory.</summary>
    internal static void ResetForTests()
    {
        lock (Gate) { _disabled = false; }
    }
}
```

- [ ] **Step 4: Run tests to verify they pass**

Run: `dotnet test tests/rororo-ur-task.Tests/rororo-ur-task.Tests.csproj -p:StandaloneTestsOnly=true --filter "FullyQualifiedName~DiagLogTests"`
Expected: 4 passed.

- [ ] **Step 5: Commit**

```bash
git add src/Diagnostics/DiagLog.cs tests/rororo-ur-task.Tests/DiagLogTests.cs
git commit -m "feat(diag): DiagLog append-only sink — rollover, never-throw, session disable"
```

---

### Task 2: `StartupWatchdog`

**Files:**
- Create: `src/Diagnostics/StartupWatchdog.cs`
- Test: `tests/rororo-ur-task.Tests/StartupWatchdogTests.cs`

**Interfaces:**
- Consumes: `DiagLog.Write(string)` from Task 1.
- Produces: `internal sealed class StartupWatchdog` in `Labs626.UrTask.Diagnostics` with ctor `StartupWatchdog(TimeSpan? threshold = null, TimeSpan? repeat = null)` (defaults 30 s / 60 s, thread starts in ctor) and `void MarkComplete()`. Task 3 constructs it with defaults and calls `MarkComplete()` at the end of `OnStartup`.

- [ ] **Step 1: Write the failing tests**

Create `tests/rororo-ur-task.Tests/StartupWatchdogTests.cs`:

```csharp
using System.IO;
using Labs626.UrTask.Diagnostics;

namespace Labs626.UrTask.Tests;

[Collection("DiagLog")]
public class StartupWatchdogTests : IDisposable
{
    private readonly string _dir;

    public StartupWatchdogTests()
    {
        _dir = Path.Combine(Path.GetTempPath(), "urtask-watchdog-" + Guid.NewGuid().ToString("N"));
        DiagLog.Directory = _dir;
        DiagLog.ResetForTests();
    }

    public void Dispose()
    {
        try { Directory.Delete(_dir, recursive: true); } catch { }
    }

    [Fact]
    public void IncompleteStartup_WritesWatchdogLine()
    {
        var watchdog = new StartupWatchdog(
            threshold: TimeSpan.FromMilliseconds(50),
            repeat: TimeSpan.FromMilliseconds(50));
        try
        {
            // Poll with a generous deadline — thread scheduling, not logic,
            // decides exactly when the first line lands.
            var deadline = DateTime.UtcNow.AddSeconds(5);
            while (DateTime.UtcNow < deadline)
            {
                if (File.Exists(DiagLog.CurrentLogPath) &&
                    File.ReadAllText(DiagLog.CurrentLogPath).Contains("WATCHDOG:"))
                    return; // pass
                Thread.Sleep(20);
            }
            Assert.Fail("watchdog line never appeared");
        }
        finally
        {
            watchdog.MarkComplete(); // stop the thread before the next test repoints DiagLog
        }
    }

    [Fact]
    public void CompletedStartup_StaysSilent()
    {
        var watchdog = new StartupWatchdog(
            threshold: TimeSpan.FromMilliseconds(50),
            repeat: TimeSpan.FromMilliseconds(50));
        watchdog.MarkComplete();

        Thread.Sleep(300); // several threshold+repeat periods

        Assert.False(
            File.Exists(DiagLog.CurrentLogPath) &&
            File.ReadAllText(DiagLog.CurrentLogPath).Contains("WATCHDOG:"),
            "watchdog fired after MarkComplete");
    }
}
```

- [ ] **Step 2: Run tests to verify they fail**

Run: `dotnet test tests/rororo-ur-task.Tests/rororo-ur-task.Tests.csproj -p:StandaloneTestsOnly=true --filter "FullyQualifiedName~StartupWatchdogTests"`
Expected: build FAILURE — `StartupWatchdog` does not exist.

- [ ] **Step 3: Write the implementation**

Create `src/Diagnostics/StartupWatchdog.cs`:

```csharp
namespace Labs626.UrTask.Diagnostics;

/// <summary>
/// Affirmative evidence for exception-free startup hangs (the #20 class: a
/// synchronous spin inside the OnStartup ctor chain hangs the process
/// windowless with zero exceptions at process scope — exception handlers
/// never fire). A background thread sleeps past the threshold and, until
/// MarkComplete() is called, writes a WATCHDOG line every repeat interval so
/// the log self-reports "hung after last breadcrumb" instead of just
/// stopping. Background thread — dies with the process, nothing to dispose.
/// </summary>
internal sealed class StartupWatchdog
{
    private readonly TimeSpan _threshold;
    private readonly TimeSpan _repeat;
    private volatile bool _complete;

    public StartupWatchdog(TimeSpan? threshold = null, TimeSpan? repeat = null)
    {
        _threshold = threshold ?? TimeSpan.FromSeconds(30);
        _repeat = repeat ?? TimeSpan.FromMinutes(1);
        new Thread(Run) { IsBackground = true, Name = "RoRoRoUrTask-StartupWatchdog" }.Start();
    }

    /// <summary>Call at the end of App.OnStartup. Silences the watchdog.</summary>
    public void MarkComplete() => _complete = true;

    private void Run()
    {
        Thread.Sleep(_threshold);
        while (!_complete)
        {
            DiagLog.Write(
                $"WATCHDOG: startup not complete after {_threshold.TotalSeconds:0}s — hung after last breadcrumb above");
            Thread.Sleep(_repeat);
        }
    }
}
```

- [ ] **Step 4: Run tests to verify they pass**

Run: `dotnet test tests/rororo-ur-task.Tests/rororo-ur-task.Tests.csproj -p:StandaloneTestsOnly=true --filter "FullyQualifiedName~StartupWatchdogTests"`
Expected: 2 passed.

- [ ] **Step 5: Commit**

```bash
git add src/Diagnostics/StartupWatchdog.cs tests/rororo-ur-task.Tests/StartupWatchdogTests.cs
git commit -m "feat(diag): StartupWatchdog — affirmative evidence for exception-free startup hangs"
```

---

### Task 3: App wiring — handlers, header, breadcrumbs, watchdog, test-crash hooks

**Files:**
- Modify: `src/App.xaml.cs` (full replacement below — current file is 43 lines)

**Interfaces:**
- Consumes: `DiagLog.Write` (Task 1), `StartupWatchdog` (Task 2).
- Produces: nothing downstream. `URTASK_TEST_CRASH` env var (`dispatcher` | `hang`) is the manual-verify contract used in Task 7.

No unit test — everything here is process-scope (handlers, WPF lifetime). Verified by build now and the Task 7 manual checklist.

- [ ] **Step 1: Replace `src/App.xaml.cs`**

```csharp
using System.Windows;
using System.Windows.Threading;
using Labs626.UrTask.Diagnostics;
using Labs626.UrTask.UI;

namespace Labs626.UrTask;

public partial class App : Application
{
    private PluginRuntime? _runtime;
    private RecorderWindow? _window;
    private TrayService? _tray;
    private StartupWatchdog? _watchdog;

    protected override void OnStartup(StartupEventArgs e)
    {
        base.OnStartup(e);

        // Evidence layer first — handlers, session header, and watchdog exist
        // before any construction step can crash or hang (2026-07-03 spec).
        RegisterExceptionEvidence();

        var version = typeof(App).Assembly.GetName().Version?.ToString(3) ?? "?";
        DiagLog.Write($"=== RoRoRo Ur Task v{version} starting — pid {Environment.ProcessId}, " +
                      $"{Environment.OSVersion.VersionString}, .NET {Environment.Version} ===");
        _watchdog = new StartupWatchdog();

        // Manual-verify hook (spec §7). Checked only when the variable is set.
        var testCrash = Environment.GetEnvironmentVariable("URTASK_TEST_CRASH");
        if (testCrash == "hang")
        {
            DiagLog.Write("URTASK_TEST_CRASH=hang — blocking OnStartup deliberately");
            Thread.Sleep(Timeout.Infinite); // windowless hang; the watchdog reports it
        }

        DiagLog.Write("startup: runtime");
        _runtime = new PluginRuntime();
        DiagLog.Write("startup: view model");
        var vm = new RecorderViewModel(_runtime);
        DiagLog.Write("startup: window");
        _window = new RecorderWindow { DataContext = vm };
        DiagLog.Write("startup: tray");
        _tray = new TrayService(_window);
        _runtime.StateChanged += () => _tray.UpdateState(_runtime.State);

        DiagLog.Write("startup: window shown");
        _window.Show();

        // Connect to RoRoRo after the window is visible. If RoRoRo isn't running,
        // the UI surfaces the failure rather than blocking startup on it.
        DiagLog.Write("startup: StartAsync dispatched");
        _ = _runtime.StartAsync();

        if (testCrash == "dispatcher")
        {
            var timer = new DispatcherTimer { Interval = TimeSpan.FromSeconds(2) };
            timer.Tick += (_, _) =>
                throw new InvalidOperationException("URTASK_TEST_CRASH=dispatcher — deliberate test crash");
            timer.Start();
        }

        _watchdog.MarkComplete();
    }

    /// <summary>
    /// Log-then-crash-loud evidence handlers (host philosophy: silent crash is
    /// worse than loud crash — never set Handled, just leave a trace). Handlers
    /// alone can't see liveness bugs (#20) — that's the StartupWatchdog's job.
    /// </summary>
    private void RegisterExceptionEvidence()
    {
        DispatcherUnhandledException += (_, args) =>
            DiagLog.Write($"FATAL (dispatcher): {args.Exception}");

        AppDomain.CurrentDomain.UnhandledException += (_, args) =>
            DiagLog.Write($"FATAL (appdomain, terminating={args.IsTerminating}): {args.ExceptionObject}");

        TaskScheduler.UnobservedTaskException += (_, args) =>
        {
            DiagLog.Write($"UNOBSERVED task exception: {args.Exception}");
            args.SetObserved(); // behavior-preserving; evidence only
        };
    }

    protected override void OnExit(ExitEventArgs e)
    {
        try { _tray?.Dispose(); } catch { }
        try
        {
            _runtime?.DisposeAsync().AsTask().Wait(TimeSpan.FromSeconds(3));
        }
        catch
        {
            // best-effort shutdown — don't let cleanup throw on exit
        }
        // Absence of this line at the end of a session = crash or hang, not exit.
        DiagLog.Write($"exiting cleanly (code {e.ApplicationExitCode})");
        base.OnExit(e);
    }
}
```

- [ ] **Step 2: Build to verify**

Run: `dotnet build rororo-ur-task.csproj`
Expected: Build succeeded, 0 errors.

- [ ] **Step 3: Run the full standalone test suite (regression gate)**

Run: `dotnet test tests/rororo-ur-task.Tests/rororo-ur-task.Tests.csproj -p:StandaloneTestsOnly=true`
Expected: all tests pass.

- [ ] **Step 4: Commit**

```bash
git add src/App.xaml.cs
git commit -m "feat(diag): startup breadcrumbs + exception evidence + URTASK_TEST_CRASH hooks"
```

---

### Task 4: Activity tee in `PluginRuntime.Log`

**Files:**
- Modify: `src/PluginRuntime.cs:533-537` (the private `Log` helper)

**Interfaces:**
- Consumes: `DiagLog.Write` (Task 1).
- Produces: every activity-view line now also lands in the log file. No signature changes; no call sites change.

**No unit test — deliberate spec deviation.** Constructing `PluginRuntime` in a test process starts the real bridge pipe accept loop (`UserPreferences` reads the machine's real prefs file, `AcceptPluginRunRequests` defaults true) — the exact liveness-hazard class #20 fixed, inside the test runner. The tee is one line; Task 7's manual checklist asserts activity lines appear in the file.

- [ ] **Step 1: Apply the tee**

In `src/PluginRuntime.cs`, change the `Log` helper (currently at lines 533–537) from:

```csharp
    private void Log(string message)
    {
        var line = $"[{DateTime.Now:HH:mm:ss}] {message}";
        RaiseUI(() => StatusLogged?.Invoke(line));
    }
```

to:

```csharp
    private void Log(string message)
    {
        // Tee to the on-disk diagnostics file (full timestamps there); the
        // activity view keeps its short HH:mm:ss formatting.
        Diagnostics.DiagLog.Write(message);
        var line = $"[{DateTime.Now:HH:mm:ss}] {message}";
        RaiseUI(() => StatusLogged?.Invoke(line));
    }
```

- [ ] **Step 2: Build + regression suite**

Run: `dotnet build rororo-ur-task.csproj && dotnet test tests/rororo-ur-task.Tests/rororo-ur-task.Tests.csproj -p:StandaloneTestsOnly=true`
Expected: build succeeds; all tests pass.

- [ ] **Step 3: Commit**

```bash
git add src/PluginRuntime.cs
git commit -m "feat(diag): tee activity log to DiagLog"
```

---

### Task 5: Tray — "Open log folder"

**Files:**
- Modify: `src/UI/TrayService.cs` (`BuildMenu` + new helper)

**Interfaces:**
- Consumes: `DiagLog.Directory` (Task 1).
- Produces: tray context-menu entry. No unit test — `TaskbarIcon`/`ContextMenu` need a WPF STA lifetime; the handler body is three lines and Task 7 verifies it by clicking.

- [ ] **Step 1: Add the menu item**

In `src/UI/TrayService.cs`, add the using at the top of the file:

```csharp
using Labs626.UrTask.Diagnostics;
```

In `BuildMenu()`, after the "Show recorder" item is added and before `menu.Items.Add(new Separator());`, insert:

```csharp
        var openLogs = new MenuItem { Header = "Open log folder" };
        openLogs.Click += (_, _) => OpenLogFolder();
        menu.Items.Add(openLogs);
```

Add the helper method after `SurfaceWindow()`:

```csharp
    private static void OpenLogFolder()
    {
        try
        {
            System.IO.Directory.CreateDirectory(DiagLog.Directory);
            System.Diagnostics.Process.Start(new System.Diagnostics.ProcessStartInfo
            {
                FileName = DiagLog.Directory,
                UseShellExecute = true,
            });
        }
        catch
        {
            // Menu click — nowhere to report; the folder just doesn't open.
        }
    }
```

- [ ] **Step 2: Build to verify**

Run: `dotnet build rororo-ur-task.csproj`
Expected: Build succeeded, 0 errors.

- [ ] **Step 3: Commit**

```bash
git add src/UI/TrayService.cs
git commit -m "feat(tray): Open log folder menu item"
```

---

### Task 6: README rider

**Files:**
- Modify: `README.md` (install section line 35; new Troubleshooting section before `## License` at line 73)

**Interfaces:** none — docs only.

- [ ] **Step 1: Fix the old-host claim**

Replace this line (README.md line 35):

```markdown
You need RoRoRo installed first ([v1.4.3 or later](https://github.com/estevanhernandez-stack-ed/ROROROblox/releases)). Older hosts will refuse the install with a clear "Update RoRoRo" message.
```

with:

```markdown
You need RoRoRo installed first ([v1.4.3 or later](https://github.com/estevanhernandez-stack-ed/ROROROblox/releases)). Hosts 1.4.3+ refuse a too-new plugin with a clear "Update RoRoRo" message — but **hosts older than 1.4.3 (1.4.0–1.4.2) silently accept the install and then ask you to restart RoRoRo, and that restart is known to fail.** If installing ends with a "Restart RoRoRo" prompt, your RoRoRo is outdated: update it from the Microsoft Store, relaunch, and reinstall the plugin.
```

- [ ] **Step 2: Add the Troubleshooting section**

Insert immediately before `## License`:

```markdown
## Troubleshooting

- **The install told me to "Restart RoRoRo" (and restarting did nothing).** Your RoRoRo host is older than v1.4.3. Update RoRoRo from the Microsoft Store, relaunch it, and reinstall the plugin — on current hosts the plugin starts immediately after install, with no restart step.
- **Something crashed, hung, or silently stopped?** The plugin keeps a diagnostic log: right-click the Ur Task tray icon → **Open log folder** → drop `ur-task.log` into the Discord support thread. The log lives at `%LOCALAPPDATA%\626Labs\RoRoRoUrTask\logs` (2 files × 1 MB max).

```

- [ ] **Step 3: Commit**

```bash
git add README.md
git commit -m "docs(readme): old-host install truth + troubleshooting section"
```

---

### Task 7: Verification — full suite + manual evidence checklist

**Files:** none (verification only). REQUIRED SUB-SKILL for the claim of completion: `superpowers:verification-before-completion`.

- [ ] **Step 1: Full standalone test suite**

Run: `dotnet test tests/rororo-ur-task.Tests/rororo-ur-task.Tests.csproj -p:StandaloneTestsOnly=true`
Expected: all tests pass (existing suite + 6 new: 4 DiagLog, 2 StartupWatchdog).

- [ ] **Step 2: Manual — clean run leaves a healthy log**

Run (PowerShell, single line): `dotnet run --project rororo-ur-task.csproj`
Then quit via tray → "Quit RoRoRo Ur Task". Open `%LOCALAPPDATA%\626Labs\RoRoRoUrTask\logs\ur-task.log` and verify, in order: the `=== RoRoRo Ur Task v… starting` header; all six `startup: …` breadcrumbs; the teed activity lines (`Hotkeys ready: …`, `Loaded N macros …`, `Connecting to RoRoRo over named pipe...` and — with no host running — `Startup failed: …`); `exiting cleanly (code 0)` as the last line. No WATCHDOG lines.

- [ ] **Step 3: Manual — dispatcher crash leaves FATAL evidence**

Run (single line): `$env:URTASK_TEST_CRASH="dispatcher"; dotnet run --project rororo-ur-task.csproj; $env:URTASK_TEST_CRASH=$null`
Expected: window appears, process dies ~2 s later. Log ends with `FATAL (dispatcher): System.InvalidOperationException: URTASK_TEST_CRASH=dispatcher …` including a stack trace, and **no** `exiting cleanly` line after it.

- [ ] **Step 4: Manual — hang self-reports via watchdog**

Run (single line): `$env:URTASK_TEST_CRASH="hang"; dotnet run --project rororo-ur-task.csproj`
Expected: no window ever appears. After ~35 s the log shows the `URTASK_TEST_CRASH=hang` line followed by `WATCHDOG: startup not complete after 30s — hung after last breadcrumb above`. Kill the process from Task Manager afterwards, then clear the variable: `$env:URTASK_TEST_CRASH=$null`

- [ ] **Step 5: Manual — tray affordance**

Start the plugin, right-click the tray icon, click **Open log folder**. Expected: Explorer opens on `%LOCALAPPDATA%\626Labs\RoRoRoUrTask\logs` with `ur-task.log` present.

- [ ] **Step 6: Manual — HostLost self-termination is distinguishable from a crash**

With RoRoRo running and the plugin connected (launch the plugin from RoRoRo), kill RoRoRo via Task Manager. Expected log tail: `Host RoRoRo connection lost — aborting playback and exiting plugin.` followed by `exiting cleanly (code 0)` — clean exit, not FATAL.

- [ ] **Step 7: Record results**

Note any deviations in the session; all six steps green = feature complete on `feat/crash-diagnostics`, ready for `superpowers:finishing-a-development-branch`.
