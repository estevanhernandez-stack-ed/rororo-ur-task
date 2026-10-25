# Ur Golem: Ur OCR merges into Ur Task

**Status:** approved by Este, 2026-09-30. Build starts 2026-10-04, autonomously, through Vibe
Cartographer `/build` from `docs/superpowers/plans/2026-09-30-ur-golem-merge.md`.

## Why

Ur Task plays macros. Ur OCR watches the screen and tells Ur Task what to play. In September 2026
most of the work went into the connection between the two plugins, not into features:

- ClearAt, SweepPath, freePath and the no-outline list were each added to the bridge. Every one
  needed a change in both repos, a contract note and a matched pair of versions.
- A drifted pair failed without saying so: Ur Task 0.8 hung on a schema-4 macro.
- Ur OCR never shipped as a real install. It ran from a Debug folder on a local branch.

One plugin removes the connection, and it lets a game preset ship as one file later. The merged
plugin is **Ur Golem**: it sees the game and acts on it.

## Decisions (locked)

| # | Decision | Why |
|---|---|---|
| D1 | The merged plugin keeps Ur Task's identity: plugin id `626labs.ur-task`, AssemblyName `626labs.ur-task`, entrypoint `626labs.ur-task.exe`, the pipe `626labs-ur-task`, and the data folder `%LOCALAPPDATA%\626Labs\RoRoRoUrTask\`. Display name "RoRoRo Ur Golem". | Existing installs upgrade in place. Macros, settings and Ur MCP keep working. |
| D2 | The code lives in `rororo-ur-task`. Ur OCR's source moves to `src/Vision/` under the namespace `Labs626.UrTask.Vision`. | This repo has the CI, the host-integration job, the hooks and the release pipeline. |
| D3 | Version **0.13.0**. The rename to 1.0.0 is Este's call at release. | SemVer: a new capability (vision), no breaking change to the bridge. |
| D4 | **Vision is opt-in.** It is off by default (`UserPreferences.VisionEnabled = false`). Off means no screen capture, no triggers, no pulse, and no vision thread started. | Screen reading is a bigger ask than input. Many Ur Task users only record and play. |
| D5 | Vision reaches the playback engine **in process, through the same server as the pipe**. Vision's `MacroRunClient` is built with its existing stream-opener constructor, and the opener returns one end of an in-memory duplex stream (`LoopbackStream`, built on `System.IO.Pipelines`). The other end goes to `MacroRunnerServer.HandleConnectionAsync`. There is one `MacroRunInvoker` and one server, whether or not the pipe accept loop runs (`AcceptPluginRunRequests` now gates only the pipe). The named pipe stays, unchanged, for Ur MCP and any third party. | Same framing, version check, routing and single flight as an external caller, with no field mapping to get wrong. Every Vision Ipc test stays. Merging the two sets of DTOs is later work (N1). |
| D6 | Where a type exists in both codebases, **Ur Task's copy wins**: PluginClient, AccountRegistry, ForegroundWatcher, WindowMetrics, HeaderInjectingCallInvoker, StartupWatchdog, TrayService, RelayCommand, theming. Vision gets thin adapters where its interfaces differ. Vision's duplicates are deleted. | One host connection, one tray, one theme source, one watchdog. |
| D7 | Vision logs to its own file, `RoRoRoUrTask\logs\vision.log` (the ported `DiagLog`, renamed `VisionLog`, same roll rule). | The pulse log is heavy, and the monitoring greps rely on its line formats. |
| D8 | Vision data moves to `RoRoRoUrTask\vision\` (`triggers.json`, `settings.json`, `display-fingerprint.json`, the import logs). On first start with Vision enabled, if `vision\triggers.json` is missing and `626Labs\rororo-ur-ocr\triggers.json` exists, it is **copied** (never moved), with the same for `settings.json`. This is logged, and the old folder is left alone. | Nothing is lost if the migration is wrong, and rolling back to Ur OCR still works. |
| D9 | If the old Ur OCR plugin is running (process `RoRoRo.UrOcr`), Vision does not start. The window shows a banner: "Ur OCR is still installed. Remove it in RoRoRo's Plugins window: Ur Golem does its job now." The check runs at start and whenever Vision is switched on. | Two pulses on one account would fight over the same macros. |
| D10 | Pause all watchers moves from bare **F9** to **Ctrl+Shift+F9**, registered by Ur Task's `HotkeyService` (new id 6, `HotkeyKind.PauseVision`). Vision's own `HotkeyService` is deleted. | A bare F9 grab takes F9 away from every app. Esc had this bug in 0.3.0. |
| D11 | The headless imports stay, on the merged exe: `626labs.ur-task.exe --import-ring <file>` and `--import-pulse <file>`. They refuse (exit 3) while the plugin runs, detected by the single-instance mutex `Local\rororo-plugin-626labs.ur-task`. This takes a `src/Program.cs` with `[STAThread] Main` and `<StartupObject>`, and `App.xaml` stays the ApplicationDefinition (its generated Main goes unused). This is Ur OCR's pattern. | The live workflow and the docs depend on them. The mutex check is exact, where a process-name check is not. |
| D12 | The UI stays two windows. The Ur OCR main window becomes `VisionWindow`, opened from a new **VISION** button in the RecorderWindow action strip and from a tray item "Vision…". No tabs, no redesign. | The merge ships the capability. The one-window redesign is later work (N2). |
| D13 | The manifest adds the capability `system.read-screen`. | Vision reads the screen. The host may ask the user to consent again on update, which is expected. |
| D14 | Target framework becomes `net10.0-windows10.0.19041.0`, with `SupportedOSPlatformVersion 10.0.19041.0`. Add `System.Drawing.Common 10.0.0`. Vision drops its `ROROROblox.PluginContract 0.3.0` reference and uses Ur Task's 0.8.0 package (still a PackageReference, per this repo's CLAUDE.md). | Windows OCR (`Windows.Media.Ocr`) needs the 19041 target, and GDI capture needs System.Drawing. |
| D15 | Git provenance: files are **copied** from Ur OCR at a pinned commit, and the commit message names it. History stays in the Ur-OCR repo, which Este archives after release. | A history-preserving merge (subtree or filter-repo) is a manual, risky step in an autonomous pass. |
| D16 | The base branch is the current `feat/ore-stop-pulse` heads. Ur Task is at `f975348` or later. Ur OCR is pinned at `43f57d8`. Work happens on `feat/ur-golem`, branched from Ur Task's `feat/ore-stop-pulse`. | Neither branch is merged to main yet, and the merge needs both. |

## Architecture after the merge

```
App (src/App.xaml)                 <- src/Program.cs Main (StartupObject): --import-* headless, else run App
 └ PluginRuntime (Ur Task, extended)
    ├ PluginClient ── host gRPC (contract 0.8.0): accounts, theme feed
    ├ AccountRegistry, ForegroundWatcher, WindowMetrics  (shared)
    ├ SequencePlayer / MacroPlayer / StepRunner           (playback)
    ├ MacroRunInvoker ── MacroRunnerServer ─┬─ pipe 626labs-ur-task (Ur MCP, third parties; gated by AcceptPluginRunRequests)
    │                                       └─ LoopbackStream ← InProcessBridge ← Vision's MacroRunClient
    ├ HotkeyService: Ctrl+Shift+R/P/L/F12, Esc while playing, Ctrl+Shift+F9 pause vision
    └ VisionRuntime (was Ur OCR PluginRuntime), started only when VisionEnabled and no Ur OCR
       ├ PulseRunner → PulseLoop (per account)
       ├ TriggerCoordinator
       ├ TriggerStore / SettingsStore  (RoRoRoUrTask\vision\)
       └ VisionLog → logs\vision.log
UI: RecorderWindow (+ VISION button) · VisionWindow (was MainWindow) · one TrayService
```

## File fates (Ur OCR at 43f57d8 → rororo-ur-task)

Every Ur OCR source file moves to `src/Vision/<same subfolder>/`, with the namespace
`RoRoRo.UrOcr.X` becoming `Labs626.UrTask.Vision.X`. The exceptions:

| Ur OCR file | Fate |
|---|---|
| `Program.cs` | Its headless branch becomes the new `src/Program.cs` (D11). File deleted. |
| `App.xaml`, `App.xaml.cs` | Deleted. Their startup steps (theme, runtime, tray, hotkeys) fold into Ur Task's `App` and `PluginRuntime`. Resource key `BoolToVis` is renamed to `BoolToVisibility` in the Vision XAML. |
| `PluginRuntime.cs` | Becomes `src/Vision/VisionRuntime.cs`. Its constructor takes the shared host pieces (adapters, D6) instead of building its own `PluginClient`. |
| `Diagnostics/DiagLog.cs` | Becomes `src/Vision/VisionLog.cs` (class `VisionLog`, file `vision.log`, D7). |
| `Diagnostics/StartupWatchdog.cs` | Deleted (Ur Task's). |
| `Hotkeys/HotkeyService.cs` | Deleted (D10). |
| `Ipc/*` (BridgeContract, FrameCodec, IMacroRunClient, MacroRunClient) | All move to `src/Vision/Ipc/` unchanged. New files: `Vision/Ipc/LoopbackStream.cs` (the in-memory duplex pair) and `Vision/Ipc/InProcessBridge.cs` (which builds a `MacroRunClient` whose opener hands one end to `MacroRunnerServer.HandleConnectionAsync`), for D5. |
| `PluginHost/PluginClient.cs`, `HeaderInjectingCallInvoker.cs`, `AccountRegistry.cs`, `ForegroundWatcher.cs`, `WindowMetrics.cs`, `IWindowMetrics.cs` | Deleted. Vision uses Ur Task's, through adapters where Vision's interface differs (`IAccountLookup`, `IForegroundCheck`, `IWindowMetrics` subset). `ElevationProbe.cs` stays (Vision only). |
| `Theming/*` | Deleted. Ur Task's host theme feed paints both windows, and the brush keys are identical. |
| `UI/TrayService.cs`, `UI/RelayCommand.cs` | Deleted. Ur Task's tray gains "Vision…" and "Pause all watchers (Ctrl+Shift+F9)". Vision's view models use Ur Task's `RelayCommand` (adapt signatures if they differ). |
| `UI/MainWindow.xaml(.cs)` | Becomes `src/Vision/UI/VisionWindow.xaml(.cs)`. |
| `Properties/AssemblyInfo.cs` | Deleted (Ur Task's csproj already grants `InternalsVisibleTo` to `rororo-ur-task.Tests`). |
| `Storage/PluginPaths.cs` | Kept. Its paths point to `RoRoRoUrTask\vision\` (D8), and the old folder path is kept as `LegacyDir` for the migration. |
| `Storage/UrTaskMacros.cs` | Kept as is. It reads the same `RoRoRoUrTask\macros\` folder, which is still correct in-process. |
| `RingImportCommand.cs`, `PulseImportCommand.cs`, `ImportReport.cs` | Move to `src/Vision/Import/`. The "Ur OCR running" check becomes the mutex check (D11). |
| `tests/RoRoRo.UrOcr.Tests/**` | Move to `tests/rororo-ur-task.Tests/Vision/**`, namespace `Labs626.UrTask.Tests.Vision.*`, fixtures to `tests/rororo-ur-task.Tests/Vision/fixtures/`. Tests of deleted files are deleted with them: `DiagLogTests` becomes `VisionLogTests`; `StartupWatchdogTests`, `HostThemeReaderTests` and `AccountRegistryTests` go. Every `Ipc/` test stays, because the client stays. |
| `tests/RoRoRo.UrOcr.IntegrationTests/**` | Dropped. Ur Task's `PluginClientIntegrationTests` covers the one host connection. |
| `tools/ring-*.ps1` | Move to `tools/vision/`. |
| `README.md`, `CHANGELOG.md` | Folded into Ur Task's README (a Vision section) and CHANGELOG (the 0.13.0 entry links Ur OCR's history at its archived repo). |

## Non-goals

- **N1** Merging the two sets of bridge DTOs into one. The adapter maps them. A later cleanup can
  remove Vision's copy.
- **N2** A one-window UI with tabs.
- **N3** A preset or pack file format. Not planned (Este, 2026-09-30). The Space Mine setup is
  archived for reuse if a mining event comes back.
- **N4** Any change to Ur MCP, RoRoRo (the host) or the pipe contract.
- **N5** Releasing, tagging, merging to main, or archiving the Ur-OCR repo. Those are Este's calls.
- **N6** Any change to pulse, trigger or playback behaviour. Every behaviour test passes unchanged,
  apart from namespace edits and the file fates above.

## Acceptance criteria

1. `dotnet build rororo-ur-task.csproj` succeeds with 0 errors and no new warnings beyond the
   existing NU1510.
2. `dotnet test tests/rororo-ur-task.Tests/rororo-ur-task.Tests.csproj -p:StandaloneTestsOnly=true`
   passes. The count is at least Ur Task's baseline plus Vision's kept tests (the plan records both
   numbers at Task 0). The only allowed failures are the two HotkeyService registration tests,
   and only when a RoRoRo instance on the machine holds the chords (win32 error 1409). This is
   documented, not new.
3. Every PulseLoop, TriggerCoordinator, Storage and Engine test from Ur OCR passes with no change
   to its assertions.
4. The in-process bridge has its own tests. A real `MacroRunnerServer` over a recording fake
   invoker, called through `InProcessBridge`, receives each Vision request (RunMacro, GetPlayback,
   ClearAt with and without a guard, SweepPath with and without freePath) with every field intact,
   and Vision receives each reply intact, NoOutline included. A busy invoker surfaces `busy`. With
   `AcceptPluginRunRequests = false` the in-process bridge still works and no pipe is listening.
5. With `VisionEnabled = false`, the runtime starts no vision thread and makes no capture call,
   and `vision.log` is not created. There is a test that proves it.
6. With a fake "Ur OCR running" probe, VisionRuntime does not start and the banner flag is set.
   There is a test.
7. The migration copies `triggers.json` and `settings.json` from the legacy folder once, never
   overwrites, and leaves the legacy files in place. There are tests.
8. `--import-ring` and `--import-pulse` run headless (no window), write to
   `RoRoRoUrTask\vision\triggers.json`, and exit 3 while the mutex is held. There are tests.
9. `manifest.json` and the csproj agree on 0.13.0 (`VersionConsistencyTests`). The name is
   "RoRoRo Ur Golem", and the capabilities include `system.read-screen`.
10. `build/build-plugin.ps1` produces `artifacts/plugin.zip` whose entrypoint is
    `626labs.ur-task.exe`.
11. CI `test.yml` passes both jobs (unit-tests and host-integration) on the branch.
12. The docs are updated: README (name, Vision section with Hide My Pets, import order, opt-in,
    "remove Ur OCR"), FEATURES, CHANGELOG 0.13.0, and CLAUDE.md (the new gotchas: vision.log,
    VisionEnabled, the ring and pulse imports, and "never run Ur OCR beside Ur Golem").

## Live smoke (Este present, after the autonomous pass, not part of it)

1. Install the branch build over the installed plugin. Ur Task's macros and assignments are
   intact. Ur MCP `list_macros` and `run_macro` work.
2. Remove the Ur OCR plugin. Enable Vision. The migration log line appears, and the Mine #8 pulse
   shows in VisionWindow.
3. Run the pulse for 10 minutes. `vision.log` shows `setting the camera`, layer reads and passes,
   and the stop monitor sees no unexplained stops.
4. Ctrl+Shift+F12 aborts a sweep. Ctrl+Shift+F9 pauses the pulse and resumes it.
5. Restart RoRoRo. Ur Golem comes back with Vision still enabled.
6. On the 4K rig (Dunder-MiffLan): capture regions line up and DpiGuard's verdict is right, now that
   the process is PerMonitorV2 aware.

## Risks

| Risk | Mitigation |
|---|---|
| Vision's view models or XAML depend on Ur OCR's `App` resources or `RelayCommand` shape | Task 5 diffs the resource keys (only `BoolToVis` differs) and adapts the RelayCommand calls. The build is the gate. |
| Ur OCR had no app.manifest (system DPI); Ur Task's is PerMonitorV2, so capture coordinates and the DpiGuard fingerprint can differ on scaled or mixed-DPI rigs | The live smoke checks capture regions and the DpiGuard verdict on the 4K rig. |
| The TFM change breaks something in Ur Task at runtime (self-contained publish) | The live smoke checks recording and playback first. Rollback is the backed-up 0.12.0 plugin folder. |
| The host re-prompts for consent on the new capability | Expected. Mention it in the README and CHANGELOG. |
| Two plugins holding the same Esc/F12 chords during the smoke (old Ur Task still running) | The smoke installs over the same plugin id, so only one copy ever runs (single-instance mutex). |
| Sonnet trims a test to make it pass | The plan forbids editing assertions in moved tests. Only `using`, namespace and file path lines may change, and the reviewer checks the diff for exactly that. |
