# Ur Golem merge: implementation plan

> **For the builder (Sonnet 5.5, Vibe Cartographer `/build`, subagent-driven):** read the spec
> `docs/superpowers/specs/2026-09-30-ur-golem-merge-design.md` first. Every decision there (D1 to
> D16) is locked; do not re-open one. Work task by task, in order. Each task ends green (build +
> tests) and in one commit. Keep a ledger at `.superpowers/sdd/2026-10-04-ur-golem/progress.md`
> (gitignored): per task, the commit, the test count, and every judgment call you made with its
> reason. If a step is impossible as written, stop that task, write why in the ledger, and move to
> the next task only if it does not depend on it.

**Goal:** Ur OCR's code runs inside Ur Task as the opt-in Vision feature; one plugin, id
`626labs.ur-task`, named "RoRoRo Ur Golem", version 0.13.0.

**Repos (sibling layout, both on disk):**
- target: the `rororo-ur-task` checkout you are in (below: `UT`)
- source: its sibling `..\Ur-OCR` (below: `OCR`), read only, pinned at commit `43f57d8`

**Commands used throughout** (run from `UT`; never build the `.sln`, see `UT/CLAUDE.md`):

```
dotnet build rororo-ur-task.csproj -nologo -v q
dotnet test tests/rororo-ur-task.Tests/rororo-ur-task.Tests.csproj -p:StandaloneTestsOnly=true -nologo
```

If `dotnet build` fails copying `626labs.ur-task.exe` because the file is locked, a RoRoRo on
this machine is running the installed plugin from a different folder; that is not this build.
If the lock is on `bin\...`, pass `--artifacts-path <scratch folder>` to `dotnet test` instead of
stopping anything. **Never stop RoRoRo, Roblox or any plugin process.**

**Known, allowed test failures:** `HotkeyServiceTests.Start_RegistersChordHotkeysAndDisposeCleansUp`
and `HotkeyServiceTests.EnableAndDisableAbortKey_AfterStart_AreIdempotentAndDoNotThrow` fail with
win32 error 1409 while a RoRoRo instance holds the chords. Record them in the ledger; do not
touch them. Any other failure blocks the task.

## Rules for every task

1. **Moved tests keep their assertions.** In a test file moved from `OCR`, you may change only:
   `namespace` and `using` lines, file paths to fixtures, and type names that the spec renames
   (`DiagLog`→`VisionLog`, `PluginRuntime`→`VisionRuntime`, `MainWindow`→`VisionWindow`). Nothing
   else. If a moved test fails, the production code is wrong, not the test.
2. **No behaviour change** to the pulse, triggers, playback or bridge (spec N6).
3. Namespace rule: `RoRoRo.UrOcr` → `Labs626.UrTask.Vision`, keeping the sub-namespace
   (`RoRoRo.UrOcr.Engine` → `Labs626.UrTask.Vision.Engine`). Tests: `RoRoRo.UrOcr.Tests` →
   `Labs626.UrTask.Tests.Vision`. XAML `x:Class` and `clr-namespace:` follow the same rule.
4. Conventional commits, each ending with the attribution line the session supplies. The
   pre-commit hooks must pass (`powershell -ExecutionPolicy Bypass -File .claude/hooks/install.ps1`
   once). The local-path guard blocks absolute user-profile paths: none may appear in committed files.
5. Line endings: keep CRLF where the source file has CRLF.
6. Do not push until Task 8. Do not merge, tag or release (spec N5).

---

## Task 0: baseline and branch

1. In `UT`: `git status` must be clean apart from the untracked `.claude/626labs-context.md`,
   `.gitnexus/` and `AGENTS.md` (leave those alone). `git switch feat/ore-stop-pulse`,
   `git pull`, then `git switch -c feat/ur-golem`. The spec and this plan are already committed
   on `feat/ore-stop-pulse`; if they show as untracked, commit them first as
   `docs(spec): the Ur Golem merge, spec and plan`.
2. Confirm `OCR` is at `43f57d8` or later on `feat/ore-stop-pulse`:
   `git -C ../Ur-OCR log -1 --format=%h`. Record the hash in the ledger; every later "copy from
   OCR" uses that exact tree (`git -C ../Ur-OCR show <hash>:<path>` if the working copy moved).
3. Baselines, recorded in the ledger:
   - `UT` test count (the standalone command above): expect about 716 total.
   - `OCR` unit count: `dotnet test ../Ur-OCR/tests/RoRoRo.UrOcr.Tests/RoRoRo.UrOcr.Tests.csproj --artifacts-path <scratch>`:
     expect 731 passed.
   - Per-file test counts in `OCR` for the files spec drops: `StartupWatchdogTests`,
     `HostThemeReaderTests`, `AccountRegistryTests` (list with `--list-tests` filtered by class).
     **Target after Task 1:** UT baseline + 731 − those dropped counts.
4. No commit.

## Task 1: the Vision library and its tests (no UI, no runtime yet)

**Copy** from `OCR` into `UT/src/Vision/` (same subfolders), then apply the namespace rule:

- `Engine/*` (all 26 files)
- `Storage/*` (all 15 files; `PluginPaths` unchanged for now, Task 4 moves the paths)
- `Ipc/*` (all 4 files)
- `RingImportCommand.cs`, `PulseImportCommand.cs`, `ImportReport.cs` → `src/Vision/Import/`
  (namespace `Labs626.UrTask.Vision.Import`; fix the references)
- `PluginHost/ElevationProbe.cs`, `PluginHost/IWindowMetrics.cs` → `src/Vision/PluginHost/`
- `PluginHost/ForegroundWatcher.cs` → `src/Vision/PluginHost/VisionForegroundCheck.cs`, class
  renamed `VisionForegroundCheck`. It implements `IForegroundCheck`. Change its dependency from
  Ur OCR's `AccountRegistry` to `IAccountLookup` (from `Vision.Engine`): "is this pid an alt" becomes
  `lookup.TryGetUserId(pid, out _)`. Keep its Win32 foreground calls as they are, and keep its
  `LastForegroundAltPid` and `CaptureForegroundAlt()` (the UI and PreviewEvaluator use them), with
  the same alt test.
- `Diagnostics/DiagLog.cs` → `src/Vision/VisionLog.cs`, class `VisionLog` in namespace
  `Labs626.UrTask.Vision.Diagnostics`, file name `vision.log`
  (rolled file `vision.1.log`), directory `%LOCALAPPDATA%\626Labs\RoRoRoUrTask\logs`. Keep its
  settable directory for tests and its never-throw rule. Replace every `DiagLog.` call in the
  copied Vision files with `VisionLog.` (Ur Task's own `DiagLog` must stay untouched and unused
  by Vision). After this task `grep -rn "DiagLog" src/Vision` must return nothing: a stray
  `Diagnostics.DiagLog` would bind silently to Ur Task's logger.

**Do not copy:** `Program.cs`, `App.xaml*`, `PluginRuntime.cs`, `Properties/*`, `Hotkeys/*`,
`Theming/*`, `UI/*`, `PluginHost/PluginClient.cs`, `HeaderInjectingCallInvoker.cs`,
`AccountRegistry.cs`, `WindowMetrics.cs`, `Diagnostics/StartupWatchdog.cs` (Tasks 3 to 5 cover
what replaces them).

**Tests:** copy `OCR/tests/RoRoRo.UrOcr.Tests/**` into `UT/tests/rororo-ur-task.Tests/Vision/`
keeping subfolders, apply the namespace rule, except these, which are not copied:
`Diagnostics/StartupWatchdogTests.cs`, `Theming/HostThemeReaderTests.cs`,
`PluginHost/AccountRegistryTests.cs`, and the csproj itself. `Diagnostics/DiagLogTests.cs` becomes
`Vision/VisionLogTests.cs` testing `VisionLog` (the file name it asserts becomes `vision.log`;
that is a rename, allowed by rule 1). Its xunit collection is renamed from `"DiagLog"` to
`"VisionLog"` (the `[CollectionDefinition]` and every `[Collection]` using it), because Ur Task's
`DiagLogTests` already defines `"DiagLog"` and xunit refuses duplicates. Copy `fixtures/` to
`tests/rororo-ur-task.Tests/Vision/fixtures/` (`_FixtureGenerator.cs` in it compiles into the test
project; that is fine).

**csproj changes** (`UT/rororo-ur-task.csproj`):
- `TargetFramework` → `net10.0-windows10.0.19041.0`, add
  `<SupportedOSPlatformVersion>10.0.19041.0</SupportedOSPlatformVersion>`.
- add `<PackageReference Include="System.Drawing.Common" Version="10.0.0" />`.
- nothing else (the PluginContract stays the 0.8.0 PackageReference).

**Test csproj changes** (`UT/tests/rororo-ur-task.Tests/rororo-ur-task.Tests.csproj`): the tests
load `Path.Combine(AppContext.BaseDirectory, "fixtures", "pitch"|"layer", ...)`, so link the files
to the same output paths:

```xml
<None Include="Vision\fixtures\pitch\*.png" Link="fixtures\pitch\%(Filename)%(Extension)" CopyToOutputDirectory="PreserveNewest" />
<None Include="Vision\fixtures\layer\*.png" Link="fixtures\layer\%(Filename)%(Extension)" CopyToOutputDirectory="PreserveNewest" />
```

Do not change the tests' fixture paths. The existing `..\fixtures\*.json` items also land in
`fixtures\`; the names do not clash.

**Expected friction and the fix:**
- A test or engine file references `PluginClient`, `AccountRegistry` or `WindowMetrics` from
  `RoRoRo.UrOcr.PluginHost`: point it at the Vision interface (`IAccountLookup`,
  `IWindowMetrics`, `IForegroundCheck`) it should have used; if it needs the concrete class only
  to construct a default, leave that construction to Task 4 by taking the interface in the
  constructor. Record each such edit in the ledger.
- `InternalsVisibleTo`: `UT` already grants it to the test assembly; nothing to add.
- Two types with the same simple name now exist (`BridgeContract`, `FrameCodec`,
  `IWindowMetrics`, `WindowSpaceMath`, `ActivityLog`?). They are in different namespaces; fix any
  ambiguous `using` in moved files with a namespace alias, never by renaming Ur Task's types.

**Gate:** build 0 errors; tests = the Task 0 target (± the documented 2), every moved test green.

**Commit:** `feat(vision): move Ur OCR's engine, storage and bridge client in as Vision`
(body: `Copied from Ur-OCR <hash>. No behaviour change.`)

## Task 2: the in-process bridge (spec D5)

1. **`PluginRuntime`**: today the bridge invoker and server are built only when
   `UserPreferences.Load().AcceptPluginRunRequests` is true (around line 117). Change it so the
   `MacroRunInvoker` and one `MacroRunnerServer` are always built (same constructor arguments, same
   `SequencePlayer` instance), and only `RunAcceptLoopAsync` (the pipe) is gated by the preference.
   Expose `internal MacroRunnerServer BridgeServer { get; }`.
2. **`src/Vision/Ipc/LoopbackStream.cs`**: `internal static class LoopbackStream` with
   `public static (Stream Client, Stream Server) CreatePair()`. Two `System.IO.Pipelines.Pipe`s;
   each end is a small `DuplexPipeStream : Stream` reading one pipe's reader and writing the other's
   writer (`PipeReader.AsStream()` / `PipeWriter.AsStream()` underneath). Disposing an end
   completes its writer and reader. No threads, no timers.
3. **`src/Vision/Ipc/InProcessBridge.cs`**: `internal static class InProcessBridge` with
   `public static MacroRunClient Create(MacroRunnerServer server)`. It returns
   `new MacroRunClient(openPipe)` (Vision's existing internal test constructor), where `openPipe`
   creates a pair, starts `server.HandleConnectionAsync(pair.Server, runtimeToken)` and returns
   `pair.Client`. `Create` takes the token as a second parameter: `PluginRuntime` passes its bridge
   token (`_bridgeCts.Token`), **not** the client's per-call `ct`. Over the real pipe the server
   runs on the accept loop's token, and the client's `ct` is Vision's per-tick watchdog, which would
   abort a run mid-ack. The server task is not awaited by `openPipe`; it is observed (exceptions to
   `VisionLog`, never thrown) and ends with `await pair.Server.DisposeAsync()` in a `finally`
   (the server never disposes the stream itself). `LoopbackStream`, `InProcessBridge` and the
   adapters are `internal` (they touch internal Ur Task types). In these files and their tests,
   alias `Labs626.UrTask.Ipc` (and in Task 3 `Labs626.UrTask.PluginHost`) to avoid CS0104 against
   Vision's identically named types.
4. **Tests** (`tests/.../Vision/Ipc/InProcessBridgeTests.cs`), against a real `MacroRunnerServer`
   over a recording fake `IMacroRunInvoker` (write the fake in the test file):
   - RunMacro: macro id, targets, inter-alt delay and caller id arrive; the reply's playback id
     comes back.
   - GetPlayback: state, reason, detail, step index and `NoOutline` come back intact.
   - ClearAt with and without a guard; SweepPath with and without `freePath`: every field arrives
     (compare against the request Vision built, field by field).
   - A fake invoker that answers `busy` surfaces `busy` to Vision's `RunAsync`.
   - Two calls in a row reuse nothing (each opens its own pair) and both succeed.
   - With the preference off, `PluginRuntime` never starts the accept loop, and
     `InProcessBridge.Create(runtime.BridgeServer)` still works. If constructing a real
     `PluginRuntime` in a test is impractical, test the gate at the smallest unit you can extract
     (a static `ShouldListen(prefs)` is fine) and record the choice.

**Gate:** build green; all tests green (+ the new ones).

**Commit:** `feat(vision): an in-process bridge through the same server as the pipe`

## Task 3: host adapters (spec D6)

In `src/Vision/PluginHost/`:

1. `AccountLookupAdapter : IAccountLookup` over Ur Task's `AccountRegistry`:
   `TryGetUserId(pid, out id)` = `registry.ResolveByPid(pid)` is not null → its `RobloxUserId`.
   It also exposes `IReadOnlyCollection<int> Pids` = `registry.Snapshot().Select(a => a.Pid)
   .OrderBy(p => p).ToArray()` (Vision's UI reads `Accounts.Pids`).
2. `WindowMetricsAdapter : Vision.PluginHost.IWindowMetrics` over Ur Task's
   `Labs626.UrTask.PluginHost.IWindowMetrics`: the three methods pass straight through.
3. Tests for both with Ur Task's real `AccountRegistry` (it has `OnLaunched`/`OnExited`) and a
   fake Ur Task `IWindowMetrics`.

**Gate / commit:** green; `feat(vision): adapters from Ur Task's host pieces to Vision's seams`

## Task 4: VisionRuntime, opt-in, the Ur OCR guard, storage move and migration

1. **`src/Vision/VisionRuntime.cs`** from `OCR/PluginRuntime.cs` (class `VisionRuntime`).
   Constructor takes: `IAccountLookup`, `IForegroundCheck`, `IWindowMetrics` (Vision's),
   `IMacroRunClient`, `bool hostConnected`, `Func<bool> urOcrRunning`, `IClock?`. It exposes
   **every public member** of Ur OCR's `PluginRuntime` (read it: the UI reads `Activity`,
   `Text.IsAvailable`, `Keys`, `Settings`, `Coordinator`, `LastDpiCheck`, `Preview`, `Foreground`,
   `WindowMetrics`, `Accounts`) except `Client`, `Hotkey` and building its own `MacroClient`.
   `Accounts` is the `AccountLookupAdapter` (with `Pids`), and `Foreground` is the
   `VisionForegroundCheck`. It builds what Ur OCR's runtime built (stores, `ElevationProbe`,
   `ColorMatcher`, `TextMatcher`, `KeySender`, `ActivityLog`, `ToastService`, `PreviewEvaluator`,
   SpotReader over CaptureEngine, PulseRunner, TriggerCoordinator, DpiGuard). Copy
   `OCR/UI/ToastService.cs` to `src/Vision/UI/` **in this task** (the runtime needs it; Task 5
   skips it). `StartAsync()`: if `urOcrRunning()` → set `BlockedByUrOcr = true`, log
   `vision: not started, Ur OCR is still running`, return without starting anything. Else start
   both engines as Ur OCR did and run the DPI check. `StopAsync()` stops both and is idempotent.
   Keep Ur OCR's log lines (they go to `vision.log`).
2. **`UserPreferences`**: add `public bool VisionEnabled { get; set; } = false;` with the same
   persistence as the other keys.
3. **`PluginRuntime`**: after the host connect attempt (connected or not: pass
   `hostConnected = false` on failure, as Ur OCR did), if `VisionEnabled`, build the adapters (Task 3),
   `VisionForegroundCheck(accountLookup)`, `InProcessBridge.Create(BridgeServer)`, and the
   `VisionRuntime`; start it. Expose `internal Vision.VisionRuntime? VisionRuntime` (not a
   property named `Vision`: it would shadow the `Vision` namespace inside the class), and
   `internal Task SetVisionEnabledAsync(bool on)` (persists the preference, starts or stops).
   `urOcrRunning` = `Process.GetProcessesByName("RoRoRo.UrOcr").Length > 0`. Disposal stops Vision
   before the playback engine.
4. **Storage (spec D8)**: `Vision/Storage/PluginPaths`: data dir →
   `%LOCALAPPDATA%\626Labs\RoRoRoUrTask\vision\`; add `LegacyDir` =
   `%LOCALAPPDATA%\626Labs\rororo-ur-ocr\`. New `Vision/Storage/LegacyMigration.cs`:
   `static MigrationResult Run(string legacyDir, string dataDir)`: for `triggers.json` and
   `settings.json`, copy when the target is missing and the source exists; never overwrite; never
   delete; write `vision\migrated-from-ur-ocr.txt` (timestamp + files copied) the first time.
   `VisionRuntime.StartAsync` runs it before loading the stores and logs
   `vision: copied triggers.json, settings.json from Ur OCR (left in place)` (or nothing if there
   was nothing to copy). Paths must be injectable for tests.
5. **Tests:**
   - Vision disabled → `PluginRuntime` builds no `VisionRuntime` (test the decision function if a
     full runtime is impractical; record it) and `vision.log` is not created.
   - `urOcrRunning` true → not started, `BlockedByUrOcr`, no engine started (fake engines or a
     flag the test can read).
   - Migration: copies both when missing; does not overwrite an existing target; leaves the
     legacy files; second run copies nothing; missing legacy dir is a no-op.

**Gate / commit:** green; `feat(vision): VisionRuntime, off by default, guarded against Ur OCR, data moved with a copy`

## Task 5: UI, tray and the pause hotkey

1. Copy `OCR/UI/*` into `src/Vision/UI/` except `TrayService.cs`, `RelayCommand.cs` and
   `ToastService.cs` (moved in Task 4).
   `MainWindow` → `VisionWindow` (files and class). Apply the namespace rule in `.cs` and `.xaml`.
2. In the Vision XAML, replace every `BoolToVis` resource reference with Ur Task's
   `BoolToVisibility`. The **keyed** resources all exist in `src/App.xaml`, but Ur OCR's App.xaml
   also has about 13 **keyless (implicit) styles** the Vision windows rely on: TextBlock (white
   foreground), Window (BgBrush background), TextBox, CheckBox, RadioButton, ComboBox, ComboBoxItem,
   ListBox, ListBoxItem, ListView, ListViewItem, GridViewColumnHeader, Expander. Without them the
   build is green and the windows render dark text on dark. Create
   `src/Vision/UI/VisionStyles.xaml`, a ResourceDictionary holding exactly those keyless styles
   (copied from `OCR/App.xaml`), and merge it into the `Resources` of every Vision window
   (VisionWindow, ColorPickerDialog, DangerousKeybindDialog, RegionPickerOverlay, SettingsFlyout if
   it is a window; UserControls inherit from their window). **Do not put it in `src/App.xaml`**:
   the implicit TextBlock and Window styles would restyle RecorderWindow. Brushes stay
   `{DynamicResource}`.
3. Vision's view models use Ur Task's **`RelayCommand<object?>`** wherever Ur OCR used its
   `RelayCommand(Action<object?>, Predicate<object?>?)` (same shape, same requery). Do not use Ur
   Task's non-generic `RelayCommand` (it takes `Action`/`Func<bool>` and does not requery). Anything that reached `(App)Application.Current` for the runtime
   now gets `VisionRuntime` passed in (constructor or `DataContext`).
4. `VisionWindow` shows `DegradedBanner` with the spec D9 sentence when `BlockedByUrOcr`, and an
   "Enable Vision" toggle bound to `PluginRuntime.SetVisionEnabledAsync` (when off, the window
   says in one sentence what Vision does and that it reads the screen). Voice: second person,
   sentence case, no emoji, for non-technical Pet Sim players (see `UT/CLAUDE.md`).
5. `RecorderWindow`: add a **VISION** button to the action strip (same style as RECIPES) that
   opens (or focuses) the single `VisionWindow`. `TrayService`: add "Vision…" and
   "Pause all watchers (Ctrl+Shift+F9)" (enabled only while Vision runs).
6. `HotkeyService`: new id 6, `HotkeyKind.PauseVision`, chord Ctrl+Shift+F9 (`VK_F9 = 0x78`),
   registered for the plugin's lifetime like F12, **but non-fatal**: today `MessageLoop` throws if
   any registration fails, which would take record, play and abort down for users who never use
   Vision. If Ctrl+Shift+F9 fails, log `Hotkeys: Ctrl+Shift+F9 unavailable (win32 error N)`, do not
   add it to `registered`, and carry on. Add `VK_F9` to `ChordHotkeyVkCodes`; `KeyName(6)`
   = "Ctrl+Shift+F9". `PluginRuntime.OnHotkey(PauseVision)` toggles the coordinator's pause exactly
   as Ur OCR's `App.TogglePause` did (read it) and updates the tray tooltip. Update the
   "Hotkeys ready" log line. Tests: `HotkeyServiceTests` gains the F9 assertions (new test, not an
   edit to the existing ones); `AbortLogTests` untouched. Labels: the VisionWindow pause button reads
   "Pause all (Ctrl+Shift+F9)"; remove the pause-hotkey row (`PauseHotkey` KeybindCapture) from
   `SettingsFlyout`, and keep `Settings.PauseAllHotkey` in the JSON model, unused, for file
   compatibility. Record both.
7. `ModalDefaultButtonSafetyTests` has hard-coded `InlineData` rows and path helpers that look only
   in `src\UI`. Make the helpers search `src\UI`, then `src\Vision\UI`, and add a row for
   `DangerousKeybindDialog.xaml`. That dialog has `IsDefault="True"` on Confirm; move it to
   "Pick different" (the safe direction: no pulse or trigger behaviour changes, so N6 holds).
   Leave ColorPickerDialog and SettingsFlyout as they are (non-consequential). Record it.

**Gate:** build green; tests green. `ModalDefaultButtonSafetyTests` green with Vision dialogs in scope.

**Commit:** `feat(vision): VisionWindow, the VISION button, tray items and Ctrl+Shift+F9 pause`

## Task 6: headless imports on the merged exe (spec D11)

1. **Keep `src/App.xaml` as the `ApplicationDefinition`; do not touch that csproj block.** Ur OCR
   does the same: with `<StartupObject>` set, the generated Main is simply unused. Add
   `src/Program.cs`:
   `[STAThread] static int Main(string[] args)`: if `args.Length > 0` and `args[0]` is
   `RingImportCommand.Flag` or `PulseImportCommand.Flag` → run that command headless and return its
   code; else `var app = new App(); app.InitializeComponent(); return app.Run();`. Set
   `<StartupObject>Labs626.UrTask.Program</StartupObject>`. `App` keeps its single-instance mutex
   logic in `OnStartup` unchanged.
2. The import commands' "Ur OCR is running" check (exit 3) becomes: the mutex
   `App.SingleInstanceMutexName` exists (`Mutex.TryOpenExisting`; dispose the handle it returns). The message says
   "Close Ur Golem (quit it from the tray, or disable it in RoRoRo) before importing."
3. Imports write to the Vision data dir (Task 4 paths) and the import logs go there too.
4. Tests: the moved `RingImportCommandTests` / `PulseImportCommandTests` stay green (their
   running-check seam, if any, now targets the mutex check; if they asserted the old process name,
   that assertion is the exception to rule 1: change only the probe, record it). New test: with the
   mutex held by the test, both commands return 3 and write nothing.
5. `SingleInstanceGuardTests` stays green.

**Gate / commit:** green; `feat(vision): --import-ring and --import-pulse on the Ur Golem exe`

## Task 7: identity, docs, tools, packaging

1. `manifest.json`: `name` "RoRoRo Ur Golem", `version` "0.13.0", description rewritten in the
   repo voice (one or two sentences: records and plays macros across your alts, and, when you
   switch Vision on, watches the game and acts on what it sees), add capability
   `system.read-screen`. Csproj `Version` 0.13.0, and its `<Product>` and `<Description>` if present
   (they still say Ur Task). `VersionConsistencyTests` green.
2. `tools/vision/`: copy `OCR/tools/ring-sweep.ps1`, `ring-sample.ps1`, `ring-fit.ps1`; fix any
   path they print that names Ur OCR's data folder.
3. Docs:
   - `README.md`: title and intro say Ur Golem (keep "formerly Ur Task" once); a **Vision**
     section built from `OCR/README.md`'s user-facing parts: turn Vision on; **remove Ur OCR**;
     turn on Hide My Pets; import order (ring then pulse, with Ur Golem closed, from the exe with
     `--import-ring` / `--import-pulse`); the pulse only acts on the account in front; F9 is now
     Ctrl+Shift+F9; the consent prompt for "read the screen" on update. Hotkeys table gains
     Ctrl+Shift+F9. Keep every existing Ur Task section.
   - `docs/FEATURES.md`: a Vision section (the headliners from Ur OCR's CHANGELOG 0.6.0 and
     earlier, one line each).
   - `CHANGELOG.md`: new top entry `## 0.13.0 — unreleased` "Ur Task is now Ur Golem" with Added
     (Vision, in-process bridge, Ctrl+Shift+F9, headless imports), Changed (name, TFM, capability,
     `AcceptPluginRunRequests` now gates only the pipe), Migration (copy from the Ur OCR folder,
     remove the Ur OCR plugin), and a link to Ur OCR's history in its own repo.
   - `CLAUDE.md`: add to "The things that will bite you": `vision.log` is the Vision debugging
     surface; Vision is off by default (`VisionEnabled` in `ui-prefs.json`); never run Ur OCR
     beside Ur Golem; imports need Ur Golem closed; the TFM is `…10.0.19041.0` because of Windows
     OCR. Keep the file's voice and brevity.
4. Packaging: run `build/build-plugin.ps1` (read it first; if it hard-codes the old TFM path, fix
   it). Check `artifacts/plugin.zip` contains `626labs.ur-task.exe` and that `manifest.json` in
   `artifacts/` says 0.13.0. Do not commit `artifacts/`.
5. CI: read `.github/workflows/test.yml`; it builds the same csproj, so nothing should change. If
   the Vision `CaptureEngineSmokeTests` needs a desktop that `windows-latest` lacks, it passed in
   Ur OCR's CI on the same image; leave it.

**Gate:** build green, tests green, the zip check passes.

**Commit:** `docs: Ur Task is now Ur Golem, with Vision` (and a separate
`chore(tools): ring tools move in under tools/vision` if cleaner)

## Task 8: acceptance and handoff

1. Walk the spec's acceptance criteria 1 to 12; for each, write in the ledger the command or test
   that proves it and its result. Any that fails: fix it in a new commit, or record why not.
2. Final counts in the ledger: UT baseline, Vision kept, new tests, total.
3. `git push -u origin feat/ur-golem`. Wait for CI (`gh run watch` or `gh run list --branch
   feat/ur-golem`); both jobs must pass. If host-integration fails for a reason unrelated to this
   branch (for example the host repo's main is red), record it with the run URL.
4. Write `docs/session-handoff/2026-10-04-ur-golem.md` (short): what shipped, counts, CI result,
   every judgment call from the ledger, and the spec's **live smoke** list for Este verbatim.
   Commit it.
5. **Stop.** No PR merge, no tag, no release, no install over the live plugin, no change to the
   Ur-OCR repo (spec N5).
