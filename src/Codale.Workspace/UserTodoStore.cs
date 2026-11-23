using Codale.Db;

namespace Codale.Workspace;

/// <summary>A user-requested item awaiting agent verification.</summary>
public sealed record UserTodo(string Id, string Text, DateTimeOffset CreatedAt);

/// <summary>Workspace-wide queue of user-defined todos.</summary>
public sealed class UserTodoStore(string directory, WorkspaceDatabasePool? databases = null)
{
    private WorkspaceDatabasePool Pool => databases ?? WorkspaceDb.Shared;

    private T Use<T>(Func<RecordCollection<UserTodo>, T> work) => Pool.UseRecords(directory, db => work(db.Record<UserTodo>()));

    public UserTodo Add(string text)
    {
        text = text.Trim();
        if (text.Length == 0 || text.Length > 8000)
            throw new ArgumentException("Enter a todo of at most 8000 characters.");
        var item = new UserTodo(Guid.NewGuid().ToString("N"), text, DateTimeOffset.UtcNow);
        return Use(records => { records.Key(item.Id).Value(item); return item; });
    }

    public IReadOnlyList<UserTodo> List() => Use(records => records.Find().Select(row => row.Value).OrderBy(item => item.CreatedAt).ToArray());

    public bool Complete(string id)
    {
        if (!Guid.TryParseExact(id, "N", out _)) throw new ArgumentException("Invalid todo id.");
        return Use(records => records.Key(id).TryDelete(out _));
    }
}
