using Codale.Db.Search;

namespace Codale.Db;

public enum SemanticMode { Bm25, Vector, Hybrid }

public sealed class SemanticIndex
{
    private readonly CodaleDatabase _database;
    private readonly string _name;

    internal SemanticIndex(CodaleDatabase database, string name)
    {
        _database = database;
        _name = name;
    }

    public SemanticKey Key(IEnumerable<string> tags, float[]? vector = null, SemanticMode mode = SemanticMode.Hybrid)
    {
        ArgumentNullException.ThrowIfNull(tags);
        if (!Enum.IsDefined(mode)) throw new ArgumentOutOfRangeException(nameof(mode));
        return new(_database, _name, string.Join(' ', tags), vector?.ToArray(), mode);
    }

    public IReadOnlyList<SemanticHit> Find(string query, int limit = 10)
    {
        ArgumentNullException.ThrowIfNull(query);
        return _database.FindSemantic(_name, query, null, limit);
    }

    public IReadOnlyList<SemanticHit> Find(float[] vector, int limit = 10)
    {
        ArgumentNullException.ThrowIfNull(vector);
        return _database.FindSemantic(_name, null, vector.ToArray(), limit);
    }

    public bool Remove(RecordReference record) => _database.RemoveSemantic(_name, record);

    public bool HasVector(RecordReference record) => _database.HasVector(_name, record);

    public IReadOnlyList<SemanticHit> FindKeywords(IEnumerable<KeywordTerm> terms, int limit = 10) =>
        _database.FindKeywords(_name, terms, limit);
}

public sealed class SemanticHit
{
    private readonly CodaleDatabase _database;
    private readonly string _type;

    internal SemanticHit(CodaleDatabase database, string type, string key, float score)
    {
        _database = database;
        _type = type;
        Key = key;
        Score = score;
    }

    public string Key { get; }
    public float Score { get; }

    public RecordKey<T>? Record<T>() => CodaleDatabase.TypeName<T>() == _type ? new RecordKey<T>(_database, Key) : null;
}

public sealed class SemanticKey
{
    private readonly CodaleDatabase _database;
    private readonly string _name;
    private readonly string _tags;
    private readonly float[]? _vector;
    private readonly SemanticMode _mode;

    internal SemanticKey(CodaleDatabase database, string name, string tags, float[]? vector, SemanticMode mode)
    {
        _database = database;
        _name = name;
        _tags = tags;
        _vector = vector;
        _mode = mode;
    }

    internal CodaleDatabase.PreparedSearch Prepare() => _database.PrepareSemantic(_name, _tags, _vector, _mode);

    public void Value(RecordReference record) => _database.IndexSemantic(Prepare(), record);
}
