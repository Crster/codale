using System.Text.Json;

namespace Codale.Workspace;

/// <summary>A run command saved for the workspace (the commands menu).</summary>
/// <param name="Shell">
/// How <paramref name="Command"/> is meant to run: null for a plain command line, otherwise
/// <c>powershell</c>, <c>batch</c> or <c>shell</c> (the body of a script of that kind).
/// </param>
public sealed record SavedCommand(string Name, string Command, string? Cwd = null, string? Shell = null);

/// <summary>
/// The workspace's configuration: what used to be files under <c>.codale/</c>, kept in the workspace database so
/// the project tree stays clean. Plain values by name (<c>config.NAME</c>), with the saved commands on top.
/// </summary>
public sealed class ConfigStore(string directory, WorkspaceDatabasePool? databases = null)
{
    private const string CommandsKey = "commands";

    public string? Get(string name) => (databases ?? WorkspaceDb.Shared).Use(directory, db => db.Open("config").Open(name).Read());

    public void Set(string name, string value) => (databases ?? WorkspaceDb.Shared).Use(directory, db => db.Open("config").Open(name).Write(value));

    /// <summary>Claims a durable operation once, including across multiple views of the same workspace.</summary>
    public bool TrySetIfAbsent(string name, string value) => (databases ?? WorkspaceDb.Shared).Use(directory, db =>
    {
        var claimed = false;
        db.Batch(() =>
        {
            var node = db.Open("config").Open(name);
            if (node.Read() is not null) return;
            node.Write(value);
            claimed = true;
        });
        return claimed;
    });

    public bool Remove(string name) => (databases ?? WorkspaceDb.Shared).Use(directory, db => db.Open("config").Open(name).Remove());

    public IReadOnlyList<string> Names() => (databases ?? WorkspaceDb.Shared).Use(directory, db => db.Open("config").Children());

    public T? GetJson<T>(string name) => Get(name) is { } json ? JsonSerializer.Deserialize<T>(json) : default;

    public void SetJson<T>(string name, T value) => Set(name, JsonSerializer.Serialize(value));

    public IReadOnlyList<SavedCommand> Commands() => GetJson<List<SavedCommand>>(CommandsKey) ?? [];

    /// <summary>Adds a command, or replaces the one of the same name (case-insensitive).</summary>
    public SavedCommand AddCommand(string name, string command, string? cwd = null) =>
        SaveCommand(null, new SavedCommand(name, command, cwd));

    /// <summary>
    /// Adds a command or replaces one of the same name. <paramref name="originalName"/> names the entry being
    /// edited, so renaming it removes the old entry instead of leaving both behind.
    /// </summary>
    public SavedCommand SaveCommand(string? originalName, SavedCommand command)
    {
        var name = command.Name.Trim();
        if (name.Length == 0 || command.Command.Trim().Length == 0)
        {
            throw new ArgumentException("A name and a command are required.");
        }

        var shell = string.IsNullOrWhiteSpace(command.Shell) ? null : command.Shell.Trim().ToLowerInvariant();
        var saved = new SavedCommand(name, command.Command, string.IsNullOrWhiteSpace(command.Cwd) ? null : command.Cwd.Trim(), shell);
        var replaces = originalName?.Trim();
        (databases ?? WorkspaceDb.Shared).Use(directory, db => db.Batch(() =>
        {
            var node = db.Open("config").Open(CommandsKey);
            var all = (node.ReadJson<List<SavedCommand>>() ?? [])
                .Where(c => !string.Equals(c.Name, name, StringComparison.OrdinalIgnoreCase) &&
                            !string.Equals(c.Name, replaces, StringComparison.OrdinalIgnoreCase))
                .Append(saved)
                .ToList();
            node.WriteJson(all);
        }));
        return saved;
    }

    /// <summary>Removes the command of that name (case-insensitive); false when there was none.</summary>
    public bool RemoveCommand(string name)
    {
        var changed = false;
        (databases ?? WorkspaceDb.Shared).Use(directory, db => db.Batch(() =>
        {
            var node = db.Open("config").Open(CommandsKey);
            var all = node.ReadJson<List<SavedCommand>>() ?? [];
            changed = all.RemoveAll(c => string.Equals(c.Name, name, StringComparison.OrdinalIgnoreCase)) > 0;
            if (changed)
            {
                node.WriteJson(all);
            }
        }));
        return changed;
    }

    /// <summary>The folder beside the database where generated script files live, never in the workspace.</summary>
    public string ScriptsDirectory => Path.Combine(Path.GetDirectoryName(Path.GetFullPath(directory))!, "command-scripts");
}
