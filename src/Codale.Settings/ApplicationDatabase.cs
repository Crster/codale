using Codale.Db;

namespace Codale.Settings;

/// <summary>Owns the application database idle lease and timer.</summary>
public sealed class ApplicationDatabase : IDisposable
{
    private readonly TimeSpan IdleClose = TimeSpan.FromSeconds(2);
    private readonly object Gate = new();
    private readonly Timer Idle;
    private bool _disposed;

    public ApplicationDatabase(string folder)
    {
        Folder = Path.GetFullPath(folder);
        Idle = new Timer(_ => Release(), null, Timeout.Infinite, Timeout.Infinite);
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
        lock (Gate)
        {
            ObjectDisposedException.ThrowIf(_disposed, this);
            var options = new CodaleDbOptions { LockTimeout = TimeSpan.FromSeconds(3) };
            _pin ??= CodaleDb.Acquire(DatabasePath, options);
            Idle.Change(IdleClose, Timeout.InfiniteTimeSpan);
            return CodaleDb.Acquire(DatabasePath, options);
        }
    }

    public void Dispose()
    {
        lock (Gate)
        {
            if (_disposed) return;
            _disposed = true;
            Idle.Dispose();
            Release();
        }
    }

    private void Release()
    {
        lock (Gate)
        {
            _pin?.Dispose();
            _pin = null;
        }
    }
}
