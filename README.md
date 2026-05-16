# RoRoRo Ur Task

> Portable macro recording for [RoRoRo](https://github.com/estevanhernandez-stack-ed/ROROROblox)-managed Roblox alts. Record once, play on any alt — now with multi-select sequential playback, multi-window mode, and compact view for long sequences.

Macros are now **fully portable** — no account binding. Pick one or more alts at play time and the macro runs on each in sequence. Multi-window recording mode (experimental) captures across multiple windows for flows that switch alts themselves. Compact mode collapses to a slim always-on-top strip so you can watch the alts while the sequence plays.

## How it works

RoRoRo Ur Task is a [RoRoRo plugin](https://github.com/estevanhernandez-stack-ed/ROROROblox/blob/main/docs/plugins/AUTHOR_GUIDE.md). It runs as a separate Windows EXE, connects to RoRoRo over a named pipe (`\\.\pipe\rororo-plugin-host`), and subscribes to RoRoRo's `account-launched` + `account-exited` events.

**Recording:** captures keyboard + mouse events (globally, even across windows if multi-window mode is enabled). Stores the current foreground alt's user-id as metadata (for reference only — not enforced at playback).

**Playback:** opens a target picker; you pick one or more alts and the macro runs on each in sequence. Multi-window mode replays raw events without foreground gating; single-window (default) gates every event to the active foreground window. Skip-on-failure: if one alt's window closes mid-sequence, playback continues on the rest. End-of-sequence summary logs to the activity view.

## Capabilities declared

| Capability | Why |
|---|---|
| `system.synthesize-keyboard-input` | Playback synthesizes keys via `SendInput`. |
| `system.synthesize-mouse-input` | Playback synthesizes mouse moves + clicks. |
| `system.watch-global-input` | Recording captures keyboard + mouse globally. |
| `host.events.account-launched` | Builds the pid → user-id map. |
| `host.events.account-exited` | Auto-stop when the bound alt's window closes. |
| `host.ui.row-badge` | Per-account "recording" / "playing" indicator (renders in v1.4.1+ when RoRoRo's host-side UI lands). |
| `host.ui.tray-menu` | Informational status entry (same — v1.4.1+ rendering). |

The four `system.*` capabilities are disclosure-only — they don't gate calls, they tell users honestly what the plugin does. RoRoRo surfaces them on the consent sheet at install time; you can opt-out of any of them, but the plugin refuses to record / play without the corresponding consent.

## Install

You need RoRoRo installed first ([v1.4.3 or later](https://github.com/estevanhernandez-stack-ed/ROROROblox/releases)). Older hosts will refuse the install with a clear "Update RoRoRo" message.

1. Open RoRoRo → Plugins → Install.
2. Paste this URL: `https://github.com/estevanhernandez-stack-ed/rororo-ur-task/releases/download/v0.2.2/`
3. Walk the consent sheet. The four `system.*` capabilities are required for the plugin to function.
4. Click Install.

RoRoRo Ur Task starts in your system tray immediately on install (its own icon, separate from RoRoRo's tray). Click the tray icon to surface the recorder window.

## Recording mode and the mouse-click caveat

**By default, recording is keyboard-only** — mouse events (clicks, moves, wheel) are dropped during capture. This is the safe default because mouse coordinates are absolute screen pixels: a recorded click only lands correctly if the target alt's window is at the same screen position it was when you recorded. For the dominant use case (jumps, walks, key-combo grinding) this isn't a problem at all — keyboard events route to whichever window has focus, and the plugin handles per-alt focus during the round-robin.

If you need mouse capture (drag flows, click-precision sequences), untick "Record keyboard only" in the recorder window. A magenta warning appears: **stack all participating Roblox windows at the same screen quadrant** before playback. Win+Arrow snaps to halves/quadrants; a window-manager utility can stack them precisely. The round-robin will then send recorded clicks to the same pixel each cycle, and since the windows occupy the same screen region, every alt receives clicks on the right UI element.

Window-relative coordinates (record once, replay at any window position) is planned for v0.3.

## Hotkeys

| Key | Action | Scope |
|---|---|---|
| `Ctrl+Shift+R` | Start recording (or stop if already recording). | Global |
| `Ctrl+Shift+P` | Play the last macro on the smart-default target (foreground alt). | Global |
| `Ctrl+Shift+M` | Toggle compact mode (always-on-top strip). | Window-level |
| `Esc` | Abort current playback. | Global |

**Note:** v0.1 shipped with F8 (record) and F5 (play) — these have moved to `Ctrl+Shift+R` and `Ctrl+Shift+P` to avoid hijacking browser and IDE refresh keys. Per-macro PLAY buttons in the recorder UI show the updated labels.

## License

MIT © 626 Labs LLC. The reference contract bindings (`ROROROblox.PluginContract`) ship under the same license — see the parent RoRoRo repository.

---

**A 626 Labs product · *Imagine Something Else*.**
