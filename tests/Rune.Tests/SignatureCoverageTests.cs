using Rune.Engine;

namespace Rune.Tests;

/// <summary>
/// Byte-range coverage is the only thing Rune actually *computes* about a
/// signature — everything else it merely reports. It is also the only part
/// testable without a real signed PDF, since a PKCS#7 signature cannot be
/// hand-authored the way the rest of tests/corpus is.
///
/// KNOWN GAP, stated rather than assumed away: PdfDocument.GetSignatures() has
/// no fixture. Its string decoding (SubFilter/Time are ASCII, Reason is UTF-16)
/// and signature enumeration are verified by hand against real signed PDFs, not
/// by this suite.
/// </summary>
public class SignatureCoverageTests
{
    // A plausible 2 KB file: signed 0..1000, gap for the /Contents literal,
    // then 1600..2000.
    private const long FileLength = 2000;

    [Fact]
    public void CanonicalRange_CoveringToEndOfFile_Covers()
    {
        int[] range = [0, 1000, 1600, 400]; // 1600 + 400 == 2000
        Assert.Equal(SignatureCoverage.CoversWholeFile, SignatureCoverageCheck.Evaluate(range, FileLength));
    }

    [Fact]
    public void ContentAppendedAfterSigning_IsDetected()
    {
        // The classic incremental-update tamper: the signed ranges stop short
        // of the file's real end.
        int[] range = [0, 1000, 1600, 400];
        Assert.Equal(SignatureCoverage.LeavesContentUnsigned, SignatureCoverageCheck.Evaluate(range, 2500));
    }

    [Fact]
    public void RangeNotStartingAtZero_LeavesHeaderUnsigned()
    {
        int[] range = [8, 992, 1600, 400];
        Assert.Equal(SignatureCoverage.LeavesContentUnsigned, SignatureCoverageCheck.Evaluate(range, FileLength));
    }

    [Fact]
    public void OversizedGap_LeavesContentUnsigned()
    {
        // A 40-byte blob needs an 82-character hex literal. A 600-byte hole
        // means real content is hiding unsigned in the middle of the file.
        int[] range = [0, 1000, 1600, 400];
        Assert.Equal(SignatureCoverage.LeavesContentUnsigned,
            SignatureCoverageCheck.Evaluate(range, FileLength, contentsLength: 40));
    }

    [Fact]
    public void GapMatchingTheContentsLiteral_StillCovers()
    {
        // 290-byte blob -> 582-char literal; the gap here is 600, within slack.
        int[] range = [0, 1000, 1600, 400];
        Assert.Equal(SignatureCoverage.CoversWholeFile,
            SignatureCoverageCheck.Evaluate(range, FileLength, contentsLength: 290));
    }

    [Theory]
    [InlineData(new int[0])]                        // absent
    [InlineData(new[] { 0 })]                       // truncated
    [InlineData(new[] { 0, 1000, 1600 })]           // odd length
    public void MalformedRange_IsUnknown_NotUncovered(int[] range)
    {
        // "We could not tell" and "it is not covered" mean very different
        // things to a reader deciding whether to trust a document.
        Assert.Equal(SignatureCoverage.Unknown, SignatureCoverageCheck.Evaluate(range, FileLength));
    }

    [Fact]
    public void NullRange_IsUnknown()
        => Assert.Equal(SignatureCoverage.Unknown, SignatureCoverageCheck.Evaluate(null, FileLength));

    [Fact]
    public void UnknownFileLength_IsUnknown()
        => Assert.Equal(SignatureCoverage.Unknown, SignatureCoverageCheck.Evaluate([0, 1000, 1600, 400], 0));

    [Theory]
    [InlineData(new[] { -1, 1000, 1600, 400 })]     // negative offset
    [InlineData(new[] { 0, -5, 1600, 400 })]        // negative length
    [InlineData(new[] { 0, 1000, 1600, 9999 })]     // runs past the file
    [InlineData(new[] { 0, 5000, 1600, 400 })]      // first range alone overruns
    public void NonsenseValues_AreUnknown_NotEvidenceOfTampering(int[] range)
        => Assert.Equal(SignatureCoverage.Unknown, SignatureCoverageCheck.Evaluate(range, FileLength));

    [Fact]
    public void OverlappingRanges_AreUnknown()
    {
        int[] range = [0, 1000, 500, 1500]; // second starts inside the first
        Assert.Equal(SignatureCoverage.Unknown, SignatureCoverageCheck.Evaluate(range, FileLength));
    }

    [Fact]
    public void SingleRangeCoveringEverything_Covers()
        => Assert.Equal(SignatureCoverage.CoversWholeFile, SignatureCoverageCheck.Evaluate([0, 2000], FileLength));

    [Fact]
    public void UnsignedDocument_ReportsNoSignatures()
    {
        using var doc = PdfDocument.Open(PixelAssert.CorpusPath("hello.pdf"));

        Assert.Equal(0, doc.SignatureCount);
        Assert.Empty(doc.GetSignatures());
    }

    [Theory]
    [InlineData("D:20260730120000+01'00'", 2026, 7, 30, 12)]
    [InlineData("D:20240101093000Z", 2024, 1, 1, 9)]
    [InlineData("20240101093000", 2024, 1, 1, 9)]     // some writers omit the D:
    public void PdfDate_IsParsed(string raw, int year, int month, int day, int hour)
    {
        var info = new PdfSignatureInfo(0, "", "", raw, 0, SignatureCoverage.Unknown, 0);

        Assert.NotNull(info.SignedAt);
        Assert.Equal(year, info.SignedAt!.Value.Year);
        Assert.Equal(month, info.SignedAt.Value.Month);
        Assert.Equal(day, info.SignedAt.Value.Day);
        Assert.Equal(hour, info.SignedAt.Value.Hour);
    }

    [Theory]
    [InlineData("")]
    [InlineData("not a date")]
    [InlineData("D:2026")]
    [InlineData("D:20261345999999")]                  // month 13, day 45
    public void MalformedDate_IsNull_NotAnException(string raw)
    {
        var info = new PdfSignatureInfo(0, "", "", raw, 0, SignatureCoverage.Unknown, 0);
        Assert.Null(info.SignedAt);
    }

    [Theory]
    [InlineData(0u, false)]
    [InlineData(1u, true)]
    [InlineData(3u, true)]
    [InlineData(9u, false)]
    public void DocMdpPermission_IdentifiesCertifyingSignatures(uint permission, bool expected)
    {
        var info = new PdfSignatureInfo(0, "", "", "", 0, SignatureCoverage.Unknown, permission);
        Assert.Equal(expected, info.IsCertifying);
    }
}
