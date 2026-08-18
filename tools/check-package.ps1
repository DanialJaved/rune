<#
.SYNOPSIS
    Asserts that a built Rune package actually contains a .NET runtime.

.DESCRIPTION
    Through v0.7.0 every Store package shipped without one. The project set
    WindowsAppSDKSelfContained (which bundles the Windows App SDK) but not
    SelfContained (which bundles the .NET runtime), and the MSIX is packaged
    from a plain `dotnet build` rather than from the `dotnet publish` with the
    self-contained flag that the portable zip uses. The result installed,
    certified and launched straight into Windows' "you must install .NET"
    download dialog.

    It is a silent failure. Nothing in the build, the upload or certification
    notices, and it cannot reproduce on a developer's machine because a
    developer's machine has the runtime. The only place it shows is inside the
    artifact, so that is where this looks.

    Handles all three shapes: a .msixupload contains a .msixbundle contains one
    .msix per architecture. EVERY architecture is checked, because an ARM64 leg
    that quietly resolved no runtime pack would otherwise ship broken to exactly
    the users least able to work around it.

    A bundle also carries RESOURCE packages (scale-100, scale-125 and so on),
    which hold scaled images and no code whatsoever. Those are skipped, and the
    test for "is this a package that needs a runtime" is whether it contains an
    executable at all rather than whether its name looks like a resource - the
    naming is a convention, the .exe is the actual thing being asked about.

    Nested containers are unpacked to TEMP FILES rather than to byte arrays. A
    self-contained bundle is ~135 MB and each package inside it ~130 MB, which
    overflows a MemoryStream round-trip and fails with "Array dimensions
    exceeded supported range" - an out-of-memory error wearing a confusing hat.

    ASCII ONLY, deliberately. Windows PowerShell 5.1 reads a .ps1 with no BOM as
    ANSI, so a UTF-8 em dash arrives as three CP1252 characters, the last of
    which is a smart quote that opens a string and swallows the rest of the
    file. The parse error it produces points at a brace forty lines away.

.EXAMPLE
    tools/check-package.ps1 artifacts/store/Rune.App_0.8.0.0_x64_arm64_bundle.msixupload
#>
[CmdletBinding()]
param(
    [Parameter(Mandatory = $true, Position = 0)]
    [string]$Path
)

Set-StrictMode -Version Latest
$ErrorActionPreference = 'Stop'
# Both: ZipFile is in the FileSystem assembly, ZipArchive and friends are in
# System.IO.Compression. Loading only one gets you a missing-type error for
# whichever half you did not load.
Add-Type -AssemblyName System.IO.Compression
Add-Type -AssemblyName System.IO.Compression.FileSystem

# The apphost needs hostfxr to find a runtime at all; coreclr and CoreLib are
# the runtime itself. Any one of them missing means the app cannot start
# without a machine-wide install.
$Required = @('hostfxr.dll', 'coreclr.dll', 'System.Private.CoreLib.dll')

if (-not (Test-Path -LiteralPath $Path)) {
    Write-Error "No such package: $Path"
    exit 2
}

$full = (Resolve-Path -LiteralPath $Path).Path
$temps = New-Object System.Collections.ArrayList

function Get-EntryNames([string]$zipPath) {
    $zip = [System.IO.Compression.ZipFile]::OpenRead($zipPath)
    try { return @($zip.Entries | ForEach-Object { $_.FullName }) }
    finally { $zip.Dispose() }
}

function Expand-Entry([string]$zipPath, [string]$entryName) {
    $zip = [System.IO.Compression.ZipFile]::OpenRead($zipPath)
    try {
        $entry = $zip.GetEntry($entryName)
        if ($null -eq $entry) { throw "No entry '$entryName' in $zipPath." }

        $out = [System.IO.Path]::GetTempFileName()
        [void]$temps.Add($out)
        $src = $entry.Open()
        try {
            $dst = [System.IO.File]::Create($out)
            try { $src.CopyTo($dst) } finally { $dst.Dispose() }
        }
        finally { $src.Dispose() }
        return $out
    }
    finally { $zip.Dispose() }
}

try {
    # Peel the containers down to one file per architecture package. A bare
    # .msix is already there; an upload has a bundle in it, and the bundle has
    # the packages.
    $packages = @{}

    $bundles = @(Get-EntryNames $full | Where-Object { $_ -match '\.(msixbundle|appxbundle)$' })
    if ($bundles.Count -gt 0) {
        foreach ($bundleName in $bundles) {
            $bundlePath = Expand-Entry $full $bundleName
            foreach ($inner in @(Get-EntryNames $bundlePath | Where-Object { $_ -match '\.(msix|appx)$' })) {
                $packages[$inner] = Expand-Entry $bundlePath $inner
            }
        }
    }
    else {
        $inners = @(Get-EntryNames $full | Where-Object { $_ -match '\.(msix|appx)$' })
        if ($inners.Count -gt 0) {
            foreach ($inner in $inners) { $packages[$inner] = Expand-Entry $full $inner }
        }
        else {
            # A bare .msix: the thing itself is the package.
            $packages[[System.IO.Path]::GetFileName($full)] = $full
        }
    }

    if ($packages.Count -eq 0) {
        Write-Error "Found no architecture packages inside $full."
        exit 2
    }

    Write-Output "Checking $([System.IO.Path]::GetFileName($full)) - $($packages.Count) package(s)"

    $failed = $false
    $checked = 0
    foreach ($name in ($packages.Keys | Sort-Object)) {
        $entries = @(Get-EntryNames $packages[$name])
        $files = @($entries | ForEach-Object { Split-Path $_ -Leaf })

        # No executable, no runtime needed: this is a resource package.
        if (-not ($entries | Where-Object { $_ -match '^[^/]+\.exe$' })) {
            Write-Output "  skip  $name (resources only)"
            continue
        }

        $checked++
        $missing = @($Required | Where-Object { $files -notcontains $_ })

        if ($missing.Count -eq 0) {
            Write-Output "  OK    $name"
        }
        else {
            Write-Output "  FAIL  $name - missing $($missing -join ', ')"
            $failed = $true
        }
    }

    if ($checked -eq 0) {
        Write-Error "No package in $full contains an executable. Nothing was actually checked."
        exit 2
    }

    if ($failed) {
        Write-Output ""
        Write-Output "This package has no .NET runtime in it. Installing it from the Store"
        Write-Output "would prompt the user to download .NET on first launch."
        Write-Output "Check that <SelfContained>true</SelfContained> is still set in Rune.App.csproj."
        exit 1
    }

    Write-Output "Every package carries its own .NET runtime."
    exit 0
}
finally {
    foreach ($t in $temps) {
        try { Remove-Item -LiteralPath $t -Force -ErrorAction Stop } catch { }
    }
}
