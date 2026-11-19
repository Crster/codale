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
            var all = Everything(db).ToList();
            var duplicate = all.FirstOrDefault(n => n.Id != note.Id && Normalize(n.Text) == Normalize(text));
            if (duplicate is not null)
            {
                note = duplicate;
                return;
            }
            // A reworded version of an existing note is an update of it, not a second note saying the same thing.
            var similar = all.Where(n => n.Id != note.Id && Similarity(n.Text, text) >= 0.8)
                .OrderByDescending(n => Similarity(n.Text, text)).FirstOrDefault();
            if (similar is not null)
            {
                note = note with { Id = similar.Id, Title = similar.Title, Tags = [.. similar.Tags.Concat(note.Tags).Distinct(StringComparer.OrdinalIgnoreCase)] };
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
        if (existing.Any(n => Normalize(n.Text) == Normalize(text) || Similarity(n.Text, text) >= 0.9)) return null;
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

    /// <summary>
    /// The notes that best answer <paramref name="query"/>, best first; empty when none relate to it. Words found in a
    /// note's title or tags count for more than words in its body, and the meaning-based (embedding) score adds to the
    /// word score instead of replacing it, so a note both related and worded alike rises above one that is only either.
    /// </summary>
    public IReadOnlyList<MemoryNote> Search(string query, int limit = 5) => (databases ?? WorkspaceDb.Shared).Use(directory, db =>
    {
        query = query.Trim();
        if (query.Length == 0) return (IReadOnlyList<MemoryNote>)[];
        var terms = Terms(query);
        var scores = new Dictionary<string, (MemoryNote Note, double Score)>();
        var now = DateTimeOffset.Now;
        foreach (var note in Everything(db))
        {
            var head = Terms($"{note.Title} {string.Join(' ', note.Tags)}");
            var body = Terms(note.Text);
            var score = 0.0;
            if (terms.Count > 0)
            {
                var inHead = terms.Count(t => Matches(head, t)) / (double)terms.Count;
                var inBody = terms.Count(t => Matches(head, t) || Matches(body, t)) / (double)terms.Count;
                score = inBody * 0.6 + inHead * 0.4;
            }
            if (Searchable(note).Contains(query, StringComparison.OrdinalIgnoreCase)) score = Math.Max(score, 1);
            if (score > 0) scores[note.Id] = (note, score);
        }

        foreach (var hit in db.Open("memory").Query(query, Math.Max(limit * 2, 10)))
        {
            if (hit.Score < 0.35 || hit.Node.ReadJson<MemoryNote>() is not { } note) continue;
            var lexical = scores.TryGetValue(note.Id, out var existing) ? existing.Score : 0;
            scores[note.Id] = (note, lexical + hit.Score * 0.5);
        }

        // A recent note edges out an equally good old one; the nudge is small so relevance still decides.
        return (IReadOnlyList<MemoryNote>)[.. scores.Values
            .Select(s => (s.Note, Score: s.Score + Math.Max(0, 0.05 - (now - s.Note.SavedAt).TotalDays / 3650)))
            .OrderByDescending(s => s.Score).Take(limit).Select(s => s.Note)];
    });

    // "build" finds "building", "tests" finds "test": a shared four-letter stem is a match.
    private static bool Matches(HashSet<string> words, string term) =>
        words.Contains(term) || (term.Length >= 5 && words.Any(w => w.Length >= 5 && (w.StartsWith(term[..4]) && term.StartsWith(w[..4]))));

    /// <summary>How much of the shorter text's words the other repeats, 0 to 1; 0 when either is too short or they differ much in size.</summary>
    private static double Similarity(string a, string b)
    {
        var x = Terms(a);
        var y = Terms(b);
        if (x.Count == 0 || y.Count == 0) return 0;
        var small = Math.Min(x.Count, y.Count);
        if (small < 4 || small < Math.Max(x.Count, y.Count) / 2) return 0; // too short or too lopsided to call a rewording
        return x.Count(y.Contains) / (double)small;
    }

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
            .Select(w => w.ToLowerInvariant()).Where(w => w.Length > 2 && !Stop.Contains(w))];

    private static readonly HashSet<string> Stop = ["the", "and", "for", "are", "but", "not", "you", "with", "that", "this", "from", "have", "has", "was", "were",
        "what", "when", "how", "should", "can", "will", "use", "using", "our", "their", "its", "into", "than", "then", "also", "about"];

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
