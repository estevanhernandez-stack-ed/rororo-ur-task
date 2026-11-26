# Breakglass chord: "I have the wheel"

Status: BUILT 2026-10-09 on branch feat/breakglass-chord (worktree .claude/worktrees/breakglass), not installed yet. Asked for by Este 2026-10-09 23:34 after he grabbed the PC twice during an agent listing run; he approved F11 at 23:42.

## The situation it fixes

Este takes the PC mid-run. Today that looks exactly like a bug from the agent's side:

- Ur Task aborts the playback ("Foreground shifted away from X at step 3/15"), the agent script reads `failed`, and the wrapper
  moves on to the next account, clicks the booth X, re-arms keep-alive.
- Keep-alive and night-watch's re-arm send input into Roblox windows, so they fight Este for focus.
- The agent spends the next turn diagnosing a "focus steal" (2026-10-09 23:23 and 23:29: both were Este).

Ctrl+Shift+F12 stops the macro but says nothing about *why*, and nothing stops the next one.

## The chord

**Ctrl+Shift+F11, toggle.** Next to abort on purpose: F12 means "stop this macro", F11 means "hands off until I say so".

Press once:
1. Abort every active playback (same path as F12).
2. Stop the keep-alive loop, remembering whether it was playing.
3. Write the flag `%LOCALAPPDATA%\626Labs\RoRoRoUrTask\breakglass` (timestamp inside).
4. Log one line: `Break-glass ON (Ctrl+Shift+F11): Este has the wheel.`
5. The bridge refuses `RunMacro` while the flag exists, with reason `breakglass`. Agents get a clean "no", not a mid-step abort.

Press again:
1. Delete the flag, log `Break-glass OFF`.
2. Restore keep-alive if it was playing at step 2 (the 12-minute rule still holds).

## Agent side (ps99-ops)

- `booth-paste-run.py`, `trade-run.py`: a `breakglass` refusal, or an abort with the flag present, exits with code 3
  "PAUSED by Este", not a failure. No registry write for that item, and no "listed".
- Wrappers that loop over accounts stop on exit 3 and don't touch any window: no booth X, no keep-alive toggle.
- `night-watch.ps1`: while the flag exists, skip REARM and emit `BREAKGLASS on since HH:MM`. After 10 minutes, emit the
  12-minute-rule warning once so nobody goes idle while Este forgets to release it.
- On release, the agent re-reads any booth it was working on (`booth-open.ps1` N/25) before resuming the queue. A run cut
  mid-commit may or may not have landed. Tonight three "listed" Huges never appeared.

## Interim rule (no code, starts now)

Until the chord ships, an abort is Este taking the wheel when the log shows Ctrl+Shift+F12, or a foreground shift to a
non-Roblox window: `Aborted (Ctrl+Shift+F12; foreground '...Visual Studio Code')`. The agent stops the batch and asks.
It doesn't retry, re-arm, or diagnose.

## Size

Ur Task: one more global hotkey next to the existing ones (Hotkeys ready line), a flag file, a refusal branch in the
bridge, a toast. Tests: the hotkey toggles the flag, the bridge refuses while it exists, keep-alive restores its prior
state. Ships as a minor bump; `rororo-ur-task.csproj` and `manifest.json` move together.

Revisit if Este wants it on a mouse button or a Stream Deck instead of a chord.
