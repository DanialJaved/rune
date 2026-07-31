# Rasterizes Rune's app mark from assets/rune.svg (and rune-small.svg below
# 32px) into every icon asset the app and the Store need:
#   src/Rune.App/Assets/rune.ico   (16/24/32/48/64/256, PNG-compressed entries)
#   src/Rune.App/Assets/*.png      (MSIX visual assets, scale-100..400)
# Run from repo root:  powershell -File tools\gen-icon.ps1
#
# The SVG is the design source of truth; edit it, not this script. What follows
# is NOT a general SVG renderer — it understands only the subset documented at
# the top of assets/rune.svg. Rendering goes through WPF (Geometry.Parse +
# RenderTargetBitmap) so the repo keeps its "no external tools" property;
# PowerShell has no in-box SVG rasterizer, and shelling out to Inkscape or
# resvg would add a dependency this project deliberately does without.

$ErrorActionPreference = 'Stop'
Add-Type -AssemblyName PresentationCore, PresentationFramework, WindowsBase, System.Drawing

$root = Join-Path $PSScriptRoot '..'
$assets = Join-Path $root 'src\Rune.App\Assets'
New-Item -ItemType Directory -Force $assets | Out-Null

function ConvertTo-Brush {
    param([string]$Spec, [xml]$Doc, [double]$Size)

    if ($Spec -notmatch '^url\(#(.+)\)$') {
        return New-Object System.Windows.Media.SolidColorBrush(
            [System.Windows.Media.ColorConverter]::ConvertFromString($Spec))
    }

    # The single supported gradient form: a vertical linearGradient by id.
    $id = $Matches[1]
    $node = $Doc.svg.defs.linearGradient | Where-Object { $_.id -eq $id }
    if (-not $node) { throw "gradient '$id' not found in the SVG" }

    $brush = New-Object System.Windows.Media.LinearGradientBrush
    $brush.StartPoint = New-Object System.Windows.Point 0, 0
    $brush.EndPoint = New-Object System.Windows.Point 0, 1
    foreach ($stop in $node.stop) {
        $brush.GradientStops.Add((New-Object System.Windows.Media.GradientStop(
            [System.Windows.Media.ColorConverter]::ConvertFromString($stop.'stop-color'),
            [double]$stop.offset)))
    }
    return $brush
}

# Renders one SVG file at the given pixel size onto a transparent surface.
function Convert-SvgToBitmap {
    param([string]$SvgPath, [int]$Size)

    [xml]$doc = Get-Content -Raw -LiteralPath $SvgPath
    # viewBox is "0 0 W H"; everything below is expressed in those units.
    $view = [double](($doc.svg.viewBox -split '\s+')[2])
    $scale = $Size / $view

    $visual = New-Object System.Windows.Media.DrawingVisual
    $ctx = $visual.RenderOpen()
    $ctx.PushTransform((New-Object System.Windows.Media.ScaleTransform $scale, $scale))

    # <rect> — only the tile uses this, and only with a uniform corner radius.
    foreach ($rect in @($doc.svg.rect)) {
        if (-not $rect) { continue }
        $r = New-Object System.Windows.Rect ([double]$rect.x), ([double]$rect.y), ([double]$rect.width), ([double]$rect.height)
        $radius = if ($rect.rx) { [double]$rect.rx } else { 0 }
        $ctx.DrawRoundedRectangle((ConvertTo-Brush $rect.fill $doc $view), $null, $r, $radius, $radius)
    }

    # <path> — filled when it carries fill, stroked when it carries stroke.
    foreach ($path in @($doc.svg.path)) {
        if (-not $path) { continue }
        $geometry = [System.Windows.Media.Geometry]::Parse($path.d)

        if ($path.fill) {
            $ctx.DrawGeometry((ConvertTo-Brush $path.fill $doc $view), $null, $geometry)
        }
        if ($path.stroke) {
            $pen = New-Object System.Windows.Media.Pen(
                (ConvertTo-Brush $path.stroke $doc $view),
                [double]$path.'stroke-width')
            # Round caps/joins are assumed rather than parsed — the mark is
            # drawn with them throughout and nothing else has looked right.
            $pen.StartLineCap = 'Round'
            $pen.EndLineCap = 'Round'
            $pen.LineJoin = 'Round'
            $ctx.DrawGeometry($null, $pen, $geometry)
        }
    }

    $ctx.Pop()
    $ctx.Close()

    $rtb = New-Object System.Windows.Media.Imaging.RenderTargetBitmap(
        $Size, $Size, 96, 96, [System.Windows.Media.PixelFormats]::Pbgra32)
    $rtb.Render($visual)
    return $rtb
}

# Below 32px the fold and the thin strokes collapse, so small sizes get their
# own art rather than a scaled-down version of the large one.
$svgLarge = Join-Path $root 'assets\rune.svg'
$svgSmall = Join-Path $root 'assets\rune-small.svg'
function Get-Source([int]$Size) { if ($Size -lt 32) { $svgSmall } else { $svgLarge } }

function Save-Bitmap {
    param($Bitmap, [string]$Path)
    $encoder = New-Object System.Windows.Media.Imaging.PngBitmapEncoder
    $encoder.Frames.Add([System.Windows.Media.Imaging.BitmapFrame]::Create($Bitmap))
    $stream = [System.IO.File]::Create($Path)
    $encoder.Save($stream)
    $stream.Dispose()
    Write-Host "wrote $(Split-Path -Leaf $Path)"
}

# Square art centred on a wider canvas (wide tile, splash screen).
function Save-Centred {
    param([int]$CanvasW, [int]$CanvasH, [int]$Art, [string]$Path)
    $visual = New-Object System.Windows.Media.DrawingVisual
    $ctx = $visual.RenderOpen()
    $ctx.DrawImage((Convert-SvgToBitmap (Get-Source $Art) $Art),
        (New-Object System.Windows.Rect ([double](($CanvasW - $Art) / 2)), ([double](($CanvasH - $Art) / 2)), ([double]$Art), ([double]$Art)))
    $ctx.Close()
    $rtb = New-Object System.Windows.Media.Imaging.RenderTargetBitmap(
        $CanvasW, $CanvasH, 96, 96, [System.Windows.Media.PixelFormats]::Pbgra32)
    $rtb.Render($visual)
    Save-Bitmap $rtb $Path
}

# ---- MSIX visual assets ----------------------------------------------------
# The previous set was scale-200 only, which is the bare minimum the Store
# accepts; Windows picks the nearest and rescales, which is why the taskbar
# icon looked soft. These are the scales Windows actually asks for.
$square = @(
    @{ Name = 'Square44x44Logo.scale-100.png'; Size = 44 },
    @{ Name = 'Square44x44Logo.scale-125.png'; Size = 55 },
    @{ Name = 'Square44x44Logo.scale-150.png'; Size = 66 },
    @{ Name = 'Square44x44Logo.scale-200.png'; Size = 88 },
    @{ Name = 'Square44x44Logo.scale-400.png'; Size = 176 },
    @{ Name = 'Square44x44Logo.targetsize-16.png'; Size = 16 },
    @{ Name = 'Square44x44Logo.targetsize-24.png'; Size = 24 },
    @{ Name = 'Square44x44Logo.targetsize-32.png'; Size = 32 },
    @{ Name = 'Square44x44Logo.targetsize-48.png'; Size = 48 },
    @{ Name = 'Square44x44Logo.targetsize-256.png'; Size = 256 },
    @{ Name = 'Square44x44Logo.targetsize-24_altform-unplated.png'; Size = 24 },
    @{ Name = 'Square44x44Logo.targetsize-48_altform-unplated.png'; Size = 48 },
    @{ Name = 'Square44x44Logo.targetsize-256_altform-unplated.png'; Size = 256 },
    @{ Name = 'Square150x150Logo.scale-100.png'; Size = 150 },
    @{ Name = 'Square150x150Logo.scale-125.png'; Size = 188 },
    @{ Name = 'Square150x150Logo.scale-150.png'; Size = 225 },
    @{ Name = 'Square150x150Logo.scale-200.png'; Size = 300 },
    @{ Name = 'Square150x150Logo.scale-400.png'; Size = 600 },
    @{ Name = 'StoreLogo.png'; Size = 50 },
    @{ Name = 'StoreLogo.scale-200.png'; Size = 100 }
)
foreach ($spec in $square) {
    Save-Bitmap (Convert-SvgToBitmap (Get-Source $spec.Size) $spec.Size) (Join-Path $assets $spec.Name)
}

Save-Centred 310 150 150 (Join-Path $assets 'Wide310x150Logo.scale-100.png')
Save-Centred 620 300 300 (Join-Path $assets 'Wide310x150Logo.scale-200.png')
Save-Centred 620 300 300 (Join-Path $assets 'SplashScreen.scale-100.png')
Save-Centred 1240 600 600 (Join-Path $assets 'SplashScreen.scale-200.png')

# ---- Multi-size .ico (PNG-compressed entries) ------------------------------
$sizes = 16, 24, 32, 48, 64, 256
$pngs = foreach ($s in $sizes) {
    $encoder = New-Object System.Windows.Media.Imaging.PngBitmapEncoder
    $encoder.Frames.Add([System.Windows.Media.Imaging.BitmapFrame]::Create((Convert-SvgToBitmap (Get-Source $s) $s)))
    $ms = New-Object System.IO.MemoryStream
    $encoder.Save($ms)
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
