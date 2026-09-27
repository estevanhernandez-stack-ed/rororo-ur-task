# Point clicks and colour checks — design

**Date:** 2026-09-27 · **Status:** design approved in conversation, spec awaiting review
**Raised by:** Este, while building Space Mine macros on Dunder-MiffLan
**Backlog entry:** "Expose and drag a macro's click points" (`docs/BACKLOG.md`)
**Dashboard decision:** V0wW67imzbu077AQM0Ds

## Why

A mouse macro today replays every recorded event, including the path the hand took between clicks.
Two things went wrong with that on 2026-09-27, both seen live:

- **The UI is not the same on every account.** "Mine Zone 8", recorded on estehernandez and played
  on CElCPapa, opened the Teleport window correctly, but the button that opens it sits in a
  different place per account. The left button column reflows around the icons each account has.
  Nothing in a macro today can be fixed for one account without re-recording it.
- **The game already shows state that a macro should act on, and a macro cannot read it.** The
  Teleport window shows an unlocked mine as a green tile and a locked one as a grey "???" tile.
  CElCPapa clicked the locked #8 and stood there. The Auto Hatch toggle shows green "On" or red
  "Off", and a macro that presses it blind turns it off half the time.

Este's rule settles the shape: **the path does not matter, only where it clicks.** A recording
collapses to point clicks, as the converted clan AutoHotkey script already works. A point can be
dragged when an account's UI differs, and can check a colour before it presses.

## When it matters

Este's main reaches the final zone of an event about 30 minutes in. That is when macros start to
matter, and they have to be written fast: Este records by hand, an agent reads the recording and
adds the checks, the macros run on the alts. Every part of this design should make that loop
faster. An alt that cannot reach the final zone should say so plainly, because that points at
the alt's progress, not at the macro.

No deadline is attached to this work.

## Decisions

| # | Question | Decision |
|---|---|---|
| 1 | Deadline | None; build it properly |
| 2 | When a recording becomes points | At save, automatically; the raw recording is kept in the file |
| 3 | How a branch appears | A **first-match** step: ordered candidates, press the first that matches |
| 4 | Which points check colour | Every point stores a sample; the check is **off by default**, one toggle to enable |
| 5 | A failed check | Wait up to 3 s for the colour, then search about 24 px around, then stop and report |
| 6 | Whose adjustment | Per account and display scale, stored on the PC, never learned silently |
| 7 | Who makes a first-match step | Anyone, on the points overlay, by clicking candidates on the live window |
| — | Where points live | A new `steps` list in the macro file, schema v4 |

## 1. The macro file, schema v4

A v4 macro keeps every v3 field (recorded client size, place id, `coordSpace: client`, and the
rest) and adds:

- **`recordedDisplayScale`**, for example `100`. This is proposal 1 of
  `docs/display-scale-findings.md`, done here because points mean nothing without it.
- **`steps`**, the list that plays, in order. Every step has `delayMs`, the wait before it.
- **`events`** stays exactly as recorded. It is the original and never plays once `steps` exists.

Step kinds:

| Step | Holds |
|---|---|
| `Key` | key code, and how long it is held (down to up) |
| `Point` | `id`, optional `label`, `x`, `y`, mouse button, `check` (see below), `checkEnabled` |
| `Drag` | mouse button, start point, direction and distance |
| `Wheel` | point, amount |
| `PointerMove` | relative movement while Roblox holds the pointer (first person, shift-lock) |
| `Wait` | milliseconds |
| `FirstMatch` | ordered `candidates` (each a `Point` with a `check`), and `onNoMatch`: `skip` or `stopAndReport` |

A **check** is:

- `box`: an offset from the point and a size, default 5x5, at most 9x9. The sample and the check
  always average **the same box**. Ur OCR's picker samples the clicked pixel and checks the
  region centre, which only line up by luck; this design does not repeat that.
- `expect`: the average colour of the box.
- `other` (optional): the colour of the other state, such as the grey of a locked tile or the red
  of an "Off" toggle.
- `tolerance`: Euclidean RGB distance, default 15. This number is unproven: Ur OCR has only ever
  shipped single-pixel checks. Checks log their measured distance so the default can be tuned
  from real runs.

A box matches when its colour is within `tolerance` of `expect`, **and, when `other` is set, closer
to `expect` than to `other`.** Greys drift 20 to 40 under darkening and hover highlights, so a
fixed threshold alone is not enough to tell green from grey.

Point ids are short and stable, not list positions, so adjustments and first-match candidates
survive steps being added or reordered.

v3 files are untouched. A macro without `steps` plays through today's event player as it does now.
Converting writes v4 and never drops `events`.

Adjustments are not in the macro file. See section 3.

## 2. Converting and playing

### Conversion

Runs when a recording is saved, and on "Convert to points" for an existing macro.

- A mouse down and up **at the same spot** (within 4 px) becomes a `Point` at the down position.
- A down and up **far apart** becomes a `Drag`. A right-button drag is a camera turn, so it keeps
  its direction and a distance with margin added, so that playback overshoots to the camera limit
  (playbook: "Drive the camera to its limit, then count back").
- Moves while the pointer is locked become `PointerMove`.
- The wheel becomes `Wheel`. Keys become `Key` with their real held duration; auto-repeat downs
  collapse. The egg recording's ninety repeated A downs are one A held for about 3 s.
- The stop-hotkey tail is trimmed: the Ctrl and Shift left at the end of every recording, which
  could leave Ctrl held.
- Repeated presses on one spot stay separate points. A macro cannot know whether a button is a
  toggle; the overlay can merge them.
- A stretch that cannot be understood, such as a down with no up, stays as raw events inside one
  step and is flagged in the overlay.

**Delays split into travel and stillness.** The recorded gap before a click is partly the hand
moving and partly the hand at rest, usually while the screen loads. Time the mouse spent moving is
dropped and replaced by a fixed jump and wiggle of about 0.15 s. Time it sat still is kept as the
point's `delayMs`. Measured on "Mine Zone 8": of 19.4 s between clicks, 5.7 s was travel.

### Playing a point

1. Find the position: the account's adjustment if one exists (section 3), otherwise the recorded
   point, then scale by current client size over recorded client size, and by current display
   scale over recorded display scale.
2. **Check off:** wait `delayMs`, jump, wiggle, press.
3. **Check on:** the delay becomes a ceiling, not a wait.
   - The target window must be in front before any check, because the capture reads the screen
     and sees whatever covers the window (Ur OCR captures by BitBlt from the desktop; the grid
     tool hit exactly this on 2026-09-27).
   - The pointer stays outside the box while checking, because hover changes a button's colour.
   - Press **as soon as the box matches**, waiting up to `delayMs` plus 3 s.
   - Not matched: search within about 24 px of the point, scaled like the point. Press where it
     matches, and log "point 3 found 12 px left".
   - Still not matched: **stop and report**, for example
     *"CElCPapa: step 3 'Teleport opener' expected green, saw dark blue (distance 142) after 5.8 s
     at 100%."*

### Playing a first-match step

Candidates are checked once each, in order. Only the first candidate gets the wait, so a window
that is fading in has its moment. The first candidate that matches is pressed. None matching means
`skip`, or stop and report naming the last candidate checked, such as *"CElCPapa stopped at #7:
#8 is locked."*

All of this is a new step player beside today's event player. The v3 path does not change.

### Samples taken at record time are provisional

At record time the pointer is on the button, so the sample shows the hover colour and may include
the cursor. Checks are off by default, and enabling one on the overlay takes a fresh sample with
the pointer parked elsewhere. A check is never enabled on a record-time sample without that
fresh sample.

## 3. The points overlay and adjustments

**Opening it:** a Points button on a v4 macro, then pick the alt. The overlay covers that alt's
game area. It is refused during playback, because a tag on a click point swallows the click.

**What it shows**, keeping the rules the AutoHotkey version proved:

- A numbered tag beside each point, never on it. The exact spot is the pixel just outside the
  tag's marked corner, so a tag never covers what it aims at and never spoils a colour read.
- The label, whether the check is on, and a live swatch of the box next to the stored sample,
  with match or no match. When the window is covered, the swatch says "window covered" instead of
  showing a false colour.
- A grey marker on the recorded spot of anything moved, with Restore always one click away.
- Drags as arrows; first-match candidates as tags numbered 8a, 8b and so on, in check order.

**What you can do:** drag a point, name it, enable or disable its check, re-sample `expect` or
`other` from the live screen, edit its delay, merge repeated presses, and "Make first match"
(then click each candidate on the live window, each sampled there and then). Keyboard steps are
listed in a side panel with their delays.

**Adjustments store:** `adjustments.json` in Ur Task's data folder
(`%LOCALAPPDATA%\626Labs\RoRoRoUrTask\`), **not in the `macros` folder**: Ur Task and Ur OCR both
read every `.json` there as a macro. Keyed by macro id, then point id, then account, then display
scale, holding x and y. Dragging a point for CElCPapa at 100% changes only that entry. Labels,
checks, delays and candidates are edited in the macro itself, because they are the same for every
account. Adjustments leave the PC only when someone exports them.

**The overlay and agents share the macro file.** Ur Task re-reads macros on every bridge call, so
an agent run through Ur MCP and an open overlay could collide. The overlay saves atomically and
holds a lock while open; a write during that is refused with "Points overlay open for Mine Zone 8".

## 4. What else it touches

- **Bundles** carry v4 as it is, `events` included. An older Ur Task receiving one says which
  version it needs, rather than playing a macro it cannot read.
- **The AutoHotkey exporter** exports v4 as point clicks, which is what the converted clan script
  does already. Checks export as comments for now.
- **Recipes** reference macros by id and do not change.
- **Ur OCR** reads only `id` and `name` from each macro file (`Storage/UrTaskMacros.cs`), so v4 is
  safe for it.
- **The action bridge gains `GetPlayback`.** `RunMacro` returns when playback starts, and nothing
  can ask afterwards how it ended. `GetPlayback(playbackId)` returns
  `{ ok, state: running | finished | stopped | failed, reason, detail, stepIndex }`. Ur Task keeps
  finished playbacks for 10 minutes so a later call can read them.
  - This is an added method, like `ListMacros` and `StopMacro` before it. Existing replies keep
    every field (`ok`, `playbackId`, `reason`, `detail`, `stopped`, `macros[].id/name`), and
    contract version `"1.0"` stays accepted. Older Ur MCP builds never call the new method.
  - `RunMacro` must not wait for playback to finish: Ur MCP's client has no read timeout, and a
    repeating macro never finishes.
  - `reason` and `detail` reach Claude word for word, so they are written as sentences.
  - **Dependency on Ur MCP:** a tool that reads `GetPlayback`, likely `wait_for_macro`, polling
    like `wait_for_ingame`. That change lives in the `rororo-ur-mcp` repo and is built there, not
    by this spec. Este gave the go-ahead on 2026-09-27. It builds against the `GetPlayback` shape
    above, on a branch, and merges after this side ships.

## 5. Failures

- Every stop names the account, step, label, expected and seen colour with its distance, the wait,
  and the display scale, so a screenshot of it is a support thread that answers itself.
- A missing or zero-size window, or a client size Roblox cannot reach at this display scale, is
  refused before the first step, as `EnsureClientSize` does today, with the wording fix from the
  display-scale findings (proposal 4).
- A check that cannot capture, because the window is minimised or not in front after activation,
  fails as "could not see the window", not as a colour mismatch.

## 6. Testing

- **Conversion:** unit tests using the two real recordings from 2026-09-27, "Mine Zone 8" and
  "Mining Z8 Egg", as fixtures. Check the collapsed steps, the travel and still split (5.7 s of
  travel in "Mine Zone 8"), held keys, and the trimmed tail.
- **Checks and first match:** against captured screenshots: #8 green and grey, Auto Hatch on and
  off. Cover the wait, the nearby search, the `other` rule, and the report text.
- **Adjustments and scaling:** plain math tests, plus a fake window that enforces Roblox's minimum
  size. A harness without that clamp passed once and then failed on real hardware.
- **Bridge:** `GetPlayback` states and retention; existing replies unchanged byte for byte.
- **Live pass on Dunder-MiffLan**, at 100% and then at 125%. This also answers test 4 of the
  2026-09-27 handoff.

## Out of scope

- Finding an icon anywhere in the button column. It is the full fix for the reflowing column and
  gets its own spec.
- Learning adjustments automatically from the nearby search.
- Shape or image matching.
- Ur OCR's own drag-a-region overlay (it already has a region picker; making regions draggable over
  a live alt is separate work).
- Capture that works while a window is covered (PrintWindow or Windows Graphics Capture).
- The Ur MCP tool for `GetPlayback`, named above as a dependency.
