# Timing-aware macro runs — per-account cadence scheduler

**Date:** 2026-07-12
**Issue:** [ur-task#29](https://github.com/estevanhernandez-stack-ed/rororo-ur-task/issues/29)
**Status:** Design approved, ready for implementation plan
**Baseline:** v0.6.0 (merge 68af186)

## Problem

`AssignmentRunner` is an unconditional spin loop. For each assignment it steals foreground,
waits a 1000ms settle, plays the macro (or sends a Space keep-alive), waits 200ms, and moves
to the next alt — then restarts the cycle with **zero inter-cycle delay**.

For a keep-alive assignment (no macro) that means a **service roughly every 1.25 seconds**,
forever, each one stealing the user's foreground.

The game needs a keystroke roughly **every 10–12 minutes** (see Thresholds below). So the
runner services keep-alive accounts **~500–600× more often than the game requires**, and every
one of those is a focus steal. Run a single account on keep-alive and your desktop is hijacked
every 1.2 seconds.

This is not a tuning problem. The runner has no concept of *when an alt is next due*. The fix
is to replace the spin loop with a **deadline scheduler**.

## Goals

- A keep-alive alt is serviced only when its idle deadline actually approaches.
- With no active alts and nothing due, the scheduler **sleeps** — zero focus steals.
- An active (farming) alt keeps running its macro back-to-back, as it does today.
- Keep-alive fires are fitted into the **gaps between active macro passes**, computed from
  macro length — not guessed.
- If an alt *cannot* be kept alive (its threshold is shorter than an active pass), say so up
  front rather than letting it get kicked silently.
- Ur Task owns cadence for the accounts it manages; ur-afk stays the fallback and stays off
  those accounts.

## Non-goals

- Changing recipe/loadout semantics. `RecipeRunner` composes over `AssignmentRunner.RunAsync`
  for its terminal loop and must keep working unchanged.
- Interrupting a macro mid-pass. A macro pass is atomic; keep-alives are fitted *between*
  passes, never by aborting one (aborting mid-farm corrupts game state).
- Learning/measuring idle thresholds at runtime. We ship a table and let the user override.
- The ur-afk side of the claim protocol. That is a companion change in the ur-afk repo.

## Decisions locked (with Este, 2026-07-12)

1. **Full #29** — scheduler + explicit roles + presets + macro-length gap-fitting + the
   unschedulable warning. Not a tactical patch.
2. **Ur Task owns assigned alts.** ur-afk skips any account Ur Task has an assignment for and
   covers only the rest. Ur Task is the surface users deliberately configure; ur-afk is the
   fallback, and users must not be trained to rely on the fallback.
3. **Shipped threshold table + per-game user override.** Works out of the box; correctable when
   our number is stale.
4. **Restore prior foreground** after every keep-alive service.
5. **Role is its own setting**, alongside the macro. Backgrounding an alt does not clear its
   macro — it pauses it. Non-destructive.
6. **Heartbeat claim file** for the ur-afk boundary. No contract bump, no host release, fails
   safe.

## Thresholds — what the research actually says

Sourced from `rororo-ur-afk/docs/game-idle-timings.md` (compiled 2026-07-06). This corrects a
long-standing misunderstanding and is load-bearing for the whole design:

- **The idle disconnect is 20 minutes, platform-wide, and it is a FLOOR.** Roblox's official
  docs say players idle "at least 20 minutes" get disconnected. Games can only *shorten* it via
  their own `Player.Idled` handler; **no game can extend it.** 20 min is the worst case
  everywhere. `[confirmed/official]`
- **Detection is input-absence.** Any keystroke resets the timer — a single Space suffices.
  Character/pathfinding movement does **not** count. `[confirmed]`
- **Focus matters.** Whether an *unfocused* window registers synthetic input is undocumented,
  and minimizing a client once paused it entirely. So "foreground the alt, then tap" is the only
  documented-safe path — which is exactly what this design does. `[confirmed]`
- **"Pet Sim = 14 min" was never the kick.** That number is Pet Sim's *own* anti-AFK teleport
  cadence (~15 min, measured from Este's logs). A 14-minute keep-alive setting felt flaky because
  it was **racing the game's built-in teleport**, not because it was too slow.

Games split into two roles:

| Game role | Games | Our job | Fire interval |
|---|---|---|---|
| **Primary keeper** — game ships no anti-AFK | Grow a Garden, Adopt Me, Brookhaven, Bee Swarm, Blox Fruits | We are the only thing keeping it alive | **11 min** |
| **Backstop** — game self-keeps (~15 min) | Pet Sim 99, Fisch, Anime Vanguards, Blade Ball | Game does the work; we're insurance | **17 min** |
| **Unknown** | anything unstamped | Assume no help | **12 min** |

These are **fire intervals, not thresholds** — the margin under the 20-minute floor is already
baked in (9 / 3 / 8 minutes of headroom respectively). Do **not** apply a second safety multiplier
on top; that is a double-discount bug waiting to happen. The research band was 10–12 min for
primary keepers and ~17–18 for backstops; the values above are pinned points inside it.

The backstop case is a bonus for the anti-thrash goal: those games need *fewer* focus steals.

Per-game numbers are mostly `[community]` confidence — hence the user override.

## Architecture

`AssignmentRunner`'s internals are replaced with a deadline scheduler. Its **public surface is
preserved** (`RunAsync(assignments, ct)`, `Progress` events, `Abort()`, single-flight claim) so
`PluginRuntime` and `RecipeRunner` do not churn.

### Data model

```csharp
public enum CadenceRole { Active, KeepAlive }

public sealed record Assignment(
    AccountRegistry.AccountInfo Alt,
    Macro? Macro,
    CadenceRole Role)                          // explicit — NO C# default value
{
    /// Legacy/derived rule, applied where assignments are built or loaded:
    /// a macro means you meant to farm; no macro means you meant to stay alive.
    public static Assignment WithDerivedRole(AccountRegistry.AccountInfo alt, Macro? macro)
        => new(alt, macro, macro is null ? CadenceRole.KeepAlive : CadenceRole.Active);
}
```

`Role` deliberately has **no C# default value**. A `= CadenceRole.Active` default would silently
contradict the derivation rule (a no-macro assignment would come out Active and get spun
back-to-back — precisely today's bug). The derivation lives in one place, `WithDerivedRole`, and
every construction/load site goes through it or passes a role explicitly.

Scheduler-internal state per assignment (not persisted):

```csharp
sealed class ScheduledAlt
{
    Assignment Assignment;
    long DueAtMs;        // monotonic; when this alt next needs servicing
    long IntervalMs;     // KeepAlive: from the game threshold. Active: 0.
}
```

**Clock is monotonic** (`Environment.TickCount64` / `Stopwatch`), never wall-clock — a DST shift
or a clock adjustment must not strand an alt or stampede the scheduler.

### The scheduling policy

Foreground is an **exclusive resource** — one window at a time. Two task classes contend for it:

- **Active** alts want it continuously (farm back-to-back). They have **no hard deadline** — a
  skipped pass just means less farming.
- **KeepAlive** alts want it briefly (~1s) but on a **hard deadline** — miss it and the game
  kicks the alt.

So keep-alives always win a tie, but only when they actually need to:

```
loop until cancelled:
    now = monotonic()

    # Which keep-alives would miss their deadline if we ran one more active pass?
    urgent = keepAlives where DueAt <= now + nextActivePassCost
    if urgent is not empty:
        alt = urgent.minBy(DueAt)                  # earliest deadline first
        service(alt)                               # focus -> Space -> RESTORE focus
        alt.DueAt = now + alt.IntervalMs
        continue

    if actives is not empty:
        alt = next active in round-robin order
        service(alt)                               # focus -> settle -> play macro (no restore)
        continue

    # Nothing active, nothing due: SLEEP. This is the whole feature.
    sleepUntil(min(keepAlives.DueAt))              # cancellable
```

`nextActivePassCost` = the upcoming active macro's `Macro.Duration` + focus settle (1000ms) +
inter-alt delay (`InterAltDelayMs`, default 500ms). **This is the macro-length lookahead**: it is
what decides whether a keep-alive can wait one more pass or must cut the line. With no active
alts, it is zero and keep-alives are serviced exactly at their deadline.

The final `else` branch is the point of the entire feature: **no active alts and nothing due
means the scheduler sleeps and steals no focus.** That is the single-account and stay-awake case.

### Make the policy a pure function

The decision — *what should happen next* — is extracted as a pure function so the hard cases test
deterministically without windows, timers, or Roblox:

```csharp
public static CadenceDecision Decide(
    IReadOnlyList<ScheduledAlt> alts, long nowMs, long nextActivePassCostMs);

public abstract record CadenceDecision
{
    public sealed record ServiceKeepAlive(ScheduledAlt Alt) : CadenceDecision;
    public sealed record RunActive(ScheduledAlt Alt)        : CadenceDecision;
    public sealed record SleepUntil(long WakeAtMs)          : CadenceDecision;
}
```

The runner becomes a thin shell: call `Decide`, act, repeat. All scheduling logic is testable
with a fake clock.

### Focus discipline

Keep-alive service:
1. Capture the current foreground window (`GetForegroundWindow` — `Win32Focus` already has it).
2. Focus the alt (`Win32Focus.AttachAndFocus`, which already carries the foreground-lock fix).
3. Verify the foreground actually flipped (existing `IForegroundWatcher` check).
4. Send Space.
5. **Restore** the captured foreground window.

A keep-alive becomes a ~1s blip instead of a hijack — and when an active alt is farming, step 5
naturally hands focus straight back to it so farming resumes.

Active service keeps today's behavior (focus, settle, play, no restore) — an active alt should
hold focus between its own back-to-back passes.

Restore failure is **non-fatal**: log it, carry on. Losing the user's focus is annoying; crashing
the loop is worse.

## The unschedulable warning

If a keep-alive alt's interval is **shorter than an active pass**, it cannot be guaranteed — even
firing it the instant a pass ends, the next pass blows its deadline. We know every macro's
`Duration` and every alt's threshold **at start**, so this is computed up front, not discovered
after an alt gets kicked:

> *Foo (Grow a Garden, 12 min keep-alive) may get kicked — your active macro's pass is 16 min.
> Shorten the macro, split it, or set Foo to Active.*

Surfaces in the activity log and the themed toast at run start. The run still proceeds (proceed-
with-successes, consistent with the recipe runner's philosophy) — we warn, we do not block.

## Game idle thresholds

```csharp
internal static class KeepAliveIntervals            // NOT "Thresholds" — it returns the FIRE
{                                                   // interval, with headroom already applied.
    public static TimeSpan For(long? placeId);
    public const int UnknownGameMinutes = 12;
}
```

- Named `KeepAliveIntervals`, not `GameIdleThresholds`, on purpose: it returns **when we fire**,
  not when the game kicks. Naming it "threshold" invites a caller to helpfully apply a safety
  margin to a number that already has one.
- Table keyed by `PlaceId`, seeded from the ur-afk research (primary-keeper vs backstop rows).
- The alt's `PlaceId` comes from presence (the game stamp shipped in v0.6 —
  `AccountInfo.PlaceId`).
- **Per-game user override** persisted in settings; overrides beat the shipped table.
- Unknown game → 12 min.

## Claim file (the ur-afk boundary)

While the scheduler runs, Ur Task publishes what it owns:

`%LOCALAPPDATA%\626Labs\claims\ur-task.json`

```json
{
  "plugin": "ur-task",
  "heartbeatUtc": "2026-07-12T18:04:11Z",
  "ttlSeconds": 60,
  "ownedUserIds": [123456, 789012]
}
```

- Written on run start, **refreshed every 20s** against a **60s TTL** (3× headroom, so one slow
  tick never looks like a crash), **deleted on clean stop**.
- Written atomically (temp file + move) so a reader never sees a torn file.
- ur-afk (companion change, **separate repo**) reads it, skips `ownedUserIds`, and treats a
  missing or **stale** heartbeat as "Ur Task isn't running — cover everything."

Fails safe in the right direction: if Ur Task crashes, the claim goes stale and the fallback
resumes covering those alts.

This is a stepping stone. When Ur Reset lands, the family will need a real host-brokered claim
registry; this file is that registry's first implementation, and the shape (`plugin`, `heartbeat`,
`owned`) is deliberately the shape a broker would expose.

## UI

- **Role toggle** per assignment row: Active / Keep-alive. Backgrounding preserves the macro.
- **Preset buttons:**
  - *All equal* — every assignment → Active (today's round-robin).
  - *One focused, rest background* — pick the focused alt; everyone else → Keep-alive, macros
    preserved.
- **Next-due indicator** on keep-alive rows (e.g. `next: 8m`). Without it the scheduler is
  invisible — a quiet screen reads as "broken." This is proof-of-life, not decoration.
- The unschedulable warning surfaces in the activity log + toast.

## Error handling

| Case | Behavior |
|---|---|
| Focus steal fails | Skip this alt, push `DueAt` out **30s** (bounded retry — do not hammer), emit `Skipped`. After 3 consecutive failures for the same alt, log it loudly: the alt is probably gone or blocked. |
| Foreground didn't actually flip | Same as above — existing verification path. |
| Restore-focus fails | Non-fatal. Log and continue. |
| Macro refused (e.g. client-size) | Existing `Refused` path; already reaches the activity log as of v0.6. |
| Alt window disappears | Drop from the schedule, log it, keep the rest running. |
| Claim file unwritable | Log once, continue scheduling. Cadence must not depend on the claim file. |
| Clock | Monotonic only. Never wall-clock. |

## Testing

The pure `Decide` function is where the real coverage lives — a fake clock makes every hard case
deterministic:

- **The regression that matters:** one keep-alive alt, one hour of simulated time → serviced ~5
  times (12-min interval), **not ~2,880 times** (today's 1.25s spin).
- No active alts, nothing due → `SleepUntil`, not a service.
- Keep-alive deadline lands inside the next active pass → `ServiceKeepAlive` **before** the active
  pass (the gap-fitting rule).
- Keep-alive deadline is comfortably beyond the next active pass → `RunActive` first.
- Two keep-alives both urgent → earliest deadline serviced first.
- All-Active assignments → round-robins back-to-back exactly as today (compat guard).
- Unschedulable detection: threshold < active pass cost → warning raised at start.
- Backstop vs primary-keeper games get their respective intervals from the table.

Integration-level (existing fakes/seams — `AssignmentRunner` already injects
`Func<int,(bool,string?)> _focus`, and `IMacroPlayer` / `IForegroundWatcher` are interfaces):

- Focus is captured and restored around a keep-alive service.
- Claim file is written on start, refreshed, and removed on clean stop.

## Compatibility / migration

- `Assignment` gains `Role` with a **derived default** so existing saved state and all current
  call sites keep working: **macro present → Active; macro absent → Keep-alive.**
- All-Active setups (macros on every alt) behave exactly as today.
- The one intentional behavior change: keep-alive alts stop being serviced every 1.25s and start
  being serviced on their real deadline. That *is* the feature.

## Out of scope (follow-ups)

- ur-afk's consumption of the claim file (companion change, ur-afk repo).
- Host-brokered claim registry (arrives with Ur Reset).
- Arbitrary per-alt intervals in the UI (the engine is interval-based, so this is a later reveal
  if wanted — roles cover the named need).
