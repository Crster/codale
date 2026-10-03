namespace Codale.Git.Tests;

/// <summary>
/// Porcelain v2 is a contract git promises to tools, so it can be pinned with recorded
/// output instead of a real repository. The awkward cases - renames spanning two
/// records, paths with spaces, detached HEAD - are the point of these tests.
/// </summary>
public sealed class GitStatusParsingTests
{
    private static string Records(params string[] records) => string.Join('\0', records) + '\0';

    [Fact]
    public void Branch_name_and_tracking_are_read_from_the_header()
    {
        var (branch, _) = GitRepository.ParseStatus(Records(
            "# branch.oid 1234567890abcdef",
            "# branch.head main",
            "# branch.upstream origin/main",
            "# branch.ab +2 -3"));

        Assert.Equal("main", branch.Display);
        Assert.Equal("origin/main", branch.Upstream);
        Assert.Equal(2, branch.Ahead);
        Assert.Equal(3, branch.Behind);
        Assert.Equal("2 ahead, 3 behind", branch.TrackingSummary);
        Assert.False(branch.IsDetached);
    }

    [Fact]
    public void Collapsed_untracked_directories_expand_to_their_files()
    {
        var (_, changes) = GitRepository.ParseStatus(
            Records("? css/", "? index.html"),
            ["css/site.css", "css/theme/dark.css", "index.html"]);

        Assert.Equal(["css/site.css", "css/theme/dark.css", "index.html"], changes.Select(c => c.Path));
    }

    [Fact]
    public void A_branch_with_no_upstream_reports_no_tracking_summary()
    {
        var (branch, _) = GitRepository.ParseStatus(Records(
            "# branch.oid abc123",
            "# branch.head feature/thing"));

        Assert.Equal("feature/thing", branch.Display);
        Assert.Null(branch.Upstream);
        Assert.Null(branch.TrackingSummary);
    }

    [Fact]
    public void Detached_head_falls_back_to_the_commit_id()
    {
        var (branch, _) = GitRepository.ParseStatus(Records(
            "# branch.oid 1234567890abcdef1234567890abcdef12345678",
            "# branch.head (detached)"));

        Assert.True(branch.IsDetached);
        Assert.Equal("1234567", branch.Display);
    }

    [Fact]
    public void Ordinary_changes_separate_staged_from_unstaged()
    {
        var (_, changes) = GitRepository.ParseStatus(Records(
            "1 M. N... 100644 100644 100644 aaa bbb staged.cs",
            "1 .M N... 100644 100644 100644 ccc ddd unstaged.cs"));

        var staged = changes.Single(c => c.Path == "staged.cs");
        Assert.True(staged.IsStaged);
        Assert.Equal(GitChangeKind.Modified, staged.Index);
        Assert.Equal(GitChangeKind.Unmodified, staged.WorkTree);
        Assert.Equal("M.", staged.ShortCode);

        var unstaged = changes.Single(c => c.Path == "unstaged.cs");
        Assert.False(unstaged.IsStaged);
        Assert.Equal(GitChangeKind.Modified, unstaged.WorkTree);
    }

    [Fact]
    public void Paths_containing_spaces_survive_intact()
    {
        // -z means git does not quote these, so the trailing field must not be split.
        var (_, changes) = GitRepository.ParseStatus(Records(
            "1 .M N... 100644 100644 100644 aaa bbb src/My Folder/A File.cs"));

        var change = Assert.Single(changes);
        Assert.Equal("src/My Folder/A File.cs", change.Path);
        Assert.Equal("A File.cs", change.FileName);
    }

    [Fact]
    public void A_rename_consumes_the_following_record_as_its_original_path()
    {
        // This is the parsing trap in porcelain v2: type 2 entries span two records.
        var (_, changes) = GitRepository.ParseStatus(Records(
            "2 R. N... 100644 100644 100644 aaa bbb R100 new/name.cs",
            "old/name.cs",
            "1 .M N... 100644 100644 100644 ccc ddd other.cs"));

        Assert.Equal(2, changes.Count);

        var renamed = changes.Single(c => c.Path == "new/name.cs");
        Assert.Equal("old/name.cs", renamed.OriginalPath);
        Assert.Equal(GitChangeKind.Renamed, renamed.Index);

        // The original-path record must not be mistaken for another change.
        Assert.Contains(changes, c => c.Path == "other.cs");
        Assert.DoesNotContain(changes, c => c.Path == "old/name.cs");
    }

    [Fact]
    public void Untracked_files_are_reported()
    {
        var (_, changes) = GitRepository.ParseStatus(Records("? newfile.txt"));

        var change = Assert.Single(changes);
        Assert.Equal("newfile.txt", change.Path);
        Assert.Equal(GitChangeKind.Untracked, change.Index);
        Assert.False(change.IsStaged);
        Assert.Equal("??", change.ShortCode);
    }

    [Fact]
    public void Ignored_records_are_kept_out_of_the_change_list()
    {
        // --ignored=traditional appends "! " records; the change list stays about real
        // work, and ParseIgnored reads them out separately.
        var (_, changes) = GitRepository.ParseStatus(Records(
            "1 .M N... 100644 100644 100644 aaa bbb tracked.cs",
            "! build/",
            "! debug.log"));

        var change = Assert.Single(changes);
        Assert.Equal("tracked.cs", change.Path);
    }

    [Fact]
    public void Ignored_records_list_their_paths()
    {
        var ignored = GitRepository.ParseIgnored(Records(
            "! build/",
            "! debug.log",
            "? untracked.txt"));

        Assert.Equal(2, ignored.Count);
        Assert.Contains("build/", ignored);
        Assert.Contains("debug.log", ignored);
    }

    [Fact]
    public void Status_without_ignored_entries_yields_an_empty_ignored_list()
    {
        Assert.Empty(GitRepository.ParseIgnored(Records("? untracked.txt")));
    }

    [Fact]
    public void Conflicts_are_flagged_and_sorted_first()
    {
        var (_, changes) = GitRepository.ParseStatus(Records(
            "? zzz-untracked.txt",
            "1 M. N... 100644 100644 100644 aaa bbb staged.cs",
            "u UU N... 100644 100644 100644 100644 aaa bbb ccc conflicted.cs"));

        Assert.True(changes[0].IsConflicted);
        Assert.Equal("conflicted.cs", changes[0].Path);
        Assert.Equal("staged.cs", changes[1].Path);
        Assert.Equal("zzz-untracked.txt", changes[2].Path);
    }

    [Fact]
    public void Nested_files_carry_their_directory_hint()
    {
        var (_, changes) = GitRepository.ParseStatus(Records(
            "1 .M N... 100644 100644 100644 aaa bbb src/App.xaml.cs",
            "1 .M N... 100644 100644 100644 ccc ddd tests/App.xaml.cs",
            "1 .M N... 100644 100644 100644 eee fff unique.cs"));

        var srcApp = changes.Single(c => c.Path == "src/App.xaml.cs");
        Assert.Equal("src/", srcApp.PathHint);

        var testApp = changes.Single(c => c.Path == "tests/App.xaml.cs");
        Assert.Equal("tests/", testApp.PathHint);

        // A root-level file has no directory to show.
        var unique = changes.Single(c => c.Path == "unique.cs");
        Assert.Null(unique.PathHint);
    }

    [Fact]
    public void An_untracked_directory_displays_its_path()
    {
        var (_, changes) = GitRepository.ParseStatus(Records("? src/"));

        var entry = Assert.Single(changes);
        Assert.Equal("src/", entry.DisplayName);
        Assert.Null(entry.PathHint);
    }

    [Fact]
    public void Empty_status_means_a_clean_tree()
    {
        var (_, changes) = GitRepository.ParseStatus(Records(
            "# branch.head main"));

        Assert.Empty(changes);
    }

    // git's own delimiters for the log format: fields by unit separator, records by
    // record separator. Written as interpolated constants because \u001f in a literal
    // silently swallows following hex-looking characters (\u001fabc parses as \u1fab).
    private static readonly string Us = "\u001f";
    private static readonly string Rs = "\u001e";

    [Fact]
    public void Log_records_are_split_on_the_record_separator()
    {
        var output =
            $"abc123{Us}{Us}Fix the thing{Us}Ada{Us}2026-09-17T10:00:00+00:00{Us}{Rs}" +
            $"def456{Us}abc123{Us}Add a feature{Us}Grace{Us}2026-09-16T09:00:00+00:00{Us}main{Rs}";

        var commits = GitRepository.ParseLog(output);

        Assert.Equal(2, commits.Count);

        Assert.Equal("abc123", commits[0].Sha);
        Assert.Empty(commits[0].Parents);
        Assert.Equal("Fix the thing", commits[0].Subject);
        Assert.Equal("Ada", commits[0].Author);
        Assert.Equal(2026, commits[0].Date.Year);
        Assert.Equal("", commits[0].Refs);

        Assert.Equal(["abc123"], commits[1].Parents);
        Assert.Equal("main", commits[1].Refs);
    }

    [Fact]
    public void A_commit_subject_containing_a_newline_does_not_split_the_record()
    {
        // Why RS/US are used as delimiters rather than newlines.
        var output = $"abc123{Us}{Us}Subject with\nembedded newline{Us}Ada{Us}2026-09-17T10:00:00+00:00{Us}{Rs}";

        var commit = Assert.Single(GitRepository.ParseLog(output));
        Assert.Contains("embedded newline", commit.Subject);
    }

    [Fact]
    public void Branch_records_carry_their_upstream_and_track()
    {
        var branches = GitRepository.ParseBranches(
            $"*\tmain\torigin/main\t[ahead 2, behind 1]\n" +
            $" \tfeature\t\t\n");

        Assert.Equal(2, branches.Count);

        // The current branch sorts first.
        Assert.Equal("main", branches[0].Name);
        Assert.True(branches[0].IsCurrent);
        Assert.Equal("origin/main", branches[0].Upstream);
        Assert.Equal("ahead 2, behind 1", branches[0].TrackingSummary);

        Assert.Equal("feature", branches[1].Name);
        Assert.False(branches[1].IsCurrent);
        Assert.Null(branches[1].Upstream);
        Assert.Null(branches[1].TrackingSummary);
    }

    [Fact]
    public void Ref_labels_strip_the_head_prefix()
    {
        var output = $"abc123{Us}{Us}Subject{Us}Ada{Us}2026-09-17T10:00:00+00:00{Us}HEAD -> main, origin/main{Rs}";

        var commit = Assert.Single(GitRepository.ParseLog(output));

        Assert.Equal("main origin/main", commit.RefDisplay);
        Assert.Contains("HEAD", commit.Refs);
    }
}
