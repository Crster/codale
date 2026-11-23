using System.Buffers.Binary;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;

using Microsoft.Data.Sqlite;

namespace Codale.Db.Engine;

internal sealed record IndexChange(string File, string Bucket, byte[]? Id, byte[]? Sort = null,
    byte[]? Value = null, string? SearchText = null, bool FullText = false);

internal readonly record struct IndexRow(byte[] Id, byte[] Value);

/// <summary>SQLite B-trees hold offsets and index entries. The append-only blocks hold the record values.</summary>
internal sealed class FolderStorage : IDisposable
{
    public const string Records = "_records";
    private const int CachedValues = 32_768;
    private const int MaxCachedValueSize = 4 << 10;
    private const int ValueCacheBudget = 16 << 20;

    private readonly record struct ValueLocation(int Block, long Offset, int Size);

    private readonly string _directory;
    private readonly FileStream _lock;
    private readonly SegmentLog _blocks;
    private readonly SqliteConnection _queue;
    private readonly Dictionary<string, SqliteConnection> _indexes = new(StringComparer.Ordinal);
    private readonly Dictionary<string, SqliteCommand> _lookups = new(StringComparer.Ordinal);
    private readonly Dictionary<string, bool> _fullText = new(StringComparer.Ordinal);
    private readonly Dictionary<ValueLocation, ReadOnlyMemory<byte>> _smallValues = [];
    private readonly Queue<ValueLocation> _cachedLocations = [];
    private int _valueCacheBytes;
    private const int VectorCacheBudget = 8 << 20;
    private readonly Dictionary<(string File, string Model), (IReadOnlyList<IndexRow> Rows, long Bytes)> _vectorCache = [];
    private long _vectorCacheBytes;
    private bool _needsRecovery;

    public FolderStorage(string directory, long blockSize, TimeSpan lockTimeout)
    {
        _directory = Path.GetFullPath(directory);
        Directory.CreateDirectory(_directory);
        _lock = FolderLock.Acquire(_directory, lockTimeout);
        _blocks = new SegmentLog(_directory, blockSize, recordBlocks: true);
        BlockSize = blockSize;
        SqliteConnection? queue = null;
        try
        {
            if (Directory.EnumerateFiles(_directory, "seg-*.log").Any())
                throw new InvalidDataException("This folder uses the path-based CodaleDb format. Open it with CodaleDb.");
            _blocks.OpenForAppend();
            if (_blocks.TotalBytes > 0 && !File.Exists(IndexPath(Records)))
                throw new InvalidDataException("The primary index for these record blocks is missing.");
            _queue = queue = Connect(Path.Combine(_directory, "tmp.db"));
            Execute(_queue, "CREATE TABLE IF NOT EXISTS pending (id INTEGER PRIMARY KEY, changes BLOB NOT NULL)");
            GetIndex(Records);
            Recover();
        }
        catch
        {
            try { foreach (var index in _indexes.Values) index.Dispose(); }
            finally
            {
                try { queue?.Dispose(); }
                finally
                {
                    try { _blocks.Dispose(); }
                    finally { _lock.Dispose(); }
                }
            }
            throw;
        }
    }

    public long BlockSize { get; }

    public static string IndexFile(string name)
    {
        ArgumentException.ThrowIfNullOrEmpty(name);
        if (name.Length <= 100 && name[0] != '_' && name.All(c => c is >= 'a' and <= 'z' or >= '0' and <= '9' or '-' or '_'))
            return name;
        return "~" + Convert.ToHexStringLower(SHA256.HashData(Encoding.BigEndianUnicode.GetBytes(name)));
    }

    public byte[] AppendValue(ReadOnlySpan<byte> value)
    {
        if (value.Length > BlockSize - SegmentLog.HeaderSize)
            throw new ArgumentException("The serialized value exceeds the record block size.", nameof(value));
        var (block, offset, size) = _blocks.Append(RecordType.Value, 0, value);
        if (value.Length <= MaxCachedValueSize) CacheValue(new(block, offset, size), value.ToArray());
        var address = new byte[16];
        BinaryPrimitives.WriteInt32LittleEndian(address, block);
        BinaryPrimitives.WriteInt64LittleEndian(address.AsSpan(4), offset);
        BinaryPrimitives.WriteInt32LittleEndian(address.AsSpan(12), size);
        return address;
    }

    public ReadOnlyMemory<byte> ReadValue(byte[] address)
    {
        if (address.Length != 16) throw new InvalidDataException("Invalid record address.");
        var block = BinaryPrimitives.ReadInt32LittleEndian(address);
        var offset = BinaryPrimitives.ReadInt64LittleEndian(address.AsSpan(4));
        var size = BinaryPrimitives.ReadInt32LittleEndian(address.AsSpan(12));
        if (block <= 0 || offset < 0 || size < SegmentLog.HeaderSize || size > (256 << 20))
            throw new InvalidDataException("Invalid record address.");
        var location = new ValueLocation(block, offset, size);
        if (_smallValues.TryGetValue(location, out var cached)) return cached;
        var frame = new byte[size];
        _blocks.Read(block, offset, frame);
        if (frame[8] != (byte)RecordType.Value || BinaryPrimitives.ReadUInt32LittleEndian(frame.AsSpan(4)) != size - 8 ||
            BinaryPrimitives.ReadUInt32LittleEndian(frame) != Crc32.Compute(frame.AsSpan(8)))
            throw new InvalidDataException($"Invalid record checksum or frame in block {block} at {offset}.");
        var value = frame.AsMemory(SegmentLog.HeaderSize);
        if (value.Length <= MaxCachedValueSize) CacheValue(location, value);
        return value;
    }

    private void CacheValue(ValueLocation location, ReadOnlyMemory<byte> value)
    {
        while (_smallValues.Count >= CachedValues || _valueCacheBytes + value.Length > ValueCacheBudget)
            if (_smallValues.Remove(_cachedLocations.Dequeue(), out var evicted)) _valueCacheBytes -= evicted.Length;
        _smallValues.Add(location, value);
        _cachedLocations.Enqueue(location);
        _valueCacheBytes += value.Length;
    }

    public byte[]? Lookup(string file, string bucket, byte[] id)
    {
        var index = OpenExisting(file);
        if (index is null) return null;
        if (!_lookups.TryGetValue(file, out var command))
        {
            command = Command(index, "SELECT value FROM entries WHERE bucket = $bucket AND id = $id",
                ("$bucket", null), ("$id", null));
            _lookups.Add(file, command);
        }
        command.Parameters["$bucket"].Value = bucket;
        command.Parameters["$id"].Value = id;
        return command.ExecuteScalar() as byte[];
    }

    public IReadOnlyList<IndexRow> Scan(string file, string bucket, byte[]? lower = null, byte[]? upper = null,
        bool includeLower = true, bool includeUpper = false, int limit = int.MaxValue)
    {
        using var command = ScanCommand(file, bucket, "id, value", lower, upper, includeLower, includeUpper, limit);
        if (command is null) return [];
        using var reader = command.ExecuteReader();
        var rows = new List<IndexRow>();
        while (reader.Read()) rows.Add(new IndexRow((byte[])reader[0], (byte[])reader[1]));
        return rows;
    }

    public IReadOnlyList<byte[]> Keys(string file, string bucket, byte[]? lower, byte[]? upper)
    {
        using var command = ScanCommand(file, bucket, "id", lower, upper, true, false, int.MaxValue);
        if (command is null) return [];
        using var reader = command.ExecuteReader();
        var keys = new List<byte[]>();
        while (reader.Read()) keys.Add((byte[])reader[0]);
        return keys;
    }

    private SqliteCommand? ScanCommand(string file, string bucket, string columns, byte[]? lower, byte[]? upper,
        bool includeLower, bool includeUpper, int limit)
    {
        var index = OpenExisting(file);
        if (index is null || limit == 0) return null;
        var sql = $"SELECT {columns} FROM entries WHERE bucket = $bucket";
        if (lower is not null) sql += includeLower ? " AND sort >= $lower" : " AND sort > $lower";
        if (upper is not null) sql += includeUpper ? " AND sort <= $upper" : " AND sort < $upper";
        var command = Command(index, sql + " ORDER BY sort, id LIMIT $limit", ("$bucket", bucket), ("$limit", limit));
        if (lower is not null) command.Parameters.AddWithValue("$lower", lower);
        if (upper is not null) command.Parameters.AddWithValue("$upper", upper);
        return command;
    }

    public IReadOnlyList<(byte[] Id, float Score)> Search(string file, string query, int limit)
    {
        var index = OpenExisting(file);
        if (index is null || !HasFullText(file, index)) return [];
        using var command = Command(index, """
            SELECT entries.id, -terms.rank FROM terms JOIN entries ON entries.rowid = terms.rowid
            WHERE terms MATCH $query ORDER BY terms.rank, entries.id LIMIT $limit
            """, ("$query", query), ("$limit", limit));
        using var reader = command.ExecuteReader();
        var rows = new List<(byte[], float)>();
        while (reader.Read()) rows.Add(((byte[])reader[0], reader.GetFloat(1)));
        return rows;
    }

    public bool HasSort(string file, string bucket, byte[] id, byte[] sort)
    {
        var index = OpenExisting(file);
        if (index is null) return false;
        using var command = Command(index, "SELECT 1 FROM entries WHERE bucket = $bucket AND id = $id AND sort = $sort",
            ("$bucket", bucket), ("$id", id), ("$sort", sort));
        return command.ExecuteScalar() is not null;
    }

    public IReadOnlyList<IndexRow> ReadVectors(string file, string bucket, string model, byte[] sort)
    {
        var key = (file, model);
        if (_vectorCache.TryGetValue(key, out var cached)) return cached.Rows;
        var rows = Scan(file, bucket, sort, sort, includeUpper: true);
        var bytes = rows.Sum(row => (long)row.Id.Length + row.Value.Length + 64);
        if (bytes > VectorCacheBudget || rows.Count == 0) return rows;
        if (_vectorCacheBytes + bytes > VectorCacheBudget)
        {
            _vectorCache.Clear();
            _vectorCacheBytes = 0;
        }
        _vectorCache.Add(key, (rows, bytes));
        _vectorCacheBytes += bytes;
        return rows;
    }

    public void CheckReady()
    {
        if (_needsRecovery) Recover();
    }

    public void Commit(IReadOnlyList<IndexChange> changes)
    {
        if (changes.Count == 0) return;
        // Every offset becomes durable before any index refers to it. An abandoned write leaves only unused space.
        _blocks.Sync();
        var groups = changes.GroupBy(change => change.File).ToArray();
        if (groups.Length == 1)
        {
            Apply(groups[0]);
            return;
        }

        // Once the redo plan is durable, replay completes it even if only some B-trees were updated.
        _needsRecovery = true;
        Execute(_queue, "INSERT INTO pending (id, changes) VALUES (1, $changes)",
            ("$changes", JsonSerializer.SerializeToUtf8Bytes(changes)));
        foreach (var group in groups) Apply(group);
        ClearQueue();
    }

    private void Recover()
    {
        using var command = Command(_queue, "SELECT changes FROM pending WHERE id = 1");
        if (command.ExecuteScalar() is byte[] pending)
        {
            var changes = JsonSerializer.Deserialize<IndexChange[]>(pending) ?? throw new InvalidDataException("Invalid write queue.");
            foreach (var group in changes.GroupBy(change => change.File)) Apply(group);
        }
        ClearQueue();
    }

    private void ClearQueue()
    {
        Execute(_queue, "DELETE FROM pending WHERE id = 1");
        _needsRecovery = false;
    }

    private void Apply(IGrouping<string, IndexChange> changes)
    {
        var index = GetIndex(changes.Key);
        using var transaction = index.BeginTransaction();
        var hasTerms = HasFullText(changes.Key, index, transaction);
        var changesSearch = changes.Any(change => change.FullText);
        if (changesSearch)
            foreach (var key in _vectorCache.Keys.Where(key => key.File == changes.Key).ToArray())
                if (_vectorCache.Remove(key, out var cached)) _vectorCacheBytes -= cached.Bytes;
        if (!hasTerms && changesSearch)
        {
            Run("CREATE VIRTUAL TABLE terms USING fts5(text)");
            hasTerms = true;
        }

        using var put = Command(index, """
            INSERT INTO entries (bucket, id, sort, value) VALUES ($bucket, $id, $sort, $value)
            ON CONFLICT (bucket, id) DO UPDATE SET sort = excluded.sort, value = excluded.value
            """, ("$bucket", null), ("$id", null), ("$sort", null), ("$value", null));
        put.Transaction = transaction;

        foreach (var change in changes)
        {
            if (change.FullText)
                Run("DELETE FROM terms WHERE rowid IN (SELECT rowid FROM entries WHERE bucket = $bucket AND id = $id)",
                    ("$bucket", change.Bucket), ("$id", change.Id));

            if (change.Id is null)
                Run("DELETE FROM entries WHERE bucket = $bucket", ("$bucket", change.Bucket));
            else if (change.Value is null)
                Run("DELETE FROM entries WHERE bucket = $bucket AND id = $id", ("$bucket", change.Bucket), ("$id", change.Id));
            else
            {
                put.Parameters["$bucket"].Value = change.Bucket;
                put.Parameters["$id"].Value = change.Id;
                put.Parameters["$sort"].Value = change.Sort ?? change.Id;
                put.Parameters["$value"].Value = change.Value;
                put.ExecuteNonQuery();
                if (change.SearchText is { Length: > 0 })
                    Run("INSERT INTO terms (rowid, text) SELECT rowid, $text FROM entries WHERE bucket = $bucket AND id = $id",
                        ("$bucket", change.Bucket), ("$id", change.Id), ("$text", change.SearchText));
            }
        }
        transaction.Commit();
        _fullText[changes.Key] = hasTerms;

        void Run(string sql, params (string, object?)[] arguments)
        {
            using var command = Command(index, sql, arguments);
            command.Transaction = transaction;
            command.ExecuteNonQuery();
        }
    }

    private bool HasFullText(string file, SqliteConnection index, SqliteTransaction? transaction = null)
    {
        if (_fullText.TryGetValue(file, out var cached)) return cached;
        using var command = Command(index, "SELECT 1 FROM sqlite_schema WHERE type = 'table' AND name = 'terms'");
        command.Transaction = transaction;
        bool exists = command.ExecuteScalar() is not null;
        _fullText[file] = exists;
        return exists;
    }

    private SqliteConnection? OpenExisting(string file) =>
        _indexes.TryGetValue(file, out var open) ? open : File.Exists(IndexPath(file)) ? GetIndex(file) : null;

    private SqliteConnection GetIndex(string file)
    {
        if (_indexes.TryGetValue(file, out var open)) return open;
        var connection = Connect(IndexPath(file));
        try
        {
            Execute(connection, """
                CREATE TABLE IF NOT EXISTS entries (
                    bucket TEXT NOT NULL, id BLOB NOT NULL, sort BLOB NOT NULL, value BLOB NOT NULL,
                    PRIMARY KEY (bucket, id)
                );
                CREATE INDEX IF NOT EXISTS ordered ON entries (bucket, sort, id);
                """);
            _indexes.Add(file, connection);
            return connection;
        }
        catch
        {
            connection.Dispose();
            throw;
        }
    }

    private string IndexPath(string file)
    {
        if (file.Length is 0 or > 100 || file.Any(c => c is not (>= 'a' and <= 'z' or >= '0' and <= '9' or '-' or '_' or '~')))
            throw new InvalidDataException("Invalid index file name.");
        return Path.Combine(_directory, $"index_{file}.db");
    }

    private static SqliteConnection Connect(string path)
    {
        var connection = new SqliteConnection(new SqliteConnectionStringBuilder { DataSource = path, Pooling = false }.ToString());
        try
        {
            connection.Open();
            Execute(connection, "PRAGMA synchronous = EXTRA");
            return connection;
        }
        catch
        {
            connection.Dispose();
            throw;
        }
    }

    private static void Execute(SqliteConnection connection, string sql, params (string, object?)[] args)
    {
        using var command = Command(connection, sql, args);
        command.ExecuteNonQuery();
    }

    private static SqliteCommand Command(SqliteConnection connection, string sql, params (string, object?)[] args)
    {
        var command = connection.CreateCommand();
        command.CommandText = sql;
        foreach (var (name, value) in args) command.Parameters.AddWithValue(name, value ?? DBNull.Value);
        return command;
    }

    public void Dispose()
    {
        try
        {
            foreach (var lookup in _lookups.Values) lookup.Dispose();
            foreach (var index in _indexes.Values) index.Dispose();
            _queue.Dispose();
            _smallValues.Clear();
            _cachedLocations.Clear();
            _vectorCache.Clear();
        }
        finally
        {
            try { _blocks.Dispose(); }
            finally { _lock.Dispose(); }
        }
    }
}
