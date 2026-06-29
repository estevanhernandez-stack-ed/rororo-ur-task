# Ur-OCR trigger authoring & verification suite — design

**Status:** approved (brainstorm) · 2026-06-29
**Target repo:** [`Ur-OCR`](https://github.com/estevanhernandez-stack-ed/Ur-OCR) (not checked out locally; spec lives here in the design hub, moves to Ur-OCR on the build trip)
**Companion specs:** [`2026-06-29-ur-ocr-action-bridge-design.md`](2026-06-29-ur-ocr-action-bridge-design.md), [`../../v0.3-ur-ocr-bridge.md`](../../v0.3-ur-ocr-bridge.md)
**Project:** RoRoRo Ur Task (dashboard id `cLloAzj27pCMMDtTlwPy`)

## 1. Problem

Authoring a color trigger is currently blind in the one way that matters: you pick a region and a color, arm it, and only find out at runtime whether it was right. The failure is **silent** — a region a few pixels off, or a color sampled in the wrong lighting/state, simply never matches (or false-fires), with nothing telling you why. For the action-bridge use case (detect an egg/event indicator → run a recovery macro), a mis-authored trigger means the macro silently never runs during the grind it was built for.

Goal: a supportive suite that makes "did I pick the right region + color, and is it matching?" **verifiable before you arm it**, and **durable** across window moves and display changes.

## 2. Current state (verified against Ur-OCR @ HEAD)

What already exists — Stage 1 (author) is largely covered:

| Capability | Status | Evidence |
|---|---|---|
| Region overlay — drag a rectangle over the live desktop, live `(x,y) — w×h` readout | ✅ exists | `UI/RegionPickerOverlay.xaml.cs` |
| Loupe — 160×160 NearestNeighbor pixel magnifier follows the cursor while dragging | ✅ exists | `UI/Loupe.xaml.cs` |
| Color sampling — captures the region, click a pixel on the snapshot, tolerance slider (0–100, default 15) | ✅ exists | `UI/ColorPickerDialog.xaml.cs`, `UI/MainViewModel.cs` AddTriggerCommand |
| Post-fire feedback — first-fire toast, live activity log (last 100), per-trigger "fired N× · Xm ago" | ✅ exists | `PluginRuntime.StartAsync`, `UI/ActivityPanel`, `TriggerRowViewModel.HitSummary` |
| Coords DPI-aware, stored as physical pixels (screen-absolute) | ✅ exists | `RegionPickerOverlay.xaml.cs` OnUp, `Storage/Trigger.cs` `RegionRect` |
| `ColorSamplingMode { SinglePixel, RegionAverage }` | ⚠️ engine only | `Storage/Trigger.cs`, `Engine/ColorMatcher.cs` — UI hardcodes `SinglePixel` |
| `DpiGuard` display-change detection | ⚠️ warn-only | `Storage/DpiGuard.cs` → amber banner; does NOT disable/re-anchor stale regions |

The gaps — all in **Stage 2 (verify)** and **Stage 3 (durability)**:

| Gap | Severity | Why it bites |
|---|---|---|
| No live match meter (real-time sampled-vs-target color + distance + matching-now) | **High** | Blind while authoring/idle — arm and pray |
| No dry-run / test mode | **High** | No way to confirm detection without the real keypress firing |
| `RegionAverage` unreachable in UI | Medium | Single-pixel default is fragile for an animated/flickering indicator |
| `DpiGuard` warns but doesn't disable | Medium | After a display change, stale triggers capture empty regions and silently never fire |
| No window-anchored capture | Low-med | Any window move silently breaks every trigger; shared configs don't port |

## 3. The suite (three stages)

- **Stage 1 — author it right:** region overlay ✅, loupe ✅, click-to-sample + tolerance ✅. *Mostly done.* The one addition here: expose `RegionAverage` (slice 2).
- **Stage 2 — verify it matches:** **live match meter** + **dry-run** (both net-new — v1).
- **Stage 3 — keep it matching:** `DpiGuard` warn→disable (slice 2); window-anchored coordinates (phase 2).

## 4. v1 — the first slice (the answer to "make sure the match is right")

Both are cheap because `Engine/TriggerCoordinator` already captures the region and computes Euclidean color distance every 200ms (5 Hz) — it just discards the result unless the trigger fires.

### 4a. Live match meter

While the trigger editor is open (or a "Live preview" toggle is on), continuously evaluate the **draft** trigger (the one being edited, armed or not) and surface the result.

- **Engine addition:** `ColorMatcher` currently returns `bool Matches(...)`. Add a method that returns the structured result — `(Rgb sampled, double distance, bool matched)` — so the UI can show the number, not just the verdict. The existing `Matches` can delegate to it.
- **New component — `PreviewEvaluator`:** given a draft `RegionRect` + `ColorCriteria`, captures via the existing `CaptureEngine` and computes the structured match at ~5 Hz, raising an event the editor binds to. Lifecycle tied to the editor/preview being open — it is NOT the armed coordinator loop and never fires anything.
- **UI (`TriggerEditView`):** a live preview panel — **target swatch | sampled swatch | distance N | MATCH dot** (green when `distance ≤ tolerance`, red otherwise), updating ~5 Hz. Point the region at the egg, watch the dot flip green when the event color appears. Adjust tolerance and see the dot respond live.

### 4b. Dry-run / test mode

Validate detection without consequences (no keypress, and — once the bridge client lands — no macro fire).

- **Per-trigger "Test (10s)" button** in `TriggerEditView` (recommended over a global toggle — it answers "verify *this* trigger"). Runs that single trigger through the real coordinator path in **log-only** mode for a time-box, then auto-stops.
- **Coordinator change:** a dry-run flag (global field or a per-trigger test set). On a would-fire while dry-run, log a new `ActivityKind.WouldFire` (+ the existing toast/overlay flash) and **skip** `IKeyPress.Press` / the action. Everything else (capture, match, edge+cooldown, account-gate) runs unchanged, so the test exercises the real detection path.
- This composes with the action bridge: when the trigger's action becomes "run a Ur Task macro," dry-run skips the `RunMacroFireAction` call too — so you can test an event→macro trigger without playing the macro on a live account.

## 5. Slice 2 (cheap follow-ons)

- **Expose `RegionAverage`** in `ColorPickerDialog` (a SinglePixel/RegionAverage choice) and stop hardcoding `SinglePixel` in `MainViewModel.AddTriggerCommand`. RegionAverage is more robust for animated indicators. Engine already implements both.
- **`DpiGuard` warn → disable-stale:** when a display change leaves a region out of the new virtual-screen bounds, mark those triggers disabled (or a `NeedsRePick` state) instead of leaving them armed to capture empty regions. Surface a one-click "re-pick" affordance from the existing banner.

## 6. Phase 2 (bigger, deferred)

- **Window-anchored coordinates:** capture relative to the Roblox window (resolve the target window, store region as window-relative offsets, translate to screen coords at capture time). Makes triggers survive window moves/resizes and makes shared clan configs portable across machines/resolutions. This is the same coordinate-constraint called out in the bridge spec §7 and is the durable fix for the "anchor your window" instruction.

## 7. Testing & validation

- **Engine (unit, CI):** the new `ColorMatcher` structured-result method — distance math, `matched == (distance ≤ tolerance)`, both sampling modes — is pure and unit-testable.
- **Dry-run gating (unit):** with a fake `IKeyPress`, assert a would-fire in dry-run logs `WouldFire` and does NOT call `Press`; in normal mode it does. No real input.
- **PreviewEvaluator (unit):** feed a stub capture returning a known bitmap; assert the emitted `(sampled, distance, matched)` matches expectation; assert it never fires anything.
- **Manual (synthetic swatch):** open a window filled with a known color, point a draft trigger at it, confirm the meter flips green at the right tolerance and the "Test" button logs `WouldFire` without a keypress. Mirrors the bridge spec's synthetic-stimulus approach — no live event needed.

## 8. Non-goals / out of scope

- Not rebuilding the region/loupe/color picking that already works.
- Not OCR-text authoring aids (this suite is color-trigger-focused; text triggers reuse the same region overlay).
- Not a full calibration wizard — tolerance auto-tune is a possible later add, not v1.
- Window-anchoring is explicitly phase 2, not v1.

## 9. Open questions / risks

- **Dry-run shape:** per-trigger time-boxed "Test" (recommended) vs. a global "don't press keys" toggle. Decide before building; per-trigger is the lean default.
- **Preview loop cost:** a 5 Hz capture of one small region while the editor is open is cheap, but confirm it pauses when the editor closes/loses focus so it doesn't run in the background.
- **`ActivityKind.WouldFire`:** new enum value — confirm the activity log/columns render it cleanly.
- **Interaction with account-gating:** the preview meter should evaluate regardless of foreground-is-alt (you author while looking at the game); only the *armed* coordinator applies the account gate. Keep the preview path ungated but clearly labeled "preview."

## 10. Where it lives & sequencing

All in the **Ur-OCR** repo. Bundle this with the other deferred Ur-OCR-side work on one build trip (clone Ur-OCR, run brainstorm-confirm → plan → build):

1. **`SequencePlayer` re-entry guard** (precondition from the bridge review — must land before the bridge client can fire concurrently with hotkey playback). *In rororo-ur-task.*
2. **`RunMacroFireAction` bridge client** + `Trigger` action discriminator + macro picker (bridge spec §5).
3. **This authoring suite** — v1 (match meter + dry-run), then slice 2.

The match meter + dry-run (v1) are independently valuable even before the bridge client — they improve every existing keybind trigger today.
