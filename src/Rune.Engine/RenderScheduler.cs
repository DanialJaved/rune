namespace Rune.Engine;

/// <summary>
/// Priority classes for one-off PDFium work scheduled onto the render thread.
/// Lower values run first; tiles are rendered between Interactive and Thumbnail.
/// </summary>
public enum PdfWorkPriority
{
    /// <summary>User is waiting on the result right now (selection, annotation edits). Runs before tiles.</summary>
    Interactive = 0,
    /// <summary>Sidebar thumbnails: nice to have soon, but never before visible tiles.</summary>
    Thumbnail = 1,
    /// <summary>Whole-document sweeps (search): only when nothing else wants the thread.</summary>
    Background = 2,
}

/// <summary>
/// Anything that can run PDFium work serialized on the single render thread.
/// Lets engine services (search) and tests take the scheduler without a
/// dependency on its concrete type.
/// </summary>
public interface IPdfWorkQueue
{
    Task<T> RunAsync<T>(PdfWorkPriority priority, Func<T> operation);
}

/// <summary>
/// The single thread through which all PDFium work flows (PDFium is not
/// thread-safe, and one thread also gives us clean prioritization).
///
/// Tiles work by desired-state reconciliation rather than a queue: the UI
/// hands over the full prioritized list of tiles it currently wants
/// (<see cref="SetDesired"/>), replacing the previous list, and the loop
/// always renders the front-most missing tile. Scrolling past something
/// simply drops it from the next desired list — no stale work, no cancellation
/// bookkeeping.
///
/// One-off operations (<see cref="RunAsync"/>) are interleaved with tiles by
/// priority: Interactive ops → tiles → Thumbnail ops → Background ops. This is
/// what keeps text selection and annotation edits from ever blocking the UI
/// thread behind a slow tile render.
/// </summary>
public sealed class RenderScheduler : IDisposable, IPdfWorkQueue
{
    private readonly object _gate = new();
    private readonly Thread _thread;
    private readonly Action<TileRequest, PageBitmap> _onRendered;

    private PdfDocument? _document;
    private List<TileRequest> _desired = [];
    private readonly Queue<ScheduledOp>[] _ops = [new(), new(), new()];
    private bool _stopping;

    private readonly record struct ScheduledOp(Action Execute, Action Cancel);

    /// <param name="onRendered">
    /// Called on the render thread when a tile is ready. Marshal to the UI
    /// thread yourself (DispatcherQueue) before touching UI state.
    /// </param>
    public RenderScheduler(Action<TileRequest, PageBitmap> onRendered)
    {
        _onRendered = onRendered;
        _thread = new Thread(RenderLoop)
        {
            Name = "Rune.RenderThread",
            IsBackground = true,
        };
        _thread.Start();
    }

    /// <summary>Swaps the document; pending tiles AND pending ops for the old document are dropped.</summary>
    public void SetDocument(PdfDocument? document)
    {
        List<ScheduledOp>? cancelled = null;
        lock (_gate)
        {
            _document = document;
            _desired = [];
            cancelled = DrainOps();
            Monitor.Pulse(_gate);
        }
        CancelAll(cancelled);
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

    /// <summary>
    /// Schedules a one-off operation on the render thread. The returned task
    /// completes (or faults) with the operation; it is cancelled if the
    /// document is swapped or the scheduler disposed before it runs.
    /// </summary>
    public Task<T> RunAsync<T>(PdfWorkPriority priority, Func<T> operation)
    {
        // RunContinuationsAsynchronously matters: without it, awaiting code
        // would resume INSIDE the render loop, stalling rendering (or worse,
        // deadlocking if the continuation blocks on more render work).
        var tcs = new TaskCompletionSource<T>(TaskCreationOptions.RunContinuationsAsynchronously);
        var op = new ScheduledOp(
            Execute: () =>
            {
                try
                {
                    tcs.TrySetResult(operation());
                }
                catch (OperationCanceledException)
                {
                    tcs.TrySetCanceled();
                }
                catch (Exception ex)
                {
                    tcs.TrySetException(ex);
                }
            },
            Cancel: () => tcs.TrySetCanceled());

        lock (_gate)
        {
            if (_stopping)
            {
                tcs.TrySetCanceled();
                return tcs.Task;
            }
            _ops[(int)priority].Enqueue(op);
            Monitor.Pulse(_gate);
        }
        return tcs.Task;
    }

    public Task RunAsync(PdfWorkPriority priority, Action operation)
        => RunAsync<object?>(priority, () =>
        {
            operation();
            return null;
        });

    private bool HasWork()
        => (_document is not null && _desired.Count > 0)
           || _ops[0].Count > 0 || _ops[1].Count > 0 || _ops[2].Count > 0;

    private List<ScheduledOp> DrainOps()
    {
        var drained = new List<ScheduledOp>();
        foreach (var queue in _ops)
        {
            while (queue.Count > 0)
            {
                drained.Add(queue.Dequeue());
            }
        }
        return drained;
    }

    private static void CancelAll(List<ScheduledOp>? ops)
    {
        if (ops is null)
        {
            return;
        }
        foreach (var op in ops)
        {
            op.Cancel();
        }
    }

    private void RenderLoop()
    {
        while (true)
        {
            ScheduledOp? op = null;
            TileRequest? request = null;
            PdfDocument? document = null;

            lock (_gate)
            {
                while (!_stopping && !HasWork())
                {
                    Monitor.Wait(_gate);
                }
                if (_stopping)
                {
                    return;
                }

                // Priority: interactive ops → tiles → thumbnails → background.
                if (_ops[0].Count > 0)
                {
                    op = _ops[0].Dequeue();
                }
                else if (_document is not null && _desired.Count > 0)
                {
                    request = _desired[0];
                    _desired.RemoveAt(0);
                    document = _document;
                }
                else if (_ops[1].Count > 0)
                {
                    op = _ops[1].Dequeue();
                }
                else
                {
                    op = _ops[2].Dequeue();
                }
            }

            if (op is { } scheduled)
            {
                scheduled.Execute(); // exceptions are captured into the op's task
                continue;
            }

            try
            {
                var bitmap = document!.RenderRegion(
                    request!.Key.PageIndex, request.Scale, request.Key.Rotation,
                    request.SrcX, request.SrcY, request.WidthPx, request.HeightPx);
                _onRendered(request, bitmap);
            }
            catch (ObjectDisposedException)
            {
                // Document was closed under us — its desired list is already gone.
            }
            catch (ArgumentOutOfRangeException)
            {
                // Stale tile request after a page mutation (delete/move); skip.
            }
            catch (PdfiumInterop.PdfiumException)
            {
                // Corrupt page: skip it. The placeholder stays on screen.
            }
        }
    }

    public void Dispose()
    {
        List<ScheduledOp>? cancelled;
        lock (_gate)
        {
            _stopping = true;
            cancelled = DrainOps();
            Monitor.Pulse(_gate);
        }
        CancelAll(cancelled);
        _thread.Join();
    }
}
