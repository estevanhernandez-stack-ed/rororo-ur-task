# Ur Task 0.13.0, wave A: keep-alive you can trust

Status: design, 2026-10-25. Follows `docs/ideas/2026-10-25-upgrade-roadmap.md` (wave A) and the
brainstorm it came from. Built on `release/0.12.1` (break-glass, turbo, paste step, recorder trim).

## Why

Keep-alive is the thing the whole multi-account setup leans on, and today nobody can tell whether
it is working. Two incidents carry the case:

- **The run that starved it.** A long repeating click run inside Ur Task held the foreground,
  keep-alive never got a turn, and three accounts were idle-kicked while the log still said
  keep-alive was running.
- **The silent afternoons.** The log from 10-09 to 10-24 has 33 "hasn't reliably reached the
  foreground" warnings, clustered: twice, every alt failed within about a minute of each other,
  each time while the user was working in another app. After the warning the log says nothing.
  On one of those days the loop ran another seven hours and nothing can say whether a single
  Space landed.

The user's words: "the alerts are enough, I just don't know why." Wave A is not more alerting.
It is keep-alive that **says what it did, says why when it could not, and stops fighting the
macros and agents it shares the foreground with.**

## Goals

1. **Every keep-alive outcome is in the log.** One line per Space that landed, on both the turbo
   and the normal path. One line per miss, saying which step failed and why. One line when an alt
   that was missing lands again.
2. **A ledger the rest of Ur Task can read.** Per account: when input last landed, when it is due,
   its state, and its recent misses. It outlives a stop and restart of the keep-alive loop (not a
   restart of Ur Task).
3. **Deadlines instead of grabs.** Keep-alive publishes, per account, the time by which it needs
   input. Agents read that and fit the input in themselves. Keep-alive takes the foreground only
   when an account reaches its deadline untouched.
4. **Any input counts.** A macro Ur Task plays on an account resets that account's clock. Most of
   the time an agent's own work keeps its accounts alive and keep-alive does nothing.
5. **Pause and pick up.** When an account reaches its deadline while a macro is running on another
   account, the macro pauses at its exact spot, keep-alive serves the due account, and the macro
   picks up from the same step.
6. **Per-account keep-alive off**, for the accounts the user is fine letting reset.
7. **Stable reasons on the bridge**, with who caused it and what to do, and #39 fixed.
8. **Measured idle limits.** The deadline comes from a measured number per game, not from desk
   research.

## Non-goals

- The autoclicker, repeat bounds, own-input stamps for clicks, condition checks (waves B and C).
- Background input (posting to a background client). Separate experiment.
- Ur AFK. It stays off until its host-side idle bug is fixed (RoRoRo #221).
- A "no input at all" hold that refuses macros on an account. Only keep-alive off is in scope.
- Agent-held pause leases with owners and timers (in the roadmap). Goals 3 to 5 cover what they
  were for inside Ur Task, so they are cut from 0.13 (builder's note: my call, see "Calls made").
- Persisting any of this across an Ur Task restart, other than break-glass, which already does.

## Anti-goals (would make this a failure even if it works)

- The log or GetStatus says keep-alive is running while nothing lands. If no Space has landed on an
  account by its deadline, that must be said, loudly, whatever the loop thinks it is doing.
- Any input sent without the foreground verified as the target. Pausing, yielding and crediting
  only ever withhold input or record it; none of them add an unverified path.
- A per-account off that outlives what the user expects. It ends on toggle or restart, nothing else.
- Renaming or changing the meaning of a bridge reason code another plugin already branches on
  (Ur OCR reruns on `refused`).
- Seven identical alert lines when one cause hit seven accounts.

## Constraints

- Bridge contract 1.0: new methods and new optional fields only. An old Ur Task answers a new
  method with `refused` / "Unknown method", and callers turn that into "update Ur Task".
- Log lines scripts already read stay as they are: "Playing assignments ...", "Assignment loop
  stopped". The break-glass lines too.
- Public repo: no account names or game-specific playbook content in code, tests or docs. Game
  names already in `KeepAliveIntervals` are fine; measured numbers go there.
- Build the `.csproj`; the standalone suite gates; the host-integration job stays.
- Ur MCP (`rororo-ur-mcp`) ships a status tool in the same window as 0.13.0. Pull that repo before
  starting; the local clone may be behind.

## The design

### Idle clock, deadline and takeover

- Each account has an **idle clock**: time since Ur Task last landed input on it.
- **Landed** means: the foreground was verified as that account's window, and `SendInput`
  reported every event inserted. In 0.13 only **key input** stamps the clock: a keep-alive Space,
  a turbo Space, a macro's key steps, a paste. Mouse input (points, drags, sweeps, wheel) does not,
  because nobody has measured whether Roblox counts it, and crediting input Roblox ignores would
  stand keep-alive down for an account that is really idle. Mouse credit is wave B's "click
  credit", after a measurement says it counts. Everything stamps through one call. A rejected `SendInput` does not stamp (today the runner
  stamps success either way: `CadenceDeps.SendKeepAlive` is an `Action` that drops the bool).
- Each account has a **limit**: how long the game lets it sit before something bad happens, the
  game's own idle reset or Roblox's disconnect, whichever is first. A game's own anti-AFK reset
  counts: it rejoins the player and loses their place, which is as bad as a kick. Limits:

  | Game | Limit | Deadline | Today's fire interval |
  |---|---|---|---|
  | No anti-AFK of its own (Grow a Garden, Adopt Me, ...) | 20 | 14 | 11 |
  | Has its own ~15 min anti-AFK reset (PS99, Fisch, ...) | 15 (PS99 measured, below) | 9 | 17 (PS99 got 12 by the name bug) |
  | Unknown | 18 | 12 | 12 |

  Unknown keeps today's 12-minute behaviour. A measured number replaces a table entry.
- The **deadline** is the limit minus 6 minutes: the time by which the account needs input. With
  the default limit that is the user's 14 minutes. Agents see the deadline.
- At the deadline, keep-alive **takes over** for that account: it serves it itself, through the
  existing safe path (focus, verify, Space, restore), retrying on a miss as today. The 6 minutes
  are its retry runway.
- **Keep-alive does nothing for an account whose clock keeps getting reset.** No more grabbing
  on a fixed interval regardless of what else is happening.
- When a loop starts, accounts with no ledger history start due at the deadline from now, spread
  so they do not all come due in the same second (today every account starts due at once).
- If an account's idle clock passes its deadline with no landed input and the next retry is not
  going to land before the limit, say so: "<account> has had no input for 14 min; expect a reset or
  kick around HH:MM." That is the line the silent afternoons were missing.

### Misses that explain themselves

A miss names its step and its cause, in one line per account per miss, and the existing
three-miss alert gets the same cause appended:

| Step | Cause shown | How it is known |
|---|---|---|
| Find the window | window gone (process exited, no main window) | `AttachAndFocus` errors, which today are dropped |
| Ask for focus | Windows refused the focus | `SetForegroundWindow`'s return value, which today is ignored (`Win32Focus.cs:94`) |
| Verify | something else in front: process name and window title | read the foreground when the verify fails, the way abort lines already do |
| Send | input rejected by Windows | `SendInput` count |
| Before trying | held off: break-glass, keep-alive off for this account, a macro pausing | ledger state |

**All at once:** when every account due in the same minute misses for the same cause and the same
foreground holder, write one line naming it ("every account missed: Windows refused focus while
'X' was in front") instead of one per account. The per-account ledger still records each.

**Recovery:** when an account that had missed lands again, one line: "<account> landed again after
N misses (M min since its last Space)".

### The ledger

One owner, created with the plugin runtime (outlives the keep-alive loop, not Ur Task). Per account
by Roblox user id: last landed input (and by what: keep-alive, macro, paste), deadline, limit,
state (`serving`, `waiting`, `off`, `held`: break-glass, `paused-for-macro`), consecutive misses
and the last miss's cause. Both service paths, the step runner and the macro player write to it;
the bridge and the UI read snapshots. A read never blocks the runner.

Turbo's per-alt lines from 0.12.1 (`SweepLog`) become ledger writes; their wording can stay.

### GetStatus and the Ur MCP tool

New bridge method `GetStatus` (no arguments beyond the usual envelope). Returns:

- keep-alive: `running`, `stopped` or `breakglass`; turbo on or off.
- per account: user id, display name, role (`active`, `keep-alive`, `off`), last landed input time
  and source, idle seconds, deadline time, limit, consecutive misses, last miss cause, state.
- current playbacks with their id and whether they are paused for keep-alive (and for which
  account).

Times are absolute and in local ISO form at the edge; the ledger keeps a monotonic clock inside.
Ur MCP gets one tool, `keepalive_status`, wrapping it. Nobody has started it: `rororo-ur-mcp` is
at 0.4.1 with no GetStatus code, so this build includes its tasks there (bridge client method,
wire record, tool, fake, tests) and a coordinated 0.5.0 release. This spec's GetStatus is the shape. A `RunMacro` response gains an optional
note when the run is likely to cross an account's deadline, using the macro's estimated duration.

### Pause and pick up

- When an account reaches its deadline and a macro is playing on a different account (bridge run,
  an Active assignment pass, a recipe step), keep-alive asks the playback to pause and waits for it
  to stop at the **next step boundary**. It never takes the foreground while a step is mid-flight.
- Paused: the step runner holds at the boundary. Steps are never interrupted: a hold, a sweep or a
  drag in progress runs to its own end first. Keys a key step left down across the boundary are
  released for the break and pressed again on resume.
- Keep-alive serves every account due within the next two minutes in that one break (turbo-style
  batching, whether or not turbo is ticked), then restores the foreground to the paused macro's
  window.
- Resume: the macro verifies its own window is in front, then continues at the step it stopped at.
  Nothing is replayed. If its window is gone, the playback ends with a reason, as it would today.
- If the playback cannot reach a boundary in time (one step longer than the remaining runway, for
  example an open hold with no max), keep-alive cuts in at the limit minus one minute, and the
  playback stops with reason `keep-alive-preempted` and advice to give the step a max.
- Log: "paused at step N/M for <account>" and "resumed at step N/M (paused X s)".
- Old event-list macros (no steps) pause only between passes.
- An account's own macro run resets its own clock, so a playback is never paused for the account
  it is playing on.

### Per-account keep-alive off

- A toggle per account in the assignment list. Off means keep-alive never serves that account.
  It still appears in GetStatus, as `off`, with its idle clock running, so a forgotten toggle is
  visible as an account heading for a reset.
- Lasts until the user turns it back on or Ur Task restarts. Not saved. Agents can read it, not
  set it.
- Macros still run on an `off` account; off is about keep-alive only.
- Accounts launched while the loop is running join it as keep-alive (today the list is fixed when
  Ctrl+Shift+P is pressed), unless they are toggled off.

### Reasons, actor and advice (#39)

- Every bridge refusal and every failed playback carries a stable reason code, an optional
  `actor` (`user`, `agent:<callerPluginId>`, `keep-alive`, `host`, `ur-task`) and an optional
  `advice` sentence. New fields are optional and trailing; old callers ignore them.
- Codes live in one place so they cannot drift. Existing codes keep their meaning, `refused`
  included. New codes in 0.13: `macro-unreadable` (#39), `keep-alive-preempted`.
- #39: a macro id whose file failed to load answers `macro-unreadable` with the load reason
  (newer schema, bad JSON), not `unknown-macro`. Same for ListMacros and recipe lookups.

### Measured limits

- **PS99, measured 2026-10-25 on Dunder-MiffLan**, one untouched account on a private server,
  10-second captures: the game shows "We're restarting Pet Simulator 99! Please wait while we
  redirect you..." and rejoins the account to a fresh server, back at spawn, in about 25 seconds.
  Lap 1: 16 min 25 s after a verified Space. Lap 2: about 14 min 55 s after landing back in, with
  no input. It repeats, so it is the game's anti-AFK, not a server restart, and Roblox's 20-minute
  disconnect never comes into play. Limit 15 (the earliest seen), deadline 9.
- Measure the other games in the table the same way when they are next played; until then their
  row stands.
- Gotcha from the same run: RoRoRo's idle clock credited a click on another window to the test
  account (RoRoRo #221), off by about 85 s. Ur Task's own landed-input clock is the one to trust.
- Fix game-name matching in `KeepAliveIntervals`: presence reports names with decoration (emoji,
  brackets, punctuation), so today PS99 never matches its entry and falls to the 12-minute default.
  Match on a normalized name that ignores decoration.
- The table's existing fire intervals give way to limits (table above). A per-PlaceId user
  override was entered as "act by this many minutes", so it is read as the **deadline** directly
  (limit = override + 6), never re-shortened. Deadlines under 2 minutes are clamped to 2, with a
  log line saying so.

## Live check before tagging

- Keep-alive on every account for the whole check. Nothing gets idle-kicked.
- Type in another app while an account comes due: the miss line names the refusal and the holder,
  and the recovery line follows.
- Run a long macro on one account while another reaches its deadline: it pauses, the other jumps,
  it resumes at the same step.
- Toggle one account off: it shows `off` in GetStatus and is never focused.
- `keepalive_status` from Ur MCP matches what the window shows.

## Left to the builder

Data structures and locking for the ledger; how the pause request reaches the step runner; exact
log wording beyond the facts each line must carry; where the toggle sits in the row; how the
spread start is computed; how GetStatus is split between the server and a status provider.

## Calls made in this design (not stated by the user)

- Deadline = limit minus 6 minutes, generalising the user's "14 minutes" from Roblox's 20.
- Takeover at the deadline (the user's "yes" to "should keep-alive step in", read as step in).
- Pause at step boundaries only; a step in progress always finishes; preempt at limit minus one
  minute.
- Only key input credits the idle clock in 0.13; mouse credit waits for a measurement (wave B).
- Limits table: own-anti-AFK games 15, unknown 18 (keeps today's 12-minute deadline); overrides
  read as deadlines, clamped at 2 minutes.
- One break serves every account due within two minutes.
- Agent pause leases and the "no input" hold cut from 0.13.
- Accounts launched mid-loop join it.
- `off` is readable but not settable by agents.
