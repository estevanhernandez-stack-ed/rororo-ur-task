# Game-aware macro library — design

**Date:** 2026-07-03
**Status:** Approved (Este, this session)
**Target version:** ROROROblox.PluginContract 0.4.0 (host half) + Ur Task v0.6 (plugin half)
**Repos:** ROROROblox + rororo-ur-task
**Build timing:** after the v0.5 chain (#15 → #16 → #18) merges — this stacks on the two-pane MACROS pane and the bundle schema.

## Problem

Macros are game-blind. RoRoRo knows which game it launches each alt into, but
the plugin contract doesn't expose it — `AccountLaunchedEvent` / `RunningAccount`
carry account/user/pid only, and `AccountActivity` (0.3.0) is input-idle
telemetry, not game identity. So the library is one flat list: a user playing
game X scrolls past macros recorded for games Y and Z, and a shared bundle says
nothing about what game its macros are for.

Stamp the game at record time, surface it in the library, and the right macros
float to the user depending on what they're playing.

## Approaches considered

- **A. Contract 0.4.0 — additive game fields on account surfaces.** The host
  already knows the launch target per alt; expose `place_id`/`place_name` on
  `AccountLaunchedEvent` + `RunningAccount`. One small plumb, every plugin in
  the family benefits (Ur-OCR per-game trigger regions is the obvious sibling
  win). **Chosen.**
- **B. Plugin-side detection** (window title / Roblox log scraping) — Roblox
  window titles are generic, log scraping is fragile and would be duplicated
  per plugin. Rejected.
- **C. Manual per-macro game tag** — user-typed names have no source of truth
  and add toil to every recording. Rejected; survives only as the per-macro
  "All games" toggle.

## Decisions (made with Este)

| Decision | Choice | Rationale |
|---|---|---|
| Game identity source | **Contract 0.4.0, additive** | place_id/place_name on `AccountLaunchedEvent` + `RunningAccount`; old plugins ignore unknown proto fields, so it's a safe minor bump. |
| Non-matching macros in the library | **Badge + sort first** | Everything stays visible; current-game (and all-games) macros float to the top with a game badge. A filter chip hard-hides on demand. Nothing ever looks deleted. |
| Assignment mismatch (macro game ≠ alt game) | **Soft warn only** | Mismatch badge + activity-log note; never blocks. Same "reference only, not enforced" stance as `RecordedAgainstUserId`. |
| Legacy / untagged macros | **null game = all games** | Every existing macro keeps working with zero migration pain. |
| Schema version | **Stays v3, nullable additive fields** | `JsonIgnore(WhenWritingNull)` house style. Critically: v0.5 readers ignore unknown JSON members, so v0.6-exported bundles still import on v0.5 — sharing stays cross-version in both directions. A v4 bump would break that for no gain. |

## Design

### 1. Contract 0.4.0 (ROROROblox repo)

- `AccountLaunchedEvent` gains `int64 place_id = 6; string place_name = 7;`
- `RunningAccount` gains `int64 place_id = 5; string place_name = 6;`
- Host plumbs both from its launch-target knowledge (the game it launched the
  alt into). Empty/0 when unknown — plugins must treat that as "no game info."
- Publish 0.4.0 through the existing Trusted Publishing flow.
- **Out of scope:** mid-session game changes (follow-friend, teleports). If it
  earns its keep later, that's a `SubscribeAccountGameChanged` stream in a
  future bump — launch-time identity is 95% of the organizing value today.

### 2. Macro schema (Ur Task, stays v3)

`Macro` gains three nullable/defaulted fields, all `WhenWritingNull`:

- `RecordedPlaceId` (`long?`) — 0/null = unknown.
- `RecordedGameName` (`string?`) — display string, denormalized at record time.
- `AllGames` (`bool`, default `false`) — user's explicit "this macro is for
  everything" toggle. A macro behaves as all-games when `AllGames == true` OR
  `RecordedPlaceId` is null (legacy).

### 3. Recording stamp

- `AccountRegistry.AccountInfo` gains `PlaceId`/`PlaceName`, filled from the
  enriched launch events and the `GetRunningAccounts` snapshot.
- At record start, stamp the macro from the recording-bound account — the same
  capture point as `RecordedAgainstUserId`. AllWindows-mode recordings stamp
  from the foreground alt at start (soft metadata; close enough).

### 4. Library UX (rides the v0.5 two-pane MACROS pane)

- **Game badge** on the macro card (mono font, muted) when `RecordedGameName`
  is present; all-games macros carry no badge — absence reads as "anywhere."
- **Sort:** macros matching the set of games currently running across alts
  first, then recency (current behavior) within groups.
- **Filter chip** in the MACROS pane header — "PLAYING NOW" toggle that
  hard-hides non-matching game-scoped macros (all-games macros always shown).
  Default off, not persisted in v1.
- **⋯ menu:** "Allow in all games" checkable item → flips `AllGames`, saves.

### 5. Assignments soft guard

- Assignment row shows a small "≠ GAME" badge (tooltip: "recorded for
  {RecordedGameName}; {alt} is in {PlaceName}") when a game-scoped macro is
  paired with an alt in a different game.
- One activity-log summary line when PLAY ASSIGNMENTS starts with mismatches
  present. Playback is never blocked.

### 6. Bundles (free ride)

Export/import carry the new fields automatically via schema serialization.
Import log lines mention the game name when present ("Imported 'farm loop'
(Pet Simulator 99)"). Cross-version sharing holds both ways per the schema
decision above.

## Out of scope (v1)

- Mid-session game-change tracking (contract stream addition later).
- Universe-level grouping — `place_id` is what the host has; multi-place games
  show per-place until the host learns universe ids.
- Ur-OCR per-game trigger scoping — sibling feature on the same contract
  fields; gets its own spec in that repo when its turn comes.

## Test plan sketch

- Schema: round-trip with/without game fields; assert a v0.5-shaped reader
  (deserialize into the v0.5 record shape) tolerates v0.6 JSON.
- Registry: enrichment from launch events + snapshot; empty place_id treated
  as no-game.
- Pure logic: current-games-first sort, PLAYING NOW filter (all-games always
  visible), mismatch detection for the assignment badge.
- Manual: record in two different games, verify badges/sort/filter; export →
  import on a v0.5 build.
