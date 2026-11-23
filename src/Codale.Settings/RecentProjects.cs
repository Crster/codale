using System.Text.Json;

namespace Codale.Settings;

/// <summary>One entry in the welcome screen's recent-projects list.</summary>
public sealed record RecentProject(string Path, string Name, DateTimeOffset LastOpened);

/// <summary>
/// Persisted MRU of opened projects, shown on the project picker so a launch can
/// resume where the user left off. Stored in the global settings area of the
/// Codale database, the same place the other app-level preferences live.
/// </summary>
public sealed class RecentProjects(ApplicationDatabase database)
{
    private const string SettingName = "recentProjects";
    private const int MaxEntries = 10;

    private static readonly JsonSerializerOptions SerializerOptions = new() { WriteIndented = false };

    public IReadOnlyList<RecentProject> Load() => Parse(database.GetSetting(SettingName));

    private static IReadOnlyList<RecentProject> Parse(string? json)
    {
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
    public void Clear() => database.SetSetting(SettingName, string.Empty);

    /// <summary>Moves the project to the top of the MRU, adding it if new, and trims the tail.</summary>
    public void Record(string projectPath)
    {
        var normalized = Codale.Core.Projects.ProjectPaths.Normalize(projectPath);
        var name = System.IO.Path.GetFileName(normalized) is { Length: > 0 } leaf ? leaf : normalized;

        database.UpdateSetting(SettingName, previous =>
        {
            var entries = Parse(previous).Where(p => !string.Equals(p.Path, normalized, StringComparison.OrdinalIgnoreCase))
                .Prepend(new RecentProject(normalized, name, DateTimeOffset.Now)).Take(MaxEntries);
            return JsonSerializer.Serialize(entries, SerializerOptions);
        });
    }
}
