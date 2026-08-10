namespace Rune.Controls;

/// <summary>
/// The annotation tool currently armed in the toolbar.
///
/// Exactly one is active at a time, Snipping-Tool style: picking a tool changes
/// what dragging on the page does and stays armed until another tool is picked
/// or Esc is pressed. <see cref="None"/> is ordinary reading, where a drag
/// selects text.
/// </summary>
public enum AnnotationTool
{
    /// <summary>Reading. Drag selects text.</summary>
    None,

    /// <summary>Freehand ink. Drag draws a stroke.</summary>
    Pen,

    /// <summary>
    /// Text markup. Drag over text highlights (or underlines/strikes it out,
    /// per the tool's style) in one gesture — unlike a paint-style highlighter
    /// this writes real PDF markup that stays attached to the words.
    /// </summary>
    Highlighter,

    /// <summary>Sticky note. Click places one.</summary>
    Note,

    /// <summary>
    /// Free text. Click puts a caret anywhere on the page, blank or not, and
    /// what is typed is previewed in place until the box is committed.
    /// </summary>
    Text,

    /// <summary>Signature stamp. Drag defines the box to place it in.</summary>
    Signature,

    /// <summary>Click removes the annotation under the pointer.</summary>
    Eraser,
}
