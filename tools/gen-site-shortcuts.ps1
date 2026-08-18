# Rewrites the shortcut table in site/features.html from the application's own
# catalogue, so the website and the F1 overlay cannot disagree.
#
# ShortcutCatalog.cs is already declared "the single source of truth for every
# keyboard shortcut Rune ships". Hand-copying it onto a web page would create a
# fourth copy (app, PROJECT.md, README, site) and they have drifted before.
#
# The catalogue's shape is regular and this parser expects exactly it:
#
#     new("Group title",
#     [
#         new("What it does", "The keys"),
#     ]),
#
# Anything else is ignored, so a malformed edit shows up as a missing row rather
# than as garbage on the site. The script FAILS if it parses nothing at all.
#
# ASCII only in this file (PROJECT.md section 7: PowerShell 5.1 reads a BOM-less
# .ps1 as ANSI, and a stray non-ASCII character swallows the rest of the file).
# The catalogue it reads is UTF-8 and full of arrow glyphs, hence -Encoding UTF8
# on the way in and a no-BOM UTF-8 write on the way out.

[CmdletBinding()]
param()

$ErrorActionPreference = 'Stop'

$repo    = Split-Path -Parent $PSScriptRoot
$catalog = Join-Path $repo 'src\Rune.App\ShortcutCatalog.cs'
$page    = Join-Path $repo 'site\features.html'

foreach ($p in @($catalog, $page)) {
    if (-not (Test-Path $p)) { throw "Missing $p" }
}

function Get-HtmlEncoded([string]$s) {
    $s -replace '&', '&amp;' -replace '<', '&lt;' -replace '>', '&gt;'
}

# A chord gets <kbd>; prose like "toolbar" or "Ctrl+K, type a number" reads
# better as plain text.
#
# The test asks what is NOT a chord rather than what is, because the catalogue
# is full of arrow glyphs and this file has to stay ASCII (see the header), so
# they cannot be matched literally. Prose is either punctuated with a comma or
# made only of lower-case words; everything else, arrows included, is a chord.
# -cmatch, not -match: PowerShell's -match is case-insensitive, which would
# call "PgUp" lower-case and strip its <kbd>.
function Format-Keys([string]$keys) {
    $parts = $keys -split ' / '
    $out = foreach ($part in $parts) {
        $t = $part.Trim()
        if ($t -match ',' -or $t -cmatch '^[a-z][a-z ]*$') {
            Get-HtmlEncoded $t
        } else {
            '<kbd>' + (Get-HtmlEncoded $t) + '</kbd>'
        }
    }
    $out -join ' / '
}

$lines = Get-Content $catalog -Encoding UTF8

$groups = New-Object System.Collections.ArrayList
$current = $null

foreach ($line in $lines) {
    # A group opens with a name and no second argument on the same line.
    if ($line -match '^\s*new\("([^"]+)",\s*$') {
        $current = [ordered]@{ Title = $Matches[1]; Items = (New-Object System.Collections.ArrayList) }
        [void]$groups.Add($current)
        continue
    }
    # A shortcut carries both a name and its keys.
    if ($current -ne $null -and $line -match '^\s*new\("([^"]+)",\s*"([^"]+)"\)') {
        [void]$current.Items.Add([ordered]@{ Name = $Matches[1]; Keys = $Matches[2] })
    }
}

$groups = @($groups | Where-Object { $_.Items.Count -gt 0 })
if ($groups.Count -eq 0) {
    throw "Parsed no shortcuts from $catalog. Has its shape changed?"
}

$sb = New-Object System.Text.StringBuilder
foreach ($g in $groups) {
    [void]$sb.AppendLine('      <h3>' + (Get-HtmlEncoded $g.Title) + '</h3>')
    [void]$sb.AppendLine('      <div class="tablewrap">')
    [void]$sb.AppendLine('        <table>')
    [void]$sb.AppendLine('          <thead><tr><th>Action</th><th>Keys</th></tr></thead>')
    [void]$sb.AppendLine('          <tbody>')
    foreach ($i in $g.Items) {
        [void]$sb.AppendLine('            <tr><td>' + (Get-HtmlEncoded $i.Name) + '</td><td>' + (Format-Keys $i.Keys) + '</td></tr>')
    }
    [void]$sb.AppendLine('          </tbody>')
    [void]$sb.AppendLine('        </table>')
    [void]$sb.AppendLine('      </div>')
    [void]$sb.AppendLine('')
}

$begin = '<!-- BEGIN GENERATED SHORTCUTS'
$end   = '<!-- END GENERATED SHORTCUTS -->'

$html = [System.IO.File]::ReadAllText($page)
$iBegin = $html.IndexOf($begin)
$iEnd   = $html.IndexOf($end)
if ($iBegin -lt 0 -or $iEnd -lt 0) { throw "Marker comments not found in $page" }

# Keep the opening marker (it carries the do-not-edit note) and replace only
# what sits between it and the closing one.
$iAfterBegin = $html.IndexOf('-->', $iBegin) + 3
$updated = $html.Substring(0, $iAfterBegin) + "`r`n" + $sb.ToString() + '      ' + $html.Substring($iEnd)

$utf8NoBom = New-Object System.Text.UTF8Encoding($false)
[System.IO.File]::WriteAllText($page, $updated, $utf8NoBom)

$count = ($groups | ForEach-Object { $_.Items.Count } | Measure-Object -Sum).Sum
"Wrote $count shortcuts in $($groups.Count) groups into site/features.html"
foreach ($g in $groups) { "  {0,-22} {1}" -f $g.Title, $g.Items.Count }
