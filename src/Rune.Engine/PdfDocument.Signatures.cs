using Rune.PdfiumInterop;

namespace Rune.Engine;

/// <summary>
/// What Rune can honestly report about one digital signature in a document.
///
/// There is no <c>IsValid</c> here, and there must not be one. PDFium returns
/// the signature dictionary and the raw PKCS#7 blob; it does not verify the
/// digest, the certificate chain, trust, or revocation, and Rune ships no
/// cryptography to do it. Presenting any of this as "valid" would tell someone
/// a document is authentic when nothing has checked that.
/// </summary>
public sealed record PdfSignatureInfo(
    int Index,
    string Reason,
    string SubFilter,
    string SignedAtRaw,
    int ContentsLength,
    SignatureCoverage Coverage,
    uint DocMdpPermission)
{
    /// <summary>
    /// The signed date as written in the file, parsed from a PDF date string
    /// (D:YYYYMMDDHHmmSS...). Null when absent or unparseable — this is the
    /// signer's claim, not an independently verified timestamp.
    /// </summary>
    public DateTimeOffset? SignedAt => ParsePdfDate(SignedAtRaw);

    /// <summary>DocMDP level: 1 no changes, 2 form filling, 3 form filling + comments. 0 when not certified.</summary>
    public bool IsCertifying => DocMdpPermission is >= 1 and <= 3;

    private static DateTimeOffset? ParsePdfDate(string raw)
    {
        if (string.IsNullOrWhiteSpace(raw))
        {
            return null;
        }

        string s = raw.Trim();
        if (s.StartsWith("D:", StringComparison.Ordinal))
        {
            s = s[2..];
        }
        if (s.Length < 14 || !DateTime.TryParseExact(
                s[..14], "yyyyMMddHHmmss", null,
                System.Globalization.DateTimeStyles.None, out var local))
        {
            return null;
        }

        // Optional trailing offset: +HH'mm' / -HH'mm' / Z
        var offset = TimeSpan.Zero;
        if (s.Length >= 17 && (s[14] == '+' || s[14] == '-'))
        {
            if (int.TryParse(s.AsSpan(15, 2), out int hours))
            {
                int minutes = s.Length >= 20 && int.TryParse(s.AsSpan(18, 2), out int m) ? m : 0;
                offset = new TimeSpan(hours, minutes, 0);
                if (s[14] == '-')
                {
                    offset = -offset;
                }
            }
        }

        try
        {
            return new DateTimeOffset(local, offset);
        }
        catch (ArgumentOutOfRangeException)
        {
            return null; // offset out of the ±14h range PDF files sometimes claim
        }
    }
}

public sealed partial class PdfDocument
{
    /// <summary>Number of digital signatures in the document (0 for almost everything).</summary>
    public int SignatureCount
    {
        get
        {
            lock (PdfiumLibrary.Lock)
            {
                ObjectDisposedException.ThrowIf(_handle == IntPtr.Zero, this);
                return PdfiumNative.GetSignatureCount(_handle);
            }
        }
    }

    /// <summary>
    /// Reads every signature's reported metadata and byte-range coverage.
    /// Reporting only — see <see cref="PdfSignatureInfo"/> on why there is no
    /// validity result.
    /// </summary>
    public IReadOnlyList<PdfSignatureInfo> GetSignatures()
    {
        var result = new List<PdfSignatureInfo>();

        long fileLength;
        try
        {
            fileLength = new FileInfo(FilePath).Length;
        }
        catch (IOException)
        {
            fileLength = 0; // coverage becomes Unknown, which is the honest answer
        }

        lock (PdfiumLibrary.Lock)
        {
            ObjectDisposedException.ThrowIf(_handle == IntPtr.Zero, this);

            int count = PdfiumNative.GetSignatureCount(_handle);
            for (int i = 0; i < count; i++)
            {
                IntPtr signature = PdfiumNative.GetSignatureObject(_handle, i);
                if (signature == IntPtr.Zero)
                {
                    continue;
                }

                int contentsLength = PdfiumNative.GetSignatureContentsLength(signature);
                int[] byteRange = PdfiumNative.GetSignatureByteRange(signature);

                result.Add(new PdfSignatureInfo(
                    i,
                    PdfiumNative.GetSignatureReason(signature),
                    PdfiumNative.GetSignatureSubFilter(signature),
                    PdfiumNative.GetSignatureTime(signature),
                    contentsLength,
                    SignatureCoverageCheck.Evaluate(byteRange, fileLength, contentsLength),
                    PdfiumNative.GetSignatureDocMdpPermission(signature)));
            }
        }

        return result;
    }
}
