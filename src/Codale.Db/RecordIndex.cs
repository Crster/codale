using System.Linq.Expressions;

namespace Codale.Db;

public sealed class RecordIndexBuilder<T>
{
    private readonly CodaleDatabase _database;
    private readonly string _name;

    internal RecordIndexBuilder(CodaleDatabase database, string name)
    {
        _database = database;
        _name = CodaleDatabase.ValidateKey(name);
    }

    /// <summary>Backfills an index and persists its member selector so later writes update it after reopening.</summary>
    public RecordIndex<T, TKey> Build<TKey>(Expression<Func<T, TKey>> selector)
    {
        _database.Build(_name, selector);
        return new(_database, _name);
    }

    public IReadOnlyList<RecordIndexEntry<T, TKey>> Find<TKey>(Func<TKey, bool>? key = null, Func<T, bool>? filter = null) =>
        _database.FindIndex(_name, key, filter);
}

public sealed class RecordIndex<T, TKey>
{
    internal CodaleDatabase Database { get; }
    internal string Name { get; }

    internal RecordIndex(CodaleDatabase database, string name)
    {
        Database = database;
        Name = CodaleDatabase.ValidateKey(name);
    }

    public IReadOnlyList<RecordIndexEntry<T, TKey>> Find(Func<TKey, bool>? key = null, Func<T, bool>? filter = null) =>
        Database.FindIndex(Name, key, filter);

    /// <summary>Seeks directly to the bounds of the ordered B-tree.</summary>
    public IReadOnlyList<RecordIndexEntry<T, TKey>> FindRange(IndexBound<TKey>? lower = null, IndexBound<TKey>? upper = null,
        Func<T, bool>? filter = null) => Database.FindIndex(Name, null, filter, lower, upper);
}

public static class StringRecordIndex
{
    public static IReadOnlyList<RecordIndexEntry<T, string>> FindPrefix<T>(this RecordIndex<T, string> index,
        string prefix, Func<T, bool>? filter = null)
    {
        ArgumentNullException.ThrowIfNull(prefix);
        return index.Database.FindIndex<T, string>(index.Name, null, filter, prefix: prefix);
    }
}

public readonly record struct IndexBound<TKey>(TKey Value, bool Inclusive = true);

public sealed record RecordIndexEntry<T, TKey>(TKey IndexKey, string Key, T Value);
