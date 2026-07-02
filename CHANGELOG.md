# Changelog

All notable changes to RoRoRo Ur Task are documented here. Format roughly follows [Keep a Changelog](https://keepachangelog.com/en/1.1.0/); versioning follows [SemVer](https://semver.org/).

## 0.4.0 — 2026-07-02

### Added

- **Window-relative mouse macros (schema v3).** Per-window recordings now store mouse positions relative to the recorded window's client area, plus the recorded client size. Playback resizes the target window once to match (refusing with a clear reason when it can't — monitor too small or window minimum) and maps every event onto the target window wherever it sits, on any monitor. No more stacking windows for mouse macros. Existing macros keep playing exactly as before (absolute screen coordinates) with a one-line advisory in the activity log; re-record to upgrade. Multi-window recordings keep raw absolute replay. v1/v2 macro files migrate to v3 on load; migration is sticky on save.
- **Window arranging suite.** Two new buttons in the recorder window: **STACK** moves every running alt window to the same position and size (what legacy screen-coordinate mouse macros need); **GRID** tiles all running alts across the monitor's work area so you can watch the round-robin. Taskbar-aware; grids that can't fit at minimum window size overlap in cascade order and say so in the activity log.

### Fixed

- **Manifest description no longer claims v0.1 bound-playback behavior** ("playback refuses unless the foreground window matches" — binding was removed in v0.2).

Same host requirement as v0.3.x — RoRoRo v1.4.3.0+.

## 0.3.1 — 2026-06-30

### Fixed

- **Esc no longer hijacked system-wide while the plugin runs.** The abort hotkey was a *global* bare-Esc registration held for the plugin's entire lifetime — so whenever Ur Task was running, Windows routed every Esc press to the plugin's message queue and never to the foreground app. Plain Esc looked dead in every program; only Shift+Esc (an unregistered chord) slipped through to apps. Reported in the wild during a tourney week of heavy plugin use — Esc that "needs Shift to work." Two changes close it: (1) abort is now the chord **Ctrl+Shift+A**, registered for the plugin's lifetime and never stealing a bare key — matching what v0.2.0 already did for record/play (bare F8/F5 → chords); (2) bare **Esc** is still an abort key, but registered on demand *only while a macro is actually playing* and unregistered the instant playback stops, so Esc belongs to you the rest of the time. The on-demand (un)registration runs on the hotkey pump thread via posted messages, since `RegisterHotKey` binds the hotkey to the calling thread.

Same manifest shape and host requirement as v0.3.0 — RoRoRo v1.4.3.0+.

## 0.3.0 — 2026-06-29

### Added

- **Action bridge — Ur Task runs macros on request from sibling plugins.** A named-pipe server (`\\.\pipe\626labs-ur-task`, current-user only) accepts a `RunMacro` request and plays a stored macro on resolved alts. This is what lets RoRoRo Ur OCR fire a specific macro when a screen trigger matches — the perception→action loop, native and account-safe. Pref-gated ("Accept run requests from other plugins", default on). Acks on accept — playback runs fire-and-forget, so a long macro never blocks the caller.

### Fixed

- **Atomic re-entry guard on the sequence player.** A hotkey-driven sequence and a bridge request arriving in the same window can no longer interleave input — the second is refused rather than clobbering the first.

## 0.2.3 — 2026-06-29

### Fixed

- **Default keep-alive now actually works.** Unassigned alts in the round-robin send a Space jump to dodge AFK kicks — but the keypress was never reaching Roblox, a silent no-op since v0.2.0. The keep-alive's `SendInput` call passed a too-small `INPUT` struct: the self-contained interop copy had dropped the mouse field, so its `cbSize` measured 32 bytes instead of the canonical 40. Windows rejects any `SendInput` whose `cbSize` doesn't match `sizeof(INPUT)`, and the rejected return value was discarded — so the failure was invisible. Recorded macros were never affected; they use a separate, correctly-sized code path. Restored the struct, stopped swallowing the `SendInput` return, and added a regression test that locks the struct size.

Same manifest shape and host requirement as v0.2.2 — RoRoRo v1.4.3.0+.

## 0.2.2 — 2026-05-16

### Changed

- **Requires RoRoRo v1.4.3.0 or newer.** Older hosts now get a clear "Update RoRoRo" error at install time instead of the silent failure-to-start mode that v0.2.1 fell into on v1.4.2.0.
- **Fresh installs start automatically.** New `autostartDefault: "on"` manifest flag tells the v1.4.3+ host to default the autostart preference to on for first-time installs — no more toggle-and-restart dance after install. Re-installs preserve the existing consent record (user choice always wins).
- Manifest now declares `entrypoint: "626labs.ur-task.exe"` explicitly. Redundant with the host's default guess for single-EXE plugins like this one, but documents intent.

No code changes. Same binary shape as v0.2.1.

## 0.2.1 — 2026-05-15

### Changed

- **Build now consumes `ROROROblox.PluginContract` from nuget.org** instead of the on-disk `ProjectReference`. Plugin authors can now `dotnet add package ROROROblox.PluginContract` exactly as `AUTHOR_GUIDE.md` describes. Release CI drops the sibling-repo checkout step — the NuGet-only pipeline produces this build end-to-end.

No user-facing functional changes. Same binary shape as v0.2.0.

## 0.2.0 — 2026-05-11

### Added

- **Portable macros.** Record once, play on any RoRoRo-managed alt. Schema bumped 1 → 2; v0.1 macros auto-migrate on load.
- **Multi-select sequential playback.** Per-macro PLAY opens a target picker; check one or more alts (with click-order tags) and the macro runs on each in sequence. Skip-on-failure (one alt's window closes → sequence continues on the rest). End-of-sequence summary in the activity log.
- **Multi-window recording mode** (experimental). Opt-in toggle in the recorder window. Captures input across all windows; playback replays raw events (no foreground gating, no auto-stop). First play in a session pops a pre-flight confirm.
- **Compact mode.** `⌐` icon (or `Ctrl+Shift+M`) collapses to a ~380×110 always-on-top strip — header + status pill only — so the alt windows are visible during sequence playback. Auto-collapses on sequence start, auto-restores on end.
- **Pin (📌) toggle** with per-mode persistence. Compact defaults pinned on; full mode defaults pinned off. Each mode's pin state is remembered between sessions.
- **Card-per-macro recorder UI.** Per-card PLAY button retires the "PLAY LAST" button. Status pill skin reflects four states (idle, recording, playing single, playing sequence with progress bar).

### Changed

- **Hotkeys are now chord defaults** (BREAKING from v0.1):
  - Record/Stop: `Ctrl+Shift+R` (was bare `F8`)
  - Play last: `Ctrl+Shift+P` (was bare `F5`)
  - Abort: `Esc` (unchanged)
  - Compact toggle: `Ctrl+Shift+M` (new, window-level only — not a global hotkey)
  - Rationale: bare F-keys hijacked those keys system-wide (browser refresh, IDE reload, Roblox Studio play). Matches the modern TinyTask default pattern.
- `MacroPlayer.PlayAsync` accepts the target user-id as an explicit parameter, decoupled from the macro metadata.
- Recorder window UI fully redesigned (card layout, status pill, multi-window toggle, empty state).

### Removed

- `BoundUserId` / `BoundAccountId` / `BoundDisplayName` from the `Macro` record. Replaced by `RecordedAgainstUserId` / `RecordedAgainstDisplayName` (informational metadata, not enforced).
- "FOREGROUND WINDOW" widget in the recorder (the target picker resolves on-demand at play time; no continuous polling needed).
- "PLAY LAST" button — every macro card has its own `PLAY` now. `Ctrl+Shift+P` still plays the last macro on the smart-default target (foreground alt).

### Known gaps

- Custom Ur Task icon (vibe-X-square family) deferred to v0.2.1 — image-gen tooling wasn't available in the v0.2 build session. v0.2 ships with the v0.1 generic 626 brand mark. See `docs/v0.2-icon-deferral.md`.

### Migration notes

- v0.1 macros on disk auto-migrate to v2 on first load — no user action required. `BoundUserId/DisplayName` get mapped to `RecordedAgainstUserId/DisplayName` (now informational). Migration is sticky: the next save persists in v2 shape.
- Hotkey muscle memory: v0.1 users will need to learn the new chords. The recorder window buttons show the new chord labels.

## 0.1.0 — 2026-05-11

Initial release. WPF plugin EXE connecting to RoRoRo's gRPC plugin host. Recorded keyboard + mouse macros bound to a specific Roblox user-id; refused playback on non-bound alts; auto-stopped on bound-alt window close. F8 record/stop · F5 play last · Esc abort.
