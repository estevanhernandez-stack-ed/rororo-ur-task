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

### The built-in theme palettes are mirrored in plugin code

Ur Task reads the host's active theme from RoRoRo's settings on disk and re-paints live. **Custom
theme files are read live; the three built-in palettes are copied into plugin source.** If RoRoRo
changes a built-in palette, this plugin needs a matching update or it will render last year's
colours and look broken through no fault of its own.

The host is building a theme feed (`IThemePaletteSource`) that would replace the mirroring. Until
that lands and this plugin consumes it, the duplication stands and is a real maintenance debt.

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
