using System.Text.Json;

using Codale.Db;

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
    public string Directory => directory;

    private const string CommandsKey = "commands";
    private sealed record Configuration(string Value);
    private WorkspaceDatabasePool Pool => databases ?? WorkspaceDb.Shared;

    private T Use<T>(Func<RecordCollection<Configuration>, T> work) => Pool.UseRecords(directory, db => work(db.Record<Configuration>()));

    public string? Get(string name) => Use(records => records.Key(name).Get()?.Value);

    public void Set(string name, string value)
    {
        ArgumentNullException.ThrowIfNull(value);
        Use(records => { records.Key(name).Value(new(value)); return true; });
    }

    /// <summary>Claims a durable operation once, including across multiple views of the same workspace.</summary>
    public bool TrySetIfAbsent(string name, string value) => Use(records =>
    {
        ArgumentNullException.ThrowIfNull(value);
        var record = records.Key(name);
        if (record.TryGet(out _)) return false;
        record.Value(new(value));
        return true;
    });

    public bool Remove(string name) => Use(records => records.Key(name).TryDelete(out _));

    public IReadOnlyList<string> Names() => Use(records => records.Keys());

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
        Use(records =>
        {
            var record = records.Key(CommandsKey);
            var all = ReadCommands(record)
                .Where(c => !string.Equals(c.Name, name, StringComparison.OrdinalIgnoreCase) &&
                            !string.Equals(c.Name, replaces, StringComparison.OrdinalIgnoreCase))
                .Append(saved)
                .ToList();
            record.Value(new(JsonSerializer.Serialize(all)));
            return true;
        });
        return saved;
    }

    /// <summary>Removes the command of that name (case-insensitive); false when there was none.</summary>
    public bool RemoveCommand(string name) => Use(records =>
    {
        var record = records.Key(CommandsKey);
        var all = ReadCommands(record);
        if (all.RemoveAll(c => string.Equals(c.Name, name, StringComparison.OrdinalIgnoreCase)) == 0) return false;
        record.Value(new(JsonSerializer.Serialize(all)));
        return true;
    });

    private static List<SavedCommand> ReadCommands(RecordKey<Configuration> record) =>
        record.Get() is { } saved ? JsonSerializer.Deserialize<List<SavedCommand>>(saved.Value) ?? [] : [];

}
