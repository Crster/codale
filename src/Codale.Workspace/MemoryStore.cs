using System.Text;

namespace Codale.Workspace;

/// <summary>A note the agent saved about the workspace.</summary>
public sealed record MemoryNote(string Id, string Title, string Text, IReadOnlyList<string> Tags, DateTimeOffset SavedAt)
{
    public string? Category { get; init; }
    public MemorySource? Source { get; init; }
}

public sealed record MemorySource(string? SessionId, string Kind, string Evidence, DateTimeOffset CapturedAt);

/// <summary>
/// What the agent keeps between conversations: short notes about the workspace (decisions, conventions, where
/// things are, what the user prefers). Saving a note under a title that exists replaces it. Recall ranks by
/// meaning (the database's embeddings) and by the words themselves, so an exact term still finds its note.
/// </summary>
/// <remarks>Layout: <c>memory.ID</c> is the note as JSON; the note's text is embedded on that same node.</remarks>
public sealed class MemoryStore(string directory, WorkspaceDatabasePool? databases = null)
{
    private const int MaxText = 8000;
    /// <exception cref="ArgumentException">There is no text, or no title to name it by.</exception>
    public MemoryNote Save(string title, string text, IEnumerable<string>? tags = null, string? category = null, MemorySource? source = null)
    {
        title = title.Trim();
        text = text.Trim();
        if (title.Length == 0 || text.Length == 0)
        {
            throw new ArgumentException("A memory needs a title and a text.");
        }

        if (text.Length > MaxText)
        {
            throw new ArgumentException($"A memory is a short note: keep it under {MaxText} characters.");
        }

        var note = new MemoryNote(IdOf(title), title, text,
            [.. (tags ?? []).Select(t => t.Trim()).Where(t => t.Length > 0).Distinct(StringComparer.OrdinalIgnoreCase)],
            DateTimeOffset.Now) { Category = category, Source = source };
        (databases ?? WorkspaceDb.Shared).Use(directory, db => db.Batch(() =>
        {
            var duplicate = Everything(db).FirstOrDefault(n => Normalize(n.Text) == Normalize(text));
            if (duplicate is not null && duplicate.Id != note.Id)
            {
                note = duplicate;
                return;
            }
            var node = db.Open("memory").Open(note.Id);
            if (node.ReadJson<MemoryNote>() is { } previous)
                note = note with { Category = note.Category ?? previous.Category, Source = note.Source ?? previous.Source };
            node.WriteJson(note);
            node.Embed(Searchable(note));
        }));
        return note;
    }

    /// <summary>Reconciles host and CLI writes. A model may replace only an existing id it was shown.</summary>
    public MemoryNote? Remember(string title, string text, string category, MemorySource source, string? replacesId = null)
    {
        if (MemoryCapture.ContainsSecret(text) || MemoryCapture.ContainsSecret(source.Evidence)) return null;
        var existing = List();
        var duplicate = existing.FirstOrDefault(n => Normalize(n.Text) == Normalize(text));
        if (duplicate is not null) return null;
        var replaced = replacesId is not null ? existing.FirstOrDefault(n => n.Id == replacesId) : null;
        return Save(replaced?.Title ?? title, text, [category], category, source);
    }

    private static string Normalize(string text) => string.Join(' ', text.Split((char[]?)null, StringSplitOptions.RemoveEmptyEntries)).ToLowerInvariant().TrimEnd('.', '!');

    public MemoryNote? Get(string id) =>
        (databases ?? WorkspaceDb.Shared).Use(directory, db => db.Open("memory").Open(IdOf(id)).ReadJson<MemoryNote>());

    public bool Forget(string id) =>
        (databases ?? WorkspaceDb.Shared).Use(directory, db => db.Open("memory").Open(IdOf(id)).Remove());

    /// <summary>Every note, newest first.</summary>
    public IReadOnlyList<MemoryNote> List() => (databases ?? WorkspaceDb.Shared).Use(directory, db =>
        (IReadOnlyList<MemoryNote>)[.. Everything(db).OrderByDescending(n => n.SavedAt)]);

    /// <summary>The notes that best answer <paramref name="query"/>, best first; empty when none relate to it.</summary>
    public IReadOnlyList<MemoryNote> Search(string query, int limit = 5) => (databases ?? WorkspaceDb.Shared).Use(directory, db =>
    {
        var terms = Terms(query);
        var scores = new Dictionary<string, (MemoryNote Note, double Score)>();
        foreach (var note in Everything(db))
        {
            var words = Terms(Searchable(note));
            var overlap = terms.Count == 0 ? 0 : terms.Count(words.Contains) / (double)terms.Count;
            var phrase = Searchable(note).Contains(query.Trim(), StringComparison.OrdinalIgnoreCase) ? 1 : 0;
            var score = Math.Max(overlap, phrase);
            if (score > 0)
            {
                scores[note.Id] = (note, score);
            }
        }

        foreach (var hit in db.Open("memory").Query(query, limit))
        {
            if (hit.Score < 0.35 || hit.Node.ReadJson<MemoryNote>() is not { } note)
            {
                continue;
            }

            scores[note.Id] = scores.TryGetValue(note.Id, out var lexical)
                ? (note, Math.Max(lexical.Score, hit.Score))
                : (note, hit.Score);
        }

        return (IReadOnlyList<MemoryNote>)[.. scores.Values.OrderByDescending(s => s.Score).Take(limit).Select(s => s.Note)];
    });

    private static IEnumerable<MemoryNote> Everything(Codale.Db.CodaleDb db)
    {
        var memory = db.Open("memory");
        foreach (var id in memory.Children())
        {
            if (memory.Open(id).ReadJson<MemoryNote>() is { } note)
            {
                yield return note;
            }
        }
    }

    private static string Searchable(MemoryNote note) => $"{note.Title}\n{note.Text}\n{string.Join(' ', note.Tags)}";

    private static HashSet<string> Terms(string text) =>
        [.. text.Split([' ', '\n', '\r', '\t', ',', '.', ';', ':', '(', ')', '"', '\'', '/', '\\'], StringSplitOptions.RemoveEmptyEntries)
            .Select(w => w.ToLowerInvariant()).Where(w => w.Length > 2)];

    /// <summary>A title's id: lower-case words joined by dashes, so "Build command" and "build  command" are one note.</summary>
    private static string IdOf(string title)
    {
        var id = new StringBuilder();
        foreach (var c in title.Trim().ToLowerInvariant())
        {
            id.Append(char.IsLetterOrDigit(c) ? c : '-');
        }

        var slug = string.Join('-', id.ToString().Split('-', StringSplitOptions.RemoveEmptyEntries));
        return slug.Length == 0 ? "note" : slug.Length > 80 ? slug[..80] : slug;
    }
}
