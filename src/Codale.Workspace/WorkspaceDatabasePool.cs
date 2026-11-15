using Codale.Db;

namespace Codale.Workspace;

/// <summary>Owns short-lived database pins for a group of workspace stores.</summary>
public sealed class WorkspaceDatabasePool : IDisposable
{
    private readonly TimeSpan IdleClose = TimeSpan.FromMilliseconds(300);
    private readonly TimeSpan MaxHold = TimeSpan.FromSeconds(1.5);
    private readonly TimeSpan LockWait = TimeSpan.FromSeconds(5);
    private readonly object Gate = new();
    private readonly Dictionary<string, Pin> Pins = new(StringComparer.OrdinalIgnoreCase);

    private bool _disposed;

    public void Dispose()
    {
        lock (Gate)
        {
            if (_disposed) return;
            _disposed = true;
            foreach (var key in Pins.Keys.ToArray()) Release(key);
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
        lock (Gate)
        {
            ObjectDisposedException.ThrowIf(_disposed, this);
            if (!Pins.TryGetValue(key, out var pin))
            {
                Pins[key] = pin = new Pin();
            }

            var options = new CodaleDbOptions { LockTimeout = LockWait };
            if (pin.Db is null)
            {
                pin.Db = CodaleDb.Acquire(key, options);
                pin.Since = DateTime.UtcNow;
                pin.Timer = new Timer(_ => Release(key, pin), null, IdleClose, Timeout.InfiniteTimeSpan);
            }
            else if (DateTime.UtcNow - pin.Since < MaxHold)
            {
                pin.Timer!.Change(IdleClose, Timeout.InfiniteTimeSpan);
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
        lock (Gate)
        {
            if (!Pins.TryGetValue(key, out var pin) || (expected is not null && !ReferenceEquals(pin, expected)))
            {
                return;
            }

            Pins.Remove(key);
            pin.Timer?.Dispose();
            pin.Db?.Dispose();
        }
    }
}
