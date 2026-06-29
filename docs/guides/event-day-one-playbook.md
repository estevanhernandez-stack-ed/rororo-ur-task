# Event day-one playbook + synthetic bridge test

## Part A — Validate the machine now (no event needed)

1. **Bridge unit tests** — `dotnet test ... -p:StandaloneTestsOnly=true` is green
   (FrameCodec / BridgeContract / MacroRunnerServer / MacroRunInvoker).
2. **Synthetic detection** — open a window filled with a known color (an image,
   or a solid-fill window). Add an Ur-OCR color trigger on that region. Confirm
   it fires when the color is shown and respects cooldown.
3. **Synthetic end-to-end** — set that trigger's action to run a harmless test
   macro (e.g. a macro that types into Notepad) on the foreground target. Show
   the swatch → confirm Ur Task plays the macro and logs a playback id.

If Part A passes, the pipeline is proven; only event-specific values remain.

## Part B — When the next event (or the current egg) is live

1. Screenshot the event/egg UI.
2. Sample the event-indicator color; pick the smallest reliable region. **Do not
   reuse old values** (e.g. K0ii's `0xFF115F` / coords) without re-checking —
   each egg/event differs.
3. Record or author the **recovery macro** against the live UI coordinates.
4. Set a cooldown that clears the recovery animation.
5. Anchor the Roblox window (fixed position + size).
6. Arm the trigger; watch Ur Task's activity log for the fire + playback.

## Notes

- Screen reading only works on a rendered window — the reactive trigger runs on
  the **active** alt; the macro's `targets` decide which alts get the action.
- Coordinates are screen-absolute in v1. Moving the window breaks both detection
  and clicks. Window-anchored coordinates are a phase-2 upgrade.
