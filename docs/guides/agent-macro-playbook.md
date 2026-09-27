# Agent-assisted macros: tips and tricks

A growing collection of rules for anyone, human or agent, authoring a macro or a screen trigger
for a Roblox event. Read it before building one. Add to it after building one.

The day-of procedure lives in `event-day-one-playbook.md`. The sizing math and the display-scale
story live in `ahk-any-screen-clicks.md`. This file is the judgement that sits above both.

## Este's rules

**Buttons are best.** When the game has a button that does the job, click that instead of moving
the character. Teleport, go-to-spawn, and world or zone transport buttons put every alt at the same
known starting point in one click, wherever it was standing and wherever its camera was pointing.
Movement keys drift: each alt starts somewhere slightly different and ends somewhere very different.

- Start every macro with the button that resets state, usually the transport.
- Record from that button, so every alt begins identical.
- When a sequence needs the character somewhere, look for the button that takes it there before
  scripting a walk.

**Set the hotkeys and hotbars the game offers, then use them.** A key press lands the same on every
PC. It does not care about window size, display scale, or where the game drew a button. A click
does. Ur Task records keyboard only by default for exactly this reason.

- Put the item or action on a hotbar slot in the game's settings, and press the number instead of
  clicking the item.
- The binding is per account, so it has to be set on every alt before the macro works there.
- Give the game a moment after a state change before pressing. The disconnect script needed three
  seconds between the automine toggle and the hotbar press before the item took.

**Shape beats colour when the colour is shared.** Some of the rarer gems are the same colour as
the blocks around them. In the mining event the walls are dark navy blocks scattered with cyan
stars, and a cyan gem sitting in that wall shares its colour with the sparkle around it. A colour
trigger aimed at that gem fires on the wall. The pink gems are the only pink in the scene, so colour
works for those.

- Before choosing a colour trigger, sample the surroundings. Run Test now with the region over
  plain wall, plain floor, and the HUD. If it matches anywhere the target is not, colour is the
  wrong cue.
- Prefer the game's own signals when it gives them: the white outline it draws around the block
  you are facing, the counter that ticks up when a gem is collected, a popup. Those are designed
  to be seen.
- Keep colour regions away from the fixed HUD, which reuses the same reds and cyans. Anchor the
  region to the game world, not the whole window.
- Ur OCR has no shape matching today. An image mode that matches a small picture of the gem, with
  tolerance and scale, is the feature this rule asks for. Until it exists, an agent can tell
  shapes apart in a screenshot but the running detector cannot, so the agent must pick a cue the
  detector can see.

**When any block counts, keep breaking the easy ones.** Missions say "break this many blocks" and
do not care which. The mine gets harder the deeper you go, and at the start even the top blocks
are slow. So: Go to Top, start automine, and hit Go to Top again whenever you like, because the
first sections break fastest. The transport button is doing double duty here, as the reset and as
the loop.

- Build the loop as transport, automine on, wait, transport, and let it repeat. Named points and
  a recipe already cover that shape in Ur Task.
- Make macros progressive. What is worth doing changes as pets and tools upgrade, so keep one
  macro per stage rather than one macro that tries to be right forever. Name them by stage.
- Read the mission text before choosing the loop. The cheapest block that counts is the one to
  break.

**Record first, then let the agent read the recording.** Record the macro by hand and get it
working. Then hand the agent the recording and a screenshot, and it can say what each click was:
this one is the transport button, this pile of twelve is the automine toggle, this one is hotbar
slot 2. What it learns, it keeps. Once it has seen the transport map, every later macro can use
those points without anyone recording them again.

- `tools/grid-capture.ps1 -Macro "<name>"` draws a recording's clicks, numbered, over a capture
  of the game area, and writes a legend with each click's point, time, and the keys pressed
  between clicks. `-Image <png>` draws them over a screenshot you already have.
- HUD clicks label themselves, because the HUD does not move. World clicks depend on where the
  character stood, so read those with the screenshot from the same moment, or from the recording's
  own timing.
- A click on a hotbar item in a recording is a hint to replace it with the key.

**Grid the screenshot.** A picture with a labelled coordinate grid gives an agent exact game-area
pixels to name, instead of guesses from a bare image. The same tool draws the grid on every
capture. Coordinates are in game-area pixels at the capture's display scale, which is written in
the picture's footer and filename, so the agent can divide by the scale when it writes points for
another PC.

**Every stop needs a way out, and the way out should be progress, not a clock.** Stopping the
automine to grab a gem is only worth it if the gem breaks. A gem above your damage level never
breaks, and a macro that holds on until it does is stuck for good. A fixed timer is the crude fix.
The better one is to watch for progress and give up when there is none.

- Prefer a progress signal the game already draws: a health bar shrinking, damage numbers, a
  crack stage changing. No change for a few seconds means give up.
- If there is no signal, use a timer, but measure it first. Log when the first hit landed and when
  the gem disappeared, per gem colour, and set the give-up at about twice the usual break time.
- Turn give-ups into knowledge. A gem tier that timed out twice is above your level, so skip that
  tier until the pets and tools are upgraded, then try it again. That is what makes a macro
  progressive without anyone editing it.
- Giving up is not failing. Release the gem, hit Go to Top or automine, and carry on.

**Pin the camera before you read the world.** The HUD sits still; the world does not. Go to
first person, zoom all the way in, and look straight down, and the block under your feet fills
the same part of the screen every time. That makes a world region as fixed as a HUD region, and
it is where the cracks show: they grow as a spider web from the middle of the block, tiny at
first on a slow gem, and the top-down view is the only one that shows them early.

- Make the camera part of the starting state, next to the transport button. A macro that
  depends on what is under the character has to set the view first, every run.
- Read progress as change, not as a picture of a crack. Watch the middle of the block and compare
  it with a few seconds ago. Growing cracks change the pixels; a gem above your level leaves them
  identical. Keep the floating hit numbers out of the region, since they move every hit.
- The per-hit popup proves the hits are landing. Only the cracks prove the gem will break.

**Match the loop to this week's scoring, and ask the clan what worked.** The contest changes
week to week even inside one event. The Space Mine clan battle counted every block broken, so
the clan's answer was the bottom layer plus bombs from the Mining Merchant, the most blocks per
minute. The Mining League a week later scores ore rarity, so that same loop scores badly.

- Read the contest rules and the devblog before choosing the loop. The event brief in
  `docs/reference/events/` is where that reading goes.
- Write the clan's strategy down with the week it applied to. A tip from a block-count week is
  wrong advice in an ore-rarity week.
- Consumables that move or clear rock in a fixed way, like a charge that bores twenty layers down,
  are buttons in the "buttons are best" sense. Use them where the clan uses them.

## Learned building the disconnect script, September 2026

**Size the game picture, not the window, and multiply by the display scale.** Points measured from
the window corner drift with the frame, and the frame changes with the scale setting. Roblox will
not shrink below 816x638 times the scale, so an "800x600" macro was really calibrated on an
800x599 game picture.

**Never assume a resize worked.** Read the size back. Allow a few pixels, refuse beyond that, and
say what size you wanted and what you got. Clicking on a window that is the wrong size is how a
toggle ends up switched off.

**Toggles are dangerous.** One misfire flips the state the wrong way. Click a toggle once, and check
its visible state before clicking whenever the screen shows it.

**Look at a small box, not a single pixel.** At 100% the UI is drawn pixel for pixel. At any other
scale it is resampled and an edge moves a pixel or two.

**Read colours with the mouse parked elsewhere.** Hovering changes a button's colour, and the
monitor reads it without a hover.

**Fresh windows eat input.** The first click after a window takes focus is dropped. Move the pointer
onto the target and wiggle it before pressing, because Roblox notices movement. Press a non-toggle
button several times; press a toggle once.

**React to the screen, not the clock, when the duration varies.** Ore breaks in different times
depending on upgrades and pets. Wait for it to disappear, not for a timer to run out.

**Name every point, and keep per-PC adjustments apart from the shared macro.** A named point can
be shown, dragged, and restored. Adjustments saved per display scale never leak onto another PC,
and a user can send them back so the defaults improve.

**Make failures screenshot-able.** A refusal that says what it wanted, what it got, and the display
scale is a support thread that answers itself.

**Keep a second way back in.** When a VIP link only opens a captcha, follow a friend who is still in
the server. With RoRoRo as the relauncher, each account comes back with its own login.

**Test at 125% before sharing.** Both main monitors run at 100%, where everything works. The
Surface at 3840x2160 and 125% is the rig that finds what the clan's laptops will find.

## Adding a tip

One rule per entry. Bold the rule, then say why in a sentence or two, then how to apply it. Note
where it came from. Este's rules stay at the top.
