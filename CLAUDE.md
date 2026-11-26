# RoRoRo Ur Task

> **Persona:** inherits **The Architect** from `~/.claude/CLAUDE.md`. Nothing to re-establish — this
> file only adds what is specific to this repo.

A first-party RoRoRo plugin: record a macro, assign it to accounts, play it back round-robin. WPF,
.NET 10, distributed through RoRoRo's plugin installer rather than the Store.

---

## The things that will bite you

### Build the `.csproj`, never the `.sln`

`rororo-ur-task.sln` is IDE-generated, gitignored, and pulls in the sibling ROROROblox projects — so
`dotnet build rororo-ur-task.sln` drags the entire host app into this build and **fails outright**
while a RoRoRo instance is running and holding its own DLLs. It looks like a broken plugin; it is a
broken solution file.

```
dotnet build rororo-ur-task.csproj
```

The host repo documents the identical trap for its own legacy `.sln`. Same disease, same cure.

### Two test commands, and the fast one does not build everything

```
# Fast, self-contained — what gates every PR.
dotnet test tests/rororo-ur-task.Tests/rororo-ur-task.Tests.csproj -p:StandaloneTestsOnly=true

# With the real host. Needs ROROROblox checked out as a SIBLING directory.
dotnet test tests/rororo-ur-task.Tests/rororo-ur-task.Tests.csproj
```

`StandaloneTestsOnly=true` drops `PluginClientIntegrationTests` and its project references to
`..\..\..\ROROROblox\`. That layout is not optional — the repos must sit side by side:

```
<parent>/
  rororo-ur-task/
  ROROROblox/
```

**This flag has already cost us once.** The integration test — the plugin's ONLY check against the
real host — stopped compiling when the host grew two constructor dependencies, and nothing noticed
for weeks because no pipeline ever built the file. The standalone suite sat at a truthful,
meaningless 268/268 green. There is now a `host-integration` CI job that checks out both repos and
builds without the flag; if you are tempted to remove it because it is slow, that is precisely the
edit that let the rot happen.

### The contract comes from NuGet, deliberately

`ROROROblox.PluginContract` is a `PackageReference`, not a project reference, even though the host
source is right there. That is on purpose: it is the exact path an external plugin author takes, so
we find out about a broken package before they do. Do not "simplify" it to a project reference.

### A green badge here goes stale without anyone touching this repo

`host-integration` proves the plugin against the host's `main`, but it only runs when something
triggers it: a push, a PR, the daily schedule, or a manual run. **The host ships far more often
than this plugin does.** In November 2026 it went from v1.19 to v1.40 while this repo had no
commits, and the integration test had stopped compiling for most of that month.

Starting work after a quiet stretch? Run the workflow first (`gh workflow run test.yml`), then
believe the result. The schedule helps, but GitHub switches scheduled workflows off after 60 days
of repository inactivity, so it fails in exactly the case it exists for.

### A root `NuGet.config` is locally excluded, and a stale one breaks restore

`.git/info/exclude` hides `NuGet.config`, so a local feed override never shows in `git status`.
One left behind from testing an unpublished contract pointed at a temp folder that no longer
existed, and every restore failed with `NU1301`. If restore complains about a local source, look
for that file. It also quietly defeats the point of the next section, so delete it when done.

### The theme comes from the host over the contract

Ur Task asks the host for its resolved palette (`GetTheme`) and follows changes
(`SubscribeThemeChanged`). It does not read RoRoRo's settings or theme files, and it carries no
copy of the built-in palettes. That mirror existed once, went stale the first time the host added
a theme, and was deleted.

The one hardcoded palette left is `HostThemeReader.Brand`. It is the fallback for a window that
has to paint before any host answers, including the standalone case. It is not a mirror and does
not need to track anything. Do not grow it back into one.

---

## Where things live at runtime

| what | where |
|---|---|
| installed plugin | `%LOCALAPPDATA%\ROROROblox\plugins\626labs.ur-task\` |
| logs | `%LOCALAPPDATA%\626Labs\RoRoRoUrTask\logs\ur-task.log` |
| action bridge | named pipe `\\.\pipe\626labs-ur-task`, current-user only |

The log is the debugging surface. A clean session always ends with an "exiting cleanly" line —
**its absence is the evidence**, which is why the startup watchdog exists: the failure mode it
catches is the windowless hang, which throws nothing for any handler to see.

---

## Macros here, not in the host — and that is not a contradiction

The host's CLAUDE.md forbids macros and input automation outright, and points at MaCro. That rule
governs **RoRoRo itself**, whose relationship with Roblox depends on being a launcher that does not
touch the client. Ur Task is a separate, opt-in, user-installed plugin that the user chooses to
add — different artifact, different consent, different risk posture.

Keep the wall where it is. Macro capability belongs on this side of the plugin boundary and must not
migrate into the host to make something convenient.

Recording is **keyboard-only by default** — see the README's mouse-click caveat before changing
anything about capture.

---

## Conventions

- **Commits:** conventional commits, same as the host.
- **Guards:** `powershell -ExecutionPolicy Bypass -File .claude/hooks/install.ps1` installs the
  pre-commit secret scan and local-path guard. Run it once per clone. The local-path allowlist holds
  four frozen session records whose paths ARE the documentation; add to it only for the same reason,
  and per-file rather than per-directory so new docs still get caught.
- **Voice:** 626 Labs — builder-to-builder, second person, sentence case. No emoji in UI copy. The
  audience is the Pet Sim clan: non-technical Windows users.
- **Versioning:** SemVer, and `rororo-ur-task.csproj` + `manifest.json` must agree. They are two
  files saying the same thing, so they drift; check both when bumping.

## What NOT to do

- **Do not build or test through the `.sln`.** See above.
- **Do not swap the contract PackageReference for a ProjectReference.** It exists to prove the
  published package works.
- **Do not remove the `host-integration` CI job.** It is the only thing standing between this repo
  and silent contract rot.
- **Do not edit the host to make something here easier** without saying so explicitly. This plugin
  consumes a published contract; if the contract needs to change, that is a host decision with its
  own release.
