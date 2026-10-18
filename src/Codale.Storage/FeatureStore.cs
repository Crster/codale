using System.Globalization;

using Codale.Core.Features;

using Microsoft.Data.Sqlite;

namespace Codale.Storage;

/// <summary>
/// One project's features and the search behind them: what each feature is, its summary, and
/// the stretches of the files the agent wrote for it. Lives in the project's own database
/// (<c>features.db</c> beside its logs) rather than <see cref="CodaleStore"/>, so indexing on a
/// background thread never queues behind the UI thread's writes.
/// </summary>
public sealed class FeatureStore : IDisposable
{
    private const int RecentPromptChars = 2_000;

    private readonly SqliteConnection _connection;
    private readonly object _gate = new();

    public FeatureStore(string databasePath)
    {
        Directory.CreateDirectory(Path.GetDirectoryName(databasePath)!);
        _connection = new SqliteConnection($"Data Source={databasePath}");
        _connection.Open();

        Execute("PRAGMA journal_mode=WAL");
        Execute("PRAGMA synchronous=NORMAL");
        Execute("""
            CREATE TABLE IF NOT EXISTS features (
                id TEXT PRIMARY KEY,
                name TEXT NOT NULL,
                description TEXT NOT NULL DEFAULT '',
                summary TEXT NOT NULL DEFAULT '',
                summary_offset INTEGER NOT NULL DEFAULT 0,
                log_bytes INTEGER NOT NULL DEFAULT 0,
                prompts TEXT NOT NULL DEFAULT '',
                created_at INTEGER NOT NULL,
                updated_at INTEGER NOT NULL
            );
            CREATE VIRTUAL TABLE IF NOT EXISTS features_fts USING fts5(id UNINDEXED, tokens);
            CREATE TABLE IF NOT EXISTS feature_files (
                feature_id TEXT NOT NULL,
                path TEXT NOT NULL,
                written_at INTEGER NOT NULL,
                PRIMARY KEY (feature_id, path)
            );
            CREATE TABLE IF NOT EXISTS file_chunks (
                id INTEGER PRIMARY KEY,
                feature_id TEXT NOT NULL,
                path TEXT NOT NULL,
                start_line INTEGER NOT NULL,
                text TEXT NOT NULL
            );
            CREATE INDEX IF NOT EXISTS ix_chunks_file ON file_chunks (feature_id, path);
            CREATE VIRTUAL TABLE IF NOT EXISTS chunks_fts USING fts5(chunk_id UNINDEXED, tokens);
            """);
    }

    public FeatureInfo Create(string name, string description)
    {
        var now = DateTimeOffset.Now;
        var feature = new FeatureInfo
        {
            Id = Guid.NewGuid().ToString("N")[..10],
            Name = name,
            Description = description,
            CreatedAt = now,
            UpdatedAt = now,
        };

        lock (_gate)
        {
            Execute(
                "INSERT INTO features (id, name, description, created_at, updated_at) VALUES ($id, $name, $description, $at, $at)",
                ("$id", feature.Id), ("$name", name), ("$description", description), ("$at", now.ToUnixTimeMilliseconds()));
            RefreshSearch(feature.Id);
        }

        return feature;
    }

    /// <summary>Most recently active first.</summary>
    public IReadOnlyList<FeatureInfo> List()
    {
        lock (_gate)
        {
            return Query($"{Select} ORDER BY updated_at DESC", Read);
        }
    }

    public FeatureInfo? Get(string id)
    {
        lock (_gate)
        {
            return Query($"{Select} WHERE id = $id", Read, ("$id", id)).FirstOrDefault();
        }
    }

    public void Rename(string id, string name)
    {
        lock (_gate)
        {
            Execute("UPDATE features SET name = $name WHERE id = $id", ("$id", id), ("$name", name));
            RefreshSearch(id);
        }
    }

    /// <summary>Removes a feature with its files and search entries; its log folder is the caller's.</summary>
    public void Delete(string id)
    {
        lock (_gate)
        {
            Execute("""
                DELETE FROM chunks_fts WHERE chunk_id IN (SELECT id FROM file_chunks WHERE feature_id = $id);
                DELETE FROM file_chunks WHERE feature_id = $id;
                DELETE FROM feature_files WHERE feature_id = $id;
                DELETE FROM features_fts WHERE id = $id;
                DELETE FROM features WHERE id = $id;
                """, ("$id", id));
        }
    }

    /// <summary>Notes what a turn added to the log: its size now, and the prompt, which the feature is found by later.</summary>
    public void RecordProgress(string id, long logBytes, string? prompt)
    {
        lock (_gate)
        {
            var prompts = prompt is { Length: > 0 }
                ? Tail((Query("SELECT prompts FROM features WHERE id = $id", r => r.GetString(0), ("$id", id)).FirstOrDefault() ?? "") + "\n" + prompt)
                : null;

            Execute(
                "UPDATE features SET log_bytes = $bytes, updated_at = $at" + (prompts is null ? "" : ", prompts = $prompts") + " WHERE id = $id",
                ("$id", id), ("$bytes", logBytes), ("$at", DateTimeOffset.Now.ToUnixTimeMilliseconds()), ("$prompts", prompts));
            RefreshSearch(id);
        }
    }

    /// <summary>A new summary, current up to <paramref name="logOffset"/> bytes of the log.</summary>
    public void SetSummary(string id, string summary, long logOffset, long? logBytes = null)
    {
        lock (_gate)
        {
            Execute(
                "UPDATE features SET summary = $summary, summary_offset = $offset, log_bytes = coalesce($bytes, log_bytes) WHERE id = $id",
                ("$id", id), ("$summary", summary), ("$offset", logOffset), ("$bytes", logBytes));
            RefreshSearch(id);
        }
    }

    /// <summary>Every feature with the words it is found by, most recently active first.</summary>
    public IReadOnlyList<FeatureDocument> Documents()
    {
        lock (_gate)
        {
            var tokens = Query("SELECT id, tokens FROM features_fts", r => (r.GetString(0), r.GetString(1)))
                .ToDictionary(t => t.Item1, t => t.Item2);
            return [.. Query($"{Select} ORDER BY updated_at DESC", Read).Select(f => new FeatureDocument(f, tokens.GetValueOrDefault(f.Id, "")))];
        }
    }

    /// <summary>Features most likely to own <paramref name="text"/>, best match first.</summary>
    public IReadOnlyList<FeatureInfo> Search(string text, int limit)
    {
        if (SearchText.ToMatchQuery(text) is not { } match)
        {
            return [];
        }

        lock (_gate)
        {
            return Query(
                $"{Select} WHERE id IN (SELECT id FROM features_fts WHERE features_fts MATCH $match ORDER BY bm25(features_fts) LIMIT $limit)",
                Read, ("$match", match), ("$limit", limit));
        }
    }

    /// <summary>Replaces what is indexed of one file the agent wrote for a feature.</summary>
    public void ReplaceFile(string featureId, string path, IReadOnlyList<(int StartLine, string Text)> chunks)
    {
        lock (_gate)
        {
            using var transaction = _connection.BeginTransaction();

            Execute("""
                DELETE FROM chunks_fts WHERE chunk_id IN (SELECT id FROM file_chunks WHERE feature_id = $f AND path = $p);
                DELETE FROM file_chunks WHERE feature_id = $f AND path = $p;
                INSERT INTO feature_files (feature_id, path, written_at) VALUES ($f, $p, $at)
                    ON CONFLICT (feature_id, path) DO UPDATE SET written_at = $at;
                """, ("$f", featureId), ("$p", path), ("$at", DateTimeOffset.Now.ToUnixTimeMilliseconds()));

            foreach (var (startLine, text) in chunks)
            {
                var id = Insert(
                    "INSERT INTO file_chunks (feature_id, path, start_line, text) VALUES ($f, $p, $line, $text) RETURNING id",
                    ("$f", featureId), ("$p", path), ("$line", startLine), ("$text", text));
                Execute(
                    "INSERT INTO chunks_fts (chunk_id, tokens) VALUES ($id, $tokens)",
                    ("$id", id), ("$tokens", SearchText.ForIndex(path + " " + text)));
            }

            transaction.Commit();
        }
    }

    /// <summary>The stretches of written files best matching <paramref name="text"/>: the feature's own first, then the rest of the project's.</summary>
    public IReadOnlyList<FileHit> SearchFiles(string text, string? featureId, int limit)
    {
        if (SearchText.ToMatchQuery(text) is not { } match)
        {
            return [];
        }

        lock (_gate)
        {
            var hits = Query(
                $"""
                SELECT c.feature_id, c.path, c.start_line, c.text
                FROM chunks_fts f JOIN file_chunks c ON c.id = f.chunk_id
                WHERE chunks_fts MATCH $match {(featureId is null ? "" : "AND c.feature_id = $feature")}
                ORDER BY bm25(chunks_fts) LIMIT $limit
                """,
                r => new FileHit(r.GetString(0), r.GetString(1), r.GetInt32(2), r.GetString(3)),
                ("$match", match), ("$feature", featureId), ("$limit", limit));
            return hits;
        }
    }

    /// <summary>Paths the agent wrote for a feature, newest first.</summary>
    public IReadOnlyList<string> FilesOf(string featureId, int limit)
    {
        lock (_gate)
        {
            return Query(
                "SELECT path FROM feature_files WHERE feature_id = $id ORDER BY written_at DESC LIMIT $limit",
                r => r.GetString(0), ("$id", featureId), ("$limit", limit));
        }
    }

    public int FileCount(string featureId)
    {
        lock (_gate)
        {
            return Query("SELECT count(*) FROM feature_files WHERE feature_id = $id", r => r.GetInt32(0), ("$id", featureId)).First();
        }
    }

    public void Dispose()
    {
        lock (_gate)
        {
            _connection.Dispose();
        }
    }

    private const string Select =
        "SELECT id, name, description, summary, summary_offset, log_bytes, created_at, updated_at FROM features";

    private static FeatureInfo Read(SqliteDataReader r) => new()
    {
        Id = r.GetString(0),
        Name = r.GetString(1),
        Description = r.GetString(2),
        Summary = r.GetString(3),
        SummaryOffset = r.GetInt64(4),
        LogBytes = r.GetInt64(5),
        CreatedAt = DateTimeOffset.FromUnixTimeMilliseconds(r.GetInt64(6)),
        UpdatedAt = DateTimeOffset.FromUnixTimeMilliseconds(r.GetInt64(7)),
    };

    private static string Tail(string text) =>
        text.Length <= RecentPromptChars ? text : text[^RecentPromptChars..];

    /// <summary>Rebuilds a feature's search entry from its name, description, summary, recent prompts and file paths.</summary>
    private void RefreshSearch(string id)
    {
        var parts = Query(
            "SELECT name || ' ' || description || ' ' || summary || ' ' || prompts || ' ' || coalesce((SELECT group_concat(path, ' ') FROM feature_files WHERE feature_id = $id), '') FROM features WHERE id = $id",
            r => r.GetString(0), ("$id", id)).FirstOrDefault();

        Execute("DELETE FROM features_fts WHERE id = $id", ("$id", id));
        if (parts is not null)
        {
            Execute("INSERT INTO features_fts (id, tokens) VALUES ($id, $tokens)", ("$id", id), ("$tokens", SearchText.ForIndex(parts)));
        }
    }

    private void Execute(string sql, params (string Name, object? Value)[] parameters)
    {
        using var command = Command(sql, parameters);
        command.ExecuteNonQuery();
    }

    private long Insert(string sql, params (string Name, object? Value)[] parameters)
    {
        using var command = Command(sql, parameters);
        return Convert.ToInt64(command.ExecuteScalar(), CultureInfo.InvariantCulture);
    }

    private List<T> Query<T>(string sql, Func<SqliteDataReader, T> read, params (string Name, object? Value)[] parameters)
    {
        using var command = Command(sql, parameters);
        using var reader = command.ExecuteReader();
        var rows = new List<T>();
        while (reader.Read())
        {
            rows.Add(read(reader));
        }

        return rows;
    }

    private SqliteCommand Command(string sql, (string Name, object? Value)[] parameters)
    {
        var command = _connection.CreateCommand();
        command.CommandText = sql;
        foreach (var (name, value) in parameters)
        {
            // Only the parameters the statement names are bound: callers share one list.
            if (sql.Contains(name, StringComparison.Ordinal))
            {
                command.Parameters.AddWithValue(name, value ?? DBNull.Value);
            }
        }

        return command;
    }
}