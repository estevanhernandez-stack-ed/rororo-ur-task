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

## What this means for the macros

- **Week 1 shape.** Transport to the bottom layer, or Core Charge down, then bombs and automine.
  Block count is the score, so the give-up on a hard gem should be short.
- **Week 2 shape.** Go where the rare ores are, which means deeper mines and X-Ray. The stop-for-gem
  flow matters more than raw block count, and the give-up should be longer for rarer ores.
- **Stage the macros by pickaxe.** What is worth breaking changes with every pickaxe quest.
- **The stop-for-gem flow** is the same both weeks: detect the ore, stop automine, hit it until it
  disappears or the cracks stop growing, then resume.
