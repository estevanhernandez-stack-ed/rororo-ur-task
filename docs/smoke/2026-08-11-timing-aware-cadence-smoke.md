# Smoke checklist — timing-aware cadence (v0.7.0)

**PR:** [#30](https://github.com/estevanhernandez-stack-ed/rororo-ur-task/pull/30) ·
**Branch:** `feat/timing-aware-cadence`

The scheduler's logic is unit-tested at 279 green. **What tests cannot see is whether the machine is
usable while it runs** — that is the entire claim of this release, and it is the reason PR #30
carries its own "needs a live smoke before merge" gate.

## The claim, stated as a number

| | focus steals per Active alt |
|---|---|
| v0.6.0 (spin loop) | about **one per second** — ~60/min |
| v0.7.0 (deadline scheduler) | about **one per 30 seconds** — ~2/min |

That is a 30x difference. It is easy to feel and easy to be wrong about, so **do not eyeball it**.
`build\measure-focus-steals.ps1` counts it.

---

## Setup

- [ ] Quit any running RoRoRo **and** any running Ur Task first (both hold single-instance claims).
- [ ] Build this branch: `dotnet build rororo-ur-task.csproj` — build the **`.csproj`**, never the
      `.sln`, which drags the whole host app in and fails while RoRoRo is running.
- [ ] Start RoRoRo, then start Ur Task.
- [ ] Have **at least 2 alts** running, at least one set to **Active** and one to keep-alive. One
      alt cannot show round-robin behaviour.

---

## Phase 1 — the load-bearing one: is the desktop usable?

Run the counter in its own PowerShell window and then **go and use the machine normally** — type in
a document, scroll a browser, whatever you would actually be doing while alts tick over.

```
powershell -ExecutionPolicy Bypass -File build\measure-focus-steals.ps1 -Minutes 5 -Note "v0.7.0, N alts, 1 Active"
```

- [ ] The header prints `reading now : <something>`. **If it says it cannot read the foreground
      window, stop** — every count after that would be a confident zero. Run it from a normal
      PowerShell window on your own desktop.
- [ ] Alt-tab once. A `->` line appears within a second. The instrument is working.
- [ ] Let it run the full 5 minutes while you use the machine.

**PASS:** steals by Roblox in the low single digits per minute — roughly one per 30s per Active alt.
**FAIL:** tens per minute. That is the old spin loop's signature and the release claim is false.

- [ ] Could you actually type a sentence without losing focus mid-word?

> Typing is the honest test. A number can look acceptable while the steals all land in one burst,
> and a burst is what ruins a sentence.

## Phase 2 — did the keep-alives still happen?

A scheduler that never steals focus passes Phase 1 perfectly and does nothing useful. **Both halves
have to be true.**

- [ ] Every alt marked keep-alive is still logged in and has not idled out after the 5 minutes.
- [ ] The grid's **next-due countdown** moves and matches what actually happens — an alt due in 10s
      gets serviced in about 10s.
- [ ] `%LOCALAPPDATA%\626Labs\RoRoRoUrTask\logs\ur-task.log` shows keep-alive activity, not silence.

## Phase 3 — the specific bug this release fixes

- [ ] A macro **recorded on a larger monitor**, assigned to an Active alt, steals focus about once
      per 30s — not once per second. This is the exact case named in PR #30.
- [ ] Set an alt to a cadence the scheduler cannot meet. You get an **up-front warning**, not silent
      permanent lateness.
- [ ] Cancel a run mid-flight: focus returns to the window you were using, and no stale assignment
      is left behind.

## Phase 4 — dialogs (new this cycle)

- [ ] Macro library → delete a macro. **Press Enter.** It must **cancel**, not delete.
- [ ] Press **Esc** on the delete, rename, and multi-window dialogs. All three close.
- [ ] Rename a macro: type a name, press Enter. It renames — Enter *should* submit here.

---

## Recording the result

Paste the counter's `=== PASTE THIS BACK ===` block into PR #30 with the alt count and which were
Active. A number in the PR is what lets the next person believe the release note, which makes the
same claim in words.

**If Phase 1 fails, do not merge.** The release notes for 0.7.0 lead with this exact claim; if the
smoke fails, the notes are wrong too, and they fail together rather than the notes quietly
overstating.

## A baseline, if you want one

The comparison is stronger with a before. On `main` (v0.6.0), same alts, same duration:

```
powershell -ExecutionPolicy Bypass -File build\measure-focus-steals.ps1 -Minutes 5 -Note "v0.6.0 baseline"
```

Not required — the absolute number is enough to pass or fail — but a measured before/after is what
turns "feels better" into evidence.
