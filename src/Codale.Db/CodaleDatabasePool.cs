namespace Codale.Db;

/// <summary>Reuses an open database for nearby operations and releases its folder lock when idle.</summary>
public sealed class CodaleDatabasePool(TimeSpan idle, CodaleDatabaseOptions? options = null, TimeSpan? maxHold = null) : IDisposable
{
    private sealed class Entry(string path)
    {
        public string Path { get; } = path;
        public Lock Gate { get; } = new();
        public CodaleDatabase? Database;
        public Timer? Timer;
        public int Active;
        public long OpenedAt;
        public long ExpiresAt;
    }

    private readonly Lock _gate = new();
    private readonly Dictionary<string, Entry> _entries = new(OperatingSystem.IsWindows() ? StringComparer.OrdinalIgnoreCase : StringComparer.Ordinal);
    private bool _disposed;

    /// <summary>Serializes a logical operation with other operations using this pool entry.</summary>
    public T Use<T>(string path, Func<CodaleDatabase, T> action)
    {
        path = Path.TrimEndingDirectorySeparator(Path.GetFullPath(path));
        Entry entry;
        lock (_gate)
        {
            ObjectDisposedException.ThrowIf(_disposed, this);
            if (!_entries.TryGetValue(path, out entry!))
            {
                entry = new(path);
                entry.Timer = new Timer(Expire, entry, Timeout.InfiniteTimeSpan, Timeout.InfiniteTimeSpan);
                _entries.Add(path, entry);
            }
            entry.Active++;
            entry.Timer!.Change(Timeout.InfiniteTimeSpan, Timeout.InfiniteTimeSpan);
        }

        try
        {
            lock (entry.Gate)
            {
                if (entry.Database is null)
                {
                    entry.Database = new CodaleDatabase(entry.Path, options);
                    entry.OpenedAt = Environment.TickCount64;
                }
                return action(entry.Database);
            }
        }
        finally
        {
            lock (_gate)
            {
                if (--entry.Active == 0)
                {
                    var now = Environment.TickCount64;
                    if (_disposed || entry.Database is null || (maxHold is { } limit && now - entry.OpenedAt >= limit.TotalMilliseconds))
                        Close(entry);
                    else
                    {
                        entry.ExpiresAt = now + (long)idle.TotalMilliseconds;
                        entry.Timer!.Change(idle, Timeout.InfiniteTimeSpan);
                    }
                }
            }
        }
    }

    public void Use(string path, Action<CodaleDatabase> action) => Use(path, db => { action(db); return true; });

    private void Expire(object? state)
    {
        var entry = (Entry)state!;
        lock (_gate)
        {
            if (entry.Active != 0 || _entries.GetValueOrDefault(entry.Path) != entry) return;
            var remaining = entry.ExpiresAt - Environment.TickCount64;
            if (remaining <= 0)
            {
                try { Close(entry); }
                catch (IOException error) { System.Diagnostics.Trace.TraceWarning(error.Message); }
            }
            else entry.Timer!.Change(TimeSpan.FromMilliseconds(remaining), Timeout.InfiniteTimeSpan);
        }
    }

    private void Close(Entry entry)
    {
        _entries.Remove(entry.Path);
        entry.Timer!.Dispose();
        entry.Database?.Dispose();
    }

    public void Dispose()
    {
        lock (_gate)
        {
            if (_disposed) return;
            _disposed = true;
            foreach (var entry in _entries.Values.Where(entry => entry.Active == 0).ToArray()) Close(entry);
        }
    }
}
