namespace Folio.PdfiumInterop;

public sealed class PdfiumException : Exception
{
    public uint ErrorCode { get; }

    public PdfiumException(string message, uint errorCode) : base(message)
    {
        ErrorCode = errorCode;
    }

    internal static PdfiumException FromLastError()
    {
        uint code = NativeMethods.FPDF_GetLastError();
        string message = code switch
        {
            NativeMethods.FPDF_ERR_FILE => "The file could not be opened or read.",
            NativeMethods.FPDF_ERR_FORMAT => "The file is not a valid PDF or is corrupted.",
            NativeMethods.FPDF_ERR_PASSWORD => "The PDF is password-protected.",
            NativeMethods.FPDF_ERR_SECURITY => "The PDF has an unsupported security scheme.",
            NativeMethods.FPDF_ERR_PAGE => "The requested page was not found or is corrupted.",
            _ => "PDFium reported an unknown error.",
        };
        return new PdfiumException(message, code);
    }
}
