using System.Security.Cryptography;

namespace Codale.Db;

/// <summary>
/// An embedded multi-model database addressed by paths: <c>db.Open("setting").Open("ai provider").Write("x")</c>.
/// One append-only log backs a key-value store, a semantic (vector) index, a keyword (BM25) index, a graph of
/// named links, compressed blobs, per-value encryption, change events and pub/sub.
/// </summary>
/// <remarks>
/// One process owns a database directory at a time (an exclusive <c>LOCK</c> file). Data that several processes
/// share is opened for the length of one operation: <c>using var db = CodaleDb.Acquire(path, new() { LockTimeout = ... })</c>.
/// The class is split by capability: key-value, vectors, keywords, graph, blobs, events, protection, maintenance
/// and the log-to-index plumbing each live in their own <c>CodaleDb.*.cs</c>.
/// </remarks>
public sealed partial class CodaleDb : IDisposable
{
    internal const char Sep = '\u001f';

    private static readonly object SharedGate = new();
    private static readonly Dictionary<string, CodaleDb> Shared = new(StringComparer.OrdinalIgnoreCase);

    private readonly CodaleDbOptions _options;
    private readonly string _dir;
    private readonly FileStream _directoryLock;
    private readonly SegmentLog _log;
    private readonly ReaderWriterLockSlim _lock = new(LockRecursionPolicy.SupportsRecursion);
    private readonly TNode _root = new("", null);
    private readonly IEmbedder _embedder;
    private readonly Timer? _flushTimer;
    private string? _sharedKey;
    private int _leases = 1;
    private bool _disposed;

    /// <summary>Opens (creating if needed) the database in <paramref name="directory"/>; the caller owns it and disposes it.</summary>
    /// <exception cref="IOException">Another process or instance has the directory open (after <see cref="CodaleDbOptions.LockTimeout"/>).</exception>
    public CodaleDb(string directory, CodaleDbOptions? options = null)
    {
        _options = options ?? new CodaleDbOptions();
        _embedder = _options.Embedder ?? new HashedEmbedder();
        _dir = Path.GetFullPath(directory);
        Directory.CreateDirectory(_dir);
        // A second process (or a second instance) on the same directory would corrupt the log: refuse it.
        _directoryLock = LockDirectory(Path.Combine(_dir, "LOCK"), _options.LockTimeout);
        _log = new SegmentLog(_dir, _options.SegmentBytes);
        try
        {
            _log.Open(Replay);
            MaybeCompact();
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

    /// <summary>
    /// The shared instance for <paramref name="directory"/>, opened if this is the first caller. Every call must be
    /// matched by one <see cref="Dispose"/>; the database closes when the last lease is released.
    /// </summary>
    public static CodaleDb Acquire(string directory, CodaleDbOptions? options = null)
    {
        var key = Path.GetFullPath(directory);
        lock (SharedGate)
        {
            if (Shared.TryGetValue(key, out var existing))
            {
                existing._leases++;
                return existing;
            }
            var db = new CodaleDb(directory, options) { _sharedKey = key };
            Shared[key] = db;
            return db;
        }
    }

    /// <summary>Starts a path. Free: no I/O happens until a terminal call such as <c>Write</c> or <c>Read</c>.</summary>
    public DbNode Open(string name) => new(this, DbNode.Validate(name));

    private static FileStream LockDirectory(string path, TimeSpan timeout)
    {
        var deadline = DateTime.UtcNow + timeout;
        while (true)
        {
            try
            {
                return new FileStream(path, FileMode.OpenOrCreate, FileAccess.ReadWrite, FileShare.None);
            }
            catch (IOException) when (DateTime.UtcNow < deadline)
            {
                Thread.Sleep(15);
            }
        }
    }

    public void Dispose()
    {
        if (_sharedKey is not null)
        {
            lock (SharedGate)
            {
                if (--_leases > 0) return;
                Shared.Remove(_sharedKey);
            }
        }
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
