using System.Buffers;

namespace Rune.Engine;

/// <summary>
/// A rendered page region: 32-bit BGRA pixels, top-down rows, opaque white
/// background. <see cref="Pixels"/> is rented from the shared array pool and
/// may be longer than Stride × Height — always address via Stride.
/// Call <see cref="Return"/> exactly once when finished with the pixels
/// (skipping it is safe but defeats buffer reuse).
/// </summary>
public sealed record PageBitmap(byte[] Pixels, int Width, int Height, int Stride)
{
    public void Return() => ArrayPool<byte>.Shared.Return(Pixels);
}
