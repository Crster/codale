using System.Text;

using Codale.Db;

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
public sealed class MemoryStore(string directory, WorkspaceDatabasePool? databases = null)
{
    private const int MaxText = 8000;
    private const string SearchIndex = "workspace-memory";
    private WorkspaceDatabasePool Pool => databases ?? WorkspaceDb.Shared;

    private T Use<T>(Func<CodaleDatabase, T> work) => Pool.UseRecords(directory, work);

    private static void StageNote(CodaleDatabase db, CodaleDatabase.RecordBatch batch, string id, MemoryNote note) =>
        batch.Put(id, note, db.Index(SearchIndex).Key([note.Title, string.Join(' ', note.Tags), Searchable(note)]));
    /// <exception cref="ArgumentException">There is no text, or no title to name it by.</exception>
    public MemoryNote Save(string title, string text, IEnumerable<string>? tags = null, string? category = null, MemorySource? source = null)
    {
        var note = CreateNote(title, text, tags, category, source);
        return Use(db => Save(db, note, Notes(db)));
    }

    private static MemoryNote CreateNote(string title, string text, IEnumerable<string>? tags, string? category, MemorySource? source)
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

        return new MemoryNote(IdOf(title), title, text,
            [.. (tags ?? []).Select(t => t.Trim()).Where(t => t.Length > 0).Distinct(StringComparer.OrdinalIgnoreCase)],
            DateTimeOffset.Now) { Category = category, Source = source };
    }

    private static MemoryNote[] Notes(CodaleDatabase db) => db.Record<MemoryNote>().Find().Select(row => row.Value).ToArray();

    private static MemoryNote Save(CodaleDatabase db, MemoryNote note, MemoryNote[] all)
    {
        var text = note.Text;
        var normalized = Normalize(text);
        var duplicate = all.FirstOrDefault(n => n.Id != note.Id && Normalize(n.Text) == normalized);
        if (duplicate is not null) return duplicate;
        var similar = all.Where(n => n.Id != note.Id).Select(n => (Note: n, Score: Similarity(n.Text, text)))
            .Where(match => match.Score >= 0.8).OrderByDescending(match => match.Score).FirstOrDefault().Note;
        if (similar is not null)
        {
            note = note with { Id = similar.Id, Title = similar.Title, Tags = [.. similar.Tags.Concat(note.Tags).Distinct(StringComparer.OrdinalIgnoreCase)] };
        }
        if (all.FirstOrDefault(n => n.Id == note.Id) is { } previous)
            note = note with { Category = note.Category ?? previous.Category, Source = note.Source ?? previous.Source };
        db.WriteBatch(batch => StageNote(db, batch, note.Id, note));
        return note;
    }

    /// <summary>Reconciles host and CLI writes. A model may replace only an existing id it was shown.</summary>
    public MemoryNote? Remember(string title, string text, string category, MemorySource source, string? replacesId = null)
    {
        if (MemoryCapture.ContainsSecret(text) || MemoryCapture.ContainsSecret(source.Evidence)) return null;
        return Use<MemoryNote?>(db =>
        {
            var existing = Notes(db);
            var normalized = Normalize(text);
            if (existing.Any(n => Normalize(n.Text) == normalized || Similarity(n.Text, text) >= 0.9)) return null;
            var replaced = replacesId is not null ? existing.FirstOrDefault(n => n.Id == replacesId) : null;
            return Save(db, CreateNote(replaced?.Title ?? title, text, [category], category, source), existing);
        });
    }

    private static string Normalize(string text) => string.Join(' ', text.Split((char[]?)null, StringSplitOptions.RemoveEmptyEntries)).ToLowerInvariant().TrimEnd('.', '!');

    public MemoryNote? Get(string id) =>
        Use(db => db.Record<MemoryNote>().Key(IdOf(id)).Get());

    public bool Forget(string id) =>
        Use(db => db.Record<MemoryNote>().Key(IdOf(id)).TryDelete(out _));

    /// <summary>Every note, newest first.</summary>
    public IReadOnlyList<MemoryNote> List() => Use(db => db.Record<MemoryNote>().Find().Select(row => row.Value)
        .OrderByDescending(n => n.SavedAt).ToArray());

    /// <summary>
    /// The notes that best answer <paramref name="query"/>, best first; empty when none relate to it. Words found in a
    /// note's title or tags count for more than words in its body, and the meaning-based (embedding) score adds to the
    /// word score instead of replacing it, so a note both related and worded alike rises above one that is only either.
    /// </summary>
    public IReadOnlyList<MemoryNote> Search(string query, int limit = 5) => Use<IReadOnlyList<MemoryNote>>(db =>
    {
        query = query.Trim();
        if (query.Length == 0 || limit <= 0) return [];
        var terms = Terms(query);
        var scores = new Dictionary<string, (MemoryNote Note, double Score)>();
        var now = DateTimeOffset.Now;
        var candidates = db.Index(SearchIndex).Find(query, (int)Math.Clamp((long)limit * 5, 16, int.MaxValue));
        foreach (var hit in candidates)
        {
            if (hit.Record<MemoryNote>()?.Get() is not { } note) continue;
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
            if (score > 0 || hit.Score >= 0.35f) scores[note.Id] = (note, score + Math.Min(hit.Score, 1) * 0.5);
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
