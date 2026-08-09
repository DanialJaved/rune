using Microsoft.Graphics.Canvas;
using Microsoft.Graphics.Canvas.Geometry;
using Microsoft.UI.Xaml.Input;
using Rune.Engine;
using Rune.Styles;
using Windows.Foundation;

namespace Rune.Controls;

// Selecting a signature that has already been placed, so it can be moved or
// resized instead of erased and re-placed.
//
// Only stamps are selectable. Markup is anchored to the words it covers and
// dragging it off them would be wrong, and ink is a path rather than a rect.
//
// Resizing goes through PdfDocument.ResizeStamp, which re-creates the stamp
// rather than editing its geometry — see that method for why editing the
// appearance in place compounds. Corner handles are aspect-locked: a signature
// stretched on one axis stops looking like the person's handwriting.
public sealed partial class PdfViewer
{
    /// <summary>The selected stamp: which page, which annotation, and where it sits (page points).</summary>
    private (int Page, int Index, Rect Local)? _selectedStamp;

    private bool _draggingStamp;
    private Point _stampDragStart;
    private Point _stampDragOffset;

    // ---- resize ----

    /// <summary>Which corner is being dragged (0 TL, 1 TR, 2 BR, 3 BL), or -1.</summary>
    private int _resizeCorner = -1;

    /// <summary>The corner that stays put for the duration of a resize, in page points.</summary>
    private Point _resizeAnchor;

    /// <summary>Width ÷ height at the moment the resize started, so the drag cannot distort it.</summary>
    private double _resizeAspect = 1;

    /// <summary>Handle size in DIPs. Big enough to grab, small enough not to swamp a short signature.</summary>
    private const double HandleSizeDip = 10;

    /// <summary>No dimension below this, in page points — a stamp dragged to nothing cannot be grabbed again.</summary>
    private const double MinStampPt = 12;

    private bool IsResizingStamp => _resizeCorner >= 0;

    /// <summary>True while a placed signature is selected, so Esc and Delete can be claimed.</summary>
    public bool HasSelectedSignature => _selectedStamp is not null;

    /// <summary>FPDF_ANNOT_SUBTYPE_STAMP.</summary>
    private const int StampSubtype = 13;

    /// <summary>Drops the selection. Returns true when there was one, so Esc can be consumed.</summary>
    public bool ClearSignatureSelection()
    {
        if (_selectedStamp is null)
        {
            return false;
        }
        _selectedStamp = null;
        _draggingStamp = false;
        _resizeCorner = -1;
        Canvas.Invalidate();
        return true;
    }

    // ---- selection ----

    /// <summary>
    /// Handles a press with no tool armed: selects a placed signature under the
    /// pointer, or starts dragging one that is already selected. Returns true
    /// when the press was consumed.
    ///
    /// Runs before links and text selection but is deliberately narrow — only
    /// a press actually inside a stamp's rect is taken, so ordinary text
    /// selection everywhere else on the page is untouched.
    /// </summary>
    private bool TryHandleStampPress(Point docPoint, Pointer pointer)
    {
        if (_layout is null || _document is null)
        {
            return false;
        }

        // Dragging the current selection takes priority: no round-trip, and it
        // keeps the grab responsive on the very first move.
        if (_selectedStamp is { } selected)
        {
            int page = _layout.PageAt(docPoint.Y);
            var local = ToPageLocal(page, docPoint);

            // Handles are checked before the body: they overlap the corners, and
            // a press there means resize, not move.
            int corner = page == selected.Page
                ? HitTestStampHandle(selected.Local, local)
                : -1;
            if (corner >= 0)
            {
                _resizeCorner = corner;
                _resizeAspect = selected.Local.Width / Math.Max(1e-6, selected.Local.Height);
                // The corner diagonally opposite is what stays still.
                _resizeAnchor = corner switch
                {
                    0 => new Point(selected.Local.Right, selected.Local.Bottom),
                    1 => new Point(selected.Local.X, selected.Local.Bottom),
                    2 => new Point(selected.Local.X, selected.Local.Y),
                    _ => new Point(selected.Local.Right, selected.Local.Y),
                };
                Canvas.CapturePointer(pointer);
                return true;
            }

            if (page == selected.Page && selected.Local.Contains(new Point(local.X, local.Y)))
            {
                _draggingStamp = true;
                _stampDragStart = docPoint;
                _stampDragOffset = new Point(local.X - selected.Local.X, local.Y - selected.Local.Y);
                Canvas.CapturePointer(pointer);
                return true;
            }
        }

        SelectStampAt(docPoint);

        // Never consume the press itself. The hit-test is asynchronous, so
        // claiming it here would swallow a click that turns out to have landed
        // on nothing — and with it the text selection the user meant to start.
        return false;
    }

    private async void SelectStampAt(Point docPoint)
    {
        if (_layout is null || _document is not { } document)
        {
            return;
        }

        int page = _layout.PageAt(docPoint.Y);
        var local = ToPageLocal(page, docPoint);

        IReadOnlyList<AnnotationInfo> annotations;
        try
        {
            annotations = await _scheduler.RunAsync(
                PdfWorkPriority.Interactive, () => document.GetAnnotations(page));
        }
        catch
        {
            return;
        }
        if (_document != document)
        {
            return;
        }

        var hit = annotations.LastOrDefault(a =>
            a.Subtype == StampSubtype &&
            local.X >= a.X && local.X <= a.X + a.Width &&
            local.Y >= a.Y && local.Y <= a.Y + a.Height);

        var next = hit is null
            ? (( int Page, int Index, Rect Local)?)null
            : (page, hit.Index, new Rect(hit.X, hit.Y, hit.Width, hit.Height));

        if (next?.Index != _selectedStamp?.Index || next?.Page != _selectedStamp?.Page)
        {
            _selectedStamp = next;
            Canvas.Invalidate();
        }
    }

    // ---- resize ----

    /// <summary>
    /// Which corner handle a page-local point is on, or -1.
    ///
    /// The handles are drawn at a fixed size in DIPs so they stay grabbable at
    /// any zoom, which means the hit area has to be converted back into page
    /// points rather than being a constant. The tolerance is generous — a
    /// signature is often only a few points tall, and missing the handle to
    /// start a move instead is a worse outcome than the reverse.
    /// </summary>
    private int HitTestStampHandle(Rect local, (double X, double Y) point)
    {
        double reach = (HandleSizeDip / Math.Max(0.05, _zoom)) * 0.9;

        var corners = new[]
        {
            new Point(local.X, local.Y),
            new Point(local.Right, local.Y),
            new Point(local.Right, local.Bottom),
            new Point(local.X, local.Bottom),
        };

        for (int i = 0; i < corners.Length; i++)
        {
            if (Math.Abs(point.X - corners[i].X) <= reach && Math.Abs(point.Y - corners[i].Y) <= reach)
            {
                return i;
            }
        }
        return -1;
    }

    /// <summary>
    /// Live preview of a corner drag. Aspect-locked: the pointer's distance from
    /// the anchor sets the width and the height follows, so a signature keeps its
    /// proportions however the corner is dragged. Committing happens on release.
    /// </summary>
    private void UpdateStampResize(Point docPoint)
    {
        if (!IsResizingStamp || _selectedStamp is not { } selected || _layout is null)
        {
            return;
        }

        var local = ToPageLocal(selected.Page, docPoint);
        var size = _pageSizes[selected.Page];

        // Width drives; height follows the original aspect.
        double width = Math.Abs(local.X - _resizeAnchor.X);
        double height = width / Math.Max(1e-6, _resizeAspect);

        // Keep the whole stamp on the page. The anchor is fixed, so the room
        // available is the distance from it to whichever edge the drag is
        // heading for.
        double roomX = local.X >= _resizeAnchor.X ? size.Width - _resizeAnchor.X : _resizeAnchor.X;
        double roomY = local.Y >= _resizeAnchor.Y ? size.Height - _resizeAnchor.Y : _resizeAnchor.Y;
        width = Math.Min(width, roomX);
        height = Math.Min(width / Math.Max(1e-6, _resizeAspect), roomY);
        width = height * _resizeAspect;

        width = Math.Max(MinStampPt, width);
        height = Math.Max(MinStampPt, height);

        // The rect grows away from the anchor, in whichever direction the pointer went.
        double x = local.X >= _resizeAnchor.X ? _resizeAnchor.X : _resizeAnchor.X - width;
        double y = local.Y >= _resizeAnchor.Y ? _resizeAnchor.Y : _resizeAnchor.Y - height;

        _selectedStamp = (selected.Page, selected.Index, new Rect(x, y, width, height));
        Canvas.Invalidate();
    }

    private async void CommitStampResize()
    {
        _resizeCorner = -1;

        if (_selectedStamp is not { } selected || _document is not { } document)
        {
            return;
        }

        int page = selected.Page;
        int index = selected.Index;
        var box = selected.Local;

        (int NewIndex, AnnotationSpec Before)? result;
        try
        {
            result = await _scheduler.RunAsync(
                PdfWorkPriority.Interactive,
                () => document.ResizeStamp(page, index, box.X, box.Y, box.Width, box.Height));
        }
        catch
        {
            return; // document swapped or closed mid-resize
        }
        if (_document != document || result is not { } resized)
        {
            // Nothing changed in the file, so put the on-screen box back rather
            // than leaving a selection that lies about the stamp's size.
            RefreshSelectedStampFromDocument(page, index);
            return;
        }

        // Re-creating appends, so the selection now points at a different index.
        _selectedStamp = (page, resized.NewIndex, box);

        InvalidatePage(page);
        DocumentEdited?.Invoke(this, EventArgs.Empty);

        // A resize is a remove-and-re-create, so undo is the same shape: drop
        // what is there now and rebuild the original from its spec. The stamp's
        // pixels ride along in the spec, which is why SnapshotBytes is charged
        // for them here, unlike a move.
        var before = resized.Before;
        int newIndex = resized.NewIndex;
        AnnotationEdited?.Invoke(this, new AnnotationEditEventArgs
        {
            Label = "resize signature",
            PageIndex = page,
            SnapshotBytes = before.Stamp?.Bgra.Length ?? 0,
            UndoAction = d =>
            {
                d.RemoveAnnotation(page, newIndex);
                d.AddAnnotationFromSpec(before);
            },
            RedoAction = d => d.ResizeStamp(page, d.GetAnnotations(page).Count - 1, box.X, box.Y, box.Width, box.Height),
        });
    }

    /// <summary>
    /// Re-reads the selected stamp's rect from the document, for when an edit did
    /// not go through and the preview has to be walked back.
    /// </summary>
    private async void RefreshSelectedStampFromDocument(int page, int index)
    {
        if (_document is not { } document)
        {
            return;
        }
        IReadOnlyList<AnnotationInfo> annotations;
        try
        {
            annotations = await _scheduler.RunAsync(
                PdfWorkPriority.Interactive, () => document.GetAnnotations(page));
        }
        catch
        {
            return;
        }
        if (_document != document || annotations.ElementAtOrDefault(index) is not { } current)
        {
            ClearSignatureSelection();
            return;
        }
        _selectedStamp = (page, index, new Rect(current.X, current.Y, current.Width, current.Height));
        Canvas.Invalidate();
    }

    // ---- drag ----

    private void UpdateStampDrag(Point docPoint)
    {
        if (!_draggingStamp || _selectedStamp is not { } selected || _layout is null)
        {
            return;
        }

        var local = ToPageLocal(selected.Page, docPoint);
        // The stamp's rect is in the file's own axes, so the limits are the
        // page's unrotated size — the layout rect has them swapped on a
        // quarter turn, which would let a drag run off the short edge.
        var size = _pageSizes[selected.Page];
        double maxX = size.Width - selected.Local.Width;
        double maxY = size.Height - selected.Local.Height;

        // Keep it on the page: a signature dragged past the edge would be
        // clipped by every reader that opens the file.
        double x = Math.Clamp(local.X - _stampDragOffset.X, 0, Math.Max(0, maxX));
        double y = Math.Clamp(local.Y - _stampDragOffset.Y, 0, Math.Max(0, maxY));

        _selectedStamp = (selected.Page, selected.Index, new Rect(x, y, selected.Local.Width, selected.Local.Height));
        Canvas.Invalidate();
    }

    private async void CommitStampDrag()
    {
        _draggingStamp = false;

        if (_selectedStamp is not { } selected || _document is not { } document)
        {
            return;
        }

        int page = selected.Page;
        int index = selected.Index;
        double x = selected.Local.X, y = selected.Local.Y;

        (float L, float B, float R, float T)? previous;
        try
        {
            previous = await _scheduler.RunAsync(
                PdfWorkPriority.Interactive, () => document.MoveAnnotation(page, index, x, y));
        }
        catch
        {
            return;
        }
        if (_document != document || previous is not { } before)
        {
            return;
        }

        InvalidatePage(page);
        DocumentEdited?.Invoke(this, EventArgs.Empty);

        // Undo restores the rect the move captured; redo simply replays the
        // move. Neither retains a bitmap — unlike an erase, which has to keep
        // the pixels to rebuild the stamp — so SnapshotBytes stays 0.
        AnnotationEdited?.Invoke(this, new AnnotationEditEventArgs
        {
            Label = "move signature",
            PageIndex = page,
            UndoAction = d => d.RestoreAnnotationRect(page, index, before),
            RedoAction = d => d.MoveAnnotation(page, index, x, y),
        });
    }

    /// <summary>Removes the selected signature, reusing the eraser's capture-then-remove path.</summary>
    public void DeleteSelectedSignature()
    {
        if (_selectedStamp is { } selected && _layout is not null)
        {
            // The eraser hit-tests from a document point, so the stamp's centre
            // has to come back out through the view rotation to reach one.
            var centre = ToDocumentPoint(selected.Page,
                selected.Local.X + selected.Local.Width / 2,
                selected.Local.Y + selected.Local.Height / 2);
            ClearSignatureSelection();
            _ = EraseAnnotationAt(centre);
        }
    }

    // ---- painting ----

    /// <summary>
    /// Frames the selected signature. Drawn after the page so it is never
    /// inverted by night mode along with the tiles.
    /// </summary>
    private void DrawStampSelection(CanvasDrawingSession session)
    {
        if (_selectedStamp is not { } selected || _layout is null)
        {
            return;
        }

        var pageRect = _layout.GetPageRect(selected.Page);
        var box = HighlightRect(selected.Page, pageRect, new TextRect(
            selected.Local.X, selected.Local.Y, selected.Local.Width, selected.Local.Height));

        session.FillRectangle(box, RuneColors.SelectedStampWash(_nightMode));
        session.DrawRectangle(box, RuneColors.SelectedStampBorder(_nightMode), 1.5f,
            new CanvasStrokeStyle { DashStyle = CanvasDashStyle.Dash });

        // Corner handles, in DIPs rather than page points so they stay the same
        // size on screen at any zoom. Drawn from the box's own corners, which
        // have already been through the view rotation, so they land on the
        // visible corners rather than the file's.
        var border = RuneColors.SelectedStampBorder(_nightMode);
        var fill = RuneColors.SelectedStampHandle(_nightMode);
        float half = (float)(HandleSizeDip / 2);
        foreach (var corner in new[]
        {
            new Point(box.Left, box.Top),
            new Point(box.Right, box.Top),
            new Point(box.Right, box.Bottom),
            new Point(box.Left, box.Bottom),
        })
        {
            var handle = new Rect(corner.X - half, corner.Y - half, HandleSizeDip, HandleSizeDip);
            session.FillRectangle(handle, fill);
            session.DrawRectangle(handle, border, 1f);
        }
    }
}
