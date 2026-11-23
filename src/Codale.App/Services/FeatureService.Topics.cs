using System.Text.Json;

using Codale.Agents.History;
using Codale.Core.Features;
using Codale.Db;
using Codale.Git;
using Codale.Search;

namespace Codale.App.Services;

public sealed partial class FeatureService
{
    private readonly Lock _topicGate = new();
    private readonly Queue<string> _topicQueue = new();
    private readonly HashSet<string> _queuedTopics = new(StringComparer.Ordinal);
    private Task? _topicWorker;
    private bool _topicsStopping;

    private sealed record PartialTurn(string[] Terms, ProjectKnowledgeIndex.TurnFile[] Files);
    private sealed record TopicCheckpoint(LogPosition Position, Dictionary<string, PartialTurn> Pending);

    private static async Task<IReadOnlyList<ProjectKnowledgeIndex.FileCommit>> ReadGitFileHistoryAsync(
        string worktree, string path, CancellationToken ct) =>
        [.. (await GitFileHistory.ReadAsync(worktree, path, ct).ConfigureAwait(false))
            .Select(c => new ProjectKnowledgeIndex.FileCommit(c.Id, c.Subject, c.FilesChanged))];

    /// <summary>A turn of <paramref name="session"/> ended: index its log from where indexing last stopped.</summary>
    public void QueueTopicBackfill(string session)
    {
        lock (_topicGate)
        {
            if (_topicsStopping) return;
            EnqueueTopic(session);
            _topicWorker ??= Task.Run(DrainTopicQueueAsync);
        }
    }

    private void EnqueueTopic(string session)
    {
        if (_queuedTopics.Add(session)) _topicQueue.Enqueue(session);
    }

    private async Task DrainTopicQueueAsync()
    {
        while (true)
        {
            lock (_topicGate)
            {
                if (_topicsStopping || !_topicQueue.TryDequeue(out var session))
                {
                    _topicWorker = null;
                    return;
                }
                _queuedTopics.Remove(session);
                try
                {
                    if (BackfillPage(session)) EnqueueTopic(session);
                }
                catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or ArgumentException or JsonException) { }
            }
            // Yield between pages so new turns and history deletion can proceed.
            await Task.Delay(15).ConfigureAwait(false);
        }
    }

    private void StopTopicBackfill()
    {
        Task? worker;
        lock (_topicGate)
        {
            _topicsStopping = true;
            worker = _topicWorker;
        }
        worker?.GetAwaiter().GetResult();
    }

    private bool BackfillPage(string session)
    {
        if (Get(session) is null) return false;
        var saved = _data.UseRecords(db => db.Record<TopicCheckpoint>().Key(session).Get()) ?? new(default, new(StringComparer.Ordinal));
        var (next, records) = LogOf(session).ReadIndexPage(saved.Position, 64);
        if (next == saved.Position) return false;

        _data.UseRecords(db => db.WriteBatch(batch =>
        {
            foreach (var record in records)
                IndexHistoryRecord(batch, session, saved.Pending, record);
            batch.Put(session, saved with { Position = next });
        }));
        return true;
    }

    private void IndexHistoryRecord(CodaleDatabase.RecordBatch batch, string session, Dictionary<string, PartialTurn> pending, JsonElement record)
    {
        var chat = Text(record, "chat") ?? "";
        var kind = Text(record, "type");
        var turn = pending.GetValueOrDefault(chat) ?? new([], []);
        if (kind == "user")
        {
            if (!record.TryGetProperty("steer", out var steer) || steer.ValueKind != JsonValueKind.True)
                turn = new([], []);
            pending[chat] = turn with { Terms = MergeTerms(turn.Terms, Text(record, "text"), 24) };
        }
        else if (pending.ContainsKey(chat))
        {
            switch (kind)
            {
                case "assistant":
                    pending[chat] = turn with { Terms = MergeTerms(turn.Terms, Text(record, "text"), 48) };
                    break;
                case "tool":
                    var name = Text(record, "name") ?? "";
                    if ((name.Contains("read", StringComparison.OrdinalIgnoreCase) || name.Equals("view", StringComparison.OrdinalIgnoreCase))
                        && record.TryGetProperty("input", out var input)
                        && (Text(input, "file_path") ?? Text(input, "path") ?? Text(input, "file")) is { } path)
                        pending[chat] = turn with { Files = MergeFiles(turn.Files, [new(path, false)]) };
                    break;
                case "toolDone":
                    if (!record.TryGetProperty("error", out var error) || error.ValueKind != JsonValueKind.True)
                        pending[chat] = turn with { Files = MergeFiles(turn.Files, EditPaths(record)) };
                    break;
                case "turnEnd":
                    pending.Remove(chat);
                    if (Text(record, "turnId") is not { } turnId) break;
                    var worktree = Text(record, "worktree") ?? _projectPath;
                    var evidence = Paths(record, "edited", true).Concat(Paths(record, "read", false));
                    Knowledge.RecordTurnTopic(session, turnId,
                        worktree, string.Join(' ', turn.Terms), MergeFiles(turn.Files, evidence), batch);
                    break;
            }
        }
    }

    private static string[] MergeTerms(string[] existing, string? text, int limit) =>
        [.. existing.Concat(SearchText.RetrievalTerms(text is { Length: > 4000 } ? text[..4000] : text ?? ""))
            .Distinct(StringComparer.Ordinal).Take(limit)];

    private static ProjectKnowledgeIndex.TurnFile[] MergeFiles(IEnumerable<ProjectKnowledgeIndex.TurnFile> old,
        IEnumerable<ProjectKnowledgeIndex.TurnFile> added) =>
        [.. old.Concat(added).GroupBy(f => f.Path, StringComparer.OrdinalIgnoreCase)
            .Select(g => new ProjectKnowledgeIndex.TurnFile(g.Key, g.Any(f => f.Edited)))
            .OrderByDescending(f => f.Edited).Take(64)];

    private static string? Text(JsonElement record, string key) =>
        record.ValueKind == JsonValueKind.Object && record.TryGetProperty(key, out var value)
        && value.ValueKind == JsonValueKind.String ? value.GetString() : null;

    private static IEnumerable<ProjectKnowledgeIndex.TurnFile> Paths(JsonElement record, string key, bool edited) =>
        record.TryGetProperty(key, out var array) && array.ValueKind == JsonValueKind.Array
            ? array.EnumerateArray().Where(e => e.ValueKind == JsonValueKind.String)
                .Select(e => new ProjectKnowledgeIndex.TurnFile(e.GetString()!, edited))
            : [];

    private static IEnumerable<ProjectKnowledgeIndex.TurnFile> EditPaths(JsonElement record)
    {
        if (record.TryGetProperty("change", out var change)
            && (Text(change, "filePath") ?? Text(change, "FilePath")) is { } path) yield return new(path, true);
        if (!record.TryGetProperty("changes", out var changes) || changes.ValueKind != JsonValueKind.Array) yield break;
        foreach (var item in changes.EnumerateArray())
            if ((Text(item, "filePath") ?? Text(item, "FilePath")) is { } itemPath) yield return new(itemPath, true);
    }
}
