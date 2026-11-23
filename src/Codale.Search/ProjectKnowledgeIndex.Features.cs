using Codale.Core.Features;
using Codale.Db;

namespace Codale.Search;

public sealed partial class ProjectKnowledgeIndex
{
    private const int RecentPromptChars = 2_000;
    private const string FeatureField = "feature";

    private sealed record FeatureRecord(string Name, string Description, string Summary, long SummaryOffset, long LogBytes,
        string Prompts, long CreatedAt, long UpdatedAt);
    private sealed record FeatureFile(string Path, long At);
    private readonly Lock _featureGate = new();
    public event Action? FeaturesChanged;

    private static long Now => DateTimeOffset.Now.ToUnixTimeMilliseconds();

    private static FeatureInfo ToInfo(string id, FeatureRecord r) => new()
    {
        Id = id,
        Name = r.Name,
        Description = r.Description,
        Summary = r.Summary,
        SummaryOffset = r.SummaryOffset,
        LogBytes = r.LogBytes,
        CreatedAt = DateTimeOffset.FromUnixTimeMilliseconds(r.CreatedAt),
        UpdatedAt = DateTimeOffset.FromUnixTimeMilliseconds(r.UpdatedAt),
    };

    public FeatureInfo CreateFeature(string name, string description)
    {
        var id = Guid.NewGuid().ToString("N");
        var now = Now;
        var record = new FeatureRecord(name, description, "", 0, 0, "", now, now);
        db.WriteBatch(batch => StageFeature(batch, id, record, []));
        FeaturesChanged?.Invoke();
        return ToInfo(id, record);
    }

    /// <summary>Most recently active first.</summary>
    public IReadOnlyList<FeatureInfo> ListFeatures() =>
        [.. db.Record<FeatureRecord>().Find().OrderByDescending(row => row.Value.UpdatedAt).Select(row => ToInfo(row.Key, row.Value))];

    public FeatureInfo? GetFeature(string id) =>
        db.Record<FeatureRecord>().Key(id).Get() is { } record ? ToInfo(id, record) : null;

    public void RenameFeature(string id, string name) => ChangeFeature(id, r => r with { Name = name });

    /// <summary>Notes what a turn added to the log: its size now, and the prompt, which the feature is found by later.</summary>
    public void RecordProgress(string id, long logBytes, string? prompt) => ChangeFeature(id, r => r with
    {
        LogBytes = logBytes,
        UpdatedAt = Now,
        Prompts = prompt is { Length: > 0 } ? Tail(r.Prompts + "\n" + prompt) : r.Prompts,
    });

    /// <summary>A new summary, current up to <paramref name="logOffset"/> bytes of the log.</summary>
    public void SetSummary(string id, string summary, long logOffset, long? logBytes = null) => ChangeFeature(id, r => r with
    {
        Summary = summary,
        SummaryOffset = logOffset,
        LogBytes = logBytes ?? r.LogBytes,
    });

    private void ChangeFeature(string id, Func<FeatureRecord, FeatureRecord> change)
    {
        lock (_featureGate)
        {
            if (db.Record<FeatureRecord>().Key(id).Get() is not { } record) return;
            var updated = change(record);
            var prefix = Scope(id);
            var files = db.Record<FeatureFile>().Keys(prefix).Select(key => key[prefix.Length..]);
            db.WriteBatch(batch => StageFeature(batch, id, updated, files));
        }
        FeaturesChanged?.Invoke();
    }

    /// <summary>Removes a feature with its files and provider session; its log folder and turn topics are the caller's.</summary>
    public void DeleteFeature(string id)
    {
        lock (_featureGate)
        {
            var files = db.Record<FeatureFile>().Keys(Scope(id));
            db.WriteBatch(batch =>
            {
                batch.Delete<FeatureRecord>(id);
                batch.Delete<ProviderSession>(id);
                foreach (var file in files) batch.Delete<FeatureFile>(file);
            });
        }
        FeaturesChanged?.Invoke();
    }

    /// <summary>Features most likely to own <paramref name="text"/>, best match first.</summary>
    public IReadOnlyList<FeatureInfo> SearchFeatures(string text, int limit)
    {
        var terms = SearchText.MatchQueryTerms(text);
        return terms.Count == 0
            ? []
            : [.. db.Index(FeatureField).FindKeywords(Terms(terms), limit)
            .Select(hit => GetFeature(hit.Key))
            .OfType<FeatureInfo>()];
    }

    /// <summary>Rebuilds a feature's search entry from its name, description, summary, recent prompts and file paths.</summary>
    private void StageFeature(CodaleDatabase.RecordBatch batch, string id, FeatureRecord r, IEnumerable<string> paths) =>
        batch.Put(id, r, db.Index(FeatureField).Key([r.Name, r.Description, r.Summary, r.Prompts, string.Join(' ', paths)], mode: SemanticMode.Bm25));

    private static string Tail(string text) => text.Length <= RecentPromptChars ? text : text[^RecentPromptChars..];

    // ---- the files the agent wrote for a feature

    public void RecordSessionFile(string sessionId, string relativePath) => RecordSessionFiles(sessionId, [relativePath]);

    public void RecordSessionFiles(string sessionId, IEnumerable<string> relativePaths)
    {
        var files = relativePaths.Distinct(StringComparer.Ordinal).ToArray();
        if (files.Length == 0) return;
        var prefix = Scope(sessionId);
        var now = Now;
        lock (_featureGate)
            db.Record<FeatureFile>().PutMany(files.Select(path => new KeyValuePair<string, FeatureFile>(prefix + path, new(path, now))));
        FeaturesChanged?.Invoke();
    }

    /// <summary>Paths the agent wrote for a feature, newest first.</summary>
    public IReadOnlyList<string> FilesOf(string featureId, int limit)
    {
        return [.. db.Record<FeatureFile>().FindPrefix(Scope(featureId))
            .OrderByDescending(x => x.Value.At)
            .Take(limit)
            .Select(x => x.Value.Path)];
    }

    public int FileCount(string featureId) => db.Record<FeatureFile>().Keys(Scope(featureId)).Count;

    // ---- provider sessions

    public void SetProviderSession(ProviderSession session) => db.Record<ProviderSession>().Key(session.SessionId).Value(session);

    public ProviderSession? GetProviderSession(string id) => db.Record<ProviderSession>().Key(id).Get();
}
