#Requires -Version 7
<#
.SYNOPSIS
  Writes the ore-stop example macros (agent-authored v4) from measured.json.

.DESCRIPTION
  One file of measured values drives every macro, so a re-measure is one edit and one run:

      pwsh -NoProfile -File docs/reference/events/macros/space-mine-ore-stop/generate.ps1

  The Ur OCR capture sweep fills the "ring" and "camera" groups and sets their "measuredOn"; the
  live outline measurement fills "ring.reach". A group whose measuredOn is null is provisional:
  the script warns and writes it anyway.

  Two families of ring macros, one per spot:
    - "Mine spot <name>": Auto Mine off, hold the spot, Auto Mine on (watch-while-riding).
    - "Clear spot <name>": hold the spot and nothing else (ore stop v1, pulse). The Ur OCR loop
      owns Auto Mine in pulse mode, so these never touch the pickaxe.
  Every ring hold carries a reach check: with the pointer on the spot, no white outline means no
  press. Each hold breaks one block and stops: it holds for 300 ms, lets go and looks (the game
  hides the outline while the button is down). No outline, or an outline that moved to the next
  block down, means the block broke; the same outline means hold again, twice as long, up to 3 s.

  Install: copy macros\*.json into %LOCALAPPDATA%\626Labs\RoRoRoUrTask\macros, then restart
  Ur Task. Each file is named <id>.json, the store's own convention, so deleting a macro from the
  library still works. The ids are fixed below so Ur OCR triggers and point adjustments survive a
  regenerate.

  Nothing here presses anything but the Auto Mine pickaxe, Go to Top, the eight ring spots, and a
  right-button camera drag. Never add a step that clicks a captcha, the Enchant Machine or an
  invite popup; OreStopExampleMacrosTests fails on any press outside those points.
#>
$ErrorActionPreference = 'Stop'
Set-StrictMode -Version Latest

$m = Get-Content -Raw -Path (Join-Path $PSScriptRoot 'measured.json') | ConvertFrom-Json
$out = Join-Path $PSScriptRoot 'macros'
New-Item -ItemType Directory -Force -Path $out | Out-Null
Get-ChildItem -Path $out -Filter '*.json' | Remove-Item

# Same margin as StepConverter.CameraDragMargin (0.9.0): overshoot the pitch limit, vertical only.
$CameraDragMargin = 1.5
$RingNames = @('N', 'NE', 'E', 'SE', 'S', 'SW', 'W', 'NW')

$Ids = @{
    'Auto Mine off (checked)' = '0e5a0000-0000-4000-8000-000000000001'
    'Auto Mine on (checked)'  = '0e5a0000-0000-4000-8000-000000000002'
    'Camera top-down'         = '0e5a0000-0000-4000-8000-000000000003'
    'Go to Top'               = '0e5a0000-0000-4000-8000-000000000004'
    'Camera turn left'        = '0e5a0000-0000-4000-8000-000000000005'
    'Mine spot N'             = '0e5a0000-0000-4000-8000-000000000011'
    'Mine spot NE'            = '0e5a0000-0000-4000-8000-000000000012'
    'Mine spot E'             = '0e5a0000-0000-4000-8000-000000000013'
    'Mine spot SE'            = '0e5a0000-0000-4000-8000-000000000014'
    'Mine spot S'             = '0e5a0000-0000-4000-8000-000000000015'
    'Mine spot SW'            = '0e5a0000-0000-4000-8000-000000000016'
    'Mine spot W'             = '0e5a0000-0000-4000-8000-000000000017'
    'Mine spot NW'            = '0e5a0000-0000-4000-8000-000000000018'
    'Clear spot N'            = '0e5a0000-0000-4000-8000-000000000021'
    'Clear spot NE'           = '0e5a0000-0000-4000-8000-000000000022'
    'Clear spot E'            = '0e5a0000-0000-4000-8000-000000000023'
    'Clear spot SE'           = '0e5a0000-0000-4000-8000-000000000024'
    'Clear spot S'            = '0e5a0000-0000-4000-8000-000000000025'
    'Clear spot SW'           = '0e5a0000-0000-4000-8000-000000000026'
    'Clear spot W'            = '0e5a0000-0000-4000-8000-000000000027'
    'Clear spot NW'           = '0e5a0000-0000-4000-8000-000000000028'
}

foreach ($group in 'autoMine', 'goToTop', 'ring', 'camera') {
    if ($null -eq $m.$group.measuredOn) {
        Write-Warning "$group is provisional (measuredOn is null). The Ur OCR capture sweep measures it; regenerate after."
    }
}
if ($null -eq $m.ring.reach.measuredOn) {
    Write-Warning "ring.reach is provisional (measuredOn is null). Measure the outline live with the pointer on and off a breakable block; regenerate after."
}
if ($null -eq $m.goToTop.check) {
    Write-Warning "goToTop.check is null: the Go to Top press is unchecked until the Ur OCR capture sweep measures it; regenerate after."
}
if ($null -ne $m.turn -and $null -eq $m.turn.measuredOn) {
    Write-Warning "turn is provisional (measuredOn is null). Regenerate after confirming the hold length live."
}

function Rgb($c) { [ordered]@{ r = [int]$c.r; g = [int]$c.g; b = [int]$c.b } }
function Box($b) { [ordered]@{ offsetX = [int]$b.offsetX; offsetY = [int]$b.offsetY; w = [int]$b.w; h = [int]$b.h } }

# The outline check for one spot: the spot's own reach if it has one, else the ring's shared
# default, replaced wholesale. The box is centred on the spot.
function Reach($spot) {
    $r = $m.ring.reach
    $own = $spot.PSObject.Properties['reach']
    if ($null -ne $own -and $null -ne $own.Value) { $r = $own.Value }
    $w = [int]$r.w
    $h = [int]$r.h
    [ordered]@{
        box      = [ordered]@{ offsetX = 0 - [int][math]::Floor($w / 2); offsetY = 0 - [int][math]::Floor($h / 2); w = $w; h = $h }
        minCount = [int]$r.minCount
        whiteMin = [int]$r.whiteMin
    }
}

# A ring hold: press on the spot while its colour holds, behind the outline check. No maxMs: ore
# is never abandoned for taking long (spec decision 5); the outline going is the way out.
function SpotHold($spot, [int]$delayMs) {
    $ring = $m.ring
    [ordered]@{
        kind    = 'hold'
        delayMs = $delayMs
        id      = "spot-$($spot.name)"
        label   = "Spot $($spot.name)"
        x       = [int]$spot.x
        y       = [int]$spot.y
        button  = 1
        check   = [ordered]@{ box = (Box $ring.box); tolerance = [int]$ring.tolerance }
        reach   = (Reach $spot)
    }
}

# The pickaxe press, gated on the dot. skipIfOther: skip when the dot already shows the other
# state; stop with a report when it shows neither (a popup or captcha over the screen).
function DotCheck([string]$id, [string]$label, [string]$want) {
    $a = $m.autoMine
    $green = Rgb $a.green
    $red = Rgb $a.red
    if ($want -eq 'green') { $expect = $green; $other = $red; $candLabel = 'Auto Mine is on (green dot)' }
    else { $expect = $red; $other = $green; $candLabel = 'Auto Mine is off (red dot)' }
    [ordered]@{
        kind       = 'firstMatch'
        delayMs    = 0
        id         = $id
        label      = $label
        candidates = @(
            [ordered]@{
                delayMs      = 0
                id           = "$id-dot"
                label        = $candLabel
                x            = [int]$a.pickaxe.x
                y            = [int]$a.pickaxe.y
                button       = 1
                check        = [ordered]@{ box = (Box $a.dotBox); expect = $expect; other = $other; tolerance = [int]$a.tolerance }
                checkEnabled = $false
            }
        )
        onNoMatch  = 'skipIfOther'
    }
}

function Macro([string]$name, [object[]]$steps) {
    if (-not $Ids.ContainsKey($name)) { throw "No fixed id for macro '$name'." }
    [ordered]@{
        schemaVersion              = 4
        id                         = $Ids[$name]
        name                       = $name
        recordMode                 = 'PerWindow'
        recordedAgainstUserId      = $null
        recordedAgainstDisplayName = 'agent'
        interAltDelayMs            = $null
        recordedAtUnixMs           = [long]$m.recordedAtUnixMs
        events                     = @()
        coordSpace                 = 'client'
        recordedClientW            = [int]$m.client.w
        recordedClientH            = [int]$m.client.h
        recordedMaximized          = $false
        recordedPlaceId            = [long]$m.game.placeId
        allGames                   = $false
        recordedDisplayScale       = [int]$m.client.displayScale
        steps                      = $steps
    }
}

function Write-Macro($macro) {
    $file = $macro.id + '.json'
    $json = $macro | ConvertTo-Json -Depth 20
    [System.IO.File]::WriteAllText((Join-Path $out $file), $json + "`n", [System.Text.UTF8Encoding]::new($false))
    Write-Host "wrote $file  $($macro.name)"
}

# ---- Auto Mine toggles ----
Write-Macro (Macro 'Auto Mine off (checked)' @(DotCheck 'am-off' 'Auto Mine off' 'green'))
Write-Macro (Macro 'Auto Mine on (checked)' @(DotCheck 'am-on' 'Auto Mine on' 'red'))

# ---- The ring ----
$ring = $m.ring
$names = @($ring.spots | ForEach-Object { $_.name })
if ($names.Count -ne 8 -or @(Compare-Object $names $RingNames).Count -ne 0) {
    throw "measured.json ring.spots must name each of N, NE, E, SE, S, SW, W, NW once; it names: $($names -join ', ')."
}

# Mine spot N .. NW: Auto Mine off, hold the spot, Auto Mine on.
foreach ($spot in $ring.spots) {
    Write-Macro (Macro "Mine spot $($spot.name)" @((DotCheck 'am-off' 'Auto Mine off' 'green'), (SpotHold $spot ([int]$ring.holdDelayMs)), (DotCheck 'am-on' 'Auto Mine on' 'red')))
}

# Clear spot N .. NW: the hold alone, no delay. The pulse loop has already stopped Auto Mine and
# paused for effects to settle before it calls these.
foreach ($spot in $ring.spots) {
    Write-Macro (Macro "Clear spot $($spot.name)" @(SpotHold $spot 0))
}

# ---- Camera top-down: right-drag down past the pitch limit, then count back ----
$cam = $m.camera
$sx = [int]$cam.start.x
$sy = [int]$cam.start.y
$dy = [int][math]::Ceiling([double]$cam.pitchTravelPx * $CameraDragMargin)
if ($sy + $dy -ge [int]$m.client.h) {
    throw "The camera drag would end at y=$($sy + $dy), outside the $($m.client.h)-pixel client. Move camera.start up or shorten pitchTravelPx."
}
$camSteps = [System.Collections.Generic.List[object]]::new()
$camSteps.Add([ordered]@{ kind = 'drag'; delayMs = 0; button = 2; startX = $sx; startY = $sy; dx = 0; dy = $dy; durationMs = [int]$cam.dragMs })
if ([int]$cam.countBackPx -gt 0) {
    $camSteps.Add([ordered]@{ kind = 'drag'; delayMs = 150; button = 2; startX = $sx; startY = $sy + $dy; dx = 0; dy = -([int]$cam.countBackPx); durationMs = [int]$cam.countBackMs })
}
# Zoom: hold I to the nearest zoom, then O for outMs. Zoom speed is fixed, so from the nearest zoom
# a set hold lands on the same distance whatever the zoom was (a fresh client starts close in, which
# reads as a dark frame). Measured 2026-09-30 at the top of Mine #8: two 900 ms holds matched frame
# for frame. From the farthest zoom it would not: in a shaft the camera is pushed in by the walls,
# so the first second of zooming in shows nothing.
if ($null -ne $cam.zoom) {
    $VkI = 0x49
    $VkO = 0x4F
    $camSteps.Add([ordered]@{ kind = 'key'; delayMs = 150; virtualKeyCode = $VkI; down = $true })
    $camSteps.Add([ordered]@{ kind = 'key'; delayMs = [int]$cam.zoom.inMs; virtualKeyCode = $VkI; down = $false })
    $camSteps.Add([ordered]@{ kind = 'key'; delayMs = 150; virtualKeyCode = $VkO; down = $true })
    $camSteps.Add([ordered]@{ kind = 'key'; delayMs = [int]$cam.zoom.outMs; virtualKeyCode = $VkO; down = $false })
    $camSteps.Add([ordered]@{ kind = 'wait'; delayMs = 300 })
}
Write-Macro (Macro 'Camera top-down' $camSteps.ToArray())

# ---- Go to Top: press it, let the teleport land, Auto Mine on ----
$g = $m.goToTop
$press = [ordered]@{ kind = 'point'; delayMs = 0; id = 'go-to-top'; label = 'Go to Top'; x = [int]$g.x; y = [int]$g.y; button = 1 }
if ($null -ne $g.check) {
    $press.check = [ordered]@{ box = (Box $g.check.box); expect = (Rgb $g.check.expect); tolerance = [int]$g.check.tolerance }
    $press.checkEnabled = $true
}
Write-Macro (Macro 'Go to Top' @($press, [ordered]@{ kind = 'wait'; delayMs = [int]$g.settleMs }, (DotCheck 'am-on' 'Auto Mine on' 'red')))

# ---- Camera turn left: hold the Left arrow, release, settle so a following capture sees the
# turned view. VK_LEFT (0x25); MacroPlayer.SendKey derives the scan code Roblox requires.
$VkLeft = 0x25
$TurnSettleMs = 300
$turnHoldMs = 500
if ($null -ne $m.turn -and $null -ne $m.turn.holdMs) { $turnHoldMs = [int]$m.turn.holdMs }
Write-Macro (Macro 'Camera turn left' @(
        [ordered]@{ kind = 'key'; delayMs = 0; virtualKeyCode = $VkLeft; down = $true },
        [ordered]@{ kind = 'key'; delayMs = $turnHoldMs; virtualKeyCode = $VkLeft; down = $false },
        [ordered]@{ kind = 'wait'; delayMs = $TurnSettleMs }
    ))
