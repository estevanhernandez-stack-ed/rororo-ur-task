# Recipes (position → loop) — Ur Task design

**Date:** 2026-07-07
**Status:** Approved (Este, 2026-07-07)
**Target version:** Ur Task v0.6/0.7 foundations
**Stacks on:** `AssignmentRunner` (round-robin loop/keep-alive), `SequencePlayer` (run-once-per-alt), `MacroStore`, the multi-select `PlaybackTargetPickerViewModel`, game-aware library (`MacroGameFilter`).
**Relationship to `2026-07-06-recipes-macro-slots-design.md`:** that spec is the *sharing* layer (clan parity, fill-in-the-blank slots, OCR end-state checks, v0.7). This spec is the *local runtime primitive* it stands on — authoring and running a position→loop recipe on your own alts, no sharing. The shareable `.rororo-recipe.json` format extends this later; members stay additive.

## Problem

Este's actual daily workflow, verbatim: record a macro that walks one account to the start point → select it for all accounts → let it run until they're all in position → then manually start the looped macro, or set keep-alive.

**The manual hand-off is the friction.** Today *you* are the sequencer — you eyeball "everyone's positioned," then flip to the loop by hand. And you *have* to babysit it, because the current runner has exactly one mode: **loop everything forever** (`AssignmentRunner`). It cannot express "run this step **once per alt** (position), then switch that alt to **loop** or **keep-alive**." Left alone, the position macro just re-walks them every pass.

## The shape

A **recipe** is an ordered list of **steps** run against a selected **alt set**:

- **Step** = `{ macro, iteration }` where `iteration ∈ RunOnce | Loop | KeepAlive`.
- **Canonical recipe:** `[ position: RunOnce, action: Loop | KeepAlive ]`. Generalizes to N `RunOnce` steps (position → sub-position → …) followed by exactly one **terminal** `Loop`/`KeepAlive` step (the sustained state).
- Invariant: the terminal step is `Loop` or `KeepAlive`; every earlier step is `RunOnce`. A recipe with no terminal step defaults its tail to `KeepAlive` (stay warm).

## Execution model — a thin orchestrator over two existing runners

The unlock: the two step types already exist as tested runners.

- **`RunOnce` step → `SequencePlayer.PlayAsync(macro, alts)`.** Plays the macro once per alt in order, returns when all are done. That completion **is the barrier** — the recipe cannot advance until every alt has finished the step.
- **`Loop` / `KeepAlive` terminal step → `AssignmentRunner.RunAsync(assignments)`.** Round-robins forever: assignment macro = loop, assignment macro `null` = keep-alive Space.

**`RecipeRunner`** (new, thin) sequences them:

```text
foreach RunOnce step in order:
    await SequencePlayer.PlayAsync(step.macro, aliveSelectedAlts)   // barrier
start AssignmentRunner.RunAsync( aliveSelectedAlts × terminalStep ) // sustained
```

Auto-handoff falls out for free: `SequencePlayer` returns only when all alts are positioned, and *that return* starts the loop. **Barrier is the default and the natural behavior** — no extra machinery. (Per-alt handoff — each alt loops the instant its own position finishes — is a future toggle, out of scope for v1. Este's workflow is barrier.)

## Loop concurrency character (Este's insight) — v1 scope

Loops have a concurrency character, and it bounds what "loop all alts" can mean:

- **Round-robin-able loop** — discrete, yields between passes (click the anvil, cast the line). `AssignmentRunner` time-slices it across all alts. Everyone genuinely loops. **✓ v1.**
- **Exclusive / active loop** — continuous, monopolizes one window's foreground + input (a real-time minigame). Physically one alt at a time; the rest sit at spawn. **Out of v1 as a fan-out** — you can't round-robin a loop that can't yield.

**v1 answer for the exclusive case:** position all (barrier), then the terminal step is `KeepAlive` for the whole squad — they hold warm while you drive the one active window yourself. The recipe got everyone safe and parked; the active loop is manual. This matches Este's "the others are just sitting at spawn." A recipe that *drives* an exclusive loop is future work (and edges toward Ur Reset territory).

## Selection (multi-account + select-all / select-none)

The recipe runs against a selected alt set. Selection already half-exists: `PlaybackTargetPickerViewModel` does click-order multi-select with `SelectedTargets`. This spec adds:

- **Select all** — add every live alt to the selection.
- **Select none** — clear it.
- Surfaced as two buttons on the selection surface. Extend the picker VM (`SelectAll()`, `SelectNone()`); the ordered-selection semantics are unchanged.

## Authoring UI (isolated surface — per the family-architecture decision)

Recipes live *in* Ur Task (authoring a macro pipeline is Ur Task's domain) but on **their own surface**, not bolted onto the dashboard — that was the explicit answer to the crowding worry.

- New entry point: **`New recipe` / Build** button → opens a recipe editor window.
- Editor: name; pick the alt set (with select-all/none); add ordered steps (choose a macro from the library, choose iteration `RunOnce`/`Loop`/`KeepAlive`); the terminal step must be `Loop` or `KeepAlive`.
- Game-aware: reuse the existing per-row game badge + soft mismatch warning from `AssignmentRow`/`MacroGameFilter` on each step.
- Run button executes the recipe via `RecipeRunner`; Abort stops the active runner.

## Persistence

Mirror `MacroStore`: one file per recipe at `%LOCALAPPDATA%\626Labs\RoRoRoUrTask\recipes\<id>.json`, `LoadAll`/`Save`. A recipe references macros **by id** (macros stay owned by `MacroStore`; recipes don't embed them). Envelope carries a `recipeVersion` for forward-compat with the shareable format later.

## Progress / feedback

Reuse the runners' existing events: `SequenceProgress` drives "Positioning 3/5…", `AssignmentProgress` drives "Looping (cycle N)" / "Keep-alive." The recipe surface shows which phase/step is live and a per-alt roll-up (positioned / looping / failed).

## Error handling

- **Position failure:** `SequencePlayer` already does skip-on-failure (focus fails → `Refused`, continue). v1 policy: **proceed to the loop with the alts that positioned**, surface the failures — don't block the whole squad on one stuck alt. (Alternative: hold-for-all. Flagged as an open question.)
- **Foreground-lock — HARD DEPENDENCY.** ur-task's `Win32Focus.AttachAndFocus` does **not** carry the foreground-lock fix that ur-afk needed (verified: no `SPI_SETFOREGROUNDLOCKTIMEOUT` / `BringWindowToTop` / `SW_RESTORE`). Recipes lean hard on focus succeeding while the user watches alts position (often idle). Port the ur-afk **v0.5.2** fix into ur-task's `Win32Focus` as part of this work, or the position step silently `Refused`s exactly when the user steps away. This is a prerequisite, not a nice-to-have.
- **Abort:** `RecipeRunner.Abort()` cancels whichever runner is active (`SequencePlayer` mid-position or `AssignmentRunner` mid-loop).

## Testing

- `RecipeRunner` orchestration (fake runners): `RunOnce` step barriers fully before the terminal step starts; terminal `Loop` starts the round-robin; terminal `KeepAlive` sends Space.
- Step model: terminal must be `Loop`/`KeepAlive`; a recipe with no terminal tail defaults to `KeepAlive`.
- Selection: `SelectAll` adds all live alts; `SelectNone` clears.
- Position-failure: one alt fails to focus → recipe proceeds with the rest, failure surfaced.
- Reuse the existing `SequencePlayer` / `AssignmentRunner` test suites — the runners themselves are already covered; only the thin orchestration is new.

## Out of scope (v1)

- Exclusive/active loop **fan-out** across alts (physics — one foreground).
- Per-alt handoff mode (v1 = barrier only).
- Recipe **sharing** / slots / OCR end-state checks (the `2026-07-06` macro-slots spec — future).
- **Ur Reset** (separate orchestrator plugin — future; consumes these recipes as its recovery routine).

## Decisions (resolved with Este, 2026-07-07)

1. **Position-failure policy:** proceed-with-successes — the recipe advances to the loop with whoever positioned; failures are surfaced, the squad is never blocked on one stuck alt.
2. **Persistence home:** a new `RecipeStore` sibling to `MacroStore` (`%LOCALAPPDATA%\626Labs\RoRoRoUrTask\recipes\<id>.json`); macros referenced by id, not embedded.
3. **Terminology:** one word "recipe", two layers — this spec is the v1 core/runtime; the `2026-07-06` macro-slots spec is the v2 sharing layer on top.
