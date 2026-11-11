using System.Text.Json;

using Codale.Db;

namespace Codale.App.Services;

/// <summary>
/// Commands typed into the shell box and into terminals, kept on this computer per project. The
/// shell box recalls them and offers the ones matching what is typed; nothing leaves the machine.
/// </summary>
/// <remarks>
/// History is an aid, so a failed read or write never blocks a command: it is logged by being
/// skipped. Each project's commands live under <c>project.KEY.command</c> in <see cref="AppDb"/>, one entry per
/// distinct command holding its source, use count and last use.
/// </remarks>
public static class CommandHistory
{
    private const int MaxCommandLength = 2_000;
    private const int MaxSuggestions = 8;

    /// <summary>Commands kept per project; the least recently used beyond this are dropped.</summary>
    public const int MaxCommandsPerProject = 2000;

    private static readonly object Gate = new();

    private sealed record Entry(string Source, int Uses, long LastUsed);

    private static DbNode CommandsOf(CodaleDb db, string projectPath) => db.Project(projectPath).Open("command");

    /// <summary>Records a command run from <paramref name="source"/> ("shell" or "terminal") in a project.</summary>
    public static void Record(string projectPath, string command, string source)
    {
        var text = command.Trim();
        if (text.Length == 0 || text.Length > MaxCommandLength || projectPath.Length == 0)
        {
            return;
        }

        lock (Gate)
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
    public static IReadOnlyList<string> Recent(string projectPath)
    {
        if (projectPath.Length == 0)
        {
            return [];
        }

        lock (Gate)
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
    public static IReadOnlyList<string> Suggest(string projectPath, string typed)
    {
        var prefix = typed.TrimStart();
        if (prefix.Length == 0 || projectPath.Length == 0)
        {
            return [];
        }

        lock (Gate)
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
    internal static void Record(string projectPath, string command, string source, DateTimeOffset when)
    {
        using var db = AppDb.Open();
        var commands = CommandsOf(db, projectPath);
        var node = commands.Open(command.Replace('', ' '));
        db.Batch(() =>
        {
            var uses = node.ReadJson<Entry>()?.Uses ?? 0;
            node.WriteJson(new Entry(source, uses + 1, when.ToUnixTimeMilliseconds()));

            // Pruned in slices of a hundred, so a full history does not rescan every command on every keystroke.
            if (commands.ChildCount() <= MaxCommandsPerProject + 100)
            {
                return;
            }

            foreach (var old in commands.Descendants()
                .Select(n => (Node: n, Entry: n.ReadJson<Entry>()))
                .OrderByDescending(x => x.Entry?.LastUsed ?? 0)
                .Skip(MaxCommandsPerProject))
            {
                old.Node.Remove();
            }
        });
    }

    /// <summary>The project's commands, most recently used first.</summary>
    internal static IReadOnlyList<string> RecentCommands(string projectPath, int max = 500)
    {
        using var db = AppDb.Open();
        return Entries(CommandsOf(db, projectPath))
            .OrderByDescending(x => x.Entry.LastUsed)
            .Take(max)
            .Select(x => x.Text)
            .ToList();
    }

    /// <summary>The project's commands that begin with <paramref name="prefix"/> (case-insensitively), the most often used first and then the most recent.</summary>
    internal static IReadOnlyList<string> CommandsStartingWith(string projectPath, string prefix, int max = 8)
    {
        using var db = AppDb.Open();
        return Entries(CommandsOf(db, projectPath))
            .Where(x => x.Text.StartsWith(prefix, StringComparison.OrdinalIgnoreCase) && x.Text != prefix)
            .OrderByDescending(x => x.Entry.Uses)
            .ThenByDescending(x => x.Entry.LastUsed)
            .Take(max)
            .Select(x => x.Text)
            .ToList();
    }

    private static IEnumerable<(string Text, Entry Entry)> Entries(DbNode commands)
    {
        foreach (var node in commands.Descendants())
        {
            if (node.ReadJson<Entry>() is { } entry)
            {
                yield return (node.Name, entry);
            }
        }
    }
}
