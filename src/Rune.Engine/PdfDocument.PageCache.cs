using Rune.PdfiumInterop;

namespace Rune.Engine;

// Keeps a bounded set of FPDF_PAGE handles open instead of loading and closing
// one around every operation.
//
// This is not primarily a performance cache. PDFium's form-fill API requires
// the *same* page handle across FORM_OnAfterLoadPage -> input events ->
// FORM_OnBeforeClosePage: the field the user is typing into lives in the page
// object, so a page closed and reloaded between keystrokes loses its editing
// state. Everything else — render, text, annotations — shares the cache so a
// page is never open through two handles at once, which is what would let a
// cached handle and a fresh one disagree about the same page.
//
// Every method here requires the caller to hold PdfiumLibrary.Lock, matching
// the rest of PdfDocument. No separate lock exists.
public sealed partial class PdfDocument
{
    /// <summary>
    /// Hooks for the form-fill environment (implemented in PdfDocument.Forms.cs).
    /// PDFium must be told when a page handle becomes live and before it dies,
    /// or form widgets on it stop responding. Compiled away while unimplemented.
    /// </summary>
    partial void OnPageEnteredCacheLocked(int pageIndex, IntPtr page);

    partial void OnPageLeavingCacheLocked(int pageIndex, IntPtr page);

    /// <summary>
    /// Pages held open at once. Generous enough for the visible window plus
    /// prefetch neighbours; each entry is only a page dictionary, not a raster.
    /// </summary>
    private const int PageCacheCapacity = 12;

    private sealed class CachedPage
    {
        public IntPtr Page;
        public IntPtr TextPage;

        /// <summary>Non-zero while an operation is using this handle — never evict.</summary>
        public int Leases;

        /// <summary>Monotonic use counter; lowest is evicted first.</summary>
        public long Stamp;
    }

    private readonly Dictionary<int, CachedPage> _pageCache = [];
    private long _pageClock;
    private int _pinnedPage = -1;

    /// <summary>How many pages are currently held open. Test/diagnostic hook.</summary>
    internal int CachedPageCount
    {
        get
        {
            lock (PdfiumLibrary.Lock)
            {
                return _pageCache.Count;
            }
        }
    }

    /// <summary>Whether a specific page is currently resident. Test/diagnostic hook.</summary>
    internal bool IsPageCached(int pageIndex)
    {
        lock (PdfiumLibrary.Lock)
        {
            return _pageCache.ContainsKey(pageIndex);
        }
    }

    /// <summary>
    /// Keeps one page resident regardless of eviction pressure — used for the
    /// page holding form focus, whose handle must stay valid between keystrokes.
    /// Pass -1 to clear.
    /// </summary>
    public void PinPage(int pageIndex)
    {
        lock (PdfiumLibrary.Lock)
        {
            _pinnedPage = pageIndex;
        }
    }

    /// <summary>
    /// Borrows a page handle, loading it if absent. Returns <see cref="IntPtr.Zero"/>
    /// if the page cannot be loaded. Every successful call must be paired with
    /// <see cref="ReleasePageLocked"/>, normally in a finally block.
    /// Caller must hold <see cref="PdfiumLibrary.Lock"/>.
    /// </summary>
    private IntPtr AcquirePageLocked(int pageIndex)
    {
        if (_pageCache.TryGetValue(pageIndex, out var entry))
        {
            entry.Leases++;
            entry.Stamp = ++_pageClock;
            return entry.Page;
        }

        IntPtr page = PdfiumNative.LoadPage(_handle, pageIndex);
        if (page == IntPtr.Zero)
        {
            return IntPtr.Zero;
        }

        // Lease and stamp before trimming, or the entry we just created looks
        // like the coldest one and evicts itself.
        entry = new CachedPage { Page = page, Leases = 1, Stamp = ++_pageClock };
        _pageCache[pageIndex] = entry;
        OnPageEnteredCacheLocked(pageIndex, page);
        TrimPageCacheLocked();
        return page;
    }

    /// <summary>
    /// The text page for an already-acquired page, loaded on first use and kept
    /// for the page's lifetime in the cache. Returns <see cref="IntPtr.Zero"/> on failure.
    /// </summary>
    private IntPtr AcquireTextPageLocked(int pageIndex)
    {
        if (!_pageCache.TryGetValue(pageIndex, out var entry))
        {
            return IntPtr.Zero;
        }
        if (entry.TextPage == IntPtr.Zero)
        {
            entry.TextPage = PdfiumNative.TextLoadPage(entry.Page);
        }
        return entry.TextPage;
    }

    /// <summary>Returns a borrowed page. Does not close it — the cache owns the handle.</summary>
    private void ReleasePageLocked(int pageIndex)
    {
        if (_pageCache.TryGetValue(pageIndex, out var entry) && entry.Leases > 0)
        {
            entry.Leases--;
        }
    }

    /// <summary>
    /// Closes one page's handle so the next acquire loads it fresh.
    ///
    /// This exists for edits made straight into a page's dictionary, which
    /// PDFium's form layer does not notice: it rebuilds a widget's appearance on
    /// <c>FORM_OnAfterLoadPage</c>, and the only way to make that run again is to
    /// let the handle go. Refuses while the page is leased, since closing a
    /// handle another operation is holding is a use-after-free.
    /// Caller must hold <see cref="PdfiumLibrary.Lock"/>.
    /// </summary>
    private bool EvictPageLocked(int pageIndex)
    {
        if (!_pageCache.TryGetValue(pageIndex, out var entry) || entry.Leases > 0)
        {
            return false;
        }
        CloseCachedPageLocked(pageIndex, entry);
        _pageCache.Remove(pageIndex);
        return true;
    }

    private void TrimPageCacheLocked()
    {
        while (_pageCache.Count > PageCacheCapacity)
        {
            int victim = -1;
            long oldest = long.MaxValue;
            foreach (var (index, entry) in _pageCache)
            {
                if (entry.Leases > 0 || index == _pinnedPage)
                {
                    continue;
                }
                if (entry.Stamp < oldest)
                {
                    oldest = entry.Stamp;
                    victim = index;
                }
            }

            // Everything is leased or pinned. Overshooting the budget by a few
            // page dictionaries is harmless; closing a handle still in use is
            // a use-after-free.
            if (victim < 0)
            {
                return;
            }

            CloseCachedPageLocked(victim, _pageCache[victim]);
            _pageCache.Remove(victim);
        }
    }

    /// <summary>
    /// Closes every open page. Required before any page-tree mutation (the
    /// handles and their indices both become meaningless), before saving, and
    /// before closing the document.
    /// Caller must hold <see cref="PdfiumLibrary.Lock"/>.
    /// </summary>
    private void ReleaseAllPagesLocked()
    {
        foreach (var (index, entry) in _pageCache)
        {
            CloseCachedPageLocked(index, entry);
        }
        _pageCache.Clear();
        _pinnedPage = -1;
    }

    private void CloseCachedPageLocked(int pageIndex, CachedPage entry)
    {
        OnPageLeavingCacheLocked(pageIndex, entry.Page);
        if (entry.TextPage != IntPtr.Zero)
        {
            PdfiumNative.TextClosePage(entry.TextPage);
            entry.TextPage = IntPtr.Zero;
        }
        if (entry.Page != IntPtr.Zero)
        {
            PdfiumNative.ClosePage(entry.Page);
            entry.Page = IntPtr.Zero;
        }
    }
}
