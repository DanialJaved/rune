namespace Rune.Engine;

public sealed record SearchProgress(int PagesSearched, int TotalPages, int HitsSoFar);

/// <summary>
/// Searches an entire document for a term, one page at a time on the thread
/// pool so the UI (and the render thread) stay responsive. Results are reported
/// incrementally as each page finishes; cancel via the token to abandon a
/// superseded query.
/// </summary>
public sealed class DocumentSearch
{
    private readonly PdfDocument _document;
    private readonly string _query;
    private readonly bool _matchCase;
    private readonly bool _wholeWord;
    private readonly IPdfWorkQueue? _workQueue;

    /// <param name="workQueue">
    /// When provided (the app passes the viewer's render scheduler), each page
    /// is searched at Background priority so the sweep never starves visible
    /// tile rendering for the shared PDFium lock. Tests pass null (thread pool).
    /// </param>
    public DocumentSearch(PdfDocument document, string query, bool matchCase = false, bool wholeWord = false,
        IPdfWorkQueue? workQueue = null)
    {
        _document = document;
        _query = query;
        _matchCase = matchCase;
        _wholeWord = wholeWord;
        _workQueue = workQueue;
    }

    /// <summary>
    /// Runs the search. <paramref name="onPageHits"/> fires (with any hits) as
    /// each page completes; <paramref name="onProgress"/> reports the running
    /// total. Both are invoked on thread-pool threads — marshal to the UI
    /// thread before touching UI state.
    /// </summary>
    public async Task<IReadOnlyList<SearchHit>> RunAsync(
        Action<IReadOnlyList<SearchHit>>? onPageHits,
        Action<SearchProgress>? onProgress,
        CancellationToken cancellationToken)
    {
        var all = new List<SearchHit>();
        if (string.IsNullOrEmpty(_query))
        {
            return all;
        }

        int pageCount = _document.PageCount;
        for (int page = 0; page < pageCount; page++)
        {
            cancellationToken.ThrowIfCancellationRequested();

            int p = page;
            var hits = _workQueue is null
                ? await Task.Run(() => _document.SearchPage(p, _query, _matchCase, _wholeWord), cancellationToken)
                            .ConfigureAwait(false)
                : await _workQueue.RunAsync(PdfWorkPriority.Background,
                            () => _document.SearchPage(p, _query, _matchCase, _wholeWord))
                            .ConfigureAwait(false);

            if (hits.Count > 0)
            {
                all.AddRange(hits);
                onPageHits?.Invoke(hits);
            }
            onProgress?.Invoke(new SearchProgress(page + 1, pageCount, all.Count));
        }
        return all;
    }
}
