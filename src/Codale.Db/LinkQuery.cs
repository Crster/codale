namespace Codale.Db;

public sealed class LinkEditor
{
    private readonly CodaleDatabase _database;
    private readonly string _name;
    private readonly bool _remove;

    internal LinkEditor(CodaleDatabase database, string name, bool remove)
    {
        _database = database;
        _name = name;
        _remove = remove;
    }

    public bool Key(RecordReference first, RecordReference second) => Keys(first, second) > 0;

    /// <summary>Connects or disconnects successive records in both directions, as one operation.</summary>
    public int Keys(params RecordReference[] records) => _database.EditLinks(_name, records, _remove);
}

public sealed class LinkQuery
{
    private readonly CodaleDatabase _database;
    private readonly RecordReference _record;

    internal LinkQuery(CodaleDatabase database, RecordReference record)
    {
        _database = database;
        _record = record;
    }

    public IReadOnlyList<LinkedRecord> Match(params LinkFilter[] filters) => _database.Match(_record, filters);
}

public sealed class LinkFilter
{
    private LinkFilter(string name, string type, Func<ReadOnlyMemory<byte>, bool> predicate)
    {
        Name = name;
        Type = type;
        Predicate = predicate;
    }

    internal string Name { get; }
    internal string Type { get; }
    internal Func<ReadOnlyMemory<byte>, bool> Predicate { get; }

    public static LinkFilter Where<T>(string name, Func<T, bool>? predicate = null) =>
        new(CodaleDatabase.ValidateKey(name), CodaleDatabase.TypeName<T>(),
            predicate is null ? _ => true : bytes => predicate(CodaleDatabase.DeserializeRecord<T>(bytes)!));
}

public sealed class LinkedRecord
{
    private readonly CodaleDatabase _database;
    private readonly string _type;
    private readonly ReadOnlyMemory<byte> _value;

    internal LinkedRecord(CodaleDatabase database, string relation, string type, string key, ReadOnlyMemory<byte> value)
    {
        _database = database;
        _type = type;
        _value = value;
        Relation = relation;
        Key = key;
    }

    public string Relation { get; }
    public string Key { get; }

    public bool TryGet<T>(out T? value)
    {
        var matches = _type == CodaleDatabase.TypeName<T>();
        value = matches ? CodaleDatabase.DeserializeRecord<T>(_value) : default;
        return matches;
    }

    public RecordKey<T>? Record<T>() => _type == CodaleDatabase.TypeName<T>() ? new RecordKey<T>(_database, Key) : null;
}
