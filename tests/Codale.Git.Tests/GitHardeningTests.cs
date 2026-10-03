namespace Codale.Git.Tests;

/// <summary>
/// Real-git coverage for argument handling, boundaries and caps: paths with spaces,
/// refs that look like options, discards that must stay inside the repository.
/// </summary>
public sealed class GitHardeningTests : IDisposable
{
    private readonly string _root = Directory.CreateTempSubdirectory("codale-git-").FullName;

    private static void Git(string workingDirectory, params string[] arguments)
    {
        var startInfo = new System.Diagnostics.ProcessStartInfo
        {
            FileName = "git",
            WorkingDirectory = workingDirectory,
            UseShellExecute = false,
            CreateNoWindow = true,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
        };

        foreach (var arg in arguments)
        {
            startInfo.ArgumentList.Add(arg);
        }

        using var process = System.Diagnostics.Process.Start(startInfo)!;
        process.StandardOutput.ReadToEnd();
        process.StandardError.ReadToEnd();
        process.WaitForExit();
    }

    private static void InitRepo(string dir)
    {
        Directory.CreateDirectory(dir);
        Git(dir, "init", "-b", "main");
        Git(dir, "config", "user.email", "codale@example.test");
        Git(dir, "config", "user.name", "Codale Test");
    }

    private static void CommitAll(string dir)
    {
        Git(dir, "add", ".");
        Git(dir, "commit", "-m", "Initial commit");
    }

    [Fact]
    public async Task Paths_with_spaces_diff_correctly()
    {
        var repoDir = Path.Combine(_root, "my repo");
        InitRepo(repoDir);
        Directory.CreateDirectory(Path.Combine(repoDir, "some dir"));
        var file = Path.Combine(repoDir, "some dir", "a b.txt");
        await File.WriteAllTextAsync(file, "v1\n");
        CommitAll(repoDir);

        // The user's own diff.noprefix must not change what the parser sees.
        Git(repoDir, "config", "diff.noprefix", "true");
        await File.WriteAllTextAsync(file, "v2\n");

        var repo = new GitRepository(repoDir);

        var diff = Assert.Single(await repo.GetFileDiffAsync("some dir/a b.txt"));
        Assert.Equal("some dir/a b.txt", diff.Path);
        Assert.Equal(1, diff.Added);
        Assert.Equal(1, diff.Removed);

        var all = Assert.Single(await repo.GetWorkingTreeDiffAsync());
        Assert.Equal("some dir/a b.txt", all.Path);
    }

    [Fact]
    public async Task Non_ascii_paths_are_not_quoted_in_diffs()
    {
        InitRepo(_root);
        var file = Path.Combine(_root, "café.txt");
        await File.WriteAllTextAsync(file, "v1\n");
        CommitAll(_root);
        await File.WriteAllTextAsync(file, "v2\n");

        var diff = Assert.Single(await new GitRepository(_root).GetFileDiffAsync("café.txt"));

        Assert.Equal("café.txt", diff.Path);
    }

    [Fact]
    public async Task A_ref_that_looks_like_an_option_is_refused()
    {
        InitRepo(_root);
        await File.WriteAllTextAsync(Path.Combine(_root, "a.txt"), "v1");
        CommitAll(_root);

        var repo = new GitRepository(_root);
        var output = Path.Combine(_root, "pwned.txt");

        Assert.Empty(await repo.GetCommitDiffAsync("--output=" + output));
        Assert.False((await repo.CheckoutAsync("--orphan")).Success);
        Assert.False((await repo.CreateBranchAsync("-x")).Success);
        Assert.False((await repo.PushAsync("--all", hasUpstream: false)).Success);
        Assert.False(File.Exists(output));
    }

    [Fact]
    public async Task Push_sets_the_upstream_on_the_first_push()
    {
        var remote = Path.Combine(_root, "remote.git");
        Directory.CreateDirectory(remote);
        Git(remote, "init", "--bare", "-b", "main");

        var work = Path.Combine(_root, "work");
        InitRepo(work);
        await File.WriteAllTextAsync(Path.Combine(work, "a.txt"), "v1");
        CommitAll(work);
        Git(work, "remote", "add", "origin", remote);

        var repo = new GitRepository(work);
        var result = await repo.PushAsync("main", hasUpstream: false);

        Assert.True(result.Success, result.Error);
        Assert.Equal("origin/main", (await repo.GetSnapshotAsync()).Branch.Upstream);
    }

    [Fact]
    public async Task Discard_of_a_directory_stays_inside_the_repository_and_off_tracked_files()
    {
        InitRepo(_root);
        Directory.CreateDirectory(Path.Combine(_root, "tracked"));
        await File.WriteAllTextAsync(Path.Combine(_root, "tracked", "keep.txt"), "keep");
        CommitAll(_root);

        Directory.CreateDirectory(Path.Combine(_root, "fresh"));
        await File.WriteAllTextAsync(Path.Combine(_root, "fresh", "new.txt"), "new");

        var outside = Directory.CreateTempSubdirectory("codale-outside-").FullName;
        await File.WriteAllTextAsync(Path.Combine(outside, "precious.txt"), "precious");

        try
        {
            var repo = new GitRepository(_root);

            // A rooted path would replace the working directory in Path.Combine.
            Assert.False((await repo.DiscardAsync(outside + Path.DirectorySeparatorChar)).Success);
            Assert.False((await repo.DiscardAsync("../" + Path.GetFileName(outside) + "/")).Success);
            Assert.True(File.Exists(Path.Combine(outside, "precious.txt")));

            // A folder with tracked files in it is not "untracked"; nor is the root itself.
            Assert.False((await repo.DiscardAsync("tracked/")).Success);
            Assert.False((await repo.DiscardAsync("./")).Success);
            Assert.True(File.Exists(Path.Combine(_root, "tracked", "keep.txt")));

            Assert.True((await repo.DiscardAsync("fresh/")).Success);
            Assert.False(Directory.Exists(Path.Combine(_root, "fresh")));
        }
        finally
        {
            Directory.Delete(outside, recursive: true);
        }
    }

    [Fact]
    public async Task The_working_tree_diff_caps_how_many_untracked_files_are_read()
    {
        InitRepo(_root);
        await File.WriteAllTextAsync(Path.Combine(_root, "a.txt"), "v1");
        CommitAll(_root);

        for (var i = 0; i < 60; i++)
        {
            await File.WriteAllTextAsync(Path.Combine(_root, $"new{i:D2}.txt"), "line\n");
        }

        var diffs = await new GitRepository(_root).GetWorkingTreeDiffAsync();

        // Every file is still listed; only the first 50 carry content.
        Assert.Equal(60, diffs.Count);
        Assert.Equal(50, diffs.Count(d => d.Hunks.Count > 0));
        Assert.All(diffs.Where(d => d.Hunks.Count == 0), d => Assert.True(d.IsNew && d.IsBinary));
    }

    [Fact]
    public async Task Git_output_past_the_cap_is_cut_at_a_whole_record_and_flagged()
    {
        InitRepo(_root);
        for (var i = 0; i < 20; i++)
        {
            await File.WriteAllTextAsync(Path.Combine(_root, $"file{i:D2}.txt"), "x");
        }

        Git(_root, "add", ".");

        var result = await GitProcess.RunAsync(
            _root, ["ls-files", "-z"], readOnly: true, GitProcess.ReadTimeout, CancellationToken.None, maxOutputChars: 100);

        Assert.True(result.Success);
        Assert.True(result.Truncated);
        Assert.True(result.StandardOutput.Length <= 100);
        Assert.EndsWith("\0", result.StandardOutput);
    }

    [Fact]
    public async Task Worktrees_report_git_errors_and_remove_takes_their_branch_along()
    {
        var repoDir = Path.Combine(_root, "my repo");
        InitRepo(repoDir);
        await File.WriteAllTextAsync(Path.Combine(repoDir, "a.txt"), "v1");
        CommitAll(repoDir);

        var worktrees = new GitWorktrees(repoDir);

        var bad = await worktrees.TryCreateAsync("session1", "--detach");
        Assert.Null(bad.Path);
        Assert.False(string.IsNullOrWhiteSpace(bad.Error));

        var missing = await worktrees.TryCreateAsync("session2", "no-such-ref");
        Assert.Null(missing.Path);
        Assert.False(string.IsNullOrWhiteSpace(missing.Error));

        var path = await worktrees.CreateAsync("abcd1234-0000");
        Assert.NotNull(path);
        Assert.True(Directory.Exists(path));

        var listed = await worktrees.ListAsync();
        Assert.Contains(listed, w => w.Branch == "codale/abcd1234");

        var removed = await worktrees.RemoveAsync(path!);
        Assert.True(removed.Success, removed.Error);
        Assert.False(Directory.Exists(path));

        var branches = (await new GitRepository(repoDir).GetSnapshotAsync()).Branches;
        Assert.DoesNotContain(branches, b => b.Name.StartsWith("codale/", StringComparison.Ordinal));

        var again = await worktrees.RemoveAsync(path!);
        Assert.False(again.Success);
        Assert.False(string.IsNullOrWhiteSpace(again.Error));
    }

    public void Dispose()
    {
        try
        {
            // git marks objects read-only, which blocks a plain recursive delete.
            foreach (var file in Directory.EnumerateFiles(_root, "*", SearchOption.AllDirectories))
            {
                File.SetAttributes(file, FileAttributes.Normal);
            }

            Directory.Delete(_root, recursive: true);
        }
        catch (IOException)
        {
        }
        catch (UnauthorizedAccessException)
        {
        }
    }
}

public sealed class DiffTextTests
{
    // Written as char codes: a literal U+2028 in source would itself end a line.
    private static readonly string Ls = ((char)0x2028).ToString();
    private static readonly string Ps = ((char)0x2029).ToString();

    [Fact]
    public void Unicode_line_separators_inside_a_line_do_not_split_it()
    {
        var diff = "diff --git a/f.txt b/f.txt\n--- a/f.txt\n+++ b/f.txt\n@@ -1 +1 @@\n-old" + Ls + "line\u0085x\u000Cy\n+new" + Ps + "line\r\n";

        var file = Assert.Single(GitDiffParser.Parse(diff));
        var lines = file.Hunks.Single().Lines;

        Assert.Equal(2, lines.Count);
        Assert.Equal("old" + Ls + "line\u0085x\u000Cy", lines[0].Text);
        Assert.Equal("new" + Ps + "line", lines[1].Text);
    }

    [Fact]
    public void Splitting_text_for_a_diff_breaks_on_line_feeds_only()
    {
        Assert.Equal(["a" + Ls + "b", "c"], TextDiff.SplitLines("a" + Ls + "b\r\nc\n"));
    }

    [Fact]
    public void Diffs_with_equal_content_are_equal_whether_or_not_their_stats_were_read()
    {
        var hunks = new[]
        {
            new DiffHunk { Header = "@@", Lines = [new DiffLine { Kind = DiffLineKind.Added, Text = "x" }] },
        };

        var a = new FileDiff { Path = "f", Hunks = hunks };
        var b = new FileDiff { Path = "f", Hunks = hunks };

        _ = a.Added;

        Assert.Equal(a, b);
        Assert.Equal(1, b.Added);
    }
}
