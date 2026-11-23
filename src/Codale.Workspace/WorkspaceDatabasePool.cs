using Codale.Db;

namespace Codale.Workspace;

/// <summary>Owns short-lived leases on typed workspace databases.</summary>
public sealed class WorkspaceDatabasePool : IDisposable
{
    private readonly CodaleDatabasePool _records = new(TimeSpan.FromMilliseconds(300), new() { LockTimeout = TimeSpan.FromSeconds(5) },
        maxHold: TimeSpan.FromSeconds(1.5));

    public T UseRecords<T>(string directory, Func<CodaleDatabase, T> work) => _records.Use(Path.Combine(directory, "records"), work);

    public void UseRecords(string directory, Action<CodaleDatabase> work) => _records.Use(Path.Combine(directory, "records"), work);

    public void Dispose() => _records.Dispose();
}
