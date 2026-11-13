namespace Codale.Editor;

/// <summary>
/// One applied replacement, in the coordinates it was applied at: <see cref="Start"/>..<see cref="OldEnd"/>
/// held <see cref="Removed"/> and now <see cref="Start"/>..<see cref="NewEnd"/> holds <see cref="Inserted"/>.
/// </summary>
internal readonly record struct EditRecord(
    DocumentPosition Start,
    DocumentPosition OldEnd,
    DocumentPosition NewEnd,
    string Removed,
    string Inserted);

/// <summary>What kind of command made an undo step; runs of the same small kind merge into one step.</summary>
public enum EditKind
{
    Other,
    Typing,
    Deleting,
}

internal sealed class UndoStep(Selection[] before, EditKind kind)
{
    public Selection[] Before { get; } = before;

    public Selection[] After { get; set; } = [];

    public EditKind Kind { get; } = kind;

    public List<EditRecord> Edits { get; } = [];

    public long LastTouchTicks { get; set; }

    /// <summary>Closed to merging: a caret move, an undo or a different command came in between.</summary>
    public bool Sealed { get; set; }

    public long Size { get; set; }
}

/// <summary>
/// Undo and redo stacks of deltas. A step costs what its edits touched rather than a copy of the
/// document, and the stacks are bounded both by step count and by the text they hold, so a long
/// session on a large file cannot grow without limit.
/// </summary>
internal sealed class UndoHistory
{
    private const int MaxSteps = 2000;
    private const long MaxChars = 32L * 1024 * 1024;
    private static readonly long CoalesceTicks = TimeSpan.FromMilliseconds(1000).Ticks;

    private readonly LinkedList<UndoStep> _undo = new();
    private readonly Stack<UndoStep> _redo = new();
    private long _undoChars;

    public bool CanUndo => _undo.Count > 0;

    public bool CanRedo => _redo.Count > 0;

    /// <summary>Forgets everything (a new document).</summary>
    public void Clear()
    {
        _undo.Clear();
        _redo.Clear();
        _undoChars = 0;
    }

    /// <summary>Stops the next edit from merging into the current step (a caret move, a click, a save).</summary>
    public void Seal()
    {
        if (_undo.Last is { } last)
        {
            last.Value.Sealed = true;
        }
    }

    /// <summary>The step a new edit of <paramref name="kind"/> goes into: the open one when it continues it, else a new one.</summary>
    public UndoStep Begin(EditKind kind, Selection[] before, long nowTicks)
    {
        _redo.Clear();
        if (kind != EditKind.Other
            && _undo.Last is { } lastNode
            && lastNode.Value is { } last
            && last.Kind == kind
            && !last.Sealed
            && nowTicks - last.LastTouchTicks < CoalesceTicks
            && SameSelections(last.After, before))
        {
            last.LastTouchTicks = nowTicks;
            return last;
        }

        if (_undo.Last is { } previous)
        {
            previous.Value.Sealed = true;
        }

        var step = new UndoStep(before, kind) { LastTouchTicks = nowTicks, Sealed = kind == EditKind.Other };
        _undo.AddLast(step);
        return step;
    }

    public void Record(UndoStep step, EditRecord edit)
    {
        step.Edits.Add(edit);
        var size = edit.Removed.Length + edit.Inserted.Length + 16;
        step.Size += size;
        _undoChars += size;
        Trim();
    }

    /// <summary>Drops a step that turned out to change nothing.</summary>
    public void DiscardIfEmpty(UndoStep step)
    {
        if (step.Edits.Count == 0 && _undo.Last?.Value == step)
        {
            _undo.RemoveLast();
        }
    }

    public UndoStep? PopUndo()
    {
        if (_undo.Last is not { } node)
        {
            return null;
        }

        _undo.RemoveLast();
        _undoChars -= node.Value.Size;
        _redo.Push(node.Value);
        return node.Value;
    }

    public UndoStep? PopRedo()
    {
        if (!_redo.TryPop(out var step))
        {
            return null;
        }

        step.Sealed = true;
        _undo.AddLast(step);
        _undoChars += step.Size;
        return step;
    }

    private void Trim()
    {
        // Never drop the step being written (the last one).
        while (_undo.Count > 1 && (_undo.Count > MaxSteps || _undoChars > MaxChars))
        {
            _undoChars -= _undo.First!.Value.Size;
            _undo.RemoveFirst();
        }
    }

    private static bool SameSelections(Selection[] a, Selection[] b)
    {
        if (a.Length != b.Length)
        {
            return false;
        }

        for (var i = 0; i < a.Length; i++)
        {
            if (a[i].Anchor != b[i].Anchor || a[i].Caret != b[i].Caret)
            {
                return false;
            }
        }

        return true;
    }
}
