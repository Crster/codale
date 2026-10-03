using System.Text.Json;
using System.Text.Json.Nodes;
using System.Text.RegularExpressions;

namespace Codale.Commands;

/// <summary>
/// Finds a project's run commands in whatever config files happen to be lying around:
/// Codale's own <c>.codale/commands.json</c> plus the ones the user already keeps for
/// other tools - Claude's <c>launch.json</c>, VS Code's <c>tasks.json</c> and
/// <c>launch.json</c>, and <c>package.json</c> scripts.
/// </summary>
/// <remarks>
/// Every file is optional and every field in it is optional. These are formats Codale
/// does not control (and <c>.claude/launch.json</c> has no published schema at all), so
/// the parsers read tolerantly: a malformed file contributes nothing and never throws.
/// Discovery runs fresh on each call - the commands menu re-runs it every time it opens,
/// which keeps up with edits from any editor without a file watcher.
/// </remarks>
public static class CommandCatalog
{
    /// <summary>VS Code configs are JSONC in the wild: comments and trailing commas are legal there.</summary>
    private static readonly JsonDocumentOptions Jsonc = new()
    {
        CommentHandling = JsonCommentHandling.Skip,
        AllowTrailingCommas = true,
    };

    public static IReadOnlyList<ProjectCommand> Discover(string projectRoot) =>
    [
        .. ReadCodale(projectRoot),
        .. ReadClaude(projectRoot),
        .. ReadVscodeTasks(projectRoot),
        .. ReadVscodeLaunch(projectRoot),
        .. ReadPackageJson(projectRoot),
    ];

    /// <summary>
    /// Adds a command to <c>.codale/commands.json</c>, or replaces the entry of the same
    /// name (case-insensitive), so an agent can register a command and re-register it
    /// corrected. Other entries and any extra fields in the file are kept; a file that
    /// cannot be parsed is an error rather than overwritten.
    /// </summary>
    public static ProjectCommand Add(string projectRoot, string name, string command, string? workingDirectory = null)
    {
        name = name.Trim();
        command = command.Trim();
        if (name.Length == 0 || command.Length == 0)
        {
            throw new ArgumentException("A name and a command are required.");
        }

        var path = Path.Combine(projectRoot, ".codale", "commands.json");
        var entry = new JsonObject { ["name"] = name, ["command"] = command };
        if (!string.IsNullOrWhiteSpace(workingDirectory))
        {
            entry["cwd"] = workingDirectory.Trim();
        }

        JsonNode? existing = null;
        if (File.Exists(path))
        {
            try
            {
                var text = File.ReadAllText(path);
                existing = string.IsNullOrWhiteSpace(text)
                    ? null
                    : JsonNode.Parse(text, documentOptions: Jsonc);
            }
            catch (JsonException ex)
            {
                throw new InvalidOperationException($"{path} is not valid JSON ({ex.Message}); fix it before adding commands.");
            }
        }

        // The canonical wrapper is { "commands": [...] }; a bare array is kept as one.
        JsonArray list;
        JsonNode document;
        switch (existing)
        {
            case JsonArray bare:
                list = bare;
                document = bare;
                break;
            case JsonObject wrapper:
                list = wrapper["commands"] as JsonArray ?? [];
                wrapper["commands"] = list;
                document = wrapper;
                break;
            default:
                list = [];
                document = new JsonObject { ["commands"] = list };
                break;
        }

        var index = list.ToList().FindIndex(n =>
            n is JsonObject o && string.Equals(NameOf(o), name, StringComparison.OrdinalIgnoreCase));
        if (index >= 0)
        {
            list[index] = entry;
        }
        else
        {
            list.Add(entry);
        }

        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        File.WriteAllText(path, document.ToJsonString(new JsonSerializerOptions { WriteIndented = true }) + Environment.NewLine);

        return new ProjectCommand
        {
            Name = name,
            Command = command,
            WorkingDirectory = entry["cwd"]?.GetValue<string>(),
            Source = CommandSource.Codale,
            SourcePath = path,
        };
    }

    private static string? NameOf(JsonObject o) =>
        (o["name"] ?? o["label"]) is JsonValue v && v.TryGetValue<string>(out var s) ? s.Trim() : null;

    private static List<ProjectCommand> ReadCodale(string root)
    {
        var path = Path.Combine(root, ".codale", "commands.json");
        using var doc = ParseFile(path);
        if (doc is null)
        {
            return [];
        }

        // The canonical shape is { "commands": [...] }; a bare array is accepted so a
        // minimal file can skip the wrapper.
        var rootElement = doc.RootElement;
        var entries = rootElement.ValueKind == JsonValueKind.Array
            ? rootElement.EnumerateArray()
            : rootElement.Items("commands");

        var commands = new List<ProjectCommand>();
        foreach (var entry in entries)
        {
            if (entry.Str("command", "cmd") is not { Length: > 0 } command)
            {
                continue;
            }

            commands.Add(new ProjectCommand
            {
                Name = entry.Str("name", "label") ?? command.Trim(),
                Command = command,
                WorkingDirectory = entry.Str("cwd", "workingDirectory"),
                Source = CommandSource.Codale,
                SourcePath = path,
            });
        }

        return commands;
    }

    /// <remarks>
    /// No published schema exists, so this accepts the three shapes seen in the wild: a
    /// bare array, VS Code-style <c>configurations</c>, or a <c>commands</c> wrapper -
    /// with the command as a string or a string array (argv style).
    /// </remarks>
    private static List<ProjectCommand> ReadClaude(string root)
    {
        var path = Path.Combine(root, ".claude", "launch.json");
        using var doc = ParseFile(path);
        if (doc is null)
        {
            return [];
        }

        var rootElement = doc.RootElement;
        var entries = rootElement.ValueKind == JsonValueKind.Array
            ? rootElement.EnumerateArray()
            : rootElement.Items("commands", "configurations");

        var commands = new List<ProjectCommand>();
        foreach (var entry in entries)
        {
            if (CommandValue(entry, "command", "cmd", "run") is not { Length: > 0 } command)
            {
                continue;
            }

            commands.Add(new ProjectCommand
            {
                Name = entry.Str("name", "label", "title") ?? command.Trim(),
                Command = command,
                WorkingDirectory = entry.Str("cwd", "workingDirectory", "directory"),
                Source = CommandSource.Claude,
                SourcePath = path,
            });
        }

        return commands;
    }

    private static List<ProjectCommand> ReadVscodeTasks(string root)
    {
        var path = Path.Combine(root, ".vscode", "tasks.json");
        using var doc = ParseFile(path);
        if (doc is null)
        {
            return [];
        }

        var commands = new List<ProjectCommand>();

        foreach (var task in doc.RootElement.Items("tasks"))
        {
            // Per-OS overrides win when present: "windows": { "command": ... }.
            var windows = task.Prop("windows");
            var command = windows.Str("command") ?? task.Str("command");
            if (string.IsNullOrWhiteSpace(command))
            {
                continue;
            }

            var args = (windows.StrArray("args").Count > 0 ? windows : task).StrArray("args");
            var cwd = windows.Prop("options").Str("cwd") ?? task.Prop("options").Str("cwd");

            commands.Add(new ProjectCommand
            {
                Name = task.Str("label") ?? JoinArguments(command, args),
                Command = JoinArguments(command, args),
                WorkingDirectory = cwd,
                Source = CommandSource.VscodeTasks,
                SourcePath = path,
            });
        }

        return commands;
    }

    /// <remarks>
    /// Launch configurations describe debuggers, not shell commands, so the command is
    /// derived: the runtime executable (or the program itself) plus its arguments.
    /// Configurations that derive nothing - attach requests, browser types with only a
    /// URL - are skipped rather than shown as commands that cannot work.
    /// </remarks>
    private static List<ProjectCommand> ReadVscodeLaunch(string root)
    {
        var path = Path.Combine(root, ".vscode", "launch.json");
        using var doc = ParseFile(path);
        if (doc is null)
        {
            return [];
        }

        var commands = new List<ProjectCommand>();

        foreach (var config in doc.RootElement.Items("configurations"))
        {
            var windows = config.Prop("windows");
            var executable = windows.Str("runtimeExecutable") ?? config.Str("runtimeExecutable")
                ?? windows.Str("program") ?? config.Str("program");
            if (string.IsNullOrWhiteSpace(executable))
            {
                continue;
            }

            var runtimeArgs = (windows.StrArray("runtimeArgs").Count > 0 ? windows : config).StrArray("runtimeArgs");
            var args = (windows.StrArray("args").Count > 0 ? windows : config).StrArray("args");

            commands.Add(new ProjectCommand
            {
                Name = config.Str("name") ?? executable,
                Command = JoinArguments(executable, [.. runtimeArgs, .. args]),
                WorkingDirectory = windows.Str("cwd") ?? config.Str("cwd"),
                Source = CommandSource.VscodeLaunch,
                SourcePath = path,
            });
        }

        return commands;
    }

    /// <summary>
    /// A script name that is safe to place after <c>npm run</c> unquoted. The key comes from a
    /// file in the (possibly untrusted) project, and the command is later run through a shell:
    /// anything beyond a plain name - spaces, <c>;</c>, <c>&amp;</c>, quotes - is not run.
    /// </summary>
    private static readonly Regex SafeScriptName = new(@"^[A-Za-z0-9_:.@/-]+$", RegexOptions.CultureInvariant);

    private static List<ProjectCommand> ReadPackageJson(string root)
    {
        var path = Path.Combine(root, "package.json");
        using var doc = ParseFile(path);
        if (doc?.RootElement.Prop("scripts") is not { ValueKind: JsonValueKind.Object } scripts)
        {
            return [];
        }

        var commands = new List<ProjectCommand>();
        foreach (var script in scripts.EnumerateObject())
        {
            if (script.Value.ValueKind != JsonValueKind.String || script.Value.GetString() is not { Length: > 0 })
            {
                continue;
            }

            // A leading dash would be read by npm as one of its own options.
            if (!SafeScriptName.IsMatch(script.Name) || script.Name.StartsWith('-'))
            {
                continue;
            }

            commands.Add(new ProjectCommand
            {
                Name = script.Name,
                Command = $"npm run {script.Name}",
                Source = CommandSource.PackageJson,
                SourcePath = path,
            });
        }

        return commands;
    }

    private static JsonDocument? ParseFile(string path)
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
    private static string? CommandValue(JsonElement entry, params string[] names)
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

    private static string JoinArguments(string command, IReadOnlyList<string> args)
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
    private static string Quote(string arg)
    {
        var trimmed = arg.Trim();

        if (trimmed.Contains('"') && !(trimmed.StartsWith('"') && trimmed.EndsWith('"') && trimmed.Count(c => c == '"') == 2))
        {
            var escaped = OperatingSystem.IsWindows() ? trimmed.Replace("'", "''") : trimmed.Replace("'", @"'\''");
            return $"'{escaped}'";
        }

        return trimmed.Contains(' ') && !trimmed.StartsWith('"') ? $"\"{trimmed}\"" : trimmed;
    }
}

/// <summary>Tolerant accessors over config files Codale does not control (see JsonEx's rationale).</summary>
internal static class JsonElementEx
{
    public static JsonElement Prop(this JsonElement e, string name) =>
        e.ValueKind == JsonValueKind.Object && e.TryGetProperty(name, out var v) ? v : default;

    public static string? Str(this JsonElement e, params string[] names)
    {
        foreach (var name in names)
        {
            if (e.Prop(name) is { ValueKind: JsonValueKind.String } v && v.GetString() is { } s)
            {
                return s;
            }
        }

        return null;
    }

    public static IReadOnlyList<string> StrArray(this JsonElement e, string name)
    {
        if (e.Prop(name) is not { ValueKind: JsonValueKind.Array } arr)
        {
            return [];
        }

        var list = new List<string>(arr.GetArrayLength());
        foreach (var item in arr.EnumerateArray())
        {
            if (item.ValueKind == JsonValueKind.String && item.GetString() is { } s)
            {
                list.Add(s);
            }
        }

        return list;
    }

    public static IEnumerable<JsonElement> Items(this JsonElement e, params string[] names)
    {
        foreach (var name in names)
        {
            if (e.Prop(name) is { ValueKind: JsonValueKind.Array } arr)
            {
                return arr.EnumerateArray();
            }
        }

        return [];
    }
}
