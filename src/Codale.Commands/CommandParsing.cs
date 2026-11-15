using System.Text.Json;
using System.Text.Json.Nodes;
using System.Text.RegularExpressions;
using System.Xml.Linq;

namespace Codale.Commands;

internal static class CommandParsing
{
    internal static readonly JsonDocumentOptions Jsonc = new()
    {
        CommentHandling = JsonCommentHandling.Skip,
        AllowTrailingCommas = true,
    };

    internal static List<ProjectCommand> PresetCommands(
        CommandSource source,
        string sourcePath,
        params (string Name, string Command)[] items) =>
        items.Select(item => new ProjectCommand
        {
            Name = item.Name,
            Command = item.Command,
            Source = source,
            SourcePath = sourcePath,
        }).ToList();

    internal static string? FirstExisting(string root, params string[] relativePaths)
    {
        foreach (var rel in relativePaths)
        {
            var full = Path.Combine(root, rel);
            if (File.Exists(full))
            {
                return full;
            }
        }

        return null;
    }

    internal static string ReadTextSafe(string path)
    {
        try
        {
            return File.Exists(path) ? File.ReadAllText(path) : "";
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or ArgumentException)
        {
            return "";
        }
    }

    internal static readonly Regex TomlSectionHeaderRegex = new(@"^\s*\[([^\]]+)\]\s*(?:#.*)?$", RegexOptions.CultureInvariant);
    internal static readonly Regex TomlKeyValueRegex = new(@"^\s*(?:""([^""]+)""|'([^']+)'|([A-Za-z0-9_.-]+))\s*=\s*(.+)$", RegexOptions.CultureInvariant);

    internal static IEnumerable<(string Key, string Value)> ParseTomlSectionKeys(string text, string targetSection)
    {
        var inSection = false;
        foreach (var rawLine in text.Split('\n'))
        {
            var line = rawLine.Trim();
            if (line.Length == 0 || line.StartsWith('#'))
            {
                continue;
            }

            var headerMatch = TomlSectionHeaderRegex.Match(line);
            if (headerMatch.Success)
            {
                inSection = string.Equals(headerMatch.Groups[1].Value.Trim(), targetSection, StringComparison.Ordinal);
                continue;
            }

            if (!inSection)
            {
                continue;
            }

            var kvMatch = TomlKeyValueRegex.Match(line);
            if (kvMatch.Success)
            {
                var key = kvMatch.Groups[1].Value is { Length: > 0 } k1 ? k1
                    : kvMatch.Groups[2].Value is { Length: > 0 } k2 ? k2
                    : kvMatch.Groups[3].Value;
                yield return (key, kvMatch.Groups[4].Value.Trim());
            }
        }
    }

    internal static JsonDocument? ParseFile(string path)
    {
        try
        {
            if (!File.Exists(path))
            {
                return null;
            }

            return JsonDocument.Parse(File.ReadAllText(path), Jsonc);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or JsonException or ArgumentException)
        {
            // Unreadable or malformed: the file simply contributes no commands.
            return null;
        }
    }

    /// <summary>The command line as a string, or joined and quoted when the format allows argv arrays.</summary>
    internal static string? CommandValue(JsonElement entry, params string[] names)
    {
        foreach (var name in names)
        {
            switch (entry.Prop(name))
            {
                case { ValueKind: JsonValueKind.String } s:
                    return s.GetString();
                case { ValueKind: JsonValueKind.Array } a:
                    var parts = a.EnumerateArray()
                        .Where(item => item.ValueKind == JsonValueKind.String)
                        .Select(item => item.GetString())
                        .OfType<string>()
                        .ToList();
                    return parts.Count > 0 ? JoinArguments(parts[0], parts[1..]) : null;
            }
        }

        return null;
    }

    internal static string JoinArguments(string command, IReadOnlyList<string> args)
    {
        if (args.Count == 0)
        {
            return command;
        }

        return string.Join(' ', [command.Trim(), .. args.Select(Quote)]);
    }

    /// <summary>
    /// Quotes an argument the shell would otherwise split on a space. An argument that holds
    /// a quote of its own would end a double-quoted string early, so it is single-quoted
    /// instead (PowerShell doubles an embedded <c>'</c>, POSIX sh closes and reopens the string).
    /// </summary>
    internal static string Quote(string arg)
    {
        var trimmed = arg.Trim();

        if (trimmed.Contains('"') && !(trimmed.StartsWith('"') && trimmed.EndsWith('"') && trimmed.Count(c => c == '"') == 2))
        {
            return $"'{trimmed.Replace("'", "''")}'";
        }

        return trimmed.Contains(' ') && !trimmed.StartsWith('"') ? $"\"{trimmed}\"" : trimmed;
    }
    internal static readonly Regex SafeScriptName = new(@"^[A-Za-z0-9_:.@/-]+$", RegexOptions.CultureInvariant);

    internal static bool IsSafeName(string name) =>
        SafeScriptName.IsMatch(name) && !name.StartsWith('-');

}
