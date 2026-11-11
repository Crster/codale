using Codale.Core.Features;
using Codale.Db;

namespace Codale.Search;

/// <summary>
/// The project's features and the sessions behind them, kept in the project's own database (beside its logs, so
/// indexing on a background thread never queues behind the UI thread's writes). Layout:
/// <list type="bullet">
/// <item><c>feature.ID</c> = the feature (keyword field <c>feature</c>); <c>feature.ID.file.PATH</c> = when the agent last wrote that file.</item>
/// <item><c>session.ID</c> = the provider conversation behind a feature; <c>delivered.PROVIDERSESSION.KEY</c> = context it already has.</item>
/// </list>
/// </summary>
public sealed partial class ProjectKnowledgeIndex
{
    private const int RecentPromptChars = 2_000;
    private const string FeatureField = "feature";

    private sealed record FeatureRecord(string Name, string Description, string Summary, long SummaryOffset, long LogBytes,
        string Prompts, long CreatedAt, long UpdatedAt);

    private DbNode FeatureRoot => db.Open("feature");

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
        db.Batch(() =>
        {
            FeatureRoot.Open(id).WriteJson(new FeatureRecord(name, description, "", 0, 0, "", now, now));
            RefreshSearch(id);
        });
        return GetFeature(id)!;
    }

    /// <summary>Most recently active first.</summary>
    public IReadOnlyList<FeatureInfo> ListFeatures() =>
        [.. FeatureRoot.Children()
            .Select(id => (Id: id, Record: FeatureRoot.Open(id).ReadJson<FeatureRecord>()))
            .Where(x => x.Record is not null)
            .OrderByDescending(x => x.Record!.UpdatedAt)
            .Select(x => ToInfo(x.Id, x.Record!))];

    public FeatureInfo? GetFeature(string id) =>
        FeatureRoot.Open(id).ReadJson<FeatureRecord>() is { } record ? ToInfo(id, record) : null;

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
        var node = FeatureRoot.Open(id);
        db.Batch(() =>
        {
            if (node.ReadJson<FeatureRecord>() is not { } record) return;
            node.WriteJson(change(record));
            RefreshSearch(id);
        });
    }

    /// <summary>Removes a feature with its files and everything recorded for its session; its log folder is the caller's.</summary>
    public void DeleteFeature(string id) => db.Batch(() =>
    {
        FeatureRoot.Open(id).Remove();

        var session = db.Open("session").Open(id);
        if (session.ReadJson<ProviderSession>() is { } provider) db.Open("delivered").Open(provider.ProviderSessionId).Remove();
        session.Remove();

        foreach (var node in db.Open("knowledge").Descendants())
            if (node.ReadJson<KnowledgeRecord>()?.SessionId == id) node.Remove();
        foreach (var node in db.Open("learned").Descendants())
            if (node.ReadJson<LearnedRecord>()?.SessionId == id) node.Remove();
        foreach (var node in db.Open("retrieval").Descendants())
            if (node.ReadJson<RetrievalRecord>()?.SessionId == id) node.Remove();
    });

    /// <summary>Features most likely to own <paramref name="text"/>, best match first.</summary>
    public IReadOnlyList<FeatureInfo> SearchFeatures(string text, int limit)
    {
        var terms = SearchText.MatchQueryTerms(text);
        if (terms.Count == 0) return [];
        return [.. FeatureRoot.Match(FeatureField, Terms(terms), limit)
            .Select(hit => GetFeature(hit.Node.Name))
            .OfType<FeatureInfo>()];
    }

    /// <summary>Rebuilds a feature's search entry from its name, description, summary, recent prompts and file paths.</summary>
    private void RefreshSearch(string id)
    {
        var node = FeatureRoot.Open(id);
        if (node.ReadJson<FeatureRecord>() is not { } r) return;
        var files = string.Join(' ', node.Open("file").Children());
        node.Index(FeatureField, SearchText.Tokens($"{r.Name} {r.Description} {r.Summary} {r.Prompts} {files}"));
    }

    private static string Tail(string text) => text.Length <= RecentPromptChars ? text : text[^RecentPromptChars..];

    // ---- the files the agent wrote for a feature

    public void RecordSessionFile(string sessionId, string relativePath) =>
        FeatureRoot.Open(sessionId).Open("file").Open(relativePath).Write(Now.ToString(System.Globalization.CultureInfo.InvariantCulture));

    /// <summary>Paths the agent wrote for a feature, newest first.</summary>
    public IReadOnlyList<string> FilesOf(string featureId, int limit)
    {
        var files = FeatureRoot.Open(featureId).Open("file");
        return [.. files.Children()
            .Select(path => (Path: path, At: long.TryParse(files.Open(path).Read(), out var at) ? at : 0))
            .OrderByDescending(x => x.At)
            .Take(limit)
            .Select(x => x.Path)];
    }

    public int FileCount(string featureId) => FeatureRoot.Open(featureId).Open("file").ChildCount();

    // ---- provider sessions

    public void SetProviderSession(ProviderSession session) => db.Open("session").Open(session.SessionId).WriteJson(session);

    public ProviderSession? GetProviderSession(string id) => db.Open("session").Open(id).ReadJson<ProviderSession>();

    public IReadOnlyList<string> DeliveredContext(string providerSessionId) => db.Open("delivered").Open(providerSessionId).Children();

    public void MarkContextDelivered(string providerSessionId, IReadOnlyList<string> keys)
    {
        var delivered = db.Open("delivered").Open(providerSessionId);
        db.Batch(() =>
        {
            foreach (var key in keys.Where(k => k.Length > 0)) delivered.Open(key).Write("1");
        });
    }

    public void ClearDeliveredContext(string providerSessionId) => db.Open("delivered").Open(providerSessionId).Remove();
}
