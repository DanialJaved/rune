# Turns captured frames into the looping demos the website serves.
#
# tools/capture-demo.ps1 records real frames of the running application. This
# stacks a take into one vertical JPEG strip and writes the matching element
# into site/index.html, where CSS walks the strip with steps(). No JavaScript,
# no video element, no codec matrix, and the only binary media format on the
# site stays the one it already ships.
#
# Why a vertical strip and not a row or a grid: 20 frames at 640 px wide is
# 12,800 px across, past what mobile Safari will decode, and a grid needs two
# animations on one element whose relative drift shows up as a frame jumping
# sideways. A strip needs exactly one animation.
#
# THE FRAME COUNT LIVES IN THE MARKUP, NOT THE STYLESHEET. This script writes
# --frames, --fw and --fh into the generated element, so the JPEG and the CSS
# cannot drift apart when the frame count changes. Same reason
# gen-site-shortcuts.ps1 generates the shortcut table instead of anyone
# hand-copying it.
#
# Deterministic and re-runnable: given the same take it produces the same bytes.
# That is why it is a separate script from the capture, which is
# timing-dependent and needs several attempts.
#
# ASCII only, no BOM: see the PROJECT.md section 7 note on PowerShell 5.1
# reading a BOM-less .ps1 as ANSI, where one stray byte swallows the rest of the
# file.

[CmdletBinding()]
param(
    # Which take to use per clip, e.g. -Take @{ reorder = 2; nightmode = 1 }.
    # Omit a clip and the newest take is used when there is only one candidate;
    # with several, this fails and lists them, because choosing the good take is
    # a judgement call that needs eyes on the frames.
    [hashtable]$Take = @{},

    [string]$FramesRoot,

    # 640 px native against a showcase column that renders about 551 CSS px, so
    # roughly 1.16x. Sharper than 1x and honestly softer than the 1280 px stills
    # beside it, which land at about 2.3x.
    [ValidateRange(320, 1280)]
    [int]$FrameWidth = 640,

    # Lower than the stills' 82 on purpose. At 640x360 the application's text is
    # already illegible, so the strip carries the shape of the motion rather
    # than its content, and there is nothing readable left to protect.
    [ValidateRange(40, 95)]
    [int]$Quality = 62,

    # Per-strip ceiling. JPEG is intra-only, so there is no inter-frame saving
    # anywhere in this scheme and cost is roughly linear in frame count. This
    # throws rather than quietly shipping a 900 KB sheet: cut frames first, then
    # quality, and only then give up on the clip and use a still.
    [int]$MaxKB = 260,

    # Longest side that decodes reliably everywhere, iOS included.
    [int]$MaxSheetPx = 8000,

    [switch]$NoHtml
)

$ErrorActionPreference = 'Stop'
Add-Type -AssemblyName System.Drawing

$repo = Split-Path -Parent $PSScriptRoot
if (-not $FramesRoot) { $FramesRoot = Join-Path $repo 'artifacts\demo' }
$dest = Join-Path $repo 'site\img'
$page = Join-Path $repo 'site\index.html'

if (-not (Test-Path $FramesRoot)) {
    throw "No captured frames at $FramesRoot. Record some first: tools\capture-demo.ps1 -Clip reorder -Pdf <the NASA handbook>"
}
if (-not (Test-Path $dest)) { New-Item -ItemType Directory -Path $dest -Force | Out-Null }
if (-not (Test-Path $page)) { throw "Missing $page" }

# The JPEG encoder has to be looked up by MIME type; there is no friendlier API.
# Same three lines as gen-site-images.ps1.
$encoder = [System.Drawing.Imaging.ImageCodecInfo]::GetImageEncoders() |
    Where-Object { $_.MimeType -eq 'image/jpeg' }
$params = New-Object System.Drawing.Imaging.EncoderParameters 1
$params.Param[0] = New-Object System.Drawing.Imaging.EncoderParameter(
    [System.Drawing.Imaging.Encoder]::Quality, [int64]$Quality)

# Clip order is fixed so a re-run reports in the same order every time.
$clips = @('reorder', 'nightmode')

function Select-TakeDir([string]$clip) {
    $clipRoot = Join-Path $FramesRoot $clip
    if (-not (Test-Path $clipRoot)) { return $null }

    $takes = @(Get-ChildItem $clipRoot -Directory -Filter 'take-*' | Sort-Object Name)
    if ($takes.Count -eq 0) { return $null }

    if ($Take.ContainsKey($clip)) {
        $want = 'take-{0:D2}' -f [int]$Take[$clip]
        $hit = $takes | Where-Object { $_.Name -eq $want }
        if (-not $hit) {
            throw "$clip has no $want. Available: $(($takes | ForEach-Object { $_.Name }) -join ', ')"
        }
        return $hit
    }

    if ($takes.Count -eq 1) { return $takes[0] }

    # Several takes and no choice made. Report what each one measured rather
    # than guessing, because only a human looking at the frames can tell a good
    # drag from one that dropped on the wrong item.
    $lines = foreach ($t in $takes) {
        $metaPath = Join-Path $t.FullName 'take.json'
        $note = 'no take.json'
        if (Test-Path $metaPath) {
            $m = Get-Content $metaPath -Raw | ConvertFrom-Json
            $note = "frames $($m.frames), worst gap $($m.gapMaxMs) ms, cadenceOk $($m.cadenceOk)"
        }
        "    $($t.Name): $note"
    }
    throw ("$clip has $($takes.Count) takes and no -Take choice. Look at the frames, then pick one:`n" +
           ($lines -join "`n") + "`n  e.g. -Take @{ $clip = 2 }")
}

function New-Strip($takeDir, [string]$clip) {
    $frames = @(Get-ChildItem $takeDir.FullName -Filter '*.png' | Sort-Object Name)
    if ($frames.Count -eq 0) {
        throw "$($takeDir.FullName) holds no PNG frames. A malformed input has to be a hard error, not garbage on the site."
    }

    # Frame geometry comes from the first frame, so a capture at a different
    # window size still composites correctly and the markup carries the real
    # aspect ratio rather than an assumed 16:9.
    $first = [System.Drawing.Image]::FromFile($frames[0].FullName)
    try {
        $srcW = $first.Width
        $srcH = $first.Height
    } finally { $first.Dispose() }

    $frameH = [int][Math]::Round($srcH * ($FrameWidth / [double]$srcW))
    $sheetH = $frameH * $frames.Count
    if ($sheetH -gt $MaxSheetPx) {
        throw ("$clip would make a ${FrameWidth}x${sheetH} strip, past the $MaxSheetPx px that decodes " +
               "reliably everywhere. Shoot fewer frames or pass a smaller -FrameWidth.")
    }

    $sheet = New-Object System.Drawing.Bitmap $FrameWidth, $sheetH
    try {
        $g = [System.Drawing.Graphics]::FromImage($sheet)
        try {
            # All three, set together. gen-site-images.ps1's header records that
            # anything less produces visible ringing on the UI's thin lines, and
            # a 3x reduction of application chrome is exactly that case.
            $g.InterpolationMode = [System.Drawing.Drawing2D.InterpolationMode]::HighQualityBicubic
            $g.PixelOffsetMode   = [System.Drawing.Drawing2D.PixelOffsetMode]::HighQuality
            $g.SmoothingMode     = [System.Drawing.Drawing2D.SmoothingMode]::HighQuality

            $y = 0
            foreach ($f in $frames) {
                $img = [System.Drawing.Image]::FromFile($f.FullName)
                try {
                    if ($img.Width -ne $srcW -or $img.Height -ne $srcH) {
                        throw "$($f.Name) is $($img.Width)x$($img.Height) but the take starts at ${srcW}x${srcH}. Frames in one take must all be the same size."
                    }
                    $g.DrawImage($img, 0, $y, $FrameWidth, $frameH)
                } finally { $img.Dispose() }
                $y += $frameH
            }
        } finally { $g.Dispose() }

        $out = Join-Path $dest "demo-$clip.jpg"
        $sheet.Save($out, $encoder, $params)
    } finally { $sheet.Dispose() }

    $kb = [int]((Get-Item $out).Length / 1KB)
    if ($kb -gt $MaxKB) {
        Remove-Item $out -Force
        throw ("$clip came out at $kb KB against a $MaxKB KB budget, so it was not kept. " +
               "Cut frames first (16 instead of 20 saves about a fifth), then -Quality, " +
               "and only then ship that row as a still.")
    }

    return [ordered]@{
        clip = $clip
        frames = $frames.Count
        frameWidth = $FrameWidth
        frameHeight = $frameH
        sheetHeight = $sheetH
        kb = $kb
        take = $takeDir.Name
    }
}

# ---------------------------------------------------------------- build

$built = @()
foreach ($clip in $clips) {
    $takeDir = Select-TakeDir $clip
    if (-not $takeDir) {
        "skip  $clip : no takes recorded yet"
        continue
    }
    $info = New-Strip $takeDir $clip
    $built += $info
    "{0,-10} {1} frames from {2} -> {3}x{4}  {5} KB" -f `
        $info.clip, $info.frames, $info.take, $info.frameWidth, $info.sheetHeight, $info.kb
}

if ($built.Count -eq 0) {
    throw "Built nothing. Record a take first with tools\capture-demo.ps1."
}

$total = ($built | ForEach-Object { $_.kb } | Measure-Object -Sum).Sum
"total: $total KB of demo strips in site/img"

if ($NoHtml) { return }

# ---------------------------------------------------------------- markup

# One marker pair per clip, so the prose in each showcase row stays hand-written
# and only the element itself is generated. Same splice as
# gen-site-shortcuts.ps1: keep the opening marker, which carries the
# do-not-edit note, and replace only what sits between it and the closing one.
$html = [System.IO.File]::ReadAllText($page)

foreach ($info in $built) {
    $clip = $info.clip
    $begin = "<!-- BEGIN GENERATED DEMO $clip"
    $end   = "<!-- END GENERATED DEMO $clip -->"

    $iBegin = $html.IndexOf($begin)
    $iEnd   = $html.IndexOf($end)
    if ($iBegin -lt 0 -or $iEnd -lt 0) {
        throw "Marker comments for '$clip' not found in $page. Expected '$begin ... -->' and '$end'."
    }
    $iAfterBegin = $html.IndexOf('-->', $iBegin) + 3

    # url() in an inline style attribute resolves against the DOCUMENT, not the
    # stylesheet. index.html and style.css are siblings here so img/... is right
    # either way, but it is the kind of thing that works locally and 404s on the
    # /rune/ Pages subpath, so keep it relative and keep it document-relative.
    $div = '      <div class="demo" aria-hidden="true" style="--sheet:url(img/demo-' + $clip +
           '.jpg); --frames:' + $info.frames + '; --fw:' + $info.frameWidth +
           '; --fh:' + $info.frameHeight + '"></div>'

    $html = $html.Substring(0, $iAfterBegin) + "`n" + $div + "`n      " + $html.Substring($iEnd)
}

$utf8NoBom = New-Object System.Text.UTF8Encoding($false)
[System.IO.File]::WriteAllText($page, $html, $utf8NoBom)

"wrote $($built.Count) demo elements into site/index.html"
