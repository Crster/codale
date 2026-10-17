using Microsoft.Graphics.Canvas;
using Microsoft.Graphics.Canvas.Geometry;
using Microsoft.UI;
using Microsoft.UI.Input;
using Microsoft.UI.Text;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Controls.Primitives;
using Microsoft.UI.Xaml.Input;
using Microsoft.UI.Xaml.Media;

namespace Codale.App.Controls;

// The change bars at the far left of the gutter, and the hover preview of what changed.
public sealed partial class CodeEditor
{
    private const double DiffBarWidth = 3;
    private const double DiffHoverWidth = 8;
    private const int MaxPreviewLines = 80;

    private IReadOnlyList<EditorDiffMarker> _diffMarkers = [];
    private EditorDiffMarker? _hoveredMarker;

    private static readonly Windows.UI.Color DiffAddedColor = Windows.UI.Color.FromArgb(255, 63, 185, 80);
    private static readonly Windows.UI.Color DiffModifiedColor = Windows.UI.Color.FromArgb(255, 56, 139, 253);
    private static readonly Windows.UI.Color DiffDeletedColor = Windows.UI.Color.FromArgb(255, 248, 81, 73);

    /// <summary>Replaces the change bars; pass an empty list to clear them.</summary>
    public void SetDiffMarkers(IReadOnlyList<EditorDiffMarker> markers)
    {
        _diffMarkers = markers;
        _marksDirty = true;
        UpdateExtent();
        HideDiffPreview();
        Canvas.Invalidate();
    }

    private static Windows.UI.Color DiffColor(EditorDiffKind kind) => kind switch
    {
        EditorDiffKind.Added => DiffAddedColor,
        EditorDiffKind.Modified => DiffModifiedColor,
        _ => DiffDeletedColor,
    };

    private double RowTop(int line) =>
        (line >= _rowOffsets.Count ? _totalRows : _rowOffsets[Math.Max(0, line)]) * _lineHeight - _verticalOffset;

    private void DrawDiffMarkers(CanvasDrawingSession session, double viewportHeight)
    {
        foreach (var marker in _diffMarkers)
        {
            var top = RowTop(marker.StartLine);
            var color = DiffColor(marker.Kind);
            if (marker.LineCount == 0)
            {
                // A deletion: a small notch pointing at the seam where the lines used to be.
                if (top < -6 || top > viewportHeight + 6)
                {
                    continue;
                }

                using var triangle = CanvasGeometry.CreatePolygon(
                    session,
                    [new System.Numerics.Vector2(0, (float)top - 4), new System.Numerics.Vector2(6, (float)top), new System.Numerics.Vector2(0, (float)top + 4)]);
                session.FillGeometry(triangle, color);
                continue;
            }

            var bottom = RowTop(marker.StartLine + marker.LineCount);
            if (bottom < 0 || top > viewportHeight)
            {
                continue;
            }

            session.FillRectangle(0, (float)top, (float)DiffBarWidth, (float)Math.Max(2, bottom - top), color);
        }
    }

    private EditorDiffMarker? MarkerAt(Windows.Foundation.Point point)
    {
        if (_diffMarkers.Count == 0 || point.X > DiffHoverWidth)
        {
            return null;
        }

        foreach (var marker in _diffMarkers)
        {
            var top = RowTop(marker.StartLine);
            var bottom = marker.LineCount == 0 ? top : RowTop(marker.StartLine + marker.LineCount);
            if (marker.LineCount == 0 ? Math.Abs(point.Y - top) <= 6 : point.Y >= top && point.Y < Math.Max(bottom, top + 2))
            {
                return marker;
            }
        }

        return null;
    }

    private void UpdateDiffHover(PointerRoutedEventArgs e)
    {
        var point = e.GetCurrentPoint(Canvas).Position;
        var marker = MarkerAt(point);
        if (ReferenceEquals(marker, _hoveredMarker))
        {
            return;
        }

        _hoveredMarker = marker;
        ProtectedCursor = InputSystemCursor.Create(marker is null ? InputSystemCursorShape.IBeam : InputSystemCursorShape.Hand);
        if (marker is null)
        {
            HideDiffPreview();
            return;
        }

        ShowDiffPreview(marker);
    }

    private void HideDiffPreview()
    {
        _hoveredMarker = null;
        Canvas.Invalidate();
    }

    private void ShowDiffPreview(EditorDiffMarker marker) => Canvas.Invalidate();

    // While the pointer rests on a bar, the hunk's rows are redrawn in place with the committed (HEAD) text,
    // so the user can flip between before (hovering) and after (not hovering) without leaving the editor.
    private void DrawDiffPeek(CanvasDrawingSession session, double viewportHeight)
    {
        if (_hoveredMarker is not { } marker || _textFormat is null)
        {
            return;
        }

        var rows = Math.Min(marker.OldLines.Count, MaxPreviewLines);
        var covered = Math.Max(marker.LineCount, rows);
        if (rows == 0 && marker.LineCount == 0)
        {
            return;
        }

        var top = RowTop(marker.StartLine);
        var height = Math.Max(covered, 1) * _lineHeight;
        var dark = ActualTheme == ElementTheme.Dark;
        var width = (float)Canvas.ActualWidth;

        session.FillRectangle(0, (float)top, width, (float)height, dark ? Windows.UI.Color.FromArgb(255, 30, 30, 30) : Colors.White);
        session.FillRectangle(0, (float)top, width, (float)height, Windows.UI.Color.FromArgb(60, 248, 81, 73));
        session.FillRectangle(0, (float)top, (float)DiffBarWidth, (float)height, DiffDeletedColor);

        for (var i = 0; i < rows; i++)
        {
            var text = marker.OldLines[i].TrimEnd('\r').Replace("\t", "    ");
            session.DrawText(text, (float)(TextX - _horizontalOffset), (float)(top + i * _lineHeight), _baseText, _textFormat);
        }

        if (marker.OldLines.Count > rows)
        {
            session.DrawText($"… {marker.OldLines.Count - rows} more", (float)TextX, (float)(top + rows * _lineHeight), _lineNumberText, _textFormat);
        }

        if (marker.OldLines.Count == 0)
        {
            session.DrawText("(nothing here in the committed version)", (float)TextX, (float)top, _lineNumberText, _textFormat);
        }
    }
}