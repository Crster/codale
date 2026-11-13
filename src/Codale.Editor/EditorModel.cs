using System.Text;

namespace Codale.Editor;

/// <summary>
/// The editing engine: a <see cref="TextBuffer"/>, one or more selections, and every command that
/// changes them. Each command builds a batch of replacements from the selections (one per caret,
/// or one per block of lines), applies the batch top to bottom in a single pass, records it as one
/// undo step and lands the selections where the command says. No pixels here: the view maps the
/// commands that depend on layout (vertical movement, clicks) onto <see cref="SetSelections"/>.
/// </summary>
public sealed class EditorModel
{
    private readonly UndoHistory _history = new();
    private List<Selection> _selections = [new(default(DocumentPosition))];
    private int _primary;

    /// <summary>When the last Ctrl+D selection came from expanding a bare caret, matches must be whole words.</summary>
    private bool _occurrenceWholeWord;

    public EditorModel()
    {
        Buffer.Reloaded += () => _history.Clear();
    }

    public TextBuffer Buffer { get; } = new();

    /// <summary>Spaces an indent level is, and the tab stop interval.</summary>
    public int TabWidth { get; set; } = 4;

    /// <summary>Indent with spaces (true) or a tab character.</summary>
    public bool InsertSpaces { get; set; } = true;

    /// <summary>Clock for coalescing typing into undo steps; replaceable in tests.</summary>
    public Func<long> Clock { get; set; } = () => DateTime.UtcNow.Ticks;

    /// <summary>The carets moved or the selections changed (also after edits).</summary>
    public event Action? SelectionsChanged;

    /// <summary>A command changed the text; raised once per command, after its selections settled.</summary>
    public event Action? Edited;

    public IReadOnlyList<Selection> Selections => _selections;

    public int PrimaryIndex => _primary;

    public Selection Primary => _selections[_primary];

    public bool HasMultipleSelections => _selections.Count > 1;

    public bool HasSelection
    {
        get
        {
            foreach (var selection in _selections)
            {
                if (!selection.IsEmpty)
                {
                    return true;
                }
            }

            return false;
        }
    }

    public bool CanUndo => _history.CanUndo;

    public bool CanRedo => _history.CanRedo;

    private string IndentUnit => InsertSpaces ? new string(' ', TabWidth) : "\t";

    // ------------------------------------------------------------------ document

    /// <summary>Replaces the whole text; undo history restarts and the caret goes to the top.</summary>
    public void Load(string text)
    {
        Buffer.Load(text);
        _selections = [new(default(DocumentPosition))];
        _primary = 0;
        SelectionsChanged?.Invoke();
    }

    /// <summary>Stops the next keystroke merging into the current undo step.</summary>
    public void SealUndo() => _history.Seal();

    // ------------------------------------------------------------------ selections

    public void SetCaret(DocumentPosition caret, bool extend = false)
    {
        var anchor = extend ? Primary.Anchor : caret;
        SetSelections([new Selection(anchor, caret)], 0);
    }

    public void SetSelection(Selection selection) => SetSelections([selection], 0);

    /// <summary>
    /// Replaces the selections. They are clamped into the document, sorted, and overlapping ones
    /// merged; <paramref name="primary"/> is the index (into <paramref name="selections"/>) of the
    /// one that owns the scroll position, or -1 for the last.
    /// </summary>
    public void SetSelections(IReadOnlyList<Selection> selections, int primary = -1)
    {
        if (selections.Count == 0)
        {
            return;
        }

        if (primary < 0 || primary >= selections.Count)
        {
            primary = selections.Count - 1;
        }

        Normalize(selections, primary, out var normalized, out var newPrimary);
        _selections = normalized;
        _primary = newPrimary;
        _history.Seal();
        SelectionsChanged?.Invoke();
    }

    /// <summary>Adds a caret (or selection); clicking an existing caret again removes it.</summary>
    public void ToggleCaret(DocumentPosition position)
    {
        position = Buffer.Clamp(position);
        if (_selections.Count > 1)
        {
            var hit = _selections.FindIndex(s => s.IsEmpty ? s.Caret == position : s.Contains(position));
            if (hit >= 0)
            {
                var rest = new List<Selection>(_selections);
                rest.RemoveAt(hit);
                SetSelections(rest, Math.Min(_primary, rest.Count - 1));
                return;
            }
        }

        var list = new List<Selection>(_selections) { new(position) };
        SetSelections(list, list.Count - 1);
    }

    public void AddSelection(Selection selection)
    {
        var list = new List<Selection>(_selections) { selection };
        SetSelections(list, list.Count - 1);
    }

    /// <summary>Escape with several carets: keep only the primary one.</summary>
    public bool CollapseToPrimary()
    {
        if (_selections.Count <= 1)
        {
            return false;
        }

        SetSelections([Primary], 0);
        return true;
    }

    public void SelectAll() => SetSelection(new Selection(default, Buffer.End));

    /// <summary>Moves every caret by <paramref name="move"/>; with <paramref name="extend"/> the anchors stay.</summary>
    public void MoveCarets(Func<Selection, DocumentPosition> move, bool extend, bool keepPreferredX = false)
    {
        var list = new List<Selection>(_selections.Count);
        foreach (var selection in _selections)
        {
            var caret = Buffer.Clamp(move(selection));
            list.Add(new Selection(extend ? selection.Anchor : caret, caret, keepPreferredX ? selection.PreferredX : double.NaN));
        }

        SetSelections(list, _primary);
    }

    /// <summary>Left/Right: one grapheme cluster, across line ends; collapses a selection onto its edge first.</summary>
    public void MoveHorizontal(bool back, bool word, bool extend) => MoveCarets(selection =>
    {
        if (!extend && !selection.IsEmpty && !word)
        {
            return back ? selection.Start : selection.End;
        }

        var caret = selection.Caret;
        var line = Buffer.GetLine(caret.Line);
        if (back)
        {
            if (caret.Col == 0)
            {
                return caret.Line > 0 ? new DocumentPosition(caret.Line - 1, Buffer.LineLength(caret.Line - 1)) : caret;
            }

            return new DocumentPosition(caret.Line, word ? TextUnits.PreviousWordStop(line, caret.Col) : TextUnits.PreviousBoundary(line, caret.Col));
        }

        if (caret.Col >= line.Length)
        {
            return caret.Line < Buffer.LineCount - 1 ? new DocumentPosition(caret.Line + 1, 0) : caret;
        }

        return new DocumentPosition(caret.Line, word ? TextUnits.NextWordStop(line, caret.Col) : TextUnits.NextBoundary(line, caret.Col));
    }, extend);

    /// <summary>Smart Home: the first press lands on the text, the next on column 0.</summary>
    public void MoveHome(bool extend) => MoveCarets(selection =>
    {
        var indent = Buffer.IndentOf(selection.Caret.Line).Length;
        return new DocumentPosition(selection.Caret.Line, selection.Caret.Col == indent ? 0 : indent);
    }, extend);

    public void MoveEnd(bool extend) => MoveCarets(s => new DocumentPosition(s.Caret.Line, Buffer.LineLength(s.Caret.Line)), extend);

    public void MoveDocumentStart(bool extend) => MoveCarets(_ => default, extend);

    public void MoveDocumentEnd(bool extend) => MoveCarets(_ => Buffer.End, extend);

    /// <summary>Selects the word or symbol run around a position (double click).</summary>
    public Selection RunAt(DocumentPosition position)
    {
        position = Buffer.Clamp(position);
        var line = Buffer.GetLine(position.Line);
        var (start, end) = TextUnits.RunAt(line, position.Col);
        return new Selection(new DocumentPosition(position.Line, start), new DocumentPosition(position.Line, end));
    }

    /// <summary>The whole line, including its break when there is a next line (triple click).</summary>
    public Selection LineAt(int line)
    {
        line = Math.Clamp(line, 0, Buffer.LineCount - 1);
        var end = line < Buffer.LineCount - 1 ? new DocumentPosition(line + 1, 0) : new DocumentPosition(line, Buffer.LineLength(line));
        return new Selection(new DocumentPosition(line, 0), end);
    }

    // ------------------------------------------------------------------ occurrences

    /// <summary>
    /// Ctrl+D. A bare caret selects its word; after that each press adds the next copy of the
    /// primary selection's text as a new selection, wrapping at the end of the document.
    /// </summary>
    public bool AddNextOccurrence()
    {
        var primary = Primary;
        if (primary.IsEmpty)
        {
            var line = Buffer.GetLine(primary.Caret.Line);
            var (start, end) = TextUnits.WordAt(line, primary.Caret.Col);
            if (start == end)
            {
                return false;
            }

            _occurrenceWholeWord = true;
            var expanded = _selections.ToList();
            expanded[_primary] = new Selection(new DocumentPosition(primary.Caret.Line, start), new DocumentPosition(primary.Caret.Line, end));
            SetSelections(expanded, _primary);
            return true;
        }

        if (primary.Start.Line != primary.End.Line)
        {
            return false;
        }

        if (_selections.Count == 1)
        {
            _occurrenceWholeWord = IsWholeWord(primary);
        }

        var needle = Buffer.GetRange(primary.Start, primary.End);
        foreach (var match in TextSearch.FindFrom(Buffer, needle, primary.End, _occurrenceWholeWord))
        {
            var candidate = new Selection(match, new DocumentPosition(match.Line, match.Col + needle.Length));
            if (_selections.Any(s => s.Start == candidate.Start))
            {
                continue;
            }

            AddSelection(candidate);
            return true;
        }

        return false;
    }

    /// <summary>Ctrl+Shift+L: a selection on every copy of the primary selection (or the word at the caret).</summary>
    public int SelectAllOccurrences(int max = 10_000)
    {
        var primary = Primary;
        var wholeWord = false;
        if (primary.IsEmpty)
        {
            var line = Buffer.GetLine(primary.Caret.Line);
            var (start, end) = TextUnits.WordAt(line, primary.Caret.Col);
            if (start == end)
            {
                return 0;
            }

            primary = new Selection(new DocumentPosition(primary.Caret.Line, start), new DocumentPosition(primary.Caret.Line, end));
            wholeWord = true;
        }

        if (primary.Start.Line != primary.End.Line)
        {
            return 0;
        }

        var needle = Buffer.GetRange(primary.Start, primary.End);
        var list = new List<Selection>();
        var primaryIndex = 0;
        foreach (var match in TextSearch.FindAll(Buffer, needle, wholeWord, max))
        {
            if (match == primary.Start)
            {
                primaryIndex = list.Count;
            }

            list.Add(new Selection(match, new DocumentPosition(match.Line, match.Col + needle.Length)));
        }

        if (list.Count > 0)
        {
            SetSelections(list, primaryIndex);
        }

        return list.Count;
    }

    private bool IsWholeWord(Selection selection)
    {
        var line = Buffer.GetLine(selection.Start.Line);
        var (s, e) = (selection.Start.Col, selection.End.Col);
        return s < e
            && TextUnits.IsWordChar(line[s]) && TextUnits.IsWordChar(line[e - 1])
            && (s == 0 || !TextUnits.IsWordChar(line[s - 1]))
            && (e == line.Length || !TextUnits.IsWordChar(line[e]));
    }

    // ------------------------------------------------------------------ text of the selections

    /// <summary>The selected text; several selections are joined one per line.</summary>
    public string GetSelectedText(string newLine = "\r\n")
    {
        if (_selections.Count == 1)
        {
            return Normalize(Buffer.GetRange(Primary.Start, Primary.End), newLine);
        }

        var builder = new StringBuilder();
        var first = true;
        foreach (var selection in _selections)
        {
            if (selection.IsEmpty)
            {
                continue;
            }

            if (!first)
            {
                builder.Append(newLine);
            }

            builder.Append(Normalize(Buffer.GetRange(selection.Start, selection.End), newLine));
            first = false;
        }

        return builder.ToString();
    }

    /// <summary>The full lines under the carets, each with its line break (copy/cut with nothing selected).</summary>
    public string GetCaretLinesText(string newLine = "\r\n")
    {
        var builder = new StringBuilder();
        var last = -1;
        foreach (var selection in _selections)
        {
            if (selection.Caret.Line == last)
            {
                continue;
            }

            last = selection.Caret.Line;
            builder.Append(Buffer.GetLine(last)).Append(newLine);
        }

        return builder.ToString();
    }

    public long SelectedLength(int newLineLength = 2)
    {
        long total = 0;
        foreach (var selection in _selections)
        {
            total += Buffer.Distance(selection.Start, selection.End, newLineLength);
        }

        return total;
    }

    private static string Normalize(string text, string newLine) => newLine == "\n" ? text : text.Replace("\n", newLine);

    // ------------------------------------------------------------------ editing commands

    /// <summary>Types (or pastes) text at every caret, replacing the selections.</summary>
    public void Type(string text)
    {
        var kind = text.Length == 1 && text[0] is not ('\n' or '\r') && !char.IsWhiteSpace(text[0]) ? EditKind.Typing : EditKind.Other;
        if (text == " ")
        {
            kind = EditKind.Typing;
        }

        Execute(kind, PerSelection(s => new PendingEdit(s.Start, s.End, text)), r => Collapsed(r.NewEnds));
    }

    /// <summary>
    /// Pastes clipboard text. When it holds exactly one line per caret (a multi-caret copy), each
    /// caret gets its own line; otherwise every caret gets the whole text.
    /// </summary>
    public void Paste(string text)
    {
        text = text.Replace("\r\n", "\n").Replace('\r', '\n');
        if (_selections.Count > 1)
        {
            var lines = text.EndsWith('\n') ? text[..^1].Split('\n') : text.Split('\n');
            if (lines.Length == _selections.Count)
            {
                var i = 0;
                Execute(EditKind.Other, PerSelection(s => new PendingEdit(s.Start, s.End, lines[i++])), r => Collapsed(r.NewEnds));
                return;
            }
        }

        Execute(EditKind.Other, PerSelection(s => new PendingEdit(s.Start, s.End, text)), r => Collapsed(r.NewEnds));
    }

    /// <summary>Replaces an explicit range as one undo step and puts a single caret after it.</summary>
    public DocumentPosition ReplaceRange(DocumentPosition start, DocumentPosition end, string text)
    {
        var landed = start;
        if (end < start)
        {
            (start, end) = (end, start);
        }

        Execute(EditKind.Other, [new PendingEdit(Buffer.Clamp(start), Buffer.Clamp(end), text)], r =>
        {
            landed = r.NewEnds[0];
            return [new Selection(landed)];
        });
        return landed;
    }

    /// <summary>Enter: a line break carrying the line's indentation, one level deeper after an opener.</summary>
    public void InsertNewLine()
    {
        var carets = new DocumentPosition[_selections.Count];
        var index = 0;
        var edits = PerSelection(s =>
        {
            var line = Buffer.GetLine(s.Start.Line);
            var indent = Buffer.IndentOf(s.Start.Line);
            if (indent.Length > s.Start.Col)
            {
                indent = indent[..s.Start.Col];
            }

            var before = s.Start.Col > 0 ? line[s.Start.Col - 1] : '\0';
            var endLine = Buffer.GetLine(s.End.Line);
            var after = s.End.Col < endLine.Length ? endLine[s.End.Col] : '\0';
            string text;
            int caretCol;
            if (before is '{' or '[' or '(')
            {
                var inner = indent + IndentUnit;
                caretCol = inner.Length;
                text = after == Closer(before) ? "\n" + inner + "\n" + indent : "\n" + inner;
            }
            else
            {
                caretCol = indent.Length;
                text = "\n" + indent;
            }

            carets[index++] = new DocumentPosition(0, caretCol);
            return new PendingEdit(s.Start, s.End, text);
        });

        Execute(EditKind.Other, edits, r =>
        {
            var list = new Selection[r.NewStarts.Length];
            for (var i = 0; i < list.Length; i++)
            {
                list[i] = new Selection(new DocumentPosition(r.NewStarts[i].Line + 1, carets[i].Col));
            }

            return list;
        });

        static char Closer(char open) => open switch { '{' => '}', '[' => ']', _ => ')' };
    }

    /// <summary>Tab: indents selected lines, or inserts spaces to the next tab stop at each caret.</summary>
    public void Tab()
    {
        if (_selections.Any(s => s.Start.Line != s.End.Line))
        {
            IndentLines();
            return;
        }

        Execute(EditKind.Other, PerSelection(s =>
        {
            var text = InsertSpaces ? new string(' ', TabWidth - (s.Start.Col % TabWidth)) : "\t";
            return new PendingEdit(s.Start, s.End, text);
        }), r => Collapsed(r.NewEnds));
    }

    public void Backspace(bool word = false)
    {
        Execute(EditKind.Deleting, PerSelection(s =>
        {
            if (!s.IsEmpty)
            {
                return new PendingEdit(s.Start, s.End, "");
            }

            var caret = s.Caret;
            if (caret.Col == 0)
            {
                return caret.Line == 0
                    ? new PendingEdit(caret, caret, "")
                    : new PendingEdit(new DocumentPosition(caret.Line - 1, Buffer.LineLength(caret.Line - 1)), caret, "");
            }

            var line = Buffer.GetLine(caret.Line);
            int from;
            if (word)
            {
                from = TextUnits.PreviousWordStop(line, caret.Col);
            }
            else if (InsertSpaces && caret.Col >= 1 && line.AsSpan(0, caret.Col).IndexOfAnyExcept(' ') < 0)
            {
                // In leading spaces, back to the previous tab stop: de-indenting is one press per level.
                from = (caret.Col - 1) / TabWidth * TabWidth;
            }
            else
            {
                from = TextUnits.PreviousBoundary(line, caret.Col);
            }

            return new PendingEdit(new DocumentPosition(caret.Line, from), caret, "");
        }), r => Collapsed(r.NewEnds));
    }

    public void Delete(bool word = false)
    {
        Execute(EditKind.Deleting, PerSelection(s =>
        {
            if (!s.IsEmpty)
            {
                return new PendingEdit(s.Start, s.End, "");
            }

            var caret = s.Caret;
            var line = Buffer.GetLine(caret.Line);
            if (caret.Col >= line.Length)
            {
                return caret.Line >= Buffer.LineCount - 1
                    ? new PendingEdit(caret, caret, "")
                    : new PendingEdit(caret, new DocumentPosition(caret.Line + 1, 0), "");
            }

            var to = word ? TextUnits.NextWordStop(line, caret.Col) : TextUnits.NextBoundary(line, caret.Col);
            return new PendingEdit(caret, new DocumentPosition(caret.Line, to), "");
        }), r => Collapsed(r.NewEnds));
    }

    /// <summary>Alt+Up / Alt+Down: moves the lines under each selection one line, keeping the selections on them.</summary>
    public bool MoveLines(bool up)
    {
        var blocks = LineBlocks();
        var edits = new List<PendingEdit>();
        var moved = new bool[blocks.Count];
        for (var b = 0; b < blocks.Count; b++)
        {
            var (first, last) = blocks[b];
            if (up ? first == 0 : last >= Buffer.LineCount - 1)
            {
                continue;
            }

            moved[b] = true;
            var block = JoinLines(first, last);
            if (up)
            {
                var above = Buffer.GetLine(first - 1);
                edits.Add(new PendingEdit(new DocumentPosition(first - 1, 0), new DocumentPosition(last, Buffer.LineLength(last)), block + "\n" + above));
            }
            else
            {
                var below = Buffer.GetLine(last + 1);
                edits.Add(new PendingEdit(new DocumentPosition(first, 0), new DocumentPosition(last + 1, Buffer.LineLength(last + 1)), below + "\n" + block));
            }
        }

        if (edits.Count == 0)
        {
            return false;
        }

        var shift = up ? -1 : 1;
        var after = _selections.Select(s =>
        {
            var b = BlockOf(blocks, s);
            return moved[b]
                ? new Selection(new DocumentPosition(s.Anchor.Line + shift, s.Anchor.Col), new DocumentPosition(s.Caret.Line + shift, s.Caret.Col))
                : s;
        }).ToArray();
        Execute(EditKind.Other, edits, _ => after);
        return true;
    }

    /// <summary>Shift+Alt+Down / Up: copies the lines under each selection below (or above) them.</summary>
    public void DuplicateLines(bool down = true)
    {
        var blocks = LineBlocks();
        var edits = new List<PendingEdit>();
        foreach (var (first, last) in blocks)
        {
            var block = JoinLines(first, last);
            edits.Add(down
                ? new PendingEdit(new DocumentPosition(last, Buffer.LineLength(last)), new DocumentPosition(last, Buffer.LineLength(last)), "\n" + block)
                : new PendingEdit(new DocumentPosition(first, 0), new DocumentPosition(first, 0), block + "\n"));
        }

        // Lines added by the blocks above push every later block down.
        var added = new int[blocks.Count];
        var running = 0;
        for (var b = 0; b < blocks.Count; b++)
        {
            var size = blocks[b].Last - blocks[b].First + 1;
            added[b] = running + (down ? size : 0);
            running += size;
        }

        var after = _selections.Select(s =>
        {
            var shift = added[BlockOf(blocks, s)];
            return new Selection(new DocumentPosition(s.Anchor.Line + shift, s.Anchor.Col), new DocumentPosition(s.Caret.Line + shift, s.Caret.Col));
        }).ToArray();
        Execute(EditKind.Other, edits, _ => after);
    }

    /// <summary>Ctrl+Shift+K: removes the lines under each selection.</summary>
    public void DeleteLines()
    {
        var blocks = LineBlocks();
        var edits = new List<PendingEdit>();
        var cols = new List<int>();
        foreach (var (first, last) in blocks)
        {
            if (last < Buffer.LineCount - 1)
            {
                edits.Add(new PendingEdit(new DocumentPosition(first, 0), new DocumentPosition(last + 1, 0), ""));
            }
            else if (first > 0)
            {
                edits.Add(new PendingEdit(new DocumentPosition(first - 1, Buffer.LineLength(first - 1)), new DocumentPosition(last, Buffer.LineLength(last)), ""));
            }
            else
            {
                edits.Add(new PendingEdit(default, new DocumentPosition(last, Buffer.LineLength(last)), ""));
            }

            cols.Add(_selections.First(s => s.Caret.Line >= first && s.Caret.Line <= last).Caret.Col);
        }

        Execute(EditKind.Other, edits, r =>
        {
            var list = new Selection[r.NewEnds.Length];
            for (var i = 0; i < list.Length; i++)
            {
                // Whichever way the block was cut out, its old place is now the start of the cut.
                var line = r.NewStarts[i].Line;
                list[i] = new Selection(Buffer.Clamp(new DocumentPosition(line, cols[i])));
            }

            return list;
        });
    }

    /// <summary>Ctrl+Enter / Ctrl+Shift+Enter: a new indented line below (or above) each caret's line.</summary>
    public void InsertLine(bool above)
    {
        var lines = _selections.Select(s => s.Caret.Line).Distinct().ToList();
        var edits = new List<PendingEdit>();
        var indents = new List<int>();
        foreach (var line in lines)
        {
            var indent = Buffer.IndentOf(line);
            indents.Add(indent.Length);
            edits.Add(above
                ? new PendingEdit(new DocumentPosition(line, 0), new DocumentPosition(line, 0), indent + "\n")
                : new PendingEdit(new DocumentPosition(line, Buffer.LineLength(line)), new DocumentPosition(line, Buffer.LineLength(line)), "\n" + indent));
        }

        Execute(EditKind.Other, edits, r =>
        {
            var list = new Selection[lines.Count];
            for (var i = 0; i < list.Length; i++)
            {
                list[i] = new Selection(above
                    ? new DocumentPosition(r.NewStarts[i].Line, indents[i])
                    : r.NewEnds[i]);
            }

            return list;
        });
    }

    /// <summary>Indents every line under the selections by one level.</summary>
    public void IndentLines()
    {
        var unit = IndentUnit;
        var edits = new List<PendingEdit>();
        foreach (var (first, last) in LineBlocks())
        {
            for (var line = first; line <= last; line++)
            {
                if (Buffer.LineLength(line) > 0 || first == last)
                {
                    edits.Add(new PendingEdit(new DocumentPosition(line, 0), new DocumentPosition(line, 0), unit));
                }
            }
        }

        Execute(EditKind.Other, edits, r => _selections.Select(s => MapSelection(r, s)).ToArray());
    }

    /// <summary>Shift+Tab: removes one level of indentation from every line under the selections.</summary>
    public void OutdentLines()
    {
        var edits = new List<PendingEdit>();
        foreach (var (first, last) in LineBlocks())
        {
            for (var line = first; line <= last; line++)
            {
                var text = Buffer.GetLine(line);
                var remove = 0;
                if (text.Length > 0 && text[0] == '\t')
                {
                    remove = 1;
                }
                else
                {
                    while (remove < TabWidth && remove < text.Length && text[remove] == ' ')
                    {
                        remove++;
                    }
                }

                if (remove > 0)
                {
                    edits.Add(new PendingEdit(new DocumentPosition(line, 0), new DocumentPosition(line, remove), ""));
                }
            }
        }

        Execute(EditKind.Other, edits, r => _selections.Select(s => MapSelection(r, s)).ToArray());
    }

    // ------------------------------------------------------------------ undo / redo

    public bool Undo()
    {
        var step = _history.PopUndo();
        if (step is null)
        {
            return false;
        }

        for (var i = step.Edits.Count - 1; i >= 0; i--)
        {
            var edit = step.Edits[i];
            Buffer.Replace(edit.Start, edit.NewEnd, edit.Removed);
        }

        RestoreSelections(step.Before);
        Edited?.Invoke();
        return true;
    }

    public bool Redo()
    {
        var step = _history.PopRedo();
        if (step is null)
        {
            return false;
        }

        foreach (var edit in step.Edits)
        {
            Buffer.Replace(edit.Start, edit.OldEnd, edit.Inserted);
        }

        RestoreSelections(step.After);
        Edited?.Invoke();
        return true;
    }

    private void RestoreSelections(Selection[] selections)
    {
        Normalize(selections, Math.Max(0, selections.Length - 1), out var normalized, out var primary);
        _selections = normalized;
        _primary = primary;
        SelectionsChanged?.Invoke();
    }

    // ------------------------------------------------------------------ batch machinery

    internal readonly record struct PendingEdit(DocumentPosition Start, DocumentPosition End, string Text);

    /// <summary>
    /// Where a batch's edits landed: index i is edit i's new start and end. <see cref="Map"/> moves
    /// any position from before the batch to where it is after it.
    /// </summary>
    internal sealed class BatchResult(List<PendingEdit> edits, DocumentPosition[] newStarts, DocumentPosition[] newEnds)
    {
        public DocumentPosition[] NewStarts { get; } = newStarts;

        public DocumentPosition[] NewEnds { get; } = newEnds;

        public DocumentPosition Map(DocumentPosition position, bool stickToStart = false)
        {
            // The last edit starting at or before the position decides where it lands.
            var lo = 0;
            var hi = edits.Count - 1;
            var found = -1;
            while (lo <= hi)
            {
                var mid = (lo + hi) / 2;
                if (edits[mid].Start <= position)
                {
                    found = mid;
                    lo = mid + 1;
                }
                else
                {
                    hi = mid - 1;
                }
            }

            if (found < 0)
            {
                return position;
            }

            var edit = edits[found];
            if (position < edit.End || (stickToStart && position == edit.Start && edit.Start != edit.End))
            {
                return stickToStart ? NewStarts[found] : NewEnds[found];
            }

            var end = NewEnds[found];
            return position.Line == edit.End.Line
                ? new DocumentPosition(end.Line, end.Col + (position.Col - edit.End.Col))
                : new DocumentPosition(position.Line + (end.Line - edit.End.Line), position.Col);
        }
    }

    private List<PendingEdit> PerSelection(Func<Selection, PendingEdit> build)
    {
        var edits = new List<PendingEdit>(_selections.Count);
        foreach (var selection in _selections)
        {
            edits.Add(build(selection));
        }

        return edits;
    }

    private static Selection[] Collapsed(DocumentPosition[] positions)
    {
        var list = new Selection[positions.Length];
        for (var i = 0; i < positions.Length; i++)
        {
            list[i] = new Selection(positions[i]);
        }

        return list;
    }

    private static Selection MapSelection(BatchResult result, Selection selection) =>
        new(result.Map(selection.Anchor), result.Map(selection.Caret));

    /// <summary>
    /// Applies a batch and records it. <paramref name="edits"/> are in original coordinates; they are
    /// applied in document order, each shifted by the edits before it, so the batch is one pass.
    /// </summary>
    private void Execute(EditKind kind, List<PendingEdit> edits, Func<BatchResult, IReadOnlyList<Selection>> after)
    {
        edits = Sanitize(edits);
        var anyChange = false;
        foreach (var edit in edits)
        {
            if (edit.Start != edit.End || edit.Text.Length > 0)
            {
                anyChange = true;
                break;
            }
        }

        if (!anyChange)
        {
            return;
        }

        var step = _history.Begin(kind, _selections.ToArray(), Clock());
        var newStarts = new DocumentPosition[edits.Count];
        var newEnds = new DocumentPosition[edits.Count];

        // Composition of the edits applied so far, for positions at or after the last one's end.
        var hasLast = false;
        var lastOldEnd = default(DocumentPosition);
        var lastNewEnd = default(DocumentPosition);
        DocumentPosition Shift(DocumentPosition p)
        {
            if (!hasLast)
            {
                return p;
            }

            return p.Line == lastOldEnd.Line
                ? new DocumentPosition(lastNewEnd.Line, lastNewEnd.Col + (p.Col - lastOldEnd.Col))
                : new DocumentPosition(p.Line + (lastNewEnd.Line - lastOldEnd.Line), p.Col);
        }

        for (var i = 0; i < edits.Count; i++)
        {
            var edit = edits[i];
            var start = Shift(edit.Start);
            var end = Shift(edit.End);
            if (start == end && edit.Text.Length == 0)
            {
                newStarts[i] = newEnds[i] = start;
                continue;
            }

            var landed = Buffer.Replace(start, end, edit.Text, out var removed);
            _history.Record(step, new EditRecord(start, end, landed, removed, edit.Text));
            newStarts[i] = start;
            newEnds[i] = landed;
            hasLast = true;
            lastOldEnd = edit.End;
            lastNewEnd = landed;
        }

        var result = new BatchResult(edits, newStarts, newEnds);
        var selections = after(result);
        Normalize(selections, Math.Min(_primary, selections.Count - 1), out var normalized, out var primary);
        _selections = normalized;
        _primary = primary;
        step.After = _selections.ToArray();
        _history.DiscardIfEmpty(step);
        SelectionsChanged?.Invoke();
        Edited?.Invoke();
    }

    /// <summary>Sorts edits and folds any that overlap into one, so the single-pass apply stays valid.</summary>
    private List<PendingEdit> Sanitize(List<PendingEdit> edits)
    {
        for (var i = 0; i < edits.Count; i++)
        {
            var e = edits[i];
            var start = Buffer.Clamp(DocumentPosition.Min(e.Start, e.End));
            var end = Buffer.Clamp(DocumentPosition.Max(e.Start, e.End));
            edits[i] = e with { Start = start, End = end };
        }

        var sorted = true;
        for (var i = 1; i < edits.Count && sorted; i++)
        {
            sorted = edits[i - 1].Start <= edits[i].Start;
        }

        if (!sorted)
        {
            edits = edits.OrderBy(e => e.Start).ToList();
        }

        for (var i = 1; i < edits.Count; i++)
        {
            var previous = edits[i - 1];
            var current = edits[i];
            if (current.Start < previous.End)
            {
                edits[i - 1] = new PendingEdit(previous.Start, DocumentPosition.Max(previous.End, current.End), previous.Text + current.Text);
                edits.RemoveAt(i);
                i--;
            }
        }

        return edits;
    }

    /// <summary>The line ranges under the selections, merged when they touch.</summary>
    private List<(int First, int Last)> LineBlocks()
    {
        var blocks = new List<(int First, int Last)>();
        foreach (var selection in _selections)
        {
            var first = selection.Start.Line;
            var last = selection.End.Line;
            if (last > first && selection.End.Col == 0)
            {
                last--;
            }

            if (blocks.Count > 0 && first <= blocks[^1].Last + 1)
            {
                blocks[^1] = (blocks[^1].First, Math.Max(blocks[^1].Last, last));
            }
            else
            {
                blocks.Add((first, last));
            }
        }

        return blocks;
    }

    private static int BlockOf(List<(int First, int Last)> blocks, Selection selection)
    {
        for (var b = 0; b < blocks.Count; b++)
        {
            if (selection.Start.Line >= blocks[b].First && selection.Start.Line <= blocks[b].Last)
            {
                return b;
            }
        }

        return blocks.Count - 1;
    }

    private string JoinLines(int first, int last)
    {
        var builder = new StringBuilder();
        for (var line = first; line <= last; line++)
        {
            if (line > first)
            {
                builder.Append('\n');
            }

            builder.Append(Buffer.GetLine(line));
        }

        return builder.ToString();
    }

    private void Normalize(IReadOnlyList<Selection> input, int primary, out List<Selection> output, out int newPrimary)
    {
        var count = input.Count;
        var indexed = new (Selection Selection, bool Primary)[count];
        for (var i = 0; i < count; i++)
        {
            var s = input[i];
            indexed[i] = (new Selection(Buffer.Clamp(s.Anchor), Buffer.Clamp(s.Caret), s.PreferredX), i == primary);
        }

        if (count > 1)
        {
            Array.Sort(indexed, (a, b) =>
            {
                var byStart = a.Selection.Start.CompareTo(b.Selection.Start);
                return byStart != 0 ? byStart : a.Selection.End.CompareTo(b.Selection.End);
            });
        }

        output = new List<Selection>(count);
        newPrimary = 0;
        for (var i = 0; i < count; i++)
        {
            var (current, isPrimary) = indexed[i];
            if (output.Count > 0)
            {
                var last = output[^1];
                var overlaps = current.Start < last.End
                    || current.Start == last.Start
                    || (current.IsEmpty && current.Start == last.End)
                    || (last.IsEmpty && last.Start == current.Start);
                if (overlaps)
                {
                    var start = DocumentPosition.Min(last.Start, current.Start);
                    var end = DocumentPosition.Max(last.End, current.End);
                    var reversed = isPrimary ? current.IsReversed : last.IsReversed;
                    output[^1] = reversed ? new Selection(end, start) : new Selection(start, end);
                    if (isPrimary)
                    {
                        newPrimary = output.Count - 1;
                    }

                    continue;
                }
            }

            if (isPrimary)
            {
                newPrimary = output.Count;
            }

            output.Add(current);
        }
    }
}
