# Records short looping demos of the running application for the website.
#
# The site's showcase rows are stills, and two of Rune's better moments are
# motion: dragging page thumbnails to reorder a document, and the sidebar
# sliding open before night mode inverts the page. This drives the real
# application and grabs a timed frame sequence. tools/gen-site-demos.ps1 turns
# those frames into the sprite sheets the site actually serves.
#
# The frames are real captures. index.html says "Every screenshot here is the
# real application, not a mockup", and that sentence has to keep being true.
#
# LICENCE AND PRIVACY. Shoot only a licence-safe document. PROJECT.md section 8b:
# the store screenshots use the NASA Systems Engineering Handbook (a US
# Government work, so public domain) precisely so they can be published, and
# never the user's own files, because filenames leak in the tab strip and the
# recents list. A clip shows the tab strip for its whole duration, so this
# matters more here than for a still. This script also points the application at
# a throwaway state directory through RUNE_STATE_DIR (see MainWindow.xaml.cs)
# and seeds it with a clean state.json, so a recording cannot pick up real
# recents, theme or tabs. Note RUNE_STATE_DIR redirects the state store ONLY:
# ThumbnailCache, SignatureStore and ShareService each derive %LOCALAPPDATA%\Rune
# independently. None of them is on screen for these clips, but do not assume
# full isolation.
#
# ASCII only, no BOM: see the PROJECT.md section 7 note on PowerShell 5.1
# reading a BOM-less .ps1 as ANSI, where one stray byte swallows the rest of the
# file.
#
# THE TRAPS. PROJECT.md section 13 records seven traps in this harness, each of
# which presents as an application bug rather than a script bug. What is done
# about each:
#
#  1. The 40-byte INPUT struct on x64. Sidestepped: this uses keybd_event and
#     mouse_event, which take flat scalars and no struct at all. If you ever
#     switch to SendInput, assert Marshal.SizeOf is 40 before anything else.
#  2. DPI awareness. SetProcessDpiAwarenessContext runs FIRST, before
#     System.Drawing is even loaded, and then the window size is read back and
#     asserted. Without it every capture is cropped to about 80 per cent of the
#     window, and it looks like the header's right-hand buttons have vanished.
#  3. Bounded pixel searches. This never scans for a UI element, it computes
#     from the Tokens.xaml constants and the window's real DPI. Readiness is
#     asserted on three named rectangles, all clear of the floating zoom pill.
#  4. Window rect versus client coords. Three coordinate spaces are named and
#     never mixed: client, screen (what mouse_event wants) and capture (where a
#     point lands in a PrintWindow bitmap). Both offsets are printed and
#     asserted at startup.
#  5. $input.mi.dwFlags sets a field on a copy. Sidestepped with trap 1. Note
#     PowerShell 5.1 has no [ushort] accelerator either; it is [uint16].
#  6. SetCursorPos does not inject an input event. IT IS NOT USED HERE AND MUST
#     NOT BE. WinUI's drag machinery is fed from the input queue, so with
#     SetCursorPos the ListView never crosses the drag threshold, no floating
#     thumbnail appears and DragItemsCompleted never fires. That looks exactly
#     like drag-to-reorder being broken in the application.
#  7. Press and release need about 90 ms of dwell, never a shared timestamp.
#
# Two more, found while writing this and not yet in section 13:
#
#  8. Arrow keys and PageUp/PageDown are extended keys. Without
#     KEYEVENTF_EXTENDEDKEY on both down and up, Shift+Down silently fails to
#     extend the thumbnail selection, which looks like SelectionMode="Extended"
#     being broken.
#  9. mouse_event's absolute coordinates are normalised 0..65535 over the
#     primary monitor, not pixels. Absolute rather than relative also removes
#     pointer-acceleration nondeterminism, which is what makes a take
#     repeatable.
#
# Nothing else may hold the foreground while this runs: synthetic input lands in
# whichever window has focus and the capture follows it. Start it and step away
# from the keyboard.

[CmdletBinding()]
param(
    # Which clip to shoot. Both are things the application genuinely does.
    [ValidateSet('reorder', 'nightmode')]
    [string]$Clip = 'reorder',

    # The document to film. Must be licence-safe: see the header.
    [Parameter(Mandatory = $true)]
    [string]$Pdf,

    [string]$Exe,

    [int]$Page = 1,
    [double]$Zoom = 1.0,

    # Capture size. 1920x1080 matches the ten existing store screenshots, so the
    # demos and the stills around them look like the same application.
    [int]$WindowWidth = 1920,
    [int]$WindowHeight = 1080,

    [ValidateRange(4, 32)]
    [int]$Frames = 20,
    [ValidateRange(5, 30)]
    [int]$Fps = 20,

    [int]$WarmupMs = 4500,
    [ValidateRange(1, 9)]
    [int]$Takes = 1,

    # Sidebar geometry in CAPTURE pixels. 0 means "use the computed default".
    # Read the real numbers off a -Probe frame once per document shape: the
    # thumbnail box height comes from the page's own aspect ratio, so it cannot
    # be hard-coded here.
    [int]$FirstItemY = 0,
    [int]$ItemPitch = 0,

    # Which thumbnails the reorder clip drags, and where to. Only about three
    # portrait thumbnails fit in a 1080-tall window (SidebarThumbMaxHeight is
    # 320 DIP), so the default drags items 2 and 3 up onto item 1 rather than
    # anything that would need the list to scroll.
    [int]$FromItem = 2,
    [int]$ToItem = 1,
    [ValidateRange(1, 4)]
    [int]$SelectCount = 2,

    [string]$OutRoot,

    # Write one annotated frame with a coordinate grid and the computed sidebar
    # item boundaries drawn on top, then exit. Sends no input.
    [switch]$Probe,

    # Send the input but keep no frames, for checking choreography cheaply.
    [switch]$DryRun,

    [switch]$KeepOpen,

    # Required to shoot anything whose filename does not look like the NASA
    # handbook. Read the licence note in the header before reaching for this.
    [switch]$IAcceptLicenceRisk
)

$ErrorActionPreference = 'Stop'

# ---------------------------------------------------------------- trap 2 first

# This must happen before System.Drawing loads and before any window geometry is
# read, or GetWindowRect lies to us by the display scaling factor and every
# bitmap comes out the wrong size.
Add-Type -Namespace RuneDpi -Name Api -MemberDefinition @'
    [DllImport("user32.dll", SetLastError = true)]
    public static extern bool SetProcessDpiAwarenessContext(IntPtr value);

    [DllImport("shcore.dll")]
    public static extern int SetProcessDpiAwareness(int value);

    [DllImport("shcore.dll")]
    public static extern int GetProcessDpiAwareness(IntPtr process, out int value);
'@

$dpiOk = $false
try {
    # -4 is DPI_AWARENESS_CONTEXT_PER_MONITOR_AWARE_V2.
    $dpiOk = [RuneDpi.Api]::SetProcessDpiAwarenessContext([IntPtr](-4))
} catch {
    $dpiOk = $false
}
if (-not $dpiOk) {
    try {
        # 2 is PROCESS_PER_MONITOR_DPI_AWARE. Returns 0 for S_OK, or
        # E_ACCESSDENIED if awareness has already latched for this process.
        $dpiOk = ([RuneDpi.Api]::SetProcessDpiAwareness(2) -eq 0)
    } catch {
        $dpiOk = $false
    }
}
if (-not $dpiOk) {
    # Awareness latches once per process, so both setters fail on a second run in
    # the same shell even though the process is already aware. Ask what it is
    # before giving up, or -Takes 2 and every re-run would fail on a lie.
    try {
        $current = 0
        if (([RuneDpi.Api]::GetProcessDpiAwareness([IntPtr]::Zero, [ref]$current) -eq 0) -and ($current -ge 2)) {
            $dpiOk = $true
        }
    } catch {
        $dpiOk = $false
    }
}
if (-not $dpiOk) {
    throw "Could not make this process per-monitor DPI aware, and it is not already aware. Awareness latches once per process, so run this from a fresh powershell.exe (not the ISE, and not a host that has already drawn something)."
}

# Whatever the calls above reported, the assertion that actually protects the
# capture is the window-size readback in Invoke-Take: a process that is lying
# about its awareness cannot get 1920x1080 back out of GetWindowRect.

Add-Type -AssemblyName System.Drawing

# ---------------------------------------------------------------- native

# C# 5 only: Add-Type in PowerShell 5.1 uses an old compiler, so no expression
# bodies, no nameof, no interpolated strings, no auto-property initialisers.
Add-Type -ReferencedAssemblies System.Drawing -TypeDefinition @'
using System;
using System.Drawing;
using System.Drawing.Imaging;
using System.Runtime.InteropServices;

public static class RuneCap
{
    [DllImport("user32.dll")] public static extern bool ShowWindow(IntPtr h, int cmd);
    [DllImport("user32.dll")] public static extern bool SetWindowPos(IntPtr h, IntPtr after, int x, int y, int cx, int cy, uint flags);
    [DllImport("user32.dll")] public static extern bool SetForegroundWindow(IntPtr h);
    [DllImport("user32.dll")] public static extern IntPtr GetForegroundWindow();
    [DllImport("user32.dll")] public static extern bool GetWindowRect(IntPtr h, out RECT r);
    [DllImport("user32.dll")] public static extern bool GetClientRect(IntPtr h, out RECT r);
    [DllImport("user32.dll")] public static extern bool ClientToScreen(IntPtr h, ref POINT p);
    [DllImport("user32.dll")] public static extern bool PrintWindow(IntPtr h, IntPtr hdc, uint flags);
    [DllImport("user32.dll")] public static extern int GetSystemMetrics(int i);
    [DllImport("user32.dll")] public static extern uint GetDpiForWindow(IntPtr h);
    [DllImport("user32.dll")] public static extern uint MapVirtualKey(uint code, uint mapType);

    // Deliberately the legacy flat-argument input APIs rather than SendInput:
    // there is no struct to mis-pack (PROJECT.md section 13, traps 1 and 5).
    [DllImport("user32.dll")] public static extern void keybd_event(byte vk, byte scan, uint flags, UIntPtr extra);
    [DllImport("user32.dll")] public static extern void mouse_event(uint flags, uint dx, uint dy, uint data, UIntPtr extra);

    [StructLayout(LayoutKind.Sequential)]
    public struct RECT { public int Left, Top, Right, Bottom; }

    [StructLayout(LayoutKind.Sequential)]
    public struct POINT { public int X, Y; }

    // PW_RENDERFULLCONTENT. An all-black result means "called before the window
    // first composited", not "unsupported".
    private const uint PW_RENDERFULLCONTENT = 2;

    // The Graphics is passed in and reused so the capture loop allocates
    // nothing. At 1920x1080 a fresh Graphics plus an 8 MB encode per tick would
    // blow a 50 ms frame budget on its own, and a missed tick is a visible jerk
    // in the finished sprite.
    public static bool Grab(IntPtr hwnd, Graphics g)
    {
        IntPtr hdc = g.GetHdc();
        try { return PrintWindow(hwnd, hdc, PW_RENDERFULLCONTENT); }
        finally { g.ReleaseHdc(hdc); }
    }

    // Readiness statistics over one bounded rectangle: how many distinct
    // luminance values it holds, what fraction is near-white, and its mean.
    // All three come from a single LockBits copy, because GetPixel over a
    // megapixel from PowerShell takes seconds.
    // Returns { distinctLuma, nearWhiteFraction, meanLuma }.
    public static double[] RectStats(Bitmap bmp, int x0, int y0, int x1, int y1, int step, int whiteAt)
    {
        if (step < 1) { step = 1; }
        x0 = Math.Max(0, x0); y0 = Math.Max(0, y0);
        x1 = Math.Min(bmp.Width - 1, x1); y1 = Math.Min(bmp.Height - 1, y1);
        if (x1 <= x0 || y1 <= y0) { return new double[] { 0, 0, 0 }; }

        BitmapData d = bmp.LockBits(new Rectangle(0, 0, bmp.Width, bmp.Height),
                                    ImageLockMode.ReadOnly, PixelFormat.Format32bppArgb);
        try
        {
            int stride = d.Stride;
            byte[] buf = new byte[Math.Abs(stride) * bmp.Height];
            Marshal.Copy(d.Scan0, buf, 0, buf.Length);

            bool[] seen = new bool[256];
            int distinct = 0;
            long white = 0, total = 0, sum = 0;

            for (int y = y0; y <= y1; y += step)
            {
                int row = y * stride;
                for (int x = x0; x <= x1; x += step)
                {
                    int i = row + x * 4;              // BGRA
                    int b = buf[i], g2 = buf[i + 1], r = buf[i + 2];
                    int luma = (r * 77 + g2 * 151 + b * 28) >> 8;
                    if (!seen[luma]) { seen[luma] = true; distinct++; }
                    if (luma >= whiteAt) { white++; }
                    sum += luma;
                    total++;
                }
            }
            if (total == 0) { return new double[] { 0, 0, 0 }; }
            return new double[] { distinct, (double)white / total, (double)sum / total };
        }
        finally { bmp.UnlockBits(d); }
    }
}
'@

# user32 constants. MOUSEEVENTF_MOVE and MOUSEEVENTF_ABSOLUTE must BOTH be set
# for an absolute move: $null -bor $null is 0, and the move then silently
# becomes a no-op, which is one of the ways this harness wastes an hour.
$MOUSEEVENTF_MOVE      = 0x0001
$MOUSEEVENTF_LEFTDOWN  = 0x0002
$MOUSEEVENTF_LEFTUP    = 0x0004
$MOUSEEVENTF_ABSOLUTE  = 0x8000
$KEYEVENTF_EXTENDEDKEY = 0x0001
$KEYEVENTF_KEYUP       = 0x0002
$SW_RESTORE            = 9
$SWP_NOZORDER          = 0x0004
$SM_CXSCREEN           = 0
$SM_CYSCREEN           = 1

$VK_SHIFT   = 0x10
$VK_CONTROL = 0x11
$VK_DOWN    = 0x28
$VK_I       = 0x49
$VK_F9      = 0x78

# Dwell between a press and its release. Below about 90 ms the release can land
# before the application's handler runs, and it looks like the click did nothing.
$DwellMs = 90

$repo = Split-Path -Parent $PSScriptRoot

# ---------------------------------------------------------------- helpers

# Start-Sleep and Thread.Sleep both round up to the roughly 15 ms system timer,
# which is a third of a frame at 20 fps. Everything here spins instead.
function Wait-Ms([double]$ms) {
    $w = [System.Diagnostics.Stopwatch]::StartNew()
    while ($w.Elapsed.TotalMilliseconds -lt $ms) { [System.Threading.Thread]::SpinWait(400) }
}

function Get-ScreenPoint([int]$clientX, [int]$clientY) {
    $p = New-Object 'RuneCap+POINT'
    $p.X = $clientX
    $p.Y = $clientY
    [void][RuneCap]::ClientToScreen($script:Hwnd, [ref]$p)
    return @($p.X, $p.Y)
}

function Send-MouseTo([int]$screenX, [int]$screenY) {
    # Trap 9: normalised 0..65535 over the primary monitor, not pixels.
    $nx = [uint32][Math]::Round($screenX * 65535.0 / ($script:ScreenW - 1))
    $ny = [uint32][Math]::Round($screenY * 65535.0 / ($script:ScreenH - 1))
    [RuneCap]::mouse_event([uint32]($MOUSEEVENTF_MOVE -bor $MOUSEEVENTF_ABSOLUTE), $nx, $ny, 0, [UIntPtr]::Zero)
}

function Send-MouseButton([int]$flag) {
    [RuneCap]::mouse_event([uint32]$flag, 0, 0, 0, [UIntPtr]::Zero)
}

function Send-Click([int]$clientX, [int]$clientY) {
    $s = Get-ScreenPoint $clientX $clientY
    Send-MouseTo $s[0] $s[1]
    Wait-Ms 40
    Send-MouseButton $MOUSEEVENTF_LEFTDOWN
    Wait-Ms $DwellMs
    Send-MouseButton $MOUSEEVENTF_LEFTUP
}

function Send-Key([int]$vk, [switch]$Extended) {
    $scan = [byte]([RuneCap]::MapVirtualKey([uint32]$vk, 0))
    $flags = 0
    if ($Extended) { $flags = $KEYEVENTF_EXTENDEDKEY }
    [RuneCap]::keybd_event([byte]$vk, $scan, [uint32]$flags, [UIntPtr]::Zero)
    Wait-Ms $DwellMs
    [RuneCap]::keybd_event([byte]$vk, $scan, [uint32]($flags -bor $KEYEVENTF_KEYUP), [UIntPtr]::Zero)
}

function Send-Chord([int]$modVk, [int]$vk, [switch]$Extended) {
    $modScan = [byte]([RuneCap]::MapVirtualKey([uint32]$modVk, 0))
    $keyScan = [byte]([RuneCap]::MapVirtualKey([uint32]$vk, 0))
    $flags = 0
    if ($Extended) { $flags = $KEYEVENTF_EXTENDEDKEY }

    [RuneCap]::keybd_event([byte]$modVk, $modScan, 0, [UIntPtr]::Zero)
    Wait-Ms 30
    [RuneCap]::keybd_event([byte]$vk, $keyScan, [uint32]$flags, [UIntPtr]::Zero)
    Wait-Ms $DwellMs
    [RuneCap]::keybd_event([byte]$vk, $keyScan, [uint32]($flags -bor $KEYEVENTF_KEYUP), [UIntPtr]::Zero)
    Wait-Ms 30
    [RuneCap]::keybd_event([byte]$modVk, $modScan, [uint32]$KEYEVENTF_KEYUP, [UIntPtr]::Zero)
}

# A real mouse sends about 125 moves a second and WinUI's drag-over logic wants a
# stream, not a teleport. Eased so the drag reads as a hand rather than a linear
# sweep.
function Send-Drag([int]$fromX, [int]$fromY, [int]$toX, [int]$toY, [int]$Steps, [double]$GapMs) {
    for ($s = 1; $s -le $Steps; $s++) {
        $t = $s / [double]$Steps
        if ($t -lt 0.5) { $e = 2 * $t * $t } else { $e = 1 - [Math]::Pow((-2 * $t + 2), 2) / 2 }
        $x = [int][Math]::Round($fromX + ($toX - $fromX) * $e)
        $y = [int][Math]::Round($fromY + ($toY - $fromY) * $e)
        $sp = Get-ScreenPoint $x $y
        Send-MouseTo $sp[0] $sp[1]
        Wait-Ms $GapMs
    }
}

# ---------------------------------------------------------------- inputs

$pdfPath = (Resolve-Path -LiteralPath $Pdf).Path
$pdfName = Split-Path -Leaf $pdfPath

# The licence gate. PROJECT.md section 8b: never shoot the user's own files.
if ($pdfPath -like '*\test_files\*') {
    throw "test_files/ holds the user's own documents and is never licence-cleared for publication. Shoot the NASA Systems Engineering Handbook instead (see the header)."
}
if (($pdfName -notmatch '^NASA') -and (-not $IAcceptLicenceRisk)) {
    throw "'$pdfName' does not look like the NASA handbook the published screenshots use. Its filename appears in the tab strip of every frame, and these frames get published. Pass -IAcceptLicenceRisk if you are certain this document is safe to publish."
}

if (-not $Exe) {
    $Exe = Join-Path $repo 'src\Rune.App\bin\x64\Debug\net10.0-windows10.0.19041.0\win-x64\Rune.exe'
}
if (-not (Test-Path $Exe)) {
    throw "No Rune.exe at $Exe. Build it first: dotnet build src/Rune.App/Rune.App.csproj -p:Platform=x64"
}

if (-not $OutRoot) { $OutRoot = Join-Path $repo 'artifacts\demo' }

# artifacts/ is already gitignored, so raw frames cannot be committed by
# accident. Do not stage them under site/img instead.
$clipRoot = Join-Path $OutRoot $Clip
New-Item -ItemType Directory -Path $clipRoot -Force | Out-Null

$stateDir = Join-Path $OutRoot 'state'
$ScreenW = [RuneCap]::GetSystemMetrics($SM_CXSCREEN)
$ScreenH = [RuneCap]::GetSystemMetrics($SM_CYSCREEN)

"clip        : $Clip"
"document    : $pdfName"
"exe         : $Exe"
"screen      : ${ScreenW}x${ScreenH}"
"window      : ${WindowWidth}x${WindowHeight}"
"frames      : $Frames at $Fps fps"
""

# ---------------------------------------------------------------- one take

function Invoke-Take([int]$TakeNumber) {

    # A clean state directory per take, so nothing carries over between takes
    # and nothing of the user's is ever on screen. Only Settings that matter for
    # framing are set; System.Text.Json fills the rest from the C# defaults.
    $sidebarOpen = ($Clip -eq 'reorder')
    if (Test-Path $stateDir) { Remove-Item $stateDir -Recurse -Force }
    New-Item -ItemType Directory -Path $stateDir -Force | Out-Null

    $seed = [ordered]@{
        Recents  = @()
        Session  = [ordered]@{ OpenPaths = @(); ActiveIndex = 0 }
        Settings = [ordered]@{
            # Light, not System: the ten stills already on the site are light,
            # and a dark clip beside them would look like a different product.
            Theme                = 'Light'
            NightMode            = $false
            RestoreSession       = $false
            ShowRecentThumbnails = $false
            SidebarOpenByDefault = $sidebarOpen
        }
    }
    $seed | ConvertTo-Json -Depth 5 | Set-Content -Path (Join-Path $stateDir 'state.json') -Encoding UTF8

    $env:RUNE_STATE_DIR = $stateDir

    $exeArgs = @($pdfPath, '--page', $Page, '--zoom', $Zoom)
    $proc = Start-Process -FilePath $Exe -ArgumentList $exeArgs -PassThru

    try {
        # Wait for a window handle to exist at all.
        $hwnd = [IntPtr]::Zero
        $deadline = [DateTime]::UtcNow.AddSeconds(30)
        while ([DateTime]::UtcNow -lt $deadline) {
            $proc.Refresh()
            if ($proc.HasExited) {
                throw "Rune.exe exited with code $($proc.ExitCode) before showing a window. If this is 0x800711C7, Smart App Control has flipped to Enforce and is blocking unsigned local builds (PROJECT.md section 7)."
            }
            if ($proc.MainWindowHandle -ne [IntPtr]::Zero) { $hwnd = $proc.MainWindowHandle; break }
            Start-Sleep -Milliseconds 150
        }
        if ($hwnd -eq [IntPtr]::Zero) { throw "Rune.exe never produced a main window within 30 s." }
        $script:Hwnd = $hwnd

        # Position and size, then leave it alone. Never SW_MAXIMIZE, and never
        # re-focus after sizing: PROJECT.md section 13 trap 4 records that
        # maximizing after sizing to 1920x1080 silently invalidates every
        # coordinate computed below.
        [void][RuneCap]::ShowWindow($hwnd, $SW_RESTORE)
        Start-Sleep -Milliseconds 250
        [void][RuneCap]::SetForegroundWindow($hwnd)
        Start-Sleep -Milliseconds 250
        [void][RuneCap]::SetWindowPos($hwnd, [IntPtr]::Zero, 0, 0, $WindowWidth, $WindowHeight, $SWP_NOZORDER)
        Start-Sleep -Milliseconds 400

        # Trap 2's payoff: if this process were not per-monitor aware, a 1920
        # request would read back as 1536 at 125 per cent scaling.
        $wr = New-Object 'RuneCap+RECT'
        [void][RuneCap]::GetWindowRect($hwnd, [ref]$wr)
        $winW = $wr.Right - $wr.Left
        $winH = $wr.Bottom - $wr.Top
        if ($winW -ne $WindowWidth -or $winH -ne $WindowHeight) {
            throw "Window read back as ${winW}x${winH}, not ${WindowWidth}x${WindowHeight}. That is trap 2: this process is not per-monitor DPI aware. Run from a fresh powershell.exe."
        }

        # Trap 4: name the three coordinate spaces and print the offsets between
        # them, rather than discovering a 9 px drift by eye later.
        $origin = New-Object 'RuneCap+POINT'
        $origin.X = 0
        $origin.Y = 0
        [void][RuneCap]::ClientToScreen($hwnd, [ref]$origin)
        $offX = $origin.X - $wr.Left
        $offY = $origin.Y - $wr.Top
        if ($offX -lt 0 -or $offX -gt 12 -or $offY -lt 0 -or $offY -gt 48) {
            throw "Client origin sits $offX,$offY inside the window rect, which is outside the expected range. Every computed coordinate would be wrong. Check the window is restored and unmaximized."
        }

        $dpi = [RuneCap]::GetDpiForWindow($hwnd)
        if ($dpi -le 0) { $dpi = 96 }
        $scale = $dpi / 96.0

        "take $TakeNumber : dpi $dpi (scale $([Math]::Round($scale, 3))), client offset $offX,$offY in the window rect"

        # Geometry, computed from Styles/Tokens.xaml rather than found by
        # scanning pixels (trap 3). SidebarWidth 280 DIP, SidebarThumbWidth 168
        # DIP, so a thumbnail's horizontal centre is 140 DIP in.
        $sideCentreX = [int][Math]::Round(140 * $scale)
        $firstY = $FirstItemY
        $pitch  = $ItemPitch
        if ($firstY -le 0) { $firstY = [int][Math]::Round(190 * $scale) }
        if ($pitch  -le 0) { $pitch  = [int][Math]::Round(253 * $scale) }

        # Readiness. Three named, bounded rectangles, all clear of the floating
        # zoom pill in the bottom right. PrintWindow returning solid black means
        # too early, not unsupported.
        $bmpProbe = New-Object System.Drawing.Bitmap $winW, $winH
        $gProbe = [System.Drawing.Graphics]::FromImage($bmpProbe)
        try {
            $headerX0 = [int][Math]::Round(220 * $scale)
            $headerY0 = [int][Math]::Round(46 * $scale)
            $headerY1 = [int][Math]::Round(86 * $scale)
            $pageX0 = [int][Math]::Round(300 * $scale)
            $pageX1 = $winW - [int][Math]::Round(80 * $scale)
            $pageY0 = [int][Math]::Round(100 * $scale)
            $pageY1 = $winH - [int][Math]::Round(120 * $scale)
            $sideX0 = [int][Math]::Round(16 * $scale)
            $sideX1 = [int][Math]::Round(264 * $scale)
            $sideY0 = [int][Math]::Round(100 * $scale)
            $sideY1 = $winH - [int][Math]::Round(60 * $scale)

            Wait-Ms $WarmupMs
            $ready = $false
            $why = 'never probed'
            for ($attempt = 1; $attempt -le 24; $attempt++) {
                if (-not [RuneCap]::Grab($hwnd, $gProbe)) { $why = 'PrintWindow returned false'; Wait-Ms 250; continue }

                $header = [RuneCap]::RectStats($bmpProbe, $headerX0, $headerY0, ($winW - [int](180 * $scale)), $headerY1, 4, 235)
                $page   = [RuneCap]::RectStats($bmpProbe, $pageX0, $pageY0, $pageX1, $pageY1, 8, 235)
                $side   = [RuneCap]::RectStats($bmpProbe, $sideX0, $sideY0, $sideX1, $sideY1, 8, 235)

                # The sidebar test only applies when the sidebar is meant to be
                # open. For the nightmode clip it starts closed on purpose.
                $sideOk = (-not $sidebarOpen) -or ($side[0] -ge 6)

                if ($header[0] -lt 8) { $why = "header band has only $($header[0]) distinct luminances (window has not composited)" }
                elseif ($page[1] -lt 0.55) { $why = "page area is only $([Math]::Round($page[1] * 100))% near-white (document has not rendered)" }
                elseif (-not $sideOk) { $why = "sidebar is nearly uniform ($($side[0]) luminances), so thumbnails have not arrived" }
                else { $ready = $true; break }

                Wait-Ms 250
            }
            if (-not $ready) { throw "Rune never reached a ready state: $why" }

            if ($Probe) {
                $out = Join-Path $clipRoot 'probe.png'
                $ann = New-Object System.Drawing.Bitmap $bmpProbe
                $g = [System.Drawing.Graphics]::FromImage($ann)
                try {
                    $thin = New-Object System.Drawing.Pen ([System.Drawing.Color]::FromArgb(90, 255, 0, 0)), 1
                    $bold = New-Object System.Drawing.Pen ([System.Drawing.Color]::FromArgb(220, 0, 160, 255)), 3
                    $font = New-Object System.Drawing.Font 'Consolas', 12
                    $brush = New-Object System.Drawing.SolidBrush ([System.Drawing.Color]::Red)
                    try {
                        for ($x = 0; $x -lt $winW; $x += 50) {
                            $g.DrawLine($thin, $x, 0, $x, $winH)
                            if ($x % 200 -eq 0) { $g.DrawString([string]$x, $font, $brush, $x + 2, 2) }
                        }
                        for ($y = 0; $y -lt $winH; $y += 50) {
                            $g.DrawLine($thin, 0, $y, $winW, $y)
                            if ($y % 200 -eq 0) { $g.DrawString([string]$y, $font, $brush, 2, $y + 2) }
                        }
                        # The computed thumbnail centres, so the numbers to pass
                        # back as -FirstItemY and -ItemPitch can be read off.
                        for ($n = 1; $n -le 6; $n++) {
                            $cy = $firstY + ($n - 1) * $pitch
                            if ($cy -gt $winH) { break }
                            $g.DrawLine($bold, 20, $cy, [int](300 * $scale), $cy)
                            $g.DrawString("item $n  y=$cy", $font, $brush, [int](300 * $scale) + 6, $cy - 8)
                        }
                        $g.DrawString("computed: -FirstItemY $firstY -ItemPitch $pitch  (sideCentreX $sideCentreX)",
                                      $font, $brush, 20, $winH - 40)
                    } finally {
                        $thin.Dispose(); $bold.Dispose(); $font.Dispose(); $brush.Dispose()
                    }
                } finally { $g.Dispose() }
                $ann.Save($out, [System.Drawing.Imaging.ImageFormat]::Png)
                $ann.Dispose()
                "probe      : $out"
                "             pass the item centres you can actually see back as -FirstItemY and -ItemPitch"
                return $null
            }
        } finally {
            $gProbe.Dispose()
            $bmpProbe.Dispose()
        }

        # ------------------------------------------------------------ cues

        # One clock for input and capture both, so the drag is photographed
        # while it happens instead of finishing off camera.
        $tick = 1000.0 / $Fps
        $fromY = $firstY + ($FromItem - 1) * $pitch
        # Land in the upper half of the target item, which is what tells the
        # ListView to insert above it rather than below.
        $toY = $firstY + ($ToItem - 1) * $pitch - [int]($pitch * 0.25)

        $cues = @()
        if ($Clip -eq 'reorder') {
            # Selection is built with the keyboard on purpose. ThumbList_KeyDown
            # handles only Delete, so Shift+Down falls through to the ListView's
            # own Extended-selection handling for free. That leaves exactly one
            # pointer coordinate that has to be approximately right, instead of
            # two that have to be exact.
            $cues += @{ At = 0;   Label = 'grab';      Do = { Send-MouseButton $MOUSEEVENTF_LEFTDOWN } }
            $cues += @{ At = 120; Label = 'threshold'; Do = { Send-Drag $sideCentreX $fromY $sideCentreX ($fromY - 14) 3 8 } }
            $cues += @{ At = 200; Label = 'drag';      Do = { Send-Drag $sideCentreX ($fromY - 14) $sideCentreX $toY 20 8 } }
            $cues += @{ At = 620; Label = 'hold';      Do = { } }
            $cues += @{ At = 780; Label = 'drop';      Do = { Send-MouseButton $MOUSEEVENTF_LEFTUP } }
        } else {
            $cues += @{ At = 120; Label = 'sidebar'; Do = { Send-Key $VK_F9 } }
            # The invert is a single frame, so the hold after it is the whole
            # point: a 50 ms flash reads as a glitch, not as a feature.
            $cues += @{ At = 560; Label = 'invert';  Do = { Send-Chord $VK_CONTROL $VK_I } }
        }

        # Pre-roll for the reorder clip happens before the clock starts, because
        # clicking and extending the selection is setup, not part of the shot.
        if ($Clip -eq 'reorder') {
            Send-Click $sideCentreX $fromY
            Wait-Ms 250
            for ($k = 1; $k -lt $SelectCount; $k++) {
                Send-Chord $VK_SHIFT $VK_DOWN -Extended
                Wait-Ms 120
            }
            Wait-Ms 350
            # Park the pointer on the grab point so the mouse-down cue lands
            # where the selection is.
            $sp = Get-ScreenPoint $sideCentreX $fromY
            Send-MouseTo $sp[0] $sp[1]
            Wait-Ms 120
        }

        # ------------------------------------------------------------ capture

        $bmps = New-Object 'System.Drawing.Bitmap[]' $Frames
        $gfxs = New-Object 'System.Drawing.Graphics[]' $Frames
        $stamps = New-Object 'double[]' $Frames
        try {
            for ($i = 0; $i -lt $Frames; $i++) {
                $bmps[$i] = New-Object System.Drawing.Bitmap $winW, $winH
                $gfxs[$i] = [System.Drawing.Graphics]::FromImage($bmps[$i])
            }

            $sw = [System.Diagnostics.Stopwatch]::StartNew()
            $ci = 0
            for ($i = 0; $i -lt $Frames; $i++) {
                $due = $i * $tick
                while ($true) {
                    $now = $sw.Elapsed.TotalMilliseconds
                    while ($ci -lt $cues.Count -and $cues[$ci].At -le $now) {
                        & $cues[$ci].Do
                        $ci++
                        $now = $sw.Elapsed.TotalMilliseconds
                    }
                    if ($now -ge $due) { break }
                    [System.Threading.Thread]::SpinWait(400)
                }
                [void][RuneCap]::Grab($hwnd, $gfxs[$i])
                $stamps[$i] = $sw.Elapsed.TotalMilliseconds
            }
            # Any cue left unfired means the clip is shorter than its
            # choreography, which would drop the drop off the end of the loop.
            while ($ci -lt $cues.Count) { & $cues[$ci].Do; $ci++ }

            # The cadence gate. A stalled tick is a visible jerk in the finished
            # sprite, and the tool should catch it rather than a human squinting
            # at a JPEG later.
            $gaps = @()
            for ($i = 1; $i -lt $Frames; $i++) { $gaps += ($stamps[$i] - $stamps[$i - 1]) }
            $min = ($gaps | Measure-Object -Minimum).Minimum
            $max = ($gaps | Measure-Object -Maximum).Maximum
            $mean = ($gaps | Measure-Object -Average).Average
            $limit = $tick * 1.6
            "             cadence min $([Math]::Round($min,1)) mean $([Math]::Round($mean,1)) max $([Math]::Round($max,1)) ms (nominal $([Math]::Round($tick,1)), limit $([Math]::Round($limit,1)))"

            if ($DryRun) { return $null }

            $takeDir = Join-Path $clipRoot ("take-{0:D2}" -f $TakeNumber)
            if (Test-Path $takeDir) { Remove-Item $takeDir -Recurse -Force }
            New-Item -ItemType Directory -Path $takeDir -Force | Out-Null

            for ($i = 0; $i -lt $Frames; $i++) {
                $bmps[$i].Save((Join-Path $takeDir ("{0:D4}.png" -f ($i + 1))),
                               [System.Drawing.Imaging.ImageFormat]::Png)
            }

            $ok = ($max -le $limit)
            $meta = [ordered]@{
                clip = $Clip; take = $TakeNumber; document = $pdfName
                frames = $Frames; fps = $Fps; width = $winW; height = $winH
                dpi = $dpi; scale = $scale
                firstItemY = $firstY; itemPitch = $pitch; sideCentreX = $sideCentreX
                fromItem = $FromItem; toItem = $ToItem; selectCount = $SelectCount
                cues = @($cues | ForEach-Object { "$($_.Label)@$($_.At)ms" })
                gapMinMs = [Math]::Round($min, 2); gapMeanMs = [Math]::Round($mean, 2); gapMaxMs = [Math]::Round($max, 2)
                cadenceOk = $ok
                capturedUtc = [DateTime]::UtcNow.ToString('o')
            }
            $meta | ConvertTo-Json -Depth 4 | Set-Content -Path (Join-Path $takeDir 'take.json') -Encoding UTF8

            if ($ok) {
                "             saved $Frames frames to $takeDir"
            } else {
                Write-Warning "take $TakeNumber stalled: worst gap $([Math]::Round($max,1)) ms against a $([Math]::Round($limit,1)) ms limit. Frames kept, cadenceOk=false in take.json. Shoot it again with nothing else running."
            }
            return $takeDir
        } finally {
            for ($i = 0; $i -lt $Frames; $i++) {
                if ($gfxs[$i]) { $gfxs[$i].Dispose() }
                if ($bmps[$i]) { $bmps[$i].Dispose() }
            }
        }
    } finally {
        if (-not $KeepOpen) {
            try { $proc.CloseMainWindow() | Out-Null; Start-Sleep -Milliseconds 600 } catch { }
            try { if (-not $proc.HasExited) { $proc.Kill() } } catch { }
        }
        Remove-Item Env:\RUNE_STATE_DIR -ErrorAction SilentlyContinue
    }
}

# ---------------------------------------------------------------- run

$saved = @()
$count = $Takes
if ($Probe) { $count = 1 }

for ($t = 1; $t -le $count; $t++) {
    $dir = Invoke-Take $t
    if ($dir) { $saved += $dir }
    if ($t -lt $count) { Start-Sleep -Seconds 2 }
}

""
if ($Probe) {
    "Probe only. No frames captured."
} elseif ($saved.Count -eq 0) {
    "Nothing saved."
} else {
    "Takes saved:"
    foreach ($d in $saved) { "  $d" }
    ""
    "Look at the frames before compositing, especially frame 1: check the tab strip"
    "shows only the demo document, and that the window is not cropped."
    "Then: tools\gen-site-demos.ps1 -Take @{ $Clip = <n> }"
}
