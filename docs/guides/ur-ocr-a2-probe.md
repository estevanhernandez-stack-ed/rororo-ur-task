# A2 probe: fire Ur Task from an Ur-OCR color trigger (no bridge yet)

This works **today**, with the shipping versions of both plugins — no update
required. It validates the whole "detect an on-screen event → run my macros"
flow before the macro bridge lands.

## What it does

Ur-OCR already fires a **keybind** when a screen region matches a color. Ur Task
already plays your assignment set on **Ctrl+Shift+P**. Point one at the other.

## Setup

1. In **Ur Task**, set up your assignment table as usual (assign macros to alts,
   or leave alts on keep-alive). Confirm **Ctrl+Shift+P** plays the set.
2. In **Ur-OCR**, add a **color trigger**:
   - Pick the screen region that shows the event indicator.
   - Pick the target color + tolerance.
   - Set the **keybind** to **Ctrl+Shift+P**.
   - Set a cooldown longer than one full assignment pass so it doesn't re-fire
     mid-run.
3. Anchor your Roblox window (fixed position + size) — Ur-OCR reads absolute
   screen pixels, so a moved window breaks the region.
4. Arm the trigger. When the color appears, Ur-OCR presses Ctrl+Shift+P and Ur
   Task runs the set. Watch Ur Task's activity log to confirm.

## Limits (why the bridge is still coming)

- Fires the **whole assignment set**, not one specific macro on specific alts.
- No structured result back to Ur-OCR — you read Ur Task's activity log.

The A3 macro bridge replaces the keybind with a direct "run *this* macro on
*these* alts" call. Until then, this gets you a working event→action loop.
