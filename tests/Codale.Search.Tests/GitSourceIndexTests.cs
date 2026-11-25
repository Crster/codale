using System.Diagnostics;
using Codale.Search.Engine;

namespace Codale.Search.Tests;

public sealed class GitSourceIndexTests
{
    [Fact]
    public async Task IndexesTrackedFilesAndTypingSuggestionsDoNotStartARefresh()
    {
        var root = Directory.CreateTempSubdirectory("codale-git-index-").FullName;
        try
        {
            RunGit(root, "init");
            File.WriteAllText(Path.Combine(root, "tracked.cs"), "class TrackedName {} ");
            File.WriteAllText(Path.Combine(root, "untracked.cs"), "class UntrackedName {} ");
            RunGit(root, "add", "tracked.cs");
            using var index = new SourceIndex(root, new() { Watch = false, MaxAge = TimeSpan.FromHours(1) });
            var snapshot = await index.GetSnapshotAsync();
            Assert.NotNull(snapshot.Find("tracked.cs"));
            Assert.Null(snapshot.Find("untracked.cs"));
            index.Invalidate();
            _ = index.Suggest("Tracked");
            Assert.False(index.IsScanning);
            Assert.False(index.IsUpToDate);
        }
        finally
        {
            foreach (var file in Directory.EnumerateFiles(root, "*", SearchOption.AllDirectories))
                File.SetAttributes(file, FileAttributes.Normal);
            Directory.Delete(root, true);
        }
    }

    private static void RunGit(string root, params string[] args)
    {
        var start = new ProcessStartInfo("git") { WorkingDirectory = root, UseShellExecute = false, CreateNoWindow = true };
        foreach (var arg in args) start.ArgumentList.Add(arg);
        using var process = Process.Start(start)!;
        process.WaitForExit();
        Assert.Equal(0, process.ExitCode);
    }
}
