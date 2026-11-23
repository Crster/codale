namespace Codale.Workspace.Tests;

public sealed class WorkspaceDataTests
{
    [Fact]
    public void DisposedFactoryClosesItsStoresAndPreservesPersistedData()
    {
        var root = System.IO.Directory.CreateTempSubdirectory("codale-owned-data-").FullName;
        var factory = new WorkspaceDataFactory(_ => root);
        var data = factory.Open("project");
        data.Config.Set("theme", "dark");
        factory.Dispose();
        factory.Dispose();
        Assert.Throws<ObjectDisposedException>(() => factory.Open("project"));
        Assert.Throws<ObjectDisposedException>(() => data.Config.Get("theme"));
        Assert.Throws<ObjectDisposedException>(() => data.UserTodos.List());
        using var next = new WorkspaceDataFactory(_ => root);
        Assert.Equal("dark", next.Open("project").Config.Get("theme"));
    }

    [Fact]
    public void Todos_use_typed_storage_and_ignore_old_records()
    {
        var directory = System.IO.Directory.CreateTempSubdirectory("codale-workspace-todos-");
        try
        {
            using var pool = new WorkspaceDatabasePool();
            var old = new UserTodo(Guid.NewGuid().ToString("N"), "old task", DateTimeOffset.UnixEpoch);
            using (var legacy = new Codale.Db.CodaleDb(directory.FullName))
                legacy.Open("user-todos").Open(old.Id).WriteJson(old);
            var store = new UserTodoStore(directory.FullName, pool);
            var added = store.Add("new task");
            Assert.Equal([added], store.List());
            Assert.True(System.IO.File.Exists(Path.Combine(directory.FullName, "records", "index__records.db")));
            Assert.False(store.Complete(old.Id));
            Assert.True(store.Complete(added.Id));
            Assert.Empty(store.List());
        }
        finally { directory.Delete(recursive: true); }
    }

    [Fact]
    public async Task PoolDisposalAllowsAnActiveLeaseToFinish()
    {
        var root = System.IO.Directory.CreateTempSubdirectory("codale-active-lease-").FullName;
        using var pool = new WorkspaceDatabasePool();
        var entered = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var proceed = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var work = Task.Run(() => pool.UseRecords(root, db =>
        {
            entered.SetResult();
            proceed.Task.GetAwaiter().GetResult();
            db.Open<string>().Key("value").Value("saved");
        }));
        try
        {
            await entered.Task.WaitAsync(TimeSpan.FromSeconds(10));
            pool.Dispose();
            Assert.Throws<ObjectDisposedException>(() => pool.UseRecords(root, _ => { }));
        }
        finally { proceed.TrySetResult(); }
        await work.WaitAsync(TimeSpan.FromSeconds(10));
        using var next = new WorkspaceDatabasePool();
        Assert.Equal("saved", next.UseRecords(root, db => db.Open<string>().Key("value").Get()));
    }

    [Fact]
    public void FactorySharesProjectIdentityAndIsolatesOtherWorkspaces()
    {
        var root = System.IO.Directory.CreateTempSubdirectory("codale-data-context-").FullName;
        using var factory = new WorkspaceDataFactory(project => Path.Combine(root, project));
        var first = factory.Open("first");
        var same = factory.Open("first");
        var other = factory.Open("other");
        first.Memory.Save("Decision", "Use the shared service");
        first.Config.Set("theme", "dark");
        Assert.Single(same.Memory.List());
        Assert.Equal("dark", same.Config.Get("theme"));
        Assert.Empty(other.Memory.List());
        Assert.Null(other.Config.Get("theme"));
        Assert.Equal(first.Directory, same.Directory);
        Assert.NotEqual(first.Directory, other.Directory);
    }
}
