# RoRoRo Ur Task

> Per-window-aware macro recording for [RoRoRo](https://github.com/estevanhernandez-stack-ed/ROROROblox)-managed Roblox alts. Record once on one account; playback refuses to fire unless the foreground window matches.

The killer beat is the user-id binding: when you start recording, RoRoRo Ur Task captures the user id of the foreground Roblox window. Playback won't fire keys or clicks into any other window — alt-tab away mid-playback and the macro stops cold. Auto-stop also triggers when the bound alt's window closes.

## How it works

RoRoRo Ur Task is a [RoRoRo plugin](https://github.com/estevanhernandez-stack-ed/ROROROblox/blob/main/docs/plugins/AUTHOR_GUIDE.md). It runs as a separate Windows EXE, connects to RoRoRo over a named pipe (`\\.\pipe\rororo-plugin-host`), and subscribes to RoRoRo's `account-launched` + `account-exited` events to maintain a live (pid → user-id) map. When you record, the foreground window's pid resolves through that map to a user id; the macro stores that binding. When you play, the same lookup runs every event — mismatch aborts.

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

You need RoRoRo installed first ([v1.4 or later](https://github.com/estevanhernandez-stack-ed/ROROROblox/releases)).

1. Open RoRoRo → Plugins → Install.
2. Paste this URL: `https://github.com/estevanhernandez-stack-ed/rororo-ur-task/releases/download/v0.1.0/`
3. Walk the consent sheet. The four `system.*` capabilities are required for the plugin to function.
4. Click Install.

RoRoRo Ur Task starts in your system tray (its own icon, separate from RoRoRo's tray). Click the tray icon to surface the recorder window.

## Hotkeys

| Key | Action |
|---|---|
| `F8` | Start recording (or stop if already recording). |
| `F5` | Play the selected macro. |
| `Esc` | Abort current playback. |

Hotkey collision: F8 + F5 are registered globally while the plugin runs. Configurable bindings land in v0.2.

## License

MIT © 626 Labs LLC. The reference contract bindings (`ROROROblox.PluginContract`) ship under the same license — see the parent RoRoRo repository.

---

**A 626 Labs product · *Imagine Something Else*.**
