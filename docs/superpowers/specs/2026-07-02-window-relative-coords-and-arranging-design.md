# Window-relative coordinates + window arranging — design

**Date:** 2026-07-02
**Status:** Approved (Este, this session)
**Target version:** v0.4.0 (schema bump + features)
**Repo:** rororo-ur-task

## Problem

Mouse macros today are screen-fragile. The recorder stores absolute screen pixels
(`MacroRecorder` hook coords) and playback re-injects them against the virtual
desktop (`MacroPlayer`, `MOUSEEVENTF_ABSOLUTE | MOUSEEVENTF_VIRTUALDESK`). A
recorded click only lands if the target window occupies the same screen region it
did at record time — hence the README's magenta "stack all participating windows
at the same quadrant" warning, manual window herding, and monitor-dependence.

Two features fix this:

1. **Window-relative coordinates (schema v3)** — record clicks relative to the
   window's client area; replay them relative to the *target* window's client
   area, wherever it sits.
2. **Window arranging suite** — one-click Stack and Grid layouts for the running
   alt windows (legacy-macro compat + round-robin monitoring).

## Approaches considered

- **A. Window-relative client coords (schema v3)** — record `ScreenToClient`,
  play `ClientToScreen`, auto-resize target to the recorded client size. Exact
  replay anywhere, any monitor. **Chosen.**
- **B. Proportional coords** (x/y as 0–1 fractions of client size, scaled at
  playback) — no resize needed, but Roblox HUD elements are fixed-size and
  corner/center-anchored; scaled clicks drift on mismatched sizes. Rejected.
- **C. Keep absolute coords + auto-stack windows** — smallest change, keeps all
  of today's fragility. Rejected as the coordinate fix; its window-positioning
  muscle survives as the arranging suite (approved as a feature family, not
  just stack).

## Decisions (made with Este)

| Decision | Choice | Rationale |
|---|---|---|
| Existing v2 mouse macros | **Play as-is + warning** | Absolute coords can't be retro-converted (window rect at record time unknown). Nothing breaks on update; users migrate by re-recording. |
| Client-size mismatch at playback | **Auto-resize target, leave it resized** | Round-robin resizes each window once, not every cycle. Refuse only when the size can't be achieved. |
| Arranging scope | **Suite: Stack + Grid** | Stack serves legacy `screen` macros; Grid serves watching the round-robin. Expanded from stack-only by Este. |

## Design

### 1. Schema v3

`Macro` gains:

- `CoordSpace` — `"screen"` (legacy) \| `"client"` (new). Macro-level, not
  per-event.
- `RecordedClientW` / `RecordedClientH` — physical pixels, present only when
  `CoordSpace == "client"`.

Rules:

- Loader migrates v2 → v3 with `CoordSpace = "screen"` (exact today-behavior).
  Migration is sticky on next save, following the `MacroV1Migrator` precedent.
- New **per-window** recordings → `CoordSpace = "client"`.
- **Multi-window (AllWindows) recordings stay `"screen"`** — raw replay has no
  anchor window to be relative to. Unchanged semantics.
- Keyboard-only macros: coords are never read; `CoordSpace` is set but inert.
- `Macro.CurrentSchemaVersion` bumps 2 → 3.

### 2. Recording (per-window mode)

- Capture the bound window's HWND at record start (PID → HWND via the existing
  `Win32Focus` pattern: `Process.GetProcessById(pid).MainWindowHandle`).
- Every mouse event converts `ScreenToClient` **at capture time** — the user
  moving the window mid-recording stays correct because each event is converted
  against the window's rect at that instant.
- Record the client size (`GetClientRect`) at record start into
  `RecordedClientW/H`. Mid-recording window **resizes** are unsupported: events
  still convert correctly against the rect at each instant, but the stored
  size is the record-start size — if the size changed by record stop, log a
  warning advising a re-record. (Mid-recording *moves* are fully supported.)
- Clicks landing outside the client area (other monitor, chrome, taskbar)
  record faithfully (coords may be negative or exceed client bounds) and replay
  faithfully relative to the target window origin. No clamping, no dropping —
  faithful replay is the contract.
- The process is PerMonitorV2 DPI-aware (app.manifest), so all rects and coords
  are honest physical pixels. No DPI translation layer needed.

### 3. Playback (`CoordSpace == "client"`)

Per target alt, before injecting events:

1. Resolve target HWND (same PID → HWND muscle).
2. `GetClientRect` — if client size ≠ `RecordedClientW/H`:
   - One `SetWindowPos` sizing the **outer** rect by the client-size delta
     (outer = current outer + (recorded client − current client)), then
     re-measure to verify. One correction pass is sufficient because chrome
     size is constant for a given window style + DPI.
   - Verified match → proceed. Can't match (monitor work area too small, or
     Roblox's minimum-size floor prevents shrinking) → **refuse that alt** with
     a clear activity-log line; sequence continues on the rest (existing
     skip-on-failure semantics).
   - The window is **left resized** after playback.
3. Every mouse event maps `ClientToScreen` **at inject time** — mid-playback
   window moves stay correct.

`CoordSpace == "screen"` macros take today's path unchanged, plus a one-line
activity-log advisory when they contain mouse events ("legacy screen-coordinate
macro — window position matters; use Stack or re-record").

### 4. Window arranging suite

New `WindowArranger` component: pure layout math + a thin Win32 apply step.

- **Stack** — move+size every running alt window to the same rect. Anchor =
  the foreground alt's current rect; if no alt is foreground, the first alt in
  the registry snapshot.
- **Grid** — tile all running alt windows across the work area of the monitor
  hosting the anchor window. `cols = ceil(sqrt(n))`, `rows = ceil(n / cols)`,
  row-major fill. Cells clamp to Roblox's minimum window size — discovered at
  apply time (set, re-measure; the window enforces its own floor via
  `WM_GETMINMAXINFO`), never hardcoded. If the work area can't fit the grid at
  minimum size, windows overlap in cascade order and the activity log says so.
  Taskbar-aware (work area, not monitor bounds).
- UI: two buttons in the recorder window — `STACK` and `GRID` — operating on
  the current `AccountRegistry` snapshot. Disabled when no alts are running.
- Layout math (N windows + anchor rect + work area → target rects) is a pure,
  injected-input function — fully unit-testable without real windows.

### 5. Riders

- **manifest.json description fix** — still carries v0.1 "playback refuses
  unless the foreground window matches" language; enforcement was removed in
  v0.2. Ships with this release (manifest changes ride version bumps).
- README: magenta stacking warning becomes legacy-only (`screen` macros with
  mouse events); document window-relative recording + the arranging buttons;
  retire "planned for a future release."
- CHANGELOG entry for 0.4.0.

### 6. Testing seams (TDD)

- **Coordinate conversion** — screen↔client mapping as pure functions over an
  injected window-rect provider. Round-trip and offset tests, negative/out-of-
  bounds coords included.
- **Size-delta math** — outer-rect correction computation (recorded client vs
  current client vs current outer) as a pure function.
- **Schema v3** — serialization round-trip (`CoordSpace`, `RecordedClientW/H`,
  null-omission for screen-space macros); v2 → v3 migration (mouse-bearing and
  keyboard-only cases); migration stickiness on save.
- **Grid/Stack layout math** — N-window rect computation: 1, 2, 3, 4, 5, 9
  windows; min-size clamping; overlap fallback.
- **Playback refusal** — size-can't-match path returns skip-on-failure, not
  abort (fake window provider).
- Win32 calls (`SetWindowPos`, `GetClientRect`, `ScreenToClient`,
  `ClientToScreen`) stay thin untested wrappers, same pattern as today's
  `Win32Focus` / `MacroPlayer` interop.

## Out of scope

- Proportional/scaled coordinate playback (approach B) — rejected.
- Cascade or custom layout presets — Stack + Grid only in v1.
- Retro-converting v2 mouse macros — impossible without record-time window
  rects.
- Cross-plugin coordinate work (Ur-OCR window-anchored trigger regions) — same
  concept, separate repo and spec (parked as authoring-suite phase 2).

## Version & release

- v0.4.0, schema v3 (macro shape change ⇒ minor bump per repo convention).
- Same manifest shape otherwise; no new capabilities — window manipulation via
  `SetWindowPos` is process-level Win32, no host contract change. Host
  requirement stays RoRoRo v1.4.3.0+.
