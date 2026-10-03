namespace Codale.Git.Tests;

/// <summary>
/// Runs the real git executable against a real repository created for the test, so the
/// process plumbing and argument handling are covered, not just the parsers.
/// </summary>
public sealed class GitRepositoryIntegrationTests : IDisposable
{
    private readonly string _root = Directory.CreateTempSubdirectory("codale-git-").FullName;

    private static void Git(string workingDirectory, string arguments)
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

        foreach (var arg in arguments.Split(' ', StringSplitOptions.RemoveEmptyEntries))
        {
            startInfo.ArgumentList.Add(arg.Replace(' ', ' '));
        }

        using var process = System.Diagnostics.Process.Start(startInfo)!;
        process.WaitForExit();
    }

    [Fact]
    public async Task A_non_repository_is_reported_as_such()
    {
        var snapshot = await new GitRepository(_root).GetSnapshotAsync();

        Assert.False(snapshot.IsRepository);
    }

    [Fact]
    public async Task A_real_repository_reports_its_branch_changes_and_history()
    {
        Git(_root, "init -b main");
        Git(_root, "config user.email codale@example.test");
        Git(_root, "config user.name Codale\u00A0Test");

        await File.WriteAllTextAsync(Path.Combine(_root, "first.txt"), "hello");
        Git(_root, "add .");
        Git(_root, "commit -m Initial\u00A0commit");

        // One staged change, one unstaged change and one untracked file.
        await File.WriteAllTextAsync(Path.Combine(_root, "staged.txt"), "staged");
        Git(_root, "add staged.txt");
        await File.WriteAllTextAsync(Path.Combine(_root, "first.txt"), "changed");
        await File.WriteAllTextAsync(Path.Combine(_root, "untracked.txt"), "new");

        var snapshot = await new GitRepository(_root).GetSnapshotAsync();

        Assert.True(snapshot.IsRepository);
        Assert.Null(snapshot.Error);
        Assert.Equal("main", snapshot.Branch.Display);

        // No remote configured, so there is nothing to be ahead or behind of.
        Assert.Null(snapshot.Branch.Upstream);

        Assert.Contains(snapshot.Changes, c => c.Path == "staged.txt" && c.IsStaged);
        Assert.Contains(snapshot.Changes, c => c.Path == "first.txt" && !c.IsStaged);
        Assert.Contains(snapshot.Changes, c => c.Path == "untracked.txt" && c.Index == GitChangeKind.Untracked);

        var commit = Assert.Single(snapshot.Log);
        Assert.Equal("Initial commit", commit.Subject);
        Assert.Equal("Codale Test", commit.Author);
        Assert.Equal(7, commit.ShortSha.Length);
    }

    [Fact]
    public async Task Stage_commit_and_checkout_round_trip()
    {
        Git(_root, "init -b main");
        Git(_root, "config user.email codale@example.test");
        Git(_root, "config user.name Codale\u00A0Test");
        await File.WriteAllTextAsync(Path.Combine(_root, "readme.txt"), "v1");
        Git(_root, "add .");
        Git(_root, "commit -m Initial\u00A0commit");

        var repo = new GitRepository(_root);

        // Stage, then take it back out: the file is modified again, not staged.
        await File.WriteAllTextAsync(Path.Combine(_root, "readme.txt"), "v2");
        Assert.True((await repo.StageAsync("readme.txt")).Success);
        var stagedSnapshot = await repo.GetSnapshotAsync();
        Assert.Contains(stagedSnapshot.Changes, c => c.Path == "readme.txt" && c.IsStaged);

        Assert.True((await repo.UnstageAsync("readme.txt")).Success);
        var unstaged = await repo.GetSnapshotAsync();
        Assert.Contains(unstaged.Changes, c => c.Path == "readme.txt" && !c.IsStaged);

        // Commit from the panel: message in, working tree clean out.
        Assert.True((await repo.StageAllAsync()).Success);
        Assert.True((await repo.CommitAsync("Second commit")).Success);

        var after = await repo.GetSnapshotAsync();
        Assert.Empty(after.Changes);
        Assert.Equal(2, after.Log.Count);
        Assert.Equal("Second commit", after.Log[0].Subject);

        // The graph knows the lineage: the new commit's parent is the root.
        Assert.Equal(after.Log[1].Sha, Assert.Single(after.Log[0].Parents));
        Assert.Empty(after.Log[1].Parents);

        // A new branch and a switch back; the working tree follows HEAD.
        Assert.True((await repo.CreateBranchAsync("feature")).Success);
        Assert.Equal("feature", (await repo.GetSnapshotAsync()).Branch.Display);

        Assert.True((await repo.CheckoutAsync("main")).Success);
        Assert.Equal("main", (await repo.GetSnapshotAsync()).Branch.Display);
        Assert.Equal("v2", await File.ReadAllTextAsync(Path.Combine(_root, "readme.txt")));
    }

    [Fact]
    public async Task Commit_failure_reports_git_message_and_empty_message_is_rejected()
    {
        Git(_root, "init -b main");
        Git(_root, "config user.email codale@example.test");
        Git(_root, "config user.name Codale\u00A0Test");

        var repo = new GitRepository(_root);

        Assert.False((await repo.CommitAsync("")).Success);

        // Nothing staged and nothing to commit: git refuses and says why.
        var result = await repo.CommitAsync("nothing to commit");
        Assert.False(result.Success);
        Assert.False(string.IsNullOrWhiteSpace(result.Error));
    }

    [Fact]
    public async Task A_file_diff_covers_tracked_and_untracked_paths()
    {
        Git(_root, "init -b main");
        Git(_root, "config user.email codale@example.test");
        Git(_root, "config user.name Codale\u00A0Test");
        await File.WriteAllTextAsync(Path.Combine(_root, "readme.txt"), "v1\nmore\n");
        Git(_root, "add .");
        Git(_root, "commit -m Initial\u00A0commit");

        var repo = new GitRepository(_root);

        await File.WriteAllTextAsync(Path.Combine(_root, "readme.txt"), "v2\nmore\n");
        await File.WriteAllTextAsync(Path.Combine(_root, "fresh.txt"), "brand new");

        var tracked = await repo.GetFileDiffAsync("readme.txt");
        var diff = Assert.Single(tracked);
        Assert.Equal("readme.txt", diff.Path);
        Assert.Equal(1, diff.Added);
        Assert.Equal(1, diff.Removed);

        var untracked = await repo.GetFileDiffAsync("fresh.txt");
        var added = Assert.Single(untracked);
        Assert.True(added.IsNew);
        Assert.Equal(1, added.Added);
    }

    [Fact]
    public async Task A_commit_diff_shows_what_the_commit_changed()
    {
        Git(_root, "init -b main");
        Git(_root, "config user.email codale@example.test");
        Git(_root, "config user.name Codale\u00A0Test");
        await File.WriteAllTextAsync(Path.Combine(_root, "readme.txt"), "v1");
        Git(_root, "add .");
        Git(_root, "commit -m Root\u00A0commit");

        var root = await new GitRepository(_root).GetSnapshotAsync();
        var rootSha = root.Log[0].Sha;

        await File.WriteAllTextAsync(Path.Combine(_root, "next.txt"), "added in second");
        Git(_root, "add .");
        Git(_root, "commit -m Second\u00A0commit");

        var repo = new GitRepository(_root);
        var snapshot = await repo.GetSnapshotAsync();
        var second = snapshot.Log[0];

        // The second commit: one file added against its parent.
        var secondDiff = await repo.GetCommitDiffAsync(second.Sha);
        var file = Assert.Single(secondDiff);
        Assert.Equal("next.txt", file.Path);
        Assert.True(file.IsNew);

        // The root commit has no parents: diff-tree --root shows the full contents.
        var rootDiff = await repo.GetCommitDiffAsync(rootSha);
        Assert.Contains(rootDiff, f => f.Path == "readme.txt");
    }

    [Fact]
    public async Task Local_branches_are_listed_with_the_current_first()
    {
        Git(_root, "init -b main");
        Git(_root, "config user.email codale@example.test");
        Git(_root, "config user.name Codale\u00A0Test");
        await File.WriteAllTextAsync(Path.Combine(_root, "readme.txt"), "v1");
        Git(_root, "add .");
        Git(_root, "commit -m Initial\u00A0commit");
        Git(_root, "branch feature");

        var snapshot = await new GitRepository(_root).GetSnapshotAsync();

        Assert.Equal(2, snapshot.Branches.Count);
        Assert.Equal("main", snapshot.Branches[0].Name);
        Assert.True(snapshot.Branches[0].IsCurrent);
        Assert.Contains(snapshot.Branches, b => b.Name == "feature" && !b.IsCurrent);
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
