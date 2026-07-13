using System.Runtime.InteropServices;

namespace Rune.PdfiumInterop;

/// <summary>
/// Bridges a .NET <see cref="FileStream"/> to PDFium's FPDF_FILEACCESS callback
/// interface, so documents are read lazily from disk (no full load into memory,
/// no path-encoding issues).
///
/// Lifetime rules (why this class exists):
///  - PDFium keeps the FPDF_FILEACCESS pointer and calls m_GetBlock at any time
///    while the document is open, so the native struct AND the delegate must
///    outlive the document. The struct lives in AllocHGlobal memory; the
///    delegate is held in a field to keep it from being garbage-collected.
///  - Callers must therefore keep this adapter alive until after
///    FPDF_CloseDocument, then dispose it.
/// </summary>
public sealed class FileAccessAdapter : IDisposable
{
    [StructLayout(LayoutKind.Sequential)]
    private struct FpdfFileAccess
    {
        public uint FileLen;          // unsigned long is 32-bit on Windows (LLP64)
        public IntPtr GetBlock;
        public IntPtr Param;
    }

    [UnmanagedFunctionPointer(CallingConvention.Cdecl)]
    private delegate int GetBlockDelegate(IntPtr param, uint position, IntPtr buffer, uint size);

    private readonly FileStream _stream;
    private readonly GetBlockDelegate _getBlock;  // keeps the delegate alive for PDFium
    private IntPtr _nativeStruct;

    /// <summary>Pointer to pass to FPDF_LoadCustomDocument.</summary>
    public IntPtr NativePointer => _nativeStruct;

    public FileAccessAdapter(string path)
    {
        _stream = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.Read);

        long length = _stream.Length;
        if (length > uint.MaxValue)
        {
            _stream.Dispose();
            throw new PdfiumException("PDF files larger than 4 GB are not supported.", NativeMethods.FPDF_ERR_FILE);
        }

        _getBlock = ReadBlock;

        var access = new FpdfFileAccess
        {
            FileLen = (uint)length,
            GetBlock = Marshal.GetFunctionPointerForDelegate(_getBlock),
            Param = IntPtr.Zero,
        };

        _nativeStruct = Marshal.AllocHGlobal(Marshal.SizeOf<FpdfFileAccess>());
        Marshal.StructureToPtr(access, _nativeStruct, fDeleteOld: false);
    }

    private int ReadBlock(IntPtr param, uint position, IntPtr buffer, uint size)
    {
        try
        {
            _stream.Position = position;
            int remaining = (int)size;
            var scratch = new byte[Math.Min(remaining, 81920)];

            while (remaining > 0)
            {
                int read = _stream.Read(scratch, 0, Math.Min(remaining, scratch.Length));
                if (read <= 0)
                {
                    return 0; // short read = failure, per FPDF_FILEACCESS contract
                }
                Marshal.Copy(scratch, 0, buffer, read);
                buffer += read;
                remaining -= read;
            }
            return 1;
        }
        catch
        {
            return 0; // never let an exception cross the native boundary
        }
    }

    public void Dispose()
    {
        if (_nativeStruct != IntPtr.Zero)
        {
            Marshal.FreeHGlobal(_nativeStruct);
            _nativeStruct = IntPtr.Zero;
        }
        _stream.Dispose();
    }
}
