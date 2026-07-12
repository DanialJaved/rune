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

# A deliberately corrupt file: must make PdfDocument.Open throw, never crash.
[System.IO.File]::WriteAllBytes((Join-Path $corpusDir 'corrupt.pdf'), [System.Text.Encoding]::ASCII.GetBytes('%PDF-1.4 this is not really a pdf'))
Write-Host 'wrote corrupt.pdf'
