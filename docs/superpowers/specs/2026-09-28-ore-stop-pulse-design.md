# Ore stop v1: ride to a target layer, then pulse and clear

**Status:** spec approved by Este, 2026-09-28 (all listed choices accepted)
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

## Found in the live run (2026-09-28)

- **The white outline is hidden while the mouse button is held.** It shows only while hovering. A
  hold on ItsJustEstePapa let go "outline gone" after 0.3 s while the block was still there, and the
  outline came back on release. That fails merge gate F4, so the outline-gone release during a hold
  must not ship as built.
- **Colour drift is too eager to end a hold.** The hit darkens a block (pink #A80B55 to #6C0224), so
  a weaker account let go after 0.3 s without breaking the block. The main breaks blocks in 0.3 s,
  which hid this.
- **Outline counts:** 385 to 460 near-white pixels in a 120x120 box on an outlined block's edge,
  against 0 to 7 on plain rock. A threshold of 60 has a wide margin.
- **Next design, hold-release-look:** hover and check the outline (none means skip); press for one
  beat (about 1 s); release and check again. If it's still outlined, the block is still there, so
  hold another beat. If there's no outline, it broke, so move on. There's no time limit on ore, per
  decision 5. Colour drift no longer ends a hold. This replaces the outline-gone and colour-drift
  releases for pulse clears.
- **Hide My Pets.** Pets sit over the character and the ring spots. Required now: the pulse setup and
  README tell the user to turn on Hide My Pets (in Settings) before running the pulse loop. Planned: a
  checked macro opens Settings, reads the toggle (green "On" or red "Off"), remembers the user's
  setting, turns it On when the loop starts, and restores it when the loop stops.

## Reach, measured, and the ore finder (approved by Este 2026-09-28)

**Reach is a distance, about 6 blocks.** Este's hover shots (2026-09-28 12:53 to 13:02):

- From the top of ItsJustEstePapa's mine (zoomed out, top-down), every hovered surface block lit up,
  corners included: at least 8 blocks across on the character's own level.
- On the main at the Mine #8 bottom layer, a middle-layer wall block about 6 blocks up and a little to
  the side lights up (130232); the one above it does not (130235). Straight overhead it may be 7 or 8.
- The Mine #8 bottom layer opens into a bowl about 9 by 8 blocks, walled by the middle layer, with ore
  set in the walls and floor. Top-down in the bowl a block is about 50 px and the character sits near
  390,340 (800x599 client).

So everything on the bowl floor is in reach, and the higher wall blocks are not. The outline check
already tells the two apart; a block out of reach costs one look (about 300 ms) and is skipped.

**Decision: an ore finder plus a grid, instead of the 8-spot ring.** Este: "it has always been the way
I have wanted to head. Like we are building our own AHK." Ur Task becomes a pointer the caller can aim,
not only a player of recorded macros. The ring touches 8 of about 70
blocks in view. At the target layer, during the calm pause:

1. **Ore first, by colour, anywhere in the frame.** Cyan crystal, purple/magenta amethyst and white
   quartz stand apart from all three Mine #8 rocks. Ur OCR finds ore-coloured patches in the calm
   frame, and each patch's centre becomes a target. Colour only picks where to look; the outline
   still decides whether to press.
2. **Then stone, on a grid.** Targets every block pitch (about 50 px at the bottom layer) out to a
   radius of about 5 blocks around the character, nearest first.
3. **Each target:** hover, outline check, hold-release-look (built, 0.11.0 Task 7). No outline means
   skip.

**Box size follows the block size.** A block is about 21 px at the surface, 50 px in the bowl and
170 px in the shaft. Clearing only happens at the target layer, so the outline box and the grid pitch
are measured there, once per zone, and live in the measured file with the ring.

**What it needs:**
- **Ur Task:** a way to press at a point Ur OCR supplies. Today every macro's points are fixed, so it
  is either one "Clear at x,y" bridge call or a small set of generated grid macros. The bridge call
  is cleaner. It is an additive method on Ur Task's own bridge (contract 1.0), not a RoRoRo host
  change.
- **Ur OCR:** ore-colour patches in the calm frame, grid targets, and the order: ore nearest first,
  then stone nearest first.

### Block size is read every pass (approved by Este 2026-09-28)

Found live: the camera is always fully zoomed out, but Roblox pulls it in to the first wall in its
way. So block size follows how open the space is, not the zoom: about 170 px in a one-block shaft,
about 32 px in the bottom pit after Este's special ability opened it, 22 px on the open surface (Este's
shots 15:02 and 15:03, and the 14:47 capture). The character stays near the frame centre
(about 400,340) in all of them.

- **Ur OCR measures the block size on each calm frame.** The repeat spacing of the rock pattern
  around the character (the bottom layer's blue grid lines are the clearest case). The spacing
  sets the grid pitch, the reach radius in pixels and the outline box (w = h = pitch). If no clear
  spacing is found, the layer's measured `pitch` is the fallback, and the log says which one was
  used.
- **The outline box cap rises from 120 px to 240 px** in Ur Task (OutlineCheck and ClearAt), so a
  shaft-sized block still fits. minCount stays 60: an outline on a 22 px block is still about 90
  near-white pixels or more.
- Ore finding is by colour and does not change.

### The layer is read by colour share, not 8 spots (approved by Este 2026-09-28)

Found live (17:08): the 8-spot vote read the Mine #8 top layer as the bottom one twice. The top layer's
dark base (#080920, indigo #0D0764) sits within 5 RGB of the bottom layer's near-black (#0A0A1C) and
dark grid line (#0E0A67), so any spot landing on base rock votes wrong. What does separate the layers
is the mix: the top layer has hot magenta, purple and orange veins, the middle is periwinkle stone,
the bottom is near-black with bright blue lines and no red.

- Each layer's `rock` list keeps only the colours that layer alone has. Colours shared between
  layers are left out of every list, so they count for nobody.
- On the calm frame, the pulse reads a disc around the character, radius 3 blocks (from that
  pass's block size), with the HUD masked. Each pixel goes to the nearest listed rock colour
  within tolerance, and ore colours count for nobody. The layer with the largest share wins when
  its share is at least `minShare` of the disc and at least `lead` times the runner-up. Otherwise
  it's no layer, and the pulse rides a burst, as today.
- The 8-spot ring stays for the old ring triggers. Only the pulse's layer read changes.

### One block per spot: tap, look, stop when it changes (Este chose single blocks, 2026-09-28)

A held beat digs several blocks deep: the main breaks a block about every 0.3 s, and once a block
breaks, the block below lights up at the same spot. Measured live the same evening (probe frames in
the sweep folder `2026-09-28-taps\probe`):

- A 250 ms tap broke one surface block on the main and on ItsJustEstePapa, including a red crystal
  ore on the main.
- When a block breaks, the outline either disappears or jumps: it moves and shrinks to the next
  block down, seen through the hole. When a tap hits without breaking, the outline's bounding box
  stays where it was (same edges within a few pixels).
- The outline clears within 150 ms of the pointer leaving the block. It does not linger.
- "Move one box-width away" for the baseline can land on the neighbouring block, which lights up
  with its edge inside the box (baseline 461 = hovered 461, a false skip).

So, for a reach hold:
1. **Baseline:** park the pointer on the Roblox window's title bar (above the client area, so it
   can't hover a block and clicks nothing), wait for the outline to clear, then count.
2. **Look before:** hover the spot, and record the outline's count over the baseline and its
   bounding box (the near-white pixels in the box).
3. **Tap:** press for 250 ms, release.
4. **Look after:** after a short settle, count and box again.
   - No outline (count over baseline below minCount): the block broke. Done.
   - The box moved or resized (any edge more than 6 px from before): the block broke and a new one
     is showing. Done. Don't press the new one.
   - Same box: hit, not broken. Tap again from step 3. No time limit (decision 5) unless the step
     sets maxMs.

This replaces the 1 s hold-release-look beats for reach holds. Non-reach holds are unchanged.

### The ClearAt bridge call (Ur Task bridge 1.x, additive)

One call carries a whole ordered list of points and plays as ONE playback, so it rides the existing
single-flight rule, playback id, GetPlayback, StopMacro and Esc unchanged.

Request, camelCase JSON like every other method:

    { "contractVersion": "1.0", "method": "ClearAt", "callerPluginId": "...",
      "target": "<decimal user id>",
      "client": { "w": 800, "h": 599 },          // client size the points were measured in
      "points": [ { "x": 412, "y": 288, "label": "ore 1" }, ... ],   // in order; 1..64 points
      "outline": { "w": 50, "h": 50, "minCount": 60, "whiteMin": 225 },
      "maxMsPerPoint": null }                     // null = no time limit (spec decision 5)

- Ur Task treats `client` exactly like a recorded macro's client size: the window is set to it first
  (EnsureClientSize), then points and the outline box play unscaled. (Ruling 2026-09-28: follow the
  existing macro path rather than a second scaling rule.)
- Refusals beyond the list below (plan ruling): whiteMin 0, maxMsPerPoint below 1, an outline box over
  240 px (raised from 120, see "Block size is read every pass"), a minCount larger than the box.
- Each point plays as a reach hold: hover, outline check (grace 300 ms), no outline means skip,
  otherwise hold-release-look beats until the outline is gone (or maxMsPerPoint).
- Baseline (ruling 2026-09-28, every reach hold, ClearAt and recorded spots alike): before the pointer
  moves onto the point, the outline box is captured once with the pointer elsewhere (moved one box-width
  outside first if it is inside); the outline shows when count minus that baseline is at least minCount,
  for the pre-press check and every beat look. White quartz passes the raw count with no hover.
- Foreground rule as macros: it acts only while the target's window is in front; a lost foreground
  aborts the playback.
- Response is RunMacroResponse (ok + playbackId, or refused with reason). Refusals: bad version,
  missing callerPluginId, unknown target, 0 or more than 64 points, a point or box outside `client`,
  minCount below 1, whiteMin outside 0..255, busy (another playback running).
- GetPlayback afterwards: finished (something was pressed), finished + reason "skipped" (every point
  skipped), stopped, failed as today. Nothing new on the wire there.
- The synthetic playback is never saved to the macro library and never shows in ListMacros. Its log
  lines name it "ClearAt (N points)" and each point by its label.

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
