namespace Folio.Engine;

/// <summary>
/// A rendered page: 32-bit BGRA pixels, top-down rows, opaque white background.
/// </summary>
public sealed record PageBitmap(byte[] Pixels, int Width, int Height, int Stride);
