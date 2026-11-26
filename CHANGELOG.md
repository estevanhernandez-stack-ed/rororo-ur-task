# Changelog

All notable changes to RoRoRo Ur Task are documented here. Format roughly follows [Keep a Changelog](https://keepachangelog.com/en/1.1.0/); versioning follows [SemVer](https://semver.org/).

## 0.13.1 — 2026-11-26

A fix release from a full read of 0.13.0. No new features. The stop keys stop more, keep-alive can
no longer go quiet without saying so, and one bad file can no longer take Ur Task down.

### Changed

These are the ones you will notice.

- **Recording pauses keep-alive.** Press record while keep-alive is on and it pauses. Stop
  recording and it comes back by itself. Ur Task used to record its own keep-alive Spaces into
  your macro, and with Turbo on it could swallow keys you pressed. If an account gets close to
  its idle limit while you record, the activity list names it and the time. Ctrl+Shift+P during a
  recording is refused with a line that says to stop recording first.
- **A per-window recording keeps only the keys you type into that game window.** It used to keep
  every key pressed anywhere on the PC until you stopped, including what you typed in other apps.
  To record across windows, tick "Record across all windows". A per-window recording also stops
  and saves if its account closes.
- **A click is refused when another window covers the spot.** If Ur Task's own compact window, a
  notification or another always-on-top tool sits over the place a macro clicks, the macro stops
  and names the account. It used to click the window on top. Colour checks stop the same way,
  where they used to read the covering window's pixels.
- **A macro that presses the Windows key, Sleep, or a browser or app-launch key is not imported
  and not played.** Windows acts on those keys even while Roblox is in front. The macro stays in
  your library so you can see it and delete it.
- **Import has limits:** 20 MB per file, 500 macros per file, and 200,000 events or 5,000 steps
  per macro. A file over a limit is refused with a line that says which.
- **A macro file with a missing, malformed or duplicate id is a load failure, not a macro.** It
  shows as one line in the activity list at startup. Import the file to give it a fresh id.
- **Rows say "stopped" when keep-alive is not running**, and the tray tooltip says whether
  keep-alive is on. Rows used to keep counting down to a Space that was not coming.
- **Esc is held for the whole of a macro that another plugin or an agent started**, including
  the pauses between its passes, so Esc always stops it.

### Fixed

**Stop keys and hotkeys**

- Esc and Ctrl+Shift+F12 stop a repeating macro started by another plugin or an agent. They used
  to stop one pass, and the next began half a second later.
- A repeating macro whose game window is gone stops and says so. It used to retry with no pause,
  pinning a CPU core and overwriting the log.
- Holding Ctrl+Shift+P, Ctrl+Shift+R or Ctrl+Shift+F11 a moment too long no longer flips it back
  off.
- Double-clicking PLAY ASSIGNMENTS no longer starts the loop and stops it again.
- If another program is using one of Ur Task's hotkeys, the others still work, your macros still
  load and Ur Task still connects to RoRoRo. The activity list names the hotkey. It used to start
  with no macros, no hotkeys and "Not connected".
- Quitting Ur Task in the middle of a macro lets go of held keys and mouse buttons.

**Keep-alive**

- An account that launches while keep-alive is starting is served. It could wedge the loop so
  that no account got a Space, while the window said running.
- One account that keeps failing no longer stops the others being served.
- Keep-alive looks again every 5 seconds, so an account you switch back on, or one that rejoins,
  is served on time. It used to sleep until the next account it already knew about was due.
- An account's idle limit follows its game once RoRoRo names it. Accounts launched after Ur Task
  connected used to stay on the unknown-game limit of 18 minutes. Pet Simulator 99 is 15.
- A macro that hits an error is reported as refused, gives up its turn, and raises the usual
  warning after three in a row. It used to be retried every 31 seconds as a "keep-alive" failure
  while the other accounts' macros never played.
- Break-glass released during a recording brings keep-alive back when you stop recording.

**Files**

- A macro name with non-English digits, such as １ or ١, no longer crashes Ur Task on every
  start.
- A failed save keeps the old macro. A save used to delete the old file first.
- Rename and "Allow in all games" change the macro as it is on disk now, so they no longer undo
  what another tool changed since Ur Task loaded it.
- Two macro files with the same id no longer crash a routine run.

**Playback**

- Macros with only keys, waits and pastes no longer need a recorded window size, are no longer
  refused at a different display scale, and no longer resize your window.
- A recording that includes mouse clicks keeps the wait between your last input and the stop.
  Keyboard-only recordings still end at the last key: carrying that wait needs a change to the
  macro file format, which comes in 0.14.0.

**RoRoRo connection**

- Declining "account launched" or "account exited" at install no longer makes Ur Task quit and
  blame RoRoRo. It asks RoRoRo for the account list every 30 seconds.
- If RoRoRo's account events end while Ur Task is running, Ur Task writes why to the log and
  exits. It used to run on with an account list that could no longer change.

**Window**

- The routine RUN button shows STOP while a routine runs.
- PLAY ASSIGNMENTS enables as soon as an account appears.
- "Export as AutoHotkey" is reachable again, as two menu items, and "Allow in all games" shows a
  tick.
- The note under a mouse recording no longer tells you to STACK your windows.

### For other plugins and agents

The action bridge. No request or response shape changed, and no reason code changed meaning.

- A client that connects and sends nothing is dropped after 2 seconds. It used to block every
  other caller, StopMacro included.
- StopMacro for a playback that is not running stops nothing. It used to stop whatever was
  running and answer `stopped: 0`.
- RunMacro refuses `interAltDelayMs` outside 0 to 60000 (`-1` used to hang the bridge) and a
  blank `macroId`. A malformed RunMacro sent while something is playing answers `refused`, where
  it used to answer `busy`.
- A repeat whose every target's game window is gone ends as `failed` with reason `refused`, and
  the detail names the accounts. Any other pass that completes nothing waits 1 second before the
  next, doubling to 30.
- Bridge errors reach `ur-task.log`. They used to go nowhere in a release build.

### Under the hood

- Built against plugin contract 0.14.0 (was 0.8.0). Nothing new is called, so Ur Task still runs
  on RoRoRo 1.19 and later.
- A release checks its tag against `manifest.json` and runs both test suites, the one against the
  real host included, before it builds anything.
- The test workflow also runs every day, so a host change is caught without a commit here.
- The test that failed about two runs in three is fixed at its cause: a test constructor that
  wrote into another test's log.
- README, FEATURES and the keystone no longer claim what the code does not do: the minimum
  RoRoRo version is 1.19, there is no consent opt-out, and a missing "exiting cleanly" line is
  normal when RoRoRo closes Ur Task.

## 0.13.0 — 2026-10-25

Keep-alive you can trust: it works from how long each account has really been idle, tells you
when it misses and why, and stops fighting your macros.

### Added

- **Keep-alive now schedules from each account's real idle time.** It used to count from the
  moment Ur Task started. Now every account has its own clock, and anything that counts as input
  resets it: Ur Task's own Space, a macro's key presses, and what RoRoRo sees you do in that
  window (RoRoRo's input clock is read too, from the moment an account launches). Starting
  keep-alive no longer jumps everyone at once: an account with recent input on either clock waits
  for its real deadline, and only accounts with no input history anywhere go first, a couple of
  seconds apart. The "next" time in the window comes from real idle, not from when you pressed
  play.
- **Misses say why, and recoveries say so too.** When a Space doesn't land, the log and the
  window name the cause: the game window is gone, Windows refused to give it focus (and which
  window was in front when it refused), another window was in front, the input was rejected, or
  keep-alive was held off. If every account due at once missed for the same reason you get one
  line, not a pile, with Turbo keep-alive on or off. When an account lands again you get a line
  for that. If an account has run past its deadline and the next retry can't land before the
  game's limit, one line says "expect a reset or kick around HH:mm" so you know what's coming.
- **A macro pauses for another account's Space, then picks up where it stopped.** If a macro is
  running on one account and another reaches its deadline, the macro pauses at the next step
  boundary, the other account gets its Space, and the macro continues from the same step. A step
  in progress always finishes first. A long hold with no max can't be paused safely, so it may be
  stopped, with advice on how to set a max.
- **Keep-alive off, per account.** A new "Keep-alive off" switch on each account row takes that
  account out of the loop. It is never focused until you turn it back on or restart Ur Task.
- **Accounts launched while keep-alive runs join it.** Launch an alt mid-run and it is picked up
  by the loop instead of waiting for a restart.
- **Game limits are built in.** Ur Task knows how long a game lets an account sit idle. Pet
  Simulator 99 was measured: its own anti-AFK resets an idle account after about 15 minutes, so
  keep-alive aims to act 6 minutes before the limit. Games it doesn't know get a safe default.
- **Agents can read all of it.** The action bridge has a new `GetStatus`, and Ur MCP 0.5.0 exposes
  it as `keepalive_status`: whether keep-alive is running, every account's idle time and deadline,
  which are off, and any paused playbacks. Agents can read the off switch but not flip it.
- **Unreadable macros say why.** Refusals now carry a stable reason code with who asked and what
  to try next. A macro file that can't be read is reported as `macro-unreadable` instead of
  looking missing (#39).
- **RoRoRo will ask for two new permissions.** Ur Task asks to read account activity and to mark
  an account active (so a Space it sent counts on RoRoRo's side too). If you decline either,
  keep-alive still works and uses its own clock; you just lose RoRoRo's view of what you did in
  that window.

### Fixed

- **A relaunched account no longer drops out of keep-alive or stops farming.** An alt that
  relaunched mid-run (a crash, a rejoin) used to fall out of the loop until you restarted it. Now
  the new window stays in the loop with the same role and macro: a keep-alive account keeps
  getting its Spaces, and an Active one keeps farming. The old window closing after the new one
  opened does not take it back out.
- **A keep-alive service that fails no longer stops the loop.** It is logged and retried in
  30 seconds, the same as a miss.

## 0.12.1 — 2026-10-25

### Added

- **Ctrl+Shift+F11 is break-glass: you have the wheel.** When you need the PC back mid-run, press
  it once. Every macro stops, keep-alive pauses, and Ur Task refuses macros from other plugins and
  agents (reason `breakglass`) until you press it again, so nothing fights you for the mouse. Ctrl+Shift+P and
  Ctrl+Shift+L are ignored while it's on. Press it again to hand back: keep-alive comes back on if
  it was playing when you took over. The state lives in a small flag file next to the logs, so it
  holds across a restart, and scripts can see it. The log says "Break-glass ON" and "Break-glass
  OFF", with what was stopped.
- **Turbo keep-alive.** An opt-in toggle next to the cadence presets. When any keep-alive window
  comes due, every keep-alive window due in the next two minutes is jumped in one quick sweep,
  so you are interrupted once instead of once per window. A small corner card counts down 3, 2, 1
  first and never takes focus; Ctrl+Shift+F12 stops the sweep. Each window gets its Space the
  moment it is confirmed in front (capped at 300ms), not after a fixed second, and your own
  window comes back once at the end. Off by default; with it off, keep-alive works exactly as
  before. While a sweep holds the screen (about a second), keys you type and clicks you make are
  dropped rather than landing in an account's window, so a W, A, S or D typed mid-sweep can't walk
  an account. Ctrl+Shift+F12, Esc and Ctrl+Shift+F11 still work during it.
- **A paste step.** `{ "kind": "paste", "delayMs": 200, "text": "Huge" }` puts the text on the
  clipboard, presses Ctrl+V in the game window, and puts back what you had copied. Leave `text`
  out and it pastes whatever is on the clipboard already, for an agent that sets it first. It
  replaces the four hand-timed Ctrl and V key steps search-box macros used. Text only goes back:
  if you had an image copied, the log says it was replaced. AutoHotkey export writes the same
  paste. Macros with a paste step don't load on Ur Task 0.12.0 or older.

### Fixed

- **The record hotkey no longer ends up in your recordings.** Ctrl+Shift+R to stop used to leave
  its Ctrl and Shift presses at the end of the macro, sometimes as a full Shift tap, which toggles
  shift-lock in Roblox at the end of every pass. Starting left the chord's releases at the front.
  Both are trimmed when the recording is saved. Ctrl and Shift you pressed yourself, before your
  last key or click, stay.

## 0.12.0 — 2026-10-25

### Added

- **Search the macro library.** A search box sits above the list and filters as you type. Every
  word has to match, in any order, against the name, the game it was recorded in or its
  `[account]` tag, so `trade ok` finds `self-trade ok`. It stacks with PLAYING NOW. Ctrl+F jumps
  to the box while Ur Task has focus; Esc in the box clears it.
- **Groups from the names you already write.** Names in the `WHERE | ROUTE n/N | what` shape group
  under their WHERE (`PLZ`, `HW Z4`, `ANY`), with a count. Route steps read in run order
  (`booth 1/3, 2/3, 3/3`), then the rest by name. Names without ` | ` go to Ungrouped at the
  bottom. Click a header to collapse it; Ur Task remembers which are collapsed. Nothing in the
  macro files changed.
- **Archive instead of delete.** Archive in a macro's menu moves its file into `macros\archive\`.
  It drops out of the library, the action bridge and the MCP's `list_macros`. Show archived lists
  them greyed out, each with Restore. Archiving a macro that is paired with an alt or used in a
  recipe asks first and names where.

- **Ur OCR can sweep the stone around you in one hold.** Clearing stone block by block paid a
  look before and after every block. `SweepPath`, a new bridge call, presses the left button on a
  block beside your character, moves the pointer one block at a time along a path Ur OCR sends
  (a real mouse move each time, waiting `dwellMs` on every block), and lets go only back on the
  block it started on, so the release can't click a player or a chest. While the button is down
  it checks the Auto Mine dot every 3 blocks, and at least every 1.5 s however long the dwell: if a menu or a profile covers it, the pointer goes
  back to the start block, lets go there, and the playback stops as `check-failed`. Losing focus
  lets go at once, as every hold does; Esc and `StopMacro` go back to the start block first. It
  rides the same single-flight rule, playback id, `GetPlayback`, `StopMacro` and Esc as
  `RunMacro`, is never saved or listed, and logs "SweepPath (N points)" and "swept N points in
  S s". The request carries `step`, the block size in px, as a new field. A path must start and end on
  the same block, move a whole number of `step` px at a time, stay inside the client, carry a guard and hold 3 to 256 points; anything else is refused with
  a sentence. Additive on bridge contract 1.0.
- **A sweep can follow the ore, not just the block grid.** `SweepPath` takes an optional
  `freePath: true`. It skips one rule, that every point sits a whole number of `step` px from the
  start, so Ur OCR can sweep the ore it found in one hold instead of a jump and two looks per
  spot (24 ore took 35 s point by point with boosters breaking each in 0.3 s). `step` may then be
  left out or 0. Every other rule stays: inside the client, start and end on the same point, 3 to
  256 points, no point repeating the one before, a guard, a `dwellMs` of 50 to 5000. The start
  line says "free path" where it would name the step. Left out or false, a path is refused off
  the lattice exactly as before. Additive on bridge contract 1.0.
- **A finished ClearAt says which spots were empty.** `GetPlayback` on a finished ClearAt now
  carries `noOutline`: the 1-based points that showed no outline, `[]` when every point had one.
  A point counts when it logged "no outline, skipped", and also when it logged "no outline on a
  fresh baseline, stopped": it pressed, but nothing broke there. Ur OCR remembers those spots and
  stops sending them pass after pass, where each one cost about 0.6 s. Every other reply (a
  saved macro, a failed or stopped ClearAt, a running one) leaves the field out, so older callers
  see no change. Additive on bridge contract 1.0.

### Changed

- **The library is ordered by group, not by recency.** The game band now only drives the PLAYING
  NOW filter.
- **Delete names where a macro is in use** (paired alts, recipes) before you confirm.
- An import now dedupes names against the whole library, not just the macros currently showing.
- **Camera top-down also sets the zoom.** After the pitch drag it holds I to the nearest zoom, then O for `camera.zoom.outMs` (1000 ms, measured 2026-09-30 at the top of Mine #8: two 900 ms holds matched frame for frame). A fresh client starts zoomed in close, which Ur OCR reads as a dark frame; Ur OCR now runs this macro at the start of its pulse and after every Go to Top. Only the example macro changes: regenerate from `measured.json` and copy `0e5a0000-0000-4000-8000-000000000003.json` into your macros folder.
- **Abort moved from `Ctrl+Shift+A` to `Ctrl+Shift+F12`.** Typing relayed through Mouse Without
  Borders or Chrome Remote Desktop can leave Ctrl and Shift held down, and the next "a" typed then
  arrives as Ctrl+Shift+A. On 2026-09-30 that stopped Ur OCR's pulse loop three times while
  nobody pressed the chord; the abort log caught each one as an injected key with Roblox in
  front. Typing never produces F12. Bare `Esc` still aborts while something plays.
- **An abort says what did it.** The log said only "Aborted." or "Abort ignored — nothing
  playing.", and on 2026-09-30 there were 8 aborts nobody could explain, one of them stopping Ur
  OCR's ore-stop pulse mid-sweep with nobody at the keyboard. The line now names the source: the
  key (`Esc`, registered only while something plays, or `Ctrl+Shift+F12`) with the window in front
  at that moment, its title, pid and process, as in
  `Aborted (Esc; foreground 'Roblox' pid 6808 RobloxPlayerBeta).`; a button in Ur Task's window
  (`Aborted (Stop button; foreground ...).`); or a bridge call, `Aborted (StopMacro from
  626labs.ur-ocr).`, which logged nothing before. Windows' hotkey message does not say whether a
  key press was injected by another program, so the line can't say that.

## 0.11.0 — not released on its own; ships in 0.12.0

### Added

- **Reach checks.** A hold or a point can carry a `reach`: with the pointer on the spot, it
  counts the near-white pixels (every channel at 225 or more) in a block-sized box around it and
  needs at least `minCount`. That is the white outline the game draws on a block your pickaxe can
  break. No outline within 300 ms: the step is skipped, not failed, and the log says
  "no outline, skipped" with the count. The box and the count scale with the window like points.
- **One block per spot.** Ur Task holds the button on each block, longer each time it doesn't
  break, and stops as soon as the block breaks. The first hold is 0.3 s, then 0.6, 1.2, 2.4, and
  3 s from then on. Between holds it lets go and looks (the game hides the outline while the
  button is down). No outline: the block broke. The outline moved or shrank: the block broke and
  the next one down is showing, and it is left alone. The log says "broke after N hold(s), X s
  held", with "(outline moved to the next block)" when it jumped. A `maxMs` bounds the pressed
  time across holds (the last hold is cut short); with none, ore is never abandoned for time.
- **Turn on Hide My Pets in Roblox Settings before running a pulse.** Pets sit over the character
  and the ring spots, and cover the outline.
- **Clear spot macros** for ore stop v1 (pulse) in `docs/reference/events/macros/space-mine-ore-stop/`:
  `Clear spot N` through `Clear spot NW`, each one hold behind a reach check and nothing else,
  because the Ur OCR loop owns Auto Mine in pulse mode.
- **A playback that pressed nothing says so.** When reach checks skipped and no button or key
  went down, `ur-task.log` reads "playback finished (skipped: no outline)", and a bridge playback
  reports `GetPlayback` state `finished` with reason `skipped`. A normal finish has no reason, as
  before.
- **Ur OCR can aim Ur Task at the blocks it finds.** Instead of only playing recorded spots, Ur
  Task now clears wherever Ur OCR points: ore first, then the stone around you. Under the hood
  this is `ClearAt`, a bridge call: one account, the client size it measured in, 1 to 64 points
  in order and one outline box, played as ONE playback. Each point is a reach hold (hover,
  outline check, then one block held until it breaks, measured over the box's no-hover
  baseline), skipped when no outline shows. The window is sized to the client it was
  measured in first, as for a macro. It rides the same single-flight rule, playback id,
  `GetPlayback`, `StopMacro` and Esc as `RunMacro`, is never saved or listed, and logs as
  "ClearAt (N points)" with each point by its label. Additive on bridge contract 1.0.
- **Clearing stops the moment a menu or a player's profile covers the game: Ur Task checks the
  Auto Mine dot before every press.** `ClearAt` takes an optional `guard`: a pixel box, the colour
  it should show and a tolerance. Before each point and each hold, a colour past the tolerance
  stops the playback as `check-failed`, and no more input is sent. A call without a guard plays
  as before.
- **Camera turn left:** a quarter turn with the arrow key, for the pulse to look again from a new
  angle.

### Changed

- **Reach holds look for the outline over a baseline.** Before the pointer moves onto the spot,
  it parks on the Roblox window's title bar (a move, never a click), waits for any outline to
  clear, and counts the box's near-white pixels. The outline counts only when the count rises at
  least `minCount` over that. The title bar hovers no block; a spot one box-width away could light
  the neighbouring block and swallow the outline. A window with no title bar takes the baseline
  beside the box instead, and the log says so. White quartz is bright enough to pass the raw count with no hover, so an
  out-of-reach quartz block read as outlined and was pressed with no time limit. The log reads
  "outline seen (N near-white px over a baseline of B, needs M)". Reach points are unchanged.
- **Clearing runs faster: Ur Task counts the background once per pass instead of before every
  spot.**
- **The outline box can be up to 240 px a side, up from 120.** The camera pops in to the first
  wall in its way, so a block in a narrow shaft measures around 170 px; the old 120 cap refused
  real shaft blocks. Reach checks and `ClearAt` both read the same `OutlineCheck.MaxSide`, so
  both are covered.
- **Mine spot macros check reach too.** A spot looking through a hole at ore out of reach no
  longer holds forever: no outline, no press, and Auto Mine goes back on.
- **Esc during the focus delay now reads `stopped`, not `finished`.** The sequence never played
  that alt, so `GetPlayback` reports state `stopped` and a repeat ends there instead of counting
  it as a clean pass.
- **A lost single-flight claim now reads `failed`/`refused`, not a clean finish.** If a second
  `RunMacro` loses the race for the in-flight `SequencePlayer` (nothing ran that pass),
  `GetPlayback` reports state `failed`, reason `refused`, detail "Another playback took the
  sequence.", and a repeat stops spinning on it instead of reading it as `finished`.
- **Pulse holds no longer end on colour change.** A hit darkens the block, which made a weaker
  account let go before the block broke. Holds with a reach check ignore colour drift; holds
  without one behave as before.
- The reach values in `measured.json` are provisional until the live outline measurement.

## 0.10.0 — not released on its own; ships in 0.12.0

### Added

- **Hold steps.** A `hold` step presses a mouse button at a point and keeps it down while a small
  box there shows the colour it had just before the press. It lets go when the colour moves past
  its tolerance, or at `maxMs` if one is set, and the log says how long it held. Losing focus,
  Esc and `StopMacro` all release the button. Built for the Space Mine ore stop, where one ore can
  take 20 hits.
- **First match can skip only on the other state.** With `onNoMatch: "skipIfOther"`, a first match
  skips when every spot shows its other colour (Auto Mine already off) and stops with a report
  when something else covers it, such as a popup or a captcha.
- **Ore-stop example macros** in `docs/reference/events/macros/space-mine-ore-stop/`: Auto Mine
  off and on (checked), Mine spot N through NW, Camera top-down and Go to Top, generated from one
  file of measured values.

### Fixed

- **Every playback's ending is in `ur-task.log`.** A stopped playback logs its stop sentence and a
  clean one logs a finish line. Bridge playbacks also log their id with the state `GetPlayback`
  returns. Before, the log showed a start and nothing after it.

## 0.9.1 — 2026-09-29

### Fixed

- **A macro from a newer Ur Task is refused instead of run.** It shows as a load failure that
  says to update Ur Task. Before, an older Ur Task loaded it as an empty macro, started playing
  it and never finished, so a keep-alive looked like it was running while it kept nobody awake.

## 0.9.0 — 2026-09-27

### Added

- **Mouse recordings play as point clicks.** A recording with clicks saves as named points; the
  path between clicks is dropped and the time the mouse sat still is kept. The original
  recording stays in the file.
- **Points can check a colour before pressing.** A point waits for its colour to show, looks a
  few pixels around if the button moved, and otherwise stops with a sentence that says what it
  expected and what it saw.
- **First match.** One step checks a list of spots in order and presses the first that matches:
  the best unlocked mine, or Auto Mine only when it is off.
- **Display scale travels with the macro.** A macro recorded at 100% and played at 125% sizes the
  window and its points for 125%, and says so in the log.
- **`GetPlayback` on the action bridge.** A caller can ask how a playback is going or how it
  ended, for 10 minutes after it ends. A failed check ends a repeating playback.

### Changed

- When Roblox will not shrink a window to the size a macro wants, the refusal names the display
  scale and Roblox's smallest window instead of suggesting a larger screen.

## 0.8.0 — 2026-08-22

### Added

- **The action bridge speaks three methods now.** `ListMacros` enumerates the macro library
  (id + name) so a caller can resolve "the farm macro" to an id; `repeat` on `RunMacro` loops a
  macro end-to-start until stopped; and `StopMacro` cancels a running playback by id — or all of
  them — and aborts the in-flight pass rather than waiting for the next loop boundary. Built for
  the RoRoRo MCP connector, so an AI operator can run and stop macros the way Ur OCR already
  triggers them. The wire contract stays 1.0: every addition is additive, and a 0.7-era caller's
  frames parse exactly as before.

## 0.7.0 — 2026-08-11

### Changed

- **Keep-alives run on a schedule instead of a spin loop.** The old round-robin woke every alt in
  turn on a fixed tick whether or not anything was due. A deadline scheduler now decides what
  actually needs to fire and when, sleeps when nothing does, and fits keep-alives into the gaps
  between real work. The visible effect: a keep-alive alt leaves your desktop usable, and an alt
  set to Active steals focus about **once per 30 seconds rather than once per second**.
- **Keep-alive intervals are game-aware**, with a per-game override when a game needs something
  different from the default.

### Added
- **Ur Task takes its colours from RoRoRo directly.** The plugin used to keep its own copy of
  the three built-in palettes, so a RoRoRo release that changed a built-in would leave Ur Task
  rendering last year's colours until somebody noticed and updated it by hand. It now reads the
  host's palette over the plugin contract and the copy is gone. Needs RoRoRo v1.19 or later for
  the live feed; against older hosts it falls back to the brand look, as before.
- **The theme log says what arrived and what got painted**, so a colour that looks wrong can be
  traced to the feed or to the plugin instead of guessed at.
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
- **The manifest version is tied to the assembly version**, with a test. Two files declaring the
  same number is exactly how they drift.
- Plugin contract moved 0.4.0 to 0.8.0 — that is what carries the theme feed.

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
