# Ur-OCR → Ur Task action bridge — design

**Status:** approved (brainstorm) · 2026-06-29
**Companion contract:** [`docs/v0.3-ur-ocr-bridge.md`](../../v0.3-ur-ocr-bridge.md) (A3 wire contract — adopted as-is)
**Companion repo:** [`Ur-OCR`](https://github.com/estevanhernandez-stack-ed/Ur-OCR)
**Project:** RoRoRo Ur Task (dashboard id `cLloAzj27pCMMDtTlwPy`)

## 1. Origin & reframe

The ask was "let RoRoRo users run the community's AutoHotkey event-macros inside Ur Task, without installing another application." The reference file (`K0ii_Double_Hatching_V1.2.ahk`, a community auto-hatcher kept locally, not committed) proved those macros are **not** key sequences — they are reactive screen-reading bots: three concurrent timers, a `PixelSearch` for color `0xFF115F` that detects a game event, and a scripted recovery click-route fired on detection.

That cannot be translated into Ur Task's flat timestamped-event engine (it reacts to pixels; a recorded macro can't). Faithful execution would mean bundling AutoHotkey or building an AHK-subset interpreter with screen-reading — a parallel engine beside the architecture 626 Labs already has.

**Decision:** deliver what these macros *do* — "detect an on-screen event, run a sequence" — by composing two existing plugins, not by executing `.ahk`. A K0ii-style `.ahk` becomes a **spec we translate** into a trigger + a macro, never a file we run.

**Conscious trade:** this gives the *capability* of community `.ahk` event-macros, natively and account-safe. It does **not** give "drop in any `.ahk` and go." Someone authors the trigger + macro once (shareable within the clan). Literal arbitrary-`.ahk` execution is explicitly out (see §9).

## 2. Architecture

Perception → action, two plugins, joined by the named-pipe bridge already sketched in the companion contract.

```text
Ur-OCR (perception)                 bridge                  Ur Task (action)
 color trigger matches  ──▶  \\.\pipe\626labs-ur-task  ──▶  MacroRunnerServer
 (region capture @5Hz,       JSON length-prefixed             → SequencePlayer
  RGB tolerance,             { RunMacro, macroId, targets }   plays macroId on
  rising-edge + cooldown)                                     targets, foreground-gated
```

**Reuse map (verified against Ur-OCR @ HEAD, file:line):**

| Capability | Home | Status |
|---|---|---|
| Region capture (GDI `BitBlt`, desktop DC) | Ur-OCR `Engine/CaptureEngine.cs:23-28` | exists |
| Color match (Euclidean RGB vs tolerance; SinglePixel/RegionAverage) | Ur-OCR `Engine/ColorMatcher.cs:13-22` | exists |
| Poll loop @5Hz, rising-edge + cooldown | Ur-OCR `Engine/TriggerCoordinator.cs:58,123-136` | exists |
| Account-aware gating (foreground-is-alt, not-elevated) before fire | Ur-OCR `Engine/TriggerCoordinator.cs:92,99` | exists |
| Macro engine, foreground-gated playback | Ur Task `MacroPlayer` / `SequencePlayer` | exists |
| The wire between detection and playback | — | **net-new** |

Net-new is only the bridge plus the seam-widening to route a match to a macro instead of a keybind.

## 3. The bridge contract

Adopt [`docs/v0.3-ur-ocr-bridge.md`](../../v0.3-ur-ocr-bridge.md) verbatim as the A3 contract:

- Transport: named pipe `\\.\pipe\626labs-ur-task`, current-user-only SDDL, single concurrent connection.
- Wire: JSON over 4-byte big-endian length-prefixed frames.
- Request: `{ contractVersion, method:"RunMacro", macroId, targets?, interAltDelayMs?, callerPluginId }`. `targets` null/omitted = smart-default (foreground alt), mirroring `Ctrl+Shift+P`.
- Response: sync ack `{ ok, playbackId, queued }` or refusal `{ ok:false, reason, detail }` with `reason ∈ {busy, unknown-macro, no-targets-resolved, refused, version-mismatch}`.
- Busy = refuse, do not queue (OCR triggers are edge-triggered with cooldowns; re-fire is cheap).
- `contractVersion` in every request; Ur Task refuses outside `1.x`.

Resolve the open question in the contract doc in favor of a `["foreground"]` sentinel for `targets` rather than null (clearer than overloading absence).

## 4. Ur Task side (this repo)

- **`src/Ipc/MacroRunnerServer.cs`** — owns the `NamedPipeServerStream`, accepts one client at a time, deserializes `RunMacro`, resolves `targets` (user-ids → running alts) via `AccountRegistry`, calls into `SequencePlayer`. Dedicated background thread; cancellation hooked to app shutdown. No changes to `MacroStore` / `MacroPlayer` / `SequencePlayer`.
- **Settings toggle** "Accept run requests from other plugins" (default on; lets a user opt out).
- **Consent / manifest (explicit capability — decided).** Declare an explicit capability string and surface it on the install consent sheet ("Accepts macro-run requests from other 626 Labs plugins on this PC"). Suggested pair: Ur Task declares `plugins.accept-run-requests` (server), Ur-OCR declares `plugins.send-run-requests` (client) — exact identifiers TBD with the host. This overrides the companion contract's lean toward consent-text-only, and carries a host dependency (see §10).
- **Wire-up** in `App.xaml.cs` startup, behind the settings toggle.

## 5. Ur-OCR side (companion repo)

The recon pinned every seam; the action abstraction is currently keybind-shaped and must be widened.

- **Widen the fire seam.** Today: `IKeyPress.Press(KeyCombo)` (`Engine/CaptureEngine.cs:16`, impl `Engine/KeySender.cs:7`), called once at `Engine/TriggerCoordinator.cs:130` `keys.Press(trig.Keybind)`, injected at `PluginRuntime.cs:46`. Introduce `IFireAction { Task FireAsync(Trigger, CancellationToken) }` with `KeyChordFireAction` (today's path) and `RunMacroFireAction` (new pipe client). Coordinator fires the action the trigger selects.
- **`Trigger` model.** Add an action discriminator (`KeyChord | RunMacro`) + macro target (`macroId`, optional `targets`) to `Storage/Trigger.cs`. `SchemaVersion` exists (`Trigger.cs:38`) but there is **no migrator**; additive, default-valued fields are safe (System.Text.Json tolerates missing/extra). Do not require a migrator for additive fields.
- **Bridge client** — `RunMacroFireAction` opens the named pipe and sends `RunMacro`. Reuse the proven named-pipe-client construction in `PluginHost/PluginClient.cs:25-37` (note: that pipe is the *host* gRPC channel `rororo-plugin-host`, a different pipe — copy the pattern, not the target).
- **Trigger-edit UI.** The keybind picker block at `UI/TriggerEditView.xaml:35-39` (`KeybindCapture`) becomes an action selector: keybind *or* macro. The macro picker reads Ur Task's macros directly from `%LOCALAPPDATA%\626Labs\RoRoRoUrTask\macros\*.json` and surfaces `Id` + `Name` + `RecordedAgainstDisplayName` (read-only; the directory is already the durable v2 contract). Update the create flow default (`UI/MainViewModel.cs:46`).
- **Manifest** — add the capability string for the sibling bridge; `manifest.json` currently declares no IPC capability and uses `contractVersion` (no `minHostVersion` field).

## 6. Phasing

**Phase A2 — probe (near-zero build).** Ur-OCR already fires keybinds on a color match. A color trigger whose keybind is `Ctrl+Shift+P` drives Ur Task's existing play-assignments chord today, no new code — only setup. Validates the perception→action UX and is shippable as a how-to guide. If A2 covers ~80% of the use case, A3 can wait.

**Phase A3 — the bridge.** Build §4 + §5 so a trigger fires a *specific* macro on *specific* alts instead of the global chord.

**Phase 2 (future, out of v1).** See §9.

## 7. Proof case: K0ii decomposition + the coordinate constraint

K0ii decomposes to: a color trigger on the `0xFF115F` region → fires a "double-hatch recovery" Ur Task macro (the event→menu→confirm→hatch route).

**Coordinate constraint (surfaced by recon, must be documented):** Ur-OCR captures at **absolute virtual-screen coordinates** (`Engine/CaptureEngine.cs:28`, desktop DC) — not window-relative. K0ii is entirely window-relative (`CoordMode "Window"`; it resizes Roblox to 800×600 to stabilize coords). Ur Task macros are likewise screen-absolute (`MacroPlayer` normalizes screen coords to the virtual desktop). Therefore a trigger + macro authored together works **only while the Roblox window stays put**; move or resize it and both the detection region and the click coords drift. v1 documents "anchor your window" (K0ii's own resize trick). Window-anchored coordinates are the phase-2 upgrade that makes shared clan configs portable across machines.

## 8. Testing & validation

The live event that K0ii targets is over; its `0xFF115F` won't appear in-game now. This blocks **tuning the event-specific config**, not **building or validating the machine**. Split accordingly.

**Testable now, no event required:**
- **Bridge (CI).** Unit-test `MacroRunnerServer` against an in-process `NamedPipeServerStream`/`Client` pair: accept, `busy`, `unknown-macro`, `version-mismatch`, target resolution via a fake `AccountRegistry`. Runs in the standalone unit-test CI added in v0.2.3 (`StandaloneTestsOnly`), no ROROROblox dependency.
- **Detection.** Point an Ur-OCR color trigger at a controllable on-screen swatch — a WPF window filled with the target color, or a static image. Showing the swatch is the simulated event; assert the trigger fires (rising edge) and respects cooldown.
- **End-to-end dev loop.** Swatch on screen → Ur-OCR detects → `RunMacro` over the pipe → Ur Task plays a harmless test macro into a throwaway window. Proves the full chain without the game.

**Waits for the next live event (event-specific, never hardcoded):**
- The real indicator color value + region rect.
- The recovery click coordinates against the live event UI.

**Event day-one playbook (readiness deliverable, authored before the event):**
1. Screenshot the event UI.
2. Sample the event-indicator color; pick the smallest reliable region.
3. Record/author the recovery macro against the live event UI coordinates.
4. Set cooldown (avoid double-fires through the recovery animation).
5. Anchor the Roblox window (fixed position + size) per §7.
6. Arm the trigger; watch Ur Task's activity log for `Fired` + playback.

Goal: when the event drops, day-one is *execution* of a known-good rig, not discovery.

## 9. Scope

**In (v1):** event-detected color trigger → fire a chosen macro on chosen alts, via the bridge. The novel half of K0ii (the recovery route).

**Out, on purpose:**
- **Continuous baseline grind** (K0ii's 7ms click + 290ms key loops). Neither plugin has "loop a macro until stop"; that's a separate Ur Task feature.
- **Literal arbitrary `.ahk` import / execution.** Parsing AHK GUIs/timers/control-flow is a tar pit; ruled out.
- **Window-relative coordinates.** v1 is screen-absolute with a "anchor your window" instruction.

**Future (phase 2):** loop-a-macro-until-stop in Ur Task; window-anchored coordinates for portable shared configs; a guided importer that turns *known* `.ahk` patterns (color-watch + click sequence) into a trigger+macro pair.

## 10. Risks & open questions

- **Cross-repo version pairing.** The contract lives in two repos. Pair `contractVersion` bumps across Ur-OCR and Ur Task; never drift them (same discipline as the csproj/manifest version pair).
- **Consent surface — RESOLVED: explicit capability on both manifests** (Ur Task server + Ur-OCR client), not consent-text-only. Consequence: the ROROROblox host enforces and renders capabilities on the consent sheet, so it must *recognize* the new strings — otherwise they show as unknown or are rejected. This makes the feature a **three-repo touch** (Ur Task manifest, Ur-OCR manifest, ROROROblox capability registry). Exact capability identifiers to be agreed with the host before the manifests are cut.
- **Multi-alt reactive limits.** Screen-reading only works on a rendered window, so a reactive trigger is inherently single-active-window; the bridge's `targets` list is most meaningful for the *action* (fan a recovery macro across alts), not for running N detectors at once. Document this expectation.
- **Detection on the active alt only.** The account-aware gate already restricts firing to when an alt is foreground; confirm that matches the intended UX (detect-on-active, act-on-targets).

## 11. Implementation order

1. **A2 probe + guide** — validate UX with `Ctrl+Shift+P` keybind trigger, no code.
2. **Ur Task `MacroRunnerServer`** + in-process-pipe unit tests (CI).
3. **Ur Task settings toggle + consent/manifest.**
4. **Ur-OCR `IFireAction` seam + `Trigger` model + `RunMacroFireAction` client.**
5. **Ur-OCR trigger-edit UI** (action selector + macro picker).
6. **Synthetic end-to-end** (swatch → detect → fire → playback) before shipping.
7. **Author the event day-one playbook** so the next event is execution, not discovery.
