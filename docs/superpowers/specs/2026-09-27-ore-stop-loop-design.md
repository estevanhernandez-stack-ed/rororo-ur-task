# Ore stop: Auto Mine that stops for every ore

**Status:** spec approved by Este, 2026-09-27
**Products:** Ur OCR (watches and decides) and Ur Task (acts). No change to the RoRoRo host.
**Builds on:** Ur Task 0.9.0 point steps and colour checks (`2026-09-27-point-clicks-and-checks-design.md`)
**Event:** PS99 Space Mine league week (`docs/reference/events/2026-09-space-mine-brief.md`)

## What it does

The main account rides Auto Mine down its best mine. Whenever a block next to the character is
not plain rock, the loop stops Auto Mine, holds the mouse on that block until it breaks, and turns
Auto Mine back on. Every ore is worth stopping for; the cheap ones break fast, so the watcher does
not tell ores apart. If Auto Mine spends too long on one layer, the loop sends the account back to
the top so it restarts on easier rock.

This is piece 1 of 2. Piece 2, the bottom-layer routine (walk across the easy blocks, then place 3x3
bombs where each clears the most), is shelved. Big Games patched the bottom-layer bombing out in
league week (Este, 2026-09-27). Try it again when the mining event comes back; it would reuse the
layer reading and the ring below.

## Decisions

| # | Question | Decision |
| --- | --- | --- |
| 1 | Which ores to stop for | Every ore. The watcher asks "rock or not", never "which ore". |
| 2 | How ore is recognised | Know the rock, stop for anything else: each layer's plain rock is a small colour set; a ring spot matching none of them (and none of the ignore colours) is ore. |
| 3 | How the layer is known | Whichever layer's rock set matches the ring. Rock colour changes by layer (Este). |
| 4 | How ore is mined | Hold the left mouse button on it (Este: hold works once Auto Mine is off; some ore takes 20+ hits). |
| 5 | Give-up on ore | None on time. The hold continues while the spot still shows its starting ore colour; it ends when the spot turns to rock or empty, or to something that is neither (a misread). Ore is never abandoned for taking long. |
| 6 | Give-up on rock | A per-loop setting, default 5 minutes on one layer, then Go to Top. Under-powered accounts get pushed back to easier rock. |
| 7 | Telling Este an account is under-powered | No notifications. A line in Ur Task's log and in `GetPlayback` ("Went to top: 5 minutes on the bottom layer") that an agent session can read. |
| 8 | Screen geometry | The loop first drives the camera top-down (to the pitch limit, then count back). With the camera fixed, the character and the ring sit at steady game-area spots. |
| 9 | Split | Ur OCR watches the ring and fires; Ur Task runs fixed macros. RunMacro by name, so the bridge contract does not change. |

## The ring

Eight spots around the character in the top-down view: N, NE, E, SE, S, SW, W, NW, one block out.
The exact game-area coordinates come from the capture sweep (below), recorded at 100% and scaled
like any client-space region. Each spot is a 5x5 sample box.

Plain rock for Mine #8, seen 2026-09-27 (colours to be measured in the sweep, not guessed):

- navy blocks with lighter violet cell lines (top layers);
- black blocks with bright violet cell lines;
- grey rock (lower layers).

Ores seen: Sunstone (orange-gold, the easiest tell), Dark Quartz, Eclipse Onyx (black glass ringed in
violet, which is close to the black-and-violet rock and is the case to test hardest). A mined ore
shows a "New Item!" card with its icon; the loop does not depend on it but it is a useful check
when tuning.

## Ur OCR changes

1. **"None of" colour match.** A colour trigger can list several colours and match when the sample
   is within tolerance of *none* of them: `ColorCriteria.NoneOf: Rgb[]`, alongside the existing
   `TargetRgb`. The rock set for the current layer, plus sky/empty and the character's own colours,
   go in the list. (Today a colour trigger only matches when the sample is near one target.)
2. **Layer sets.** The ring triggers carry one `NoneOf` list per layer and use the list of the layer
   the ring currently matches best. A layer is "current" when most ring spots sit within tolerance
   of that layer's rock colours.
3. **Dwell condition.** A trigger can require its match to hold continuously for N ms before it
   fires (`HoldForMs`). The rock cap is a trigger "current layer unchanged for 5 minutes" that runs
   the Go to Top macro.
4. **Re-arm when Ur Task is busy.** Triggers are edge-fired with a cooldown. If RunMacro comes back
   refused because a sequence is already running, the trigger must stay armed and fire again after
   the cooldown while its spot still matches, instead of treating the edge as spent.
5. **Priority.** When several ring spots match at once, fire the first in ring order; the rest fire
   on their next evaluation after the macro ends.

Ur OCR only evaluates the foreground alt (its account-aware rule). The loop runs on the main,
which is in the foreground while Auto Mine rides; running it on alts in a round robin is out of
scope here.

## Ur Task changes

1. **A hold step.** `kind: "hold"`: press a mouse button at a point and keep it held until the
   check box's colour moves away from its starting sample by more than the tolerance, then
   release. It uses the same foreground guard as every other step (focus lost: release and abort),
   releases on Esc and StopMacro, and reports how long it held. No time limit by default; an
   optional `maxMs` exists for other uses.
2. **The macros**, all agent-authored v4, recorded at 100%:
   - `Auto Mine off (checked)`: press the pickaxe only if the dot is green.
   - `Auto Mine on (checked)`: press only if the dot is red (exists today as "LP Auto Mine on").
   - `Mine spot N` for each of the 8 ring spots: Auto Mine off, then hold on spot N, then Auto Mine on.
   - `Camera top-down`: a right-button drag down past the pitch limit, then the count-back.
   - `Go to Top`: press Go to Top at about (400, 50), then Auto Mine on.
3. **Log the ending.** A playback's stop sentence and a normal finish both go into `ur-task.log`
   (today only `GetPlayback` has them). This is already on the backlog from the 0.9.0 live pass.

## Failure and edge cases

- **Misread spot** (a pet or effect on the spot): the hold ends as soon as the colour stops being
  the starting ore colour, then Auto Mine goes back on. Worst case is a short pause, never a stall.
- **Ore that never breaks:** the hold continues. Este can stop it with Esc or StopMacro; an agent
  sees it running in `GetPlayback`.
- **Auto Mine already off when a mine macro starts:** the checked "off" press skips because the dot
  is red, and the macro continues.
- **Camera knocked out of top-down** (Este touches the game): ring spots stop matching rock or ore
  sensibly. The loop re-runs `Camera top-down` whenever no layer matches for 10 s.
- **Disconnects:** out of scope. Recovery stays with Ur MCP as today.
- **Never:** click captchas, the Enchant Machine, or friend-invite popups.

## Testing

- **Ur OCR:** unit tests for `NoneOf` (match when far from every listed colour, no match near any),
  layer selection, `HoldForMs`, and re-arm on a busy refusal.
- **Ur Task:** unit tests for the hold step against the fake IO: releases when the colour changes,
  holds while it does not, releases on focus loss and cancel, and keeps its button in the held set
  so a closed window still releases it.
- **Capture sweep, live on Dunder-MiffLan:** ride Auto Mine down Mine #8 on the main with the camera
  top-down, capture the ring every few seconds, and write each layer's rock colours and the ring
  coordinates into the triggers. Then a live run: the loop stops for ore on each layer, mines it,
  resumes, and pushes to the top on the rock cap.

## Out of scope

- The bottom-layer bombing routine (piece 2).
- Choosing between ores or valuing them.
- Running the loop on several accounts in a round robin.
- Notifications of any kind.
