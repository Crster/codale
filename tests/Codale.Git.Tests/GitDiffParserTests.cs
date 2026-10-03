namespace Codale.Git.Tests;

public sealed class GitDiffParserTests
{
    private const string SimpleDiff = """
        diff --git a/src/Thing.cs b/src/Thing.cs
        index 1111111..2222222 100644
        --- a/src/Thing.cs
        +++ b/src/Thing.cs
        @@ -10,6 +10,7 @@ public class Thing
             public void Go()
             {
                 Prepare();
        +        Retry();
                 Run();
             }
         }
        """;

    [Fact]
    public void A_single_file_change_yields_hunks_with_line_numbers()
    {
        var file = Assert.Single(GitDiffParser.Parse(SimpleDiff));

        Assert.Equal("src/Thing.cs", file.Path);
        Assert.Equal("Thing.cs", file.FileName);
        Assert.False(file.IsNew);
        Assert.False(file.IsBinary);
        Assert.Equal(1, file.Added);
        Assert.Equal(0, file.Removed);

        var hunk = Assert.Single(file.Hunks);
        Assert.StartsWith("@@ -10,6 +10,7 @@", hunk.Header);

        var added = hunk.Lines.Single(l => l.Kind == DiffLineKind.Added);
        Assert.Equal("        Retry();", added.Text);

        // The added line is the fourth line of the new file's hunk, starting at 10.
        Assert.Equal(13, added.NewNumber);
        Assert.Null(added.OldNumber);

        // Context lines carry both sides' numbering.
        var firstContext = hunk.Lines.First(l => l.Kind == DiffLineKind.Context);
        Assert.Equal(10, firstContext.OldNumber);
        Assert.Equal(10, firstContext.NewNumber);
    }

    [Fact]
    public void Added_and_removed_lines_are_counted_separately()
    {
        var diff = """
            diff --git a/a.txt b/a.txt
            --- a/a.txt
            +++ b/a.txt
            @@ -1,3 +1,3 @@
             keep
            -old one
            -old two
            +new one
            """;

        var file = Assert.Single(GitDiffParser.Parse(diff));

        Assert.Equal(1, file.Added);
        Assert.Equal(2, file.Removed);
        Assert.Equal("+1 \u22122", file.Stat);
    }

    [Fact]
    public void Several_files_in_one_diff_are_separated()
    {
        var diff = SimpleDiff + "\n" + """
            diff --git a/b.txt b/b.txt
            new file mode 100644
            --- /dev/null
            +++ b/b.txt
            @@ -0,0 +1,2 @@
            +hello
            +world
            """;

        var files = GitDiffParser.Parse(diff);

        Assert.Equal(2, files.Count);
        Assert.Equal("src/Thing.cs", files[0].Path);

        Assert.Equal("b.txt", files[1].Path);
        Assert.True(files[1].IsNew);
        Assert.Equal(2, files[1].Added);
    }

    [Fact]
    public void A_deleted_file_is_flagged()
    {
        var diff = """
            diff --git a/gone.txt b/gone.txt
            deleted file mode 100644
            --- a/gone.txt
            +++ /dev/null
            @@ -1,1 +0,0 @@
            -was here
            """;

        var file = Assert.Single(GitDiffParser.Parse(diff));

        Assert.True(file.IsDeleted);
        Assert.Equal(1, file.Removed);
    }

    [Fact]
    public void A_binary_file_is_flagged_and_has_no_hunks()
    {
        var diff = """
            diff --git a/logo.png b/logo.png
            index 1111111..2222222 100644
            Binary files a/logo.png and b/logo.png differ
            """;

        var file = Assert.Single(GitDiffParser.Parse(diff));

        Assert.True(file.IsBinary);
        Assert.Empty(file.Hunks);
        Assert.Equal("binary", file.Stat);
    }

    [Fact]
    public void A_rename_records_both_paths()
    {
        var diff = """
            diff --git a/old/name.cs b/new/name.cs
            similarity index 95%
            rename from old/name.cs
            rename to new/name.cs
            --- a/old/name.cs
            +++ b/new/name.cs
            @@ -1,2 +1,2 @@
             same
            -before
            +after
            """;

        var file = Assert.Single(GitDiffParser.Parse(diff));

        Assert.Equal("new/name.cs", file.Path);
        Assert.Equal("old/name.cs", file.OldPath);
    }

    [Fact]
    public void Paths_containing_spaces_are_parsed_using_the_a_and_b_prefixes()
    {
        var diff = """
            diff --git a/My Folder/A File.cs b/My Folder/A File.cs
            --- a/My Folder/A File.cs
            +++ b/My Folder/A File.cs
            @@ -1,1 +1,1 @@
            -x
            +y
            """;

        var file = Assert.Single(GitDiffParser.Parse(diff));

        Assert.Equal("My Folder/A File.cs", file.Path);
    }

    [Fact]
    public void The_no_newline_marker_is_not_treated_as_content()
    {
        var diff = """
            diff --git a/a.txt b/a.txt
            --- a/a.txt
            +++ b/a.txt
            @@ -1,1 +1,1 @@
            -old
            \ No newline at end of file
            +new
            """;

        var file = Assert.Single(GitDiffParser.Parse(diff));

        Assert.Equal(1, file.Added);
        Assert.Equal(1, file.Removed);
        Assert.DoesNotContain(file.Hunks[0].Lines, l => l.Text.Contains("No newline"));
    }

    [Fact]
    public void Multiple_hunks_in_one_file_are_kept_separate()
    {
        var diff = """
            diff --git a/a.txt b/a.txt
            --- a/a.txt
            +++ b/a.txt
            @@ -1,2 +1,3 @@
             one
            +inserted
            @@ -10,2 +11,3 @@
             ten
            +also inserted
            """;

        var file = Assert.Single(GitDiffParser.Parse(diff));

        Assert.Equal(2, file.Hunks.Count);
        Assert.Equal(11, file.Hunks[1].Lines.First(l => l.Kind == DiffLineKind.Context).NewNumber);
    }

    [Fact]
    public void Empty_or_whitespace_input_yields_nothing()
    {
        Assert.Empty(GitDiffParser.Parse(""));
        Assert.Empty(GitDiffParser.Parse("   \n  "));
    }

    [Fact]
    public void Worktree_list_porcelain_is_parsed()
    {
        var porcelain = """
            worktree X:/repo
            HEAD abc123
            branch refs/heads/main

            worktree X:/repo/.codale/worktrees/1a2b3c4d
            HEAD def456
            branch refs/heads/codale/1a2b3c4d

            """;

        var worktrees = GitWorktrees.ParseList(porcelain);

        Assert.Equal(2, worktrees.Count);
        Assert.True(worktrees[0].IsMain);
        Assert.Equal("main", worktrees[0].Branch);

        Assert.False(worktrees[1].IsMain);
        Assert.Equal("codale/1a2b3c4d", worktrees[1].Branch);
        Assert.EndsWith("1a2b3c4d", worktrees[1].Path);
    }
}
