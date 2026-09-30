# Ore stop: sweep the stone, target the ore

**Status:** Design decisions recorded 2026-09-29 (Este answered the four questions). Waiting on the live drag measurement before a plan. Not built.
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

**Still first, before a plan:** the live measurement above. If a held, moving pointer doesn't mine
each block it passes over, this design changes shape.
