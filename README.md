# RoRoRo Ur Task

> Portable macro recording for [RoRoRo](https://github.com/estevanhernandez-stack-ed/ROROROblox)-managed Roblox alts. Record once, play on any alt — with round-robin assignments + AFK keep-alive, multi-select sequential playback, multi-window mode, compact view for long sequences, and an action bridge so sibling plugins (like [RoRoRo Ur OCR](https://github.com/estevanhernandez-stack-ed/Ur-OCR)) can fire macros on screen triggers.

Macros are now **fully portable** — no account binding. Pick one or more alts at play time and the macro runs on each in sequence. Multi-window recording mode (experimental) captures across multiple windows for flows that switch alts themselves. Compact mode collapses to a slim always-on-top strip so you can watch the alts while the sequence plays.

## How it works

RoRoRo Ur Task is a [RoRoRo plugin](https://github.com/estevanhernandez-stack-ed/ROROROblox/blob/main/docs/plugins/AUTHOR_GUIDE.md). It runs as a separate Windows EXE, connects to RoRoRo over a named pipe (`\\.\pipe\rororo-plugin-host`), and subscribes to RoRoRo's `account-launched` + `account-exited` events.

**Recording:** captures keyboard + mouse events (globally, even across windows if multi-window mode is enabled). Stores the current foreground alt's user-id as metadata (for reference only — not enforced at playback).

**Playback:** opens a target picker; you pick one or more alts and the macro runs on each in sequence. Multi-window mode replays raw events without foreground gating; single-window (default) gates every event to the active foreground window. Skip-on-failure: if one alt's window closes mid-sequence, playback continues on the rest. End-of-sequence summary logs to the activity view.

**Assignments (round-robin):** assign a macro to each running alt — or leave an alt unassigned and it gets a periodic Space jump (keep-alive) so it dodges AFK kicks. `Ctrl+Shift+P` (or the PLAY ASSIGNMENTS button) cycles through every running alt in a loop — focus, play its macro, move to the next — until you stop it.

**Action bridge (v0.3+):** Ur Task listens on a local named pipe (`\\.\pipe\626labs-ur-task`, current-user only) for `RunMacro` requests from sibling plugins. This is what lets [RoRoRo Ur OCR](https://github.com/estevanhernandez-stack-ed/Ur-OCR) fire a specific macro when a color/text screen trigger matches — the perception→action loop, no AutoHotkey needed. Gated by the "Accept run requests from other plugins" preference (default on).

## Capabilities declared

| Capability | Why |
|---|---|
| `system.synthesize-keyboard-input` | Playback synthesizes keys via `SendInput`. |
| `system.synthesize-mouse-input` | Playback synthesizes mouse moves + clicks. |
| `system.watch-global-input` | Recording captures keyboard + mouse globally. |
| `host.events.account-launched` | Builds the pid → user-id map. |
| `host.events.account-exited` | Auto-stop when a target alt's window closes. |
| `host.ui.row-badge` | Per-account "recording" / "playing" indicator (renders in v1.4.1+ when RoRoRo's host-side UI lands). |
| `host.ui.tray-menu` | Informational status entry (same — v1.4.1+ rendering). |

The three `system.*` capabilities are disclosure-only — they don't gate calls, they tell users honestly what the plugin does. RoRoRo surfaces them on the consent sheet at install time; you can opt-out of any of them, but the plugin refuses to record / play without the corresponding consent.

## Install

You need RoRoRo installed first ([v1.4.3 or later](https://github.com/estevanhernandez-stack-ed/ROROROblox/releases)). Older hosts will refuse the install with a clear "Update RoRoRo" message.

1. Open RoRoRo → Plugins → Install.
2. Paste this URL: `https://github.com/estevanhernandez-stack-ed/rororo-ur-task/releases/latest/download/` — this always resolves to the newest stable release (GitHub redirects `latest/download/`), so it never needs bumping per version.
3. Walk the consent sheet. The three `system.*` capabilities are required for the plugin to function.
4. Click Install.

RoRoRo Ur Task starts in your system tray immediately on install (its own icon, separate from RoRoRo's tray). Click the tray icon to surface the recorder window.

## Recording mode and the mouse-click caveat

**By default, recording is keyboard-only** — mouse events (clicks, moves, wheel) are dropped during capture. Keyboard events route to whichever window has focus, which is exactly right for the dominant use case (jumps, walks, key-combo grinding).

If you need mouse capture (drag flows, click-precision sequences), untick "Record keyboard only" in the recorder window. As of **v0.4.0**, per-window mouse recordings are **window-relative**: positions are stored relative to the recorded window's client area, and playback resizes the target window to match and lands every click in the right spot — wherever the window sits, on any monitor. No window stacking required.

**Legacy mouse macros** (recorded before v0.4.0) still use absolute screen coordinates: they play exactly as before, and the target window must occupy the same screen region as at record time. Use the **STACK** button to line windows up for them — or just re-record to upgrade.

Playback of a window-relative macro refuses cleanly (and skips to the next alt) when the target window can't reach the recorded size — monitor too small, or below the window's minimum.

## Window arranging

Two buttons in the recorder window operate on all running alts:

- **STACK** — moves every alt window to the same position and size (anchored on the foreground alt). What legacy screen-coordinate mouse macros need.
- **GRID** — tiles all alt windows across the monitor's work area so you can watch the round-robin visit each one. If they can't fit at minimum size, they overlap in cascade order (the activity log says so).

## Hotkeys

| Key | Action | Scope |
|---|---|---|
| `Ctrl+Shift+R` | Start recording (or stop if already recording). | Global |
| `Ctrl+Shift+P` | Play assignments — run the round-robin loop across all running alts. Press again to stop. | Global |
| `Ctrl+Shift+A` | Abort current playback. | Global |
| `Ctrl+Shift+M` | Toggle compact mode (always-on-top strip). | Window-level |
| `Esc` | Abort current playback — but only while a macro is playing, so Esc stays yours the rest of the time. | Global (during playback only) |

**Note:** v0.1 shipped with F8 (record) and F5 (play) — these have moved to `Ctrl+Shift+R` and `Ctrl+Shift+P` to avoid hijacking browser and IDE refresh keys. As of **v0.3.1**, abort is `Ctrl+Shift+A`; bare `Esc` still aborts but is only claimed while a macro is playing, so it no longer intercepts Esc system-wide. Per-macro PLAY buttons in the recorder UI show the updated labels.

## License

MIT © 626 Labs LLC. The reference contract bindings (`ROROROblox.PluginContract`) ship under the same license — see the parent RoRoRo repository.

---

**A 626 Labs product · *Imagine Something Else*.**
