using Microsoft.Graphics.Canvas;
using Microsoft.Graphics.Canvas.Geometry;
using Microsoft.Graphics.Canvas.Text;

namespace Codale.App.Controls;

// Change bars between the line numbers and the text, and the hover peek at the committed text.
// Resting the pointer on a bar opens a panel over the hunk showing the previous version of
// those lines, so before and after can be compared without leaving the editor.
public sealed partial class CodeEditor
{
    private const double DiffBarWidth = 3;
    private const int MaxPreviewLines = 60;

    private IReadOnlyList<EditorDiffMarker> _diffMarkers = [];
    private EditorDiffMarker? _hoveredMarker;

    private static readonly Windows.UI.Color DiffAddedColor = Windows.UI.Color.FromArgb(255, 63, 185, 80);
    private static readonly Windows.UI.Color DiffModifiedColor = Windows.UI.Color.FromArgb(255, 56, 139, 253);
    private static readonly Windows.UI.Color DiffDeletedColor = Windows.UI.Color.FromArgb(255, 248, 81, 73);

    /// <summary>Replaces the change bars; pass an empty list to clear them.</summary>
    public void SetDiffMarkers(IReadOnlyList<EditorDiffMarker> markers)
    {
        _diffMarkers = markers;
        _hoveredMarker = null;
        _marksDirty = true;
        Invalidate();
    }

    private static Windows.UI.Color DiffColor(EditorDiffKind kind) => kind switch
    {
        EditorDiffKind.Added => DiffAddedColor,
        EditorDiffKind.Modified => DiffModifiedColor,
        _ => DiffDeletedColor,
    };

    private double DiffBarX => GutterWidth - DiffZone;

    /// <summary>Top of a line's first row in view coordinates; the line past the end is the bottom of the text.</summary>
    private double LineTop(int line) =>
        (line >= _model.Buffer.LineCount ? TotalRows : FirstRowOf(Math.Max(0, line))) * _lineHeight - _scrollY;

    private void DrawDiffMarkers(CanvasDrawingSession ds, double viewportHeight)
    {
        var x = (float)DiffBarX;
        foreach (var marker in _diffMarkers)
        {
            var top = LineTop(marker.StartLine);
            var color = DiffColor(marker.Kind);
            if (marker.LineCount == 0)
            {
                if (top < -6 || top > viewportHeight + 6)
                {
                    continue;
                }

                // A deletion: a small notch pointing at the seam where the lines used to be.
                using var triangle = CanvasGeometry.CreatePolygon(ds,
                [
                    new System.Numerics.Vector2(x, (float)top - 4),
                    new System.Numerics.Vector2(x + 5, (float)top),
                    new System.Numerics.Vector2(x, (float)top + 4),
                ]);
                ds.FillGeometry(triangle, color);
                continue;
            }

            var bottom = LineTop(marker.StartLine + marker.LineCount);
            if (bottom < 0 || top > viewportHeight)
            {
                continue;
            }

            var hovered = ReferenceEquals(marker, _hoveredMarker);
            ds.FillRectangle(x, (float)top, (float)(hovered ? DiffBarWidth + 2 : DiffBarWidth), (float)Math.Max(2, bottom - top), color);
        }
    }

    private EditorDiffMarker? MarkerAt(Windows.Foundation.Point point)
    {
        if (_diffMarkers.Count == 0 || point.X < DiffBarX - 3 || point.X > DiffBarX + DiffZone + 4)
        {
            return null;
        }

        foreach (var marker in _diffMarkers)
        {
            var top = LineTop(marker.StartLine);
            var bottom = marker.LineCount == 0 ? top : LineTop(marker.StartLine + marker.LineCount);
            if (marker.LineCount == 0 ? Math.Abs(point.Y - top) <= 6 : point.Y >= top && point.Y < Math.Max(bottom, top + 2))
            {
                return marker;
            }
        }

        return null;
    }

    /// <summary>Tracks the bar under the pointer; true when the pointer is on one.</summary>
    private bool UpdateDiffHover(Windows.Foundation.Point point)
    {
        var marker = MarkerAt(point);
        if (!ReferenceEquals(marker, _hoveredMarker))
        {
            _hoveredMarker = marker;
            Invalidate();
        }

        return marker is not null;
    }

    private void HideDiffPreview()
    {
        if (_hoveredMarker is not null)
        {
            _hoveredMarker = null;
            Invalidate();
        }
    }

    /// <summary>
    /// The previous version of the hovered hunk, in a panel laid over the current lines (or, for a
    /// pure deletion, just below the seam), with the old line numbers in its own gutter.
    /// </summary>
    private void DrawDiffPeek(CanvasDrawingSession ds, double width, double height)
    {
        if (_hoveredMarker is not { } marker || _textFormat is null)
        {
            return;
        }

        var shown = Math.Min(marker.OldLines.Count, MaxPreviewLines);
        var rows = Math.Max(1, shown + (marker.OldLines.Count > shown ? 1 : 0));
        var top = (float)LineTop(marker.StartLine);
        var left = (float)(DiffBarX + DiffBarWidth + 3);
        var right = (float)(width - VBarHoverWidth - 4);
        var panelHeight = (float)(rows * _lineHeight + 8);
        if (top + panelHeight > height && top - panelHeight > 0 && marker.LineCount == 0)
        {
            top -= panelHeight;
        }

        ds.FillRoundedRectangle(left, top, right - left, panelHeight, 4, 4, Brush(_panelColor));
        ds.FillRoundedRectangle(left, top, right - left, panelHeight, 4, 4, Brush(Windows.UI.Color.FromArgb(0x26, 248, 81, 73)));
        ds.DrawRoundedRectangle(left, top, right - left, panelHeight, 4, 4, Brush(_panelBorderColor));

        var textX = (float)(TextLeft - _scrollX);
        using (ds.CreateLayer(1f, new Windows.Foundation.Rect(left + 1, top + 1, Math.Max(0, right - left - 2), Math.Max(0, panelHeight - 2))))
        {
            for (var i = 0; i < shown; i++)
            {
                var text = marker.OldLines[i].TrimEnd('\r');
                using var layout = new CanvasTextLayout(Device, text.Length == 0 ? " " : text, _textFormat, 0f, (float)_lineHeight);
                layout.Options = Microsoft.Graphics.Canvas.Text.CanvasDrawTextOptions.EnableColorFont;
                ds.DrawTextLayout(layout, textX, top + 4 + (float)(i * _lineHeight), Brush(_baseText));
            }

            var note = marker.OldLines.Count == 0
                ? "(these lines are new: nothing here in the committed version)"
                : marker.OldLines.Count > shown ? $"… {marker.OldLines.Count - shown} more lines" : null;
            if (note is not null)
            {
                using var layout = new CanvasTextLayout(Device, note, _textFormat, 0f, (float)_lineHeight);
                layout.Options = Microsoft.Graphics.Canvas.Text.CanvasDrawTextOptions.EnableColorFont;
                ds.DrawTextLayout(layout, textX, top + 4 + (float)((rows - 1) * _lineHeight), Brush(_lineNumberText));
            }
        }
    }
}
