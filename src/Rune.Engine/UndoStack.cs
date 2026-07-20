namespace Rune.Engine;

/// <summary>An entry on the undo stack. The stack only needs a label and a size; execution lives with the owner.</summary>
public interface IUndoableEdit
{
    string Label { get; }

    /// <summary>Bytes of captured snapshot data (deleted-page PDFs etc.), for the memory cap.</summary>
    long SnapshotBytes { get; }
}

/// <summary>
/// A bounded per-document undo/redo stack. Deliberately dumb: it orders and
/// caps edits; the owner executes them (on the render thread) and keeps UI
/// state in sync. LIFO execution is what makes inverse operations composable —
/// every edit's undo runs against exactly the document state its redo left.
/// </summary>
public sealed class UndoStack<TEdit> where TEdit : class, IUndoableEdit
{
    public const int MaxEdits = 50;
    public const long MaxSnapshotBytes = 64L * 1024 * 1024;

    private readonly List<TEdit> _undo = [];
    private readonly List<TEdit> _redo = [];

    public bool CanUndo => _undo.Count > 0;
    public bool CanRedo => _redo.Count > 0;
    public string? UndoLabel => CanUndo ? _undo[^1].Label : null;
    public string? RedoLabel => CanRedo ? _redo[^1].Label : null;

    /// <summary>Records a new edit (which has already been executed). Clears the redo branch.</summary>
    public void Push(TEdit edit)
    {
        _undo.Add(edit);
        _redo.Clear();
        Trim();
    }

    /// <summary>Moves the top edit to the redo stack and returns it for the caller to execute its undo.</summary>
    public TEdit? PopUndo()
    {
        if (!CanUndo)
        {
            return null;
        }
        var edit = _undo[^1];
        _undo.RemoveAt(_undo.Count - 1);
        _redo.Add(edit);
        return edit;
    }

    /// <summary>Moves the top redo edit back to the undo stack and returns it for re-execution.</summary>
    public TEdit? PopRedo()
    {
        if (!CanRedo)
        {
            return null;
        }
        var edit = _redo[^1];
        _redo.RemoveAt(_redo.Count - 1);
        _undo.Add(edit);
        return edit;
    }

    /// <summary>Drops everything (save-in-place reopens the document; old edits reference dead state).</summary>
    public void Clear()
    {
        _undo.Clear();
        _redo.Clear();
    }

    private void Trim()
    {
        while (_undo.Count > MaxEdits)
        {
            _undo.RemoveAt(0);
        }
        long bytes = _undo.Sum(e => e.SnapshotBytes);
        while (bytes > MaxSnapshotBytes && _undo.Count > 1)
        {
            bytes -= _undo[0].SnapshotBytes;
            _undo.RemoveAt(0);
        }
    }
}
