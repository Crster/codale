using Codale.Editor;

using Microsoft.Graphics.Canvas;

namespace Codale.App.Controls;

// Background highlights, all drawn through the same column mapping as the text:
//  - occurrences: other copies of the selected text, or of the word under the caret;
//  - pinned words (Alt+H): words the user keeps highlighted, each in its own colour;
//  - result highlights: search hits and rows handed in by the owning tab.
// All of them also show as stripes on the scrollbar.
public sealed partial class CodeEditor
{
    private const int MinOccurrenceLength = 1;
    private const int MaxOccurrenceLength = 200;
    private const int MaxOccurrences = 5000;

    private List<(int Line, int Col, int Length)> _occurrences = [];
    private Microsoft.UI.Dispatching.DispatcherQueueTimer? _occurrenceTimer;

    private sealed record PinnedWord(string Text, Windows.UI.Color Color)
    {
        public List<(int Line, int Col)> Hits { get; set; } = [];
    }

    private readonly List<PinnedWord> _pinnedWords = [];
    private int _pinnedHitsVersion = -1;

    private static readonly Windows.UI.Color[] PinnedPalette =
    [
        Windows.UI.Color.FromArgb(0x60, 0xF2, 0xC1, 0x4E),
        Windows.UI.Color.FromArgb(0x60, 0x4E, 0xC9, 0xB0),
        Windows.UI.Color.FromArgb(0x60, 0xE0, 0x6C, 0x9F),
        Windows.UI.Color.FromArgb(0x60, 0x6C, 0x9E, 0xF8),
        Windows.UI.Color.FromArgb(0x60, 0xA3, 0xD9, 0x6B),
        Windows.UI.Color.FromArgb(0x60, 0xF0, 0x8A, 0x4B),
    ];

    private IReadOnlyList<(int StartLine, int EndLine)> _resultRows = [];
    private IReadOnlyList<(int Line, int Col, int Length)> _resultMatches = [];

    // ------------------------------------------------------------------ occurrences

    /// <summary>The selection or the text changed: refind copies a moment later, never per event.</summary>
    private void QueueOccurrences()
    {
        if (_occurrenceTimer is null)
        {
            _occurrenceTimer = Microsoft.UI.Dispatching.DispatcherQueue.GetForCurrentThread().CreateTimer();
            _occurrenceTimer.IsRepeating = false;
            _occurrenceTimer.Interval = TimeSpan.FromMilliseconds(120);
            _occurrenceTimer.Tick += (_, _) => RefreshOccurrences();
        }

        _occurrenceTimer.Stop();
        _occurrenceTimer.Start();
    }

    private void RefreshOccurrences()
    {
        if (_closed)
        {
            return;
        }

        var found = new List<(int Line, int Col, int Length)>();
        var primary = _model.Primary;
        var buffer = _model.Buffer;
        string? needle = null;
        var wholeWord = false;
        var skip = primary.Start;

        if (!primary.IsEmpty)
        {
            if (primary.Start.Line == primary.End.Line)
            {
                var length = primary.End.Col - primary.Start.Col;
                if (length is >= MinOccurrenceLength and <= MaxOccurrenceLength)
                {
                    var text = buffer.GetRange(primary.Start, primary.End);
                    if (!string.IsNullOrWhiteSpace(text))
                    {
                        needle = text;
                    }
                }
            }
        }
        else if (!_model.HasMultipleSelections)
        {
            // A bare caret on a word marks that word's other uses, as whole words.
            var line = buffer.GetLine(primary.Caret.Line);
            var (start, end) = TextUnits.WordAt(line, primary.Caret.Col);
            if (end - start >= 2)
            {
                needle = line[start..end];
                wholeWord = true;
                skip = new DocumentPosition(primary.Caret.Line, start);
            }
        }

        if (needle is not null)
        {
            foreach (var match in TextSearch.FindAll(buffer, needle, wholeWord, MaxOccurrences))
            {
                if (match != skip || _model.HasMultipleSelections)
                {
                    found.Add((match.Line, match.Col, needle.Length));
                }
            }

            // A word used once has nothing to point at.
            if (wholeWord && found.Count == 0)
            {
                found.Clear();
            }
        }

        RefreshPinnedHits();

        if (found.Count == 0 && _occurrences.Count == 0)
        {
            return;
        }

        _occurrences = found;
        _marksDirty = true;
        Invalidate();
    }

    private void DrawOccurrences(CanvasDrawingSession ds, int firstLine, int lastLine)
    {
        if (_occurrences.Count == 0)
        {
            return;
        }

        var color = _model.Primary.IsEmpty ? _wordHighlightColor : _occurrenceColor;
        for (var i = FirstAtOrAfter(_occurrences, firstLine); i < _occurrences.Count; i++)
        {
            var (line, col, length) = _occurrences[i];
            if (line > lastLine)
            {
                break;
            }

            if (col + length <= _model.Buffer.LineLength(line))
            {
                FillSpan(ds, line, col, col + length, color);
            }
        }
    }

    private static int FirstAtOrAfter(List<(int Line, int Col, int Length)> hits, int line)
    {
        var lo = 0;
        var hi = hits.Count;
        while (lo < hi)
        {
            var mid = (lo + hi) / 2;
            if (hits[mid].Line < line)
            {
                lo = mid + 1;
            }
            else
            {
                hi = mid;
            }
        }

        return lo;
    }

    // ------------------------------------------------------------------ pinned words

    /// <summary>
    /// Alt+H: keeps the selected text (or the word at the caret) highlighted in its own colour,
    /// or removes that highlight when it is already pinned.
    /// </summary>
    public void TogglePinnedHighlight()
    {
        var primary = _model.Primary;
        string text;
        if (!primary.IsEmpty && primary.Start.Line == primary.End.Line)
        {
            text = _model.Buffer.GetRange(primary.Start, primary.End);
        }
        else
        {
            var line = _model.Buffer.GetLine(primary.Caret.Line);
            var (start, end) = TextUnits.WordAt(line, primary.Caret.Col);
            text = line[start..end];
        }

        if (string.IsNullOrWhiteSpace(text))
        {
            return;
        }

        var existing = _pinnedWords.FindIndex(p => p.Text == text);
        if (existing >= 0)
        {
            _pinnedWords.RemoveAt(existing);
        }
        else
        {
            var used = _pinnedWords.Select(p => p.Color).ToHashSet();
            var color = PinnedPalette.FirstOrDefault(c => !used.Contains(c), PinnedPalette[_pinnedWords.Count % PinnedPalette.Length]);
            _pinnedWords.Add(new PinnedWord(text, color));
        }

        _pinnedHitsVersion = -1;
        RefreshPinnedHits();
        _marksDirty = true;
        Invalidate();
    }

    /// <summary>Alt+Shift+H: drops every pinned highlight.</summary>
    public void ClearPinnedHighlights()
    {
        if (_pinnedWords.Count == 0)
        {
            return;
        }

        _pinnedWords.Clear();
        _marksDirty = true;
        Invalidate();
    }

    private void RefreshPinnedHits()
    {
        if (_pinnedWords.Count == 0 || _pinnedHitsVersion == _model.Buffer.Version)
        {
            return;
        }

        _pinnedHitsVersion = _model.Buffer.Version;
        foreach (var pinned in _pinnedWords)
        {
            var whole = pinned.Text.All(TextUnits.IsWordChar);
            pinned.Hits = TextSearch.FindAll(_model.Buffer, pinned.Text, whole, MaxOccurrences)
                .Select(p => (p.Line, p.Col)).ToList();
        }

        _marksDirty = true;
    }

    private void DrawPinnedWords(CanvasDrawingSession ds, int firstLine, int lastLine)
    {
        foreach (var pinned in _pinnedWords)
        {
            var length = pinned.Text.Length;
            foreach (var (line, col) in pinned.Hits)
            {
                if (line < firstLine)
                {
                    continue;
                }

                if (line > lastLine)
                {
                    break;
                }

                if (col + length <= _model.Buffer.LineLength(line))
                {
                    FillSpan(ds, line, col, col + length, pinned.Color);
                }
            }
        }
    }

    // ------------------------------------------------------------------ results handed in by the tab

    public void SetResultHighlights(
        IReadOnlyList<(int StartLine, int EndLine)> rows,
        IReadOnlyList<(int Line, int Col, int Length)> matches)
    {
        _resultRows = rows;
        _resultMatches = matches;
        Invalidate();
    }

    private void DrawResultHighlights(CanvasDrawingSession ds, int firstLine, int lastLine)
    {
        var rowBrush = Brush(SearchHighlightStyle.Row(_light));
        var left = (float)GutterWidth;
        var right = (float)(ViewportWidth - VBarRestWidth);
        foreach (var (start, end) in _resultRows)
        {
            for (var line = Math.Max(firstLine, start); line <= Math.Min(lastLine, end); line++)
            {
                var bottom = RowY(FirstRowOf(line) + RowCountOf(line)) - 1;
                ds.FillRectangle(left, bottom, right - left, 1, rowBrush);
            }
        }

        var matchColor = SearchHighlightStyle.Match(_light);
        foreach (var (line, col, length) in _resultMatches)
        {
            if (line < firstLine || line > lastLine || length <= 0 || line >= _model.Buffer.LineCount)
            {
                continue;
            }

            FillSpan(ds, line, col, col + length, matchColor);
        }
    }
}
