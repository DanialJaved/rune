# Generates the test-PDF corpus in tests/corpus/.
# Run from the repo root:  powershell -File tools\gen-corpus.ps1
# Hand-assembles PDFs (tracking xref byte offsets) so no PDF library is needed.

$ErrorActionPreference = 'Stop'
$corpusDir = Join-Path $PSScriptRoot '..\tests\corpus'
New-Item -ItemType Directory -Force $corpusDir | Out-Null

function New-SimplePdf {
    param(
        [string]$Path,
        [string[][]]$Pages   # one string[] of text lines per page
    )

    $objects = New-Object System.Collections.Generic.List[string]

    $pageCount = $Pages.Count
    # Object numbering: 1=Catalog, 2=Pages, 3=Font, then per page i: (4+2i)=Page, (5+2i)=Contents
    $kids = (0..($pageCount - 1) | ForEach-Object { "$(4 + 2 * $_) 0 R" }) -join ' '

    $objects.Add("<< /Type /Catalog /Pages 2 0 R >>")
    $objects.Add("<< /Type /Pages /Kids [$kids] /Count $pageCount >>")
    $objects.Add("<< /Type /Font /Subtype /Type1 /BaseFont /Helvetica >>")

    for ($i = 0; $i -lt $pageCount; $i++) {
        $contentsRef = 5 + 2 * $i
        $objects.Add("<< /Type /Page /Parent 2 0 R /MediaBox [0 0 612 792] /Resources << /Font << /F1 3 0 R >> >> /Contents $contentsRef 0 R >>")

        $lines = $Pages[$i] | ForEach-Object {
            $escaped = $_ -replace '([\\()])', '\$1'
            "($escaped) Tj T*"
        }
        $stream = "BT /F1 24 Tf 72 720 Td 18 TL " + ($lines -join ' ') + " ET"
        $objects.Add("<< /Length $($stream.Length) >>`nstream`n$stream`nendstream")
    }

    # Assemble with byte-accurate offsets (everything is ASCII, so string
    # length == byte length). Parts are joined once at the end — O(n).
    $parts = New-Object System.Collections.Generic.List[string]
    $header = "%PDF-1.4`n"
    $parts.Add($header)
    $pos = $header.Length
    $offsets = New-Object System.Collections.Generic.List[int]

    for ($n = 0; $n -lt $objects.Count; $n++) {
        $offsets.Add($pos)
        $obj = "$($n + 1) 0 obj`n$($objects[$n])`nendobj`n"
        $parts.Add($obj)
        $pos += $obj.Length
    }

    $xref = "xref`n0 $($objects.Count + 1)`n0000000000 65535 f `n"
    foreach ($off in $offsets) {
        $xref += "{0:d10} 00000 n `n" -f $off
    }
    $parts.Add($xref)
    $parts.Add("trailer`n<< /Size $($objects.Count + 1) /Root 1 0 R >>`nstartxref`n$pos`n%%EOF")

    $out = $parts -join ''
    [System.IO.File]::WriteAllBytes($Path, [System.Text.Encoding]::ASCII.GetBytes($out))
    Write-Host "wrote $Path ($([math]::Round($out.Length / 1KB, 1)) KB, $pageCount pages)"
}

# Small two-page smoke-test file.
New-SimplePdf -Path (Join-Path $corpusDir 'hello.pdf') -Pages @(
    , @('Hello from Folio!')
    , @('Page two.')
)

# 1000-page "book" for performance testing (open time, mid-document render,
# virtualized scrolling). ~20 text lines per page.
$book = for ($p = 1; $p -le 1000; $p++) {
    $pageLines = @("Page $p")
    for ($l = 1; $l -le 20; $l++) {
        $pageLines += "This is line $l of page $p in the Folio test book, used to benchmark scrolling."
    }
    , $pageLines
}
New-SimplePdf -Path (Join-Path $corpusDir 'book-1000.pdf') -Pages $book

# A file with a table of contents (outline) and two links on page 0:
# one internal (GoTo page 2) and one external (URI). Hand-numbered objects.
function New-LinkedPdf {
    param([string]$Path)

    function ContentObj([string]$text) {
        $s = "BT /F1 24 Tf 72 700 Td ($text) Tj ET"
        return "<< /Length $($s.Length) >>`nstream`n$s`nendstream"
    }

    $objects = @(
        '<< /Type /Catalog /Pages 2 0 R /Outlines 6 0 R >>',                                                    # 1
        '<< /Type /Pages /Kids [3 0 R 4 0 R 5 0 R] /Count 3 >>',                                                 # 2
        '<< /Type /Page /Parent 2 0 R /MediaBox [0 0 612 792] /Resources << /Font << /F1 9 0 R >> >> /Contents 10 0 R /Annots [7 0 R 8 0 R] >>',  # 3
        '<< /Type /Page /Parent 2 0 R /MediaBox [0 0 612 792] /Resources << /Font << /F1 9 0 R >> >> /Contents 11 0 R >>',  # 4
        '<< /Type /Page /Parent 2 0 R /MediaBox [0 0 612 792] /Resources << /Font << /F1 9 0 R >> >> /Contents 12 0 R >>',  # 5
        '<< /Type /Outlines /First 13 0 R /Last 14 0 R /Count 2 >>',                                             # 6
        '<< /Type /Annot /Subtype /Link /Rect [72 680 300 720] /Border [0 0 0] /Dest [5 0 R /Fit] >>',          # 7 internal -> page 2
        '<< /Type /Annot /Subtype /Link /Rect [72 620 300 660] /Border [0 0 0] /A << /S /URI /URI (https://example.com/) >> >>',  # 8 URI
        '<< /Type /Font /Subtype /Type1 /BaseFont /Helvetica >>',                                               # 9
        (ContentObj 'Page one with links'),                                                                     # 10
        (ContentObj 'Page two'),                                                                                 # 11
        (ContentObj 'Page three'),                                                                               # 12
        '<< /Title (Chapter 1) /Parent 6 0 R /Next 14 0 R /Dest [3 0 R /Fit] >>',                               # 13 -> page 0
        '<< /Title (Chapter 2) /Parent 6 0 R /Prev 13 0 R /Dest [5 0 R /Fit] >>'                                # 14 -> page 2
    )

    $parts = New-Object System.Collections.Generic.List[string]
    $header = "%PDF-1.4`n"
    $parts.Add($header)
    $pos = $header.Length
    $offsets = New-Object System.Collections.Generic.List[int]
    for ($n = 0; $n -lt $objects.Count; $n++) {
        $offsets.Add($pos)
        $obj = "$($n + 1) 0 obj`n$($objects[$n])`nendobj`n"
        $parts.Add($obj)
        $pos += $obj.Length
    }
    $xref = "xref`n0 $($objects.Count + 1)`n0000000000 65535 f `n"
    foreach ($off in $offsets) { $xref += "{0:d10} 00000 n `n" -f $off }
    $parts.Add($xref)
    $parts.Add("trailer`n<< /Size $($objects.Count + 1) /Root 1 0 R >>`nstartxref`n$pos`n%%EOF")

    $out = $parts -join ''
    [System.IO.File]::WriteAllBytes($Path, [System.Text.Encoding]::ASCII.GetBytes($out))
    Write-Host "wrote $Path ($($out.Length) bytes, outline + links)"
}
New-LinkedPdf -Path (Join-Path $corpusDir 'linked.pdf')

# A deliberately corrupt file: must make PdfDocument.Open throw, never crash.
[System.IO.File]::WriteAllBytes((Join-Path $corpusDir 'corrupt.pdf'), [System.Text.Encoding]::ASCII.GetBytes('%PDF-1.4 this is not really a pdf'))
Write-Host 'wrote corrupt.pdf'
