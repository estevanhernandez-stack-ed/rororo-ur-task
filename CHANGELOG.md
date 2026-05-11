# Changelog

All notable changes to RoRoRo Ur Task are documented here. Format roughly follows [Keep a Changelog](https://keepachangelog.com/en/1.1.0/); versioning follows [SemVer](https://semver.org/).

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
