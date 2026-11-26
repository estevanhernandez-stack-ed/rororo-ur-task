# Review and remediation, shipping as 0.13.1

**Written:** 2026-11-25 · **Baseline:** `main` at `0c5b536` (v0.13.0) · **Status:** approved 2026-11-25, Phase 0 in progress

Ur Task has had no commits and no CI runs since v0.13.0 on 2026-10-26. In that month the host went
from v1.19 to v1.40.0.0 and the plugin contract went from 0.8.0 to 0.14.0. Nothing has read the
plugin end to end since the keep-alive wave landed 22 commits in one evening.

Four phases. Phase 0 makes the baseline true. Phase 1 reads the code. Phase 2 turns findings into
one ranked list that Este approves. Phase 3 fixes what was approved and ships 0.13.1 before wave B
(0.14.0, smart autoclicker) starts.

---

## What is already known

Verified on 2026-11-25, before any review lane ran.

| # | Finding | Evidence |
| --- | --- | --- |
| K1 | `host-integration` does not compile against the current host. | CI run 45734371255: `PluginClientIntegrationTests.cs(200)`, `NoOpLauncher` is missing `IPluginLaunchInvoker.RequestLaunchTargetAsync(string, string?, long?, PluginGameInstance?)` (host #247). `unit-tests` green. |
| K2 | The CI guard only watches this repo. | `test.yml` triggers on push, pull_request and workflow_dispatch. A quiet month here means the host changes with nothing compiling the integration test. Second occurrence of the August rot. |
| K3 | Contract pin is six versions behind. | csproj pins `ROROROblox.PluginContract` 0.8.0; NuGet has 0.14.0. The proto diff from 0.8.0 to host main is 107 insertions and no removals, so the wire is additive. |
| K4 | The keystone describes a debt that no longer exists. | `CLAUDE.md` says built-in theme palettes are mirrored in plugin code. `src/Theming/HostThemeReader.cs` deleted the mirror and `PluginClient` consumes `GetTheme` and `SubscribeThemeChanged`. Only the `Brand` fallback remains, on purpose. |
| K5 | CHANGELOG calls two shipped releases unreleased. | `## 0.13.0 — unreleased` and `## 0.12.1 — unreleased`. Both were tagged and released 2026-10-25. |
| K6 | Planning docs live on one machine. | Untracked on Dunder: `docs/ideas/` (the wave B to D roadmap, the brainstorm, stepped flows, break-glass note), the 2026-10-11 library spec, and 30 uncommitted lines in `docs/BACKLOG.md`. |
| K7 | Branch litter. | 10 of 14 remote branches are merged into main. `feat/theme-from-host` shows 5 unmerged commits whose content is already on main by another route. `.gitnexus/` is not ignored on main. |
| K8 | Five follow-ups were parked at 0.13.0 and never filed. | Stopped-state rows showing `next: Nm`; break-glass attributing misses to accounts keep-alive never served; the "nothing was in front" wording; a throwing Active macro re-focusing every 30 s; two prefs sources. |
| K9 | Two 0.13.0 live checks never closed. | A miss line naming the holder (no miss occurred in the smoke), and RoRoRo's activity clock moving due times (needed permissions granted through a real install). |
| K10 | Bare Esc is the easy kill key, on purpose. | Este, 2026-11-25. `HotkeyService.cs` registers a bare-Esc abort (`ID_ABORT_BARE`) beside Ctrl+Shift+F12 and it stays. The dashboard task "Esc anywhere aborts Ur Task keep-alive" (2026-10-05) asked to restrict it; that half is closed by this ruling. What is left to check is when Esc is armed and how loud a stop is, not whether Esc should abort. |
| K11 | `DiagLogTests` is flaky. | Three full runs on 2026-11-25 failed 2, 1 and 0 of the log tests (`Write_ConcurrentWriters_EveryLineIntact`, `IncompleteStartup_WritesWatchdogLine`). Both pass alone. `DiagLog.Directory` is a process-wide static and the `DiagLog` collection does not cover every class whose code writes to it. The live log was not touched. |
| K12 | A stale local `NuGet.config` broke restore. | Hidden by `.git/info/exclude`, left from August, pointing at a deleted temp feed named `local-contract-0.8.0`. Every restore failed with `NU1301`. Moved aside in Phase 0. |

No vulnerable packages. `Grpc.Net.Client` is 2.70.0 here, 2.68.0 in the host, 2.85.0 latest.

---

## Phase 0: make the baseline true

One PR, `chore/catch-up-to-host-1.40`. Nothing here waits on the review, and the review should not
start on a red baseline.

1. **Fix the test double.** Implement the new `RequestLaunchTargetAsync` overload on `NoOpLauncher`.
   Then build without `StandaloneTestsOnly` locally, because CI stops at the first compile error
   and there may be more behind it. Needs `ROROROblox` checked out as a sibling, on its main.
2. **Bump the contract pin to 0.14.0.** Expected to be a recompile. Do not start calling new RPCs
   in this PR; `minHostVersion` stays at 1.19.0 unless a new call is added. Leave `Grpc.Net.Client`
   alone unless the bump forces it.
3. **Close the CI hole.** Add a daily `schedule:` trigger to `test.yml`. Know its limit: GitHub
   disables scheduled workflows in a public repo after 60 days without repo activity, which is
   exactly the quiet-repo case. The durable fix is the host triggering this workflow when its
   contract or plugin surface changes. That is a host-repo change, so file it as a host issue and
   say so. Do not edit the host from here.
4. **Fix the records.** Date 0.12.1 and 0.13.0 in the CHANGELOG. Rewrite the stale theme section of
   `CLAUDE.md` (K4) to describe what is true: the feed is consumed, `Brand` is a fallback.
5. **Commit the machine-only docs** (K6). Add `.gitnexus/` and `.claude/worktrees/` to `.gitignore`.
   `AGENTS.md` and `.claude/626labs-context.md` are tool-generated; ignore them, do not commit them.
6. **Prune merged branches.** Deleting remote branches needs Este's explicit yes. List them, ask once.

**Exit gate:** both jobs green on the PR, and green again on a `workflow_dispatch` run after merge.

---

## Phase 1: the review

Read-only. Seven lanes, independent, run in parallel. Judgment tier for every lane: these are
audits of concurrency and Win32 input code, not volume work.

Each lane returns findings in one shape: `file:line`, what is wrong, the concrete scenario that
makes it fail (inputs or state, then the wrong outcome), and a proposed severity. A finding without
a failure scenario is an opinion and gets dropped.

| Lane | Scope | Size | Look hardest at |
| --- | --- | --- | --- |
| 1. Keep-alive and foreground arbitration | `src/Macros/KeepAlive/`, `AssignmentRunner.cs`, `ForegroundGate`, `SweepInputGuard` | ~2,800 lines | Races between a playback's exit and keep-alive's break. Every path that takes focus must give it back. K8, K9. The 15 s guard cap. |
| 2. Playback engine | `StepRunner.cs`, `MacroPlayer.cs`, `SequencePlayer`, `RecipeRunner`, `src/Macros/Steps/` | ~4,000 lines | Held keys and buttons released on every exit path, including cancel and throw. Pause and resume at a step boundary. `EnsureClientSize` refusing macros that carry no coordinates (known from the paste-step test). Display scale. |
| 3. Recording and hotkeys | `MacroRecorder.cs`, `src/Hotkeys/` | ~1,100 lines | Bare Esc stays as the kill key (K10); do not propose removing or narrowing it. Check only that it is armed just while something is running, since a registered hotkey swallows the key system-wide (the v0.3.1 bug), and that a stop is hard to miss afterwards. Hook lifetime and thread affinity. Modifier leak from the record chord. What happens when `RegisterHotKey` fails because another instance holds the key. |
| 4. IPC and the host boundary | `src/Ipc/`, `src/PluginHost/`, `PluginRuntime.cs` | ~3,800 lines | Named pipe is `CurrentUserOnly`: confirm, then check message size limits and malformed input. Reason codes stay stable. HostLost self-terminate. Capability-denied fallback. Startup watchdog. Run `/vibe-sec:audit` here, with `docs/v0.2-security-review.md` as the baseline to diff against. |
| 5. Untrusted files | `MacroV1Migrator.cs`, `RecipeStore.cs`, `PointAdjustmentStore.cs`, `UserPreferences.cs`, bundle import and export | spread | Macro bundles come from other clan members. Path traversal on import, oversized or hostile JSON, a newer schema, duplicate ids, a corrupt prefs file on startup. |
| 6. UI | `src/UI/`, `src/Theming/`, `App.xaml` | ~3,900 lines | Work on the dispatcher that should not be. Brushes mutated in place (does not re-theme). Culture-sensitive formatting. Copy against the voice rules: sentence case, second person, no emoji, written for non-technical Windows users. |
| 7. Tests, CI and records | `tests/`, `.github/workflows/`, README, `FEATURES.md`, `manifest.json`, issues, dashboard | 793 tests, 81 files | Run `/vibe-test:audit`. What the standalone flag hides beyond the one integration file. The two hotkey tests that fail when an installed Ur Task holds the keys. The 19 empty `catch` blocks: which are deliberate. Whether README and the manifest description still describe 0.13.0. Open issues #27, #28, #29, #34 against what shipped. |

**Verification rule.** The session model re-checks every finding against the code before it enters
the list. On the 0.13.0 build every serious catch came from that layer, and a lane report accepted
on trust is how a wrong finding becomes a wrong fix.

**Exit gate:** seven lane reports, each finding confirmed or dropped with a reason.

---

## Phase 2: triage

One file, `docs/reviews/2026-11-review-findings.md`. Ranked, most severe first.

**Severity.**

- **S1.** Can drop an account, lose or corrupt a macro, send input to the wrong window, or cross a
  security boundary.
- **S2.** Wrong behaviour that has a workaround, or a diagnostic that misleads.
- **S3.** Debt, polish, copy.

**Disposition, one per finding.**

- **Fix in 0.13.1.**
- **Defer to 0.14.0.** Only when wave B rewrites the code in question, so a patch would be thrown away.
- **Won't fix.** With the reason written down.

**What a patch release may not contain.** No macro schema change. No change to the bridge wire. No
new manifest capability, because a capability change asks every clan install for consent on update.
A finding that needs any of these is deferred by rule, however severe, and gets a mitigation if one
exists.

**Exit gate:** Este reads the list and approves the dispositions. No fix lands before this.

---

## Phase 3: remediate and ship

Branch `release/0.13.1` off main after Phase 0 merges.

- **One finding, one commit, test first.** The failing test is the proof the finding was real.
- **Bulk tier for well-specified fixes**, session model verifies each worker report against the diff
  and a test run. S1 fixes stay on the session model.
- **Order:** S1, then S2, then whatever S3 is cheap. Stop adding when the list is done, not when
  ideas run out.

**Release gates, all of them.**

1. `dotnet test ... -p:StandaloneTestsOnly=true` green.
2. `dotnet test ...` with the real host green, locally and in CI.
3. `rororo-ur-task.csproj` and `manifest.json` agree on 0.13.1.
4. CHANGELOG entry, dated.
5. Live smoke on Dunder against the installed host, with a checklist in `docs/smoke/`. Includes the
   two checks 0.13.0 left open (K9), now reachable because the permissions are granted.
6. Release cut through `/cut-release`, with real notes.
7. Same session: dashboard version, a decision entry, and the memory line.

**After the ship:** close or move the dashboard tasks the review settled, and move the PS99 game
tasks to the PS99 Ops project, where their evidence lives.

---

## What this plan does not do

- **No host edits.** If the contract needs to change, that is a host decision with its own release.
- **No wave B work.** The autoclicker starts after 0.13.1 ships.
- **No refactor for its own sake.** `AssignmentRunner.cs` is 1,638 lines and `StepRunner.cs` is
  1,110. Size alone is not a finding. A bug that the size is hiding is.
