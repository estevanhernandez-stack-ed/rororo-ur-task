# Autonomous keep-alive — scope

**Found:** during the v0.7.0 smoke, 2026-08-11 · **Status:** proposed, not started
**Blocks 0.7.0?** No — see "Ship 0.7.0 first" below.

**One sentence:** keep-alive only runs while the macro loop runs, so the moment you stop playing
assignments every managed alt starts idling toward a kick.

---

## What it does today

`AssignmentRunner.RunAsync` owns the whole cadence loop, keep-alives included. It is entered from
exactly two places: the `Ctrl+Shift+P` handler and recipe runs. There is **no timer and no
background path** — a grep for `PeriodicTimer` / `DispatcherTimer` / a standing loop returns
nothing.

Stop the loop and keep-alives stop with it. Observed in smoke: `CElCPapa · next: 0m`, overdue and
not firing, because nothing was running to fire it.

### This is a boundary, not an oversight

The ur-afk claim file is published off the runner's `Started` event and released on `Stopped`. So
the current contract between the two plugins is:

> **Ur Task owns these alts only while its loop is running. Otherwise they belong to Ur AFK.**

That is a coherent handoff, and it is why Ur AFK covers the gap today. Any change here is a change
to that contract, not just an added feature.

---

## What Este asked for

1. Keep-alive fires without `Ctrl+Shift+P`.
2. A user toggle — but most people will leave it on, so **default on**.
3. It doubles as the **emergency** keep-alive: assigned alts that are not currently being run by the
   macro loop still get kept alive.

Point 3 is the sharpest. An alt with a macro assigned gets **nothing** when the loop is stopped, so
today the account most likely to matter is the one least protected.

---

## The part that will bite: the claim must follow the behaviour

**If Ur Task keeps alts alive autonomously, it has to hold the claim autonomously.** Otherwise both
plugins service the same alt and fight over the foreground — two keep-alives, two focus steals,
racing.

So the claim's lifetime moves from *"while the loop runs"* to *"while autonomous keep-alive is
enabled AND this alt is in our set"*. That is the actual design change; the timer is the easy part.

Consequences to settle before writing code:

- **Toggle off must release the claim promptly**, or Ur AFK stays locked out of alts nobody is
  keeping alive — strictly worse than today.
- **A crash must not leave a stale claim.** The claim file is already a heartbeat (v0.7.0 work), so
  the mechanism exists; it now has to keep beating outside a run.
- **Ur AFK's own behaviour is not in this repo.** Anything asserted about how it reacts is an
  assumption until tested with both installed.

---

## Shape

**A standing scheduler, not a second loop.** `CadenceScheduler` already decides *what is due and
when* as pure logic. Autonomous mode should drive that same scheduler from a background timer, so
there is one scheduling policy with two drivers — not two implementations that drift.

**Role resolution when the loop is off:**

| alt | loop running | loop stopped (autonomous on) |
|---|---|---|
| KeepAlive | keep-alive on cadence | **unchanged** — same cadence |
| Active (macro assigned) | macro runs | **keep-alive only** — this is Este's point 3 |

**Preference:** `AutonomousKeepAlive`, default **on**, in `UserPreferences`. Default-on is a
behaviour change for existing users, so it belongs in the release notes as one.

---

## Ship 0.7.0 first

0.7.0's claim is about **cadence** — that the scheduler decides when to fire instead of spinning,
measured at 1.7 focus steals/min against ~60 before. That claim is true and measured, and this gap
does not touch it.

The drafted release notes say "keep-alives run on a schedule instead of a spin loop." They do **not**
claim keep-alive runs unattended. Nothing in them needs correcting for this to be outstanding — but
**do not add such a claim** without this work.

Ur AFK covers the gap today, which is what makes waiting reasonable rather than negligent.

---

## Testing

- With autonomous on and the loop stopped: a KeepAlive alt fires on its cadence.
- With autonomous on and the loop stopped: an **Active** alt gets keep-alives rather than nothing.
- With the loop running: behaviour is identical to today (no double-firing, no changed cadence).
- Toggling off releases the claim, and toggling on re-publishes it.
- The claim keeps beating with the loop stopped, and goes stale on kill — the existing heartbeat
  tests extend to the autonomous driver.
- One scheduling policy: assert the autonomous driver and the loop driver consult the same
  `CadenceScheduler` decision, so a future cadence change cannot apply to one and not the other.

## Not in scope

- **Changing Ur AFK.** Different repo, different product. This makes Ur Task able to stand alone;
  it does not make Ur AFK unnecessary, and the claim handoff must keep working for people running
  both.
- **The hold-on-Ctrl+Shift feature** (Este's separate idea from the same smoke): pausing a macro on
  chord detection rather than abandoning the run. Related surface, different problem, its own scope.
