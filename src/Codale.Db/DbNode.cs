using System.Text.Json;

namespace Codale.Db;

/// <summary>
/// A path inside a <see cref="CodaleDb"/>. Creating one is free (just a string); every terminal call does one
/// lookup. <c>db.Open("setting").Open("ai provider").Open("claude KEY").Write("xxxxx")</c>.
/// </summary>
public readonly struct DbNode
{
    private readonly CodaleDb _db;
    private readonly string _path;

    internal DbNode(CodaleDb db, string path)
    {
        _db = db;
        _path = path;
    }

    internal static string Validate(string name)
    {
        ArgumentException.ThrowIfNullOrEmpty(name);
        if (name.Contains(CodaleDb.Sep)) throw new ArgumentException("A name cannot contain the unit separator (U+001F).", nameof(name));
        return name;
    }

    public DbPath Location => DbPath.FromRaw(_path);

    /// <summary>The last segment of the path.</summary>
    public string Name => _path[(_path.LastIndexOf(CodaleDb.Sep) + 1)..];

    public DbNode Open(string name) => new(_db, _path + CodaleDb.Sep + Validate(name));

    /// <summary>Names of the direct children, sorted.</summary>
    public IReadOnlyList<string> Children() => _db.Children(_path);

    public int ChildCount() => _db.ChildCount(_path);

    /// <summary>Every node below this one that holds a value (in path order when <paramref name="sorted"/>).</summary>
    public IReadOnlyList<DbNode> Descendants(bool sorted = false) => _db.Descendants(_path, sorted);

    // key-value

    public void Write(string value) => _db.WriteText(_path, value);

    /// <returns>The value, or null when nothing was written here.</returns>
    public string? Read() => _db.ReadText(_path);

    public bool Exists() => _db.Exists(_path);

    /// <summary>Removes this node and everything beneath it.</summary>
    public bool Remove() => _db.Remove(_path);

    /// <summary>Stores <paramref name="value"/> as JSON.</summary>
    public void WriteJson<T>(T value) => _db.WriteText(_path, JsonSerializer.Serialize(value));

    public T? ReadJson<T>() => Read() is { } json ? JsonSerializer.Deserialize<T>(json) : default;

    // protection

    /// <summary>Stores the value encrypted (AES-256-GCM) with the key set by <see cref="CodaleDb.Protect(string)"/>.</summary>
    public void WriteProtect(string value) => _db.WriteProtected(_path, value);

    public string? ReadProtect() => _db.ReadProtected(_path);

    // blobs

    public void WriteBlob(ReadOnlySpan<byte> data) => _db.WriteBlob(_path, data);

    public void WriteBlob(Stream data)
    {
        using var memory = new MemoryStream();
        data.CopyTo(memory);
        _db.WriteBlob(_path, memory.ToArray());
    }

    public byte[]? ReadBlob() => _db.ReadBlob(_path);

    // semantic search

    /// <summary>Stores each text as a child of this node (named by the text itself) and embeds it for <see cref="Query"/>.</summary>
    public void Write(IEnumerable<string> texts) => _db.WriteTexts(_path, texts);

    /// <summary>Attaches the embedding of <paramref name="text"/> to this node; the node's value is untouched.</summary>
    public void Embed(string text) => _db.Embed(_path, text);

    /// <summary>True when this node has an embedding made by the database's current embedder.</summary>
    public bool HasVector() => _db.HasCurrentVector(_path);

    /// <summary>Forgets the embeddings below this node that a different embedder made (they cannot be compared).</summary>
    public void DropStaleVectors() => _db.DropStaleVectors(_path);

    /// <summary>The embedded nodes at or below this one most similar to <paramref name="text"/>, best first.</summary>
    public IReadOnlyList<DbHit> Query(string text, int k = 5) => _db.Query(_path, text, k);

    // keyword search

    /// <summary>Indexes the words of this node under a named field, replacing the field's earlier words.</summary>
    public void Index(string field, IEnumerable<string> words) => _db.Index(_path, field, words);

    /// <summary>The nodes at or below this one whose <paramref name="field"/> words match <paramref name="terms"/> (any of them), best BM25 first.</summary>
    public IReadOnlyList<DbHit> Match(string field, IReadOnlyList<KeywordTerm> terms, int k = 10) => _db.Match(_path, field, terms, k);

    // graph

    /// <summary>Everything stored beneath this node (and the nodes it links to), optionally narrowed by a case-insensitive substring.</summary>
    public IReadOnlyList<DbPath> Find(string? filter = null) => _db.Find(_path, filter);

    public void Link(string relation, DbNode target) => _db.Link(_path, relation, target._path);

    public bool Unlink(string relation, DbNode target) => _db.Unlink(_path, relation, target._path);

    public IReadOnlyList<DbPath> Links(string? relation = null) => _db.Links(_path, relation);

    // events and pub/sub

    /// <summary>Calls <paramref name="handler"/> after every committed write or removal at or below this node.</summary>
    public IDisposable Watch(Action<DbEvent> handler) => _db.Watch(_path, handler);

    /// <returns>How many subscribers received the message.</returns>
    public int Publish(string message) => _db.Publish(_path, message);

    /// <summary>Receives messages published at or below this node. Dispose to unsubscribe.</summary>
    public IDisposable Subscribe(Action<DbMessage> handler) => _db.Subscribe(_path, handler);

    public override string ToString() => Location.ToString();
}
