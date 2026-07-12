# RoRoRo Ur Task — Feature List

Portable macro recording and multi-account automation for RoRoRo-managed Roblox alts. Record once, play on any alt. A RoRoRo plugin — runs as its own tray app, connects to RoRoRo over a named pipe, and follows your alts as they launch and exit.

---

## The headliners

**Per-account macro assignment.** Assign a different macro to each running alt in a two-pane dashboard — click a macro, click an alt, done. One-to-many: the same macro can be pinned to many alts at once. `Ctrl+Shift+P` then cycles every alt in a round-robin — focus, play its macro, move to the next — until you stop. Any alt you leave unassigned automatically gets a keep-alive instead.

**Window-size-aware macros.** A recorded macro captures the window's **client size** and stores every mouse position **relative to the window's client area** — not absolute screen pixels. On playback, the plugin **resizes the target window to the recorded size before the macro runs**, so every click lands exactly right no matter where the window sits or which monitor it's on. No stacking windows, no lining anything up. If a window can't reach the recorded size (monitor too small, or below the window's minimum), playback refuses cleanly and skips to the next alt instead of misclicking.

**Import / export — shareable macro bundles.** Export your whole macro library to a single bundle file and hand it to a friend or clanmate; they import it and have your macros instantly. Bundles are cross-version tolerant — friends on older Ur Task versions can still open them (readers ignore fields they don't understand). Individual macros export too.

---

## Recording

- **Keyboard-only by default** (recommended) — keys route to whichever window has focus, exactly right for jumps, walks, and key-combo grinding. Mouse events are dropped unless you opt in.
- **Keyboard + mouse capture** — untick "keyboard only" for drag flows and click-precision sequences (these become the window-relative macros above).
- **Multi-window recording mode** (experimental) — captures across multiple windows for flows that switch alts themselves; replays raw events without foreground gating.
- **Hotkey-driven** — `Ctrl+Shift+R` starts/stops recording without touching the mouse.
- Faithful timing — playback preserves the original inter-event timing.

## Portable, per-account, or multi-account playback

- **Fully portable macros** — no account binding. Record on one alt, play on any alt (or all of them).
- **Multi-select sequential playback** — pick one or more alts at play time (select-all / select-none, click-order preserved) and the macro runs on each in sequence.
- **Per-account assignments + round-robin** — a macro per alt, looped across all running alts.
- **Skip-on-failure** — if one alt's window closes mid-sequence, playback continues on the rest; an end-of-run summary logs to the activity view.

## Recipes & loadouts (v0.6)

- **Recipes (position → loop)** — build an ordered routine: run position steps **once per alt** (walk everyone to the start), then automatically hand off to a **looped** macro or a keep-alive. No more babysitting the "everyone's in position, now start the loop" moment. Proceed-with-successes: one stuck alt never blocks the squad.
- **Loadouts (run once)** — the same builder with a **"Run once"** ending: replay a setup across your alts a single time, then stop. Record a weekly loadout / event setup once, apply it to the whole squad.
- **Run from the assignment grid** — pick a saved routine, check exactly which alts it runs on (per-alt checkboxes + select-all / select-none), and fire it with a button or `Ctrl+Shift+L`. Nothing auto-grabs, nothing gets wiped.
- **Recipe/loadout library** — saved routines are listed with **LOADOUT / RECIPE** type badges; run, edit, or delete each.

## Keep-alive (AFK dodge)

- Unassigned alts get a periodic **Space** jump so they dodge Roblox's idle kick while you work the others.
- Keep-alive is also a first-class routine ending (a recipe can end by holding the squad on keep-alive).
- Focus is **foreground-lock-aware** — the keep-alive/playback still grabs the window and lands input even when you've been idle (the exact moment it matters), with a verify-before-keystroke safety check so a failed focus is a skipped action, never a stray keystroke.

## Game-aware macro library

- Macros are **stamped with the game** they were recorded in, shown as a game badge on the card.
- **PLAYING NOW filter** — hide macros for games none of your alts are currently in (all-games and unstamped macros always stay visible).
- **Allow in all games** override per macro, for macros that aren't game-specific.
- **Soft mismatch warning** — a `≠ GAME` badge when a game-scoped macro is paired with an alt in a different game. Advisory only; playback is never blocked.

## Window arranging

- **STACK** — move every alt window to the same position and size (what legacy absolute-coordinate macros need; also snapshots positions to restore later).
- **GRID** — tile all alt windows across the monitor so you can watch the round-robin visit each one.
- **RESET** — restore windows to their pre-STACK/GRID positions.
- **CLEAR** — clear all macro-to-alt assignments.

## Live status & the dashboard

- **Two-pane dashboard** — macro library on the left, per-alt assignments + a live activity log on the right.
- **At-a-glance run state** — a status pill (recording / playing / looping), a progress bar for sequences, and a **cyan highlight on the alt row** currently being played, so you don't have to window-hop to see what's happening.
- **Compact mode** (`Ctrl+Shift+M`) — collapse to a slim, always-on-top status strip so you can watch the alts while a sequence plays.
- **Theme-following** — the whole UI tracks your RoRoRo host theme live.

## Hotkeys

| Key | Action |
|---|---|
| `Ctrl+Shift+R` | Start / stop recording |
| `Ctrl+Shift+P` | Play assignments (round-robin loop) — press again to stop |
| `Ctrl+Shift+L` | Run the selected routine on the checked alts |
| `Ctrl+Shift+A` | Abort current playback |
| `Esc` | Abort — but only while a macro is playing, so `Esc` stays yours the rest of the time |
| `Ctrl+Shift+M` | Toggle compact mode |

## Integration & extensibility

- **Export to AutoHotkey** — any macro exports to a standalone `.ahk` script, in **AutoHotkey v1 or v2** (you pick). Keyboard macros port faithfully (key down/up with your original timing); mouse macros port best-effort under window-client coords. The script runs on its own — no Ur Task, no account binding — with an honest header noting it plays on the active window only and can't do the per-account round-robin. Get your work out of the walled garden.
- **Action bridge** — Ur Task exposes a local, current-user-only IPC endpoint so sibling plugins (like **RoRoRo Ur OCR**) can fire a specific macro when a screen trigger matches. That's the perception→action loop — OCR sees a condition, Ur Task plays the response — no AutoHotkey needed. Consent-gated (default on).
- **RoRoRo-native** — subscribes to account-launched / account-exited events; the alt list stays live as you launch and close accounts.
- **Per-account row badges** — recording / playing indicators surface on RoRoRo's account rows (on hosts that render them).

## Reliability & safety

- **Self-terminates when RoRoRo closes** — no zombie process left holding input or hotkeys.
- **Single-flight run guards** — a running loop can't be double-started or collide with a routine run.
- **Crash diagnostics** — a rolling diagnostic log + a startup watchdog; grab it from the tray (**Open log folder**) for support without a screen-share. Logs never contain cookies or passwords.
- **Honest, opt-out capabilities** — the plugin discloses exactly what it does (synthesize keyboard input, synthesize mouse input, watch global input) on RoRoRo's install-time consent sheet; you can decline any of them.

---

*A 626 Labs product · Imagine Something Else.*
