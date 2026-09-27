# Display scale, Roblox's minimum window, and what they mean for Ur Task and Ur OCR

**Found:** 2026-09-21, while converting a clan member's AutoHotkey macro to run on any screen.
**Status:** findings and proposals. Nothing in Ur Task or Ur OCR has been changed.
**Companion:** `docs/guides/ahk-any-screen-clicks.md` is the write-up for AutoHotkey authors.

Window-relative coordinates solved window position, monitor resolution, and frame thickness. They
did not solve the Windows display-scale setting, and neither product accounts for it today. Most
laptops ship at 125% or 150%, so this is the likeliest cause of "the shared macro misses on my
laptop".

## Measured facts

1. **Roblox lays out its UI in logical units.** The viewport Roblox reports is the physical client
   size divided by the display scale, and offset-sized UI is enlarged by that scale. Roblox staff
   stated this on the developer forum in March 2026 (thread 4473249). Data in a 2021 thread fits
   exactly: 2560 wide at 225% renders 1138 wide.
2. **Roblox has a minimum window size, and it scales.** The minimum outer size is 816x638 at 100%,
   multiplied by the display scale and rounded down.

   | Display scale | Minimum client area | Source |
   | --- | --- | --- |
   | 100% | 800 x 599 | 56 `EnsureClientSize: already exact 800x599` lines in a real `ur-task.log` |
   | 125% | 1002 x 750 | Measured on Dunder-MiffLan, 3840x2160 at 125% |
   | 150% | 1202 x 901 | Predicted |
   | 200% | 1606 x 1205 | Predicted |

3. **Resizing below the minimum fails silently.** `SetWindowPos` returns success and the window
   lands at the minimum. Community "800x600" macros are really calibrated on 816x638 with an
   800x599 client, and their authors do not know it.
4. **The scaled target sits just below the scaled minimum.** At 125% the target is 1000x749 and the
   minimum is 1002x750, because the frame does not grow in proportion. Any size check needs a few
   pixels of slack to pass on a min-size recording.
5. **The window frame changes with scale.** Left and top are 8 and 31 at 100%, 9 and 38 at 125%,
   11 and 45 at 150%, 13 and 58 at 200%. Client-space coordinates already make this irrelevant.
6. **Even scaling gets close, not exact.** On Dunder-MiffLan at 125%, with size and points both
   multiplied by 1.25, every Pet Sim 99 point landed within a button's width of its target. That
   was still enough to break the one pixel-colour check. The points were then re-aimed by hand, by
   dragging a tag onto each button, and the full load-in sequence ran.

   | Point | Original | Re-aimed at 125% | Moved by |
   | --- | --- | --- | --- |
   | Loaded check (orange of the gift icon) | 45, 214 | 38.4, 200 | 6.6 left, 14 up |
   | Mine world button | 46, 401 | 48.8, 401.6 | 2.8 right, 0.6 down |
   | Automine toggle | 43, 328 | 34.4, 310.4 | 8.6 left, 17.6 up |
   | 5th mine, first click | 102, 214 | 100, 173.6 | 2 left, 40.4 up |
   | 5th mine, second click | 664, 379 | 647.2, 385.6 | 16.8 left, 6.6 down |

   All values are in 100%-scale client units. Read them with care. They were placed by hand, so
   each carries roughly ten units of aiming noise, and the original points are not button centres.
   Two left-column points moved by a similar amount, about 8 left and 16 up, and the first 5th-mine
   click moved 40 up, which is more than aiming noise. The Mine world value was set while already
   in the mine world, where that button may not be on screen, so it proves nothing.

   The practical lesson is the important part. A click target tolerates this error and a
   single-pixel colour check does not. Anything that reads a pixel needs either a search box or a
   per-PC way to re-aim it.

## Ur Task

**Today.** A client-space macro stores physical client pixels plus `RecordedClientW` and
`RecordedClientH`. Playback reproduces that physical client size and maps each point through the
client origin. The v0.4 spec says "No DPI translation layer needed". `EnsureClientSize` allows 2
pixels of slack. `WindowArrangeService` uses a nominal 640x480 floor and says so.

**Gaps.**

- **Scale mismatch is silent.** A macro recorded at 100% and played at 125% gets the same physical
  client size and therefore a different logical viewport. Roblox lays the UI out differently, and
  clicks can miss with no warning. This is the silent mis-click that the v0.6 positioning work set
  out to eliminate.
- **Min-size recordings refuse with the wrong advice.** Recordings at 800x599 are common, per the
  log. Played at 125%, Roblox cannot shrink below 1002x750, so `EnsureClientSize` refuses. The
  refusal is right. Its message says the macro "looks recorded on a larger screen", which is wrong
  here: the window came out bigger than asked, not smaller.
- **Exported AutoHotkey scripts inherit the scale gap.** The exporter emits client coordinates and a
  header asking for the recorded client size, with no mention of display scale.

**Proposals, smallest first.**

1. Record the display scale with the macro, as nullable soft metadata like `RecordedPlaceId`, so
   schema v3 readers still open it. Everything else depends on having this number.
2. On playback, when the recorded scale differs from the target window's scale, at minimum write
   an activity-log line saying so. That alone makes a miss explainable.
3. Then scale: target client size and every point multiplied by player scale over recorded scale,
   with the size slack widened to about 6 logical pixels. This is what the converted AutoHotkey
   script does, and it carried the Dunder-MiffLan run apart from the one game-placed button.
4. Fix the refusal wording for the case where the window came out larger than requested. Name the
   display scale and Roblox's minimum size instead of a larger screen.
5. Optionally raise the arrange floor from 640x480 to 816x638 times the scale, so GRID's overlap
   prediction matches what Roblox will actually allow.
6. Teach the AutoHotkey exporter the same trick. Today it emits client coordinates and asks the
   user to size the window by hand. It could emit the helper block instead, so an exported macro
   sizes its own game area and scales itself. The converted clan script is a working reference.

The AutoHotkey script also got a per-PC, per-scale way to re-aim a single point by dragging a named
tag over the real window. Ur Task's closest equivalent today is re-recording the whole macro on
that PC. Este used the tag editor on Dunder-MiffLan and wants the pattern in the products as an
enhancement. See the backlog entry "Expose and drag a macro's click points".

## Ur OCR

**Today.** A client-space trigger stores its region plus the recorded client size. At run time
`WindowSpaceMath.ToScreenRegion` scales the region by current client size over recorded client
size, because a watcher scales the region where Ur Task resizes the window. The process is
system-DPI-aware. `DpiGuard` fingerprints the virtual screen size and the primary display's scale
per machine, and flags stale regions when the display changes.

**What the findings mean.**

- Proportional scaling is right for UI that grows with the window and wrong for UI sized in fixed
  logical units, which includes Roblox's own dialogs and many HUD buttons. Display scale is a second
  input that the ratio cannot see. Two PCs with the same physical client size and different scales
  give a ratio of 1, while fixed-unit UI is 1.25x larger and corner-anchored elements have moved.
- Exposure is smaller than Ur Task's. A region is usually larger than a click target and OCR
  tolerates padding. Small colour triggers are the risky ones, for the same reason the AutoHotkey
  script's one-pixel loaded check was.
- `DpiGuard` is per machine. It catches a display change on one PC. It does not travel with a
  trigger, so it cannot explain a trigger that was authored on another PC.
- The AutoHotkey fix for mixed-scale monitors was a thread-level switch to per-monitor awareness.
  That is not a drop-in for a WPF process. The standing warning holds: do not add PerMonitorV2 to
  Ur OCR without fixing the region picker first.

**Proposals.**

1. Record the display scale per client-space trigger, nullable.
2. When it differs at run time, keep the proportional scaling and surface the mismatch on the
   trigger row, so a miss is explainable.
3. Reuse the measured minimum sizes above for any future arranging or resizing work.

## Beyond these two products

- **A recovery pattern for the planned Ur Reset plugin.** `roblox://experiences/start?userId=<id>`
  joins whatever server a friend is in, with no auth ticket, using the login the Roblox app already
  holds. Alternating it with the VIP link, as the converted script's "VIP link, friend as backup"
  mode does, recovers an account whose VIP link only opens a captcha. Whether a friend join really
  avoids a captcha lock is still unproven.
- **RoRoRo can be the relauncher for an outside script (2026-09-22).** The converted AutoHotkey
  script gained two rejoin modes that ask RoRoRo to relaunch a saved account, or launch it to
  follow a friend, instead of running a `roblox://` link. It talks to RoRoRo through Ur MCP's exe
  over stdin/stdout, the same consented plugin Claude uses, so no new permission exists. Two
  things learned: feeding the exe a file and closing stdin does not work, because the server shuts
  down on end-of-input before answering, so the pipe has to stay open until the reply; and native
  pipes with `CREATE_NO_WINDOW` keep it invisible. A read-only call round-trips in about three
  seconds. After a relaunch the script only accepts a Roblox window that was not already open, so
  it cannot grab another alt's window. A real launch through this path has not been tested yet.
- **A drag-tag overlay is a reusable debugging surface.** Named tags over the real window, showing
  where each point will land and where it originally was, settled in one screenshot what the
  numbers alone could not. Este has asked for this in the products. It is in the backlog.

## How to test this class of thing

- Dunder-MiffLan, switched to 125%, is the rig for anything other than 100%. Both monitors on
  Nebuchadnezzar, the main PC, are at
  100%, which is why none of this showed up locally.
- A stand-in window is only faithful if it has Roblox's minimum size. In AutoHotkey,
  `Gui("+Resize +MinSize800x599")` reproduces the clamp exactly. A C# test needs a fake
  `IWindowMetrics` that clamps the same way. The first AutoHotkey harness used an unconstrained
  window, passed, and the script then refused on Dunder-MiffLan.
- Run test harnesses hidden on the main PC. Live Roblox sessions and screen-recording plugins run
  there. Generate a copy of the script with its windows hidden and its hotkeys stripped, and never
  touch a real Roblox window, the mouse, or the clipboard.

## 2026-09 point macros at 125%

Live pass for Ur Task 0.9.0 on Dunder-MiffLan, 2026-09-27. Four accounts were in the Space Mine
private server. Every macro was recorded or authored at 100%, and the machine was switched to 125%
between runs.

| Macro | At 125% | What happened |
| --- | --- | --- |
| "top/automine", a real recording converted to 3 points | CElCPapa | Windows had already resized the clients to about 1002x751. Ur Task logged "Display scale differs: recorded at 100%, playing at 125% — scaling to 1000x749" and "windowed-fit ok: 1000x749". Go to Top and the pickaxe both landed, and Auto Mine came on. |
| First match over the eight Teleport tiles, started inside a mine | estehernandez, ItsJustEstePapa | Both sized to 1000x749. The robot button landed. The colour checks read the scaled tiles at the 100% samples (green 98,240,2 and grey 152,155,174) and chose #8 and #6, each account's best unlocked mine. |

- **Scaling the points in proportion was enough.** The robot button, all eight tiles and the pickaxe
  landed with no per-scale adjustment. Inside the mines, then, the UI scales evenly. The up-and-left
  shift in the table above was measured on the spawn-world left column, which reflows, and this pass
  did not test it.
- **Colours hold across scale.** Box averages taken at 100% matched at 125% within the default
  tolerance of 15.
- **The focus guard earned its keep.** One run started while another account's Roblox window was
  opening. That window took the foreground at step 3, and the run stopped with "Foreground shifted
  away from CElCPapa at step 3/5". Nothing was sent to the wrong window.
- **The first try after switching scale was refused** with "Couldn't focus CElCPapa". That refusal
  comes from the existing pre-flight check, not the step runner. A retry a minute later worked.

## Open questions

- Is the 125% shift in the table above the same on every 125% PC? One PC is one data point. If a
  second user's `[Points@125]` section shows the same up-and-left move for the left column, it is
  worth baking into the defaults as a per-scale correction.
- The minimum sizes at 150% and above are predictions.
- Roblox's own disconnect dialog should scale evenly. No real disconnect has been watched at 125%.
