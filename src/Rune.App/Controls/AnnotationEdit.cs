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
}
