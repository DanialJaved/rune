# Generates Rune's app icon: a rounded accent-blue tile with a white runic
# "R" (raido rune) drawn as clean strokes. Outputs:
#   src/Rune.App/Assets/rune.ico            (16/24/32/48/64/256, PNG-compressed)
#   src/Rune.App/Assets/*.png               (MSIX visual assets)
# Run from repo root:  powershell -File tools\gen-icon.ps1

$ErrorActionPreference = 'Stop'
Add-Type -AssemblyName System.Drawing

$assets = Join-Path $PSScriptRoot '..\src\Rune.App\Assets'
New-Item -ItemType Directory -Force $assets | Out-Null

function Draw-RuneTile {
    param([int]$Size, [bool]$Transparent = $false)

    $bmp = New-Object System.Drawing.Bitmap($Size, $Size)
    $g = [System.Drawing.Graphics]::FromImage($bmp)
    $g.SmoothingMode = 'AntiAlias'

    $accent = [System.Drawing.Color]::FromArgb(255, 0, 99, 177)   # Windows accent blue
    $accent2 = [System.Drawing.Color]::FromArgb(255, 0, 120, 212)

    if (-not $Transparent) {
        # Rounded-rect tile with a subtle vertical gradient.
        $radius = [Math]::Max(2, [int]($Size * 0.22))
        $path = New-Object System.Drawing.Drawing2D.GraphicsPath
        $d = $radius * 2
        $path.AddArc(0, 0, $d, $d, 180, 90)
        $path.AddArc($Size - $d, 0, $d, $d, 270, 90)
        $path.AddArc($Size - $d, $Size - $d, $d, $d, 0, 90)
        $path.AddArc(0, $Size - $d, $d, $d, 90, 90)
        $path.CloseFigure()
        $brush = New-Object System.Drawing.Drawing2D.LinearGradientBrush(
            (New-Object System.Drawing.Point(0, 0)),
            (New-Object System.Drawing.Point(0, $Size)),
            $accent2, $accent)
        $g.FillPath($brush, $path)
        $brush.Dispose(); $path.Dispose()
    }

    # Raido rune (runic R): vertical stave + angled bow + leg, as strokes.
    $w = [Math]::Max(1.5, $Size * 0.09)
    $pen = New-Object System.Drawing.Pen([System.Drawing.Color]::White, $w)
    $pen.StartCap = 'Round'; $pen.EndCap = 'Round'; $pen.LineJoin = 'Round'

    $x0 = $Size * 0.36      # stave x
    $top = $Size * 0.22
    $bottom = $Size * 0.78
    $mid = $Size * 0.52
    $x1 = $Size * 0.64      # bow tip x

    $g.DrawLine($pen, [single]$x0, [single]$top, [single]$x0, [single]$bottom)                 # stave
    $g.DrawLine($pen, [single]$x0, [single]$top, [single]$x1, [single]($Size * 0.34))          # bow upper
    $g.DrawLine($pen, [single]$x1, [single]($Size * 0.34), [single]$x0, [single]$mid)          # bow lower
    $g.DrawLine($pen, [single]$x0, [single]$mid, [single]$x1, [single]$bottom)                 # leg

    $pen.Dispose(); $g.Dispose()
    return $bmp
}

function Save-Png([System.Drawing.Bitmap]$bmp, [string]$path) {
    $bmp.Save($path, [System.Drawing.Imaging.ImageFormat]::Png)
    Write-Host "wrote $(Split-Path -Leaf $path)"
}

# ---- MSIX visual assets ----------------------------------------------------
foreach ($spec in @(
    @{ Name = 'Square44x44Logo.scale-200.png';   Size = 88 },
    @{ Name = 'Square44x44Logo.targetsize-24_altform-unplated.png'; Size = 24 },
    @{ Name = 'Square150x150Logo.scale-200.png'; Size = 300 },
    @{ Name = 'Wide310x150Logo.scale-200.png';   Size = 300 },   # square art centered on wide canvas below
    @{ Name = 'StoreLogo.png';                   Size = 50 }
)) {
    if ($spec.Name -like 'Wide*') {
        $wide = New-Object System.Drawing.Bitmap(620, 300)
        $g = [System.Drawing.Graphics]::FromImage($wide)
        $tile = Draw-RuneTile -Size 300
        $g.DrawImage($tile, 160, 0)
        $g.Dispose(); $tile.Dispose()
        Save-Png $wide (Join-Path $assets $spec.Name)
        $wide.Dispose()
    }
    else {
        $tile = Draw-RuneTile -Size $spec.Size
        Save-Png $tile (Join-Path $assets $spec.Name)
        $tile.Dispose()
    }
}

# Splash screen: 1240x600, tile centered.
$splash = New-Object System.Drawing.Bitmap(1240, 600)
$g = [System.Drawing.Graphics]::FromImage($splash)
$tile = Draw-RuneTile -Size 300
$g.DrawImage($tile, 470, 150)
$g.Dispose(); $tile.Dispose()
Save-Png $splash (Join-Path $assets 'SplashScreen.scale-200.png')
$splash.Dispose()

# ---- Multi-size .ico (PNG-compressed entries) ------------------------------
$sizes = 16, 24, 32, 48, 64, 256
$pngs = foreach ($s in $sizes) {
    $bmp = Draw-RuneTile -Size $s
    $ms = New-Object System.IO.MemoryStream
    $bmp.Save($ms, [System.Drawing.Imaging.ImageFormat]::Png)
    $bmp.Dispose()
    , $ms.ToArray()
}

$icoPath = Join-Path $assets 'rune.ico'
$stream = [System.IO.File]::Create($icoPath)
$writer = New-Object System.IO.BinaryWriter($stream)
$writer.Write([uint16]0)               # reserved
$writer.Write([uint16]1)               # type: icon
$writer.Write([uint16]$sizes.Count)    # image count
$offset = 6 + 16 * $sizes.Count
for ($i = 0; $i -lt $sizes.Count; $i++) {
    $s = $sizes[$i]
    $writer.Write([byte]($s -band 0xFF))   # width (0 = 256)
    $writer.Write([byte]($s -band 0xFF))   # height
    $writer.Write([byte]0)                 # palette
    $writer.Write([byte]0)                 # reserved
    $writer.Write([uint16]1)               # planes
    $writer.Write([uint16]32)              # bpp
    $writer.Write([uint32]$pngs[$i].Length)
    $writer.Write([uint32]$offset)
    $offset += $pngs[$i].Length
}
foreach ($png in $pngs) { $writer.Write($png) }
$writer.Dispose(); $stream.Dispose()
Write-Host "wrote rune.ico ($($sizes -join '/'))"
