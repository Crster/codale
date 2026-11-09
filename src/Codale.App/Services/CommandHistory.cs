using Codale.Storage;

namespace Codale.App.Services;

/// <summary>
/// Commands typed into the shell box and into terminals, kept on this computer per project. The
/// shell box recalls them and offers the ones matching what is typed; nothing leaves the machine.
/// </summary>
/// <remarks>
/// History is an aid, so a failed read or write never blocks a command: it is logged by being
/// skipped. One store stays open for the app's lifetime; each call takes the lock.
/// </remarks>
public static class CommandHistory
{
    private const int MaxCommandLength = 2_000;
    private const int MaxSuggestions = 8;

    private static readonly object Gate = new();
    private static CodaleStore? _store;

    private static CodaleStore Store => _store ??= new CodaleStore(CodaleStore.DefaultDatabasePath);

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
                Store.RecordCommand(projectPath, text, source, DateTimeOffset.Now);
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
                return Store.RecentCommands(projectPath);
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
                return Store.CommandsStartingWith(projectPath, prefix, MaxSuggestions);
            }
            catch (Exception)
            {
                return [];
            }
        }
    }
}
