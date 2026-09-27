# Backlog — RoRoRo Ur Task

Findings parked deliberately, with enough context to pick up cold. Newest first.

## Ludacris mode — one input, every window at once

**Raised by Este:** 2026-09-24 · **Kind:** feature, blocked on a cheap experiment

"When I move you move." The user plays their main normally and every other claimed alt mirrors the
same input live, with nothing recorded first.

**Literally simultaneous is not possible from outside the process.** Windows has one foreground
window and one keyboard focus, and `SendInput` is system-wide — it lands wherever focus happens to
be. There is no broadcast primitive to reach for. That is the Win32 input model, not a Roblox
restriction, so it cannot be cleverness'd around.

Three ways around it, and they are not equal:

1. **`PostMessage` per window handle.** Posts input into one specific window's message queue,
   bypassing focus entirely. The only genuine simultaneity available to us, and the only path where
   the user keeps playing while the alts mirror. Whether Roblox acts on it is unknown — games that
   read input through raw input or polling ignore posted messages, and Roblox is likely one of
   them. **`PostMessage` appears nowhere in `src/`; nothing in this family has ever tried
   background input.**
2. **A hook DLL injected into each Roblox process.** What the real multiboxing suites do.
   **Closed — do not revisit.** Code injection into the Roblox client is the exact line RoRoRo's
   posture depends on not crossing, and Hyperion would read it as tampering.
3. **Fast sequential fan-out.** Focus, send, next. Already built — it is what `AssignmentRunner`
   does. `DefaultPerAltDelayMs` is 1000ms per alt (`AssignmentRunner.cs:58`), so eight alts is
   eight-plus seconds a round. That settle is tuned for macro playback reliability, not for live
   mirroring.

**The constraint that decides the feature is focus theft, and it is what makes this unlike every
macro feature already shipped.** Macros run hands-off, so nobody minds the foreground cycling.
Ludacris mode means the user is driving. Focus-cycling takes the foreground away from *them*, so
the input that breaks is their own. That is the shape of the thing, not a tuning problem, and it
is why path 1 is not merely the nicest option but the only one that delivers what was actually
asked for.

**Settle it with an experiment, not an argument.** Post a `WM_KEYDOWN`/`WM_KEYUP` pair and a
`WM_LBUTTONDOWN`/`WM_LBUTTONUP` pair at a backgrounded Roblox window and watch whether the
character moves and the click registers. Perhaps twenty lines. The outcome picks the design: it
works, and Ludacris mode is real; it does not, and the honest version is fan-out sold as "every
alt does this, one after another" rather than as mirroring.

This is the v0.6 positioning lesson again — read what the machine actually does, do not theorise
Win32.

One thing already in our favour if the experiment passes: v0.4's client-space coordinates mean a
click at client (x,y) maps correctly onto every window whatever its size or position. The geometry
is solved.

## Autoclicker — one repeater engine, a standalone toggle and a macro step

**Raised by Este:** 2026-09-24 · **Kind:** feature, not started

Send a key or a mouse button on repeat at an interval the user picks. Two ways in, one engine
underneath:

1. **A standalone toggle.** A chord hotkey arms it with no macro involved — the case where you are
   just playing and want fast clicks. This is AutoHotkey parity, and the reference is already in
   this repo: `K0ii_Double_Hatching_V1.2.ahk` runs a `ClickLoop` at `ClickInterval` ms and a
   `KeyLoop` at `KeyInterval` ms as two independent timers, each with its own editable field in the
   GUI. The clan understands that shape. Match it rather than inventing one.
2. **A macro step.** Schema v3 → v4 gains a repeat event — what to send, interval, jitter, stop
   condition. The recorder writes ONE step and the player expands it at playback.

**Do not record the repeated clicks literally.** At the 7ms interval that script defaults to,
thirty seconds of autoclicking is roughly 4,300 events, in a file that otherwise holds one event
per real action. It would be unreadable and un-tunable — changing the rate would mean recording
the whole macro again. A single step holding a number is the entire point: the interval stays
editable afterwards, which is what Este meant by wanting the macro aware of the timing.

**Do not arm it with a bare key or a bare mouse button.** The first sketch was double-clicking the
target button itself to start and stop. That is the v0.3.1 bug again — a bare-Esc global hotkey
that hijacked Esc system-wide — except the blast radius is worse, because left click is the most
load-bearing input on the machine, and the recorder would have to swallow two real clicks the game
was going to act on. Use a chord. Ctrl+Shift+C is free (R, P, L and A are taken) and
`HotkeyService.ChordHotkeyVkCodes` already exists so the recorder skips chords instead of
capturing them.

**The injected-input filter is a prerequisite, and it is nearly free.** Without it the repeater's
own output feeds straight back into the recorder and every synthetic click lands in the recording.
`MacroRecorder` already marshals `flags` out of `KBDLLHOOKSTRUCT` and `MSLLHOOKSTRUCT` (lines 329
and 341) and never reads it — `LLKHF_INJECTED` (0x10) and `LLMHF_INJECTED` (0x01) are sitting
there for the taking.

Take the stronger form as well: stamp our own `SendInput` with a magic `dwExtraInfo` and match on
that. The OS flag only says "some software sent this"; the stamp says "*we* sent this", which is
the distinction that matters once another 626 Labs plugin is injecting into the same session.
`MacroPlayer` sets no `dwExtraInfo` today. Neither mechanism is a security boundary — a kernel
injector is not flagged — which is irrelevant for this use.

That filter is worth doing on its own merits regardless of whether the autoclicker gets built:
today any other tool's injected input lands in a recording as though the user had typed it.

## Expose and drag a macro's click points

**Raised by Este:** 2026-09-21, after using it in the converted clan AutoHotkey script
**Kind:** enhancement, not a fix

One button exposes every click point as a named tag over the real window, and dragging a tag
re-aims that point. Este's words: "the click to expose and drag to move is helpful."

What it would give Ur Task:

- See what a mouse macro will click before running it.
- Fix one click when the game moves one button, instead of re-recording the whole macro.
- Let someone who received a shared macro fit it to their PC without touching the shared copy.

Rules the AutoHotkey version already proved, worth keeping as they are:

- The tag sits beside its point. The exact spot is the pixel just outside the tag's marked corner,
  so a tag never covers what it points at and never pollutes a pixel read.
- The original is always shown and always restorable. A grey marker sits on the original spot of
  anything that was moved.
- Adjustments are stored apart from the macro and per display scale. The shared macro stays clean,
  and the adjustments are a small thing a user can send back.
- Tags are refused during playback, because a tag on a click point swallows the click.

Open design question: a recorded click is a down and up pair plus the mouse moves that lead into
it, so moving a click has to carry those moves with it. Recorded macros also have no names, so a
tag would show a number and a timestamp, with an optional label the user types.

Ur OCR's version of the same idea: expose every trigger's region as a labelled box over the alt
window, drag to move or resize, and show the live read, colour or text, while dragging.

Reference implementation: the "Click points" section of the converted script. Write-up:
`docs/guides/ahk-any-screen-clicks.md`, section "When the math is not enough".

## Shared mouse macros ignore the Windows display-scale setting

**Found:** converting a clan AutoHotkey macro, 2026-09-21 · **Severity:** silent mis-click on
laptops at 125% or 150%, which is most of them

Window-relative macros reproduce the recorded **physical** client size. Roblox lays its UI out in
logical units, physical divided by display scale, so a macro recorded at 100% and played at 125%
gets the right window and the wrong layout, with no warning. Both dev-PC monitors are at 100%, so
it never showed up here. The Surface at 125% is the rig that does show it.

Two related facts came out of the same work. Roblox's minimum window is 816x638 times the display
scale, which is an 800x599 client at 100%, and recordings at exactly that size are common in the
log. At 125% that recording cannot be reached, and `EnsureClientSize` refuses with advice about a
"larger screen" that is wrong for this cause.

Full write-up, with measurements, ranked proposals, and the same analysis for Ur OCR:
`docs/display-scale-findings.md`. First step is small: record the display scale with the macro.

## Ur Task renders strangely under the flatline theme (v0.7.0)

**Found:** v1.20 host walk, 2026-08-11 · **Severity:** cosmetic, but it is live
**Only flatline.** Every other theme renders correctly.

v0.7.0 deleted the plugin's mirrored copy of the host palettes and took the feed instead (#31), so
this is almost certainly in that path. Flatline is a **built-in** host theme, not a user theme —
worth stating because the obvious first guess (the user-theme path through the feed) is wrong.

One lead for whoever picks it up: the dev machine also carries a leftover `flatline.json` **user
theme file** in `%LOCALAPPDATA%\ROROROblox\themes\` from the glow campaign, and `ThemeStore` drops
user themes whose id collides with a built-in. If the feed and the picker disagree about which
flatline is in play, that collision is where to look first.

Shipped in v0.7.0 and reaching users now. Not urgent — it is a look, not a failure — but it is the
newest built-in, so it is the one people will try.

## A "hold" for macro playback, instead of abort-only

**Raised by Este:** v1.20 walk, 2026-08-11

Cancel mid-flight works: focus returns, no stale assignment. But it is all-or-nothing — the run is
abandoned. As soon as the plugin sees Ctrl+Shift it should **pause before the next action** and be
resumable, rather than throwing the run away.

Este's words: "We will make a beautiful hold feature together." Not a bug; a better shape for the
same surface.

## Keep-alive requires the assignment loop (scoped)

Fully scoped in `docs/superpowers/specs/2026-08-11-autonomous-keep-alive-scope.md`.

Since the walk, one requirement changed: build it to work **alongside** Ur AFK, not instead of it.
Ur AFK stays as the last-resort net for users who will not configure anything (24 installs as of
2026-08-11). That makes it a layered fallback:

1. Ur Task, loop running — macros on full cadence
2. Ur Task, loop stopped, autonomous on — keep-alive only, still claimed
3. Ur AFK — anything Ur Task has not claimed

**The scope doc still says "instead of" and needs revising to match** before anyone builds from it.
The claim rule is the load-bearing part either way: Ur Task claims what it is actually servicing,
moment to moment, and Ur AFK takes the rest. Otherwise both plugins service the same alt and fight
over the foreground.
