# Changelog

All notable changes to RoRoRo Ur Task are documented here. Format roughly follows [Keep a Changelog](https://keepachangelog.com/en/1.1.0/); versioning follows [SemVer](https://semver.org/).

## 0.7.0 — unreleased

> **Not yet shipped.** Version bumped and notes drafted ahead of the tag so the release is a
> tag-and-go once PR #30's live smoke passes. If PR #31 (take the theme from the host) merges
> before the tag, its entry belongs in this section too.

### Changed

- **Keep-alives run on a schedule instead of a spin loop.** The old round-robin woke every alt in
  turn on a fixed tick whether or not anything was due. A deadline scheduler now decides what
  actually needs to fire and when, sleeps when nothing does, and fits keep-alives into the gaps
  between real work. The visible effect: a keep-alive alt leaves your desktop usable, and an alt
  set to Active steals focus about **once per 30 seconds rather than once per second**.
- **Keep-alive intervals are game-aware**, with a per-game override when a game needs something
  different from the default.

### Added

- **Roles, presets, and a next-due countdown** in the assignment grid, so you can see what the
  scheduler intends to do next rather than inferring it from focus stealing.
- **An up-front warning when an alt cannot be kept alive** at the cadence you asked for, instead of
  quietly missing the deadline forever.
- **A heartbeat claim file** so Ur AFK stays off alts this plugin is already managing — the two
  plugins stop fighting over the same account.
- **Foreground capture and restore.** A macro that needs focus takes it, then gives back the window
  you were actually using.

### Fixed

- **Enter no longer deletes a macro.** All three dialogs had the affirmative button as the Enter
  default and no Esc route at all: Enter deleted a recording, Enter dismissed the multi-window
  warning by doing exactly what it warned about, and Esc did nothing anywhere. Delete and
  multi-window playback now default to CANCEL; rename still submits on Enter, which is correct for
  a text box. Esc closes all three.
- **Claim-file races**, a **setup-window claim leak**, and **stale assignments** left behind by a
  cancelled run.
- **Focus-failure toast spam**, and the unschedulable warning now fires on a real projected gap
  rather than an axis mismatch.
- **A hot spin on Active alts**, plus forward-progress and restore-on-cancel guards, and a cost
  estimate that corrects itself instead of drifting.

### Internal

- **The one test that checks against the real RoRoRo host had stopped compiling** and nothing
  noticed, because CI never built it. Repaired, and a `host-integration` CI job now builds it on
  every PR so it cannot rot again silently.
- Pre-commit secret-scan and local-path guards, a CLAUDE.md, and the 0.6.0 changelog entry that
  shipped missing.

## 0.6.0 — 2026-07-12

> Reconstructed 2026-08-11 from the 36 commits between `v0.5.0` and `v0.6.0`. The release shipped
> without a changelog entry; this records what went out rather than leaving a gap between 0.5.0 and
> whatever comes next. Grouped from commit subjects, so it describes the changes accurately but is
> terser than an entry written at release time.

### Added

- **Recipes and loadouts.** A saved-recipes library — create, edit, delete, and run a sequence
  against a chosen alt. Recipes loop; loadouts run once and finish. Both get their own section
  above assignments, type badges, and a run surface folded into the assignment grid so a routine
  starts where the alts already are. Ctrl+Shift+L and the RUN button both toggle start/stop.
- **Export a macro to AutoHotkey**, in both v1 and v2 syntax — your recording leaves in a format
  that outlives the plugin.
- **Resize the main window from any edge or corner**, not just the corner grip.
- **Themed auto-dismissing toast** for playback errors.

### Fixed

- **Playback reaches the window size it recorded.** A run of fixes to window positioning: move a
  low window up before resizing so the target size is reachable, maximize-then-settle for
  full-screen recordings, reproduce a recorded client size including work-area overhang, and keep
  the resized window inside the work area so clicks never land off-screen. Genuine size failures
  now advise instead of silently mis-clicking.
- **Assignments stay usable at small window sizes.**
- **A recipe aborts on host loss or dispose**, and plain-loop and recipe modes exclude each other
  in both directions.
- **AssignmentRunner single-flight guard**, and the macro library loads once per recipe run rather
  than per step.

## 0.5.0 — 2026-07-03

### Added

- **Shareable macros.** EXPORT in the macro library writes your whole collection (or a single macro via the card's ⋯ → Export…) to a portable version-stamped bundle file; IMPORT reads bundles *or* bare macro files shared straight out of someone's `macros\` folder. Every imported entry runs through the same migrator as the on-disk store, so a friend still on v0.1/v0.2 can share with you (and, because the macro schema stayed v3 by design, a v0.5 build can read bundles from future versions too). Imports are additive and can never overwrite what you have: every macro gets a fresh id, name collisions dedupe with a " (2)" suffix, and a broken entry in a shared bundle is skipped with a note in the activity log — it never sinks the rest of the batch.
- **Ur Task follows your RoRoRo theme — live.** The plugin reads the host's active theme (built-ins and your custom theme files alike) straight from RoRoRo's settings on disk and re-paints itself, including while both apps are running: switch themes in RoRoRo and the plugin follows in about two seconds. No plugin-contract change, no pipe traffic — and if RoRoRo isn't installed, the plugin simply keeps its brand look. One caveat for theme tinkerers: the three built-in palettes are mirrored in plugin code, so if a future RoRoRo changes its built-ins, the plugin needs a matching update (custom theme files are always read live).
- **Crash diagnostics.** The plugin now leaves evidence when things go wrong: an append-only rolling log at `%LOCALAPPDATA%\626Labs\RoRoRoUrTask\logs\ur-task.log` with a session header, per-step startup breadcrumbs, the activity log teed to file, and log-then-crash-loud exception handlers (a silent crash is worse than a loud one — nothing is swallowed). A startup watchdog reports the failure mode exception handlers structurally cannot see: the exception-free windowless hang (exactly the bug fixed below). The tray menu gains **Open log folder**; a clean session always ends with an "exiting cleanly" line — its absence in the log *is* the evidence.

### Changed

- **Two-pane dashboard.** The recorder window grows from a 520×720 single stacked column into an 860×560 dashboard: status pill on top, an action bar (RECORD / ABORT / PLAY-STOP toggle on the left, STACK · GRID · RESET · CLEAR on the right), the macro library on the left — finally free to use the window's full height instead of the old fixed 140px strip — and assignments over the activity log on the right. Pin and compact moved into the custom title bar so they stay reachable in compact mode, which is unchanged (Ctrl+Shift+M, same slim strip). IMPORT/EXPORT live in the MACROS pane header.
- **Plugin contract bumped 0.1.0 → 0.3.0**, matching Ur AFK. Both intervening contract releases were additive, so nothing about the host conversation changes — this just puts the whole plugin family on the same page (and stages the game-aware features specced for v0.6).

### Fixed

- **Launching a second Ur Task no longer hangs it forever.** With one instance already running (owning the action-bridge pipe), a second launch spun the UI thread in a synchronous retry loop before the window could even be created — windowless, one core pegged, and *zero* exceptions for any handler to catch. The accept loop now yields before touching the pipe and bows out cleanly when another instance owns the bridge. Found by the automated visual pass for this release; the new startup watchdog exists so this class of bug can never hide again.

Same host requirement as v0.3.x/v0.4.x — RoRoRo v1.4.3.0+.

## 0.4.1 — 2026-07-03

### Changed

- **Plugin icon joins the Ur family set.** Ur Task previously used the generic 626 Labs brain logo; it now has its own mark in the shared family style (flat-top hexagon, cyan stroke, cyan/magenta swoosh): a record dot and play triangle under a repeat arc — record once, replay anywhere. Matches Ur OCR (scan) and Ur AFK (heartbeat over a keyboard). No code changes.

## 0.4.0 — 2026-07-03

### Added

- **Window-relative mouse macros (schema v3).** Per-window recordings now store mouse positions relative to the recorded window's client area, plus the recorded client size. Playback resizes the target window once to match (refusing with a clear reason when it can't — monitor too small or window minimum) and maps every event onto the target window wherever it sits, on any monitor. No more stacking windows for mouse macros. Keyboard-only recordings (the default) are unaffected — they carry no coordinates and never trigger a resize. Existing macros keep playing exactly as before (absolute screen coordinates) with a one-line advisory in the activity log; re-record to upgrade. Multi-window recordings keep raw absolute replay. v1/v2 macro files migrate to v3 on load; migration is sticky on save.
- **Window arranging suite.** Buttons in the recorder window to wrangle your alt windows: **STACK** minimizes every running alt (gets them out of the way — the common starting state); **GRID** tiles all running alts across the monitor's work area so you can watch the round-robin; **RESET** puts every alt back exactly where it was before you stacked or gridded (and un-minimizes it); **CLEAR** wipes the macro-to-alt assignments. STACK and GRID snapshot each window's position once per cycle, so RESET always returns to the true originals even after a STACK-then-GRID. Window size is preserved on restore — no scaling — which keeps macros portable. Taskbar-aware; grids that can't fit at minimum window size overlap in cascade order and say so in the activity log.

### Fixed

- **Themed the recorder's window-control buttons.** Pin, Compact, STACK, GRID, and RESET were falling back to default white Windows chrome; they now match the app's navy theme.
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
