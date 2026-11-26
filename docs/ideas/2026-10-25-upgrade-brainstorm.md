# Ur Task upgrades: what four lenses agreed on

Brainstorm, 2026-10-25, after v0.12.0. Four workers each took one lens (timing and scheduling, a smart autoclicker, reliability across many accounts, authoring and the agent surface), read the code first, and cited real incidents from a month of daily multi-account use. 46 ideas came back. They collapse into five shared building blocks and four waves.

Nothing here is decided. This is the menu.

## The signal: keep-alive is the top pain, three times over

Three of the four lenses ranked a keep-alive fix first without seeing each other's work. The incident behind it: a long repeating click run inside Ur Task held the foreground, keep-alive never got a turn, and three accounts were idle-kicked while the log still said keep-alive was running. A separate stray Esc stopped keep-alive for 9.5 hours. Today nothing tells you, or an agent, the truth about keep-alive except a log line, and the log line was wrong.

Confirmed gaps in the code:
- the bridge repeat loop in `MacroRunInvoker` (around line 276) never consults the cadence scheduler;
- keep-alive state lives in memory only (`ObservedPassCostMs`, `DueAtMs`), with no per-tap log line and no bridge call;
- every keep-alive starts due at the same moment (`DueAtMs = ClockMs()` in `AssignmentRunner`);
- `RunMacro.Repeat` is a bare bool, so a bounded run means an agent polls and stops it by hand;
- `WaitStep` is a fixed delay;
- nothing in `src/` knows about the client's 20 FPS cap.

## Five building blocks most ideas share

| Block | What it is | Ideas that ride on it |
|---|---|---|
| **Keep-alive ledger** | Per alt: when input last actually landed, next due, state (running, paused by whom, off), one log line per tap | Honest status call, pause leases, per-account off switch, idle-age column, projected-kick warning |
| **Yield point** | A safe moment in a run (between passes, or a marked step) where a due keep-alive gets serviced and focus comes back | Repeat loops that don't starve keep-alive, long macros, the autoclicker, run plans |
| **Condition evaluator** | One "does this box show colour X / outline / not X" check with an edge (appears, disappears), consecutive-poll debounce and a hover-safe sample spot | Click until, click only while lit, covered-spot guard, until and if steps, toggle step, expect-after retry, assertions |
| **Own-input stamp** | Mark every `SendInput` Ur Task sends (`dwExtraInfo`) so it can tell its input from a human's | Recorder stops capturing its own clicks, autoclicker pause on grab, "user has the wheel" detection |
| **Bounds** | `maxPasses`, `maxSeconds`, `untilLocalTime`, and a required cap on anything that loops | Run windows, autoclicker caps, until steps, bridge repeats an agent can't forget to stop |

Build those five and most of the list becomes small.

## Wave 1: keep-alive you can trust (S to M, low risk)

1. **Ledger + per-tap log line + `GetStatus` / `GetKeepAlive` bridge call.** Per account: last input landed (stamped only when `SendInput` returned the expected count and focus was confirmed), next due, consecutive failures, projected gap. The MCP gets one tool. The hotkey toast says the resulting state ("Keep-alive on, 7 accounts"). Fixes "the log says running" for good.
2. **Repeat-boundary yield.** The bridge repeat loop checks the scheduler between passes and services anything due. A few lines; removes the click-run starvation class.
3. **Pause leases.** Anything that pauses keep-alive (a macro, an agent, break-glass) does it with an owner and a deadline, and keep-alive re-arms on expiry, completion, abort or crash. "Paused by X, resumes in 4:10" in the UI.
4. **Per-account keep-alive off (and "no input") with a mandatory expiry.** For an account someone else is sitting on, or a parked booth. `RunMacro` against a held account is refused with a reason, the same shape as break-glass. Ships after the ledger, so a forgotten hold shows as a projected kick instead of a silent one.

## Wave 2: the smart autoclicker

OP Auto Clicker parity, built around keep-alive from day one so it can't recreate the incident above.

1. **Repeat engine.** Button or key, interval with jitter, count or duration cap, click or hold, left/right/middle, fixed point (client-space, follows the window) or cursor. Interval floor about 50 ms: the client renders at 20 FPS, so faster clicks are dropped anyway, and the UI should say so.
2. **Ctrl+Shift+C toggle.** Arm and disarm at the pointer's window, no macro needed, with a status chip (armed, target, clicks sent). Refuses to fire while Ctrl or Shift is physically held, or every click becomes Ctrl+Shift+click. The F12 abort also disarms it.
3. **Focus guard.** Before every click, the foreground must be the target. On mismatch: stop (default for agent runs) or pause until it's back (default for the hotkey). Grabbing the mouse pauses it, using the own-input stamp.
4. **Credit and yield.** Clicks into an alt count as that alt's input, so a single-window run never needs a keep-alive. A multi-window run yields at the shortest deadline.
5. **Smart conditions** (the condition evaluator): *click until* a colour appears or disappears, *click only while* a button is lit (with an idle timeout so a night run doesn't click a dead screen forever), *stop if covered* by a popup or captcha (up to three guard boxes). Every ending says why: `count | duration | untilColor | gateIdle | guard | focusLoss | abort`.
6. **Bridge calls** `StartRepeat` / `StopRepeat` / `GetRepeat`, and a read-only `ProbeColor` so an agent can build the condition from a live sample. The bridge rejects a repeat with no cap.

Later in this wave: multi-spot cycles (round-robin, first-lit, random-in-box), retry-until-next-screen, and adaptive cadence with a one-shot "calibrate" that finds the fastest interval that still lands. That last one replaces the hand-run speed tests. Note: the 240 / 100 / 60 ms comparison quoted in the brief has no measured source, so calibrate is how to get a real number.

## Wave 3: steps that check the screen

1. **Toggle step.** "Make X on": read the state, press only if it differs, re-read to confirm, stop with `toggle-stuck` if it didn't take; stop on an unknown colour instead of guessing. The recorder can sample both states from two captures. Answers "a lock macro turned the feature off".
2. **Until / If steps.** `until <check> do <steps> (max N or M ms)`, e.g. tap W until the E prompt shows instead of a timed walk; `if <check> then … else …` for the usual detours (a panel left open, a popup). Bounds are mandatory; nesting capped at 2.
3. **Macro guard + assertions.** Promote `ScreenGuard` from ClearAt-only to saved macros: a box that must hold for the whole macro, checked before every click, plus "this panel must be open now" assertions. A failure names the point, the expected and the sampled colour, so after a game update you re-aim one point instead of re-recording.
4. **Expect-after with retries** on a point: click, confirm the next screen, retry a bounded number of times. Retries default to 0 on buy-like steps, so a slow screen never double-spends.

## Wave 4: an agent can author and verify a macro

1. **Stable failure reasons** with `actor` (user, game, plugin) and `advice` (stop-and-ask, retry-once, re-record, wait). Fixes issue #39 (a load failure comes back as a bare `unknown-macro`). S.
2. **Folder watcher + duplicate-id quarantine.** The library refreshes when files land on disk, and two files claiming one id are quarantined, never silently overwritten. S.
3. **`ValidateMacro` / `SaveMacro` / `DescribeMacro`.** Validate returns the validator's sentences, estimated time and point ids and writes nothing; Save goes through `MacroStore` with `onConflict: refuse | newId | replaceWithBackup`; Describe returns a numbered plain-English step list. M.
4. **Step trace + capture-at-step** on `GetPlayback`: per step, pressed / skipped / check failed, with expected vs sampled colour; optional client-rect PNG at chosen steps, kept local and pruned. M.
5. **Dry run.** Resolve everything and sample every check without pressing anything. It must say "n/a" for checks after the first step that would change the screen, or it's false confidence. M. (Step-by-step playback, the "hold" feature, is L.)
6. **Macro arguments + paste step.** `{{item}}`, `{{price}}` passed by `RunMacro`, so one macro replaces a generator's worth of copies; a paste step (clipboard, Ctrl+V, restore clipboard) fills search boxes in one beat. Paste alone is about a day.
7. **Call-macro step.** Write the transport/camera reset once; every route starts by calling it. Cycle check and depth cap.
8. **Run windows and wait-until** on `RunMacro` and recipes: for 40 minutes, until 11:45, N passes, or every 2 h from :05.

## Small wins to slot in anywhere

- **Tap-floor lint**: flag key or click holds under about 50 ms (20 FPS). About a day; would have saved at least three failed runs. Grows into a linter: toggle-looking points with no check, check delays used as waits, the record hotkey's Ctrl leaking into a recording.
- **Recorder hygiene**: drop the record/stop hotkey's own keys, drop injected input, mark the focus click.
- **Paste step** (above).

## Later, or only on evidence

- **Per-account speed factor / profiles** for timed walks (accounts measured 16% apart, repeatable). Valuable, but until steps solve most of it without calibration.
- **Binding-aware "use item" keys** (one action, each account's own keybind) and **packs** with stable ids and update-in-place.
- **Preflight drift check** (sample every point's expected colour, press nothing) and a **queryable run journal**.
- **"User has the wheel" detection**: pause on real human input instead of failing. L and the riskiest surface; ship log-only first.
- **Resumable runs** from author-declared checkpoints (round back, never forward).
- **Observed timings and ETAs** from real runs; **run plans** (a timeline of windows); **keep-alive stagger** (only if the turbo live test shows a gap problem); **adaptive per-alt settle** (touches the dropped-first-click failure, so gate it).
- **Background input experiment** (post messages to a background client). About twenty lines, and if it works it changes the whole model (no focus theft, parallel alts). The cheapest high-information experiment on the list.

Rejected: humanised timing to look less like a macro. It's evasion, and the posture says no.

## Dependencies that matter

- Turbo keep-alive (PR #41) and break-glass F11 (uncommitted) are assumed by several ideas. Land them first, or at least decide.
- The disconnect/captcha screen detector and countdown-reading wait-until belong to Ur Golem (screen watching). Don't build a second screen-watching stack.
- Every new step kind needs a schema bump; the "refuse a newer macro" guard (0.9.1) already protects old installs.

Worker files with full sketches, class names and risks: `brainstorm-timing.md`, `brainstorm-autoclicker.md`, `brainstorm-reliability.md`, `brainstorm-authoring.md` (session scratchpad, 2026-10-25).
