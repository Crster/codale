namespace Codale.Storage;

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

    // key-value

    public void Write(string value) => _db.WriteText(_path, value);

    /// <returns>The value, or null when nothing was written here.</returns>
    public string? Read() => _db.ReadText(_path);

    public bool Exists() => _db.Exists(_path);

    /// <summary>Removes this node and everything beneath it.</summary>
    public bool Remove() => _db.Remove(_path);

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

    /// <summary>Stores each text as a child of this node (named by the text itself) and indexes it for <see cref="Query"/>.</summary>
    public void Write(IEnumerable<string> texts) => _db.WriteTexts(_path, texts);

    /// <summary>The entries at or below this node most similar to <paramref name="text"/>, best first.</summary>
    public IReadOnlyList<DbHit> Query(string text, int k = 5) => _db.Query(_path, text, k);

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
