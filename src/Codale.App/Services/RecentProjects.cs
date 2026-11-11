using System.Text.Json;

using Codale.Db;

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
        using var db = AppDb.Open();
        return Load(db);
    }

    private static IReadOnlyList<RecentProject> Load(CodaleDb db)
    {
        var json = db.Setting(SettingName).Read();
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
        using var db = AppDb.Open();
        db.Setting(SettingName).Write(string.Empty);
    }

    /// <summary>Moves the project to the top of the MRU, adding it if new, and trims the tail.</summary>
    public static void Record(string projectPath)
    {
        var normalized = Codale.Core.Projects.ProjectPaths.Normalize(projectPath);
        var name = System.IO.Path.GetFileName(normalized) is { Length: > 0 } leaf ? leaf : normalized;

        // One lease for the read-modify-write.
        using var db = AppDb.Open();

        var entries = Load(db)
            .Where(p => !string.Equals(p.Path, normalized, StringComparison.OrdinalIgnoreCase))
            .ToList();
        entries.Insert(0, new RecentProject(normalized, name, DateTimeOffset.Now));

        if (entries.Count > MaxEntries)
        {
            entries.RemoveRange(MaxEntries, entries.Count - MaxEntries);
        }

        db.Setting(SettingName).Write(JsonSerializer.Serialize(entries, SerializerOptions));
    }
}
