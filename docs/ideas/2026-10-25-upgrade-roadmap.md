# Ur Task upgrade roadmap: four waves, one release each

Decided 2026-10-25: all four waves from `2026-10-25-upgrade-brainstorm.md`, one at a time, stacked so each wave lands on the pieces the one before built. Each release goes through spec, plan and build, ends with a live check on real accounts, and ships as its own tagged release.

## Order, and why this order

| Release | Wave | Ships | Builds the shared piece |
|---|---|---|---|
| **0.12.1** | Prereqs + quick wins | Break-glass Ctrl+Shift+F11 (built, uncommitted). Turbo keep-alive (#41) after its live test. Paste step. Record hotkey's Ctrl no longer leaks into recordings. | (none) |
| **0.13.0** | A: keep-alive you can trust | Keep-alive ledger and per-tap log line. `GetStatus` bridge call + MCP tool. Repeat loops yield to a due keep-alive. Pause leases. Per-account keep-alive off / no-input with expiry. Stable failure reasons with `actor` and `advice` (fixes #39). | **Ledger**, **yield point**, **reason vocabulary** |
| **0.14.0** | B: smart autoclicker | Repeat engine (interval, jitter, caps, click/hold, fixed point or cursor, 50 ms floor). Ctrl+Shift+C toggle. Focus guard and pause-on-grab. Click credit for keep-alive. Click until / click while lit / stop if covered. `StartRepeat`/`StopRepeat`/`GetRepeat`/`ProbeColor`. Tap-floor lint. | **Own-input stamp**, **condition evaluator**, **bounds** |
| **0.15.0** | C: steps that check the screen | Toggle step. Until and If steps. Macro guard and assertions (ScreenGuard on saved macros). Expect-after with retries. Macro schema v5. | Reuses the condition evaluator from B |
| **0.16.0** | D: agent authoring | `ValidateMacro`/`SaveMacro`/`DescribeMacro`. Folder watcher + duplicate-id quarantine. Step trace + capture-at-step. Dry run. Macro arguments. Call-macro step. Run windows and wait-until on `RunMacro` and recipes. | Reuses bounds, conditions, reasons |

**A before B:** B's clicker has to hand the foreground to keep-alive and credit its own clicks as input. That machinery is A's ledger and yield point. B without A recreates the 10-07 incident.

**B before C:** the condition evaluator gets proven on the simplest consumer first (one spot, one colour, a repeat loop). C turns it into step kinds inside saved macros, which needs a schema bump, so it should land on a proven evaluator.

**C before D:** D's dry run and trace report on checks, and its arguments and call-macro work across the step kinds C adds. An agent authoring macros gets the most from D once until/if/toggle exist.

## Rules for every release

- One spec and plan per release under `docs/superpowers/`. Build subagent-driven (Opus session, Sonnet workers), and Opus verifies the worker reports.
- Additive on bridge contract 1.0. New step kinds bump the macro schema; the 0.9.1 "refuse a newer macro" guard protects old installs.
- Ur MCP (`rororo-ur-mcp`) releases alongside A (status tool), B (repeat tools) and D (authoring tools).
- Live check on real accounts before tagging: keep-alive runs on everyone, and nothing gets idle-kicked during the check.
- Cut the release with real notes (`cut-release`), csproj and manifest versions agree, and record the ship the same session.

## Open questions, settled per spec

- **Ur Golem merge** (Ur OCR into Ur Task). The condition evaluator from B is a natural core for Golem's screen watching. Decide at the B spec whether Golem lands between C and D, or stays separate until after D.
- **Background input experiment** (post messages to a background client). About twenty lines; if it works, it changes B and D (no focus theft). Worth running before the B spec.
- **Turbo (#41)** needs its live test (Notepad typing smoke test, dropped-Space check in `ur-task.log`) before 0.12.1 can ship it.
- The later items (speed profiles, bindings, packs, preflight, journal, wheel detection, resumable runs, timings, run plans) are re-ranked after 0.16.0 on fresh evidence.
