using System.Diagnostics;
using System.Runtime.CompilerServices;
using System.Text;
using System.Text.Json;

using Codale.Core.Processes;
using Codale.Core.Text;

namespace Codale.Search;

public sealed record SearchQuery
{
    public required string Text { get; init; }
    public bool IsRegex { get; init; }
    public bool MatchCase { get; init; }

    /// <summary>Optional include glob, e.g. <c>*.cs</c>.</summary>
    public string? Glob { get; init; }

    /// <summary>Globs to leave out, e.g. <c>*.min.js</c>, on top of .gitignore.</summary>
    public IReadOnlyList<string> ExcludeGlobs { get; init; } = [];

    public int MaxResults { get; init; } = 500;
}

public sealed record SearchHit
{
    public required string FilePath { get; init; }
    public required int LineNumber { get; init; }
    public required string Line { get; init; }

    /// <summary>Character offset of the match within <see cref="Line"/>.</summary>
    public int MatchStart { get; init; }

    public int MatchLength { get; init; }

    public string FileName => Path.GetFileName(FilePath);

    /// <summary>Path relative to the project root, for display.</summary>
    public string RelativePath { get; init; } = "";
}

/// <summary>
/// Literal and regex search over the project, backed by ripgrep.
/// </summary>
/// <remarks>
/// ripgrep rather than a managed walker because it already respects .gitignore, skips
/// binaries and is an order of magnitude faster on a real repository. Its --json output
/// is a stable, documented protocol, so byte offsets and line text arrive unambiguously
/// instead of being re-parsed out of formatted console output.
///
/// These are the primitives under both the plain search and the helper model's grep tool.
/// </remarks>
public sealed class RipgrepSearch
{
    private readonly string _root;
    private readonly string _ripgrepPath;

    public RipgrepSearch(string root, string? ripgrepPath = null)
    {
        _root = root;
        _ripgrepPath = ripgrepPath ?? ResolveRipgrep();
    }

    /// <summary>
    /// Prefers the copy shipped beside the app so behaviour does not depend on what the
    /// user happens to have installed; falls back to PATH.
    /// </summary>
    /// <remarks>
    /// PATH hits are resolved through their links to the real executable: winget's
    /// rg.exe is a 0-byte alias reparse point, and spawning the alias instead of its
    /// target stalls when the parent process is under antivirus scrutiny.
    /// </remarks>
    public static string ResolveRipgrep()
    {
        var bundled = Path.Combine(AppContext.BaseDirectory, "rg.exe");
        if (File.Exists(bundled))
        {
            return ResolveLink(bundled) ?? bundled;
        }

        foreach (var dir in (Environment.GetEnvironmentVariable("PATH") ?? "").Split(Path.PathSeparator))
        {
            if (string.IsNullOrWhiteSpace(dir))
            {
                continue;
            }

            try
            {
                var candidate = Path.Combine(dir, "rg.exe");
                if (File.Exists(candidate))
                {
                    return ResolveLink(candidate) ?? candidate;
                }
            }
            catch (ArgumentException)
            {
            }
        }

        return "rg";
    }

    /// <summary>Follows symlink chains to the real file, tolerating resolution failures.</summary>
    private static string? ResolveLink(string path)
    {
        try
        {
            var final = new FileInfo(path).ResolveLinkTarget(returnFinalTarget: true);
            return final is null ? null : final.FullName;
        }
        catch (IOException)
        {
            return null;
        }
    }

    public async IAsyncEnumerable<SearchHit> SearchAsync(
        SearchQuery query,
        [EnumeratorCancellation] CancellationToken ct = default)
    {
        if (string.IsNullOrWhiteSpace(query.Text))
        {
            yield break;
        }

        var startInfo = new ProcessStartInfo
        {
            FileName = _ripgrepPath,
            WorkingDirectory = _root,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            UseShellExecute = false,
            CreateNoWindow = true,
            StandardOutputEncoding = new UTF8Encoding(false),
            StandardErrorEncoding = new UTF8Encoding(false),
        };

        startInfo.ArgumentList.Add("--json");
        startInfo.ArgumentList.Add("--line-number");

        // File-level noise (unreadable files) stays out of stderr, which is read for what
        // is left: a pattern ripgrep rejects.
        startInfo.ArgumentList.Add("--no-messages");

        // Honour .gitignore even in a folder that is not (yet) a git repository.
        startInfo.ArgumentList.Add("--no-require-git");

        if (!query.IsRegex)
        {
            startInfo.ArgumentList.Add("--fixed-strings");
        }

        startInfo.ArgumentList.Add(query.MatchCase ? "--case-sensitive" : "--ignore-case");

        if (query.Glob is { Length: > 0 } glob)
        {
            startInfo.ArgumentList.Add("--glob");
            startInfo.ArgumentList.Add(glob);
        }

        foreach (var exclude in query.ExcludeGlobs)
        {
            startInfo.ArgumentList.Add("--glob");
            startInfo.ArgumentList.Add("!" + exclude);
        }

        // Cap the work ripgrep does rather than only the results we keep.
        startInfo.ArgumentList.Add("--max-count");
        startInfo.ArgumentList.Add("50");

        startInfo.ArgumentList.Add("--");
        startInfo.ArgumentList.Add(query.Text);
        startInfo.ArgumentList.Add(".");

        using var process = Process.Start(startInfo);
        if (process is null)
        {
            yield break;
        }

        using var registration = ct.Register(() => ProcessUtil.TryKillTree(process));

        // Read as it arrives so a full stderr pipe can never stall ripgrep.
        var stderr = process.StandardError.ReadToEndAsync();

        var fullRoot = Path.GetFullPath(_root);
        var returned = 0;
        var stoppedEarly = false;

        while (await process.StandardOutput.ReadLineAsync(ct).ConfigureAwait(false) is { } line)
        {
            if (returned >= query.MaxResults)
            {
                // Stop ripgrep outright. Once we stop reading, its stdout pipe fills and
                // it blocks mid-write, so waiting for it to exit below would never return.
                ProcessUtil.TryKillTree(process);
                stoppedEarly = true;
                break;
            }

            if (Parse(line, fullRoot) is not { } hit)
            {
                continue;
            }

            returned++;
            yield return hit;
        }

        try
        {
            await process.WaitForExitAsync(ct).ConfigureAwait(false);
        }
        catch (OperationCanceledException)
        {
        }

        // Exit 2 with nothing found is ripgrep refusing the search (a bad regex), which
        // must not read as "no matches". Exit 2 with hits is just an unreadable file.
        if (!stoppedEarly && returned == 0 && !ct.IsCancellationRequested && process.HasExited && process.ExitCode == 2 &&
            (await stderr.ConfigureAwait(false)).Trim() is { Length: > 0 } message)
        {
            throw new RipgrepSearchException(TextClip.Truncate(message, 500));
        }
    }

    /// <summary>
    /// Every file ripgrep would search, relative to the root - .gitignore and binaries
    /// already applied. One process, tens of milliseconds on a normal repository.
    /// </summary>
    public async Task<IReadOnlyList<string>> ListFilesAsync(
        IReadOnlyList<string>? excludeGlobs = null, int max = 20000, CancellationToken ct = default)
    {
        var startInfo = new ProcessStartInfo
        {
            FileName = _ripgrepPath,
            WorkingDirectory = _root,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            UseShellExecute = false,
            CreateNoWindow = true,
            StandardOutputEncoding = new UTF8Encoding(false),
            StandardErrorEncoding = new UTF8Encoding(false),
        };

        startInfo.ArgumentList.Add("--files");
        startInfo.ArgumentList.Add("--no-messages");
        startInfo.ArgumentList.Add("--no-require-git");

        foreach (var exclude in excludeGlobs ?? [])
        {
            startInfo.ArgumentList.Add("--glob");
            startInfo.ArgumentList.Add("!" + exclude);
        }

        var files = new List<string>();

        using var process = Process.Start(startInfo);
        if (process is null)
        {
            return files;
        }

        using var registration = ct.Register(() => ProcessUtil.TryKillTree(process));

        // Nothing wants stderr here, but an undrained pipe is a hang waiting for a chatty day.
        _ = process.StandardError.ReadToEndAsync();

        while (await process.StandardOutput.ReadLineAsync(ct).ConfigureAwait(false) is { } line)
        {
            if (files.Count >= max)
            {
                ProcessUtil.TryKillTree(process);
                break;
            }

            var relative = line.StartsWith(".\\", StringComparison.Ordinal) || line.StartsWith("./", StringComparison.Ordinal)
                ? line[2..]
                : line;
            files.Add(relative.Replace('/', Path.DirectorySeparatorChar));
        }

        return files;
    }

    internal static SearchHit? Parse(string jsonLine, string root)
    {
        if (string.IsNullOrWhiteSpace(jsonLine))
        {
            return null;
        }

        try
        {
            using var doc = JsonDocument.Parse(jsonLine);
            var rootElement = doc.RootElement;

            // begin/end/summary events carry no match to show.
            if (!rootElement.TryGetProperty("type", out var type) || type.ValueKind != JsonValueKind.String || !type.ValueEquals("match"u8))
            {
                return null;
            }

            if (!rootElement.TryGetProperty("data", out var data))
            {
                return null;
            }

            var path = Text(data, "path");
            var lineText = Text(data, "lines");

            if (path is null || lineText is null)
            {
                return null;
            }

            var lineNumber = data.TryGetProperty("line_number", out var n) && n.TryGetInt32(out var parsed)
                ? parsed
                : 0;

            var start = 0;
            var length = 0;

            if (data.TryGetProperty("submatches", out var submatches) &&
                submatches.ValueKind == JsonValueKind.Array &&
                submatches.GetArrayLength() > 0)
            {
                var first = submatches[0];
                start = first.TryGetProperty("start", out var s) && s.TryGetInt32(out var sv) ? sv : 0;
                var end = first.TryGetProperty("end", out var e) && e.TryGetInt32(out var ev) ? ev : start;
                length = Math.Max(0, end - start);
            }

            // rg runs with the root as its working directory, so the path arrives
            // root-relative already (modulo a leading .\ or ./, and rg prints Windows
            // paths with backslashes); a Combine onto the already-absolute root replaces
            // a GetFullPath per hit on the hottest parse loop.
            var relative = path.Length >= 2 && path[0] == '.' && (path[1] == '\\' || path[1] == '/')
                ? path[2..]
                : path;
            var full = Path.Combine(root, relative.Replace('/', Path.DirectorySeparatorChar));

            return new SearchHit
            {
                FilePath = full,
                RelativePath = relative,
                LineNumber = lineNumber,
                Line = lineText.TrimEnd('\r', '\n'),
                MatchStart = start,
                MatchLength = length,
            };
        }
        catch (JsonException)
        {
            return null;
        }
    }

    /// <summary>
    /// ripgrep encodes paths and line text either as {"text": "..."} or, when the bytes
    /// are not valid UTF-8, as {"bytes": "&lt;base64&gt;"}.
    /// </summary>
    private static string? Text(JsonElement parent, string property)
    {
        if (!parent.TryGetProperty(property, out var element))
        {
            return null;
        }

        if (element.TryGetProperty("text", out var text))
        {
            return text.GetString();
        }

        if (element.TryGetProperty("bytes", out var bytes) && bytes.GetString() is { } encoded)
        {
            try
            {
                return Encoding.UTF8.GetString(Convert.FromBase64String(encoded));
            }
            catch (FormatException)
            {
                return null;
            }
        }

        return null;
    }
}

/// <summary>ripgrep rejected the search itself (an invalid regex, say) rather than finding nothing.</summary>
public sealed class RipgrepSearchException(string message) : InvalidOperationException(message);
