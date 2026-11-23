namespace Codale.Settings.Tests;

public sealed class ApplicationStoreTests
{
    [Fact]
    public void Preferences_start_fresh_and_persist_in_the_database()
    {
        var root = Directory.CreateTempSubdirectory("codale-settings-import-");
        try
        {
            var oldFile = Path.Combine(root.FullName, "settings.json");
            const string oldJson = """{"chat.fontSize":20}""";
            File.WriteAllText(oldFile, oldJson);
            using (var settings = new AppSettingsStore(root.FullName))
            {
                Assert.Equal(12.5, settings.ChatFontSize);
                settings.ChatFontSize = 18;
            }
            using (var reopened = new AppSettingsStore(root.FullName)) Assert.Equal(18, reopened.ChatFontSize);
            Assert.Equal(oldJson, File.ReadAllText(oldFile));
        }
        finally { root.Delete(recursive: true); }
    }

    [Fact]
    public void SettingsStateLivesInTheProductionInstance()
    {
        Assert.Equal("Codale.Settings", typeof(AppSettingsStore).Assembly.GetName().Name);
        var fields = typeof(AppSettingsStore).GetFields(System.Reflection.BindingFlags.Static |
            System.Reflection.BindingFlags.Public | System.Reflection.BindingFlags.NonPublic);
        Assert.All(fields, field => Assert.True(field.IsLiteral, $"{field.Name} must be owned by the settings instance."));
    }

    [Fact]
    public async Task TypedDatabaseChangesNotifyTheirSettingsOwner()
    {
        var root = Directory.CreateTempSubdirectory("codale-settings-reload-").FullName;
        using var database = new ApplicationDatabase(Path.Combine(root, "first"));
        using var first = new AppSettingsStore(database);
        using var other = new AppSettingsStore(Path.Combine(root, "other"));
        Assert.Equal(12.5, first.ChatFontSize);
        Assert.Equal(12.5, other.ChatFontSize);
        var changed = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        first.Changed += (_, _) =>
        {
            if (first.ChatFontSize == 20) changed.TrySetResult();
        };
        database.UseRecords(db => { db.Open<Setting>().Key("chat.fontSize").Value(new("20")); return true; });
        await changed.Task.WaitAsync(TimeSpan.FromSeconds(10));
        Assert.Equal(20, first.ChatFontSize);
        Assert.Equal(12.5, other.ChatFontSize);
    }

    [Fact]
    public void SettingsInstancesIsolateCachesAndPersistAcrossOwners()
    {
        var root = Directory.CreateTempSubdirectory("codale-settings-owners-").FullName;
        using var first = new AppSettingsStore(Path.Combine(root, "first"));
        using var other = new AppSettingsStore(Path.Combine(root, "other"));
        first.ChatFontSize = 18;
        Assert.Equal(12.5, other.ChatFontSize);
        first.Dispose();
        Assert.Throws<ObjectDisposedException>(() => first.ChatFontSize);
        using var reopened = new AppSettingsStore(Path.Combine(root, "first"));
        Assert.Equal(18, reopened.ChatFontSize);
    }

    [Fact]
    public void DisposalSuppressesQueuedSettingsNotifications()
    {
        var root = Directory.CreateTempSubdirectory("codale-settings-dispatch-").FullName;
        using var settings = new AppSettingsStore(root);
        var notifications = new List<Action>();
        settings.AttachUiThread(notifications.Add, () => false);
        var count = 0;
        settings.Changed += (_, _) => count++;
        settings.ChatFontSize = 18;
        Assert.NotEmpty(notifications);
        settings.Dispose();
        foreach (var notification in notifications) notification();
        Assert.Equal(0, count);
        Assert.Throws<ObjectDisposedException>(() => settings.ChatFontSize = 19);
    }

    [Fact]
    public void RecentProjectsAndCommandHistoryUseTheirInjectedDatabase()
    {
        var root = Directory.CreateTempSubdirectory("codale-app-store-owners-").FullName;
        using var first = new ApplicationDatabase(Path.Combine(root, "first"));
        using var other = new ApplicationDatabase(Path.Combine(root, "other"));
        var recents = new RecentProjects(first);
        var history = new CommandHistory(first);
        recents.Record(root);
        recents.Record(root);
        Assert.Single(recents.Load());
        Assert.Empty(new RecentProjects(other).Load());
        history.Record(root, "dotnet build", "terminal");
        history.Record(root, "dotnet test", "terminal");
        Assert.Contains("dotnet build", history.Recent(root));
        Assert.Contains("dotnet build", history.Suggest(root, "dotnet b"));
        Assert.Empty(new CommandHistory(other).Recent(root));
        recents.Clear();
        Assert.Empty(recents.Load());
        first.Dispose();
        Assert.Throws<ObjectDisposedException>(() => first.UseRecords(_ => true));
        Assert.Empty(history.Recent(root));
    }
}
