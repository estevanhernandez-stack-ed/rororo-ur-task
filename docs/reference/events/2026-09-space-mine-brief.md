# Event brief: Pet Simulator 99 Space Mine and Space Forge, September 2026

What an authoring session needs to know about this event before it writes a macro or trigger.
Distilled from the PS99 devblogs of 2026-09-19 (Space Mine) and 2026-09-26 (Space Forge), plus
what the clan found worked. The raw posts sit next to this file under `raw/`, kept out of git.

## The one thing that changed between weeks

| Week | Contest | What scores | So the loop should |
| --- | --- | --- | --- |
| 1, from 2026-09-19 | Yee-haw Clan Battle, 6 days | Every block broken adds to the clan total | Break the most blocks per minute, any blocks |
| 2, from 2026-09-26 | Mining League | Rarer ores are worth far more points | Reach rare ores, and break them |

Week 1 rewarded the cheapest block that counts. The clan's answer was to get down to the bottom
layer, where the rock changes from top to middle to bottom, and drop bombs bought from the Mining
Merchant. Week 2 rewards ore rarity, so the same loop scores badly. Read the contest before
choosing the loop.

## The world

- **Eight mines, each harder and longer than the last.** Moon Base, Asteroid Belt, Comet Ice,
  Nebula Depths, Black Hole Core, then week 2 added Alien Caverns, Supernova Forge, and Eclipse
  Rift. Rock comes in three layers per mine: top, middle, bottom.
- **Ores.** Moonstone, Star Ruby, Helium-3, Nebulite, with Dark Matter only in the Black Hole Core.
  Week 2 added Starlight Quartz, Sunstone, and Eclipse Onyx, the last only in Eclipse Rift.
- **Mining Chests** are buried in the week 2 rock. They hold enchants, potions, and in the rare
  ones a Huge, a Titanic, or the Solar Flare pickaxe.
- **Auto Mine** finds the next block by itself. It is the AFK loop the stop-for-gem flow interrupts.

## Consumables that act like buttons

Space TNT from the Mining Merchant. Each one moves or clears rock in a fixed way, which makes it a
one-click action a macro can rely on.

- **Core Charge** bores a shaft 20 layers straight down. The fastest way to the bottom layer.
- **Drill Array** sinks five shafts in an X. Strip mining, many blocks per charge.
- **Rover Charge** leaps and slams down three times, blasting each landing.
- **Breach Charge** is a small blast with massive power that cracks the rock above the pickaxe.
- **Void Charge** pulls the rock inward, then blows it out.
- **Stardust Charge** turns the surrounding rock into solid ore. Worth the most in league week.
- **Big Bang TNT** is the Forever Pack's nuke.

## Upgrades, and where the stages are

- **Pickaxe quests** hand out stronger pickaxes in order: Scrap, Plasma, Laser, Helium-3, then in
  week 2 Ion, Quantum, and Corona for clearing the last quest board. Solar Flare only comes from
  the rarest chests. Each pickaxe is a stage boundary for a progressive macro.
- **Enchants**, week 2. Fourteen powers, and better pickaxes hold more. The ones that change a
  macro: X-Ray finds buried ores, Fracture shatters everything nearby, Fortune and Gem Upgrade
  raise the haul, Explosive and Lightning clear rock.
- **The Enchant Machine wipes the pickaxe on every roll.** Never put it in a loop. A macro that
  clicks it by accident destroys the enchants the player rolled for.
- **Mining Boosts** for coins, damage, and speed come from rare ore blocks. Week 2 raised them to
  Tier II.
- **Pets** raise pickaxe damage. Ore eggs, one per mine, hatch with Space Coins.

## UI map, measured 2026-09-27

Game-area pixels on an 800x599 client at 100% display scale. Measured with `tools/grid-capture.ps1`
on four accounts.

- **The left button column is not fixed.** It reflows around the icons an account has, so the same
  button sits in a different place per account. The blue crystal was at about (40, 365) on
  estehernandez and (100, 305) on CElCPapa. Anything opened from that column needs a moveable
  point or an image search, never a fixed click.
- **Go to Top** is fixed, top centre, about (400, 50).
- **Teleport window** (opened from the left column) is fixed on every account. Tiles run #1 to #4
  along the top at y about 230, x about 137, 312, 485, 660, then #5 to #8 back along the bottom at
  y about 390, x 660, 485, 312, 137. Close X about (750, 112). **An unlocked mine's tile is green
  with its name; a locked one is grey with "???".** Read the tile colour to find an account's best
  mine.
- **Hatch Settings** (the green circling-arrows button in the left column). Auto Hatch toggle: on
  shows green "On" on the right at about (555, 193); off shows red "Off" on the left at about
  (490, 190). Charged Eggs is the row below in the same green, so keep a sample box inside the Auto
  Hatch row.
- **Egg buy dialog** (press E at the egg): "Would you like to Auto Hatch" with Buy 1, Buy N, Buy M
  at about (248, 430), (400, 430), (548, 430). The numbers change per account; the positions and the
  green do not.
- **Movement keys:** Up and Down arrows walk without stopping Auto Mine; W and S stop it. Left and
  Right arrows turn the camera.
- **Inside the mines the left column is the same on every account.** It only reflowed in the
  spawn world. The Auto Mine button (pickaxe) is at about (40, 300); its corner dot at about
  (55, 288) is **red when Auto Mine is off and green when on.** Check the dot before pressing.
- **The route from spawn:** "Space Mine!" in the left column, then the robot Teleport button
  (about 100, 172 on the main), then the tile. Give the Teleport window time to finish opening:
  a tile click 2.5 s after it appears missed on slower alts.
- **Auto Mine digs straight down and returns to the top by itself at the bottom.** Go to Top is
  the manual version. Without enchants it misses ore to the sides.

## Recovering after a network drop, 2026-09-27

Two drops, four accounts, recovered through Ur MCP.

- **Clients that die** leave a `--launch-to-tray` Roblox process behind; harmless. Relaunch the
  main into the private server, have the alts follow it, then route each to its best mine.
- **Clients that survive** sit on Roblox's "Disconnected (Error 277)" dialog. Reconnect, at about
  (493, 388), rejoins faster than a relaunch.
- **Reconnect can meet a captcha** ("Verifying you're not a bot"). Never touch it. Closing that
  client and having it follow the main got straight in, once. One data point, not proof.
- **Windows reopen stacked.** Arrange them before reading anything off the screen.

## The goal for the league week (Este, 2026-09-27)

Auto Mine as travel, ore as the score. Ur OCR spots ore, the macro stops Auto Mine, mines every
ore block it can reach from there, then turns Auto Mine back on. That is the stop-for-gem flow
widened to the whole vein.

## Patched in league week (Este, 2026-09-27)

- **Bottom-layer bombing no longer works.** Week 1's clan loop was: get to the bottom layer, walk
  across breaking the easy blocks, then place 3x3 bombs to clear the most. Big Games patched it out
  for week 2. The event ends this week and is expected to return. When it does, test the bombing
  again before building it (piece 2 of `docs/superpowers/specs/2026-09-27-ore-stop-loop-design.md`).

## What this means for the macros

- **Week 1 shape.** Transport to the bottom layer, or Core Charge down, then bombs and automine.
  Block count is the score, so the give-up on a hard gem should be short.
- **Week 2 shape.** Go where the rare ores are, which means deeper mines and X-Ray. The stop-for-gem
  flow matters more than raw block count, and the give-up should be longer for rarer ores.
- **Stage the macros by pickaxe.** What is worth breaking changes with every pickaxe quest.
- **The stop-for-gem flow** is the same both weeks: detect the ore, stop automine, hit it until it
  disappears or the cracks stop growing, then resume.
