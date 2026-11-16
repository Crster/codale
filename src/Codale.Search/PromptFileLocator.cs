using System.Text;
using System.Text.RegularExpressions;
using Codale.Core.Features;
using Codale.Search.Engine;

namespace Codale.Search;

public sealed record FileLocatorContext(string Text, IReadOnlyList<string> Files);

/// <summary>Local global-search hints, never source snippets or generated prose.</summary>
public static class PromptFileLocator
{
    public static bool ShouldRetrieve(string prompt, bool hasAttachments)
    {
        var text = prompt.Trim();
        if (hasAttachments || text.Length == 0 || text.StartsWith('/')) return false;
        // Operational requests already tell the CLI what to do. Diagnostics and feature
        // changes mentioning these words still benefit from source discovery.
        if (Regex.IsMatch(text, @"^(?:(?:please|now|just|then)\s+)*(?:(?:git|dotnet|npm|pnpm|yarn|cargo)\s+)?(?:commit|build|rebuild|rename|push|pull|fetch|checkout|publish|deploy|run\s+(?:the\s+)?tests?)\b",
            RegexOptions.IgnoreCase | RegexOptions.CultureInvariant)) return false;
        return SearchText.RetrievalTerms(text).Count > 0;
    }

    public static async Task<FileLocatorContext> RetrieveAsync(string root, string prompt,
        ProjectKnowledgeIndex? knowledge = null, CancellationToken ct = default, IReadOnlySet<string>? knownFiles = null)
    {
        var result = await new SourceExplorer(SourceIndex.For(root), null)
        {
            Knowledge = knowledge, MaxRelated = 0, MaxRankedResults = 12,
            Budget = TimeSpan.FromSeconds(3)
        }.RunAsync(prompt, ct).ConfigureAwait(false);
        var terms = SearchText.RetrievalTerms(prompt).Take(12).ToArray();
        // History only boosts actual current matches. Deleted files and unrelated old
        // sessions can never introduce a hint.
        var history = knowledge?.SearchFeatures(prompt, 3)
            .SelectMany(f => knowledge.FilesOf(f.Id, 12))
            .ToHashSet(StringComparer.OrdinalIgnoreCase) ?? [];
        var matches = new List<(DiscoveredFile File, string[] Terms)>();
        foreach (var file in result.Files)
        {
            ct.ThrowIfCancellationRequested();
            if (knownFiles?.Contains(file.RelativePath.Replace('\\', '/')) == true) continue;
            if (!ProjectKnowledgeIndex.Eligible(file.RelativePath) || !File.Exists(file.FilePath)) continue;
            var searchable = file.RelativePath + "\n" + string.Join("\n", file.Ranges.Select(r => r.Code));
            var verified = terms.Where(t => searchable.Contains(t, StringComparison.OrdinalIgnoreCase)).Take(4).ToArray();
            if (verified.Length > 0) matches.Add((file, verified));
        }
        var text = new StringBuilder();
        var files = new List<string>();
        foreach (var match in matches.OrderByDescending(m => history.Contains(m.File.RelativePath)).Take(6))
        {
            var path = match.File.RelativePath.Replace('\\', '/');
            var line = $"- {path} | {string.Join(", ", match.Terms)}\n";
            if (Encoding.UTF8.GetByteCount(text.ToString() + line) > 1400) break;
            text.Append(line); files.Add(path);
        }
        return new(files.Count == 0 ? "" :
            "Project file search hints (paths | matching keywords; verify as needed):\n" + text + "\n", files);
    }
}
