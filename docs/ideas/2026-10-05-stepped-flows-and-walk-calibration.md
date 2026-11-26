# Stepped flows and per-account walk calibration (idea, 2026-10-05)

Two ideas from Este, raised during the first agent-driven PS99 self-trade (main -> PapasbbBri, 14:23 CDT). Not specced.
Captured so the measurements and failures from that run are in one place when a spec starts.

## 1. Stepped flows: wait for the button, then click it

**Problem.** A trade took about 25 agent turns, and nearly every recovery was the same failure: a click landed on a
screen that was not the expected one. Observed in one run:

- After any game "Ok" popup the bottom bar stays open, so the menu button opens Inventory instead of the trade list.
- A pending trade request does not appear as Accept! in a list that is already open; the list must be reopened.
- Row order in the trade list changes between opens. Unlocking a pet reorders search results (cell 7 became cell 5).
- Nonstop Auto Hatch hides the HUD, so the menu button is not on screen at all.

Each step was "play a fixed macro, capture, read the capture, decide". The agent was the state machine.

**Idea.** A flow is a list of steps, and each step is "wait until this is on screen, then act on it". Ur OCR (Ur Golem
after the merge) already matches regions; with affine (position and scale tolerant) matching it can find a button
wherever the game draws it. A step:

    wait_for: template "Accept!" (or text) within region, timeout 5 s
    then:     click its centre
    else:     branch (e.g. "Inventory is open" -> click its X, retry)

The trade becomes a flow file instead of six macros plus an agent. Branches cover the known detours (bar left open,
stale list, popup in the way). Item identity still needs a check: a hover-tooltip read ("Huge Turtle", "Rainbow",
"#2,106") before the click, which is OCR text matching on a known region.

**Fits with.** The Ur Golem merge (docs/superpowers specs, 2026-09-30) already brings OCR triggers into Ur Task.
This extends a trigger from "fire a macro" to "gate a step".

## 2. Per-account walk calibration: grow the walk until it arrives

**Problem.** Walk speed differs per account (speed upgrades, potions, hoverboards; playbook, 2026-10-03). A timed W
hold recorded on one account over- or under-shoots on another. Today the fix is per-account copies of a walk, and the
macro playbook says to walk on foot only until timings are measured.

**Idea (Este).** Treat a walk as distance, not milliseconds, and let each account find its own time:

1. A walk step declares a target ("until the E prompt shows", "until the egg is centred", an OCR or pixel check).
2. First run on an account starts with the shortest duration (fastest known speed).
3. If the target is not reached, extend by the next speed tier's increment and check again, and so on.
4. The duration that arrived is stored per account id (Roblox user id) for that walk.
5. Later runs on that account use the stored value, and re-grow it if a check fails (speed changed: new upgrade,
   potion expired).

**Calibration macro.** A user runs one calibration walk per account, e.g. a fixed path between two landmarks
in a known zone, and Ur Task records seconds per unit for each account. Every timed walk then scales by
`account_speed / recorded_speed`. The same flow is the measurement study (below), packaged for users.

**Needs first (Este: "Movement is foundational").** A movement pacing study: walk speed per account in the
regular world and in the event world, measured, not assumed. Known data so far: top-down zoomed-out plaza,
`step W+D 250ms` moved the trader about 47 px (0.19 px/ms); hatch-box exit on the main took S250 D250 S1000.

## Open questions

- Where the per-account table lives: next to macros (portable with export) or in host settings.
- Whether speed is linear enough that one ratio per account works, or needs one per world (event worlds can apply
  their own speed modifiers).
- How a failed check is detected without OCR on every step (pixel probe vs full OCR).
