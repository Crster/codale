using Codale.Db;

namespace Codale.Workspace;

/// <summary>Owns short-lived database pins for a group of workspace stores.</summary>
public sealed class WorkspaceDatabasePool : IDisposable
{
    private readonly TimeSpan _idleClose = TimeSpan.FromMilliseconds(300);
    private readonly TimeSpan _maxHold = TimeSpan.FromSeconds(1.5);
    private readonly TimeSpan _lockWait = TimeSpan.FromSeconds(5);
    private readonly Lock _gate = new();
    private readonly Dictionary<string, Pin> _pins = [with(StringComparer.OrdinalIgnoreCase)];

    private bool _disposed;

    public void Dispose()
    {
        lock (_gate)
        {
            if (_disposed) return;
            _disposed = true;
            foreach (var key in _pins.Keys.ToArray()) Release(key);
        }
    }

    private sealed class Pin
    {
        public CodaleDb? Db;
        public DateTime Since;
        public Timer? Timer;
    }

    public void Use(string directory, Action<CodaleDb> action) => Use<object?>(directory, db =>
    {
        action(db);
        return null;
    });

    /// <exception cref="IOException">Another process kept the database busy longer than the wait.</exception>
    public T Use<T>(string directory, Func<CodaleDb, T> work)
    {
        var key = Path.GetFullPath(directory);
        CodaleDb lease;
        lock (_gate)
        {
            ObjectDisposedException.ThrowIf(_disposed, this);
            if (!_pins.TryGetValue(key, out var pin))
            {
                _pins[key] = pin = new Pin();
            }

            var options = new CodaleDbOptions { LockTimeout = _lockWait };
            if (pin.Db is null)
            {
                pin.Db = CodaleDb.Acquire(key, options);
                pin.Since = DateTime.UtcNow;
                pin.Timer = new Timer(_ => Release(key, pin), null, _idleClose, Timeout.InfiniteTimeSpan);
            }
            else if (DateTime.UtcNow - pin.Since < _maxHold)
            {
                pin.Timer!.Change(_idleClose, Timeout.InfiniteTimeSpan);
            }

            lease = CodaleDb.Acquire(key, options);
        }

        try
        {
            return work(lease);
        }
        finally
        {
            lease.Dispose();
        }
    }

    private void Release(string key, Pin? expected = null)
    {
        lock (_gate)
        {
            if (!_pins.TryGetValue(key, out var pin) || (expected is not null && !ReferenceEquals(pin, expected)))
            {
                return;
            }

            _pins.Remove(key);
            pin.Timer?.Dispose();
            pin.Db?.Dispose();
        }
    }
}