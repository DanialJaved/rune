using Rune.Engine;
using Rune.PdfiumInterop;

namespace Rune.Tests;

/// <summary>
/// Cross-checks <see cref="PageRotationTransform"/> against PDFium's own
/// rotation arithmetic on a real page.
///
/// This is the test that makes the rotated-view work trustworthy.
/// <see cref="PageRotationTests"/> proves the transform is self-consistent, but
/// a transform can be consistently wrong — it has to agree with
/// <c>FPDF_RenderPageBitmap</c>, which is what actually puts the page on screen.
/// PDFium's <c>FPDF_DeviceToPage</c> is fed the same rotation and the same device
/// box the viewer renders into, so if the two disagree by more than rounding,
/// the viewer is hit-testing a page that isn't where PDFium drew it.
///
/// It also covers the direction that cannot be verified by driving the app in
/// this environment: injected pointer events reach XAML controls but not the
/// Win2D canvas, so drag-to-select can only be checked by hand. The drawing
/// direction was confirmed on screen at all four rotations.
/// </summary>
public class PageRotationParityTests
{
    private static string CorpusPath(string name) => Path.Combine(AppContext.BaseDirectory, "corpus", name);

    [Theory]
    [InlineData(0)]
    [InlineData(1)]
    [InlineData(2)]
    [InlineData(3)]
    public void ToUnrotated_AgreesWithPdfiumDeviceToPage(int rotation)
    {
        PdfiumLibrary.EnsureInitialized();

        var bytes = File.ReadAllBytes(CorpusPath("hello.pdf"));
        var pinned = System.Runtime.InteropServices.GCHandle.Alloc(bytes, System.Runtime.InteropServices.GCHandleType.Pinned);
        try
        {
            lock (PdfiumLibrary.Lock)
            {
                IntPtr doc = PdfiumNative.LoadMemDocument(pinned.AddrOfPinnedObject(), bytes.Length, null);
                Assert.NotEqual(IntPtr.Zero, doc);
                try
                {
                    Assert.True(PdfiumNative.TryGetPageSize(doc, 0, out float w, out float h));

                    var transform = new PageRotationTransform(rotation, w, h);
                    var (drawnW, drawnH) = transform.DrawnSize;

                    // The device box the viewer renders this page into at 1 px
                    // per point, which is what DeviceToPage has to be told.
                    int sizeX = (int)Math.Round(drawnW);
                    int sizeY = (int)Math.Round(drawnH);

                    IntPtr page = PdfiumNative.LoadPage(doc, 0);
                    Assert.NotEqual(IntPtr.Zero, page);
                    try
                    {
                        // Corners, edge midpoints and an off-centre interior point.
                        var probes = new List<(int X, int Y)>
                        {
                            (0, 0), (sizeX, 0), (0, sizeY), (sizeX, sizeY),
                            (sizeX / 2, 0), (0, sizeY / 2), (sizeX / 2, sizeY), (sizeX, sizeY / 2),
                            (sizeX / 2, sizeY / 2), (sizeX / 3, sizeY / 7), (sizeX * 4 / 5, sizeY * 2 / 3),
                        };

                        foreach (var (dx, dy) in probes)
                        {
                            // PDFium answers in PDF page space: bottom-left origin.
                            var (px, py) = PdfiumNative.DeviceToPage(page, sizeX, sizeY, dx, dy, rotation);
                            // Same convention the viewer's consumers use: top-left origin.
                            double expectedX = px;
                            double expectedY = h - py;

                            var (actualX, actualY) = transform.ToUnrotated(dx, dy);

                            Assert.True(Math.Abs(expectedX - actualX) <= 1.0,
                                $"rotation {rotation}, device ({dx},{dy}): x expected {expectedX:0.###}, got {actualX:0.###}");
                            Assert.True(Math.Abs(expectedY - actualY) <= 1.0,
                                $"rotation {rotation}, device ({dx},{dy}): y expected {expectedY:0.###}, got {actualY:0.###}");
                        }
                    }
                    finally
                    {
                        PdfiumNative.ClosePage(page);
                    }
                }
                finally
                {
                    PdfiumNative.CloseDocument(doc);
                }
            }
        }
        finally
        {
            pinned.Free();
        }
    }

    /// <summary>
    /// The drawn box has to match what PDFium is asked to render into, or a
    /// correct point mapping still lands in a box of the wrong shape. Pinned
    /// against <c>PdfDocument.GetPagePixelSize</c>, which is what feeds the
    /// tile pipeline.
    /// </summary>
    [Theory]
    [InlineData(0)]
    [InlineData(1)]
    [InlineData(2)]
    [InlineData(3)]
    public void DrawnSize_MatchesTheRenderersPixelSize(int rotation)
    {
        using var doc = PdfDocument.Open(CorpusPath("hello.pdf"));
        var (w, h) = doc.GetPageSize(0);

        var (pxW, pxH) = doc.GetPagePixelSize(0, scale: 1.0f, rotation);
        var (drawnW, drawnH) = new PageRotationTransform(rotation, w, h).DrawnSize;

        Assert.Equal(pxW, (int)Math.Round(drawnW));
        Assert.Equal(pxH, (int)Math.Round(drawnH));
    }
}
