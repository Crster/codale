namespace Codale.Git.Tests;

/// <summary>
/// The file tree's view of a status listing: entries find their own node, folders
/// inherit the most severe change beneath them, and ignored paths mute their subtree.
/// </summary>
public sealed class GitStatusMapTests
{
    private static GitFileStatus Change(
        string path,
        GitChangeKind index = GitChangeKind.Unmodified,
        GitChangeKind workTree = GitChangeKind.Unmodified) => new()
    {
        Path = path,
        Index = index,
        WorkTree = workTree,
    };

    [Fact]
    public void An_entrys_own_change_is_reported()
    {
        var map = GitStatusMap.Build([Change("src/app.cs", workTree: GitChangeKind.Modified)], []);

        var (status, ignored) = map.Classify("src/app.cs");

        Assert.Equal(GitChangeKind.Modified, status);
        Assert.False(ignored);
    }

    [Fact]
    public void A_clean_path_reports_nothing()
    {
        var map = GitStatusMap.Build([Change("src/app.cs", workTree: GitChangeKind.Modified)], []);

        var (status, ignored) = map.Classify("readme.md");

        Assert.Null(status);
        Assert.False(ignored);
    }

    [Fact]
    public void A_folder_shows_the_most_severe_change_beneath_it()
    {
        var map = GitStatusMap.Build(
        [
            Change("src/app.cs", workTree: GitChangeKind.Modified),
            Change("src/gone.cs", workTree: GitChangeKind.Deleted),
        ], []);

        Assert.Equal(GitChangeKind.Deleted, map.Classify("src").Status);
    }

    [Fact]
    public void A_folder_above_a_change_does_not_colour_its_siblings()
    {
        var map = GitStatusMap.Build([Change("src/app.cs", workTree: GitChangeKind.Modified)], []);

        Assert.Null(map.Classify("docs").Status);
        Assert.Null(map.Classify("src.rs").Status);
    }

    [Fact]
    public void A_collapsed_untracked_directory_colours_its_contents()
    {
        // --untracked-files=normal reports a wholly new folder as "newdir/" without
        // listing anything inside it, but the tree will still walk its children.
        var map = GitStatusMap.Build([Change("newdir/", GitChangeKind.Untracked, GitChangeKind.Untracked)], []);

        Assert.Equal(GitChangeKind.Untracked, map.Classify("newdir").Status);
        Assert.Equal(GitChangeKind.Untracked, map.Classify("newdir/deep/file.cs").Status);
    }

    [Fact]
    public void An_ignored_file_is_flagged()
    {
        var map = GitStatusMap.Build([], ["debug.log"]);

        Assert.True(map.Classify("debug.log").Ignored);
        Assert.False(map.Classify("debug.log.old").Ignored);
    }

    [Fact]
    public void An_ignored_directory_flags_everything_beneath_it()
    {
        var map = GitStatusMap.Build([], ["build/"]);

        Assert.True(map.Classify("build").Ignored);
        Assert.True(map.Classify("build/out.bin").Ignored);
        Assert.True(map.Classify("build/sub/deep.obj").Ignored);
        Assert.False(map.Classify("buildnotes.txt").Ignored);
    }

    [Fact]
    public void A_tracked_change_inside_an_ignored_directory_is_not_muted()
    {
        // gitignore only hides untracked paths, so a tracked file that sits in a
        // directory matching an ignore rule still shows its change, not the mute.
        var map = GitStatusMap.Build(
            [Change("libs/kept.cs", workTree: GitChangeKind.Modified)],
            ["libs/"]);

        var (status, ignored) = map.Classify("libs/kept.cs");

        Assert.Equal(GitChangeKind.Modified, status);
        Assert.False(ignored);
        Assert.False(map.Classify("libs").Ignored);
    }

    [Fact]
    public void A_change_beats_an_ignore_rule_for_the_same_path()
    {
        var map = GitStatusMap.Build(
            [Change("edge.cs", index: GitChangeKind.Added)],
            ["edge.cs"]);

        var (status, ignored) = map.Classify("edge.cs");

        Assert.Equal(GitChangeKind.Added, status);
        Assert.False(ignored);
    }

    [Fact]
    public void Paths_are_matched_without_regard_to_case()
    {
        var map = GitStatusMap.Build([Change("src/app.cs", workTree: GitChangeKind.Modified)], ["debug.log"]);

        Assert.Equal(GitChangeKind.Modified, map.Classify("SRC/App.cs").Status);
        Assert.True(map.Classify("Debug.LOG").Ignored);
    }
}
