using Codale.Core.Projects;
using Codale.Db;

namespace Codale.Workspace;

/// <summary>
/// The workspace's own database, kept in Codale's data folder and never in the workspace itself, so the
/// project tree stays clean. It houses four things: <c>history</c> (every chat's log, tool calls included),
/// <c>memory</c> (notes the agent saves and recalls), <c>config</c> (what used to live in <c>.codale/</c>) and
/// <c>env</c> (secrets, encrypted per value with a key only this Windows account can unwrap).
/// </summary>
/// <remarks>
/// The app and the agents' tool servers are separate processes and one process owns a database folder at a
/// time, so nothing holds it for long: <see cref="Use{T}"/> opens it for one operation and keeps it a moment
/// afterwards (a burst of writes does not reopen it each time), but never longer than a second and a half, so
/// another process always gets its turn.
/// </remarks>
public static class WorkspaceDb
{
    private static readonly TimeSpan IdleClose = TimeSpan.FromMilliseconds(300);
    private static readonly TimeSpan MaxHold = TimeSpan.FromSeconds(1.5);
    private static readonly TimeSpan LockWait = TimeSpan.FromSeconds(5);
    private static readonly object Gate = new();
    private static readonly Dictionary<string, Pin> Pins = new(StringComparer.OrdinalIgnoreCase);

    private sealed class Pin
    {
        public CodaleDb? Db;
        public DateTime Since;
        public Timer? Timer;
    }

    /// <summary>The database folder for a project (shared by its linked worktrees).</summary>
    public static string DirectoryFor(string projectPath) =>
        Path.Combine(ProjectPaths.KnowledgeDirectory(projectPath), "workspace-db");

    public static void Use(string directory, Action<CodaleDb> action) => Use<object?>(directory, db =>
    {
        action(db);
        return null;
    });

    /// <exception cref="IOException">Another process kept the database busy longer than the wait.</exception>
    public static T Use<T>(string directory, Func<CodaleDb, T> work)
    {
        var key = Path.GetFullPath(directory);
        CodaleDb lease;
        lock (Gate)
        {
            if (!Pins.TryGetValue(key, out var pin))
            {
                Pins[key] = pin = new Pin();
            }

            var options = new CodaleDbOptions { LockTimeout = LockWait };
            if (pin.Db is null)
            {
                pin.Db = CodaleDb.Acquire(key, options);
                pin.Since = DateTime.UtcNow;
                pin.Timer = new Timer(_ => Release(key), null, IdleClose, Timeout.InfiniteTimeSpan);
            }
            else if (DateTime.UtcNow - pin.Since < MaxHold)
            {
                pin.Timer!.Change(IdleClose, Timeout.InfiniteTimeSpan);
            }

            lease = CodaleDb.Acquire(key, options);
        }

        try
        {
            return work(lease);
        }
        finally
        {
            lease.Dispose();
        }
    }

    private static void Release(string key)
    {
        lock (Gate)
        {
            if (!Pins.Remove(key, out var pin))
            {
                return;
            }

            pin.Timer?.Dispose();
            pin.Db?.Dispose();
        }
    }
}
