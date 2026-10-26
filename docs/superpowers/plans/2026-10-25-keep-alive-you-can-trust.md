# Keep-alive you can trust (Ur Task 0.13.0, wave A) Implementation Plan

> **For agentic workers:** REQUIRED SUB-SKILL: Use superpowers:subagent-driven-development (recommended) or superpowers:executing-plans to implement this plan task-by-task. Steps use checkbox (`- [ ]`) syntax for tracking.

**Goal:** Keep-alive that logs every Space and every miss with its cause, schedules from each account's real idle time, pauses a running macro at a step boundary instead of fighting it, can be turned off per account, and is readable by agents through `GetStatus` and Ur MCP's `keepalive_status`.

**Architecture:** A `KeepAliveLedger` owned by `PluginRuntime` becomes the one source of truth per account (last landed input, limit, deadline, state, misses), fed by the runner, the step runner and RoRoRo's activity clock. The cadence runner schedules from ledger deadlines. A `ForegroundGate` arbitrates the foreground between playbacks and keep-alive: keep-alive asks for a break, the step runner parks at its next step boundary, keep-alive serves every account due within two minutes, and the playback resumes. The bridge reads ledger snapshots for `GetStatus`.

**Tech Stack:** C# / .NET 10 WPF plugin, xUnit, gRPC host contract `ROROROblox.PluginContract` 0.8.0 (NuGet), named-pipe JSON bridge (contract 1.x); sibling repo `rororo-ur-mcp` (.NET, ModelContextProtocol).

**Spec:** `docs/superpowers/specs/2026-10-25-keep-alive-you-can-trust-design.md` (approved 2026-10-25). Code map from research: the spec's "Left to the builder" list is decided below where tasks need a shared name.

**Branch:** `feat/0.13.0-wave-a`, cut from `spec/0.13.0-wave-a` (which sits on `release/0.12.1`, PR #44). Rebase onto `main` once #44 merges.

## Global Constraints

- Build `dotnet build rororo-ur-task.csproj`, never the `.sln`. Gate: `dotnet test tests/rororo-ur-task.Tests/rororo-ur-task.Tests.csproj -p:StandaloneTestsOnly=true`. The two `HotkeyServiceTests` fail while an installed Ur Task holds Ctrl+Shift+R; that is environmental, nothing else may fail.
- A running Ur Task locks `bin\`; compile-check with `-p:OutDir=<scratch>` when it is up (repo-relative tests then fail; ignore only those).
- Bridge contract stays 1.x: new methods and new optional trailing fields only. Existing reason codes keep their meaning, `refused` included (Ur OCR reruns on it).
- Log lines scripts parse stay byte-identical: `Playing assignments — …`, `Assignment loop stopped`, the break-glass ON/OFF lines, the 0.12.1 turbo `Space to X (N ms to front)` / `sweep done, n of N Space sent` wording.
- Safety invariant: no input is ever sent unless the foreground resolves to the target. Pausing, gating and crediting only withhold or record input.
- Only key input stamps the idle clock in 0.13 (keep-alive Space, turbo Space, macro key steps, paste). Mouse input never stamps.
- Limits: no-anti-AFK games 20 min, own-anti-AFK games 15 min (PS99 measured), unknown 18 min. Deadline = limit − 6 min. A per-PlaceId user override is read as the deadline (limit = override + 6); deadlines clamp to ≥ 2 min with a log line.
- Preempt a playback that cannot reach a step boundary at limit − 1 min.
- One break serves every account due within 2 min (`AssignmentRunner.TurboBatchWindowMs`).
- Per-account off: memory only, ends on toggle or Ur Task restart, readable by agents, not settable.
- Public repo: no account names or game playbook content in code, tests, commits or docs. Game names already in `KeepAliveIntervals` may stay.
- Commits: conventional, ending with the attribution trailer the session supplies.
- `rororo-ur-task.csproj` and `manifest.json` versions agree (0.13.0). Ur MCP: its csproj and manifest agree (0.5.0).

## Review Focus

1. **The user declines the two new host capabilities on update** (`host.queries.account-activity`, `host.commands.mark-account-active`): calls come back permission-denied. Expected: one log line, keep-alive runs on Ur Task's own clock, nothing stops. Test in Task 5.
2. **RoRoRo reports activity for an account Ur Task doesn't track, or a time in the future** (clock skew, a stale id). Expected: ignored, never moves a deadline later than now + deadline. Test in Task 5.
3. **A break is requested while the playback is ending, between passes, or already parked for another break.** Expected: no deadlock; a request against a playback that ends grants at once; a second request joins the first break. Test in Task 7.
4. **The paused macro's window closes during the break.** Expected: resume fails its foreground check and the playback ends with the existing aborted reason; the gate is free afterwards. Test in Task 8.
5. **Ur Task restarts while accounts sit near their deadline and RoRoRo has no activity for some.** Expected: tracked-by-host accounts are due at their real deadline; unknown ones are due now, a few seconds apart, never all in the same second. Test in Task 4.

---

## File Structure

| File | Responsibility |
|---|---|
| `src/Macros/KeepAliveIntervals.cs` → rename `src/Macros/KeepAlive/KeepAliveLimits.cs` | Limits table, game-name normalisation, deadline, override handling |
| `src/Macros/KeepAlive/KeepAliveLedger.cs` (new) | Per-account state, thread-safe, snapshots; the only writer of "last input" |
| `src/Macros/KeepAlive/KeepAliveTypes.cs` (new) | `InputSource`, `MissCause`, `Miss`, `AccountKeepAliveState`, `AccountLedgerEntry`, `KeepAliveLimit` |
| `src/Macros/KeepAlive/MissReporter.cs` (new) | Turns misses into log lines: per-account, all-at-once aggregation, recovery, expected-reset warning |
| `src/Macros/KeepAlive/ForegroundGate.cs` (new) | Playback ↔ keep-alive arbitration, pause/resume, preempt |
| `src/PluginHost/HostActivity.cs` (new) | `IHostActivity` + gRPC implementation over `PluginClient` |
| `src/PluginHost/Win32Focus.cs` | `FocusAttempt` with a cause; read `SetForegroundWindow` result |
| `src/Macros/AssignmentRunner.cs` | Schedule from the ledger; stamp, miss, recover; deadline watchdog; add/remove accounts live |
| `src/Macros/Steps/StepRunner.cs`, `src/Macros/MacroPlayer.cs`, `src/Macros/SequencePlayer.cs` | Gate boundaries; key-input stamps |
| `src/Ipc/Reasons.cs` (new), `src/Ipc/BridgeContract.cs`, `src/Ipc/MacroRunInvoker.cs`, `src/Ipc/MacroRunnerServer.cs`, `src/Ipc/StatusProvider.cs` (new) | Reason codes, actor/advice, #39, `GetStatus` |
| `src/PluginRuntime.cs`, `src/UI/AssignmentRow.cs`, `src/UI/RecorderViewModel.cs`, `src/UI/RecorderWindow.xaml` | Wiring, per-account off toggle, "next" from the ledger |
| `rororo-ur-mcp/Ipc/*`, `rororo-ur-mcp/Tools/KeepAliveTools.cs` (new) | `keepalive_status` |

---

### Task 1: Limits table and game-name matching

**Files:**
- Rename: `src/Macros/KeepAliveIntervals.cs` → `src/Macros/KeepAlive/KeepAliveLimits.cs`
- Create: `src/Macros/KeepAlive/KeepAliveTypes.cs` (only `KeepAliveLimit` in this task)
- Modify: `src/UI/RecorderViewModel.cs:1214` (caller), `src/Macros/AssignmentRunner.cs:54` (`CadenceDeps.Real`)
- Test: `tests/rororo-ur-task.Tests/KeepAliveLimitsTests.cs` (replace the old intervals tests' file if one exists)

**Interfaces:**
- Produces: `internal sealed record KeepAliveLimit(TimeSpan Limit, TimeSpan Deadline, bool Clamped)`; `internal static class KeepAliveLimits { public const int DeadlineMarginMinutes = 6; public const int MinDeadlineMinutes = 2; public const int PlatformIdleKickFloorMinutes = 20; public static KeepAliveLimit For(long? placeId, string? placeName, UserPreferences prefs); internal static string NormalizeGameName(string? name); }`

- [ ] **Step 1: Write the failing tests**

```csharp
[Theory]
[InlineData("🎉 [PSX] Pet Simulator 99! 🐾", 15, 9)]
[InlineData("Pet Simulator 99", 15, 9)]
[InlineData("Grow a Garden", 20, 14)]
[InlineData("[UPD] Grow a Garden 🌱", 20, 14)]
[InlineData("Some Unknown Game", 18, 12)]
[InlineData(null, 18, 12)]
public void Limit_and_deadline_by_game(string? name, int limitMin, int deadlineMin)
{
    var l = KeepAliveLimits.For(null, name, new UserPreferences());
    Assert.Equal((TimeSpan.FromMinutes(limitMin), TimeSpan.FromMinutes(deadlineMin), false), (l.Limit, l.Deadline, l.Clamped));
}

[Fact]
public void A_place_override_is_the_deadline_and_clamps_at_two_minutes()
{
    var prefs = new UserPreferences { KeepAliveOverridesByPlaceId = { [42] = 10, [43] = 1 } };
    Assert.Equal((TimeSpan.FromMinutes(16), TimeSpan.FromMinutes(10), false), Tuple(KeepAliveLimits.For(42, "x", prefs)));
    Assert.Equal((TimeSpan.FromMinutes(8), TimeSpan.FromMinutes(2), true), Tuple(KeepAliveLimits.For(43, "x", prefs)));
}

[Fact]
public void Normalize_drops_emoji_brackets_and_punctuation()
    => Assert.Equal("pet simulator 99", KeepAliveLimits.NormalizeGameName("🎉 [PSX] Pet Simulator 99! 🐾"));
```

(`Tuple` is a local helper returning `(l.Limit, l.Deadline, l.Clamped)`. Clamped override 1 → deadline 2, limit 2 + 6 = 8.)

- [ ] **Step 2: Run to verify they fail**

Run: `dotnet test tests/rororo-ur-task.Tests/rororo-ur-task.Tests.csproj -p:StandaloneTestsOnly=true --filter FullyQualifiedName~KeepAliveLimitsTests`
Expected: FAIL, `KeepAliveLimits` does not exist.

- [ ] **Step 3: Implement**

Normalise: lower-case, drop bracketed segments (`[...]`, `(...)`), keep only letters, digits and spaces, collapse spaces, trim. Rows: own-anti-AFK games (the old `BackstopGames` list) limit 15; no-anti-AFK (old `PrimaryKeeperGames`) limit 20; unknown 18. Keep the class doc's sourcing note, updated to "PS99 measured 2026-10-25: 15 to 16.5 min". Callers that needed a fire interval now use `.Deadline`. Delete `KeepAliveIntervals`; `WarnThresholdMinutes` and `CheckUnschedulableWarnings` move to compare against `Limit`.

- [ ] **Step 4: Run to verify they pass** (same command). Expected: PASS.

- [ ] **Step 5: Run the full gate, then commit**

```bash
git add -A src tests
git commit -m "feat(keep-alive): per-game limits and deadlines, PS99 measured at 15 min, names match through decoration"
```

---

### Task 2: The ledger

**Files:**
- Modify: `src/Macros/KeepAlive/KeepAliveTypes.cs`
- Create: `src/Macros/KeepAlive/KeepAliveLedger.cs`
- Test: `tests/rororo-ur-task.Tests/KeepAlive/KeepAliveLedgerTests.cs`

**Interfaces:**
- Consumes: `KeepAliveLimit` (Task 1), `AccountRegistry.AccountInfo`.
- Produces:
  - `internal enum InputSource { KeepAlive, Macro, Paste, Host }`
  - `internal enum MissCause { WindowGone, FocusRefused, OtherInFront, SendRejected, HeldOff }`
  - `internal sealed record Miss(MissCause Cause, string? Holder)` (Holder = `'<title>' (<process>)` or the hold reason)
  - `internal enum AccountKeepAliveState { Waiting, Serving, Off, Held, PausedForMacro }`
  - `internal sealed record AccountLedgerEntry(long UserId, string DisplayName, string AccountId, KeepAliveLimit Limit, long? LastInputMs, InputSource? LastSource, AccountKeepAliveState State, bool Off, int ConsecutiveMisses, Miss? LastMiss)`
  - `internal sealed class KeepAliveLedger(Func<long> monoMs, Func<DateTimeOffset> wallNow)` with: `void Track(AccountRegistry.AccountInfo alt, KeepAliveLimit limit)`, `void Untrack(long userId)`, `void StampLanded(long userId, InputSource source)`, `void MergeHostActivity(string accountId, DateTimeOffset lastActivity)`, `int RecordMiss(long userId, Miss miss)` (returns new consecutive count), `void SetState(long userId, AccountKeepAliveState state)`, `bool SetOff(long userId, bool off)`, `long DueAtMs(long userId)`, `AccountLedgerEntry? Get(long userId)`, `IReadOnlyList<AccountLedgerEntry> Snapshot()`, `DateTimeOffset ToWall(long monoMs)`.

- [ ] **Step 1: Write the failing tests** (fake clocks: `long now`, `DateTimeOffset wall = 2026-10-25T14:00-05:00` advanced together)

```csharp
[Fact] public void Due_is_last_input_plus_deadline()            // Track at t=0, StampLanded at t=60_000 → DueAtMs == 60_000 + 9*60_000 for a 15-min limit
[Fact] public void No_input_on_either_clock_means_due_now()     // Track only → DueAtMs == now
[Fact] public void Host_activity_counts_only_when_later()       // Stamp at wall 14:05; Merge 14:03 → unchanged; Merge 14:07 → LastInputMs moves to 7 min, LastSource Host
[Fact] public void Host_activity_in_the_future_is_capped_at_now() // Merge wall+5 min → LastInputMs == now
[Fact] public void Host_activity_for_an_untracked_account_is_ignored()
[Fact] public void Misses_count_up_and_a_landing_clears_them()  // RecordMiss twice → 2, Get().LastMiss set; StampLanded → 0, LastMiss null
[Fact] public void Off_is_kept_and_visible_in_the_snapshot()    // SetOff → State Off, Off true; DueAtMs unchanged
[Fact] public void Untrack_then_track_keeps_no_history()
```

Each body asserts the exact numbers in its comment.

- [ ] **Step 2: Run to verify they fail.** `--filter FullyQualifiedName~KeepAliveLedgerTests`. Expected: FAIL (types missing).

- [ ] **Step 3: Implement.** One `lock` around a `Dictionary<long, Entry>` plus `Dictionary<string,long>` accountId → userId; every public method takes the lock for O(1) work, never awaits, never calls out while holding it. `Snapshot()` returns immutable records. Wall ↔ mono conversion uses the pair of clocks sampled together.

- [ ] **Step 4: Run to verify they pass.** Expected: PASS.

- [ ] **Step 5: Commit** `feat(keep-alive): ledger of last landed input, deadlines, misses and off per account`

---

### Task 3: Focus attempts that say why

**Files:**
- Modify: `src/PluginHost/Win32Focus.cs:56-104`, `src/Macros/AssignmentRunner.cs:13-20` (CadenceDeps), test rigs in `tests/rororo-ur-task.Tests/CadenceRunnerTests.cs` and `TurboKeepAliveTests.cs`
- Test: `tests/rororo-ur-task.Tests/FocusAttemptTests.cs`

**Interfaces:**
- Consumes: `MissCause` (Task 2).
- Produces: `internal sealed record FocusAttempt(bool Ok, MissCause? Cause, string? Detail) { public static implicit operator FocusAttempt((bool ok, string? error) t); }` (the conversion maps a false tuple to `Cause = FocusRefused`, so existing rigs returning `(true, null)` / `(false, "x")` compile unchanged). `Win32Focus.AttachAndFocus(int pid) : FocusAttempt` returns `WindowGone` for a missing process or zero main window, `FocusRefused` when `SetForegroundWindow` returns false. `CadenceDeps.Focus : Func<int, FocusAttempt>`; `CadenceDeps.SendKeepAlive : Func<bool>` (real: `SendSpaceKeepAlive()`); `CadenceDeps.DescribeForeground : Func<string>` init property defaulting to `() => "unknown"` (real: a short `'<title>' (<process>)` built from `Win32Focus.DescribeForeground` data).

- [ ] **Step 1: Write the failing tests:** `Tuple_conversion_keeps_old_rigs_meaning` (`(false,"x")` → `Ok false, Cause FocusRefused, Detail "x"`), and in the runner rig a case where `SendKeepAlive` returns false: the alt's `ConsecutiveFocusFailures` goes to 1 and its due moves by `FocusRetryBackoffMs` (30 000), i.e. a rejected Space is a miss, not a success.
- [ ] **Step 2: Run, verify FAIL.**
- [ ] **Step 3: Implement.** Update `SendKeepAlive:` lambdas in the two rig files to `() => { …; return true; }` (6 sites).
- [ ] **Step 4: Run the full gate, verify PASS.**
- [ ] **Step 5: Commit** `refactor(keep-alive): focus and Space report why they failed`

---

### Task 4: The runner schedules from the ledger and explains itself

**Files:**
- Create: `src/Macros/KeepAlive/MissReporter.cs`
- Modify: `src/Macros/AssignmentRunner.cs` (ctor, `RunAsync` setup at :240-250, `ServiceKeepAliveAsync` :538-590, `ServiceKeepAliveSweepAsync`, `EmitFocusFailureWarning` :757)
- Test: `tests/rororo-ur-task.Tests/KeepAlive/RunnerLedgerTests.cs`, `MissReporterTests.cs`

**Interfaces:**
- Consumes: `KeepAliveLedger`, `FocusAttempt`, `Miss`, `CadenceDeps.SweepLog` (0.12.1).
- Produces: `AssignmentRunner(IMacroPlayer, IForegroundWatcher, CadenceDeps, KeepAliveLedger)` (the existing 3-arg internal ctor creates a private ledger so old tests run); `internal sealed class MissReporter(Action<string> log, Func<DateTimeOffset> wallNow)` with `void Landed(AccountLedgerEntry before, string name)`, `void Missed(string name, Miss miss, int consecutive)`, `void FlushMinute()` (emits the all-at-once line), `void ExpectReset(string name, DateTimeOffset at)`.

Decisions this task fixes:
- Initial `DueAtMs` = `ledger.DueAtMs(userId)`; accounts due now are spread `index * 2_000` ms apart.
- Normal path logs `keep-alive: Space to X (N ms to front)` on success, same shape as turbo.
- Miss line: `keep-alive: missed X, <cause text>; retry in 30s`, cause text per spec table (`window gone`, `Windows refused focus`, `'<title>' (<process>) was in front`, `Windows rejected the input`, `held off: <why>`).
- All-at-once: misses with the same `(Cause, Holder)` for every account due in one sweep or within 60 s collapse into one line `keep-alive: every due account missed: <cause text>`; per-account ledger still records each.
- Recovery: on a landing after ≥ 1 miss, `keep-alive: X landed again after N misses (M min since its last Space)`.
- Expected reset: when an account's idle reaches its deadline and its next retry falls after `limit`, `keep-alive: X has had no input for N min; expect a reset or kick around HH:mm`. Once per account per idle stretch.
- The 3-miss Warning keeps its text and appends ` Last cause: <cause text>.`

- [ ] **Step 1: Write the failing tests** (runner rig from `TurboKeepAliveTests`, ledger with fake clocks):
  - `Start_does_not_make_everyone_due_now`: ledger stamps A at −8 min (deadline 9), B never → first Focus is B at ~0, A at +1 min.
  - `Unknown_accounts_start_two_seconds_apart`: three untouched alts → focus times 0, 2 000, 4 000 (+ settle).
  - `A_landed_space_stamps_the_ledger_and_logs_a_line`.
  - `A_verify_miss_names_whats_in_front`: foreground resolves to nobody, `DescribeForeground` = `'Visual Studio Code' (Code)` → log contains `'Visual Studio Code' (Code) was in front`.
  - `Seven_alts_missing_for_one_cause_log_one_line` (assert exactly one `every due account missed` line and 7 ledger misses).
  - `Recovery_line_after_misses`.
  - `Expect_reset_line_when_retry_cannot_land_before_the_limit`.
- [ ] **Step 2: Run, verify FAIL.**
- [ ] **Step 3: Implement.** Runner writes through ledger only; `ScheduledAlt.ConsecutiveFocusFailures` reads from the ledger entry. Turbo keeps its 0.12.1 strings and also stamps / records misses.
- [ ] **Step 4: Full gate, PASS.** The 0.12.1 turbo log test must still pass unchanged.
- [ ] **Step 5: Commit** `feat(keep-alive): schedule from real idle, log every Space, miss and recovery with its cause`

---

### Task 5: RoRoRo's activity clock, both ways

**Files:**
- Create: `src/PluginHost/HostActivity.cs`
- Modify: `src/PluginHost/PluginClient.cs` (two calls), `src/PluginRuntime.cs` (30 s poll, wiring), `src/Macros/AssignmentRunner.cs` (mark after landing), `manifest.json` (capabilities)
- Test: `tests/rororo-ur-task.Tests/KeepAlive/HostActivityTests.cs`

**Interfaces:**
- Produces: `internal interface IHostActivity { Task<IReadOnlyList<(string AccountId, DateTimeOffset LastActivity)>?> GetAsync(CancellationToken ct); Task MarkActiveAsync(string accountId, CancellationToken ct); }` (null from `GetAsync` = unavailable); `HostActivity(PluginClient client, Action<string> log)`; `internal sealed class HostActivityPoller(IHostActivity host, KeepAliveLedger ledger, Action<string> log)` with `Task RunAsync(TimeSpan every, CancellationToken ct)`; `CadenceDeps.MarkActive : Action<string>` init property (no-op default; real fires `MarkActiveAsync` without awaiting).

- [ ] **Step 1: Write the failing tests:**
  - `Poll_merges_activity_into_the_ledger`.
  - `Permission_denied_logs_once_and_keeps_running` (fake throws `RpcException(StatusCode.PermissionDenied)` three polls → one log line containing `account-activity`, no throw, ledger untouched).
  - `Unknown_ids_and_future_times_are_ignored` (Review Focus 2).
  - `A_landed_space_marks_the_account_active` (runner rig: `MarkActive` receives the alt's `AccountId` once per landing, never on a miss).
- [ ] **Step 2: Run, verify FAIL.**
- [ ] **Step 3: Implement.** `PluginClient.GetAccountActivityAsync` / `MarkAccountActiveAsync` wrap the generated client; `HostActivity` maps `last_activity_unix_ms`. Add `"host.queries.account-activity"` and `"host.commands.mark-account-active"` to `manifest.json` capabilities.
- [ ] **Step 4: Full gate, PASS.**
- [ ] **Step 5: Commit** `feat(keep-alive): read RoRoRo's activity clock and credit it after every Space`

---

### Task 6: Macro key input credits the idle clock

**Files:**
- Modify: `src/Macros/Steps/StepRunner.cs` (`StepContext`, `KeyStep` case, `PlayPasteAsync`, raw-step key events), `src/Macros/MacroPlayer.cs:348` (ctx), event-list key path in `MacroPlayer`
- Test: `tests/rororo-ur-task.Tests/Steps/KeyCreditTests.cs`

**Interfaces:**
- Produces: `StepContext.OnKeyLanded : Action<InputSource>?` (new optional trailing record parameter, default null). `MacroPlayer` gets an optional `KeepAliveLedger?` and passes `src => ledger.StampLanded(targetUserId, src)`.

- [ ] **Step 1: Write the failing tests:** key step down sent → one `Macro` credit; paste → one `Paste` credit after Ctrl+V; point/drag/wheel/sweep steps → no credit; a key send that returns false (`FakeIo.SendWorks = false`) → no credit.
- [ ] **Step 2: Run, verify FAIL.**
- [ ] **Step 3: Implement.** Credit only after `SendGuarded` succeeds for a key-down.
- [ ] **Step 4: Full gate, PASS.**
- [ ] **Step 5: Commit** `feat(keep-alive): a macro's key presses reset its account's idle clock`

---

### Task 7: The foreground gate and pause at step boundaries

**Files:**
- Create: `src/Macros/KeepAlive/ForegroundGate.cs`
- Modify: `src/Macros/Steps/StepRunner.cs` (boundary call at top of the step loop :124, held keys), `src/Macros/SequencePlayer.cs` (boundary between targets and passes), `src/Macros/MacroPlayer.cs` (enter/exit playback), `src/Ipc/Reasons.cs` is created in Task 10; use the literal `"keep-alive-preempted"` here and swap in Task 10
- Test: `tests/rororo-ur-task.Tests/KeepAlive/ForegroundGateTests.cs`, `tests/rororo-ur-task.Tests/Steps/PauseResumeTests.cs`

**Interfaces:**
- Produces:
  - `internal sealed class ForegroundGate(Func<long> monoMs, Action<string> log)`
  - `IDisposable EnterPlayback(long targetUserId, string label)` (one at a time; a second waits for the first)
  - `Task AtBoundaryAsync(int stepIndex, int stepCount, CancellationToken ct)` (returns at once unless a break is pending; when pending, marks parked, awaits the lease's release, then returns)
  - `Task<IDisposable> RequestBreakAsync(string forAccount, TimeSpan preemptAfter, CancellationToken ct)` (grants immediately when no playback is in; otherwise when the playback parks or exits; after `preemptAfter`, cancels the playback through `PreemptToken` and grants)
  - `CancellationToken PreemptToken` (StepRunner links it into its run token)
  - `(bool Paused, string? For, long? PlaybackTarget) Status`
  - `StepContext.Gate : ForegroundGate?` (optional trailing)
- Log lines: `paused at step N/M for <account>`, `resumed at step N/M (paused X s)`.

- [ ] **Step 1: Write the failing tests:**
  - `Break_with_no_playback_grants_at_once`.
  - `Break_waits_for_the_boundary_and_the_step_finishes_first` (StepRunner with a 2-step macro, a hold step with `MaxMs` 1 000 first; request at t=100 → grant at ≥ 1 000; step 2 runs only after lease disposed).
  - `Resume_continues_at_the_same_step_nothing_replayed` (sent events before + after == the unpaused run's events).
  - `Keys_held_across_the_boundary_are_released_and_pressed_again`.
  - `A_step_longer_than_the_runway_is_preempted` (open hold, `preemptAfter` 500 → playback ends `Aborted` with `Reason` `keep-alive-preempted`, held button released).
  - Review Focus 3: `Request_against_an_ending_playback_grants`, `Second_request_joins_the_first_break`.
  - `Event_list_macros_pause_only_between_passes` (SequencePlayer).
- [ ] **Step 2: Run, verify FAIL.**
- [ ] **Step 3: Implement.** Gate state under one lock; `TaskCompletionSource` for parked and released, created with `RunContinuationsAsynchronously`. After resume, StepRunner's existing per-step foreground check runs before any input (no new input path).
- [ ] **Step 4: Full gate, PASS.**
- [ ] **Step 5: Commit** `feat(steps): playbacks pause at a step boundary for keep-alive and pick up where they stopped`

---

### Task 8: Keep-alive takes its turn through the gate

**Files:**
- Modify: `src/Macros/AssignmentRunner.cs` (service paths go through the gate; deadline watchdog during Active passes), `src/PluginRuntime.cs` (one gate shared by `_player`, `_sequence`, `_runner`)
- Test: `tests/rororo-ur-task.Tests/KeepAlive/TakeoverTests.cs`

**Interfaces:**
- Consumes: `ForegroundGate`, `KeepAliveLedger`, the 0.12.1 sweep (`ServiceKeepAliveSweepAsync`).
- Produces: `AssignmentRunner` ctor gains `ForegroundGate gate` (rig default: a fresh gate). A break always serves every keep-alive alt due within `TurboBatchWindowMs` through the sweep path (countdown and input guard included), whether or not turbo is ticked; outside a break, turbo off keeps the old one-alt path. Preempt runway = `limit − 1 min − now` for the most urgent alt.

- [ ] **Step 1: Write the failing tests:**
  - `A_bridge_playback_on_another_account_pauses_for_a_due_alt` (fake player parks at boundary; assert order `paused` → `focus alt` → `tap` → `restore playback window` → `resumed`).
  - `An_alt_playing_its_own_macro_is_never_paused_for_itself` (its key credit keeps it from coming due).
  - `Keep_alive_never_takes_the_foreground_mid_step` (no Focus call while the fake step is in flight).
  - Review Focus 4: `Paused_macro_window_closes_during_the_break` (after release, the fake player reports aborted; gate `Status.Paused` is false; next due service runs).
  - `Active_pass_is_paused_by_the_watchdog` (Active alt's long pass, keep-alive alt hits deadline mid-pass → served without waiting for the pass to end).
- [ ] **Step 2: Run, verify FAIL.**
- [ ] **Step 3: Implement.** The watchdog is a `Task` started by `RunAsync`, ticking each second while a playback is in, reading `ledger.DueAtMs`; it and the main loop share one `SemaphoreSlim(1)` so only one service runs. `PausedForMacro` state is set on the paused playback's account for the length of the break.
- [ ] **Step 4: Full gate, PASS.**
- [ ] **Step 5: Commit** `feat(keep-alive): due accounts are served through the gate, pausing other playbacks instead of fighting them`

---

### Task 9: Per-account off, live membership, and the "next" shown

**Files:**
- Modify: `src/Macros/AssignmentRunner.cs` (`AddAssignment`, `RemoveAssignment`), `src/PluginRuntime.cs` (AccountAdded/Removed → runner when running; break-glass OFF and Ctrl+Shift+P use live membership instead of rebuilding), `src/UI/AssignmentRow.cs`, `src/UI/RecorderViewModel.cs` (drop `_keepAliveDueAtMs` seeding, read `ledger.DueAtMs`), `src/UI/RecorderWindow.xaml` (toggle)
- Test: `tests/rororo-ur-task.Tests/KeepAlive/MembershipTests.cs`, `tests/rororo-ur-task.Tests/UI/AssignmentRowTests.cs`

**Interfaces:**
- Produces: `void AssignmentRunner.AddAssignment(Assignment a)`, `void RemoveAssignment(long robloxUserId)`; `AssignmentRow.KeepAliveOff : bool` (two-way bound, calls `ledger.SetOff`); `AssignmentRow.NextDueText` reads the ledger (`next: 7m`, `off`, `paused for <account>`, `held` under break-glass).

- [ ] **Step 1: Write the failing tests:**
  - `An_off_account_is_never_focused_and_its_clock_runs` (rig, 30 simulated minutes: no Focus for it; `Snapshot` shows `Off` with idle growing).
  - `An_account_launched_mid_loop_joins_as_keep_alive`; `…unless_it_was_toggled_off_before`.
  - `Breakglass_off_does_not_add_accounts_that_were_not_in_the_loop` (the 0.12.1 live finding: an account launched during break-glass that the user set to a non-keep-alive role stays out).
  - `Off_resets_on_a_new_ledger` (restart semantics: a new `KeepAliveLedger` has no offs).
  - `Next_due_text_comes_from_the_ledger` (stamp at −5 min, deadline 9 → `next: 4m`).
  - `Breakglass_holds_every_account_and_says_so`: break-glass ON sets every tracked entry to `Held`; an account reaching its deadline meanwhile records `Miss(HeldOff, "break-glass")` and the expected-reset line fires when due; OFF returns them to `Waiting`.
- [ ] **Step 2: Run, verify FAIL.**
- [ ] **Step 3: Implement.** Place the toggle in the row next to the role dropdown, label `Keep-alive off`, sentence case, no emoji.
- [ ] **Step 4: Full gate, PASS.**
- [ ] **Step 5: Commit** `feat(keep-alive): turn keep-alive off per account; accounts join the running loop; next-due from the ledger`

---

### Task 10: Reason codes, actor and advice, and #39

**Files:**
- Create: `src/Ipc/Reasons.cs`
- Modify: `src/Ipc/BridgeContract.cs` (`RunMacroResponse`, `GetPlaybackResponse`: optional trailing `string? Actor = null, string? Advice = null`), `src/Ipc/MacroRunInvoker.cs` (all `Refused("…")` literals → `Reasons.*`; loadMacros keeps failures), `src/Ipc/MacroRunnerServer.cs`, `src/Ipc/PlaybackRegistry.cs`, `src/Hotkeys/Breakglass.cs` (RefusalReason → `Reasons.Breakglass`), `src/Macros/MacroStore.cs` (nothing new; `LoadResult.Failures` is consumed)
- Test: `tests/rororo-ur-task.Tests/Ipc/ReasonsTests.cs`

**Interfaces:**
- Produces: `internal static class Reasons { Busy = "busy", Refused = "refused", UnknownMacro = "unknown-macro", NoTargetsResolved = "no-targets-resolved", UnknownPlayback = "unknown-playback", VersionMismatch = "version-mismatch", Breakglass = "breakglass", MacroUnreadable = "macro-unreadable", KeepAlivePreempted = "keep-alive-preempted", CheckFailed = "check-failed", Aborted = "aborted", Error = "error", Skipped = "skipped" }`; `internal static class Actors { User = "user", KeepAlive = "keep-alive", Host = "host", UrTask = "ur-task"; static string Agent(string callerPluginId) => $"agent:{callerPluginId}"; }`. `MacroRunInvoker` production ctor's `loadMacros` becomes `Func<LoadResult>`.

- [ ] **Step 1: Write the failing tests:**
  - `Every_existing_code_is_unchanged` (assert each constant's string, so a rename fails loudly).
  - `A_macro_whose_file_failed_to_load_is_macro_unreadable` (fake load result with a failure for `<id>.json` whose reason is the newer-schema sentence → `Reason "macro-unreadable"`, `Detail` that sentence, `Advice "Update Ur Task to use it."`).
  - `Old_shapes_are_unchanged_when_actor_and_advice_are_null` (serialize `RunMacroResponse.Refused("busy")` → JSON has no `actor`/`advice` keys).
  - `Breakglass_refusal_names_the_user_as_actor`.
  - `Preempted_playback_reports_keep_alive_as_actor`.
- [ ] **Step 2: Run, verify FAIL.**
- [ ] **Step 3: Implement.** ListMacros and the recipe lookups (`PluginRuntime.cs:444,525`) log each distinct load failure once.
- [ ] **Step 4: Full gate, PASS.**
- [ ] **Step 5: Commit** `feat(bridge): stable reason codes with actor and advice; unreadable macros say why (#39)`

---

### Task 11: GetStatus on the bridge

**Files:**
- Create: `src/Ipc/StatusProvider.cs`
- Modify: `src/Ipc/BridgeContract.cs` (records, `MethodGetStatus = "GetStatus"`), `src/Ipc/MacroRunnerServer.cs` (ctor `IStatusProvider? status = null`, dispatch case), `src/Ipc/MacroRunInvoker.cs` (RunMacro `Note`), `src/PluginRuntime.cs` (wiring)
- Test: `tests/rororo-ur-task.Tests/Ipc/GetStatusTests.cs`

**Interfaces:**
- Produces:
  - `GetStatusRequest(string ContractVersion, string Method, string? CallerPluginId)`
  - `GetStatusResponse(bool Ok, KeepAliveStatus? KeepAlive, IReadOnlyList<AccountStatus>? Accounts, IReadOnlyList<PlaybackStatus>? Playbacks, string? Reason, string? Detail)`
  - `KeepAliveStatus(string State /* running|stopped|breakglass */, bool Turbo)`
  - `AccountStatus(string UserId, string Name, string Role /* active|keep-alive|off */, string? LastInputAt, string? LastInputSource /* keep-alive|macro|paste|host */, int? IdleSeconds, string? DeadlineAt, int LimitMinutes, int Misses, string? LastMiss, string State /* waiting|serving|off|held|paused-for-macro */)` (times: local ISO 8601 with offset, `DateTimeOffset.ToString("O")`)
  - `PlaybackStatus(string PlaybackId, bool Paused, string? PausedFor)`
  - `RunMacroResponse` gains optional trailing `string? Note = null`: `"This run (about N min) crosses <account>'s keep-alive deadline at HH:mm; it will pause there for a Space."` when `Macro.Duration` from now passes any other tracked account's deadline.
  - `internal interface IStatusProvider { GetStatusResponse GetStatus(); }`
- [ ] **Step 1: Write the failing tests:** JSON round trip with exact camelCase keys (`keepAlive`, `accounts`, `lastInputAt`, `deadlineAt`, `pausedFor`); an `off` account appears with role `off`; `breakglass` state while break-glass is on; missing `callerPluginId` → `refused`; an old-style unknown method still answers `refused` / `Unknown method`; RunMacro note present only when a deadline is crossed.
- [ ] **Step 2: Run, verify FAIL.**
- [ ] **Step 3: Implement.** `StatusProvider` reads `ledger.Snapshot()`, runner state, break-glass, prefs turbo, `PlaybackRegistry`, `gate.Status`; never blocks.
- [ ] **Step 4: Full gate, PASS.**
- [ ] **Step 5: Commit** `feat(bridge): GetStatus reports keep-alive, every account's idle and deadline, and paused playbacks`

---

### Task 12: Ur MCP `keepalive_status` (repo `rororo-ur-mcp`)

**Files:** (in the sibling checkout `../rororo-ur-mcp`, branch `feat/keepalive-status` off `main` at 0.4.1)
- Modify: `Ipc/IUrTaskBridge.cs`, `Ipc/UrTaskBridgeClient.cs` (wire records mirroring Task 11, `GetStatusAsync`), `tests/rororo-ur-mcp.Tests/Fakes.cs`, `CHANGELOG.md`, `rororo-ur-mcp.csproj` + `manifest.json` → 0.5.0
- Create: `Tools/KeepAliveTools.cs`, `tests/rororo-ur-mcp.Tests/KeepAliveToolsTests.cs`

**Interfaces:**
- Consumes: Task 11's JSON shape exactly.
- Produces: `Task<BridgeStatus> GetStatusAsync(CancellationToken ct = default)`; tool `keepalive_status`, description: "Keep-alive state in Ur Task: whether it is running, and for each account its idle time, the time by which it needs input (its deadline), misses, and whether keep-alive is off for it. Use it before a long macro run to see which accounts need a Space and when."

- [ ] **Step 1: Write the failing tests:** tool renders one line per account `Name: idle 6m, needs input by 15:42 (keep-alive), misses 0`; `off` accounts say `keep-alive off`; a bridge answering `refused` / `Unknown method 'GetStatus'` renders `Update Ur Task to 0.13.0 or later to see keep-alive status.`
- [ ] **Step 2: Run** `dotnet test tests/rororo-ur-mcp.Tests` → FAIL.
- [ ] **Step 3: Implement.**
- [ ] **Step 4: Run** → PASS.
- [ ] **Step 5: Commit** `feat: keepalive_status reads Ur Task's GetStatus` and `chore(release): 0.5.0`

---

### Task 13: Version, changelog, smoke checklist

**Files:**
- Modify: `rororo-ur-task.csproj`, `manifest.json` (0.13.0), `CHANGELOG.md` (`## 0.13.0 — unreleased`)
- Create: `docs/smoke/<date>-0.13.0-smoke.md`

- [ ] **Step 1:** Changelog entries in the 626 voice (second person, sentence case), one per user-visible change: real-idle scheduling, explained misses and recovery lines, pause and pick up, per-account off, accounts joining the running loop, `GetStatus` / `keepalive_status`, `macro-unreadable`, the two new permissions RoRoRo asks for and what happens if declined.
- [ ] **Step 2:** Smoke checklist = the spec's "Live check before tagging" list, plus: install over 0.12.1 and note what RoRoRo shows for the two new permissions (Review Focus 1); decline them once and confirm keep-alive still runs.
- [ ] **Step 3:** Full gate; `dotnet build rororo-ur-task.csproj` 0 errors.
- [ ] **Step 4: Commit** `chore(release): version 0.13.0 and smoke checklist`
