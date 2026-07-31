using Rune.Engine;

namespace Rune.Controls;

/// <summary>
/// One undoable annotation edit, raised by <see cref="PdfViewer"/> after the
/// edit has been applied. The actions run on the render thread when the undo
/// stack replays them. Only raised for annotation subtypes Rune can faithfully
/// re-create (highlight/underline/strikeout/note/ink) — deleting a foreign
/// annotation still works but is not pushed onto the stack.
/// </summary>
public sealed class AnnotationEditEventArgs : EventArgs
{
    public required string Label { get; init; }
    public required int PageIndex { get; init; }
    public required Action<PdfDocument> UndoAction { get; init; }
    public required Action<PdfDocument> RedoAction { get; init; }

    /// <summary>
    /// Bytes this edit keeps alive, for <see cref="UndoStack{TEdit}"/>'s memory
    /// cap. Zero for markup and ink, which retain only a handful of
    /// coordinates — but a signature holds a whole bitmap, and 50 undoable
    /// signatures would otherwise retain tens of megabytes invisibly.
    /// </summary>
    public long SnapshotBytes { get; init; }
}
