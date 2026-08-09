using Microsoft.Graphics.Canvas;
using Microsoft.Graphics.Canvas.Geometry;
using Microsoft.UI.Xaml.Input;
using Rune.Engine;
using Rune.Styles;
using Windows.Foundation;

namespace Rune.Controls;

// Selecting a signature that has already been placed, so it can be nudged into
// the right spot instead of erased and re-placed.
//
// Only stamps are selectable. Markup is anchored to the words it covers and
// dragging it off them would be wrong, and ink is a path rather than a rect.
//
// There are no resize handles, and that is a PDFium limit rather than an
// omission: it translates a stamp's appearance to the annotation rect but never
// scales it to fit, so a resized rect reports one size and draws another. Size
// is chosen at placement, where Rune still holds the pixels. See
// PdfDocument.MoveAnnotation for the measurements.
public sealed partial class PdfViewer
{
    /// <summary>The selected stamp: which page, which annotation, and where it sits (page points).</summary>
    private (int Page, int Index, Rect Local)? _selectedStamp;

    private bool _draggingStamp;
    private Point _stampDragStart;
    private Point _stampDragOffset;

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
    }
}
