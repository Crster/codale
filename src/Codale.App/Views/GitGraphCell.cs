using Codale.Git;

using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Media;
using Microsoft.UI.Xaml.Shapes;

using Windows.UI;

namespace Codale.App.Views;

/// <summary>
/// One row of the history graph: a dot for the commit in its lane, and the lines
/// carrying every lane from the top of the row to the row below.
/// </summary>
/// <remarks>
/// Drawn with plain shapes on a <see cref="Canvas"/> rather than a custom render pass -
/// rows are small and virtualized, so a handful of Lines and an Ellipse per row is all
/// the work needed. The cell stretches to the row it sits in (rows vary with the text
/// they carry), and each edge runs from its lane's top, through the middle (where the
/// dot sits), angling over to its parent lane at the very bottom; the row below starts
/// its own edges at those same points, so the lines read as continuous across rows.
/// </remarks>
public sealed class GitGraphCell : Canvas
{
    private const double LaneWidth = 11;

    private static readonly Color[] Palette =
    [
        Color.FromArgb(255, 0x3B, 0x82, 0xF6), // blue
        Color.FromArgb(255, 0xA8, 0x55, 0xF7), // purple
        Color.FromArgb(255, 0x10, 0xB9, 0x81), // green
        Color.FromArgb(255, 0xF5, 0x9E, 0x0B), // amber
        Color.FromArgb(255, 0xEC, 0x48, 0x99), // pink
        Color.FromArgb(255, 0xEF, 0x44, 0x44), // red
    ];

    public GitGraphCell()
    {
        HorizontalAlignment = HorizontalAlignment.Left;
        VerticalAlignment = VerticalAlignment.Stretch;
        DataContextChanged += (_, args) => Draw(args.NewValue as GitCommit);

        // The first draw happens before the row's height is known; once the cell is
        // arranged at the row's real height, redraw with it.
        SizeChanged += (_, _) => Draw(DataContext as GitCommit);
    }

    private void Draw(GitCommit? commit)
    {
        Children.Clear();

        if (commit is null)
        {
            return;
        }

        var laneCount = Math.Max(commit.LaneCount, commit.Lane + 1);
        Width = Math.Max(laneCount, 1) * LaneWidth;

        // Not arranged yet; SizeChanged brings us back with the row's real height.
        var height = ActualHeight;
        if (height <= 0)
        {
            return;
        }

        foreach (var edge in commit.Edges)
        {
            var line = new Polyline
            {
                Stroke = new SolidColorBrush(Brush(edge.From)),
                StrokeThickness = 1.5,
                StrokeLineJoin = PenLineJoin.Round,
            };

            var fromX = X(edge.From);
            var toX = X(edge.To);

            var points = new PointCollection
            {
                new Windows.Foundation.Point(fromX, 0),
                new Windows.Foundation.Point(fromX, height / 2),
                new Windows.Foundation.Point(toX, height),
            };
            line.Points = points;
            Children.Add(line);
        }

        var dot = new Ellipse
        {
            Width = 7,
            Height = 7,
            Fill = new SolidColorBrush(Brush(commit.Lane)),
        };

        SetLeft(dot, X(commit.Lane) - 3.5);
        SetTop(dot, height / 2 - 3.5);
        Children.Add(dot);

        // The checked-out commit gets a ring, the way a graph marks HEAD.
        if (commit.Refs.Contains("HEAD", StringComparison.Ordinal))
        {
            var ring = new Ellipse
            {
                Width = 11,
                Height = 11,
                Stroke = new SolidColorBrush(Brush(commit.Lane)),
                StrokeThickness = 1.5,
            };

            SetLeft(ring, X(commit.Lane) - 5.5);
            SetTop(ring, height / 2 - 5.5);
            Children.Add(ring);
        }
    }

    private static double X(int lane) => lane * LaneWidth + LaneWidth / 2;

    private static Color Brush(int lane) => Palette[lane % Palette.Length];
}
