using Codale.Core.Agents;

namespace Codale.Git.Tests;

public sealed class TextDiffTests
{
    [Fact]
    public void Replaced_line_reads_as_removed_then_added_with_numbers()
    {
        var diff = TextDiff.Compute("a.txt", "one\ntwo\nthree\n", "one\nTWO\nthree\n");

        var hunk = Assert.Single(diff.Hunks);
        Assert.Equal(
            [DiffLineKind.Context, DiffLineKind.Removed, DiffLineKind.Added, DiffLineKind.Context],
            hunk.Lines.Select(l => l.Kind));
        Assert.Equal(2, hunk.Lines[1].OldNumber);
        Assert.Equal(2, hunk.Lines[2].NewNumber);
        Assert.Equal("@@ -1,3 +1,3 @@", hunk.Header);
        Assert.Equal(1, diff.Added);
        Assert.Equal(1, diff.Removed);
    }

    [Fact]
    public void Distant_changes_split_into_hunks_trimmed_to_context()
    {
        var before = string.Join("\n", Enumerable.Range(1, 40).Select(i => $"line {i}"));
        var after = before.Replace("line 2\n", "line two\n").Replace("line 38", "line thirty-eight");

        var diff = TextDiff.Compute("a.txt", before, after);

        Assert.Equal(2, diff.Hunks.Count);
        Assert.All(diff.Hunks, h => Assert.True(h.Lines.Count <= 2 + 2 * TextDiff.DefaultContext));
        Assert.Equal(38, diff.Hunks[1].Lines.First(l => l.Kind == DiffLineKind.Removed).OldNumber);
    }

    [Fact]
    public void Snippet_diff_is_unnumbered_and_keeps_every_line()
    {
        var diff = TextDiff.Compute("a.cs", "a\nb\nc\nd\ne\nf\ng\nh\ni", "a\nb\nc\nd\nX\nf\ng\nh\ni", numbered: false);

        var hunk = Assert.Single(diff.Hunks);
        Assert.Equal(10, hunk.Lines.Count);
        Assert.All(hunk.Lines, l => Assert.Null(l.OldNumber ?? l.NewNumber));
    }

    [Fact]
    public void New_file_is_all_additions()
    {
        var diff = TextDiff.Compute("new.txt", null, "x\ny\n");

        Assert.True(diff.IsNew);
        Assert.Equal(2, diff.Added);
        Assert.Equal(0, diff.Removed);
    }

    [Fact]
    public void Identical_texts_have_no_hunks()
    {
        Assert.Empty(TextDiff.Compute("a", "same\n", "same\n").Hunks);
    }

    [Fact]
    public void Structured_patch_keeps_its_line_numbers()
    {
        var diff = TextDiff.FromPatch("a.cs",
        [
            new FilePatchHunk { OldStart = 10, NewStart = 10, Lines = [" keep", "-old", "+new", "+more", " tail"] },
        ]);

        var lines = Assert.Single(diff.Hunks).Lines;
        Assert.Equal(11, lines[1].OldNumber);
        Assert.Equal(12, lines[3].NewNumber);
        Assert.Equal(2, diff.Added);
        Assert.Equal("@@ -10,3 +10,4 @@", diff.Hunks[0].Header);
    }

    [Fact]
    public void Bare_unified_body_parses_without_headers()
    {
        var diff = TextDiff.FromUnified("a.txt", "+hi\n-bye\n");

        Assert.Equal(1, diff.Added);
        Assert.Equal(1, diff.Removed);
        Assert.All(diff.Hunks.SelectMany(h => h.Lines), l => Assert.Null(l.OldNumber ?? l.NewNumber));
    }

    [Fact]
    public void Unified_body_with_hunk_header_is_numbered()
    {
        var diff = TextDiff.FromUnified("a.txt", "--- a/a.txt\n+++ b/a.txt\n@@ -3,2 +3,2 @@\n ctx\n-old\n+new\n");

        var lines = Assert.Single(diff.Hunks).Lines;
        Assert.Equal(3, lines.Count);
        Assert.Equal(4, lines[1].OldNumber);
        Assert.Equal(4, lines[2].NewNumber);
    }
}
