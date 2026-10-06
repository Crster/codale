using System.Text;
using System.Text.RegularExpressions;

using Codale.Core.Text;

namespace Codale.Search;

/// <summary>The loop's tools when a <see cref="SourceIndex"/> backs it.</summary>
public sealed partial class SearchAgentLoop
{
    private const int MaxSeedChars = 3_500;
    private const int MaxSeedFiles = 16;

    /// <summary>The opening move with an index: its ranking, as findings, hits and sections.</summary>
    private sealed record IndexSeed(
        IReadOnlyList<string> Terms,
        int FileCount,
        string Text,
        IReadOnlyList<SearchHit> Hits,
        IReadOnlyList<(string Path, int Start, int End, string Reason)> Ranges);

    /// <summary>The index's snapshot, or null - no index, or it could not be built - so the loop falls back to ripgrep.</summary>
    private async Task<SourceSnapshot?> SnapshotAsync(CancellationToken ct)
    {
        if (Index is null)
        {
            return null;
        }

        try
        {
            return await Index.GetSnapshotAsync(ct).ConfigureAwait(false);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or InvalidOperationException ||
                                   (ex is OperationCanceledException && !ct.IsCancellationRequested))
        {
            return null;
        }
    }

    /// <summary>
    /// The whole project ranked for the question's words, with the declarations the best
    /// matches use and the files that use them - what a person would open first, found
    /// before the model spends a single step.
    /// </summary>
    private async Task<IndexSeed?> IndexSeedAsync(string query, CancellationToken ct)
    {
        DiscoveryResult result;
        try
        {
            result = await new SourceExplorer(Index!, model: null)
            {
                MaxRankedResults = MaxSeedFiles,
                MaxRelated = 3,
            }.RunAsync(query, ct).ConfigureAwait(false);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or InvalidOperationException ||
                                   (ex is OperationCanceledException && !ct.IsCancellationRequested))
        {
            return null;
        }

        var text = new StringBuilder();
        var hits = new List<SearchHit>();
        var ranges = new List<(string, int, int, string)>();

        if (result.Files.Count == 0)
        {
            text.AppendLine($"The source index found nothing for \"{string.Join(" ", result.Keywords)}\".");
        }
        else
        {
            text.AppendLine($"Source index ranking for \"{string.Join(" ", result.Keywords)}\" ({result.Files.Count} files, best first):");
        }

        foreach (var file in result.Files)
        {
            var block = new StringBuilder();
            block.AppendPath(file.RelativePath);
            block.Append(file.Reason.Length > 0 ? $" - {file.Reason}" : file.MatchCount > 0 ? $" - {file.MatchCount} matching lines" : " - named for it");
            block.AppendLine();

            foreach (var range in file.Ranges.Take(2))
            {
                var code = range.Code.Split('\n');
                var shown = range.MatchLines.Count > 0 ? range.MatchLines.Take(3).ToList() : [range.StartLine];
                foreach (var number in shown)
                {
                    var index = number - range.StartLine;
                    if (index >= 0 && index < code.Length)
                    {
                        block.AppendLine($"  {number}: {TextClip.Truncate(code[index].Trim(), 110)}");
                        hits.Add(new SearchHit
                        {
                            FilePath = file.FilePath,
                            RelativePath = file.RelativePath,
                            LineNumber = number,
                            Line = code[index],
                        });
                    }
                }
            }

            if (ranges.Count < 16 && file.Ranges.Count > 0)
            {
                var first = file.Ranges[0];
                ranges.Add((file.RelativePath, first.StartLine, first.EndLine, file.Reason.Length > 0 ? file.Reason : "ranked by the source index"));
            }

            if (text.Length + block.Length > MaxSeedChars)
            {
                break;
            }

            text.Append(block);
        }

        _terms = result.Keywords;
        return new IndexSeed(result.Keywords, result.Files.Count, text.ToString(), hits, ranges);
    }

    /// <summary>
    /// The model's grep, in memory over the index - line by line and case-insensitive as
    /// ripgrep runs it. Null hands the pattern to ripgrep: no index, a pattern .NET does
    /// not accept (ripgrep reports its own error), or one too slow to finish.
    /// </summary>
    private IReadOnlyList<SearchHit>? GrepIndex(string pattern, string? glob, int max, CancellationToken ct)
    {
        if (_snapshot is null || string.IsNullOrWhiteSpace(pattern))
        {
            return null;
        }

        try
        {
            var regex = new Regex(pattern, RegexOptions.IgnoreCase | RegexOptions.CultureInvariant, TimeSpan.FromSeconds(1));
            return _snapshot.Grep(regex, glob, max, ct: ct);
        }
        catch (Exception ex) when (ex is ArgumentException or RegexMatchTimeoutException)
        {
            return null;
        }
    }

    /// <summary>A name's declarations and its busiest users, as the symbol tool reports them.</summary>
    internal static (IReadOnlyList<SearchHit> Hits, string Text) DescribeSymbol(SourceSnapshot snapshot, string name)
    {
        if (name.Length < 2)
        {
            return ([], "symbol needs a name.");
        }

        var hits = new List<SearchHit>();
        var text = new StringBuilder();
        var definitions = snapshot.Definitions(name).Take(10).ToList();

        if (definitions.Count == 0)
        {
            text.AppendLine($"symbol {name}: no declaration found.");
        }
        else
        {
            text.AppendLine($"symbol {name} is declared in:");
            foreach (var definition in definitions)
            {
                var lines = definition.File.ReadLines();
                var line = definition.Symbol.Line <= lines.Length ? lines[definition.Symbol.Line - 1] : "";
                text.Append("  ").AppendPath(definition.File.RelativePath)
                    .AppendLine($":{definition.Symbol.Line}: {TextClip.Truncate(line.Trim(), 120)}");
                hits.Add(new SearchHit
                {
                    FilePath = definition.File.FullPath,
                    RelativePath = definition.File.RelativePath,
                    LineNumber = definition.Symbol.Line,
                    Line = line,
                });
            }
        }

        if (snapshot.PostingOf(name.ToLowerInvariant()) is { } posting)
        {
            var declaring = definitions.Select(d => d.File).ToHashSet();
            var users = Enumerable.Range(0, posting.Files.Count)
                .Select(j => (File: snapshot.Files[posting.Files[j]], Count: posting.Counts[j]))
                .Where(u => !declaring.Contains(u.File))
                .OrderByDescending(u => u.Count)
                .ThenBy(u => u.File.RelativePath, StringComparer.OrdinalIgnoreCase)
                .ToList();

            if (users.Count > 0)
            {
                text.Append($"used in {users.Count} other files: ");
                text.AppendLine(string.Join(", ", users.Take(12).Select(u => $"{u.File.RelativePath.Replace('\\', '/')} ({u.Count})")));
            }
        }

        return (hits, text.ToString());
    }
}
