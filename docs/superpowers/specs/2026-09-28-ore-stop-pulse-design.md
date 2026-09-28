# Ore stop v1: ride to a target layer, then pulse and clear

**Status:** direction agreed with Este, 2026-09-28; spec awaiting review
**Replaces:** the "watch the ring while riding" part of `2026-09-27-ore-stop-loop-design.md`. Everything
already built is kept: the hold step, the checked Auto Mine toggles, the layer vote, playback logs.
**Products:** Ur Task (acts), Ur OCR (reads the layer). No RoRoRo host change.

## Why the change

The 2026-09-28 sweep (60 frames, one full ride down Mine #8, 480 ring samples) showed that watching fixed
spots while Auto Mine rides does not work in this mine:

- about 40% of ride frames have an effect over the spots: enchant power-ball white flashes, cyan bursts,
  the orange pickaxe swing, pets, "New Item!" cards, currency popups;
- plain rock spans dark navy (#080913) to hot magenta (#EB0974), which overlaps the red and pink crystal
  ores, so "not rock" cannot tell veins from ore.

Standing still is clean: frames before the ride barely changed. And the game already marks what the
pickaxe can break: a white outline on the block under the mouse.

## What it does

1. **Ride to the account's target.** Auto Mine digs. Ur OCR's layer vote reads the rock around the
   character. When the vote reads the account's target layer (1, 2 or 3, counting rock types down), the
   loop stops Auto Mine. Setting per account: target layer, and "top of it" or "one level above" for an
   under-powered account.
2. **Pause, then clear.** Wait about a second for effects to settle. Then for each spot in reach, move
   the pointer onto it and check for the white outline:
   - outlined: hold until it breaks (the existing hold step; no time limit, as spec decision 5);
   - not outlined: skip.

   Order: ore first (the brightest non-rock colour in the calm frame), then stone, which pays coins too.
3. **Ride on a little and repeat.** With nothing outlined in reach, turn Auto Mine on for a short burst
   (configurable, default 2 s), then pause and clear again.
4. **Past the target:** if the vote reads a layer below the target, press Go to Top and ride down again.
   Auto Mine also returns to the top by itself at the bottom.
5. **Rock cap per layer:** if clearing makes no progress for the configured minutes on one layer, Go to
   Top, logged by Ur OCR as before ("Went to top: N minutes on the <layer> layer").

## The outline check (new in Ur Task)

The outline is a 1-2 px white frame around the hovered block's face, visible up to about 3 blocks out
(Este's screenshots, 2026-09-28 08:03). Averaging a 5x5 box cannot see a line that thin. New check kind
on a point or hold step: **count near-white pixels** (each channel >= 225) in a block-sized rectangle
around the spot, and pass when the count is at least a threshold. The threshold and rectangle size per
spot are measured live, first with the pointer on and off a breakable block.

The hold uses it twice: before pressing (no outline, no press) and during the hold (the outline gone
means the block broke or went out of reach, so let go).

## Depth

The layer vote only runs on calm frames: during the pause, never during the ride. Each zone has its own
three rock types, learnt per zone from a calm sample at each layer (one measured file per mine). The
ride itself is timed, not read.

## Settings per account

- target layer (1-3) and "top" or "one above";
- ride burst between clears (default 2 s);
- rock cap minutes (default 5);
- pause before reading (default 1000 ms).

## Not in v1 (v2: the bomb grid)

At the target layer: walk to the middle of a 3x3 area, drop a bomb from its hotbar key, walk 3 blocks to
the next middle, repeat; bombs go off after a few seconds and clear about 10 blocks down. Rare wide bombs
and small bombs on the way down are options. Walking is timed arrow keys, which needs its own live
measuring (speed, walls, a mine reset mid-run). Breaking more makes the mine reset sooner; reset time
depends on the mine's size. Build v2 for the next mining event.

## Testing

- Ur Task: the outline-count check against a fake capture (a thin white frame passes, averaged lava
  does not); the hold's before and during use of it.
- Ur OCR: the loop's states (ride, pause, read, clear, burst) against fakes; the per-account target.
- Live on Dunder-MiffLan: measure the outline threshold, sample each Mine #8 layer at rest, then a run
  on the main (target 3) and one alt (target 1 or 2).
