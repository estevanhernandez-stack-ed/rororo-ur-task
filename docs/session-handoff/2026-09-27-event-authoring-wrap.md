# Event-authoring sprint, stopping point 2026-09-27

> Paste this whole file as the opening prompt for the next session, on whichever PC you are on.
> Claude Code's memory notes live per machine, so on a different PC this file is the memory.

## TL;DR

Between 2026-09-21 and 2026-09-27 we converted a clan member's AutoHotkey disconnect macro to run
on any screen, learned that Windows display scale is the thing our own products ignore, built a
grid-capture tool so an agent can read a recording off a screenshot, started the agent macro
playbook, and wrote the brief for the current PS99 event. The north star is a Claude session that
builds the week's Ur OCR trigger and Ur Task macro from "here's the event", ready by Saturday
11:45. Ur MCP already exists and is the control plane for that.

## Read these first, in this order

1. `docs/guides/agent-macro-playbook.md`: the rules. Este's own rules at the top, session lessons
   below. Append new tips there, in his voice: bold rule, why, how to apply, source.
2. `docs/reference/events/2026-09-space-mine-brief.md`: the current event. Week 1 clan battle
   counted blocks; week 2 Mining League scores ore rarity. The loop differs per week.
3. `docs/display-scale-findings.md`: measurements and ranked proposals for Ur Task and Ur OCR.
   Roblox lays out UI in logical units scaled by Windows display scale, and neither product
   accounts for it. Roblox's minimum window is 816x638 times the scale (800x599 client at 100%).
4. `docs/guides/ahk-any-screen-clicks.md`: the write-up for AutoHotkey authors. The author it was
   written for (Syn, K0ii) did not want it; it stands as the spec for our own version.
5. `docs/BACKLOG.md`, top two entries: "Expose and drag a macro's click points" (Este wants this
   in the products) and the display-scale gap.

## What exists now

- **`tools/grid-capture.ps1`.** Captures every visible Roblox game area pixel for pixel, draws a
  labelled coordinate grid, and with `-Macro "<name>"` overlays a recorded Ur Task macro's
  clicks, numbered, plus a legend of clicks and keys. `-Image <png>` works on a screenshot you
  already have. Read the output PNG with the Read tool. Verified on the "Dig and dug" macro:
  click 8 = Go to Top, the twelve-click pile at the left edge = automine toggle, click 5 =
  hotbar slot 2 clicked.
- **The converted AutoHotkey script**, `AutominingDisconnectAHK_AnyScreen.ahk`, lives in Este's
  Downloads folder on the main PC, not in this repo (`*.ahk` is gitignored as third-party).
  Copy it by hand to another PC. It has: game-area sizing times display scale, a Click points
  window with draggable named tags saved per display scale, F5 point inspector, friend rejoin,
  a 3 s hotbar delay after the automine toggle, and two RoRoRo rejoin modes that call Ur MCP's
  exe over native pipes. 82/82 automated checks. **A real launch through the RoRoRo modes is
  untested.**
- **Ur MCP** (`rororo-ur-mcp` repo, plugin id `626labs.ur-mcp`, 13 tools). Registered on the
  main PC at user scope in the default Claude config. On another PC, register it from
  PowerShell with the real path, in one line:
  `claude mcp add -s user rororo -- "$env:LOCALAPPDATA\ROROROblox\plugins\626labs.ur-mcp\626labs.ur-mcp.exe"`
  Tools appear in a NEW session. Este's PowerShell sets `CLAUDE_CONFIG_DIR` to a personal
  config; the VS Code extension uses the default one, so register where the session runs.
- **Pushing a macro into running Ur Task works today:** the bridge re-reads
  `%LOCALAPPDATA%\626Labs\RoRoRoUrTask\macros\*.json` on every call. Ur OCR does NOT reload
  `triggers.json` while running; edit it with Ur OCR closed.
- **The handout for AutoHotkey authors** is also a private claude.ai artifact (Este owns it; find
  it in the artifact gallery under "Any-Screen AHK Macros").

## What to test next, on Dunder-MiffLan (3840x2160, switch it to 125% first)

1. **RoRoRo as the relauncher.** RoRoRo running with Ur MCP installed there. In the AHK script,
   type the account's RoRoRo name, pick "RoRoRo: relaunch account", press F10. First real launch
   through the bridge.
2. **Syn's theory** that the game UI only sizes itself at launch. Press F10 for a full relaunch,
   then open Click points and Drag on screen. If the grey squares (originals) now sit on the
   buttons and the moved tags are off, he is right, and the fix is to always size before the HUD
   loads. If the tags are still right, he is wrong. Matters for Ur Task too, which resizes an
   already-loaded window right before playback.
3. **Cracks on a darker block surface.** Este was checking whether the spider-web cracks still
   show; they are the progress signal for the stop-for-gem give-up.
4. **Any Ur Task mouse macro recorded at 100%, played at 125%.** Confirms or downgrades the
   display-scale gap inside Ur Task itself. Ten minutes.

## The mining flow we are building toward

Detect an ore, stop Auto Mine, hit the ore until it disappears or its cracks stop growing, resume.
Ur OCR needs, smallest first: a falling-edge trigger (fires when the match disappears), a
StopMacro action (the Ur Task bridge already has StopMacro), a "region still for N seconds" mode
(frame differencing; the give-up), and a "colour blob at least this big" mode (cyan gems share
their colour with the wall's stars, but a gem is one big patch and the stars are dots). Shape
matching proper can wait: OpenCvSharp4 (Apache 2.0) for flat UI if ever needed. Camera must be
pinned first person, zoomed in, looking down, so the block under the character is a fixed region.

## Gotchas learned, so nobody pays twice

- Roblox refuses to shrink below its minimum window; `SetWindowPos` reports success anyway. A
  stand-in test window must enforce that minimum (`+MinSize800x599` in AutoHotkey) or the test
  lies.
- Run any test harness hidden on the main PC: live sessions and Ur Score recordings run there.
  Install `OnError` before the `#Include`, and never trust a result file that may be stale.
- Feeding Ur MCP's exe a file on stdin and closing it returns nothing; the pipe must stay open
  until the reply. Native pipes with `CREATE_NO_WINDOW` keep it invisible.
- AutoHotkey variable names are case-insensitive, and globals must be assigned before the GUI code
  that reads them; `/validate` checks syntax only.
- `roblox://experiences/start?userId=<id>` follows a friend with no auth ticket. Whether a friend
  join dodges a captcha-locked account is Este's observation, unproven.
- The Enchant Machine wipes the pickaxe on every roll. Never automate it.

## Open questions

- Which PS99 button moved at 125%, and is it the same on every 125% PC? Este's `[Points@125]`
  section of `AutominingDisconnect.ini` on Dunder-MiffLan holds the numbers; two left-column points
  moved about 8 left and 16 up, the first 5th-mine click 40 up.
- Do the cracks show on darker rock, and does the star texture animate while standing still?
- Points per ore and block health per layer for the league week. Only play tells.
