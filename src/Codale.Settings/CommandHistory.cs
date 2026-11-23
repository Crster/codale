using Codale.Db;
using Codale.Core.Projects;

namespace Codale.Settings;

/// <summary>
/// Commands typed into the shell box and into terminals, kept on this computer per project. The
/// shell box recalls them and offers the ones matching what is typed; nothing leaves the machine.
/// </summary>
/// <remarks>
/// History is an aid, so a failed read or write never blocks a command: it is logged by being
/// skipped. Each project's commands live under <c>project.KEY.command</c> in <see cref="ApplicationDatabase"/>, one entry per
/// distinct command holding its source, use count and last use.
/// </remarks>
public sealed class CommandHistory(ApplicationDatabase database)
{
    private const int MaxCommandLength = 2_000;
    private const int MaxSuggestions = 8;

    /// <summary>Commands kept per project; the least recently used beyond this are dropped.</summary>
    public const int MaxCommandsPerProject = 2000;

    private readonly Lock _gate = new();

    private sealed record Entry(string Text, string Prefix, string Source, int Uses, long LastUsed);
    private sealed record Total(int Count);

    private const string PrefixIndex = "command-prefix";

    private T Use<T>(string projectPath, Func<CodaleDatabase, string, T> work) => database.UseRecords(db =>
    {
        var project = ProjectPaths.InstanceKey(projectPath) + ":";
        _ = db.Record<Entry>().Index(PrefixIndex).Build(entry => entry.Prefix);
        return work(db, project);
    });

    /// <summary>Records a command run from <paramref name="source"/> ("shell" or "terminal") in a project.</summary>
    public void Record(string projectPath, string command, string source)
    {
        var text = command.Trim();
        if (text.Length == 0 || text.Length > MaxCommandLength || projectPath.Length == 0)
        {
            return;
        }

        lock (_gate)
        {
            try
            {
                Record(projectPath, text, source, DateTimeOffset.Now);
            }
            catch (Exception)
            {
                // Not kept this time; the command itself still runs.
            }
        }
    }

    /// <summary>The project's commands, most recent first.</summary>
    public IReadOnlyList<string> Recent(string projectPath)
    {
        if (projectPath.Length == 0)
        {
            return [];
        }

        lock (_gate)
        {
            try
            {
                return RecentCommands(projectPath);
            }
            catch (Exception)
            {
                return [];
            }
        }
    }

    /// <summary>The project's commands that start with what is typed, for the shell box to offer.</summary>
    public IReadOnlyList<string> Suggest(string projectPath, string typed)
    {
        var prefix = typed.TrimStart();
        if (prefix.Length == 0 || projectPath.Length == 0)
        {
            return [];
        }

        lock (_gate)
        {
            try
            {
                return CommandsStartingWith(projectPath, prefix, MaxSuggestions);
            }
            catch (Exception)
            {
                return [];
            }
        }
    }

    /// <summary>A new command is kept; a repeat counts another use and moves to the front of the recent list.</summary>
    internal void Record(string projectPath, string command, string source, DateTimeOffset when) => Use(projectPath, (db, project) =>
    {
        var text = command.Replace('\u001f', ' ');
        var key = project + text;
        var commands = db.Record<Entry>();
        var previous = commands.Key(key).Get();
        var saved = new Entry(text, project + text.ToUpperInvariant(), source, (previous?.Uses ?? 0) + 1, when.ToUnixTimeMilliseconds());
        if (previous is not null)
        {
            commands.Key(key).Value(saved);
            return true;
        }

        var count = (db.Record<Total>().Key(project).Get()?.Count ?? 0) + 1;
        var trim = count > MaxCommandsPerProject + 100
            ? commands.FindPrefix(project).Append(new(key, saved)).OrderByDescending(row => row.Value.LastUsed)
                .ThenBy(row => row.Key, StringComparer.Ordinal).Skip(MaxCommandsPerProject).Select(row => row.Key).ToArray()
            : [];
        db.WriteBatch(batch =>
        {
            batch.Put(key, saved);
            foreach (var old in trim) batch.Delete<Entry>(old);
            batch.Put(project, new Total(count - trim.Length));
        });
        return true;
    });

    /// <summary>The project's commands, most recently used first.</summary>
    internal IReadOnlyList<string> RecentCommands(string projectPath, int max = 500) => Use(projectPath, (db, project) =>
        db.Record<Entry>().FindPrefix(project).OrderByDescending(row => row.Value.LastUsed).Take(max).Select(row => row.Value.Text).ToArray());

    /// <summary>The project's commands that begin with <paramref name="prefix"/> (case-insensitively), the most often used first and then the most recent.</summary>
    internal IReadOnlyList<string> CommandsStartingWith(string projectPath, string prefix, int max = 8) => Use(projectPath, (db, project) =>
        db.Record<Entry>().Index<string>(PrefixIndex).FindPrefix(project + prefix.ToUpperInvariant(), row => row.Text != prefix)
            .OrderByDescending(row => row.Value.Uses).ThenByDescending(row => row.Value.LastUsed).Take(max).Select(row => row.Value.Text).ToArray());
}
