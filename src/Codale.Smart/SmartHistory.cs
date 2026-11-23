using System.Text;
using System.Text.Json;

using Codale.Core.Projects;
using Codale.Workspace;

namespace Codale.Smart;

/// <summary>Bounded Smart activity history scoped to a worktree within its workspace database.</summary>
public sealed class SmartHistory(Func<string, IWorkspaceData> workspaceFor)
{
    public const int MaxRuns = 10;
    public const long MaxBytes = 1024 * 1024;

    private sealed record Run(string Json, DateTimeOffset StartedAt, int Bytes);

    public SmartHistory(string directory) : this(_ => new WorkspaceData(directory)) { }

    public void Save(SmartActivity activity)
    {
        try
        {
            var project = activity.Context.Project;
            var prefix = ProjectPaths.InstanceKey(project) + ":";
            var key = prefix + activity.Id;
            var json = JsonSerializer.Serialize(activity);
            var current = new Run(json, activity.StartedAt, Encoding.UTF8.GetByteCount(json));
            workspaceFor(project).UseRecords(db =>
            {
                var runs = db.Record<Run>().FindPrefix(prefix).Where(row => row.Key != key).Append(new(key, current))
                    .OrderByDescending(row => row.Value.StartedAt);
                long bytes = 0;
                var kept = 0;
                db.WriteBatch(batch =>
                {
                    foreach (var row in runs)
                    {
                        bytes += row.Value.Bytes;
                        if (++kept > MaxRuns || bytes > MaxBytes) batch.Delete<Run>(row.Key);
                        else if (row.Key == key) batch.Put(key, current);
                    }
                });
            });
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException) { }
    }

    public IReadOnlyList<SmartActivity> Read(string project)
    {
        try
        {
            var prefix = ProjectPaths.InstanceKey(project) + ":";
            var rows = workspaceFor(project).UseRecords(db => db.Record<Run>().FindPrefix(prefix));
            var results = new List<SmartActivity>();
            foreach (var row in rows.OrderByDescending(row => row.Value.StartedAt).Take(MaxRuns))
            {
                try
                {
                    if (JsonSerializer.Deserialize<SmartActivity>(row.Value.Json) is { Context: not null } run
                        && !string.IsNullOrWhiteSpace(run.Id) && Path.IsPathFullyQualified(run.Context.Project)
                        && Path.IsPathFullyQualified(run.Context.WorkingDirectory))
                        results.Add(run);
                }
                catch (JsonException) { }
            }
            return results;
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException) { return []; }
    }
}
