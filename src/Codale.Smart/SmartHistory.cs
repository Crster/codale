using System.Text.Json;

namespace Codale.Smart;

/// <summary>Bounded per-project history. Writes are atomic and logs never prevent inference.</summary>
public sealed class SmartHistory(string directory)
{
    public const int MaxRuns = 200;
    public const long MaxBytes = 10 * 1024 * 1024;
    private readonly object _gate = new();
    private string Folder(string project) => Path.Combine(directory, SummaryCache.Hash(Path.GetFullPath(project).ToUpperInvariant()));

    public void Save(SmartActivity activity)
    {
        lock (_gate)
        {
            string? temp = null;
            try
            {
                var folder = Folder(activity.Context.Project);
                Directory.CreateDirectory(folder);
                var path = Path.Combine(folder, activity.Id + ".json");
                temp = path + "." + Guid.NewGuid().ToString("N") + ".tmp";
                File.WriteAllText(temp, JsonSerializer.Serialize(activity));
                File.Move(temp, path, true);
                long total = 0; int kept = 0;
                foreach (var file in new DirectoryInfo(folder).EnumerateFiles("*.json").OrderByDescending(f => f.LastWriteTimeUtc))
                {
                    total += file.Length;
                    if (++kept > MaxRuns || total > MaxBytes) file.Delete();
                }
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException) { }
            finally { try { if (temp is not null && File.Exists(temp)) File.Delete(temp); } catch (IOException) { } catch (UnauthorizedAccessException) { } }
        }
    }

    public IReadOnlyList<SmartActivity> Read(string project)
    {
        lock (_gate)
        {
            var results = new List<SmartActivity>();
            try
            {
                var folder = Folder(project);
                if (!Directory.Exists(folder)) return results;
                long total = 0;
                foreach (var file in new DirectoryInfo(folder).EnumerateFiles("*.json").OrderByDescending(f => f.LastWriteTimeUtc).Take(MaxRuns))
                {
                    if ((total += file.Length) > MaxBytes) break;
                    try
                    {
                        var run = JsonSerializer.Deserialize<SmartActivity>(File.ReadAllText(file.FullName));
                        if (run?.Context is not null && !string.IsNullOrWhiteSpace(run.Id) && !string.IsNullOrWhiteSpace(run.Context.Project)
                            && Path.IsPathFullyQualified(run.Context.Project) && Path.IsPathFullyQualified(run.Context.WorkingDirectory)) results.Add(run);
                    }
                    catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or JsonException or ArgumentException) { }
                }
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException) { }
            return results;
        }
    }
}
