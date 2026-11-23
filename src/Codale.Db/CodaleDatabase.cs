using System.Text.Json;
using System.Text.Json.Nodes;

using Codale.Db.Engine;
using Codale.Db.Search;
using Microsoft.Data.Sqlite;

namespace Codale.Db;

public sealed class CodaleDatabaseOptions
{
    /// <summary>Maximum size of a record block, including record headers.</summary>
    public int RecordBlockBytes { get; init; } = 256 << 20;
    public TimeSpan LockTimeout { get; init; }
    public IEmbedder? Embedder { get; init; }
}

/// <summary>An embedded store for typed records, ordered and semantic indexes, and named links.</summary>
public sealed partial class CodaleDatabase : IDisposable
{
    private static readonly JsonSerializerOptions Json = new()
    {
        IncludeFields = true,
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
        PropertyNameCaseInsensitive = true,
    };

    private readonly Lock _gate = new();
    private readonly Lock _embeddingGate = new();
    private readonly FolderStorage _storage;
    private readonly Dictionary<string, IndexDefinition[]> _definitions = new(StringComparer.Ordinal);
    private const string RecordPrefix = "records:";
    private const int AddressCacheLimit = 32_768;
    private readonly Dictionary<(string Type, string Key), byte[]> _addresses = [];
    private readonly IEmbedder _embedder;
    private bool _disposed;

    public CodaleDatabase(string path, CodaleDatabaseOptions? options = null)
    {
        options ??= new CodaleDatabaseOptions();
        if (options.RecordBlockBytes is <= SegmentLog.HeaderSize or > 256 << 20)
            throw new ArgumentOutOfRangeException(nameof(options), "Record blocks must be larger than a record header and at most 256 MiB.");
        if (options.LockTimeout < TimeSpan.Zero) throw new ArgumentOutOfRangeException(nameof(options), "Lock timeout must be nonnegative.");
        _embedder = options.Embedder ?? new HashedEmbedder();
        try { _storage = new FolderStorage(path, options.RecordBlockBytes, options.LockTimeout); }
        catch (SqliteException error) { throw StorageError(error); }
    }

    public RecordCollection<T> Record<T>() => new(this);
    public RecordCollection<T> Open<T>() => Record<T>();

    internal static string TypeName(Type type)
    {
        if (type.IsArray) return $"{TypeName(type.GetElementType()!)}[{new string(',', type.GetArrayRank() - 1)}]";
        if (type.IsConstructedGenericType)
            return TypeName(type.GetGenericTypeDefinition()) + "[" + string.Join(',', type.GetGenericArguments().Select(TypeName)) + "]";
        return $"{type.Assembly.GetName().Name}:{type.FullName}";
    }

    internal static string TypeName<T>() => RecordTypeName<T>.Value;

    private static class RecordTypeName<T>
    {
        public static readonly string Value = TypeName(typeof(T));
    }

    internal static string ValidateKey(string key)
    {
        ArgumentException.ThrowIfNullOrEmpty(key);
        // Reject malformed UTF-16 so JSON and B-tree encodings identify the same record.
        for (var i = 0; i < key.Length; i++)
        {
            if (!char.IsSurrogate(key[i])) continue;
            if (!char.IsSurrogatePair(key, i)) throw new ArgumentException("The key contains an unpaired surrogate.", nameof(key));
            i++;
        }
        return key;
    }

    private static string RecordBucket(string type) => RecordPrefix + type;
    private static byte[] Id(string key) => OrderedKey.Text(key);

    private byte[]? Address(string type, string key)
    {
        var identity = (type, key);
        if (_addresses.TryGetValue(identity, out var cached)) return cached;
        var address = _storage.Lookup(FolderStorage.Records, RecordBucket(type), Id(key));
        if (address is not null) RememberAddress(identity, address);
        return address;
    }

    private void RememberAddress((string Type, string Key) identity, byte[] address)
    {
        if (_addresses.Count >= AddressCacheLimit && !_addresses.ContainsKey(identity)) _addresses.Clear();
        _addresses[identity] = address;
    }

    private void CommitRecords(List<IndexChange> changes)
    {
        var notifications = CaptureChanges(changes);
        try { _storage.Commit(changes); }
        catch
        {
            _addresses.Clear();
            throw;
        }
        foreach (var change in changes)
        {
            if (RecordIdentity(change) is not { } identity) continue;
            if (change.Value is { } address) RememberAddress(identity, address);
            else _addresses.Remove(identity);
        }
        foreach (var notification in notifications) _watches.Publish(notification.Type, notification);
    }

    private static (string Type, string Key)? RecordIdentity(IndexChange change) =>
        change.File == FolderStorage.Records && change.Bucket.StartsWith(RecordPrefix, StringComparison.Ordinal)
            ? (change.Bucket[RecordPrefix.Length..], OrderedKey.ReadText(change.Id!)) : null;

    private TResult Access<TResult>(Func<TResult> action)
    {
        if (_gate.IsHeldByCurrentThread) throw new InvalidOperationException("A database operation cannot reenter the storage transaction.");
        lock (_gate)
        {
            ObjectDisposedException.ThrowIf(_disposed, this);
            try
            {
                _storage.CheckReady();
                return action();
            }
            catch (SqliteException error) { throw StorageError(error); }
        }
    }

    private static IOException StorageError(SqliteException error) => new("The database index could not be accessed. " + error.Message, error);

    internal bool TryGet<T>(string key, out T? value)
    {
        var bytes = Access<ReadOnlyMemory<byte>?>(() =>
        {
            if (Address(TypeName<T>(), key) is { } address) return _storage.ReadValue(address);
            return default;
        });
        value = bytes is null ? default : DeserializeRecord<T>(bytes.Value);
        return bytes is not null;
    }

    internal void Put<T>(string key, T value) => PutMany([new KeyValuePair<string, T>(key, value)]);

    internal void PutMany<T>(IEnumerable<KeyValuePair<string, T>> values)
    {
        ArgumentNullException.ThrowIfNull(values);
        var batch = values.Select(item => (Key: ValidateKey(item.Key), Value: JsonSerializer.SerializeToUtf8Bytes(item.Value, Json))).ToArray();
        var type = TypeName<T>();
        Access(() =>
        {
            var definitions = Definitions(type);
            var changes = new List<IndexChange>();
            foreach (var item in batch) StageValue(type, item.Key, item.Value, definitions, changes);
            CommitRecords(changes);
            return true;
        });
    }

    internal bool Patch<T>(string key, object patch)
    {
        ArgumentNullException.ThrowIfNull(patch);
        if (JsonSerializer.SerializeToNode(patch, Json) is not JsonObject fields)
            throw new ArgumentException("A patch must be a JSON object.", nameof(patch));
        var type = TypeName<T>();
        return Access(() =>
        {
            if (Address(type, key) is not { } address) return false;
            var node = JsonNode.Parse(_storage.ReadValue(address).Span);
            if (node is not JsonObject record) throw new InvalidOperationException("Only object records can be patched.");
            foreach (var (name, value) in fields)
            {
                // Keep the original JSON member spelling for persisted member selectors.
                var member = record.ContainsKey(name) ? name : record.Select(field => field.Key)
                    .FirstOrDefault(existing => StringComparer.OrdinalIgnoreCase.Equals(existing, name)) ?? name;
                record[member] = value?.DeepClone();
            }
            byte[] updated = JsonSerializer.SerializeToUtf8Bytes(record, Json);
            _ = JsonSerializer.Deserialize<T>(updated, Json);
            var changes = new List<IndexChange>();
            StageValue(type, key, updated, Definitions(type), changes);
            CommitRecords(changes);
            return true;
        });
    }

    internal bool TryDelete<T>(string key, out T? value)
    {
        var type = TypeName<T>();
        var result = Access<(bool Found, T? Value)>(() =>
        {
            if (Address(type, key) is not { } address) return default;
            var deleted = DeserializeRecord<T>(_storage.ReadValue(address));
            var changes = new List<IndexChange>();
            StageDelete(type, key, changes);
            CommitRecords(changes);
            return (true, deleted);
        });
        value = result.Value;
        return result.Found;
    }

    private void StageDelete(string type, string key, List<IndexChange> changes)
    {
        var id = Id(key);
        var reference = new StoredReference(type, key);
        changes.Add(new(FolderStorage.Records, RecordBucket(type), id));
        foreach (var definition in Definitions(type)) changes.Add(new(definition.File, SecondaryBucket(type), id));
        StageSemanticRemovals(reference, changes);
        StageLinkRemovals(reference, changes);
    }

    private void StageValue(string type, string key, byte[] bytes, IndexDefinition[] definitions, List<IndexChange> changes)
    {
        var id = Id(key);
        if (definitions.Length > 0)
        {
            using var document = JsonDocument.Parse(bytes);
            foreach (var definition in definitions) changes.Add(definition.Project(type, id, document.RootElement));
        }
        changes.Add(new(FolderStorage.Records, RecordBucket(type), id, Value: _storage.AppendValue(bytes)));
    }

    internal IReadOnlyList<RecordEntry<T>> FindRecords<T>(Func<string, bool>? key, Func<T, bool>? filter, string? prefix = null,
        string? from = null, string? until = null, int limit = int.MaxValue)
    {
        var (lower, upper) = Bounds(prefix, from, until);
        var type = TypeName<T>();
        var rows = Access(() =>
        {
            var snapshot = _storage.Scan(FolderStorage.Records, RecordBucket(type), lower, upper, limit: limit);
            return snapshot.Select(row =>
            {
                var key = OrderedKey.ReadText(row.Id);
                RememberAddress((type, key), row.Value);
                return (Key: key, Address: row.Value);
            }).ToArray();
        });
        var matches = new List<RecordEntry<T>>();
        foreach (var row in rows)
        {
            if (key is not null && !key(row.Key)) continue;
            var value = Read<T>(row.Address);
            if (filter is null || filter(value)) matches.Add(new(row.Key, value));
        }
        return matches;
    }

    internal IReadOnlyList<string> RecordKeys<T>(string prefix)
    {
        var (lower, upper) = Bounds(prefix, null, null);
        return Access(() => _storage.Keys(FolderStorage.Records, RecordBucket(TypeName<T>()), lower, upper)
            .Select(OrderedKey.ReadText).ToArray());
    }

    private static (byte[]? Lower, byte[]? Upper) Bounds(string? prefix, string? from, string? until)
    {
        var lower = from is not null ? Id(from) : prefix is not null ? Id(prefix) : null;
        var upper = until is not null ? Id(until) : prefix is not null ? OrderedKey.AfterPrefix(Id(prefix)) : null;
        return (lower, upper);
    }

    private T Read<T>(byte[] address) => DeserializeRecord<T>(Access(() => _storage.ReadValue(address)))!;

    internal static T? DeserializeRecord<T>(ReadOnlyMemory<byte> bytes) => JsonSerializer.Deserialize<T>(bytes.Span, Json);

    public void Dispose()
    {
        if (_gate.IsHeldByCurrentThread) throw new InvalidOperationException("A database operation is in progress on this thread.");
        lock (_gate)
        {
            if (_disposed) return;
            _disposed = true;
            _watches.Dispose();
            _addresses.Clear();
            _storage.Dispose();
        }
    }
}
