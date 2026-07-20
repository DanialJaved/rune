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
            new("Rotate", "Ctrl+R"),
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
            new("Highlight selection", "Ctrl+H"),
            new("Draw with the pen", "Ctrl+E"),
            new("Copy selected text", "Ctrl+C"),
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
        ]),
        new("Vim keys (Settings toggle)",
        [
            new("Scroll", "j / k / h / l"),
            new("First / last page", "gg / G"),
            new("Next match or page / previous page", "n / p"),
        ]),
    ];
}
