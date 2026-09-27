# Grid capture for agent-authored macros.
#
# Takes a picture of a Roblox window's GAME AREA (client area, pixel for pixel), or uses a
# picture you give it, and draws a labelled coordinate grid on it so an agent reading the
# picture can name a spot in game-area pixels. With -Macro it also draws every click of a
# recorded Ur Task macro on the picture, numbered, and writes a sidecar text file listing
# what was clicked where, and which keys were pressed between clicks.
#
# Read-only: never activates, moves, resizes, or clicks anything.
#
#   .\grid-capture.ps1                          capture every visible Roblox window, 50 px grid
#   .\grid-capture.ps1 -Grid 100                coarser grid
#   .\grid-capture.ps1 -Macro "Dig and dug"     also overlay that macro's clicks (name, id, or path)
#   .\grid-capture.ps1 -Image shot.png          grid an existing picture instead of capturing
#   .\grid-capture.ps1 -Demo                    synthetic 800x599 canvas, for checking the drawing
param(
    [string]$OutDir = $PSScriptRoot,
    [string]$Tag = "grid",
    [int]$Grid = 50,
    [string]$Macro = "",
    [string]$Image = "",
    [switch]$Demo
)

Add-Type -AssemblyName System.Drawing
Add-Type -TypeDefinition @'
using System; using System.Text; using System.Collections.Generic; using System.Runtime.InteropServices;
public static class Cap {
  [StructLayout(LayoutKind.Sequential)] public struct RECT { public int l, t, r, b; }
  [StructLayout(LayoutKind.Sequential)] public struct POINT { public int x, y; }
  public delegate bool EnumProc(IntPtr h, IntPtr p);
  [DllImport("user32.dll")] public static extern bool EnumWindows(EnumProc cb, IntPtr p);
  [DllImport("user32.dll")] public static extern uint GetWindowThreadProcessId(IntPtr h, out uint pid);
  [DllImport("user32.dll")] public static extern bool IsWindowVisible(IntPtr h);
  [DllImport("user32.dll")] public static extern bool IsIconic(IntPtr h);
  [DllImport("user32.dll")] public static extern bool GetClientRect(IntPtr h, out RECT r);
  [DllImport("user32.dll")] public static extern bool ClientToScreen(IntPtr h, ref POINT p);
  [DllImport("user32.dll")] public static extern uint GetDpiForWindow(IntPtr h);
  [DllImport("user32.dll")] public static extern bool SetProcessDpiAwarenessContext(IntPtr v);
  [DllImport("user32.dll", CharSet = CharSet.Unicode)] public static extern int GetWindowText(IntPtr h, StringBuilder sb, int n);
  public static List<IntPtr> Windows(HashSet<uint> pids) {
    var o = new List<IntPtr>();
    EnumWindows(delegate(IntPtr h, IntPtr p) {
      uint pid; GetWindowThreadProcessId(h, out pid);
      if (pids.Contains(pid) && IsWindowVisible(h) && !IsIconic(h)) {
        RECT cr; GetClientRect(h, out cr);
        if (cr.r > 100 && cr.b > 100) o.Add(h);
      }
      return true;
    }, IntPtr.Zero);
    return o;
  }
}
'@
[void][Cap]::SetProcessDpiAwarenessContext([IntPtr](-4))

# ---------------------------------------------------------------- sources
# Each source: a Bitmap plus what we know about it (scale, title, origin).
$sources = @()
if ($Demo) {
    $bmp = New-Object System.Drawing.Bitmap 800, 599
    $g = [System.Drawing.Graphics]::FromImage($bmp)
    $g.Clear([System.Drawing.Color]::FromArgb(24, 22, 60))
    $g.FillRectangle((New-Object System.Drawing.SolidBrush ([System.Drawing.Color]::FromArgb(50, 45, 110))), 0, 0, 120, 599)
    $g.Dispose()
    $sources += @{ bmp = $bmp; title = "demo"; scale = 1.0; origin = "0,0" }
} elseif ($Image -ne "") {
    if (-not (Test-Path $Image)) { "IMAGE NOT FOUND: $Image"; exit 2 }
    $bmp = [System.Drawing.Bitmap]::FromFile((Resolve-Path $Image))
    $sources += @{ bmp = $bmp; title = [IO.Path]::GetFileNameWithoutExtension($Image); scale = 1.0; origin = "n/a" }
} else {
    $pids = New-Object 'System.Collections.Generic.HashSet[uint32]'
    Get-Process RobloxPlayerBeta -ErrorAction SilentlyContinue | ForEach-Object { [void]$pids.Add([uint32]$_.Id) }
    $wins = [Cap]::Windows($pids)
    if ($wins.Count -eq 0) { "NO VISIBLE ROBLOX WINDOW on this PC (processes: $($pids.Count)). Nothing captured."; exit 2 }
    foreach ($h in $wins) {
        $cr = New-Object Cap+RECT; [void][Cap]::GetClientRect($h, [ref]$cr)
        $pt = New-Object Cap+POINT; [void][Cap]::ClientToScreen($h, [ref]$pt)
        $sb = New-Object System.Text.StringBuilder 128; [void][Cap]::GetWindowText($h, $sb, 128)
        $bmp = New-Object System.Drawing.Bitmap $cr.r, $cr.b
        $g = [System.Drawing.Graphics]::FromImage($bmp)
        $g.CopyFromScreen($pt.x, $pt.y, 0, 0, (New-Object System.Drawing.Size $cr.r, $cr.b))
        $g.Dispose()
        $sources += @{ bmp = $bmp; title = $sb.ToString(); scale = ([Cap]::GetDpiForWindow($h) / 96.0); origin = "$($pt.x),$($pt.y)" }
    }
}

# ------------------------------------------------------------------ macro
$macroObj = $null
if ($Macro -ne "") {
    $dir = Join-Path $env:LOCALAPPDATA "626Labs\RoRoRoUrTask\macros"
    $path = $null
    if (Test-Path $Macro) { $path = (Resolve-Path $Macro).Path }
    elseif (Test-Path (Join-Path $dir "$Macro.json")) { $path = Join-Path $dir "$Macro.json" }
    else {
        foreach ($f in Get-ChildItem $dir -Filter *.json -ErrorAction SilentlyContinue) {
            $j = Get-Content $f.FullName -Raw | ConvertFrom-Json
            if ($j.name -and $j.name.ToLower() -eq $Macro.ToLower()) { $path = $f.FullName; break }
        }
    }
    if (-not $path) { "MACRO NOT FOUND: $Macro (looked in $dir by id and by name)"; exit 3 }
    $macroObj = Get-Content $path -Raw | ConvertFrom-Json
    $macroObj | Add-Member -NotePropertyName _path -NotePropertyValue $path
}

# ---------------------------------------------------------------- drawing
function Draw-Label($g, [string]$text, [float]$x, [float]$y, $font, $fill) {
    # Text with a dark halo so it reads on any background.
    $path = New-Object System.Drawing.Drawing2D.GraphicsPath
    $path.AddString($text, $font.FontFamily, [int]$font.Style, $font.Size * 1.33, (New-Object System.Drawing.PointF $x, $y), [System.Drawing.StringFormat]::GenericDefault)
    $g.DrawPath((New-Object System.Drawing.Pen ([System.Drawing.Color]::FromArgb(230, 0, 0, 0)), 3), $path)
    $g.FillPath($fill, $path)
    $path.Dispose()
}

$i = 0
foreach ($src in $sources) {
    $i++
    $bmp = $src.bmp
    $w = $bmp.Width; $h = $bmp.Height
    $g = [System.Drawing.Graphics]::FromImage($bmp)
    $g.SmoothingMode = [System.Drawing.Drawing2D.SmoothingMode]::AntiAlias
    $g.TextRenderingHint = [System.Drawing.Text.TextRenderingHint]::AntiAliasGridFit

    # Grid: minor lines every Grid px, major (labelled) every 2*Grid px.
    $minor = New-Object System.Drawing.Pen ([System.Drawing.Color]::FromArgb(70, 255, 255, 255)), 1
    $major = New-Object System.Drawing.Pen ([System.Drawing.Color]::FromArgb(140, 255, 220, 0)), 1
    $labelFont = New-Object System.Drawing.Font "Consolas", 9, ([System.Drawing.FontStyle]::Bold)
    $yellow = New-Object System.Drawing.SolidBrush ([System.Drawing.Color]::FromArgb(255, 255, 220, 0))
    for ($x = 0; $x -lt $w; $x += $Grid) {
        $isMajor = (($x / $Grid) % 2) -eq 0
        $g.DrawLine($(if ($isMajor) { $major } else { $minor }), $x, 0, $x, $h)
        if ($isMajor) { Draw-Label $g "$x" ($x + 2) 1 $labelFont $yellow; Draw-Label $g "$x" ($x + 2) ($h - 16) $labelFont $yellow }
    }
    for ($y = 0; $y -lt $h; $y += $Grid) {
        $isMajor = (($y / $Grid) % 2) -eq 0
        $g.DrawLine($(if ($isMajor) { $major } else { $minor }), 0, $y, $w, $y)
        if ($isMajor -and $y -gt 0) { Draw-Label $g "$y" 2 ($y + 1) $labelFont $yellow; Draw-Label $g "$y" ($w - 34) ($y + 1) $labelFont $yellow }
    }

    # Footer: what this picture is, in the corner the game leaves emptiest.
    $info = "game area {0}x{1}  scale {2}%  grid {3} px" -f $w, $h, [math]::Round($src.scale * 100), $Grid
    Draw-Label $g $info ($w / 2 - 120) ($h - 34) $labelFont $yellow

    # Macro clicks.
    $legend = @()
    if ($macroObj) {
        $rw = $macroObj.recordedClientW; $rh = $macroObj.recordedClientH
        $sx = 1.0; $sy = 1.0
        if ($rw -and $rh -and ($rw -ne $w -or $rh -ne $h)) { $sx = $w / $rw; $sy = $h / $rh }
        $legend += "macro: $($macroObj.name)  id: $($macroObj.id)"
        $legend += "recorded on a $rw x $rh game area, coordSpace $($macroObj.coordSpace); this picture is $w x $h" + $(if ($sx -ne 1 -or $sy -ne 1) { "  (clicks scaled x$([math]::Round($sx,3)) y$([math]::Round($sy,3)))" } else { "" })
        $legend += ""
        $n = 0; $keys = @()
        $markFont = New-Object System.Drawing.Font "Consolas", 11, ([System.Drawing.FontStyle]::Bold)
        $red = New-Object System.Drawing.SolidBrush ([System.Drawing.Color]::FromArgb(255, 255, 60, 60))
        $white = New-Object System.Drawing.SolidBrush ([System.Drawing.Color]::White)
        $ring = New-Object System.Drawing.Pen ([System.Drawing.Color]::FromArgb(255, 255, 60, 60)), 2
        foreach ($e in $macroObj.events) {
            if ($e.kind -eq "KeyDown") { $keys += "vk$($e.virtualKeyCode)" + $(if ($e.virtualKeyCode -ge 48 -and $e.virtualKeyCode -le 90) { " (" + [char]$e.virtualKeyCode + ")" } else { "" }) }
            if ($e.kind -ne "MouseDown") { continue }
            $n++
            if ($keys.Count) { $legend += "    keys before click ${n}: " + ($keys -join ", "); $keys = @() }
            $px = [int]($e.x * $sx); $py = [int]($e.y * $sy)
            $btn = @{1 = "left"; 2 = "right"; 3 = "middle"}[[int]$e.mouseButton]; if (-not $btn) { $btn = "button $($e.mouseButton)" }
            $legend += ("click {0}: {1} at {2}, {3}  (t={4:N1}s)" -f $n, $btn, $e.x, $e.y, ($e.timestampMs / 1000))
            $g.DrawEllipse($ring, $px - 9, $py - 9, 18, 18)
            $g.FillEllipse($red, $px - 2, $py - 2, 4, 4)
            Draw-Label $g "$n" ($px + 9) ($py - 20) $markFont $white
        }
        if ($keys.Count) { $legend += "    keys after the last click: " + ($keys -join ", ") }
        if ($n -eq 0) { $legend += "(no mouse clicks in this macro: keyboard only)" }
    }
    $g.Dispose()

    $stamp = Get-Date -Format "HHmmss"
    $safeTag = ($Tag -replace '[^A-Za-z0-9_-]', '_')
    $base = "{0}-{1}-{2}x{3}-{4}pct-{5}" -f $safeTag, $i, $w, $h, [math]::Round($src.scale * 100), $stamp
    $png = Join-Path $OutDir "$base.png"
    $bmp.Save($png, [System.Drawing.Imaging.ImageFormat]::Png)
    $bmp.Dispose()
    "captured [{0}] game area {1}x{2} at screen {3} scale {4}% -> {5}" -f $src.title, $w, $h, $src.origin, [math]::Round($src.scale * 100), $png
    if ($legend.Count) {
        $txt = Join-Path $OutDir "$base.txt"
        $legend | Set-Content -Path $txt -Encoding utf8
        "legend -> $txt"
        $legend
    }
}
