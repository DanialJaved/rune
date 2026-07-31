namespace Rune.Engine;

/// <summary>
/// How much of the file a signature's /ByteRange actually covers.
///
/// This is deliberately NOT a validity verdict. Rune has no cryptography: it
/// cannot check the certificate chain, the digest, trust, or revocation. What
/// it can do is read the byte range the signer claimed to sign and compare it
/// with the file on disk — which catches the specific, common case of content
/// appended after signing.
/// </summary>
public enum SignatureCoverage
{
    /// <summary>The byte range is missing or malformed; draw no conclusion.</summary>
    Unknown,

    /// <summary>The signed ranges span the whole file.</summary>
    CoversWholeFile,

    /// <summary>Part of the file lies outside the signed ranges — it was changed or appended after signing.</summary>
    LeavesContentUnsigned,
}

/// <summary>
/// Pure byte-range arithmetic, kept separate from PDFium so it can be tested
/// exhaustively without a signed fixture (which cannot be hand-authored).
/// </summary>
public static class SignatureCoverageCheck
{
    /// <summary>
    /// Evaluates a PDF /ByteRange.
    ///
    /// The canonical form is [0, a, b, c]: bytes 0..a and b..b+c are signed,
    /// and the gap between them is the hex /Contents literal holding the
    /// signature itself. Full coverage means the first range starts at 0 and
    /// the last one ends at the end of the file.
    ///
    /// Never throws — malformed input yields <see cref="SignatureCoverage.Unknown"/>,
    /// because "we could not tell" and "it is not covered" mean very different
    /// things to someone deciding whether to trust a document.
    /// </summary>
    /// <param name="byteRange">Offset/length pairs, as read from the signature dictionary.</param>
    /// <param name="fileLength">Size of the PDF on disk, in bytes.</param>
    /// <param name="contentsLength">Decoded length of the PKCS#7 blob, used as a gap sanity check.</param>
    public static SignatureCoverage Evaluate(IReadOnlyList<int>? byteRange, long fileLength, long contentsLength = 0)
    {
        if (byteRange is null || byteRange.Count < 2 || byteRange.Count % 2 != 0 || fileLength <= 0)
        {
            return SignatureCoverage.Unknown;
        }

        long covered = 0;
        long expectedNextStart = 0;
        long gapTotal = 0;

        for (int i = 0; i < byteRange.Count; i += 2)
        {
            long offset = byteRange[i];
            long length = byteRange[i + 1];

            if (offset < 0 || length < 0 || offset > fileLength || offset + length > fileLength)
            {
                return SignatureCoverage.Unknown; // nonsense values, not evidence of tampering
            }

            if (offset < expectedNextStart)
            {
                return SignatureCoverage.Unknown; // overlapping or unordered ranges
            }

            gapTotal += offset - expectedNextStart;
            covered += length;
            expectedNextStart = offset + length;
        }

        // Anything after the final signed range is unsigned trailing content —
        // the incremental-update trick used to alter a signed document.
        if (expectedNextStart != fileLength)
        {
            return SignatureCoverage.LeavesContentUnsigned;
        }

        // The only legitimate gap is the /Contents hex literal: two hex digits
        // per byte plus the angle brackets. A materially larger hole means real
        // content is sitting unsigned in the middle of the file.
        if (contentsLength > 0)
        {
            long minimumLiteral = (contentsLength * 2) + 2;
            if (gapTotal > minimumLiteral + 64)
            {
                return SignatureCoverage.LeavesContentUnsigned;
            }
        }

        // A first range that does not start at 0 leaves the header unsigned.
        return byteRange[0] == 0 && covered > 0
            ? SignatureCoverage.CoversWholeFile
            : SignatureCoverage.LeavesContentUnsigned;
    }
}
