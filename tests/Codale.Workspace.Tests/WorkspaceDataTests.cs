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
        using var next = new WorkspaceDataFactory(_ => root);
        Assert.Equal("dark", next.Open("project").Config.Get("theme"));
    }

    [Fact]
    public async Task PoolDisposalAllowsAnActiveLeaseToFinish()
    {
        var root = System.IO.Directory.CreateTempSubdirectory("codale-active-lease-").FullName;
        using var pool = new WorkspaceDatabasePool();
        var entered = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var proceed = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var work = Task.Run(() => pool.Use(root, db =>
        {
            entered.SetResult();
            proceed.Task.GetAwaiter().GetResult();
            db.Open("value").Write("saved");
        }));
        try
        {
            await entered.Task.WaitAsync(TimeSpan.FromSeconds(10));
            pool.Dispose();
            Assert.Throws<ObjectDisposedException>(() => pool.Use(root, _ => { }));
        }
        finally { proceed.TrySetResult(); }
        await work.WaitAsync(TimeSpan.FromSeconds(10));
        using var next = new WorkspaceDatabasePool();
        Assert.Equal("saved", next.Use(root, db => db.Open("value").Read()));
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