# RoRoRo Ur Task v0.2 — Build Checklist

**Spec:** [`docs/superpowers/specs/2026-05-11-v0.2-design.md`](../docs/superpowers/specs/2026-05-11-v0.2-design.md)
**Plan:** [`docs/superpowers/plans/2026-05-11-v0.2-implementation.md`](../docs/superpowers/plans/2026-05-11-v0.2-implementation.md)
**Started:** 2026-05-11

This checklist is the **operational queue**: what's done, what's blocked, what's next. The plan is *how to build each item*; this is *what to build in what order*.

---

## Phase A — Foundation: schema migration + chord hotkeys

Unblocks every other phase. Land first; the codebase still has v0.1 behavior end-to-end after Phase A, but the data + hotkey layers are positioned for v0.2.

- [ ] **A1 — V2 Macro envelope + sweep all references**
  - **Dependencies:** none
  - **Effort:** ~1.5h
  - **Acceptance:** `Macro` record has v2 fields (`RecordMode`, `RecordedAgainstUserId/DisplayName`, `InterAltDelayMs`). All v0.1 references to `BoundUserId`/`BoundDisplayName`/`BoundAccountId` are gone. `dotnet build` succeeds. Existing `PluginClientIntegrationTests` still pass.
  - **Files:** `src/Macros/Macro.cs`, `src/Macros/MacroPlayer.cs`, `src/Macros/AutoStopCoordinator.cs`, `src/PluginRuntime.cs`, `src/UI/RecorderViewModel.cs`, `src/UI/RecorderWindow.xaml`

- [ ] **A2 — `MacroV1Migrator` + fixture test**
  - **Dependencies:** A1
  - **Effort:** ~1h
  - **Acceptance:** `MacroV1Migrator.LoadAndMigrate(json)` returns a v2 `Macro`. `tests/fixtures/macro-v1.json` deserializes; `MacroV1MigrationTests` (2 tests) pass.
  - **Files:** `src/Macros/MacroV1Migrator.cs`, `tests/fixtures/macro-v1.json`, `tests/rororo-ur-task.Tests/MacroV1MigrationTests.cs`, `tests/rororo-ur-task.Tests/rororo-ur-task.Tests.csproj`

- [ ] **A3 — Wire migrator into `MacroStore.LoadAll`**
  - **Dependencies:** A2
  - **Effort:** ~0.5h
  - **Acceptance:** `MacroStore.LoadAll()` produces v2 `Macro`s from either v1 or v2 JSON on disk. v0.1 macros stored locally still load.
  - **Files:** `src/Macros/MacroStore.cs`

- [ ] **A4 — Hotkey chord defaults**
  - **Dependencies:** A1 (independent of A2/A3 but easier to land after the foundation sweep stabilizes)
  - **Effort:** ~1.5h
  - **Acceptance:** Recording uses `Ctrl+Shift+R`, play uses `Ctrl+Shift+P`, abort uses `Esc`. Bare F5/F8 no longer registered. `MacroRecorder` filters R/P only when Ctrl+Shift is held (so lowercase typing isn't dropped); always filters Esc.
  - **Files:** `src/Hotkeys/HotkeyService.cs`, `src/Macros/MacroRecorder.cs`, `src/PluginRuntime.cs`

- [ ] **A5 — `HotkeyService` registration test**
  - **Dependencies:** A4
  - **Effort:** ~0.5h
  - **Acceptance:** `HotkeyServiceTests` (3 tests) pass — registration + re-registration after dispose, `ChordHotkeyVkCodes` membership, `AbortVkCode` constant.
  - **Files:** `tests/rororo-ur-task.Tests/HotkeyServiceTests.cs`

---

## Phase B — MacroPlayer signature: targetUserId as parameter

Small, mechanical change. Decouples the playback target from the macro itself.

- [ ] **B1 — `MacroPlayer.PlayAsync(macro, targetUserId, ct)`**
  - **Dependencies:** A1
  - **Effort:** ~1h
  - **Acceptance:** `MacroPlayer.PlayAsync` accepts target user-id as a parameter. Pre-flight + mid-playback foreground checks compare against the passed target. `PlaybackStartedArgs` carries `TargetUserId`. `AutoStopCoordinator` reads from event args. Build green, all tests pass. Hotkey play path passes the foreground's user-id.
  - **Files:** `src/Macros/MacroPlayer.cs`, `src/Macros/AutoStopCoordinator.cs`, `src/PluginRuntime.cs`

---

## Phase C — SequencePlayer: batch playback engine

Fully TDD-able; no UI required. After Phase C the engine exists with tests but isn't wired to anything visual yet.

- [ ] **C1 — Sequence types**
  - **Dependencies:** B1
  - **Effort:** ~0.5h
  - **Acceptance:** `SequenceProgress`, `SequenceResult`, `AltOutcome`, `SequencePhase`, public `PlaybackOutcome` with `Skipped` variant. Compile-green.
  - **Files:** `src/Macros/SequenceTypes.cs`, `src/Macros/MacroPlayer.cs`

- [ ] **C2 — Happy path: all-alts-succeed**
  - **Dependencies:** C1
  - **Effort:** ~1h
  - **Acceptance:** `IMacroPlayer` + `IForegroundWatcher` interfaces extracted. `SequencePlayer.PlayAsync(macro, targets, delay)` returns 3/3 completed for a 3-alt happy-path test. `SequencePlayerTests.PlayAsync_AllSucceed_*` passes.
  - **Files:** `src/Macros/SequencePlayer.cs`, `src/Macros/MacroPlayer.cs`, `src/PluginHost/ForegroundWatcher.cs`, `tests/rororo-ur-task.Tests/SequencePlayerTests.cs`

- [ ] **C3 — Skip-on-failure semantics**
  - **Dependencies:** C2
  - **Effort:** ~0.5h
  - **Acceptance:** When alt N's `MacroPlayer.PlayAsync` returns `Refused`, sequence continues to alt N+1. `PerAlt` records the failure. Test `PlayAsync_OneAltFails_SkipsAndContinues` passes.
  - **Files:** `tests/rororo-ur-task.Tests/SequencePlayerTests.cs`

- [ ] **C4 — Mid-sequence abort**
  - **Dependencies:** C2
  - **Effort:** ~0.5h
  - **Acceptance:** `SequencePlayer.Abort()` mid-iteration marks remaining targets as `Skipped`. Test `PlayAsync_AbortMidSequence_RemainingMarkedSkipped` passes.
  - **Files:** `tests/rororo-ur-task.Tests/SequencePlayerTests.cs`

- [ ] **C5 — Focus-flip failure handling**
  - **Dependencies:** C2
  - **Effort:** ~0.5h
  - **Acceptance:** If `SetForegroundWindow` doesn't successfully flip foreground (or pid is stale), the alt is recorded as failed and sequence continues. Test `PlayAsync_FocusFlipFails_*` passes.
  - **Files:** `tests/rororo-ur-task.Tests/SequencePlayerTests.cs`

---

## Phase D — Target picker modal

Modal + viewmodel + runtime routing.

- [ ] **D1 — `PlaybackTargetPickerViewModel` + tests**
  - **Dependencies:** none (pure C# logic, no XAML)
  - **Effort:** ~1h
  - **Acceptance:** Selection state, single-select replacement, multi-select accumulation, order-tag renumbering on deselect, `CanPlay`/`PlayButtonLabel` properties. 5 viewmodel tests pass.
  - **Files:** `src/UI/PlaybackTargetPickerViewModel.cs`, `tests/rororo-ur-task.Tests/PlaybackTargetPickerViewModelTests.cs`

- [ ] **D2 — `PlaybackTargetPickerWindow.xaml` + code-behind**
  - **Dependencies:** D1
  - **Effort:** ~1.5h
  - **Acceptance:** Modal renders with sticky header, scrollable alt list, sticky footer. Keyboard nav (↑↓/Enter/Space/Esc) works. Single-click on a row in single-select mode plays immediately.
  - **Files:** `src/UI/PlaybackTargetPickerWindow.xaml` + `.xaml.cs`

- [ ] **D3 — `PluginRuntime.TriggerPlayMacro` routes through picker**
  - **Dependencies:** D2, C2 (`SequencePlayer` for the multi case)
  - **Effort:** ~1h
  - **Acceptance:** Per-macro PLAY always opens the picker. Single selection → `MacroPlayer.PlayAsync` direct. Multi → `SequencePlayer.PlayAsync`. `Esc` aborts whichever path is active. `SequenceProgressed` event fires on the viewmodel.
  - **Files:** `src/PluginRuntime.cs`

---

## Phase E — Multi-window recording mode

Small phase. Mode metadata + pre-flight modal + AllWindows playback path.

- [ ] **E1 — `RecordMode` state on `PluginRuntime` + writes to saved macro**
  - **Dependencies:** A1
  - **Effort:** ~0.5h
  - **Acceptance:** `PluginRuntime.CurrentRecordMode` defaults to `PerWindow`. New recordings persist their mode. AllWindows recordings don't require a foreground alt at record-start.
  - **Files:** `src/PluginRuntime.cs`

- [ ] **E2 — `MultiWindowConfirmDialog`**
  - **Dependencies:** none
  - **Effort:** ~0.5h
  - **Acceptance:** Dialog with header, warning text, CANCEL + PLAY buttons. `DialogResult` propagates correctly.
  - **Files:** `src/UI/MultiWindowConfirmDialog.xaml` + `.xaml.cs`

- [ ] **E3 — Route AllWindows macros through pre-flight, skip picker**
  - **Dependencies:** E1, E2, D3
  - **Effort:** ~0.5h
  - **Acceptance:** `TriggerPlayMacro` branches: AllWindows shows the first-of-session pre-flight modal (once per session), then calls `MacroPlayer.PlayAllWindowsRawAsync` (no foreground gating, no auto-stop). Subsequent plays in the same session skip the warning.
  - **Files:** `src/PluginRuntime.cs`, `src/Macros/MacroPlayer.cs`, `src/Macros/AutoStopCoordinator.cs`

---

## Phase F — Recorder window UI redesign

Largest UI block. Lands after data + engine are in place so the UI binds against real shape.

- [ ] **F1 — v0.2 viewmodel properties + `RelayCommand<T>`**
  - **Dependencies:** D3, E1
  - **Effort:** ~1.5h
  - **Acceptance:** `RecorderViewModel` has `SequenceProgress`, `IsCompact`, `IsTopmost`, `RecordMode`, `IsRecordModeAllWindows`, `PlayMacroCommand` (RelayCommand<Macro>), `StatusLabel`, `StatusMeta`, `SequenceProgressFraction`, `HasMacros`/`HasNoMacros`. Old `BoundForegroundLabel` + foreground timer removed.
  - **Files:** `src/UI/RecorderViewModel.cs`, `src/UI/RelayCommand.cs`

- [ ] **F2 — `RecorderWindow.xaml` rewrite (card-per-macro layout)**
  - **Dependencies:** F1
  - **Effort:** ~2h
  - **Acceptance:** Window renders the v0.2 layout — header with pin + compact icons, status pill (4 skins), RECORD/ABORT buttons, multi-window toggle, scrollable card list with per-card PLAY button, empty state hint, activity log, footer. `BoolToVisibility` converter added to `App.xaml`. Manual smoke-launch verifies all elements visible and bindings resolve.
  - **Files:** `src/UI/RecorderWindow.xaml`, `src/UI/RecorderWindow.xaml.cs`, `src/App.xaml`

---

## Phase G — Compact mode

After Phase F the full window is stable; now layer in compact.

- [ ] **G1 — Compact row visibility + window resize**
  - **Dependencies:** F2
  - **Effort:** ~1h
  - **Acceptance:** Toggling `IsCompact` hides rows 2-7, resizes window to ~380×110. Toggling back restores 520×640. Pin icon + compact icon visible in both modes.
  - **Files:** `src/UI/RecorderWindow.xaml`, `src/UI/RecorderWindow.xaml.cs`, `src/UI/RecorderViewModel.cs`

- [ ] **G2 — Auto-collapse on sequence start, auto-expand on end**
  - **Dependencies:** G1
  - **Effort:** ~0.5h
  - **Acceptance:** When `SequencePlayer.Progress` fires with `Phase=Focusing` and `Index=0` and `Total>1`, viewmodel auto-sets `IsCompact=true`. On `Phase=Done` or `Phase=Aborted`, restores prior compact state. Single-alt plays don't trigger collapse.
  - **Files:** `src/UI/RecorderViewModel.cs`

- [ ] **G3 — Pin state persistence per mode**
  - **Dependencies:** G1
  - **Effort:** ~0.5h
  - **Acceptance:** `UserPreferences` reads/writes `%LOCALAPPDATA%\626Labs\RoRoRoUrTask\ui-prefs.json`. Pin state per-mode persists across app launches. Compact defaults to `Topmost=true`; full defaults to `Topmost=false`.
  - **Files:** `src/UI/UserPreferences.cs`, `src/UI/RecorderViewModel.cs`

- [ ] **G4 — `Ctrl+Shift+M` window-level keybinding**
  - **Dependencies:** G1
  - **Effort:** ~0.5h
  - **Acceptance:** `ToggleCompactCommand` exists on the viewmodel; `Ctrl+Shift+M` keybinding in `RecorderWindow.xaml` invokes it. Hotkey works when recorder window is focused (does NOT register globally).
  - **Files:** `src/UI/RecorderViewModel.cs`

---

## Phase H — Polish + release

- [ ] **H1 — Icon (attempt 626labs-design; defer if image gen unavailable)**
  - **Dependencies:** none
  - **Effort:** ~0.5–1h
  - **Acceptance:** If the design skill produces a usable 1024×1024 PNG: `icon.png` updated, `icon.ico` generated (16/24/32/48/64/256), `<ApplicationIcon>` set in csproj. If not: file follow-up in 626 dashboard, ship with v0.1 icon, note in release.
  - **Files:** `icon.png`, `icon.ico`, `rororo-ur-task.csproj`

- [ ] **H2 — Version bump 0.1.0 → 0.2.0**
  - **Dependencies:** all prior tasks complete + smoke pass (H3)
  - **Effort:** ~0.25h
  - **Acceptance:** `manifest.json`, `rororo-ur-task.csproj` `<Version>`, `app.manifest` all bumped. Build produces 0.2.0 assembly version.
  - **Files:** `manifest.json`, `rororo-ur-task.csproj`, `app.manifest`

- [ ] **H3 — Smoke checklist run**
  - **Dependencies:** all build tasks complete
  - **Effort:** ~1h
  - **Acceptance:** Every box in `docs/superpowers/plans/2026-05-11-v0.2-smoke-checklist.md` is checked against a real RoRoRo 1.4 dev build with 2+ alts. Any failures filed as follow-ups or fixed inline.
  - **Files:** `docs/superpowers/plans/2026-05-11-v0.2-smoke-checklist.md`

- [ ] **H4 — Tag, push, release**
  - **Dependencies:** H2, H3
  - **Effort:** ~0.5h
  - **Acceptance:** `v0.2.0` tag pushed to GitHub. Existing release workflow builds + publishes GH Release with all artifacts. RoRoRo `MEMORY.md` updated; decision logged in 626 dashboard; Discord clan announcement queued.

---

## Phase I — Documentation & Security Verification (required final phase)

The Vibe Cart canonical close: don't ship without these.

- [ ] **I1 — README / CHANGELOG updates**
  - **Dependencies:** H2
  - **Effort:** ~0.5h
  - **Acceptance:** Top-level `README.md` (if present) reflects v0.2 features — portable macros, multi-select sequential, multi-window mode, compact view, chord hotkeys. `CHANGELOG.md` has a `## 0.2.0 — 2026-05-11` section enumerating the changes (breaking: hotkeys moved to chord defaults).
  - **Files:** `README.md`, `CHANGELOG.md`

- [ ] **I2 — Inline doc comments updated**
  - **Dependencies:** all build tasks
  - **Effort:** ~0.5h
  - **Acceptance:** XML doc comments on `MacroPlayer`, `SequencePlayer`, `PlaybackTargetPickerWindow`, `Macro`, `MacroV1Migrator`, `HotkeyService` accurately describe v0.2 behavior. No stale references to `BoundUserId`/`F5`/`F8` anywhere in `///` comments.
  - **Files:** various `src/**/*.cs`

- [ ] **I3 — Secrets scan**
  - **Dependencies:** none
  - **Effort:** ~0.25h
  - **Acceptance:** `git log -p` and the working tree have no API keys, tokens, credentials, or private URLs. `.env` patterns are gitignored (`.gitignore` already covers `*.env` / `.env.local` / `secrets/`). No hard-coded user-ids in source.
  - **Command:** `git -C C:\Users\estev\Projects\rororo-ur-task log -p | grep -iE "(api[_-]?key|secret|password|token)" | head -50` — should return nothing meaningful.

- [ ] **I4 — Dependency audit**
  - **Dependencies:** none
  - **Effort:** ~0.5h
  - **Acceptance:** Run `dotnet list package --vulnerable --include-transitive` on both csproj files. No high/critical vulnerabilities. Note any moderate ones in the release notes if they need to ship anyway.
  - **Command:** `dotnet list "C:\Users\estev\Projects\rororo-ur-task\rororo-ur-task.csproj" package --vulnerable --include-transitive`

- [ ] **I5 — Deployment security review**
  - **Dependencies:** H4
  - **Effort:** ~0.25h
  - **Acceptance:**
    - GitHub Actions workflow uses pinned action versions (no `@main`/`@master`) — `release.yml` already uses `@v4` for checkout.
    - No write-permission scopes beyond `contents: write` (already minimal).
    - Release artifacts attached to the GH Release are the ones built by CI, not local-side-loaded.
    - Signing: confirm the EXE / Velopack artifacts ship with the same code-signing arrangement as v0.1 (or document the gap).
  - **Files:** `.github/workflows/release.yml`, GH Release UI verification

- [ ] **I6 — Sync MEMORY.md + log dashboard decision + Discord post**
  - **Dependencies:** H4 (tag pushed, release live)
  - **Effort:** ~0.5h
  - **Acceptance:**
    - RoRoRo repo's `MEMORY.md` references a new `project_rororo_ur_task_v0.2.md` (v0.1 stays in audit history).
    - `mcp__626Labs__manage_decisions log` records the v0.2 ship as a `feature` decision.
    - Discord clan announcement single-post covers both RoRoRo v1.4 and Ur Task v0.2 (the clan sees a working plugin system, not a constrained one).

---

## Total effort

~22-28 hours of focused work. Realistic single-engineer estimate for a focused week.

## Critical path

A1 → A2 → A3 → B1 → C2 → D3 → F1 → F2 → G1 → H3 → H4 → I6

A4/A5 (hotkeys) can land in parallel with the B/C track. E1/E2/E3 can land any time after A1. G2/G3/G4 parallel after G1.

## Status

`[ ]` not started · `[~]` in progress · `[x]` done · `[!]` blocked (annotate why inline)
