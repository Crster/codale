namespace Codale.Workspace;

/// <summary>Workspace-scoped access to stores sharing the same database identity.</summary>
public interface IWorkspaceData
{
    string Directory { get; }
    MemoryStore Memory { get; }
    HistoryStore History { get; }
    ConfigStore Config { get; }
    EnvStore Secrets { get; }
    UserTodoStore UserTodos { get; }
}

public interface IWorkspaceDataFactory
{
    IWorkspaceData Open(string projectPath);
}

public sealed class WorkspaceData : IWorkspaceData
{
    public string Directory { get; }
    public MemoryStore Memory { get; }
    public HistoryStore History { get; }
    public ConfigStore Config { get; }
    public EnvStore Secrets { get; }
    public UserTodoStore UserTodos { get; }

    public WorkspaceData(string directory, WorkspaceDatabasePool? databases = null)
    {
        Directory = Path.GetFullPath(directory);
        Memory = new(Directory, databases);
        History = new(Directory, databases);
        Config = new(Directory, databases);
        Secrets = new(Directory, databases);
        UserTodos = new(Directory, databases);
    }
}

public sealed class WorkspaceDataFactory(Func<string, string>? directoryFor = null) : IWorkspaceDataFactory, IDisposable
{
    private readonly WorkspaceDatabasePool _databases = new();
    private bool _disposed;
    public void Dispose()
    {
        _disposed = true;
        _databases.Dispose();
    }
    public IWorkspaceData Open(string projectPath)
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
        return new WorkspaceData((directoryFor ?? WorkspaceDb.DirectoryFor)(projectPath), _databases);
    }
}