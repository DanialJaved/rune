namespace Folio.Engine;

/// <summary>
/// The single thread through which all page rendering flows (PDFium is not
/// thread-safe, and one thread also gives us clean prioritization).
///
/// Works by desired-state reconciliation rather than a queue: the UI hands
/// over the full prioritized list of tiles it currently wants
/// (<see cref="SetDesired"/>), replacing the previous list, and the loop
/// always renders the front-most missing tile. Scrolling past something
/// simply drops it from the next desired list — no stale work, no cancellation
/// bookkeeping.
/// </summary>
public sealed class RenderScheduler : IDisposable
{
    private readonly object _gate = new();
    private readonly Thread _thread;
    private readonly Action<TileRequest, PageBitmap> _onRendered;

    private PdfDocument? _document;
    private List<TileRequest> _desired = [];
    private bool _stopping;

    /// <param name="onRendered">
    /// Called on the render thread when a tile is ready. Marshal to the UI
    /// thread yourself (DispatcherQueue) before touching UI state.
    /// </param>
    public RenderScheduler(Action<TileRequest, PageBitmap> onRendered)
    {
        _onRendered = onRendered;
        _thread = new Thread(RenderLoop)
        {
            Name = "Folio.RenderThread",
            IsBackground = true,
        };
        _thread.Start();
    }

    /// <summary>Swaps the document; pending work for the old document is dropped.</summary>
    public void SetDocument(PdfDocument? document)
    {
        lock (_gate)
        {
            _document = document;
            _desired = [];
            Monitor.Pulse(_gate);
        }
    }

    /// <summary>
    /// Replaces the desired tile list (highest priority first). The caller is
    /// responsible for excluding tiles that are already cached or in flight.
    /// </summary>
    public void SetDesired(List<TileRequest> requests)
    {
        lock (_gate)
        {
            _desired = requests;
            Monitor.Pulse(_gate);
        }
    }

    private void RenderLoop()
    {
        while (true)
        {
            TileRequest? request;
            PdfDocument? document;

            lock (_gate)
            {
                while (!_stopping && (_document is null || _desired.Count == 0))
                {
                    Monitor.Wait(_gate);
                }
                if (_stopping)
                {
                    return;
                }

                request = _desired[0];
                _desired.RemoveAt(0);
                document = _document;
            }

            try
            {
                var bitmap = document!.RenderRegion(
                    request.Key.PageIndex, request.Scale, request.Key.Rotation,
                    request.SrcX, request.SrcY, request.WidthPx, request.HeightPx);
                _onRendered(request, bitmap);
            }
            catch (ObjectDisposedException)
            {
                // Document was closed under us — its desired list is already gone.
            }
            catch (PdfiumInterop.PdfiumException)
            {
                // Corrupt page: skip it. The placeholder stays on screen.
            }
        }
    }

    public void Dispose()
    {
        lock (_gate)
        {
            _stopping = true;
            Monitor.Pulse(_gate);
        }
        _thread.Join();
    }
}
