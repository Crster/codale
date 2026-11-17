using System.Text.Json;
using System.Text.Json.Nodes;
using System.Text.RegularExpressions;
using Xunit.Abstractions;

namespace Codale.Search.Tests.Eval;

/// <summary>
/// Builds locator eval cases from real Codex sessions: each prompt sent in a project, labelled with the
/// files that turn edited. Set CODALE_CODEX_SESSIONS to ~/.codex/sessions, CODALE_LOCATOR_PROJECT to the
/// project the sessions ran in, and CODALE_LOCATOR_CASES_OUT to the JSON file to write.
/// </summary>
public sealed partial class CodexHistoryCases(ITestOutputHelper output)
{
    private const int MaxEdited = 8;

    [GeneratedRegex(@"[\\/]+")]
    private static partial Regex Slashes();

    [Fact]
    public void Writes_cases_from_codex_history()
    {
        var sessions = Environment.GetEnvironmentVariable("CODALE_CODEX_SESSIONS");
        var project = Environment.GetEnvironmentVariable("CODALE_LOCATOR_PROJECT");
        var target = Environment.GetEnvironmentVariable("CODALE_LOCATOR_CASES_OUT");
        if (string.IsNullOrWhiteSpace(sessions) || string.IsNullOrWhiteSpace(project) || string.IsNullOrWhiteSpace(target)) return;

        var root = Slashes().Replace(Path.GetFullPath(project), "/").TrimEnd('/') + "/";
        var cases = new List<object>();
        var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (var file in Directory.EnumerateFiles(sessions, "rollout-*.jsonl", SearchOption.AllDirectories).Order())
        {
            var turns = Read(file, root);
            if (turns is null) continue;
            var first = true;
            foreach (var (prompt, edited) in turns)
            {
                var isFirst = first;
                first = false;
                var files = edited.Where(f => f.StartsWith("src/", StringComparison.OrdinalIgnoreCase) &&
                    File.Exists(Path.Combine(project, f))).Distinct(StringComparer.OrdinalIgnoreCase).ToList();
                if (files.Count == 0 || edited.Count > MaxEdited) continue;
                if (!PromptFileLocator.ShouldRetrieve(prompt, hasAttachments: false, isFreshSession: isFirst, isNewTopic: true)) continue;
                if (!seen.Add(prompt)) continue;
                cases.Add(new { prompt, files, first = isFirst });
            }
        }

        File.WriteAllText(target, JsonSerializer.Serialize(cases, new JsonSerializerOptions { WriteIndented = true }));
        output.WriteLine($"{cases.Count} cases written to {target}");
    }

    /// <summary>The session's turns in order (request text, repo-relative files it edited); null when it ran elsewhere or is a review.</summary>
    private static List<(string Prompt, List<string> Edited)>? Read(string file, string root)
    {
        var turns = new List<(string Prompt, List<string> Edited)>();
        var byId = new Dictionary<string, int>();
        foreach (var line in File.ReadLines(file))
        {
            if (JsonNode.Parse(line) is not JsonObject entry || entry["payload"] is not JsonObject payload) continue;
            if ((string?)entry["type"] == "session_meta")
            {
                var cwd = Slashes().Replace((string?)payload["cwd"] ?? "", "/").TrimEnd('/') + "/";
                if (!cwd.StartsWith(root, StringComparison.OrdinalIgnoreCase) || (string?)payload["thread_source"] == "guardian_review") return null;
                continue;
            }

            if ((string?)payload["type"] != "item_completed" || payload["item"] is not JsonObject item) continue;
            var turn = (string?)payload["turn_id"] ?? "";
            switch ((string?)item["type"])
            {
                case "UserMessage" when !byId.ContainsKey(turn):
                    var text = string.Join(' ', (item["content"] as JsonArray ?? [])
                        .OfType<JsonObject>().Where(c => (string?)c["type"] == "text").Select(c => (string?)c["text"] ?? ""));
                    byId[turn] = turns.Count;
                    turns.Add((Request(text), []));
                    break;
                case "FileChange" when byId.TryGetValue(turn, out var index) && item["changes"] is JsonObject changes:
                    foreach (var (path, _) in changes)
                    {
                        var full = Slashes().Replace(path, "/");
                        if (full.StartsWith(root, StringComparison.OrdinalIgnoreCase)) turns[index].Edited.Add(full[root.Length..]);
                    }
                    break;
            }
        }

        return turns;
    }

    /// <summary>What the user typed: attachment preambles and context Codale sent ahead of it removed.</summary>
    internal static string Request(string text)
    {
        const string marker = "## My request:";
        var at = text.LastIndexOf(marker, StringComparison.Ordinal);
        if (at >= 0) text = text[(at + marker.Length)..];
        // Context hints Codale put in front: a header line, "- path" rows and indented anchors, then a blank line.
        if (text.StartsWith("Likely edit sites", StringComparison.Ordinal) || text.StartsWith("Project file search hints", StringComparison.Ordinal))
        {
            var end = text.IndexOf("\n\n", StringComparison.Ordinal);
            text = end < 0 ? "" : text[(end + 2)..];
        }

        return text.Trim();
    }
}
