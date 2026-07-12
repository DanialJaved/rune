# Generates the minimal test-PDF corpus in tests/corpus/.
# Run from the repo root:  powershell -File tools\gen-corpus.ps1
# Hand-assembles PDFs (tracking xref byte offsets) so no PDF library is needed.

$ErrorActionPreference = 'Stop'
$corpusDir = Join-Path $PSScriptRoot '..\tests\corpus'
New-Item -ItemType Directory -Force $corpusDir | Out-Null

function New-SimplePdf {
    param(
        [string]$Path,
        [string[]]$PageTexts   # one entry per page
    )

    $objects = @()   # object bodies, index = object number - 1

    $pageCount = $PageTexts.Count
    # Object numbering: 1=Catalog, 2=Pages, 3=Font, then per page i: (4+2i)=Page, (5+2i)=Contents
    $kids = (0..($pageCount - 1) | ForEach-Object { "$(4 + 2 * $_) 0 R" }) -join ' '

    $objects += "<< /Type /Catalog /Pages 2 0 R >>"
    $objects += "<< /Type /Pages /Kids [$kids] /Count $pageCount >>"
    $objects += "<< /Type /Font /Subtype /Type1 /BaseFont /Helvetica >>"

    for ($i = 0; $i -lt $pageCount; $i++) {
        $contentsRef = 5 + 2 * $i
        $objects += "<< /Type /Page /Parent 2 0 R /MediaBox [0 0 612 792] /Resources << /Font << /F1 3 0 R >> >> /Contents $contentsRef 0 R >>"
        $text = $PageTexts[$i] -replace '([\\()])', '\$1'
        $stream = "BT /F1 36 Tf 72 700 Td ($text) Tj ET"
        $objects += "<< /Length $($stream.Length) >>`nstream`n$stream`nendstream"
    }

    # Assemble with byte-accurate offsets (everything is ASCII).
    $out = "%PDF-1.4`n"
    $offsets = @()
    for ($n = 0; $n -lt $objects.Count; $n++) {
        $offsets += $out.Length
        $out += "$($n + 1) 0 obj`n$($objects[$n])`nendobj`n"
    }

    $xrefPos = $out.Length
    $out += "xref`n0 $($objects.Count + 1)`n0000000000 65535 f `n"
    foreach ($off in $offsets) {
        $out += "{0:d10} 00000 n `n" -f $off
    }
    $out += "trailer`n<< /Size $($objects.Count + 1) /Root 1 0 R >>`nstartxref`n$xrefPos`n%%EOF"

    [System.IO.File]::WriteAllBytes($Path, [System.Text.Encoding]::ASCII.GetBytes($out))
    Write-Host "wrote $Path ($($out.Length) bytes, $pageCount pages)"
}

New-SimplePdf -Path (Join-Path $corpusDir 'hello.pdf') -PageTexts @('Hello from Folio!', 'Page two.')

# A deliberately corrupt file: must make PdfDocument.Open throw, never crash.
[System.IO.File]::WriteAllBytes((Join-Path $corpusDir 'corrupt.pdf'), [System.Text.Encoding]::ASCII.GetBytes('%PDF-1.4 this is not really a pdf'))
Write-Host 'wrote corrupt.pdf'
