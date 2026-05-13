# RoRoRo Ur Task v0.2 — Stopping point, evening of 2026-05-12

> Paste this whole file as the opening prompt for the next session.

## TL;DR

Long afternoon session: rc9 → **rc15** in one stretch. Multiple major redesigns: pairing model flipped from 1:1 to one-to-many, PLAY/STOP turned into a real toggle, zombie-plugin fix, keyboard-only recording default, visual treatment for active-painter and active-alt-row. Awaiting Este's smoke of **rc15** to validate the last visual fixes (rc14 shipped them but they didn't land due to a WPF property-precedence quirk; rc15 refactors with DataTrigger pattern).

If rc15 visuals land, v0.2.0 stable can ship next session.

## Current state

### Branch + tags

- `v0.2-build` at HEAD `018d17f` (rc15)
- PR #2 still open against main
- Tags pushed: `v0.2.0-rc1` through `v0.2.0-rc15`
- Tests: **25/25 passing** (`AssignmentMapTests` x7, `AssignmentRunnerTests` x3, `SequencePlayerTests` x4, `HotkeyServiceTests` x3, `PlaybackTargetPickerViewModelTests` x5, `MacroV1MigrationTests` x3)

### Latest install URL

```
https://github.com/estevanhernandez-stack-ed/rororo-ur-task/releases/download/v0.2.0-rc15/
```

### Dev RoRoRo binary

```
C:\Users\estev\Projects\ROROROblox\src\ROROROblox.App\bin\Debug\net10.0-windows\ROROROblox.App.exe
```

Launch via `Start-Process`. Currently up at end of session.

### Installed plugin state at session pause

rc15 installed at `C:\Users\estev\AppData\Local\ROROROblox\plugins\626labs.ur-task\`. Autostart was toggled ON. Plugin auto-launched on the last RoRoRo restart (PID 34392 at session end, RoRoRo at PID 54316).

### Build + test commands

```
dotnet build "C:\Users\estev\Projects\rororo-ur-task\rororo-ur-task.csproj"
dotnet test "C:\Users\estev\Projects\rororo-ur-task\tests\rororo-ur-task.Tests\rororo-ur-task.Tests.csproj"
```

## What changed across rc10 → rc15

### rc10 (`fbd0e7b`) — first reaction to rc9 smoke

- 1:1 macro↔alt enforcement (turned out to be a mis-read — see rc12)
- Per-card `→ AltName` chip via PairedAltByMacroId map
- AssignmentMap helper extracted + 7 tests

### rc11 (`ffc4491`) — CRASH FIX for rc10

- Explicit `Mode=OneWay` on new MultiBindings. rc10 shipped a WPF binding crash: TwoWay default on `PairedAltByMacroId` (read-only property) threw at first render. Plugin couldn't launch. Caught via direct-launch EXE test in the build pipeline (manual run — no automated XAML render test).

### rc12 (`b68bebe`) — pairing model flip + PLAY/STOP toggle

- **Reverted strict 1:1 → one-to-many.** Each ALT pairs with at most one macro, but each MACRO can pair with multiple alts. AssignmentMap simplified to a straight write.
- PLAY/STOP unified into a single button + chord toggle. rc11 trapped the user mid-loop (PLAY started a new round instead of stopping; Task Manager was the only escape). Now: single button labels `PLAY ASSIGNMENTS` ↔ `STOP` based on `IsRunnerActive`, with magenta outline in STOP state. `TogglePlayStopCommand`. Ctrl+Shift+P also toggles (`OnHotkey(HotkeyKind.Play)` aborts when runner is running).
- Multi-alt chip: cards show "→ alt1, alt2, alt3" (comma-joined, sorted) when paired with multiple alts.
- README section on mouse-coord stacking requirement.

### rc13 (`fe59839`) — CRITICAL zombie-plugin fix

When RoRoRo is killed (Task Manager, crash) while a round-robin is playing, the plugin is a separate process — its `AssignmentRunner` kept looping forever, sending macro input to dead Roblox PIDs. From the user's perspective, "macros kept playing after RoRoRo died." On next RoRoRo launch, autostart spawned a SECOND plugin process, making it look like "autostart auto-resumed playback."

Fix layers:
- `PluginClient.HostLost` event, fired exactly once via `Interlocked.CompareExchange` from either gRPC consumer task's catch on unexpected exceptions (Unavailable, Internal, IO — not the expected Cancelled).
- `PluginRuntime.OnHostLost` handler aborts runner + sequence + player, then shuts down WPF Application cleanly (Environment.Exit fallback for dispatcher race).

Este confirmed the diagnosis exactly: "I was able to get to the taskbar to stop 'ur task' after thinking that stopping rororo in the task manager was enough, but it kept going."

### rc14 (`b7bb9f9`) — keyboard-only default + stronger visuals

Three from the rc13 smoke pass:

- **Keyboard-only recording, default ON.** Persisted via `UserPreferences.KeyboardOnlyRecording`. Mouse hook events dropped at `OnMouseEvent`. When user unticks: magenta warning surfaces about window-stacking. Macro recordings start clean — no recorded clicks at the front that would steal foreground from other alts during round-robin. **THIS WAS THE LOAD-BEARING WIN: Este reported "macro worked in all four accounts."**
- Stronger active-painter visual: 2px cyan border + RowHoverBrush background tint when the card is the painter. (Turned out: rc14 had local-value overrides that nullified the thickness/tint parts — fixed in rc15.)
- Active-alt-row highlight during round-robin. New `IntEqualsConverter`. New `RecorderViewModel.CurrentRunnerAltPid`. (Turned out: didn't fire visibly — fixed in rc15.)

### rc15 (`018d17f`) — DataTrigger refactor for visual triggers

The rc14 visual triggers weren't fully landing:

- **Macro card painter:** Border element had `Background` and `BorderThickness` set as **local values**, which override Style triggers per WPF DP precedence. Only BorderBrush change made it through.
- **Assignment row active-alt highlight:** the MultiBinding-via-Style.Setter Tag pattern wasn't surfacing even with clean Style.

Fix: removed local-value overrides on macro card; refactored both triggers to `DataTrigger` that evaluates the MultiBinding directly (no Tag-intermediate). DataTrigger is more idiomatic for "trigger when a computed bool is true" and tends to re-evaluate more reliably inside nested DataTemplates.

**Awaiting validation in next smoke pass.**

## Pending smoke next session

Verify rc15:

1. **Painter visual (full)** — click any macro card. Should see **2px cyan border + dark-blue background tint**. rc14 only got the cyan color change because of the local-value bug. If rc15 lands all three (color + thickness + tint), the painter UX is settled.
2. **Active alt row during cycle** — start a round-robin (Ctrl+Shift+P or PLAY ASSIGNMENTS). The alt row whose turn it is should glow cyan border + tint while it's that alt's turn. This was the rc14 miss. If rc15 fixes it, we're good.

If both land: **tag v0.2.0 stable from rc15**, merge PR #2, announce.

If active-alt still doesn't fire: instrument with a log line in `PluginRuntime` AssignmentProgressed handler (capture alt PID) to prove the event chain is alive, then debug the binding. Possible deeper issues: RelativeSource AncestorType=Window not resolving inside ItemsControl→ScrollViewer→Border nesting; or DataContext propagation hiccup.

## Active follow-ups (task list)

- **#40 H3 — Manual smoke (Este)** [in_progress] — awaiting rc15 visual validation
- **#41 H4 — Tag + push v0.2.0 stable (Este)** [pending] — gated on H3 green
- **#42 I6 — MEMORY.md + dashboard + Discord (Este)** [pending] — gated on H4
- **#56 ROROROblox follow-up — warn before restart with Roblox open** [pending, deferred]
- **#65 ROROROblox bug — captcha showed avoided account name, then loaded that account on dismiss** [pending] — got WORSE this afternoon: two crossover-captchas, two windows of the same wrong account on dismiss. Another agent is investigating; #65 has the screenshot path at `C:\Users\estev\OneDrive\Pictures\Screenshots 1\Screenshot 2026-05-12 160942.png`
- **#66 ROROROblox UX — add per-plugin Launch button on Plugins window rows** [pending] — Este hit the no-Launch gap multiple times; fresh install autostart-off requires the toggle-then-restart dance
- **#67 rc14 UX — active-painter card needs stronger visual** [pending] — rc15 attempts the fix; close if rc15 smoke confirms
- **#68 rc14 UX — highlight active alt row during cycle** [pending] — rc15 attempts the fix; close if rc15 smoke confirms
- **#69 v0.3 — window-relative mouse coords + auto-stack helper** [pending] — proper fix for the click-precision case; documented as v0.3 work in the README

## First moves next session

1. **Read this file**
2. `git -C "C:\Users\estev\Projects\rororo-ur-task" status` + `log --oneline -10`
3. Confirm dev RoRoRo + plugin are both running. If not: `Start-Process` the RoRoRo binary, plugin should autostart (autostart was on at session pause).
4. **Smoke rc15 visual fixes** (painter visual + active alt row).
5. Based on result:
   - **Both land** → tag v0.2.0 stable from rc15 (`git tag v0.2.0` on HEAD `018d17f`, push), merge PR #2, write announcement.
   - **Active-painter lands but row highlight doesn't** → add a Log line to PluginRuntime's AssignmentProgressed handler (or VM's RunnerProgress setter) to verify the event chain. If event chain is alive, the binding is the issue — try ElementName-anchored binding instead of RelativeSource.
   - **Neither lands** → deeper investigation. Start by reading `RecorderWindow.xaml` lines 526-565 (assignment row template) and `RecorderViewModel.cs` 415 (CurrentRunnerAltPid getter).

## Audit trail — full session arc (one chronological list)

```
018d17f  fix(ui): DataTrigger pattern + remove local-value overrides on active-state triggers       (rc15)
b7bb9f9  feat(ui): keyboard-only recording default + stronger active visuals + active-alt row highlight (rc14)
fe59839  fix(plugin): self-terminate when RoRoRo host connection drops                              (rc13)
b68bebe  feat(assignments): one-to-many pairing + PLAY/STOP toggle + multi-alt chip                 (rc12)
592e598  docs(handoff): annotate rc10 supersession + updated install URL
ffc4491  fix(ui): explicit Mode=OneWay on paired-alt MultiBindings to prevent crash                 (rc11)
fbd0e7b  feat(assignments): enforce 1:1 macro↔alt pairing + multi-pair visibility                   (rc10)
a027f58  docs(handoff): v0.2 rc9 stopping point — assignment redesign awaiting smoke
48de5d1  feat(ui): assignment table redesign — round-robin with keep-alive                          (rc9)
```

## What we learned

- WPF dependency-property precedence is sharper than I expected: **local values override Style triggers.** Don't set Background/BorderBrush/BorderThickness directly on a Border AND in its Style — pick one. Style is cleaner because triggers can modify it.
- MultiBinding-via-Style.Setter Tag is fragile inside nested DataTemplates. Prefer `DataTrigger` with the MultiBinding as `DataTrigger.Binding` — direct evaluation, no Tag intermediate.
- The plugin process is genuinely separate from RoRoRo. Without a gRPC liveness check, it zombies forever. `PluginClient.HostLost` + `OnHostLost` handler is the load-bearing safety net.
- Keyboard-only default is the right call for the dominant use case. Mouse capture is the exceptional path that requires stacking.
- "Only one each" was ambiguous — interpreted as 1:1 in rc10, corrected to one-to-many in rc12. Pair-model decisions are worth confirming with a concrete example before building.

## When v0.2.0 stable ships

- Tag `v0.2.0` on `v0.2-build` HEAD → CI builds + publishes release
- Merge PR #2 → main
- Update MEMORY.md + the dashboard via `mcp__626Labs__manage_decisions log`
- Discord post (RoRoRo v1.4.1 + Ur Task v0.2 together)
- v0.2.1 follow-up: custom icon
- v0.3 plan: window-relative coords + maybe an auto-stack helper
- v1.4.0.1 ROROROblox Store submission for the AccountSummary fix (already in the v1.4.1 dev build; needs Partner Center cycle)
