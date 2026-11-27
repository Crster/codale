using Codale.Db;

namespace Codale.Settings;

/// <summary>Reuses the application database between operations and releases its lock when idle.</summary>
public sealed class ApplicationDatabase(string folder) : IDisposable
{
    // The launcher and project windows run in separate processes and all use this directory.
    private readonly CodaleDatabasePool _records = new(TimeSpan.FromMilliseconds(100), new() { LockTimeout = TimeSpan.FromSeconds(3) },
        maxHold: TimeSpan.FromMilliseconds(500));
    private readonly Lock _watchGate = new();
    private readonly List<SettingsWatch> _watches = [];
    private bool _disposed;

    public string Folder { get; } = Path.GetFullPath(folder);
    public string DatabasePath { get; } = Flattened(Path.Combine(Path.GetFullPath(folder), "data"));

    private static string Flattened(string databasePath)
    {
        MoveNestedRecords(databasePath);
        return databasePath;
    }

    /// <summary>
    /// The database briefly lived one level deeper, in <c>data\records</c>. Moves its files up
    /// once, if <c>data</c> holds no database of its own yet; otherwise the nested copy is left alone.
    /// </summary>
    internal static bool MoveNestedRecords(string databasePath)
    {
        var nested = Path.Combine(databasePath, "records");
        try
        {
            if (!Directory.Exists(nested) || Directory.EnumerateFiles(databasePath).Any()) return false;
            foreach (var file in Directory.EnumerateFiles(nested))
                File.Move(file, Path.Combine(databasePath, Path.GetFileName(file)));
            Directory.Delete(nested);
            return true;
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            return false;
        }
    }

    public T UseRecords<T>(Func<CodaleDatabase, T> action) => _records.Use(DatabasePath, db =>
    {
        lock (_watchGate)
        {
            foreach (var watch in _watches) watch.Attach(db);
        }
        return action(db);
    });

    internal IDisposable WatchSettings(Action<RecordEntry<Setting>, RecordAction> handler)
    {
        lock (_watchGate)
        {
            ObjectDisposedException.ThrowIf(_disposed, this);
            var watch = new SettingsWatch(this, handler);
            _watches.Add(watch);
            return watch;
        }
    }

    private sealed class SettingsWatch(ApplicationDatabase owner, Action<RecordEntry<Setting>, RecordAction> handler) : IDisposable
    {
        private CodaleDatabase? _attached;
        private IDisposable? _watch;

        public void Attach(CodaleDatabase database)
        {
            if (ReferenceEquals(_attached, database)) return;
            _watch?.Dispose();
            _watch = database.Open<Setting>().Watch(handler);
            _attached = database;
        }

        public void Dispose()
        {
            lock (owner._watchGate)
            {
                owner._watches.Remove(this);
                _watch?.Dispose();
                _watch = null;
                _attached = null;
            }
        }
    }

    private sealed record SettingRecord(string Value);

    private T UseSetting<T>(string key, Func<RecordKey<SettingRecord>, T> work) => UseRecords(db => work(db.Record<SettingRecord>().Key(key)));

    public string? GetSetting(string name) => UseSetting("global:" + name, node => node.Get()?.Value);
    public void SetSetting(string name, string value) => UseSetting("global:" + name, node => { node.Value(new(value)); return true; });
    public void UpdateSetting(string name, Func<string?, string> update) => UseSetting("global:" + name, node =>
    {
        node.Value(new(update(node.Get()?.Value)));
        return true;
    });

    public void Dispose()
    {
        lock (_watchGate)
        {
            if (_disposed) return;
            _disposed = true;
            foreach (var watch in _watches.ToArray()) watch.Dispose();
        }
        _records.Dispose();
    }
}
