# Builds the website's images from assets already in the repo.
#
# The Store screenshots are 1920x1080 PNGs totalling ~3.3 MB, which is fine for
# Partner Center and far too heavy for a landing page. This resizes them and
# re-encodes as JPEG, which is the right format here: they are photographs of a
# UI over a document, not flat graphics.
#
# The source set is deliberately licence-safe (shot against the NASA Systems
# Engineering Handbook, a US Government work and so public domain) precisely so
# it can be published. See PROJECT.md section 8b. Never point this at anything
# shot over the user's own files.
#
# ASCII only, no BOM issues: see the PROJECT.md section 7 note on PowerShell 5.1
# reading a BOM-less .ps1 as ANSI.

[CmdletBinding()]
param(
    [int]$Width = 1280,
    [int]$Quality = 82
)

$ErrorActionPreference = 'Stop'
Add-Type -AssemblyName System.Drawing

$repo = Split-Path -Parent $PSScriptRoot
$src  = Join-Path $repo 'docs\store-screenshots'
$dest = Join-Path $repo 'site\img'

if (-not (Test-Path $src)) { throw "No screenshots at $src" }
if (-not (Test-Path $dest)) { New-Item -ItemType Directory -Path $dest -Force | Out-Null }

# The JPEG encoder has to be looked up by MIME type; there is no friendlier API.
$encoder = [System.Drawing.Imaging.ImageCodecInfo]::GetImageEncoders() |
    Where-Object { $_.MimeType -eq 'image/jpeg' }
$params = New-Object System.Drawing.Imaging.EncoderParameters 1
$params.Param[0] = New-Object System.Drawing.Imaging.EncoderParameter(
    [System.Drawing.Imaging.Encoder]::Quality, [int64]$Quality)

$total = 0
foreach ($file in Get-ChildItem $src -Filter *.png | Sort-Object Name) {
    $img = [System.Drawing.Image]::FromFile($file.FullName)
    try {
        $h = [int][Math]::Round($img.Height * ($Width / [double]$img.Width))
        $bmp = New-Object System.Drawing.Bitmap $Width, $h
        try {
            $g = [System.Drawing.Graphics]::FromImage($bmp)
            try {
                # Downscaling a screenshot with anything less than these three
                # set together produces visible ringing on the UI's thin lines.
                $g.InterpolationMode = [System.Drawing.Drawing2D.InterpolationMode]::HighQualityBicubic
                $g.PixelOffsetMode   = [System.Drawing.Drawing2D.PixelOffsetMode]::HighQuality
                $g.SmoothingMode     = [System.Drawing.Drawing2D.SmoothingMode]::HighQuality
                $g.DrawImage($img, 0, 0, $Width, $h)
            } finally { $g.Dispose() }

            $out = Join-Path $dest ($file.BaseName + '.jpg')
            $bmp.Save($out, $encoder, $params)
            $kb = [int]((Get-Item $out).Length / 1KB)
            $total += $kb
            "{0,-28} {1}x{2} -> {3}x{4}  {5} KB" -f $file.Name, $img.Width, $img.Height, $Width, $h, $kb
        } finally { $bmp.Dispose() }
    } finally { $img.Dispose() }
}

# The app icon doubles as the site's logo and favicon source.
$logo = Join-Path $repo 'src\Rune.App\Assets\Square44x44Logo.targetsize-256.png'
if (Test-Path $logo) {
    Copy-Item $logo (Join-Path $dest 'rune-256.png') -Force
    "rune-256.png copied from the app's own icon assets"
}

"total: $total KB in site/img"
