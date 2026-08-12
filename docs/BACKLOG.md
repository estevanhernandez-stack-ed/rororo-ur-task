# Backlog — RoRoRo Ur Task

Findings parked deliberately, with enough context to pick up cold. Newest first.

## Ur Task renders strangely under the flatline theme (v0.7.0)

**Found:** v1.20 host walk, 2026-08-11 · **Severity:** cosmetic, but it is live
**Only flatline.** Every other theme renders correctly.

v0.7.0 deleted the plugin's mirrored copy of the host palettes and took the feed instead (#31), so
this is almost certainly in that path. Flatline is a **built-in** host theme, not a user theme —
worth stating because the obvious first guess (the user-theme path through the feed) is wrong.

One lead for whoever picks it up: the dev machine also carries a leftover `flatline.json` **user
theme file** in `%LOCALAPPDATA%\ROROROblox\themes\` from the glow campaign, and `ThemeStore` drops
user themes whose id collides with a built-in. If the feed and the picker disagree about which
flatline is in play, that collision is where to look first.

Shipped in v0.7.0 and reaching users now. Not urgent — it is a look, not a failure — but it is the
newest built-in, so it is the one people will try.

## A "hold" for macro playback, instead of abort-only

**Raised by Este:** v1.20 walk, 2026-08-11

Cancel mid-flight works: focus returns, no stale assignment. But it is all-or-nothing — the run is
abandoned. As soon as the plugin sees Ctrl+Shift it should **pause before the next action** and be
resumable, rather than throwing the run away.

Este's words: "We will make a beautiful hold feature together." Not a bug; a better shape for the
same surface.

## Keep-alive requires the assignment loop (scoped)

Fully scoped in `docs/superpowers/specs/2026-08-11-autonomous-keep-alive-scope.md`.

Since the walk, one requirement changed: build it to work **alongside** Ur AFK, not instead of it.
Ur AFK stays as the last-resort net for users who will not configure anything (24 installs as of
2026-08-11). That makes it a layered fallback:

1. Ur Task, loop running — macros on full cadence
2. Ur Task, loop stopped, autonomous on — keep-alive only, still claimed
3. Ur AFK — anything Ur Task has not claimed

**The scope doc still says "instead of" and needs revising to match** before anyone builds from it.
The claim rule is the load-bearing part either way: Ur Task claims what it is actually servicing,
moment to moment, and Ur AFK takes the rest. Otherwise both plugins service the same alt and fight
over the foreground.
