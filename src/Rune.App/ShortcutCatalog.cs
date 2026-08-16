namespace Rune;

/// <summary>
/// The single source of truth for every keyboard shortcut Rune ships. The
/// shortcuts dialog renders it; keep it in sync when adding accelerators
/// (PROJECT.md §5 mirrors this table).
/// </summary>
internal static class ShortcutCatalog
{
    internal sealed record Shortcut(string Name, string Keys);
    internal sealed record Group(string Title, Shortcut[] Shortcuts);

    internal static readonly Group[] Groups =
    [
        new("Navigation",
        [
            new("Scroll", "↑ / ↓"),
            new("Previous / next page", "← / →"),
            new("Screen up / down", "PgUp / PgDn"),
            new("Screen down / up", "Space / Shift+Space"),
            new("First / last page", "Home / End"),
            new("Back / forward", "Alt+← / Alt+→"),
            new("Go to page", "Ctrl+K, type a number"),
            new("Bookmark current page", "Ctrl+B"),
        ]),
        new("View",
        [
            new("Zoom in / out", "Ctrl++ / Ctrl+-"),
            new("Actual size", "Ctrl+1"),
            new("Fit width / fit page", "Ctrl+2 / Ctrl+0"),
            new("Rotate right / left", "Ctrl+R / Ctrl+Shift+R"),
            new("Night mode", "Ctrl+I"),
            new("Sidebar", "F9"),
            new("Presentation", "F5"),
        ]),
        new("Find",
        [
            new("Find in document", "Ctrl+F"),
            new("Next / previous match", "F3 / Shift+F3"),
        ]),
        new("Annotate",
        [
            new("Pen, highlighter, note, text, picture, sign, eraser", "toolbar"),
            new("Highlight selection", "Ctrl+H"),
            new("Draw with the pen", "Ctrl+E"),
            new("Type on the page", "Ctrl+T"),
            new("Move or resize what you placed", "click it"),
            new("Resize a picture before dropping it", "Ctrl+wheel"),
            new("Delete what you placed", "Delete"),
            new("Put the tool away", "Esc"),
            new("Copy selected text", "Ctrl+C"),
        ]),
        // These take over from the document's own chords while a text box is
        // open or a form field has the caret, and hand them straight back on
        // Esc. Both halves matter, so both are listed.
        new("Formatting text (in a text box or a form field)",
        [
            new("Bold / italic / underline", "Ctrl+B / Ctrl+I / Ctrl+U"),
            new("Bigger / smaller", "Ctrl+Shift+> / Ctrl+Shift+<"),
            new("Left / centre / right / justify", "Ctrl+L / Ctrl+E / Ctrl+R / Ctrl+J"),
            new("Finish the box and free the shortcuts", "Esc"),
            new("Reflow a text box (its type stays the same size)", "drag a corner"),
        ]),
        new("File & window",
        [
            new("Open", "Ctrl+O"),
            new("Save / save as", "Ctrl+S / Ctrl+Shift+S"),
            new("Print", "Ctrl+P"),
            new("Document properties", "Ctrl+D"),
            new("Close tab", "Ctrl+W"),
            new("Command palette", "Ctrl+K"),
            new("Keyboard shortcuts", "F1"),
        ]),
        new("Pages (thumbnail sidebar)",
        [
            new("Select pages", "Click / Ctrl / Shift"),
            new("Reorder pages", "Drag"),
            new("Copy / cut pages", "Ctrl+C / Ctrl+X"),
            new("Paste pages (works across tabs)", "Ctrl+V"),
            new("Delete pages", "Delete"),
            new("Undo / redo", "Ctrl+Z / Ctrl+Y"),
        ]),
        new("Vim keys (Settings toggle)",
        [
            new("Scroll", "j / k / h / l"),
            new("First / last page", "gg / G"),
            new("Next match or page / previous page", "n / p"),
        ]),
    ];
}
