# Recipes with macro slots — design

**Date:** 2026-07-06
**Status:** Approved direction (Este, this session) — spec banked for a build session
**Target version:** Ur Task v0.7 (+ Ur-OCR sibling work, + possible PluginContract bump for bridge config-provisioning)
**Stacks on:** `2026-07-03-game-aware-macro-library.md` (contract 0.4.0 game stamps, bundle schema v3) and the v0.3 action bridge
**Goal, in the author's words:** *"Help people make full macros faster and ship those to clan members so that everyone has parity instantly. Juice them all."*

## Problem

Macro bundles ship finished macros — but a working automation is a *pipeline*:
OCR regions + triggers + macros + routing between them. None of that wiring is
shippable today, so composition knowledge lives only in the author's head, and
the plugins stay over the clan's heads (single-digit installs against a
~7x battle-week usage spike on the host).

Worse, the one thing a pipeline can't ship is the user's own context. The
author's loop macro assumes alts standing at the anvil; the importer's alts are
at spawn, at their progress level. A shared bundle that "just works" is
impossible unless the recipe can *ask the user to record the user-specific
parts* — and verify they did it right.

## The shape

A **recipe** is a template with declared holes:

- **Provided macros** — the portable ones the author recorded (PerWindow /
  client coord-space, position-independent loops). Ship in the file.
- **Required macro slots** — what the recipe cannot ship. Each slot declares:
  - `name` + plain-language `brief` ("record a macro that walks one account
    from spawn to the mine entrance")
  - `fillMode`: `shared` (one recording, played round-robin) or `perAlt`
    (wizard prompts per account)
  - `endStateCheck` (optional but the killer feature): an Ur-OCR region +
    trigger definition the author authored. The importer's recording is graded
    against it — screen matches, slot turns green; doesn't, "re-record."
- **OCR definitions** — regions + triggers the pipeline needs, shipped whole.
- **Wiring** — trigger → macro routing (which trigger fires which macro on
  which alts), inter-macro sequencing (run slot A once per alt, then loop
  provided macro B).
- **Metadata** — game stamp (from the v0.6 game-aware fields), author, recipe
  version, min plugin versions.

## Import wizard (Ur Task)

1. Open recipe → checklist view: provided items ✓, required slots ○.
2. Per slot: brief on screen, **Record** button opens the existing recorder
   pre-named for the slot. `perAlt` slots iterate the account list.
3. On stop: if the slot has an `endStateCheck`, probe via Ur-OCR — pass turns
   the slot green, fail explains and offers re-record. (Reuses the a2-probe
   path from `docs/guides/ur-ocr-a2-probe.md`.)
4. All slots green → recipe activates: OCR defs provisioned, wiring live.
5. Runtime: when an `endStateCheck` stops passing (game patch moved the
   world), pause the recipe, flag ONLY that slot for re-record, notify. The
   loop logic survives; maintenance shrinks to the user-specific parts.

## Cross-plugin mechanics (the open design question)

Ur Task owns the recipe file and the wizard. The OCR definitions must land in
Ur-OCR. Options for a build session to settle:

- **A. Bridge config-provisioning** — extend the action bridge so Ur Task can
  push region/trigger sets to Ur-OCR. Needs a consent story: "Ur Task wants to
  configure Ur OCR" is a new capability grant, likely a PluginContract bump.
- **B. Shared drop-folder contract** — Ur Task writes an OCR-def file to a
  well-known per-user path; Ur-OCR watches and imports with its own in-app
  confirm. No contract change, weaker UX, simpler consent (Ur-OCR asks).

Lean A for UX, B if the consent surface for plugin-configures-plugin gets
hairy. Either way the recipe file format is identical — mechanics are
swappable later.

## Compatibility stance

Same philosophy that kept bundles cross-version: additive members, readers
ignore unknown fields. A recipe is a NEW file kind (`.rororo-recipe.json`,
envelope `recipeVersion: 1`) rather than bundle v2 — old Ur Task builds should
fail clean ("update to import recipes"), not half-import a pipeline as a bag
of macros.

## Distribution

Discord-first (the clan channel is the marketplace that already works — drop
the file, six imports by morning). Later, without new invention: recipes list
on the hub's rororo-plugins page via the same nightly-data pattern shipped
2026-07-06 (a `recipes/` dir in this repo + a hub bot that indexes it).

## Out of scope

- A rules-engine plugin ("Ur Chain") — decided against this session; recipes
  + slots inside Ur Task cover the composition need. Revisit only when recipes
  outgrow what two plugins can express.
- Recipe monetization, cross-game auto-generalization, mid-session game-change
  awareness (tracked in the game-aware spec's out-of-scope).

## Sequencing

After the game-aware v0.6 chain ships (contract 0.4.0 → stamps → library UI).
Recipes want the game stamp on day one so a recipe declares what game it's
for and the library files it correctly.
