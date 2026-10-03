using System.Globalization;

using Codale.Core.Projects;

using Microsoft.Data.Sqlite;

namespace Codale.Storage;

/// <summary>A session Codale started, as it is remembered across restarts.</summary>
public sealed record SessionRecord
{
    public required string SessionId { get; init; }
    public required string ProjectKey { get; init; }
    public required string ProjectPath { get; init; }
    public string Provider { get; init; } = "claude";
    public string? Title { get; init; }
    public string? WorktreePath { get; init; }
    public string? BranchName { get; init; }
    public DateTimeOffset StartedAt { get; init; }
    public DateTimeOffset UpdatedAt { get; init; }

    /// <summary>True while Codale believes the agent is still running.</summary>
    public bool IsRunning { get; init; }

    public decimal CostUsd { get; init; }
}

/// <summary>The database was written by a newer Codale than this one; opening it for writes could corrupt it.</summary>
public sealed class StoreSchemaException(string message) : InvalidOperationException(message);

/// <summary>
/// Codale's own database: what it started, what the user arranged, and enough to pick
/// up after a crash.
/// </summary>
/// <remarks>
/// Transcripts are deliberately <em>not</em> duplicated here. The CLIs own those files
/// and are the authority on them; storing a second copy would mean two sources of truth
/// that drift. What is stored is an index plus Codale's own annotations.
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

    private const string SessionColumns = """
        session_id, project_key, project_path, provider, title, worktree_path,
        branch_name, started_at, updated_at, is_running, cost_usd
        """;

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

            CREATE TABLE IF NOT EXISTS sessions (
                session_id    TEXT PRIMARY KEY,
                project_key   TEXT NOT NULL,
                project_path  TEXT NOT NULL,
                provider      TEXT NOT NULL DEFAULT 'claude',
                title         TEXT,
                worktree_path TEXT,
                branch_name   TEXT,
                started_at    TEXT NOT NULL,
                updated_at    TEXT NOT NULL,
                is_running    INTEGER NOT NULL DEFAULT 0,
                cost_usd      REAL NOT NULL DEFAULT 0
            );

            CREATE INDEX IF NOT EXISTS ix_sessions_project ON sessions (project_key, updated_at DESC);

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

    public void UpsertSession(SessionRecord session)
    {
        using var command = _connection.CreateCommand();
        command.CommandText = """
            INSERT INTO sessions
                (session_id, project_key, project_path, provider, title, worktree_path,
                 branch_name, started_at, updated_at, is_running, cost_usd)
            VALUES
                ($id, $key, $path, $provider, $title, $worktree,
                 $branch, $started, $updated, $running, $cost)
            ON CONFLICT (session_id) DO UPDATE SET
                title         = COALESCE(excluded.title, sessions.title),
                worktree_path = COALESCE(excluded.worktree_path, sessions.worktree_path),
                branch_name   = COALESCE(excluded.branch_name, sessions.branch_name),
                updated_at    = excluded.updated_at,
                is_running    = excluded.is_running,
                cost_usd      = excluded.cost_usd;
            """;

        command.Parameters.AddWithValue("$id", session.SessionId);
        command.Parameters.AddWithValue("$key", session.ProjectKey);
        command.Parameters.AddWithValue("$path", session.ProjectPath);
        command.Parameters.AddWithValue("$provider", session.Provider);
        command.Parameters.AddWithValue("$title", (object?)session.Title ?? DBNull.Value);
        command.Parameters.AddWithValue("$worktree", (object?)session.WorktreePath ?? DBNull.Value);
        command.Parameters.AddWithValue("$branch", (object?)session.BranchName ?? DBNull.Value);
        command.Parameters.AddWithValue("$started", session.StartedAt.ToString("O"));
        command.Parameters.AddWithValue("$updated", session.UpdatedAt.ToString("O"));
        command.Parameters.AddWithValue("$running", session.IsRunning ? 1 : 0);
        command.Parameters.AddWithValue("$cost", (double)session.CostUsd);

        command.ExecuteNonQuery();
    }

    public IReadOnlyList<SessionRecord> GetSessions(string projectPath, int limit = 50)
    {
        var key = ProjectPaths.InstanceKey(projectPath);

        using var command = _connection.CreateCommand();
        command.CommandText = $"""
            SELECT {SessionColumns}
            FROM sessions
            WHERE project_key = $key
            ORDER BY updated_at DESC
            LIMIT $limit;
            """;

        command.Parameters.AddWithValue("$key", key);
        command.Parameters.AddWithValue("$limit", limit);

        return ReadSessions(command);
    }

    private static List<SessionRecord> ReadSessions(SqliteCommand command)
    {
        var sessions = new List<SessionRecord>();
        using var reader = command.ExecuteReader();

        while (reader.Read())
        {
            sessions.Add(Read(reader));
        }

        return sessions;
    }

    /// <summary>
    /// Sessions still flagged as running. On startup these are, by definition, orphans:
    /// the process that owned them is gone, so the user is offered a resume.
    /// </summary>
    public IReadOnlyList<SessionRecord> GetInterruptedSessions(string projectPath)
    {
        var key = ProjectPaths.InstanceKey(projectPath);

        using var command = _connection.CreateCommand();
        command.CommandText = $"""
            SELECT {SessionColumns}
            FROM sessions
            WHERE project_key = $key AND is_running = 1
            ORDER BY updated_at DESC;
            """;
        command.Parameters.AddWithValue("$key", key);

        return ReadSessions(command);
    }

    public void MarkStopped(string sessionId)
    {
        using var command = _connection.CreateCommand();
        command.CommandText = "UPDATE sessions SET is_running = 0, updated_at = $now WHERE session_id = $id;";
        command.Parameters.AddWithValue("$id", sessionId);
        command.Parameters.AddWithValue("$now", DateTimeOffset.Now.ToString("O"));
        command.ExecuteNonQuery();
    }

    /// <summary>Called on a clean start so a previous crash does not leave stale rows forever.</summary>
    public void MarkAllStopped(string projectPath)
    {
        using var command = _connection.CreateCommand();
        command.CommandText = "UPDATE sessions SET is_running = 0 WHERE project_key = $key;";
        command.Parameters.AddWithValue("$key", ProjectPaths.InstanceKey(projectPath));
        command.ExecuteNonQuery();
    }

    /// <summary>Forgets a session and its todos; the transcript file is the caller's to remove.</summary>
    public void DeleteSession(string sessionId)
    {
        using var command = _connection.CreateCommand();
        command.CommandText = """
            DELETE FROM todos WHERE session_id = $id;
            DELETE FROM sessions WHERE session_id = $id;
            """;
        command.Parameters.AddWithValue("$id", sessionId);
        command.ExecuteNonQuery();
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

    private static SessionRecord Read(SqliteDataReader reader) => new()
    {
        SessionId = reader.GetString(0),
        ProjectKey = reader.GetString(1),
        ProjectPath = reader.GetString(2),
        Provider = reader.GetString(3),
        Title = reader.IsDBNull(4) ? null : reader.GetString(4),
        WorktreePath = reader.IsDBNull(5) ? null : reader.GetString(5),
        BranchName = reader.IsDBNull(6) ? null : reader.GetString(6),
        StartedAt = DateTimeOffset.Parse(reader.GetString(7), CultureInfo.InvariantCulture, DateTimeStyles.RoundtripKind),
        UpdatedAt = DateTimeOffset.Parse(reader.GetString(8), CultureInfo.InvariantCulture, DateTimeStyles.RoundtripKind),
        IsRunning = reader.GetInt32(9) != 0,
        CostUsd = (decimal)reader.GetDouble(10),
    };

    private void Execute(string sql)
    {
        using var command = _connection.CreateCommand();
        command.CommandText = sql;
        command.ExecuteNonQuery();
    }

    public void Dispose() => _connection.Dispose();
}
