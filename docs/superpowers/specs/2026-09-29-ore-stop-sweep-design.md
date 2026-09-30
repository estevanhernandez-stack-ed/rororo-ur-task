# Ore stop: sweep the stone, target the ore

**Status:** Design decisions recorded 2026-09-29 (Este answered the four questions). Live drag measurement PASSED 2026-09-29; ready for a plan. Not built.
**Builds on:** `2026-09-28-ore-stop-pulse-design.md` (the pulse, ClearAt, the ore finder, the guard).

## Why

Este stopped the pulse on 2026-09-29 at 00:10: "It mines too slow right now. We need to just hold
down left mouse and go around the player if we aren't looking for ore."

Per-spot clearing pays a fixed cost at every spot: park the pointer, take a baseline, hover, look,
hold, look again. That's 0.6-1 s a spot, for a pickaxe that breaks a bottom-layer block in about
0.3 s. The per-spot look exists to find the ore and to stop at one block. Stone needs neither.

## What changes

At the target layer, a pass becomes two phases:

1. **Ore, as now.** The finder's ore points go through ClearAt: outline check, growing holds, one
   block per spot, the guard. Ore is worth the care.
2. **Stone, swept.** One continuous hold. The button goes down on a block **beside** the character,
   the pointer travels a ring path around the character, one block apart, out to the reach radius,
   and the button comes up where it started. **Never on the block under the character:** measured
   overnight, a hold there digs straight down, the character drops with it, and the camera moves,
   so the rest of the path points at the wrong place. Whatever block is under the pointer while it's held gets
   mined. No per-spot look, no baseline, no park.

Then read again, as now. A sweep that broke nothing (the frame barely changed) counts as an empty
pass: turn the camera, then ride a burst.

## The path

- Square rings around the character centre (400,310), from the first ring outward, one block (the
  pass's block size) per step, out to 4 blocks (Este, 2026-09-29: slightly tighter than reach). The centre block is excluded, and the path
  never crosses it (the ring-to-ring step goes outward, not through the middle). It's the same grid the finder already lays, visited in spiral
  order, not nearest-first.
- HUD points are skipped (the same HudMask). The path jumps over them with the button still held;
  the move is a single straight input.
- **The button comes up only on the start block** (the first-ring block the sweep began on). A release is a click, and
  a click on a player or a chest opens a popup. The start block is the one place a release can't
  land on something else.
- The dwell per point is a per-account setting, default 400 ms. The main breaks a bottom-layer block
  in about 0.3 s; a weaker account needs longer.

## Safety

- **Check the guard during the sweep, not only before.** With the button held, the guard pixel
  (the Auto Mine dot) is sampled every few points. If it changes, move back to the start block (the
  first-ring block) and release there, then stop the playback. (A hold can't open a popup; only a release can. Returning
  to the start before releasing keeps even that release safe.)
- The foreground check happens before the press and at every point. On focus loss, release, as
  every hold does today.
- The window is sized to the measured client first, as for ClearAt.

## The Ur Task side

A new additive bridge method, **`SweepPath`**: `{ target, client, path: [{x,y}], dwellMs, guard }`.
It plays as one playback (single-flight, GetPlayback, StopMacro, Esc) and uses real mouse-move
input for every step (Roblox ignores a bare cursor jump). It logs `SweepPath (N points)` and on
completion `swept N points in S s`.

## What to measure live first (before building)

The whole design rests on one unverified assumption: **does holding the button and moving the
pointer mine each block the pointer passes over?** It could instead keep mining the first block,
or stop when the pointer leaves it. Measure on the main with the probe tool (a new `-Mode sweep`):
hold, move the pointer across a row of 3-4 blocks at 400 ms each, release on the start block, and
compare the frames before and after. Also measure how many blocks break per 400 ms dwell, on the
main and on ItsJustEstePapa.

## Decisions (Este, 2026-09-29)

1. **Sweep the stone only; ore keeps its careful path** (ClearAt: outline check, growing holds, one
   block per spot). Unchanged from the draft.
2. **Dwell is per account.** Default 400 ms, with a per-account override, because pickaxe strength
   varies by account: the main breaks a bottom-layer block in about 0.3 s, and a weaker account needs
   longer.
3. **Radius slightly tighter than full reach:** out to 4 blocks (reach is about 5-6), so the path
   stays on blocks the character can surely mine. Tunable after the live measurement.
4. **At the target layer only, never on the ride down.** A sweep needs Auto Mine OFF (the loop
   already turns it off at the target and back on afterwards), so it can't run while Auto Mine rides
   the shaft. The target layer is the user's choice: set it to the top layer to sweep there, or
   choose to go all the way down to the bottom.

**Measured 2026-09-29, Dunder-MiffLan, the main at 100%: a held, moving pointer DOES mine each
block it passes over.** Three runs, top-down camera, three blocks beside the character (never the
one under it), 400 ms per block, released on the start block:

- Run 1 (shaft walls, blue layer): every point's outline box changed and three "New Item!" drops
  appeared; the shaft walls opened. (Este confirmed it was not a mine reset.)
- Run 3 (bottom floor, a pixel-change measure with the pointer parked away): the three dragged
  points changed by 45, 101 and 81 (0-765 scale) against a noise of 0 and two undragged controls
  of 0. Rewards were about double a run that broke less.
- Blocks at this zoom are about 150-180 px, not the pulse's default 50 px: the path's step must
  come from the pass's measured block size, as the design already says.

Frames and the test script: `%LOCALAPPDATA%\626Labs\ore-stop-sweep\2026-09-29-drag\`. The design
stands as written.

## Later inputs (Este, relayed by the controller, 2026-09-29, after the drag run)

5. **The drag measurement is confounded.** The main's special pickaxe shoots power balls that break
   blocks on their own, so the scene change in the drag runs cannot all be credited to the pointer.
   "A held, moving pointer mines every block it passes" stays the working assumption; it is not yet
   a measured fact. The design is not rewritten around this. The Ur OCR plan's live task measures
   the pointer's share on its own: a sweep over a patch well away from the character, each swept
   point's change compared with control points the pointer never passes over, in the same run.
   Power balls also touch the "broke nothing" rule: a power ball that breaks a block on the path
   reads as progress.
6. **Near-camera blocks.** The blocks nearest the camera sit toward the bottom of the window and are
   hard to hit at any distance from the character. On that side only (screen-down from the centre),
   the path's rows continue past ring 4 toward the bottom edge of the client, stopping a margin short
   of it (a named constant, 12 px) and still skipping HUD points. The other three sides stay at
   ring 4. A per-account toggle stored with the dwell, default on. Note for the build: today's
   HudMask masks everything below y 470 of the 599 px client across the full width (the hotbar
   line), so the extension adds at most one row until that band is narrowed to the hotbar's real
   extent, which the live task measures.
