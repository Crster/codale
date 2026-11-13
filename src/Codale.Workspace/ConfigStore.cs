using System.Text.Json;
using System.Text.Json.Nodes;

namespace Codale.Workspace;

/// <summary>A run command saved for the workspace (the commands menu).</summary>
public sealed record SavedCommand(string Name, string Command, string? Cwd = null);

/// <summary>
/// The workspace's configuration: what used to be files under <c>.codale/</c>, kept in the workspace database so
/// the project tree stays clean. Plain values by name (<c>config.NAME</c>), with the saved commands on top.
/// </summary>
public sealed class ConfigStore(string directory)
{
    private const string CommandsKey = "commands";

    public string? Get(string name) => WorkspaceDb.Use(directory, db => db.Open("config").Open(name).Read());

    public void Set(string name, string value) => WorkspaceDb.Use(directory, db => db.Open("config").Open(name).Write(value));

    public bool Remove(string name) => WorkspaceDb.Use(directory, db => db.Open("config").Open(name).Remove());

    public IReadOnlyList<string> Names() => WorkspaceDb.Use(directory, db => db.Open("config").Children());

    public T? GetJson<T>(string name) => Get(name) is { } json ? JsonSerializer.Deserialize<T>(json) : default;

    public void SetJson<T>(string name, T value) => Set(name, JsonSerializer.Serialize(value));

    public IReadOnlyList<SavedCommand> Commands() => GetJson<List<SavedCommand>>(CommandsKey) ?? [];

    /// <summary>Adds a command, or replaces the one of the same name (case-insensitive).</summary>
    public SavedCommand AddCommand(string name, string command, string? cwd = null)
    {
        name = name.Trim();
        command = command.Trim();
        if (name.Length == 0 || command.Length == 0)
        {
            throw new ArgumentException("A name and a command are required.");
        }

        var saved = new SavedCommand(name, command, string.IsNullOrWhiteSpace(cwd) ? null : cwd.Trim());
        WorkspaceDb.Use(directory, db => db.Batch(() =>
        {
            var node = db.Open("config").Open(CommandsKey);
            var all = (node.ReadJson<List<SavedCommand>>() ?? [])
                .Where(c => !string.Equals(c.Name, name, StringComparison.OrdinalIgnoreCase))
                .Append(saved)
                .ToList();
            node.WriteJson(all);
        }));
        return saved;
    }

    /// <summary>
    /// Where the commands can be edited as a file. It lives beside the database in Codale's data folder, never in the
    /// workspace; <see cref="SyncEdited"/> reads it back after an edit.
    /// </summary>
    public string EditablePath => Path.Combine(Path.GetDirectoryName(Path.GetFullPath(directory))!, "commands.json");

    /// <summary>Writes the saved commands to <see cref="EditablePath"/> (with a starter entry when there are none) and returns it.</summary>
    public string ExportForEditing()
    {
        var commands = Commands();
        var list = commands.Count > 0
            ? commands
            : [new SavedCommand("build", "dotnet build"), new SavedCommand("run", "dotnet run", "src/MyApp")];
        Directory.CreateDirectory(Path.GetDirectoryName(EditablePath)!);
        File.WriteAllText(EditablePath, JsonSerializer.Serialize(new { commands = list },
            new JsonSerializerOptions { WriteIndented = true, DefaultIgnoreCondition = System.Text.Json.Serialization.JsonIgnoreCondition.WhenWritingNull }) + Environment.NewLine);
        Set(StampKey, File.GetLastWriteTimeUtc(EditablePath).Ticks.ToString());
        return EditablePath;
    }

    private const string StampKey = "commands-file-stamp";

    /// <summary>Replaces the saved commands with the edited file's when it was changed since it was exported; an invalid file changes nothing.</summary>
    public void SyncEdited()
    {
        if (!File.Exists(EditablePath) ||
            File.GetLastWriteTimeUtc(EditablePath).Ticks.ToString() == Get(StampKey))
        {
            return;
        }

        try
        {
            var stamp = File.GetLastWriteTimeUtc(EditablePath).Ticks.ToString();
            var parsed = JsonNode.Parse(File.ReadAllText(EditablePath), documentOptions: new JsonDocumentOptions
            {
                CommentHandling = JsonCommentHandling.Skip,
                AllowTrailingCommas = true,
            });
            var edited = new List<SavedCommand>();
            foreach (var entry in parsed as JsonArray ?? (parsed as JsonObject)?["commands"] as JsonArray ?? [])
            {
                var command = Text(entry, "command") ?? Text(entry, "cmd");
                if (command is { Length: > 0 })
                {
                    edited.Add(new SavedCommand(Text(entry, "name") ?? Text(entry, "label") ?? command, command,
                        Text(entry, "cwd") ?? Text(entry, "workingDirectory")));
                }
            }

            SetJson(CommandsKey, edited);
            Set(StampKey, stamp);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or JsonException)
        {
            // Half-typed JSON: keep what is saved until the file is valid again.
        }
    }

    private static string? Text(JsonNode? entry, string key) =>
        entry is JsonObject o && o[key] is JsonValue v && v.TryGetValue<string>(out var s) ? s.Trim() : null;
}
