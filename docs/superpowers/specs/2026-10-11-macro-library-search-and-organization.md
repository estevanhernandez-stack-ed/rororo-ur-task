# Macro library: search, grouping, and a cleanup of the 100 macros (draft for Este)

Status: draft, 2026-10-11. Nothing built. Decisions A-D at the bottom are Este's.

## The problem

The library is one flat list of 100 macros, sorted by game band and then newest first. PLAYING NOW is the only filter
(`MacroGameFilter`, `RecorderWindow.xaml:329`). Finding a macro means scrolling, and every new recording or
agent-written step pushes the working ones further down.

Use is concentrated. Agent plays since 10-07, from the bridge log lines:

| Family | Plays |
|---|---:|
| `PLZ \| booth 1/3`, `2/3`, `3/3`, close picker | ~550 / 553 / 347 / 205 |
| self-trade chain (list open, row, ready, yes, ok) | 86-130 each |
| `ANY \| jump (keep alive)` | 63 |
| `Camera top-down + zoom out all the way` | 27 |
| `trader 2b` (Pro spawn -> level 2 booth) | 23 |
| everything else | 0-10 |

Caveat: the log only names bridge (agent) plays and only goes back to 10-07. Este's hotkey and button plays aren't
counted, so a 0 means "the agent didn't use it", not "nobody did".

## Part 1: what Ur Task gets

### 1. Search box

- Above the list, filtering live.
- Case-insensitive. Every typed word has to match, in any order, so `trade ok` finds `self-trade ok`.
- It matches the name, the recorded game name and the `[account]` tag.
- Stacks with PLAYING NOW.
- Ctrl+F focuses the box, and Esc inside the box clears it. Never register a bare Esc globally: that was the v0.3.1
  bug.
- Pure static filter next to `MacroGameFilter`, with unit tests. The view model only binds to it.

### 2. Groups built from the names we already write

The naming convention in `ps99-ops/macros/NAMING.md` is `<WHERE> | <ROUTE> n/N | <what> [account] (notes)`. That
already is the structure, so the list can show it without any new data:

- **Group header = WHERE** (`PLZ`, `HW Z4`, `ANY`, ...), with a count. Click to collapse.
- **Inside a group, route steps sort by ROUTE, then n.** So `booth 1/3, 2/3, 3/3` read top to bottom in run order.
  Standalone actions come after them.
- **Names without a ` | ` go into an "Ungrouped" group** at the bottom. That makes renaming visibly pay off.
- Collapse state is saved in `ui-prefs.json`.
- Parsing is a pure function with tests (`MacroName.Parse` -> where, route, step, of, what, account).
- **No schema change.** Old files, shared bundles and the MCP keep working as they are.

### 3. Archive instead of delete

- An archived macro moves to `macros\archive\`. `MacroStore` keeps loading only the top level.
- So the library, the bridge and the MCP's `list_macros` all stop showing it, and the agent stops wading through
  dead routes too.
- "Show archived" lists them greyed out, with Restore (moves the file back) next to each.
- The context menu gets Archive next to Rename and Delete.
- The alternative is an `Archived` flag in the macro file. That's worse here: every reader has to learn to skip it,
  and the agent would still see archived ones in `list_macros`.

### 4. Later, if wanted

- `list_macros` takes an optional `query` and returns the group. That's a bridge contract change plus
  `rororo-ur-mcp/Tools/MacroTools.cs`, so it ships as its own release.
- Pinned macros at the top. Probably not needed once groups and search exist.

## Part 2: cleanup of the existing macros (data only, no code)

The per-macro table is in the appendix. In summary:

| Action | Count | What |
|---|---:|---|
| keep | 10 | booth chain, `ANY \| jump`, camera reset, `use item on repeat`, in-plaza re-teleport |
| rename | 23 | to the convention: self-trade -> `ANY \| trade n/8`, `trader 2b/2c/3` -> `PLZ \| trader 2-4/4`, camera and booth step macros get prefixes |
| merge | 24 | the 12 `pace` and 12 `step (top-down)` macros press the same keys. Keep one `ANY \| move <key> <ms>` family (W/A/S/D x 250/500/1000, plus the diagonals that are in use) |
| archive | 40 | Hatch Wars week-1 routes (week 2 has new zones), back-lane booth hunts (found unreliable 10-08), early experiments, `trader 2` (superseded) |
| re-record | 1 | `PLZ \| trader 1/4 \| world -> Pro plaza`: Update 97 moved the teleport button to the robot icon (100,118) |
| ask | 1 | `Crystal` (today's crystal-key macro): give it a WHERE |

The result is about 40 visible macros instead of 100, nearly all of them in named groups.

**How it's done:**
- Rename by editing the name in the JSON (ids stay the same, so recipes, assignments and agent tools keep working),
  then restart Ur Task.
- Archive by moving the file once the archive folder exists. Until then, move it to `ps99-ops/macros/archive/` as a
  backup.
- The ps99-ops tools that write macros (`booth-list.py`, `self-trade.py`, `trade-run.py`) set names in code, so the
  renames have to go there too, or the next run writes the old names back.

## Decided (Este, 2026-10-11 10:12)

- **A:** groups come from parsing the names. **B:** archive is a folder on disk.
- **C:** the cleanup is approved as proposed, keeping no week-1 routes. Done at 10:13:
  - 52 macros archived and 37 renamed, 47 live.
  - Backup in `ps99-ops/macros/snapshots/20261011-1013`.
  - The "step" diagonals went to the archive with the rest of the step family.
  - `Crystal` is untouched (Este's running macro).
- **D:** do it now. Plugin search and groups, plus `list_macros` search in rororo-ur-mcp.
  - The MCP filters and groups on its own side, parsing names the same way as the plugin.
  - So there's no bridge contract change, and old hosts keep working.

## Decisions for Este (as asked)

- **A. Where groups come from:** parse the existing name convention (recommended: zero migration), or a new explicit
  Folder field you set per macro.
- **B. Archive:** archive folder on disk (recommended), or a flag in the file.
- **C. Cleanup:** approve the appendix as is, or mark changes. The archive list in particular: any week-1 Hatch Wars
  route you want to keep for a re-run?
- **D. MCP search:** in this release, or later.

## Order of work

1. Cleanup first, on data alone. It's useful with no code shipped. Back up the folder, rename and archive, update the
   tool names in ps99-ops.
2. Ur Task: name parser and search filter with tests, then grouped list UI, then the archive folder and Restore.
3. Optional: the MCP `query` parameter.

## Appendix: every macro, with a proposed action

| Macro | Agent plays since 10-07 | Proposed | Note |
|---|---:|---|---|
| HW ANY \| pumpkin route \| any zone (not Z1) -> EDGE of the Giant Pumpkin ring (Este walk 10-07 + 2x S 200, measured) | 0 | archive | Hatch Wars week-1 routes; week 2 has new zones |
| HW Z1 \| pumpkin 1/3 \| teleport to Z1 (#1 Ember Cliffs) | 0 | archive | Hatch Wars week-1 routes; week 2 has new zones |
| HW Z1 \| pumpkin 2/3 \| walk back onto the pumpkin ring (S 2250) (from Z1 teleport arrival) | 0 | archive | Hatch Wars week-1 routes; week 2 has new zones |
| HW Z1 \| pumpkin 3/3 \| open if full, then feed (repeat; Active on main) (start on the pumpkin dialog) | 0 | archive | Hatch Wars week-1 routes; week 2 has new zones |
| HW Z4 \| egg 1/6 \| Hatch Wars button (world -> event spawn) | 1 | archive | Hatch Wars week-1 routes; week 2 has new zones |
| HW Z4 \| egg 2/6 + 4/6 \| teleport to Z4 + camera top-down (DIRECT) | 2 | archive | Hatch Wars week-1 routes; week 2 has new zones |
| HW Z4 \| egg 2/6 \| teleport via #3 to Z4 + camera (use when already in Z4) | 0 | archive | Hatch Wars week-1 routes; week 2 has new zones |
| HW Z4 \| egg 3/6 \| fight only, tap 10s [main] (standing on pad) | 1 | archive | Hatch Wars week-1 routes; week 2 has new zones |
| HW Z4 \| egg 3/6 \| fight only, tap ~11s [CECPapa] (standing on pad, E at 350,270) | 0 | archive | Hatch Wars week-1 routes; week 2 has new zones |
| HW Z4 \| egg 3/6 \| walk to boss pad + fight, tap ~11s [CECPapa] (from button arrival) | 0 | archive | Hatch Wars week-1 routes; week 2 has new zones |
| HW Z4 \| egg 3/6 \| walk to boss pad + fight, tap ~11s [main] (from button arrival) | 0 | archive | Hatch Wars week-1 routes; week 2 has new zones |
| HW Z4 \| egg 5/6 \| Auto Farm lock (W 1100, click, S 1100) (from squared arrival) | 1 | archive | Hatch Wars week-1 routes; week 2 has new zones |
| HW Z4 \| egg 6/6 \| nudge W 300, A 100 [main] (after walk to egg) | 0 | archive | Hatch Wars week-1 routes; week 2 has new zones |
| HW Z4 \| egg 6/6 \| walk to egg [CECPapa] (from squared arrival; Este walk 10-06) | 0 | archive | Hatch Wars week-1 routes; week 2 has new zones |
| HW Z4 \| egg 6/6 \| walk to egg [main] (from squared arrival) | 1 | archive | Hatch Wars week-1 routes; week 2 has new zones |
| HW Z4 \| quick hatch \| E + click Buy 144, ~4/s (Este's hatchfacs, raw replay) [main] (lined up at the egg) | 0 | archive | Hatch Wars week-1 routes; week 2 has new zones |
| HW Z4 \| quick hatch \| E + click every 100 ms, 15 s pass [main] (lined up at the egg) | 0 | archive | Hatch Wars week-1 routes; week 2 has new zones |
| HW Z4 \| quick hatch \| E + click every 60 ms, 15 s pass [main] (lined up at the egg) | 0 | archive | Hatch Wars week-1 routes; week 2 has new zones |
| HW \| autoclick Buy 72 spot (400,430) every 100 ms (repeat) [centred on egg, E under Buy 72] | 1 | archive | Hatch Wars week-1 routes; week 2 has new zones |
| HW \| buy 89 at egg (E, Buy 89) | 0 | archive | Hatch Wars week-1 routes; week 2 has new zones |
| HW \| buy 89 at egg (close Hatch Settings, E, Buy 89) | 0 | archive | Hatch Wars week-1 routes; week 2 has new zones |
| PLZ \| back lane 1/2 \| Pro spawn -> back-wall corner (Este walk 10-08) (after Camera top-down + zoom out) | 2 | archive | back-lane booth hunt found unreliable 10-08 |
| PLZ \| back lane 2/2 \| back-wall corner -> END of back lane (W 13s, D tap) (Este walk 10-08) | 1 | archive | back-lane booth hunt found unreliable 10-08 |
| PLZ \| waterfall corner \| Pro spawn -> back-lane waterfall corner (Este walk v2 10-08 + W+D pin) (after world->Pro + Camera top-down) | 1 | archive | back-lane booth hunt found unreliable 10-08 |
| R surge tapper (stand in zone, hatching) | 0 | archive | early experiments, superseded |
| Walk forward 1.5 s | 0 | archive | early experiments, superseded |
| egg nudge: A 250 then W 150 (zone 1) | 0 | archive | Hatch Wars week-1 routes; week 2 has new zones |
| event zone 1: in farm -> egg (Este walk, z1 to egg) | 0 | archive | Hatch Wars week-1 routes; week 2 has new zones |
| event zone 1: world -> button -> top-down -> Auto Farm lock | 0 | archive | Hatch Wars week-1 routes; week 2 has new zones |
| event zone 4: arrival -> Auto Farm -> egg (Este CECZ4 + spliced click) | 0 | archive | Hatch Wars week-1 routes; week 2 has new zones |
| event zone 4: arrival -> egg (Este walk, CECZ4) | 0 | archive | Hatch Wars week-1 routes; week 2 has new zones |
| hatch pause: egg -> side of zone (D1000, top-down) | 0 | archive | Hatch Wars week-1 routes; week 2 has new zones |
| hatch resume walk: back to egg (A1000, top-down) | 0 | archive | Hatch Wars week-1 routes; week 2 has new zones |
| hatch wars egg start (Witching, Auto Hatch, Buy 89) | 0 | archive | Hatch Wars week-1 routes; week 2 has new zones |
| hatch wars zone 4 route (bounce #3, teleport #4, Auto Farm) | 0 | archive | Hatch Wars week-1 routes; week 2 has new zones |
| hatchfacs | 0 | archive | Hatch Wars week-1 routes; week 2 has new zones |
| keep awake (zoom nudge, no walk) | 0 | archive | early experiments, superseded |
| main fix: D D S S S (250ms each, Este) | 0 | archive | Hatch Wars week-1 routes; week 2 has new zones |
| trader 2: Pro spawn -> empty booth (Este walk) | 0 | archive | superseded by trader 2b |
| zone 1: egg -> farm middle (S 600, A 400) | 0 | archive | Hatch Wars week-1 routes; week 2 has new zones |
| Crystal | 0 | ask | new today (Este's crystal keys): name it `<WHERE> | crystal keys ...` |
| ANY \| jump (keep alive) | 63 | keep |  |
| ANY \| use item on repeat (clipboard = item name, inventory open on its tab) | 0 | keep |  |
| Camera top-down + zoom out all the way | 27 | keep |  |
| PLZ \| booth 1/3 \| Make Listing + paste search + pick + Confirm (clipboard = name) | 556 | keep |  |
| PLZ \| booth 1/3 \| Make Listing + paste search + pick + Confirm [egg tab] (clipboard = name) | 0 | keep |  |
| PLZ \| booth 2/3 \| paste price (clipboard = price) | 553 | keep |  |
| PLZ \| booth 3/3 \| Submit + Yes | 347 | keep |  |
| PLZ \| booth close picker (X) | 205 | keep |  |
| PLZ \| booth skin (paintbrush, paste name, pick, Confirm) (clipboard = skin) | 0 | keep |  |
| PLZ \| trader 1/4 \| in plaza -> Pro spawn (re-teleport) | 3 | keep |  |
| pace A 1000ms | 0 | merge | 12 pace macros + 12 step (top-down) macros are the same keys: keep one `ANY | move` family |
| pace A 250ms | 0 | merge | 12 pace macros + 12 step (top-down) macros are the same keys: keep one `ANY | move` family |
| pace A 500ms | 0 | merge | 12 pace macros + 12 step (top-down) macros are the same keys: keep one `ANY | move` family |
| pace D 1000ms | 0 | merge | 12 pace macros + 12 step (top-down) macros are the same keys: keep one `ANY | move` family |
| pace D 250ms | 0 | merge | 12 pace macros + 12 step (top-down) macros are the same keys: keep one `ANY | move` family |
| pace D 500ms | 0 | merge | 12 pace macros + 12 step (top-down) macros are the same keys: keep one `ANY | move` family |
| pace S 1000ms | 0 | merge | 12 pace macros + 12 step (top-down) macros are the same keys: keep one `ANY | move` family |
| pace S 250ms | 0 | merge | 12 pace macros + 12 step (top-down) macros are the same keys: keep one `ANY | move` family |
| pace S 500ms | 0 | merge | 12 pace macros + 12 step (top-down) macros are the same keys: keep one `ANY | move` family |
| pace W 1000ms | 0 | merge | 12 pace macros + 12 step (top-down) macros are the same keys: keep one `ANY | move` family |
| pace W 250ms | 0 | merge | 12 pace macros + 12 step (top-down) macros are the same keys: keep one `ANY | move` family |
| pace W 500ms | 0 | merge | 12 pace macros + 12 step (top-down) macros are the same keys: keep one `ANY | move` family |
| step A 1500ms (top-down) | 0 | merge | fold into `ANY | move` family (same keys as pace) |
| step A 250ms (top-down) | 0 | merge | fold into `ANY | move` family (same keys as pace) |
| step D 250ms (top-down) | 0 | merge | fold into `ANY | move` family (same keys as pace) |
| step S 120ms (top-down) | 0 | merge | fold into `ANY | move` family (same keys as pace) |
| step S 250ms (top-down) | 0 | merge | fold into `ANY | move` family (same keys as pace) |
| step S+A 250ms (top-down, down-left) | 0 | merge | fold into `ANY | move` family (same keys as pace) |
| step W 1000ms (top-down) | 0 | merge | fold into `ANY | move` family (same keys as pace) |
| step W 250ms (top-down) | 0 | merge | fold into `ANY | move` family (same keys as pace) |
| step W+A 600ms (top-down, up-left) | 0 | merge | fold into `ANY | move` family (same keys as pace) |
| step W+A 900ms (top-down, up-left) | 0 | merge | fold into `ANY | move` family (same keys as pace) |
| step W+D 250ms (top-down, up-right) | 0 | merge | fold into `ANY | move` family (same keys as pace) |
| step W+D 750ms (top-down, up-right) | 0 | merge | fold into `ANY | move` family (same keys as pace) |
| PLZ \| trader 1/4 \| world -> Pro plaza (teleport menu) | 10 | re-record | Update 97 moved the left column: teleport is now the robot icon (100,118) |
| Camera tilt to horizon (from top-down) | 0 | rename | `ANY | camera ...` |
| Camera top-down | 0 | rename | `ANY | camera ...` |
| Camera turn right | 0 | rename | `ANY | camera ...` |
| Roblox Reconnect button (agent) | 0 | rename | `ANY | Roblox Reconnect button` |
| Zoom out all the way (run first) | 2 | rename | `ANY | camera ...` |
| booth list pet (fast): select + price | 0 | rename | `PLZ | booth ...` prefix |
| booth step-away (S 400, from E spot) | 1 | rename | `PLZ | booth ...` prefix |
| booth step-back (W 400, reverse of step-away) | 0 | rename | `PLZ | booth ...` prefix |
| camera nudge left 100ms | 0 | rename | `ANY | camera ...` |
| camera nudge right 100ms | 0 | rename | `ANY | camera ...` |
| camera nudge right 200ms | 0 | rename | `ANY | camera ...` |
| camera nudge right 800ms (about 95 deg) | 0 | rename | `ANY | camera ...` |
| self-trade 3 add: pets 'huge dragon' cells  | 0 | rename | `ANY | trade n/8 | ...` (ids 100-107; 102/106/107 rewritten per run) |
| self-trade list open (menu 397,514) | 104 | rename | `ANY | trade n/8 | ...` (ids 100-107; 102/106/107 rewritten per run) |
| self-trade list search: ELeonDog | 8 | rename | `ANY | trade n/8 | ...` (ids 100-107; 102/106/107 rewritten per run) |
| self-trade list search: ItsJustEstePapa | 8 | rename | `ANY | trade n/8 | ...` (ids 100-107; 102/106/107 rewritten per run) |
| self-trade ok | 130 | rename | `ANY | trade n/8 | ...` (ids 100-107; 102/106/107 rewritten per run) |
| self-trade ready / confirm (same spot) | 117 | rename | `ANY | trade n/8 | ...` (ids 100-107; 102/106/107 rewritten per run) |
| self-trade row 1 click (Trade! / Accept!) | 97 | rename | `ANY | trade n/8 | ...` (ids 100-107; 102/106/107 rewritten per run) |
| self-trade yes | 86 | rename | `ANY | trade n/8 | ...` (ids 100-107; 102/106/107 rewritten per run) |
| trader 2b: Pro spawn -> LEVEL 2 booth (Este walk) | 23 | rename | `PLZ | trader 2/4 | Pro spawn -> level 2 booth (Este walk)` |
| trader 2c: level 2 booth -> LEVEL 3 booth (Este walk, 2to3) | 1 | rename | `PLZ | trader 3/4 | level 2 -> level 3 booth` |
| trader 3: claim booth + restore listings (Yes, Ok) | 0 | rename | `PLZ | trader 4/4 | claim booth + restore (Yes, Ok)` |

Totals: archive 40, ask 1, keep 10, merge 24, re-record 1, rename 23
