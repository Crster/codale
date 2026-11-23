namespace Codale.Db;

public sealed class RecordCollection<T>
{
    private readonly CodaleDatabase _database;

    internal RecordCollection(CodaleDatabase database) => _database = database;

    public RecordKey<T> Key(string key) => new(_database, CodaleDatabase.ValidateKey(key));

    /// <summary>Writes a set of records as one transaction.</summary>
    public void PutMany(IEnumerable<KeyValuePair<string, T>> values) => _database.PutMany(values);

    public IReadOnlyList<RecordEntry<T>> Find(Func<string, bool>? key = null, Func<T, bool>? filter = null) =>
        _database.FindRecords(key, filter);

    public IReadOnlyList<RecordEntry<T>> FindPrefix(string prefix, Func<T, bool>? filter = null)
    {
        ArgumentNullException.ThrowIfNull(prefix);
        return _database.FindRecords(null, filter, prefix);
    }

    /// <summary>Reads up to <paramref name="limit"/> records in ordinal key order within [from, until).</summary>
    public IReadOnlyList<RecordEntry<T>> FindRange(string from, string until, int limit = int.MaxValue)
    {
        ArgumentNullException.ThrowIfNull(from);
        ArgumentNullException.ThrowIfNull(until);
        ArgumentOutOfRangeException.ThrowIfNegative(limit);
        return _database.FindRecords<T>(null, null, from: from, until: until, limit: limit);
    }

    public IReadOnlyList<string> Keys(string prefix = "") => _database.RecordKeys<T>(prefix);

    /// <summary>Watches committed changes of this type in order. Callbacks run off the writer's thread.</summary>
    public IDisposable Watch(Action<RecordEntry<T>, RecordAction> handler) => _database.Watch(handler);

    public RecordIndexBuilder<T> Index(string name) => new(_database, name);
    public RecordIndex<T, TKey> Index<TKey>(string name) => new(_database, name);
}

public sealed record RecordEntry<T>(string Key, T Value);

/// <summary>A reference to a typed key in an open database.</summary>
public abstract class RecordReference
{
    private protected RecordReference(CodaleDatabase database, string type, string key)
    {
        Database = database;
        Type = type;
        Key = key;
    }

    internal CodaleDatabase Database { get; }
    internal string Type { get; }
    public string Key { get; }
}

public sealed class RecordKey<T> : RecordReference
{
    internal RecordKey(CodaleDatabase database, string key) : base(database, CodaleDatabase.TypeName<T>(), key) { }

    public void Value(T value) => Database.Put(Key, value);
    public bool Set(object patch) => Database.Patch<T>(Key, patch);
    /// <summary>Deletes the key and returns its old value, or default when it is absent.</summary>
    public T? Delete() => TryDelete(out var value) ? value : default;
    public bool TryDelete(out T? value) => Database.TryDelete(Key, out value);
    public T? Get() => TryGet(out var value) ? value : default;
    public bool TryGet(out T? value) => Database.TryGet(Key, out value);
}
