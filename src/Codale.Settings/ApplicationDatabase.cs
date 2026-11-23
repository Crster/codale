using Codale.Core.Projects;
using Codale.Db;

namespace Codale.Settings;

/// <summary>Reuses the application database between operations and releases its lock when idle.</summary>
public sealed class ApplicationDatabase(string folder) : IDisposable
{
    private readonly CodaleDatabasePool _records = new(TimeSpan.FromSeconds(2), new() { LockTimeout = TimeSpan.FromSeconds(3) },
        maxHold: TimeSpan.FromSeconds(2));
    private readonly Lock _watchGate = new();
    private readonly List<SettingsWatch> _watches = [];
    private bool _disposed;

    public string Folder { get; } = Path.GetFullPath(folder);
    public string DatabasePath => Path.Combine(Folder, "data", "records");

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
    private sealed record SessionTodoList(SessionTodo[] Items);
    public sealed record SessionTodo(string Content, string Status);

    private T UseSetting<T>(string key, Func<RecordKey<SettingRecord>, T> work) => UseRecords(db => work(db.Record<SettingRecord>().Key(key)));

    public string? GetSetting(string name) => UseSetting("global:" + name, node => node.Get()?.Value);
    public void SetSetting(string name, string value) => UseSetting("global:" + name, node => { node.Value(new(value)); return true; });
    public void UpdateSetting(string name, Func<string?, string> update) => UseSetting("global:" + name, node =>
    {
        node.Value(new(update(node.Get()?.Value)));
        return true;
    });

    public string? GetProjectSetting(string projectPath, string name) =>
        UseSetting("project:" + ProjectPaths.InstanceKey(projectPath) + ":" + name, node => node.Get()?.Value);

    public void SetProjectSetting(string projectPath, string name, string value) =>
        UseSetting("project:" + ProjectPaths.InstanceKey(projectPath) + ":" + name, node => { node.Value(new(value)); return true; });

    public void SaveSessionTodos(string id, SessionTodo[] todos) => UseRecords(db =>
    {
        db.Record<SessionTodoList>().Key(id).Value(new(todos));
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
