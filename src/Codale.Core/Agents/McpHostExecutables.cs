namespace Codale.Core.Agents;

/// <summary>
/// Where the Codale MCP host executables are, and the copy of one that another process can run. Packaged installs
/// live under WindowsApps, which a child process cannot start from, so such a host is copied once to a staging folder
/// (keyed by a stamp of its files) and the copy is used. Any failure to stage falls back to the original path.
/// </summary>
public sealed class McpHostExecutables(string baseDirectory, string stagingRoot)
{
    private const string PackagedMarker = @"\WindowsApps\";

    private readonly Lock _gate = new();
    private readonly Dictionary<string, string> _runnable = [with(StringComparer.Ordinal)];

    /// <summary>The executable as installed: <c>base\name\name.exe</c>.</summary>
    public string SourcePath(string name) => Path.Combine(baseDirectory, name, name + ".exe");

    /// <summary>Whether the host is installed.</summary>
    public bool Exists(string name) => File.Exists(SourcePath(name));

    /// <summary>The path to start the host from: the installed one, or its staged copy when the install is packaged.</summary>
    public string Runnable(string name)
    {
        var source = SourcePath(name);
        if (!source.Contains(PackagedMarker, StringComparison.OrdinalIgnoreCase))
        {
            return source;
        }

        // Every server and hook call asks; the stamp scan and staging check run once per name.
        lock (_gate)
        {
            if (_runnable.TryGetValue(name, out var known) && File.Exists(known))
            {
                return known;
            }

            // A failed staging falls back to the source and is retried next time.
            var staged = Stage(name, source);
            if (!string.Equals(staged, source, StringComparison.OrdinalIgnoreCase))
            {
                _runnable[name] = staged;
            }

            return staged;
        }
    }

    private string Stage(string name, string source)
    {
        try
        {
            // The exe is a small apphost that rarely changes between builds; the code
            // lives in the dlls beside it (and in subfolders such as the browser's driver),
            // so the stamp covers every file under the folder.
            var files = new DirectoryInfo(Path.GetDirectoryName(source)!).GetFiles("*", SearchOption.AllDirectories);
            var stamp = $"{files.Length:x}-{files.Sum(f => f.Length):x}-{files.Max(f => f.LastWriteTimeUtc.Ticks):x}";
            var target = Path.Combine(stagingRoot, name, stamp);
            var staged = Path.Combine(target, name + ".exe");
            if (File.Exists(staged))
            {
                return staged;
            }

            var partial = target + ".partial";
            if (Directory.Exists(partial))
            {
                Directory.Delete(partial, recursive: true);
            }

            CopyDirectory(Path.GetDirectoryName(source)!, partial);
            Directory.Move(partial, target);

            return staged;
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            return source;
        }
    }

    private static void CopyDirectory(string from, string to)
    {
        Directory.CreateDirectory(to);
        foreach (var file in Directory.GetFiles(from))
        {
            File.Copy(file, Path.Combine(to, Path.GetFileName(file)), overwrite: true);
        }

        foreach (var dir in Directory.GetDirectories(from))
        {
            CopyDirectory(dir, Path.Combine(to, Path.GetFileName(dir)));
        }
    }
}