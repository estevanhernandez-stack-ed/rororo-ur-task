<#
.SYNOPSIS
    Counts how often something steals your foreground window. Read-only.

.DESCRIPTION
    The v0.7.0 cadence claim is a number: an Active alt should take focus roughly once per 30
    seconds, where the old spin loop took it about once per second. That is a 30x difference, which
    is easy to feel and easy to be wrong about — "it seems better" is not a smoke result.

    This samples the foreground window and reports the actual rate, so the smoke passes or fails on
    a measurement instead of an impression.

    Touches nothing. No admin. Ctrl+C stops early and still reports.

.EXAMPLE
    powershell -ExecutionPolicy Bypass -File build\measure-focus-steals.ps1 -Minutes 5

.EXAMPLE
    # Baseline the OLD behaviour first, on main, for a before/after you can trust.
    powershell -ExecutionPolicy Bypass -File build\measure-focus-steals.ps1 -Minutes 5 -Note "v0.6.0 baseline"
#>
param(
    # double, not int: -Minutes 0.5 silently truncated to 0, took one sample, and reported an
    # empty result with no hint that the duration was the problem.
    [double]$Minutes = 5,
    [int]$SampleMs = 250,
    [string]$Note = "",
    # Run anyway with no alts up — for measuring an idle baseline on purpose.
    [switch]$Force
)

$ErrorActionPreference = 'Stop'

Add-Type @"
using System;
using System.Runtime.InteropServices;
using System.Text;
public class Fg {
  [DllImport("user32.dll")] public static extern IntPtr GetForegroundWindow();
  [DllImport("user32.dll")] public static extern uint GetWindowThreadProcessId(IntPtr h, out uint pid);
  [DllImport("user32.dll")] public static extern int GetWindowText(IntPtr h, StringBuilder s, int n);
  public static string TitleOf(IntPtr h) { var sb = new StringBuilder(300); GetWindowText(h, sb, 300); return sb.ToString(); }
  public static uint PidOf(IntPtr h) { uint pid; GetWindowThreadProcessId(h, out pid); return pid; }
}
"@

Write-Host ""
Write-Host "Focus-steal measurement" -ForegroundColor Cyan
Write-Host ("  duration : {0} min, sampling every {1} ms" -f $Minutes, $SampleMs)
if ($Note) { Write-Host ("  note     : {0}" -f $Note) }
Write-Host ""
Write-Host "Go and use the machine normally - type in a window, browse, whatever you would do" -ForegroundColor Yellow
Write-Host "while alts are being kept alive. Ctrl+C stops early and still reports." -ForegroundColor Yellow
Write-Host ""

# Preconditions. A 5-minute run against a machine with no alts measures a quiet desktop and
# reports a confident zero, which reads exactly like a pass. Check before spending the time.
$alts = @(Get-Process RobloxPlayerBeta -ErrorAction SilentlyContinue | Where-Object { $_.PrivateMemorySize64 -gt 500MB })
$host_ = @(Get-Process ROROROblox* -ErrorAction SilentlyContinue)
if ($alts.Count -eq 0 -or $host_.Count -eq 0) {
    Write-Host ""
    $verdict = if ($Force) { "NOTHING TO MEASURE - running anyway (-Force)." } else { "NOTHING TO MEASURE - not starting." }
    Write-Host $verdict -ForegroundColor Red
    Write-Host ("  RoRoRo running : {0}" -f $(if ($host_.Count) { "yes" } else { "NO" })) -ForegroundColor Red
    Write-Host ("  live alts      : {0}" -f $alts.Count) -ForegroundColor Red
    Write-Host ""
    Write-Host "This test measures how often the cadence scheduler steals focus. With no alts" -ForegroundColor Yellow
    Write-Host "being kept alive there is nothing to steal it, and the run would report 0 -" -ForegroundColor Yellow
    Write-Host "which looks exactly like a pass and means nothing." -ForegroundColor Yellow
    Write-Host ""
    Write-Host "Start RoRoRo, start Ur Task, launch at least 2 alts (one set Active), then re-run."
    Write-Host "Override with -Force if you are deliberately measuring a baseline." -ForegroundColor DarkGray
    Write-Host ""
    if (-not $Force) { exit 1 }
}

# Prove the instrument works before asking anyone to trust its total. If this line is blank,
# "(unknown)", or never changes while you alt-tab, the measurement is not reading your desktop and
# the number at the end would be a confident zero.
$probe = [Fg]::GetForegroundWindow()
if ($probe -eq [IntPtr]::Zero) {
    Write-Host "WARNING: cannot read the foreground window in this context." -ForegroundColor Red
    Write-Host "Run this from a normal PowerShell window on your own desktop, not over a remote" -ForegroundColor Red
    Write-Host "session or from a scheduled task, or every count below will read zero." -ForegroundColor Red
    Write-Host ""
} else {
    $probeName = "(unknown)"
    try { $probeName = (Get-Process -Id ([int][Fg]::PidOf($probe)) -ErrorAction Stop).ProcessName } catch {}
    Write-Host ("  reading now : {0}  <- alt-tab and you should see lines appear below" -f $probeName)
    Write-Host ""
}

$deadline = (Get-Date).AddMinutes($Minutes)
$start = Get-Date
$events = New-Object System.Collections.ArrayList
$lastPid = -1
$lastName = ""

try {
    do {
        $h = [Fg]::GetForegroundWindow()
        if ($h -ne [IntPtr]::Zero) {
            $pid2 = [int][Fg]::PidOf($h)
            if ($pid2 -ne $lastPid) {
                $name = "(unknown)"
                try { $name = (Get-Process -Id $pid2 -ErrorAction Stop).ProcessName } catch {}
                # First sample is not a steal, it is just where focus already was.
                if ($lastPid -ne -1) {
                    [void]$events.Add([pscustomobject]@{
                        At   = Get-Date
                        From = $lastName
                        To   = $name
                    })
                    $colour = if ($name -like "RobloxPlayerBeta*") { "Yellow" } else { "DarkGray" }
                    Write-Host ("  {0}  {1} -> {2}" -f (Get-Date -Format "HH:mm:ss"), $lastName, $name) -ForegroundColor $colour
                }
                $lastPid = $pid2
                $lastName = $name
            }
        }
        if ((Get-Date) -ge $deadline) { break }
        Start-Sleep -Milliseconds $SampleMs
    } while ($true)
}
finally {
    $elapsed = ((Get-Date) - $start).TotalMinutes
    if ($elapsed -le 0) { $elapsed = 0.01 }

    $toRoblox = @($events | Where-Object { $_.To -like "RobloxPlayerBeta*" })
    $rate = [math]::Round($toRoblox.Count / $elapsed, 1)
    $secondsEach = if ($toRoblox.Count -gt 0) { [math]::Round(($elapsed * 60) / $toRoblox.Count, 1) } else { 0 }

    Write-Host ""
    Write-Host "=== PASTE THIS BACK ===" -ForegroundColor Green
    Write-Host ""
    Write-Host ("measured for        : {0:N1} min" -f $elapsed)
    Write-Host ("focus changes total : {0}" -f $events.Count)
    if ($events.Count -eq 0) {
        Write-Host ""
        Write-Host "  ^ ZERO transitions of ANY kind, including your own window switches." -ForegroundColor Red
        Write-Host "    That is not a quiet scheduler, it is a run that measured nothing." -ForegroundColor Red
        Write-Host "    Either no alts were being kept alive, or you never changed windows." -ForegroundColor Red
        Write-Host "    DO NOT record this as a pass." -ForegroundColor Red
    }
    Write-Host ("steals by Roblox    : {0}" -f $toRoblox.Count)
    Write-Host ("  -> rate           : {0} per minute" -f $rate)
    if ($toRoblox.Count -gt 0) {
        Write-Host ("  -> one every      : {0} seconds" -f $secondsEach)
    }
    if ($Note) { Write-Host ("note                : {0}" -f $Note) }
    Write-Host ""
    Write-Host "Expected for v0.7.0: roughly one steal per 30s per Active alt (2/min for one alt)."
    Write-Host "The v0.6.0 spin loop was about one per second (60/min). If you are seeing tens per"
    Write-Host "minute, the scheduler is not doing its job and the smoke has FAILED."
    Write-Host ""
}
