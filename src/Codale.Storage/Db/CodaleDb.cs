using System.Buffers.Binary;
using System.IO.Compression;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;

using Codale.Core.Features;

namespace Codale.Storage;

/// <summary>
/// An embedded multi-model database addressed by paths: <c>db.Open("setting").Open("ai provider").Write("x")</c>.
/// One append-only log backs a key-value store, a semantic (vector) index, a graph of named links, compressed
/// blobs, change events and pub/sub. One process owns a database directory at a time.
/// </summary>
public sealed class CodaleDb : IDisposable
{
    internal const char Sep = '\u001f';
    private const int CacheLimit = 1024;
    private const int MaxNameLength = 4096;

    private readonly CodaleDbOptions _options;
    private readonly string _dir;
    private readonly FileStream _directoryLock;
    private readonly SegmentLog _log;
    private readonly ReaderWriterLockSlim _lock = new();
    private readonly TNode _root = new("", null);
    private readonly IEmbedder _embedder;
    private readonly Hub<DbEvent> _events = new((sub, path, e) => e.Kind == DbEventKind.Removed
        ? IsUnder(path, sub) || IsUnder(sub, path)
        : IsUnder(path, sub));
    private readonly Hub<DbMessage> _messages = new((sub, path, _) => IsUnder(path, sub));
    private readonly Dictionary<string, MatrixCache> _matrices = [];
    private readonly Timer? _flushTimer;
    private byte[]? _masterKey;
    private long _dead;
    private long _version;
    private bool _disposed;

    private sealed record MatrixCache(long Version, TNode[] Nodes, QuantizedMatrix Matrix);

    private sealed record KeyFile(string Kind, string Salt, string Wrapped);

    public CodaleDb(string directory, CodaleDbOptions? options = null)
    {
        _options = options ?? new CodaleDbOptions();
        _embedder = _options.Embedder ?? new HashedEmbedder();
        _dir = Path.GetFullPath(directory);
        Directory.CreateDirectory(_dir);
        // A second process (or a second instance) on the same directory would corrupt the log: refuse it.
        _directoryLock = new FileStream(Path.Combine(_dir, "LOCK"), FileMode.OpenOrCreate, FileAccess.ReadWrite, FileShare.None);
        _log = new SegmentLog(_dir, _options.SegmentBytes);
        try
        {
            _log.Open(Replay);
        }
        catch
        {
            _log.Dispose();
            _directoryLock.Dispose();
            throw;
        }
        if (_options.Flush == FlushMode.Interval)
            _flushTimer = new Timer(_ => Flush(), null, _options.FlushInterval, _options.FlushInterval);
    }

    /// <summary>Starts a path. Free: no I/O happens until a terminal call such as <c>Write</c> or <c>Read</c>.</summary>
    public DbNode Open(string name) => new(this, DbNode.Validate(name));

    // ---------------------------------------------------------------- protection

    /// <summary>Protects <c>WriteProtect</c>/<c>ReadProtect</c> values with a passphrase.</summary>
    public void Protect(string passphrase) => Protect(KeyProvider.Passphrase(passphrase));

    /// <summary>Protects values with the RSA key in a PEM file.</summary>
    public void Protect(Uri pemFile) => Protect(KeyProvider.Pem(pemFile));

    /// <summary>
    /// Unlocks (or, the first time, creates) the master key using <paramref name="provider"/>.
    /// A database keeps the provider kind it was first protected with.
    /// </summary>
    /// <exception cref="UnauthorizedAccessException">Wrong passphrase / key.</exception>
    public void Protect(KeyProvider provider)
    {
        ArgumentNullException.ThrowIfNull(provider);
        var file = Path.Combine(_dir, "protect.json");
        byte[] master;
        if (File.Exists(file))
        {
            var stored = JsonSerializer.Deserialize<KeyFile>(File.ReadAllText(file)) ?? throw new InvalidDataException("protect.json is empty.");
            if (stored.Kind != provider.Kind) throw new InvalidOperationException($"This database is protected with '{stored.Kind}', not '{provider.Kind}'.");
            try
            {
                master = provider.Unwrap(Convert.FromBase64String(stored.Wrapped), Convert.FromBase64String(stored.Salt));
            }
            catch (CryptographicException ex)
            {
                throw new UnauthorizedAccessException("The key does not unlock this database.", ex);
            }
        }
        else
        {
            master = RandomNumberGenerator.GetBytes(32);
            var salt = RandomNumberGenerator.GetBytes(16);
            var wrapped = provider.Wrap(master, salt);
            var temp = file + ".tmp";
            File.WriteAllText(temp, JsonSerializer.Serialize(new KeyFile(provider.Kind, Convert.ToBase64String(salt), Convert.ToBase64String(wrapped))));
            File.Move(temp, file, overwrite: true);
        }
        if (_masterKey is not null) CryptographicOperations.ZeroMemory(_masterKey);
        _masterKey = master;
    }

    // ---------------------------------------------------------------- key-value

    internal string? ReadText(string path)
    {
        _lock.EnterReadLock();
        try
        {
            var value = FindNode(path, create: false)?.Value;
            if (value is null) return null;
            if (value.Flags != 0) throw new InvalidOperationException("This value is protected or a blob; use ReadProtect()/ReadBlob().");
            return Encoding.UTF8.GetString(ReadRaw(value));
        }
        finally { _lock.ExitReadLock(); }
    }

    internal bool Exists(string path)
    {
        _lock.EnterReadLock();
        try { return FindNode(path, create: false)?.Value is not null; }
        finally { _lock.ExitReadLock(); }
    }

    internal void WriteText(string path, string value)
    {
        ArgumentNullException.ThrowIfNull(value);
        WriteBytes(path, Encoding.UTF8.GetBytes(value), 0, value);
    }

    internal void WriteProtected(string path, string value)
    {
        ArgumentNullException.ThrowIfNull(value);
        var key = _masterKey ?? throw new InvalidOperationException("Call db.Protect(...) before WriteProtect().");
        WriteBytes(path, RecordCipher.Seal(key, path, Encoding.UTF8.GetBytes(value)), ValueFlags.Protected, null);
    }

    internal string? ReadProtected(string path)
    {
        var key = _masterKey ?? throw new InvalidOperationException("Call db.Protect(...) before ReadProtect().");
        _lock.EnterReadLock();
        try
        {
            var value = FindNode(path, create: false)?.Value;
            if (value is null) return null;
            if ((value.Flags & ValueFlags.Protected) == 0) throw new InvalidOperationException("This value was not written with WriteProtect().");
            return Encoding.UTF8.GetString(RecordCipher.Open(key, path, ReadRaw(value)));
        }
        finally { _lock.ExitReadLock(); }
    }

    private void WriteBytes(string path, ReadOnlySpan<byte> value, byte flags, string? eventValue)
    {
        _lock.EnterWriteLock();
        try
        {
            ApplyPut(path, AppendPut(path, value, flags));
            Emit(path, DbEventKind.Written, eventValue);
            Committed();
        }
        finally { _lock.ExitWriteLock(); }
    }

    internal bool Remove(string path)
    {
        _lock.EnterWriteLock();
        try
        {
            if (FindNode(path, create: false) is null) return false;
            var append = _log.Append(RecordType.Remove, 0, Encoding.UTF8.GetBytes(path));
            ApplyRemove(path, append.Size);
            Emit(path, DbEventKind.Removed, null);
            Committed();
            return true;
        }
        finally { _lock.ExitWriteLock(); }
    }

    internal IReadOnlyList<string> Children(string path)
    {
        _lock.EnterReadLock();
        try
        {
            var children = FindNode(path, create: false)?.Children;
            return children is null ? [] : children.Keys.Order(StringComparer.Ordinal).ToList();
        }
        finally { _lock.ExitReadLock(); }
    }

    // ---------------------------------------------------------------- blobs

    internal void WriteBlob(string path, ReadOnlySpan<byte> data)
    {
        var buffer = new byte[4 + BrotliEncoder.GetMaxCompressedLength(data.Length)];
        BinaryPrimitives.WriteInt32LittleEndian(buffer, data.Length);
        if (!BrotliEncoder.TryCompress(data, buffer.AsSpan(4), out var written, Math.Clamp(_options.BlobQuality, 0, 11), 22))
            throw new InvalidOperationException("Brotli compression failed.");
        WriteBytes(path, buffer.AsSpan(0, 4 + written), ValueFlags.Blob, null);
    }

    internal byte[]? ReadBlob(string path)
    {
        _lock.EnterReadLock();
        try
        {
            var value = FindNode(path, create: false)?.Value;
            if (value is null) return null;
            if ((value.Flags & ValueFlags.Blob) == 0) throw new InvalidOperationException("This value was not written with WriteBlob().");
            var raw = ReadRaw(value);
            var output = new byte[BinaryPrimitives.ReadInt32LittleEndian(raw)];
            if (!BrotliDecoder.TryDecompress(raw.AsSpan(4), output, out var written) || written != output.Length)
                throw new InvalidDataException("Blob is corrupt.");
            return output;
        }
        finally { _lock.ExitReadLock(); }
    }

    // ---------------------------------------------------------------- semantic

    internal void WriteTexts(string path, IEnumerable<string> texts)
    {
        var items = texts
            .Where(t => !string.IsNullOrWhiteSpace(t))
            .Distinct(StringComparer.Ordinal)
            .Select(t => (Path: path + Sep + DbNode.Validate(t), Text: t, Vector: VectorMath.Quantize(_embedder.Embed(t))))
            .ToList();
        if (items.Count == 0) return;

        _lock.EnterWriteLock();
        try
        {
            foreach (var (itemPath, text, vector) in items)
            {
                ApplyPut(itemPath, AppendPut(itemPath, Encoding.UTF8.GetBytes(text), 0));
                ApplyVector(itemPath, _embedder.Model, vector, AppendVector(itemPath, _embedder.Model, vector));
                Emit(itemPath, DbEventKind.Written, text);
            }
            Committed();
        }
        finally { _lock.ExitWriteLock(); }
    }

    internal IReadOnlyList<DbHit> Query(string path, string text, int k)
    {
        ArgumentNullException.ThrowIfNull(text);
        if (k <= 0) return [];
        var query = _embedder.Embed(text);

        for (var attempt = 0; ; attempt++)
        {
            var reembed = false;
            _lock.EnterReadLock();
            try
            {
                var start = FindNode(path, create: false);
                if (start is null) return [];

                MatrixCache? cache;
                lock (_matrices) _matrices.TryGetValue(path, out cache);
                if (cache is null || cache.Version != Volatile.Read(ref _version))
                {
                    var nodes = new List<TNode>();
                    Collect(start, nodes, node => node.Vector is not null);
                    if (attempt == 0 && nodes.Any(n => n.VectorModel != _embedder.Model))
                    {
                        reembed = true;
                    }
                    else
                    {
                        nodes = nodes.Where(n => n.VectorModel == _embedder.Model).ToList();
                        var rows = nodes.Select((n, i) => ((long)i, n.Vector!)).ToList();
                        cache = new MatrixCache(Volatile.Read(ref _version), [.. nodes], QuantizedMatrix.From(rows));
                        lock (_matrices) _matrices[path] = cache;
                    }
                }

                if (!reembed)
                {
                    return cache!.Matrix.Top(query, k)
                        .Where(r => r.Score > 0)
                        .Select(r =>
                        {
                            var node = cache.Nodes[r.Id];
                            return new DbHit(DbPath.FromRaw(node.PathString()), node.Name, r.Score);
                        })
                        .ToList();
                }
            }
            finally { _lock.ExitReadLock(); }

            ReembedStale(path);
        }
    }

    /// <summary>Entries embedded by an older model are re-embedded from their text (the entry's name) on first query.</summary>
    private void ReembedStale(string path)
    {
        _lock.EnterWriteLock();
        try
        {
            var start = FindNode(path, create: false);
            if (start is null) return;
            var stale = new List<TNode>();
            Collect(start, stale, node => node.Vector is not null && node.VectorModel != _embedder.Model);
            foreach (var node in stale)
            {
                var nodePath = node.PathString();
                var vector = VectorMath.Quantize(_embedder.Embed(node.Name));
                ApplyVector(nodePath, _embedder.Model, vector, AppendVector(nodePath, _embedder.Model, vector));
            }
            Committed();
        }
        finally { _lock.ExitWriteLock(); }
    }

    // ---------------------------------------------------------------- graph

    internal void Link(string from, string rel, string to)
    {
        ArgumentException.ThrowIfNullOrEmpty(rel);
        _lock.EnterWriteLock();
        try
        {
            var append = _log.Append(RecordType.Link, 0, EncodeStrings(from, rel, to));
            ApplyLink(from, rel, to);
            _ = append;
            Committed();
        }
        finally { _lock.ExitWriteLock(); }
    }

    internal bool Unlink(string from, string rel, string to)
    {
        _lock.EnterWriteLock();
        try
        {
            var node = FindNode(from, create: false);
            if (node?.Links is null || !node.Links.Contains((rel, to))) return false;
            var append = _log.Append(RecordType.Unlink, 0, EncodeStrings(from, rel, to));
            _dead += append.Size;
            ApplyUnlink(from, rel, to);
            Committed();
            return true;
        }
        finally { _lock.ExitWriteLock(); }
    }

    internal IReadOnlyList<DbPath> Links(string from, string? rel)
    {
        _lock.EnterReadLock();
        try
        {
            return LinkTargets(FindNode(from, create: false), rel).Select(DbPath.FromRaw).ToList();
        }
        finally { _lock.ExitReadLock(); }
    }

    private IEnumerable<string> LinkTargets(TNode? node, string? rel)
    {
        if (node?.Links is null) yield break;
        foreach (var (linkRel, target) in node.Links)
        {
            if (rel is not null && linkRel != rel) continue;
            var targetNode = FindNode(target, create: false);
            if (targetNode is { IsEmpty: false }) yield return target;
        }
    }

    /// <summary>Every descendant that holds a value, plus the nodes this one links to, optionally filtered by a case-insensitive substring of the relative path.</summary>
    internal IReadOnlyList<DbPath> Find(string path, string? filter)
    {
        _lock.EnterReadLock();
        try
        {
            var start = FindNode(path, create: false);
            if (start is null) return [];
            var found = new List<string>();
            var prefixLength = path.Length == 0 ? 0 : path.Length + 1;
            Walk(start);
            foreach (var target in LinkTargets(start, null))
                if (Matches(target, filter) && !found.Contains(target)) found.Add(target);
            return found.Select(DbPath.FromRaw).ToList();

            void Walk(TNode node)
            {
                if (node.Children is null) return;
                foreach (var child in node.Children.Values.OrderBy(c => c.Name, StringComparer.Ordinal))
                {
                    var childPath = child.PathString();
                    if (child.Value is not null && Matches(childPath[prefixLength..], filter)) found.Add(childPath);
                    Walk(child);
                }
            }
        }
        finally { _lock.ExitReadLock(); }

        static bool Matches(string text, string? filter) =>
            string.IsNullOrEmpty(filter) || text.Replace(Sep, '.').Contains(filter, StringComparison.OrdinalIgnoreCase);
    }

    // ---------------------------------------------------------------- events and pub/sub

    internal IDisposable Watch(string path, Action<DbEvent> handler)
    {
        ArgumentNullException.ThrowIfNull(handler);
        return _events.Subscribe(path, handler);
    }

    internal int Publish(string path, string payload)
    {
        ArgumentNullException.ThrowIfNull(payload);
        return _messages.IsEmpty ? 0 : _messages.Publish(path, new DbMessage(DbPath.FromRaw(path), payload));
    }

    internal IDisposable Subscribe(string path, Action<DbMessage> handler)
    {
        ArgumentNullException.ThrowIfNull(handler);
        return _messages.Subscribe(path, handler);
    }

    private void Emit(string path, DbEventKind kind, string? value)
    {
        if (!_events.IsEmpty) _events.Publish(path, new DbEvent(DbPath.FromRaw(path), kind, value));
    }

    private static bool IsUnder(string path, string ancestor) =>
        ancestor.Length == 0
        || path.Equals(ancestor, StringComparison.Ordinal)
        || (path.Length > ancestor.Length && path[ancestor.Length] == Sep && path.StartsWith(ancestor, StringComparison.Ordinal));

    // ---------------------------------------------------------------- maintenance

    /// <summary>Forces everything written so far to stable storage.</summary>
    public void Flush()
    {
        if (_disposed) return;
        _lock.EnterReadLock();
        try { _log.Sync(); }
        catch (ObjectDisposedException) { }
        finally { _lock.ExitReadLock(); }
    }

    /// <summary>Rewrites the log with only live data, reclaiming overwritten and removed records.</summary>
    public void Compact()
    {
        _lock.EnterWriteLock();
        try { CompactLocked(); }
        finally { _lock.ExitWriteLock(); }
    }

    private void Committed()
    {
        if (_options.Flush == FlushMode.Always) _log.Sync();
        if (_options.AutoCompact && _dead >= _options.CompactMinDeadBytes && _dead * 2 >= _log.TotalBytes) CompactLocked();
    }

    private void CompactLocked()
    {
        var first = _log.BeginCompaction();
        var nodes = new List<TNode>();
        Collect(_root, nodes, _ => true);
        foreach (var node in nodes)
        {
            if (node.Parent is null) continue;
            var path = node.PathString();
            if (node.Value is { } value)
            {
                var moved = AppendPut(path, ReadRaw(value), value.Flags);
                moved.Cache = value.Cache;
                node.Value = moved;
            }
            if (node.Vector is not null) node.VectorRecordSize = AppendVector(path, node.VectorModel!, node.Vector);
            if (node.Links is not null)
                foreach (var (rel, target) in node.Links) _log.Append(RecordType.Link, 0, EncodeStrings(path, rel, target));
        }
        _log.Sync();
        _log.DeleteSegmentsBefore(first);
        _dead = 0;
    }

    // ---------------------------------------------------------------- log <-> index

    private ValueRef AppendPut(string path, ReadOnlySpan<byte> value, byte flags)
    {
        var keyLength = Encoding.UTF8.GetByteCount(path);
        var head = new byte[4 + keyLength];
        BinaryPrimitives.WriteInt32LittleEndian(head, keyLength);
        Encoding.UTF8.GetBytes(path, head.AsSpan(4));
        var append = _log.Append(RecordType.Put, flags, head, value);
        var reference = new ValueRef(append.Segment, append.Offset + SegmentLog.HeaderSize + head.Length, value.Length, flags, append.Size);
        if (flags == 0 && value.Length <= CacheLimit) reference.Cache = value.ToArray();
        return reference;
    }

    private int AppendVector(string path, string model, byte[] vector)
    {
        var keyLength = Encoding.UTF8.GetByteCount(path);
        var modelLength = Encoding.UTF8.GetByteCount(model);
        var head = new byte[4 + keyLength + 2 + modelLength];
        BinaryPrimitives.WriteInt32LittleEndian(head, keyLength);
        Encoding.UTF8.GetBytes(path, head.AsSpan(4));
        BinaryPrimitives.WriteUInt16LittleEndian(head.AsSpan(4 + keyLength), (ushort)modelLength);
        Encoding.UTF8.GetBytes(model, head.AsSpan(6 + keyLength));
        return _log.Append(RecordType.Vector, 0, head, vector).Size;
    }

    private static byte[] EncodeStrings(params string[] values)
    {
        using var stream = new MemoryStream();
        Span<byte> length = stackalloc byte[4];
        foreach (var value in values)
        {
            var bytes = Encoding.UTF8.GetBytes(value);
            BinaryPrimitives.WriteInt32LittleEndian(length, bytes.Length);
            stream.Write(length);
            stream.Write(bytes);
        }
        return stream.ToArray();
    }

    private static string[] DecodeStrings(ReadOnlySpan<byte> body, int count)
    {
        var result = new string[count];
        for (var i = 0; i < count; i++)
        {
            var length = BinaryPrimitives.ReadInt32LittleEndian(body);
            result[i] = Encoding.UTF8.GetString(body.Slice(4, length));
            body = body[(4 + length)..];
        }
        return result;
    }

    private byte[] ReadRaw(ValueRef value)
    {
        if (value.Cache is not null) return value.Cache;
        var bytes = new byte[value.Length];
        _log.Read(value.Segment, value.Offset, bytes);
        return bytes;
    }

    private void Replay(int segment, long offset, int size, RecordType type, byte flags, ReadOnlySpan<byte> body)
    {
        switch (type)
        {
            case RecordType.Put:
            {
                var keyLength = BinaryPrimitives.ReadInt32LittleEndian(body);
                var path = Encoding.UTF8.GetString(body.Slice(4, keyLength));
                var value = body[(4 + keyLength)..];
                var reference = new ValueRef(segment, offset + SegmentLog.HeaderSize + 4 + keyLength, value.Length, flags, size);
                if (flags == 0 && value.Length <= CacheLimit) reference.Cache = value.ToArray();
                ApplyPut(path, reference);
                break;
            }
            case RecordType.Remove:
                ApplyRemove(Encoding.UTF8.GetString(body), size);
                break;
            case RecordType.Vector:
            {
                var keyLength = BinaryPrimitives.ReadInt32LittleEndian(body);
                var path = Encoding.UTF8.GetString(body.Slice(4, keyLength));
                var modelLength = BinaryPrimitives.ReadUInt16LittleEndian(body[(4 + keyLength)..]);
                var model = Encoding.UTF8.GetString(body.Slice(6 + keyLength, modelLength));
                ApplyVector(path, model, body[(6 + keyLength + modelLength)..].ToArray(), size);
                break;
            }
            case RecordType.Link:
            {
                var parts = DecodeStrings(body, 3);
                ApplyLink(parts[0], parts[1], parts[2]);
                break;
            }
            case RecordType.Unlink:
            {
                var parts = DecodeStrings(body, 3);
                _dead += size;
                ApplyUnlink(parts[0], parts[1], parts[2]);
                break;
            }
        }
    }

    private void ApplyPut(string path, ValueRef reference)
    {
        var node = FindNode(path, create: true)!;
        if (node.Value is not null) _dead += node.Value.RecordSize;
        // A plain rewrite drops the old embedding; Write(list) appends a fresh one right after.
        if (node.Vector is not null)
        {
            _dead += node.VectorRecordSize;
            node.Vector = null;
            node.VectorModel = null;
        }
        node.Value = reference;
        Interlocked.Increment(ref _version);
    }

    private void ApplyVector(string path, string model, byte[] vector, int recordSize)
    {
        var node = FindNode(path, create: false);
        if (node?.Value is null)
        {
            _dead += recordSize;
            return;
        }
        if (node.Vector is not null) _dead += node.VectorRecordSize;
        node.Vector = vector;
        node.VectorModel = model;
        node.VectorRecordSize = recordSize;
        Interlocked.Increment(ref _version);
    }

    private void ApplyRemove(string path, int removeRecordSize)
    {
        _dead += removeRecordSize;
        var node = FindNode(path, create: false);
        if (node is null || node.Parent is null) return;

        var all = new List<TNode>();
        Collect(node, all, _ => true);
        foreach (var removed in all)
            _dead += (removed.Value?.RecordSize ?? 0) + (removed.Vector is null ? 0 : removed.VectorRecordSize);

        node.Parent.Children!.Remove(node.Name);
        for (var parent = node.Parent; parent is { Parent: not null } && parent.IsEmpty; parent = parent.Parent)
            parent.Parent.Children!.Remove(parent.Name);
        Interlocked.Increment(ref _version);
    }

    private void ApplyLink(string from, string rel, string to)
    {
        var node = FindNode(from, create: true)!;
        node.Links ??= [];
        if (!node.Links.Contains((rel, to))) node.Links.Add((rel, to));
    }

    private void ApplyUnlink(string from, string rel, string to)
    {
        var node = FindNode(from, create: false);
        node?.Links?.Remove((rel, to));
    }

    private TNode? FindNode(string path, bool create)
    {
        var node = _root;
        if (path.Length == 0) return node;
        var rest = path.AsSpan();
        while (true)
        {
            var cut = rest.IndexOf(Sep);
            var part = cut < 0 ? rest : rest[..cut];

            TNode? next = null;
            node.Children?.GetAlternateLookup<ReadOnlySpan<char>>().TryGetValue(part, out next);
            if (next is null)
            {
                if (!create) return null;
                next = new TNode(part.ToString(), node);
                (node.Children ??= new Dictionary<string, TNode>(StringComparer.Ordinal))[next.Name] = next;
            }
            node = next;
            if (cut < 0) return node;
            rest = rest[(cut + 1)..];
        }
    }

    private static void Collect(TNode node, List<TNode> into, Func<TNode, bool> keep)
    {
        if (keep(node)) into.Add(node);
        if (node.Children is null) return;
        foreach (var child in node.Children.Values) Collect(child, into, keep);
    }

    public void Dispose()
    {
        if (_disposed) return;
        _disposed = true;
        _flushTimer?.Dispose();
        _events.Dispose();
        _messages.Dispose();
        _lock.EnterWriteLock();
        try
        {
            _log.Dispose();
            _directoryLock.Dispose();
            if (_masterKey is not null) CryptographicOperations.ZeroMemory(_masterKey);
        }
        finally { _lock.ExitWriteLock(); }
    }
}
