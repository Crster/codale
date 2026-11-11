using Codale.Core.Projects;
using Codale.Db;

namespace Codale.App.Services;

/// <summary>
/// Codale's own database: what the user arranged (settings, panel state, recent projects, command history) and the
/// task lists agents showed. One folder is shared by every Codale window, and each window is its own process, so
/// the database is held only while it is in use: <see cref="Open"/> keeps it for a short idle time after the last
/// call (typing in the shell box does not reopen it per keystroke) and then lets go for the other windows.
/// </summary>
/// <remarks>
/// Layout: <c>setting.NAME</c> machine-wide values; <c>project.KEY.ui.NAME</c> per-project values;
/// <c>project.KEY.command.TEXT</c> command history; <c>todos.SESSION.POSITION</c> an agent's task list.
/// KEY is <see cref="ProjectPaths.InstanceKey"/>. What the agent did lives in the project's own database
/// (see <c>FeatureService</c>).
/// </remarks>
public static class AppDb
{
    private static readonly TimeSpan IdleClose = TimeSpan.FromSeconds(2);
    private static readonly object Gate = new();
    private static readonly Timer Idle = new(_ => Release(), null, Timeout.Infinite, Timeout.Infinite);
    private static CodaleDb? _pin;

    /// <summary>
    /// Under MSIX this redirects into the package's local folder, so it survives the in-place updates
    /// run.ps1 performs. Settings files such as <c>settings.json</c> live beside the database.
    /// </summary>
    public static string Folder { get; private set; } =
        Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "Codale");

    public static string DatabasePath => Path.Combine(Folder, "data");

    /// <summary>Points the database somewhere else (tests).</summary>
    internal static void UseFolder(string folder)
    {
        lock (Gate)
        {
            Release();
            Folder = folder;
        }
    }

    /// <summary>A lease on the database; dispose it when the operation is done.</summary>
    /// <exception cref="IOException">Another Codale window kept the database busy longer than the wait.</exception>
    public static CodaleDb Open()
    {
        lock (Gate)
        {
            var options = new CodaleDbOptions { LockTimeout = TimeSpan.FromSeconds(3) };
            _pin ??= CodaleDb.Acquire(DatabasePath, options);
            Idle.Change(IdleClose, Timeout.InfiniteTimeSpan);
            return CodaleDb.Acquire(DatabasePath, options);
        }
    }

    /// <summary>The root of a project's own entries, keyed by its path.</summary>
    public static DbNode Project(this CodaleDb db, string projectPath) =>
        db.Open("project").Open(ProjectPaths.InstanceKey(projectPath));

    /// <summary>A machine-wide setting.</summary>
    public static DbNode Setting(this CodaleDb db, string name) => db.Open("setting").Open(name);

    private static void Release()
    {
        lock (Gate)
        {
            _pin?.Dispose();
            _pin = null;
        }
    }
}
