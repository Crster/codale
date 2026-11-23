using Codale.Db;

namespace Codale.Workspace;

/// <summary>Owns or borrows short-lived leases on typed workspace databases.</summary>
public sealed class WorkspaceDatabasePool : IDisposable
{
    private readonly CodaleDatabasePool _records;
    private readonly bool _ownsPool;
    private volatile bool _disposed;

    public WorkspaceDatabasePool()
    {
        _records = new(TimeSpan.FromMilliseconds(300), new() { LockTimeout = TimeSpan.FromSeconds(5) },
            maxHold: TimeSpan.FromSeconds(1.5));
        _ownsPool = true;
    }

    private WorkspaceDatabasePool(CodaleDatabasePool records) => _records = records;

    internal WorkspaceDatabasePool Borrow() => new(_records);

    public T UseRecords<T>(string directory, Func<CodaleDatabase, T> work)
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
        return _records.Use(directory, work);
    }

    public void UseRecords(string directory, Action<CodaleDatabase> work) => UseRecords(directory, db =>
    {
        work(db);
        return true;
    });

    public void Dispose()
    {
        _disposed = true;
        if (_ownsPool) _records.Dispose();
    }
}
