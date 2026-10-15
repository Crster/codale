using Codale.Git;

namespace Codale.App.Controls;

public enum EditorDiffKind
{
    Added,
    Modified,
    Deleted,
}

/// <summary>
/// One change bar in the editor gutter: a run of lines in the buffer that differs from HEAD.
/// A pure deletion has <see cref="LineCount"/> 0 and sits at the top of the line that followed it.
/// </summary>
public sealed record EditorDiffMarker(
    int StartLine,
    int LineCount,
    EditorDiffKind Kind,
    IReadOnlyList<string> OldLines,
    IReadOnlyList<string> NewLines);

public static class DiffMarkers
{
    /// <summary>Diffs the buffer against the committed text into gutter markers (0-based lines).</summary>
    public static IReadOnlyList<EditorDiffMarker> Build(string headText, string currentText)
    {
        var diff = TextDiff.Compute(
            "file",
            headText.Replace("\r\n", "\n"),
            currentText.Replace("\r\n", "\n"),
            numbered: true,
            context: 0);

        var markers = new List<EditorDiffMarker>();
        var delta = 0; // new line number minus old line number, from the groups already passed
        foreach (var hunk in diff.Hunks)
        {
            var removed = new List<DiffLine>();
            var added = new List<DiffLine>();
            foreach (var line in hunk.Lines)
            {
                if (line.Kind == DiffLineKind.Removed)
                {
                    removed.Add(line);
                }
                else if (line.Kind == DiffLineKind.Added)
                {
                    added.Add(line);
                }
                else
                {
                    Flush();
                }
            }

            Flush();

            void Flush()
            {
                if (removed.Count == 0 && added.Count == 0)
                {
                    return;
                }

                var oldLines = removed.Select(l => l.Text).ToList();
                var newLines = added.Select(l => l.Text).ToList();
                if (added.Count > 0 && added[0].NewNumber is { } newNumber)
                {
                    markers.Add(new EditorDiffMarker(
                        newNumber - 1,
                        added.Count,
                        removed.Count > 0 ? EditorDiffKind.Modified : EditorDiffKind.Added,
                        oldLines,
                        newLines));
                }
                else if (removed.Count > 0 && removed[0].OldNumber is { } oldNumber)
                {
                    markers.Add(new EditorDiffMarker(oldNumber + delta - 1, 0, EditorDiffKind.Deleted, oldLines, newLines));
                }

                delta += added.Count - removed.Count;
                removed.Clear();
                added.Clear();
            }
        }

        return markers;
    }
}