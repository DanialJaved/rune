using Rune.Engine;

namespace Rune.Tests;

public class UndoStackTests
{
    private sealed record Edit(string Label, long SnapshotBytes) : IUndoableEdit;

    [Fact]
    public void Push_Pop_FollowsLifoAndTracksLabels()
    {
        var stack = new UndoStack<Edit>();
        stack.Push(new Edit("a", 0));
        stack.Push(new Edit("b", 0));

        Assert.True(stack.CanUndo);
        Assert.Equal("b", stack.UndoLabel);

        Assert.Equal("b", stack.PopUndo()!.Label);
        Assert.Equal("a", stack.PopUndo()!.Label);
        Assert.False(stack.CanUndo);
        Assert.True(stack.CanRedo);
        Assert.Equal("a", stack.RedoLabel);
    }

    [Fact]
    public void Push_ClearsRedoBranch()
    {
        var stack = new UndoStack<Edit>();
        stack.Push(new Edit("a", 0));
        stack.PopUndo();
        Assert.True(stack.CanRedo);

        stack.Push(new Edit("b", 0));
        Assert.False(stack.CanRedo);
    }

    [Fact]
    public void Trim_CapsEditCount()
    {
        var stack = new UndoStack<Edit>();
        for (int i = 0; i < UndoStack<Edit>.MaxEdits + 20; i++)
        {
            stack.Push(new Edit($"e{i}", 0));
        }

        int undoable = 0;
        while (stack.PopUndo() is not null)
        {
            undoable++;
        }
        Assert.Equal(UndoStack<Edit>.MaxEdits, undoable);
    }

    [Fact]
    public void Trim_CapsSnapshotBytes()
    {
        var stack = new UndoStack<Edit>();
        // Each 20 MB; only ~3 fit under the 64 MB cap.
        for (int i = 0; i < 6; i++)
        {
            stack.Push(new Edit($"e{i}", 20L * 1024 * 1024));
        }

        int count = 0;
        while (stack.PopUndo() is not null)
        {
            count++;
        }
        Assert.InRange(count, 1, 3);
    }
}

public class AnnotationUndoTests
{
    private static string CorpusPath(string name) => Path.Combine(AppContext.BaseDirectory, "corpus", name);

    [Fact]
    public void AddMarkup_Undo_RemovesIt_Redo_RestoresIt()
    {
        using var doc = PdfDocument.Open(CorpusPath("hello.pdf"));
        int before = doc.GetAnnotations(0).Count;

        doc.AddMarkup(0, MarkupKind.Highlight, [new TextRect(50, 50, 100, 20)], 255, 210, 0, 102);
        var spec = doc.CaptureLastAnnotation(0);
        Assert.NotNull(spec);
        Assert.Equal(before + 1, doc.GetAnnotations(0).Count);

        // Undo
        Assert.True(doc.RemoveLastAnnotation(0));
        Assert.Equal(before, doc.GetAnnotations(0).Count);

        // Redo
        doc.AddAnnotationFromSpec(spec!);
        var annots = doc.GetAnnotations(0);
        Assert.Equal(before + 1, annots.Count);
        Assert.Contains(annots, a => a.Subtype == (int)MarkupKind.Highlight);
    }

    [Fact]
    public void DeleteAnnotation_Undo_RestoresSubtypeAndContents()
    {
        using var doc = PdfDocument.Open(CorpusPath("hello.pdf"));
        doc.AddNote(0, 100, 100, "Remember this");
        int noteIndex = doc.GetAnnotations(0).Count - 1;

        // Capture-before-delete, then delete.
        var spec = doc.CaptureAnnotation(0, noteIndex);
        Assert.NotNull(spec);
        Assert.True(doc.RemoveAnnotation(0, noteIndex));
        Assert.DoesNotContain(doc.GetAnnotations(0), a => a.IsNote);

        // Undo the delete.
        doc.AddAnnotationFromSpec(spec!);
        var note = doc.GetAnnotations(0).FirstOrDefault(a => a.IsNote);
        Assert.NotNull(note);
        Assert.Equal("Remember this", note!.Contents);
    }

    [Fact]
    public void CaptureInk_PreservesStrokeGeometry()
    {
        using var doc = PdfDocument.Open(CorpusPath("hello.pdf"));
        var stroke = new List<(double X, double Y)> { (100, 100), (150, 120), (200, 90) };
        doc.AddInk(0, stroke, 226, 34, 34, 255, 3.0f);

        var spec = doc.CaptureLastAnnotation(0);
        Assert.NotNull(spec);
        Assert.Equal(PdfiumInterop.PdfiumNative.AnnotInk, spec!.Subtype);
        Assert.Single(spec.InkStrokes);
        Assert.Equal(3, spec.InkStrokes[0].Count);

        // Round-trip: remove, re-add from spec, capture again — geometry survives.
        doc.RemoveLastAnnotation(0);
        doc.AddAnnotationFromSpec(spec);
        var respec = doc.CaptureLastAnnotation(0);
        Assert.NotNull(respec);
        Assert.Equal(spec.InkStrokes[0].Count, respec!.InkStrokes[0].Count);
    }

    [Fact]
    public void CaptureAnnotation_ReturnsNullForExoticSubtype()
    {
        // linked.pdf carries link annotations (subtype 2) we don't re-create.
        using var doc = PdfDocument.Open(CorpusPath("linked.pdf"));
        var annots = doc.GetAnnotations(0);
        // If a link annot is present, capturing it must return null (not throw).
        foreach (var a in annots.Where(a => a.Subtype == 2))
        {
            Assert.Null(doc.CaptureAnnotation(0, a.Index));
        }
    }
}

public class PageMoveUndoTests
{
    private static string CorpusPath(string name) => Path.Combine(AppContext.BaseDirectory, "corpus", name);

    [Fact]
    public void RestoreMovedPages_UndoesAMove()
    {
        using var doc = PdfDocument.Open(CorpusPath("book-1000.pdf"));

        doc.MovePages([0, 1], 3);       // scramble
        doc.RestoreMovedPages([0, 1], 3); // undo

        for (int i = 0; i < 6; i++)
        {
            Assert.Contains($"Page {i + 1}", doc.ExtractText(i));
        }
    }

    [Fact]
    public void DeleteThenReinsertSnapshots_RestoresOriginal()
    {
        using var doc = PdfDocument.Open(CorpusPath("book-1000.pdf"));

        // Simulate the app's delete-undo: snapshot each victim, delete, re-insert.
        int[] victims = [1, 3, 5];
        var snapshots = victims.Select(p => doc.ExportPages([p])).ToArray();
        doc.DeletePages(victims);
        Assert.Equal(997, doc.PageCount);

        for (int i = 0; i < victims.Length; i++)
        {
            doc.InsertPages(snapshots[i], victims[i]);
        }

        Assert.Equal(1000, doc.PageCount);
        for (int i = 0; i < 7; i++)
        {
            Assert.Contains($"Page {i + 1}", doc.ExtractText(i));
        }
    }
}
