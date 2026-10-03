using System.Text.Json;

using Codale.Storage;

namespace Codale.App.Services;

/// <summary>One entry in the welcome screen's recent-projects list.</summary>
public sealed record RecentProject(string Path, string Name, DateTimeOffset LastOpened);

/// <summary>
/// Persisted MRU of opened projects, shown on the project picker so a launch can
/// resume where the user left off. Stored in the global settings area of the
/// Codale database, the same place the other app-level preferences live.
/// </summary>
public static class RecentProjects
{
    private const string SettingName = "recentProjects";
    private const int MaxEntries = 10;

    private static readonly JsonSerializerOptions SerializerOptions = new() { WriteIndented = false };

    public static IReadOnlyList<RecentProject> Load()
    {
        try
        {
            using var store = new CodaleStore(CodaleStore.DefaultDatabasePath);
            return Load(store);
        }
        catch (StoreSchemaException ex)
        {
            // A database from a newer Codale: show an empty list rather than fail the welcome screen.
            CrashLog.Warn("recent", ex.Message);
            return [];
        }
    }

    private static IReadOnlyList<RecentProject> Load(CodaleStore store)
    {
        var json = store.GetSetting(SettingName);
        if (string.IsNullOrEmpty(json))
        {
            return [];
        }

        try
        {
            return JsonSerializer.Deserialize<List<RecentProject>>(json) ?? [];
        }
        catch (JsonException)
        {
            // A corrupt entry must not take down the welcome screen.
            return [];
        }
    }

    /// <summary>Removes every entry from the MRU, e.g. from the picker's Clear link.</summary>
    public static void Clear()
    {
        try
        {
            using var store = new CodaleStore(CodaleStore.DefaultDatabasePath);
            store.SetSetting(SettingName, string.Empty);
        }
        catch (StoreSchemaException ex)
        {
            // The list reappears on next launch at worst; not worth failing over.
            CrashLog.Warn("recent", ex.Message);
        }
    }

    /// <summary>Moves the project to the top of the MRU, adding it if new, and trims the tail.</summary>
    public static void Record(string projectPath)
    {
        var normalized = Codale.Core.Projects.ProjectPaths.Normalize(projectPath);
        var name = System.IO.Path.GetFileName(normalized) is { Length: > 0 } leaf ? leaf : normalized;

        // One store for the read-modify-write; two open+migrate cycles per project open
        // used to sit on the UI thread here.
        try
        {
            using var store = new CodaleStore(CodaleStore.DefaultDatabasePath);

            var entries = Load(store)
                .Where(p => !string.Equals(p.Path, normalized, StringComparison.OrdinalIgnoreCase))
                .ToList();
            entries.Insert(0, new RecentProject(normalized, name, DateTimeOffset.Now));

            if (entries.Count > MaxEntries)
            {
                entries.RemoveRange(MaxEntries, entries.Count - MaxEntries);
            }

            store.SetSetting(SettingName, JsonSerializer.Serialize(entries, SerializerOptions));
        }
        catch (StoreSchemaException ex)
        {
            // The project still opens; it just is not remembered.
            CrashLog.Warn("recent", ex.Message);
        }
    }
}
