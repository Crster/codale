using System.Globalization;

using Codale.Core.Projects;

using Microsoft.Data.Sqlite;

namespace Codale.Storage;

/// <summary>The database was written by a newer Codale than this one; opening it for writes could corrupt it.</summary>
public sealed class StoreSchemaException(string message) : InvalidOperationException(message);

/// <summary>
/// Codale's own database: the task lists agents showed, and what the user arranged (panel
/// sizes, per-project settings).
/// </summary>
/// <remarks>
/// What the agent did is not kept here: a project's features, their logs and their file index
/// live in the project's own folder (see <c>FeatureStore</c>).
/// </remarks>
public sealed class CodaleStore : IDisposable
{
    private readonly SqliteConnection _connection;

    /// <summary>
    /// Databases this process has already migrated. Every store construction used to
    /// replay the whole DDL batch on the UI thread; the schema cannot change mid-run,
    /// so each database file only needs it once per process.
    /// </summary>
    private static readonly HashSet<string> MigratedPaths = new(StringComparer.OrdinalIgnoreCase);

    /// <summary>Serialises migration so two stores opened at once on a fresh file do not both build the schema.</summary>
    private static readonly object MigrateLock = new();

    /// <summary>The schema this build understands. A database stamped higher was written by a newer Codale.</summary>
    public const int SupportedSchemaVersion = 1;

    public CodaleStore(string databasePath)
    {
        Directory.CreateDirectory(Path.GetDirectoryName(databasePath)!);

        _connection = new SqliteConnection(new SqliteConnectionStringBuilder
        {
            DataSource = databasePath,
            Mode = SqliteOpenMode.ReadWriteCreate,
        }.ToString());

        _connection.Open();

        // WAL keeps a slow read from blocking the UI thread's writes. synchronous=NORMAL
        // is the standard WAL pairing: commits no longer fsync on every write, which the
        // UI thread pays per session update; only the last commits of a power loss roll back.
        Execute("PRAGMA journal_mode=WAL;");
        Execute("PRAGMA synchronous=NORMAL;");
        Execute("PRAGMA foreign_keys=ON;");

        var fullPath = Path.GetFullPath(databasePath);
        try
        {
            lock (MigrateLock)
            {
                // Recorded only once Migrate has finished: a failed attempt must be retried, not remembered as done.
                if (!MigratedPaths.Contains(fullPath))
                {
                    Migrate();
                    MigratedPaths.Add(fullPath);
                }
            }
        }
        catch
        {
            _connection.Dispose();
            throw;
        }
    }

    /// <summary>
    /// Default location. Under MSIX this redirects into the package's local folder, so
    /// it survives the in-place updates run.ps1 performs.
    /// </summary>
    public static string DefaultDatabasePath =>
        Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
            "Codale",
            "codale.db");

    private void Migrate()
    {
        Execute("""
            CREATE TABLE IF NOT EXISTS schema_version (version INTEGER NOT NULL);

            CREATE TABLE IF NOT EXISTS todos (
                session_id TEXT NOT NULL,
                position   INTEGER NOT NULL,
                content    TEXT NOT NULL,
                status     TEXT NOT NULL,
                PRIMARY KEY (session_id, position)
            );

            CREATE TABLE IF NOT EXISTS ui_state (
                project_key TEXT NOT NULL,
                name        TEXT NOT NULL,
                value       TEXT NOT NULL,
                PRIMARY KEY (project_key, name)
            );
            """);

        using var command = _connection.CreateCommand();
        command.CommandText = "SELECT COALESCE(MAX(version), 0) FROM schema_version;";
        var version = Convert.ToInt32(command.ExecuteScalar(), CultureInfo.InvariantCulture);

        if (version > SupportedSchemaVersion)
        {
            throw new StoreSchemaException(
                $"This database uses schema version {version}, but this Codale only understands up to {SupportedSchemaVersion}. Update Codale to open it.");
        }

        if (version == 0)
        {
            Execute($"INSERT INTO schema_version (version) VALUES ({SupportedSchemaVersion});");
        }
    }

    public void SaveTodos(string sessionId, IReadOnlyList<(string Content, string Status)> todos)
    {
        using var transaction = _connection.BeginTransaction();

        using (var delete = _connection.CreateCommand())
        {
            delete.Transaction = transaction;
            delete.CommandText = "DELETE FROM todos WHERE session_id = $id;";
            delete.Parameters.AddWithValue("$id", sessionId);
            delete.ExecuteNonQuery();
        }

        for (var i = 0; i < todos.Count; i++)
        {
            using var insert = _connection.CreateCommand();
            insert.Transaction = transaction;
            insert.CommandText =
                "INSERT INTO todos (session_id, position, content, status) VALUES ($id, $pos, $content, $status);";
            insert.Parameters.AddWithValue("$id", sessionId);
            insert.Parameters.AddWithValue("$pos", i);
            insert.Parameters.AddWithValue("$content", todos[i].Content);
            insert.Parameters.AddWithValue("$status", todos[i].Status);
            insert.ExecuteNonQuery();
        }

        transaction.Commit();
    }

    public IReadOnlyList<(string Content, string Status)> GetTodos(string sessionId)
    {
        using var command = _connection.CreateCommand();
        command.CommandText = "SELECT content, status FROM todos WHERE session_id = $id ORDER BY position;";
        command.Parameters.AddWithValue("$id", sessionId);

        var todos = new List<(string, string)>();
        using var reader = command.ExecuteReader();

        while (reader.Read())
        {
            todos.Add((reader.GetString(0), reader.GetString(1)));
        }

        return todos;
    }

    public void SetUiState(string projectPath, string name, string value) =>
        SetRawState(ProjectPaths.InstanceKey(projectPath), name, value);

    public string? GetUiState(string projectPath, string name) =>
        GetRawState(ProjectPaths.InstanceKey(projectPath), name);

    /// <summary>
    /// Machine-wide setting, not scoped to a project. Real project keys are SHA-256 hex,
    /// so this sentinel can never collide with one.
    /// </summary>
    private const string GlobalKey = "::codale-global::";

    public void SetSetting(string name, string value) => SetRawState(GlobalKey, name, value);

    public string? GetSetting(string name) => GetRawState(GlobalKey, name);

    private void SetRawState(string key, string name, string value)
    {
        using var command = _connection.CreateCommand();
        command.CommandText = """
            INSERT INTO ui_state (project_key, name, value) VALUES ($key, $name, $value)
            ON CONFLICT (project_key, name) DO UPDATE SET value = excluded.value;
            """;

        command.Parameters.AddWithValue("$key", key);
        command.Parameters.AddWithValue("$name", name);
        command.Parameters.AddWithValue("$value", value);
        command.ExecuteNonQuery();
    }

    private string? GetRawState(string key, string name)
    {
        using var command = _connection.CreateCommand();
        command.CommandText = "SELECT value FROM ui_state WHERE project_key = $key AND name = $name;";
        command.Parameters.AddWithValue("$key", key);
        command.Parameters.AddWithValue("$name", name);

        return command.ExecuteScalar() as string;
    }

    private void Execute(string sql)
    {
        using var command = _connection.CreateCommand();
        command.CommandText = sql;
        command.ExecuteNonQuery();
    }

    public void Dispose() => _connection.Dispose();
}