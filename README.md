# RoRoRo Ur Task

> Portable macro recording for [RoRoRo](https://github.com/estevanhernandez-stack-ed/ROROROblox)-managed Roblox alts. Record once, play on any alt — with window-relative mouse macros (clicks land right at any window position, on any monitor), round-robin assignments + AFK keep-alive, multi-select sequential playback, one-click STACK/GRID window arranging, shareable macro bundles (export/import — friends on older versions included), a two-pane dashboard that follows your RoRoRo theme live, multi-window mode, compact view for long sequences, and an action bridge so sibling plugins (like [RoRoRo Ur OCR](https://github.com/estevanhernandez-stack-ed/Ur-OCR)) can fire macros on screen triggers.

Macros are now **fully portable** — no account binding. Pick one or more alts at play time and the macro runs on each in sequence. Multi-window recording mode (experimental) captures across multiple windows for flows that switch alts themselves. Compact mode collapses to a slim always-on-top strip so you can watch the alts while the sequence plays.

## How it works

RoRoRo Ur Task is a [RoRoRo plugin](https://github.com/estevanhernandez-stack-ed/ROROROblox/blob/main/docs/plugins/AUTHOR_GUIDE.md). It runs as a separate Windows EXE, connects to RoRoRo over a named pipe (`\\.\pipe\rororo-plugin-host`), and subscribes to RoRoRo's `account-launched` + `account-exited` events.

**Recording:** captures keyboard + mouse events (globally, even across windows if multi-window mode is enabled). Stores the current foreground alt's user-id as metadata (for reference only — not enforced at playback).

**Playback:** opens a target picker; you pick one or more alts and the macro runs on each in sequence. Multi-window mode replays raw events without foreground gating; single-window (default) gates every event to the active foreground window. Skip-on-failure: if one alt's window closes mid-sequence, playback continues on the rest. End-of-sequence summary logs to the activity view.

**Assignments (round-robin):** assign a macro to each running alt — or leave an alt unassigned and it gets a periodic Space jump (keep-alive) so it dodges AFK kicks. `Ctrl+Shift+P` (or the PLAY ASSIGNMENTS button) cycles through every running alt in a loop — focus, play its macro, move to the next — until you stop it.

**Action bridge (v0.3+):** Ur Task listens on a local named pipe (`\\.\pipe\626labs-ur-task`, current-user only) for `RunMacro` requests from sibling plugins. This is what lets [RoRoRo Ur OCR](https://github.com/estevanhernandez-stack-ed/Ur-OCR) fire a specific macro when a color/text screen trigger matches — the perception→action loop, no AutoHotkey needed. The bridge is on by default and there is no switch for it in the window. To turn it off: quit Ur Task (it rewrites its settings file while it runs), set `"AcceptPluginRunRequests": false` in `%LOCALAPPDATA%\626Labs\RoRoRoUrTask\ui-prefs.json`, and start Ur Task again.

## Capabilities declared

| Capability | Why |
|---|---|
| `system.synthesize-keyboard-input` | Playback synthesizes keys via `SendInput`. |
| `system.synthesize-mouse-input` | Playback synthesizes mouse moves + clicks. |
| `system.watch-global-input` | Recording captures keyboard + mouse globally. |
| `host.events.account-launched` | Builds the pid → user-id map. |
| `host.events.account-exited` | Auto-stop when a target alt's window closes. |
| `host.queries.account-activity` | Keep-alive reads RoRoRo's activity clock for each account. If you decline it, keep-alive falls back to its own clock. |
| `host.commands.mark-account-active` | Keep-alive tells RoRoRo when it has just served an account. If you decline it, keep-alive carries on with its own clock. |
| `host.ui.row-badge` | Per-account "recording" / "playing" indicator (renders in v1.4.1+ when RoRoRo's host-side UI lands). |
| `host.ui.tray-menu` | Informational status entry (same — v1.4.1+ rendering). |

The three `system.*` capabilities are disclosure. They tell you what the plugin does on your PC: it sends keys, it sends mouse input, and it watches your keyboard and mouse. RoRoRo shows them on the consent sheet at install time, but it cannot enforce them, and Ur Task does not check which ones you ticked. The plugin needs all three and behaves the same either way, so there is no opting out of one of them. If you do not want a plugin that does these things, cancel the install.

## Install

You need RoRoRo installed first ([v1.19 or later](https://github.com/estevanhernandez-stack-ed/ROROROblox/releases)). If your RoRoRo is older than that but at least 1.4.3, it refuses the install and tells you to update RoRoRo. **Hosts older than 1.4.3 (1.4.0–1.4.2) do not check: they silently accept the install and then ask you to restart RoRoRo, and that restart is known to fail.** If installing ends with a "Restart RoRoRo" prompt, your RoRoRo is outdated: update it from the Microsoft Store, relaunch, and reinstall the plugin.

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
| `Ctrl+Shift+L` | Run the selected routine on the checked alts. Press again to stop it. | Global |
| `Ctrl+Shift+F12` | Abort current playback. | Global |
| `Ctrl+Shift+F11` | Break-glass: you take the wheel. Stops every playback and keep-alive, and holds them off, including after a restart, until you press it again. | Global |
| `Ctrl+Shift+M` | Toggle compact mode (always-on-top strip). | Window-level |
| `Esc` | Stop whatever is playing. Ur Task holds Esc only while something is playing, and that includes the whole time the assignment loop is on. | Global (while something is playing) |

**Esc and the assignment loop.** For a single macro or a routine, Esc is taken for as long as it plays and handed back when it ends. The assignment loop (`Ctrl+Shift+P`, keep-alive included) counts as playing for as long as it is on, even between keep-alive taps. So while the loop is on, pressing Esc in any app stops the loop instead of doing what Esc normally does there. That is on purpose: Esc is the easy stop key. Once the loop stops, Esc is yours again.

**Break-glass.** Holding them off means this: while it is on, `Ctrl+Shift+P` and `Ctrl+Shift+L` do nothing and run requests from other plugins are refused. Press `Ctrl+Shift+F11` again to hand control back. Keep-alive starts again by itself if it was running when you pressed it and Ur Task has not restarted since.

**Note:** v0.1 shipped with F8 (record) and F5 (play) — these have moved to `Ctrl+Shift+R` and `Ctrl+Shift+P` to avoid hijacking browser and IDE refresh keys. As of **v0.3.1**, bare `Esc` is claimed only while something is playing instead of for the whole time Ur Task runs. As of **v0.12.0**, the abort chord is `Ctrl+Shift+F12` instead of `Ctrl+Shift+A`: if you type through Mouse Without Borders or Chrome Remote Desktop, Ctrl and Shift can get stuck down on the way, and your next "a" would stop the macro. Per-macro PLAY buttons in the recorder UI show the updated labels.

## Troubleshooting

- **The install told me to "Restart RoRoRo" (and restarting did nothing).** Your RoRoRo host is older than v1.4.3, and Ur Task needs v1.19 or later. Update RoRoRo from the Microsoft Store, relaunch it, and reinstall the plugin — on current hosts the plugin starts immediately after install, with no restart step.
- **Something crashed, hung, or silently stopped?** The plugin keeps a diagnostic log: right-click the Ur Task tray icon → **Open log folder** → drop `ur-task.log` into the Discord support thread. The log lives at `%LOCALAPPDATA%\626Labs\RoRoRoUrTask\logs` (2 files × 1 MB max).

## License

Apache License 2.0, © 2026 626Labs LLC. See [LICENSE](LICENSE) and [NOTICE](NOTICE). Releases published before 2026-09-24 were released under the MIT License. The contract bindings (`ROROROblox.PluginContract`) come from the parent RoRoRo repository under its own license (MIT).

---

**A 626 Labs product · *Imagine Something Else*.**
