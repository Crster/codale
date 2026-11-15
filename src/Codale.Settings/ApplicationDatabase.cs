using Codale.Db;

namespace Codale.Settings;

/// <summary>Owns the application database idle lease and timer.</summary>
public sealed class ApplicationDatabase : IDisposable
{
    private readonly TimeSpan _idleClose = TimeSpan.FromSeconds(2);
    private readonly Lock _gate = new();
    private readonly Timer _idle;
    private bool _disposed;

    public ApplicationDatabase(string folder)
    {
        Folder = Path.GetFullPath(folder);
        _idle = new Timer(_ => Release(), null, Timeout.Infinite, Timeout.Infinite);
    }
    private CodaleDb? _pin;

    /// <summary>
    /// Under MSIX this redirects into the package's local folder, so it survives in-place package updates. Settings files such as <c>settings.json</c> live beside the database.
    /// </summary>
    public string Folder { get; private set; }

    public string DatabasePath => Path.Combine(Folder, "data");

    /// <summary>A lease on the database; dispose it when the operation is done.</summary>
    /// <exception cref="IOException">Another Codale window kept the database busy longer than the wait.</exception>
    public CodaleDb Open()
    {
        lock (_gate)
        {
            ObjectDisposedException.ThrowIf(_disposed, this);
            var options = new CodaleDbOptions { LockTimeout = TimeSpan.FromSeconds(3) };
            _pin ??= CodaleDb.Acquire(DatabasePath, options);
            _idle.Change(_idleClose, Timeout.InfiniteTimeSpan);
            return CodaleDb.Acquire(DatabasePath, options);
        }
    }

    public void Dispose()
    {
        lock (_gate)
        {
            if (_disposed) return;
            _disposed = true;
            _idle.Dispose();
            Release();
        }
    }

    private void Release()
    {
        lock (_gate)
        {
            _pin?.Dispose();
            _pin = null;
        }
    }
}