using System.Runtime.CompilerServices;
using System.Text;
using System.Text.RegularExpressions;
using System.Threading.Channels;

using Codale.Core.Text;

using Microsoft.Extensions.FileSystemGlobbing;

namespace Codale.Search;

public sealed record SearchQuery
{
    public required string Text { get; init; }
    public bool IsRegex { get; init; }
    public bool MatchCase { get; init; }

    /// <summary>Only files matching this glob are searched ("*.cs", "src/**/*.ts").</summary>
    public string? Glob { get; init; }

    /// <summary>Globs of files that are never searched.</summary>
    public IReadOnlyList<string> ExcludeGlobs { get; init; } = [];

    public int MaxResults { get; init; } = 500;
}

public sealed record SearchHit
{
    public required string FilePath { get; init; }
    public required int LineNumber { get; init; }
    public required string Line { get; init; }

    /// <summary>Column where the match starts, in characters of <see cref="Line"/>.</summary>
    public int MatchStart { get; init; }

    /// <summary>Length of the match in characters.</summary>
    public int MatchLength { get; init; }

    public string FileName => Path.GetFileName(FilePath);

    /// <summary>The path relative to the searched root, with the platform's separators.</summary>
    public string RelativePath { get; init; } = "";
}

/// <summary>
/// Literal and regex search over a project, and the file listing behind it. Managed: it
/// walks the tree itself, applies each folder's .gitignore, skips hidden entries, symbolic
/// links and binary files, and streams the matching lines as they are found.
/// </summary>
public sealed class WorkspaceSearch
{
    /// <summary>Files larger than this are not searched; matches per file are capped below, so a big generated file still cannot flood the results.</summary>
    private const long MaxFileBytes = 32_000_000;

    /// <summary>Matches reported per file, so one huge generated file cannot fill the results.</summary>
    private const int MaxMatchesPerFile = 50;

    private static readonly TimeSpan RegexTimeout = TimeSpan.FromSeconds(2);

    private readonly string _root;

    public WorkspaceSearch(string root) => _root = Path.GetFullPath(root);

    /// <summary>
    /// Every file under the root that is not ignored, relative to it. Hidden entries, links and
    /// folders named in <paramref name="excludeGlobs"/> are left out.
    /// </summary>
    public Task<IReadOnlyList<string>> ListFilesAsync(
        IReadOnlyList<string>? excludeGlobs = null, int max = 20000, CancellationToken ct = default) =>
        Task.Run<IReadOnlyList<string>>(() => Walk(BuildMatcher(excludeGlobs), max, ct), ct);

    /// <summary>Streams the lines that match, at most <see cref="SearchQuery.MaxResults"/>.</summary>
    /// <exception cref="WorkspaceSearchException">The pattern is not a valid regular expression.</exception>
    public async IAsyncEnumerable<SearchHit> SearchAsync(
        SearchQuery query,
        [EnumeratorCancellation] CancellationToken ct = default)
    {
        if (string.IsNullOrWhiteSpace(query.Text))
        {
            yield break;
        }

        var regex = query.IsRegex ? CompileRegex(query) : null;
        var files = await ListFilesAsync(query.ExcludeGlobs, int.MaxValue, ct).ConfigureAwait(false);
        var include = query.Glob is { Length: > 0 } glob ? BuildMatcher([glob]) : null;

        using var stop = CancellationTokenSource.CreateLinkedTokenSource(ct);
        var channel = Channel.CreateBounded<SearchHit>(new BoundedChannelOptions(256) { SingleReader = true });
        var found = 0;

        var producer = Task.Run(async () =>
        {
            try
            {
                await Parallel.ForEachAsync(
                    files.Where(f => include is null || include.Match(f.Replace('\\', '/')).HasMatches),
                    new ParallelOptions { CancellationToken = stop.Token, MaxDegreeOfParallelism = Environment.ProcessorCount },
                    async (relative, token) =>
                    {
                        foreach (var hit in ScanFile(relative, query, regex))
                        {
                            token.ThrowIfCancellationRequested();
                            if (Interlocked.Increment(ref found) > query.MaxResults)
                            {
                                return;
                            }

                            await channel.Writer.WriteAsync(hit, token).ConfigureAwait(false);
                        }
                    }).ConfigureAwait(false);
                channel.Writer.TryComplete();
            }
            catch (OperationCanceledException)
            {
                channel.Writer.TryComplete();
            }
            catch (Exception ex)
            {
                channel.Writer.TryComplete(ex);
            }
        }, CancellationToken.None);

        try
        {
            await foreach (var hit in channel.Reader.ReadAllAsync(ct).ConfigureAwait(false))
            {
                yield return hit;
            }
        }
        finally
        {
            // The caller stopped early: release the scan instead of letting it block on a full channel.
            stop.Cancel();
            await producer.ConfigureAwait(false);
        }
    }

    private static Regex CompileRegex(SearchQuery query)
    {
        try
        {
            return new Regex(
                query.Text,
                (query.MatchCase ? RegexOptions.None : RegexOptions.IgnoreCase) | RegexOptions.CultureInvariant,
                RegexTimeout);
        }
        catch (ArgumentException ex)
        {
            throw new WorkspaceSearchException(TextClip.Truncate(ex.Message, 500));
        }
    }

    /// <summary>The file's text, or null when it is unreadable, binary or too large to search.</summary>
    private static string? ReadText(string full)
    {
        try
        {
            if (new FileInfo(full).Length > MaxFileBytes)
            {
                return null;
            }

            var bytes = File.ReadAllBytes(full);
            return LooksBinary(bytes) ? null : Encoding.UTF8.GetString(bytes);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            return null;
        }
    }

    private IEnumerable<SearchHit> ScanFile(string relative, SearchQuery query, Regex? regex)
    {
        var full = Path.Combine(_root, relative);
        if (ReadText(full) is not { } text)
        {
            yield break;
        }

        var comparison = query.MatchCase ? StringComparison.Ordinal : StringComparison.OrdinalIgnoreCase;
        var lines = text.Split('\n');
        var matches = 0;

        for (var i = 0; i < lines.Length && matches < MaxMatchesPerFile; i++)
        {
            var line = lines[i].TrimEnd('\r');
            int start;
            int length;

            if (regex is null)
            {
                start = line.IndexOf(query.Text, comparison);
                length = query.Text.Length;
                if (start < 0)
                {
                    continue;
                }
            }
            else
            {
                Match match;
                try
                {
                    match = regex.Match(line);
                }
                catch (RegexMatchTimeoutException)
                {
                    continue;
                }

                if (!match.Success)
                {
                    continue;
                }

                start = match.Index;
                length = match.Length;
            }

            matches++;
            yield return new SearchHit
            {
                FilePath = full,
                RelativePath = relative,
                LineNumber = i + 1,
                Line = line,
                MatchStart = start,
                MatchLength = length,
            };
        }
    }

    /// <summary>A NUL in the opening stretch is how git recognises a binary file.</summary>
    private static bool LooksBinary(byte[] bytes) => Array.IndexOf(bytes, (byte)0, 0, Math.Min(bytes.Length, 8000)) >= 0;

    private List<string> Walk(Matcher excluded, int max, CancellationToken ct)
    {
        var files = new List<string>();
        Visit(_root, "", IgnoreRules.Empty, excluded, files, max, ct);
        return files;
    }

    private void Visit(string dirFull, string dirRelative, IgnoreRules inherited, Matcher excluded,
        List<string> files, int max, CancellationToken ct)
    {
        ct.ThrowIfCancellationRequested();
        var rules = inherited.Extend(dirFull, dirRelative);

        IEnumerable<FileSystemInfo> entries;
        try
        {
            entries = new DirectoryInfo(dirFull).EnumerateFileSystemInfos("*", new EnumerationOptions
            {
                IgnoreInaccessible = true,
                RecurseSubdirectories = false,
                AttributesToSkip = FileAttributes.ReparsePoint,
            });
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            return;
        }

        foreach (var entry in entries)
        {
            if (files.Count >= max)
            {
                return;
            }

            // Hidden entries (.git, .vs, .env ...) are not searched.
            if (entry.Name.StartsWith('.'))
            {
                continue;
            }

            var relative = dirRelative.Length == 0 ? entry.Name : dirRelative + "/" + entry.Name;
            var isDirectory = entry is DirectoryInfo;

            if (rules.IsIgnored(relative, isDirectory))
            {
                continue;
            }

            if (isDirectory)
            {
                // A folder named by an exclude glob is skipped whole; the probe name lets
                // "**/bin/**" match the folder itself.
                if (excluded.Match(relative + "/_").HasMatches)
                {
                    continue;
                }

                Visit(entry.FullName, relative, rules, excluded, files, max, ct);
            }
            else if (!excluded.Match(relative).HasMatches)
            {
                files.Add(relative.Replace('/', Path.DirectorySeparatorChar));
            }
        }
    }

    /// <summary>
    /// Globs: a glob without a folder part ("*.cs") matches at any depth.
    /// </summary>
    private static Matcher BuildMatcher(IReadOnlyList<string>? globs)
    {
        var matcher = new Matcher(StringComparison.OrdinalIgnoreCase);
        foreach (var glob in globs ?? [])
        {
            var normal = glob.Replace('\\', '/').TrimStart('.', '/');
            matcher.AddInclude(normal);
            if (!normal.Contains('/'))
            {
                matcher.AddInclude("**/" + normal);
            }
        }

        return matcher;
    }
}

/// <summary>The pattern was rejected (an invalid regular expression), rather than finding nothing.</summary>
public sealed class WorkspaceSearchException(string message) : InvalidOperationException(message);
