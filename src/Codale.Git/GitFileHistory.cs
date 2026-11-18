namespace Codale.Git;

public static class GitFileHistory
{
    public sealed record Commit(string Id, string Subject, int FilesChanged);

    public static async Task<IReadOnlyList<Commit>> ReadAsync(string worktree, string path, CancellationToken ct)
    {
        var log = await GitProcess.RunAsync(worktree,
            ["--literal-pathspecs", "log", "-8", "--follow", "--format=%H%x00%s", "--", path],
            true, TimeSpan.FromSeconds(4), ct, maxOutputChars: 16_384).ConfigureAwait(false);
        if (!log.Success) return [];
        var commits = log.StandardOutput.Split('\n', StringSplitOptions.RemoveEmptyEntries)
            .Select(line => line.TrimEnd('\r').Split('\0', 2))
            .Where(parts => parts.Length == 2 && parts[0].Length is 40 or 64 && parts[0].All(Uri.IsHexDigit))
            .ToArray();
        if (commits.Length == 0) return [];

        var stat = await GitProcess.RunAsync(worktree,
            ["-c", "diff.renames=false", "show", "--format=%x00%H", "--name-only", .. commits.Select(c => c[0]), "--"],
            true, TimeSpan.FromSeconds(4), ct, maxOutputChars: 128_000).ConfigureAwait(false);
        var counts = new Dictionary<string, int>(StringComparer.Ordinal);
        if (stat.Success && !stat.Truncated)
            foreach (var block in stat.StandardOutput.Split('\0', StringSplitOptions.RemoveEmptyEntries))
            {
                var lines = block.Split('\n', StringSplitOptions.TrimEntries | StringSplitOptions.RemoveEmptyEntries);
                if (lines.Length > 0) counts[lines[0]] = lines.Length - 1;
            }
        return [.. commits.Select(c => new Commit(c[0], c[1], counts.GetValueOrDefault(c[0], int.MaxValue)))];
    }
}
