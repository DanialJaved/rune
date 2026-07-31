# Generates the test-PDF corpus in tests/corpus/.
# Run from the repo root:  powershell -File tools\gen-corpus.ps1
# Hand-assembles PDFs (tracking xref byte offsets) so no PDF library is needed.

$ErrorActionPreference = 'Stop'
$corpusDir = Join-Path $PSScriptRoot '..\tests\corpus'
New-Item -ItemType Directory -Force $corpusDir | Out-Null

# Serializes already-rendered object bodies (1-based, in order) into a complete
# PDF: header, numbered objects, xref with byte-accurate offsets, trailer.
# Everything we emit is ASCII, so string length == byte length.
function ConvertTo-PdfBytes {
    param([string[]]$Objects)

    $parts = New-Object System.Collections.Generic.List[string]
    $header = "%PDF-1.4`n"
    $parts.Add($header)
    $pos = $header.Length
    $offsets = New-Object System.Collections.Generic.List[int]

    for ($n = 0; $n -lt $Objects.Count; $n++) {
        $offsets.Add($pos)
        $obj = "$($n + 1) 0 obj`n$($Objects[$n])`nendobj`n"
        $parts.Add($obj)
        $pos += $obj.Length
    }

    $xref = "xref`n0 $($Objects.Count + 1)`n0000000000 65535 f `n"
    foreach ($off in $offsets) { $xref += "{0:d10} 00000 n `n" -f $off }
    $parts.Add($xref)
    $parts.Add("trailer`n<< /Size $($Objects.Count + 1) /Root 1 0 R >>`nstartxref`n$pos`n%%EOF")

    return ($parts -join '')
}

# Wraps a content/appearance stream body in a stream object with /Length.
function New-StreamObj {
    param([string]$Body, [string]$ExtraDict = '')
    $dict = if ($ExtraDict) { "$ExtraDict /Length $($Body.Length)" } else { "/Length $($Body.Length)" }
    return "<< $dict >>`nstream`n$Body`nendstream"
}

function New-SimplePdf {
    param(
        [string]$Path,
        [string[][]]$Pages,        # one string[] of text lines per page
        [string]$MediaBox = '0 0 612 792'   # default US Letter portrait
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
        $objects.Add("<< /Type /Page /Parent 2 0 R /MediaBox [$MediaBox] /Resources << /Font << /F1 3 0 R >> >> /Contents $contentsRef 0 R >>")

        $lines = $Pages[$i] | ForEach-Object {
            $escaped = $_ -replace '([\\()])', '\$1'
            "($escaped) Tj T*"
        }
        # Start one line below the top of THIS page — a hardcoded y would fall
        # off the page on anything shorter than US Letter (e.g. 540pt slides).
        $pageTop = [double](($MediaBox -split '\s+')[3])
        $textY = [math]::Round($pageTop - 72)
        $stream = "BT /F1 24 Tf 72 $textY Td 18 TL " + ($lines -join ' ') + " ET"
        $objects.Add((New-StreamObj $stream))
    }

    $out = ConvertTo-PdfBytes $objects
    [System.IO.File]::WriteAllBytes($Path, [System.Text.Encoding]::ASCII.GetBytes($out))
    Write-Host "wrote $Path ($([math]::Round($out.Length / 1KB, 1)) KB, $pageCount pages)"
}

# Small two-page smoke-test file.
New-SimplePdf -Path (Join-Path $corpusDir 'hello.pdf') -Pages @(
    , @('Hello from Rune!')
    , @('Page two.')
)

# A 4:3 landscape deck (720x540 pt) — the shape a PowerPoint export takes.
# Regression fixture for thumbnails being letterboxed in portrait boxes.
New-SimplePdf -Path (Join-Path $corpusDir 'slides.pdf') -MediaBox '0 0 720 540' -Pages @(
    , @('Slide one')
    , @('Slide two')
    , @('Slide three')
)

# 1000-page "book" for performance testing (open time, mid-document render,
# virtualized scrolling). ~20 text lines per page.
$book = for ($p = 1; $p -le 1000; $p++) {
    $pageLines = @("Page $p")
    for ($l = 1; $l -le 20; $l++) {
        $pageLines += "This is line $l of page $p in the Rune test book, used to benchmark scrolling."
    }
    , $pageLines
}
New-SimplePdf -Path (Join-Path $corpusDir 'book-1000.pdf') -Pages $book

# A file with a table of contents (outline) and two links on page 0:
# one internal (GoTo page 2) and one external (URI). Hand-numbered objects.
function New-LinkedPdf {
    param([string]$Path)

    function ContentObj([string]$text) {
        return (New-StreamObj "BT /F1 24 Tf 72 700 Td ($text) Tj ET")
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

    $out = ConvertTo-PdfBytes $objects
    [System.IO.File]::WriteAllBytes($Path, [System.Text.Encoding]::ASCII.GetBytes($out))
    Write-Host "wrote $Path ($($out.Length) bytes, outline + links)"
}
New-LinkedPdf -Path (Join-Path $corpusDir 'linked.pdf')

# An AcroForm with the four widget kinds Rune needs to exercise: text field,
# combobox, checkbox and pushbutton. Fixture for form-fill round-trips
# (FORM_OnChar -> value -> save -> reopen), FFLDraw compositing and flatten.
#
# /NeedAppearances true tells the viewer to generate widget appearance streams,
# so the text field and combobox need no hand-written /AP.
#
# KNOWN LIMITATION: the checkbox's /Yes state does not render a visible tick in
# PDFium, despite the hand-written ZapfDingbats appearance below. Its *value*
# toggles and round-trips through save correctly (see FormFillTests), so this
# only affects how the fixture looks, not what it tests. Real-world PDFs ship
# their own checkbox appearance streams and draw fine.
function New-FormPdf {
    param([string]$Path)

    # Object numbering is hand-assigned; keep these in sync with the array order.
    #  1 Catalog   2 Pages   3 /Helv   4 Page   5 name (Tx)   6 country (Ch)
    #  7 agree (Btn checkbox)   8 submit (Btn pushbutton)   9 /ZaDb
    # 10 Contents  11 checkbox /Yes AP  12 checkbox /Off AP
    $acroForm = '/AcroForm << /Fields [5 0 R 6 0 R 7 0 R 8 0 R] /DA (/Helv 0 Tf 0 g) ' +
                '/DR << /Font << /Helv 3 0 R /ZaDb 9 0 R >> >> /NeedAppearances true >>'

    $labels = 'BT /F1 14 Tf 72 706 Td (Full name:) Tj ET ' +
              'BT /F1 14 Tf 72 656 Td (Country:) Tj ET ' +
              'BT /F1 14 Tf 72 606 Td (Agree to terms:) Tj ET'

    # ZapfDingbats (4) is a checkmark. The /Off state is a deliberately empty stream.
    $apResources = '/Type /XObject /Subtype /Form /BBox [0 0 20 20] /Resources << /Font << /ZaDb 9 0 R >> >>'

    $objects = @(
        "<< /Type /Catalog /Pages 2 0 R $acroForm >>",                                                            # 1
        '<< /Type /Pages /Kids [4 0 R] /Count 1 >>',                                                              # 2
        '<< /Type /Font /Subtype /Type1 /BaseFont /Helvetica /Name /Helv /Encoding /WinAnsiEncoding >>',           # 3
        ('<< /Type /Page /Parent 2 0 R /MediaBox [0 0 612 792] ' +
         '/Resources << /Font << /F1 3 0 R /Helv 3 0 R /ZaDb 9 0 R >> >> ' +
         '/Contents 10 0 R /Annots [5 0 R 6 0 R 7 0 R 8 0 R] >>'),                                                # 4
        ('<< /Type /Annot /Subtype /Widget /FT /Tx /T (name) /V () /Rect [200 700 450 724] ' +
         '/F 4 /P 4 0 R /DA (/Helv 12 Tf 0 g) /MK << /BC [0 0 0] /BG [1 1 1] >> >>'),                             # 5
        # /Ff 131072 = bit 18 (Combo): a dropdown rather than a list box.
        ('<< /Type /Annot /Subtype /Widget /FT /Ch /Ff 131072 /T (country) /V (UK) /Opt [(UK) (US) (PK)] ' +
         '/Rect [200 650 450 674] /F 4 /P 4 0 R /DA (/Helv 12 Tf 0 g) /MK << /BC [0 0 0] /BG [1 1 1] >> >>'),     # 6
        ('<< /Type /Annot /Subtype /Widget /FT /Btn /T (agree) /V /Off /AS /Off ' +
         '/Rect [200 602 220 622] /F 4 /P 4 0 R /DA (/ZaDb 0 Tf 0 g) ' +
         '/MK << /BC [0 0 0] /BG [1 1 1] /CA (4) >> /AP << /N << /Yes 11 0 R /Off 12 0 R >> >> >>'),              # 7
        # /Ff 65536 = bit 17 (Pushbutton): has no value, must not be editable.
        ('<< /Type /Annot /Subtype /Widget /FT /Btn /Ff 65536 /T (submit) /Rect [200 540 320 570] ' +
         '/F 4 /P 4 0 R /DA (/Helv 12 Tf 0 g) /MK << /BC [0 0 0] /BG [0.8 0.8 0.8] /CA (Submit) >> >>'),          # 8
        '<< /Type /Font /Subtype /Type1 /BaseFont /ZapfDingbats /Name /ZaDb >>',                                  # 9
        (New-StreamObj $labels),                                                                                  # 10
        (New-StreamObj 'q BT /ZaDb 14 Tf 0 g 3 3 Td (4) Tj ET Q' $apResources),                                   # 11
        (New-StreamObj '' $apResources)                                                                           # 12
    )

    $out = ConvertTo-PdfBytes $objects
    [System.IO.File]::WriteAllBytes($Path, [System.Text.Encoding]::ASCII.GetBytes($out))
    Write-Host "wrote $Path ($($out.Length) bytes, AcroForm: text + combo + checkbox + pushbutton)"
}
New-FormPdf -Path (Join-Path $corpusDir 'form.pdf')

# A deliberately corrupt file: must make PdfDocument.Open throw, never crash.
[System.IO.File]::WriteAllBytes((Join-Path $corpusDir 'corrupt.pdf'), [System.Text.Encoding]::ASCII.GetBytes('%PDF-1.4 this is not really a pdf'))
Write-Host 'wrote corrupt.pdf'
