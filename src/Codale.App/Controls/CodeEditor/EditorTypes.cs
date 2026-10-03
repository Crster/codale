namespace Codale.App.Controls;

/// <summary>
/// Where the caret is, in the shape the status readout consumes: the line 0-based,
/// the column already 1-based.
/// </summary>
public readonly record struct EditorCursorPosition(int LineNumber, int CharacterPositionInLine);

/// <summary>Carries the caret position along a selection-changed report.</summary>
public sealed class EditorSelectionEventArgs
{
    /// <summary>The caret line, 0-based.</summary>
    public required int LineNumber { get; init; }

    /// <summary>The caret column, 1-based.</summary>
    public required int CharacterPositionInLine { get; init; }
}

/// <summary>The text changed - by typing, pasting, or a programmatic edit.</summary>
public delegate void EditorTextEventHandler(CodeEditor sender);

/// <summary>The caret moved or the selection changed.</summary>
public delegate void EditorSelectionChangedEventHandler(CodeEditor sender, EditorSelectionEventArgs args);
