# Crash diagnostics — file logging + unhandled-exception evidence — design

**Date:** 2026-07-03
**Status:** Approved (Este, this session)
**Target version:** v0.5 train (own PR, independent of #15/#16/#18)
**Repo:** rororo-ur-task

## Problem

The plugin has zero on-disk evidence of its own behavior. Today's support case
(user: "installed Ur Task → said RoRoRo needed to restart → restart does
nothing") was diagnosed as a host-side issue on RoRoRo 1.4.0–1.4.2 (decision
`rS9Cfx60moLeWf7kdUr0`) — but if it had been the *other* candidate, a plugin
startup crash on current hosts, we would have been blind:

- No file logging anywhere. `PluginRuntime.Log()` feeds the in-app activity
  view only — gone the moment the process dies.
- No unhandled-exception handlers. A throw anywhere in `App.OnStartup`'s
  constructor chain (`PluginRuntime` → `RecorderViewModel` → `RecorderWindow` →
  `TrayService` → `Show()`) kills the process with no trace.
- `StartAsync` swallows hotkey-registration and connect failures into the
  activity view; `OnHostLost` self-terminates with exit 0. From the host's
  side, every one of these looks identical: the "RoRoRo Ur Task stopped —
  click to restart" banner, looping.
- Exception handlers alone can't see **liveness bugs**. Proof from this same
  session (#20): launching a second Ur Task instance hard-hung it windowless
  forever — the bridge accept loop ran synchronously until its first await,
  the already-owned pipe threw *before* any await, and the swallow-and-retry
  loop spun the UI thread inside `new PluginRuntime()`. Zero exceptions ever
  reached process scope. A handler-only design is blind to this entire class;
  the evidence layer needs breadcrumbs plus a watchdog.

Remote diagnosis currently requires walking a Discord user through Event
Viewer. The fix: a crash-safe log file, exception handlers that leave evidence
before dying, and a one-click way for users to hand us the file.

## Approaches considered

- **A. Hand-rolled append-only sink** — ~60 lines, zero new dependencies,
  size-based rollover. Everything the plugin needs; nothing to track.
  **Chosen.**
- **B. Serilog + File sink** — battle-tested rolling/async logging, +2 NuGet
  deps. More capability than a two-file rollover needs; another dep to keep
  current. Rejected.
- **C. Windows Event Log as the evidence channel** — no file management, but
  support users can't find it (that's the problem being solved). Rejected.

## Decisions (made with Este)

| Decision | Choice | Rationale |
| --- | --- | --- |
| Logger | **Hand-rolled `DiagLog`** | Needs are append + roll; 60 lines beats a dependency. |
| Log scope | **Full activity tee + crashes** | The activity stream is already curated and low-volume. "Send me the log" then covers *every* support case — startup, connects, playback refusals — not just crashes. |
| Crash behavior | **Log, then crash loud** | Same philosophy as the host (`App.xaml.cs`: "Silent crash is worse than loud crash"). Handlers never set `Handled`; we just have evidence afterward. |
| Rollover | **2 files × 1 MB** | Worst case 2 MB on disk. Enough history for any support thread. |
| Liveness bugs (hangs) | **Breadcrumbs + startup watchdog** | #20 proved the exception path is blind to exception-free hangs. The watchdog turns "log suspiciously ends" into an affirmative "hung at step X" line. |
| Support affordance | **Tray → "Open log folder"** | One step from banner-loop report to log file in Discord. |
| Docs | **README rider in same PR** | Fix the false "older hosts will refuse the install" claim while we're here. |

## Design

### 1. `DiagLog` — the sink

New file `src/Diagnostics/DiagLog.cs`. Static class:

```csharp
internal static class DiagLog
{
    public static string Directory { get; set; } =   // test seam
        Path.Combine(Environment.GetFolderPath(
            Environment.SpecialFolder.LocalApplicationData),
            "626Labs", "RoRoRoUrTask", "logs");

    public static void Write(string message);
}
```

- **Path:** `%LOCALAPPDATA%\626Labs\RoRoRoUrTask\logs\ur-task.log` — sibling of
  the existing `macros` directory (`MacroStore.DefaultDirectory()` precedent).
- **Line format:** `yyyy-MM-dd HH:mm:ss.fff  <message>`. Full date in the file
  (sessions span days); the activity view keeps its own `HH:mm:ss` formatting.
- **Write strategy:** open-append-close per write, under a process-wide lock.
  No held file handle — users can copy the file while the plugin runs, and a
  crash never truncates it. Cross-process interleaving (two plugin instances
  racing during a host restart) is possible and tolerated — lines may
  interleave, never corrupt mid-line beyond OS append semantics.
- **Rollover:** checked at write time. When `ur-task.log` exceeds 1 MB: delete
  `ur-task.1.log` if present, rename current → `ur-task.1.log`, continue fresh.
- **Failure mode:** every filesystem operation is wrapped and never throws. A
  failed write is simply lost — the next write tries again. If the directory
  itself can't be created on first use, `DiagLog` disables itself for the
  session and all subsequent writes are no-ops. Diagnostics must never take
  the plugin down.
- **Static, not injected:** it must be callable from `App.OnStartup` before any
  object exists, and from exception handlers when everything is broken.
  Threading a logger through five constructors buys purity the use case
  doesn't need. Tests repoint `Directory` at a temp dir.

### 2. Startup breadcrumbs + watchdog

`App.OnStartup` writes a session header, then a one-line breadcrumb before
each construction step:

```text
=== RoRoRo Ur Task v0.5.0 starting — pid 4812, Windows 10.0.26100, .NET 10.0.1 ===
startup: runtime
startup: view model
startup: window
startup: tray
startup: window shown
startup: StartAsync dispatched
```

This is the direct fix for today's blind spot: a crash *or hang* anywhere in
the ctor chain is bisectable from the last breadcrumb in the file. `OnExit`
writes `exiting cleanly (code 0)` — its *absence* at the end of a session is
the crash discriminator (clean `HostLost` self-termination goes through
`OnExit`; a crash never does).

**Startup watchdog** — the affirmative evidence for the #20 class
(exception-free hangs the handlers in §3 can never see). First thing in
`OnStartup`, before any construction: start a background thread that sleeps
30 seconds, then checks a `volatile bool` the end of `OnStartup` sets. If
startup hasn't completed, it writes:

```text
WATCHDOG: startup not complete after 30s — hung after last breadcrumb above
```

then repeats the line once a minute while the hang persists. ~15 lines, one
flag, no timers to dispose (background thread dies with the process). Under
the #20 bug, the log would have read `startup: runtime` followed by watchdog
lines — a windowless hang self-reports instead of being inferred from a log
that just stops. Steady-state (post-startup) liveness monitoring is out of
scope — see non-goals.

### 3. Exception handlers

Registered in `App.OnStartup` before anything else, mirroring the host's
pattern:

- **`DispatcherUnhandledException`** — log `ToString()` (full stack + inner
  exceptions). Do **not** set `Handled` — the app crashes visibly, the host
  shows its banner, and the log explains why.
- **`AppDomain.CurrentDomain.UnhandledException`** — log. Catches non-UI
  threads (hotkey pump, hook threads).
- **`TaskScheduler.UnobservedTaskException`** — log + `SetObserved()`.
  Behavior-preserving (unobserved exceptions don't crash .NET Core), but it
  surfaces silent fire-and-forget failures (`StartAsync`, bridge accept loop).
  Best-effort: fires at GC time, not at throw time.

### 4. Activity tee

`PluginRuntime.Log(string message)` gains one line — `DiagLog.Write(message)`
with the **raw** message, before the UI formatting. Everything the activity
view shows lands in the file: hotkey registration results, macro load
counts/failures, connect success ("Connected. Host version …") and failures
("Startup failed: …"), playback refusals, `HostLost`. No call sites change.

### 5. Tray — "Open log folder"

`TrayService.BuildMenu()` gains a menu item between "Show recorder" and the
separator: **Open log folder** → ensure the directory exists, then
`Process.Start` with `UseShellExecute = true` on `DiagLog.Directory`. Failure
is swallowed (menu click, nothing to report to).

Support flow becomes: right-click tray → Open log folder → drag
`ur-task.log` into Discord.

### 6. README rider

Two edits in the same PR:

- **Install section:** correct "Older hosts will refuse the install with a
  clear 'Update RoRoRo' message" — true only for hosts ≥ 1.4.3
  (`minHostVersion` enforcement shipped there). Hosts 1.4.0–1.4.2 silently
  accept the install and show a "Restart RoRoRo" CTA with a known
  fails-to-relaunch race. New wording: require RoRoRo 1.4.3+, and "if the
  install tells you to *restart RoRoRo*, your RoRoRo is outdated — update it
  from the Microsoft Store and reinstall."
- **Troubleshooting note:** where the log lives, and the tray → Open log
  folder path for support threads.

### 7. Testing

Unit (`tests/rororo-ur-task.Tests`, `DiagLog.Directory` → temp dir):

- Write lands with the expected timestamp prefix.
- Rollover: file pushed past 1 MB rolls to `.1`, old `.1` deleted, fresh file
  continues.
- Uncreatable directory (point at an invalid path) → writes become no-ops, no
  throw.
- Concurrent writes from multiple threads → every line intact (no mid-line
  interleaving), count matches.
- `PluginRuntime.Log` tee: a logged message appears in both the
  `StatusLogged` event payload and the file.
- Watchdog: completion flag not set within an injectable threshold → watchdog
  line lands in the file; flag set in time → no watchdog line.

Manual verify (pre-merge, per `superpowers:verification-before-completion`):

- `URTASK_TEST_CRASH=dispatcher` env var makes `App` throw on the dispatcher
  two seconds after startup — run once, confirm the exception lands in the log
  with full stack, the process dies, and the host banner appears.
  `URTASK_TEST_CRASH=hang` blocks `OnStartup` before completion — confirm the
  watchdog line appears (run with a shortened threshold). The variable is
  checked only when present; zero cost otherwise.
- Kill RoRoRo while the plugin runs → log shows the `HostLost` line + clean
  exit marker.

## Non-goals

- No telemetry, crash upload, or phone-home of any kind. The file stays on the
  user's disk until they hand it over.
- No log-level system, no verbosity settings, no UI toggle. One stream.
- No steady-state liveness monitoring (UI-thread heartbeat, hang detection
  after startup). The watchdog covers startup only — the window where #20
  lived and where a hang is otherwise invisible (no UI exists yet to look
  frozen). Post-startup hangs have a visibly frozen window as their evidence.
- No host-side (ROROROblox) changes — the old-host restart race is already
  gone in 1.4.3+; the Store update is the remedy for stragglers.
- No retroactive fix for the missing-consent / Event Viewer support path on
  versions already shipped.

## PR position

Own branch off `main`, rides the v0.5 train independent of the in-flight PRs
(#15 → #16 → #18 stack, #19, #20). Touch set: `src/Diagnostics/DiagLog.cs`
(new), `src/App.xaml.cs`, `src/PluginRuntime.cs` (one line),
`src/UI/TrayService.cs`, `README.md`, tests. No overlap with the
share/dashboard/theme surfaces; #20's bridge-pipe fix touches
`MacroRunnerServer`, also outside this set.
