using Microsoft.Graphics.Canvas;
using Microsoft.UI.Xaml;

namespace Codale.App.Controls;

public sealed partial class CodeEditor
{
    private IReadOnlyList<(int StartLine, int EndLine)> _resultRows = [];
    private IReadOnlyList<(int Line, int Col, int Length)> _resultMatches = [];

    public void SetResultHighlights(
        IReadOnlyList<(int StartLine, int EndLine)> rows,
        IReadOnlyList<(int Line, int Col, int Length)> matches)
    {
        _resultRows = rows;
        _resultMatches = matches;
        Canvas.Invalidate();
    }

    private void DrawResultHighlights(CanvasDrawingSession session, int firstLine, int lastLine)
    {
        var light = ActualTheme == ElementTheme.Light;
        var right = Math.Max(TextX, Canvas.ActualWidth - OverlayScrollBar.Thickness);
        // Clip to the text area so horizontal scrolling cannot paint over the gutter.
        using var clip = session.CreateLayer(1, new Windows.Foundation.Rect(TextX, 0,
            Math.Max(0, right - TextX), Canvas.ActualHeight));
        foreach (var (start, end) in _resultRows)
        {
            for (var line = Math.Max(firstLine, start); line <= Math.Min(lastLine, end); line++)
            {
                var rows = RowCountOf(_lineCache[line].Display.Length);
                for (var row = 0; row < rows; row++)
                {
                    var bottom = (_rowOffsets[line] + row + 1) * _lineHeight - _verticalOffset;
                    session.FillRectangle((float)TextX, (float)(bottom - 1), (float)(right - TextX), 1,
                        Brush(session, SearchHighlightStyle.Row(light)));
                }
            }
        }

        foreach (var (line, col, length) in _resultMatches)
        {
            if (line < firstLine || line > lastLine || length <= 0) continue;
            var raw = _doc.GetLine(line);
            var from = TextDocument.ExpandedColFromRaw(raw, Math.Clamp(col, 0, raw.Length));
            var to = TextDocument.ExpandedColFromRaw(raw, Math.Clamp(col + length, 0, raw.Length));
            for (var row = from / _wrapCols; row <= (to - 1) / _wrapCols && to > from; row++)
            {
                var rowStart = row * _wrapCols;
                var x = TextX + (Math.Max(from, rowStart) - rowStart) * _charWidth - _horizontalOffset;
                var width = (Math.Min(to, rowStart + _wrapCols) - Math.Max(from, rowStart)) * _charWidth;
                var y = (_rowOffsets[line] + row) * _lineHeight - _verticalOffset;
                session.FillRectangle((float)x, (float)y, (float)width, (float)_lineHeight,
                    Brush(session, SearchHighlightStyle.Match(light)));
            }
        }
    }
}
