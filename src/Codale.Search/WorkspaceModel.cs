using System.Text.Json;
using System.Text.Json.Serialization;

using Codale.Db;
using Codale.Search.Engine;
using Codale.Workspace;

namespace Codale.Search;

/// <summary>A learned pair of identifiers: <see cref="Next"/> was written straight after <see cref="Previous"/>, <see cref="Count"/> times.</summary>
internal readonly record struct WordPair(string Previous, string Next, int Count);

/// <summary>
/// The statistics the workspace model keeps per file. Each one is a channel with its own version:
/// when a channel's computation changes, bump its version and the files stored under the old one are
/// read again - the other channels and the other files are kept.
/// </summary>
public static class ModelChannels
{
    public const string Terms = "terms";
    public const string Symbols = "symbols";
    public const string Transitions = "transitions";

    /// <summary>The versions this build computes and accepts from the stored model.</summary>
    public static IReadOnlyDictionary<string, int> Current { get; } = new Dictionary<string, int>
    {
        [Terms] = 1,
        [Symbols] = 5,
        [Transitions] = 1,
    };
}

/// <summary>
/// A model of one workspace, trained from its files and kept current one file at a time.
/// </summary>
/// <remarks>
/// <para>
/// Training is counting: each file contributes its identifiers, its declarations and the identifier
/// pairs written next to each other (a first-order model of what follows what). The model keeps those
/// per-file statistics plus the aggregates over all files (document frequencies, declared names, pair
/// counts). <see cref="Put"/> and <see cref="Remove"/> subtract a file's old contribution and add the
/// new one, so a change costs the size of that file, not of the workspace.
/// </para>
/// <para>
/// The model is saved in a workspace database and loaded on the next start. A file whose
/// length and write time still match its stored entry is not read again, so a restart needs no
/// re-tokenising of an unchanged workspace.
/// </para>
/// </remarks>
/// <param name="path">Database directory; null keeps the model in memory only.</param>
/// <param name="key">Workspace identity within the database.</param>
public sealed class WorkspaceModel
{
    public WorkspaceModel(string? path = null, string key = "model")
    {
        _directory = path is null ? null : new Lazy<string>(() => File.Exists(path) ? path + ".records" : path);
        _key = key;
    }

    internal WorkspaceModel(Func<string> directory, string key)
    {
        _directory = new Lazy<string>(directory, LazyThreadSafetyMode.PublicationOnly);
        _key = key;
    }
    private static readonly Dictionary<string, int> CurrentChannels = new(ModelChannels.Current);

    private readonly Lock _gate = new();
    private readonly Lock _saveGate = new();
    private readonly Lazy<string>? _directory;
    private readonly string _key;
    private readonly Dictionary<string, Entry> _files = [with(StringComparer.OrdinalIgnoreCase)];
    private readonly Dictionary<string, int> _termFiles = [with(StringComparer.Ordinal)];
    private readonly Dictionary<string, Dictionary<SourceSymbolKind, int>> _symbols = [with(StringComparer.OrdinalIgnoreCase)];
    private readonly Dictionary<string, Dictionary<string, int>> _next = [with(StringComparer.Ordinal)];

    // Sorted views for prefix lookups; rebuilt on the next query after a change.
    private string[]? _termIndex;
    private string[]? _symbolIndex;
    private bool _loaded;
    private readonly Dictionary<string, long> _dirty = new(StringComparer.OrdinalIgnoreCase);
    private long _revision;

    /// <summary>The one copy of each word the model and the index's files share.</summary>
    internal StringPool Pool { get; } = new();

    /// <summary>Files the model holds statistics for.</summary>
    public int FileCount
    {
        get
        {
            lock (_gate)
            {
                return _files.Count;
            }
        }
    }

    /// <summary>True when something changed since the model was last saved.</summary>
    public bool IsDirty
    {
        get
        {
            lock (_gate)
            {
                return _dirty.Count > 0;
            }
        }
    }

    /// <summary>
    /// The statistics for <paramref name="file"/> are already in the model when the file has the same
    /// length and write time; otherwise the file's old contribution is replaced by the new one.
    /// </summary>
    internal void Put(SourceFile file)
    {
        lock (_gate)
        {
            // The entry shares the file's arrays: a file is immutable, so one copy serves both.
            var entry = new Entry(
                file.RelativePath,
                file.Length,
                file.LastWriteUtc.Ticks,
                file.TermKeys,
                file.TermCounts,
                file.TokenCount,
                file.SymbolArray,
                file.Transitions);

            if (_files.TryGetValue(file.RelativePath, out var known) &&
                known.Length == file.Length && known.LastWriteTicks == file.LastWriteUtc.Ticks)
            {
                if (ReferenceEquals(known.Terms, file.TermKeys))
                {
                    return;
                }

                // Read again but unchanged: the statistics stand, and the stored arrays give way to the file's.
                if (SameStatistics(known, entry))
                {
                    _files[entry.Path] = entry;
                    return;
                }
            }

            if (known is not null)
            {
                Apply(known, -1);
            }

            _files[entry.Path] = entry;
            Apply(entry, +1);
            _dirty[entry.Path] = ++_revision;
        }
    }

    /// <summary>Forgets a file: its contribution to every aggregate is taken back out.</summary>
    internal void Remove(string relativePath)
    {
        lock (_gate)
        {
            if (!_files.Remove(relativePath, out var entry))
            {
                return;
            }

            Apply(entry, -1);
            _dirty[entry.Path] = ++_revision;
        }
    }

    /// <summary>The paths the model holds statistics for.</summary>
    internal IReadOnlyList<string> Paths()
    {
        lock (_gate)
        {
            return [.. _files.Keys];
        }
    }

    /// <summary>
    /// The stored file as a <see cref="SourceFile"/> without its text, when the file is unchanged since the
    /// model last saw it. Null when it is unknown or changed, and the caller reads it.
    /// </summary>
    internal SourceFile? Rehydrate(string relativePath, string fullPath, long length, DateTime lastWriteUtc)
    {
        lock (_gate)
        {
            return !_files.TryGetValue(relativePath, out var entry) ||
                entry.Length != length || entry.LastWriteTicks != lastWriteUtc.Ticks
                ? null
                : new SourceFile(
                relativePath,
                fullPath,
                entry.Length,
                lastWriteUtc,
                null,
                entry.Terms,
                entry.Counts,
                entry.TokenCount,
                entry.Symbols,
                entry.Transitions);
        }
    }

    /// <summary>
    /// The workspace's words that start with <paramref name="prefix"/>, best first. Declared names rank
    /// above plain identifiers; a name that followed <paramref name="previous"/> in the code ranks first of
    /// all, since the model has learned that pairing from the workspace.
    /// </summary>
    public IReadOnlyList<WorkspaceSuggestion> Suggest(string prefix, string? previous = null, int max = 30)
    {
        if (prefix.Length == 0)
        {
            return [];
        }

        lock (_gate)
        {
            // Keyed without case, so a declared name and a plain word of the same spelling are one candidate.
            // Terms are stored in lower case: their text is the spelling the code used after the word before.
            var scores = new Dictionary<string, Candidate>(StringComparer.OrdinalIgnoreCase);

            _symbolIndex ??= [.. _symbols.Keys.OrderBy(k => k, StringComparer.OrdinalIgnoreCase)];
            foreach (var name in Range(_symbolIndex, prefix, StringComparer.OrdinalIgnoreCase, StringComparison.OrdinalIgnoreCase))
            {
                var kinds = _symbols[name];
                var kind = kinds.MaxBy(k => k.Value).Key;
                var files = kinds.Values.Sum();
                scores[name] = new Candidate(name, 10 + Math.Log2(1 + files), kind, files);
            }

            _termIndex ??= [.. _termFiles.Keys.OrderBy(k => k, StringComparer.Ordinal)];
            foreach (var term in Range(_termIndex, prefix.ToLowerInvariant(), StringComparer.Ordinal, StringComparison.Ordinal))
            {
                // A word that is already fully typed has nothing left to complete.
                if (scores.ContainsKey(term) || term.Length == prefix.Length)
                {
                    continue;
                }

                var files = _termFiles[term];
                scores[term] = new Candidate(term, Math.Log2(1 + files), null, files);
            }

            if (previous is { Length: > 0 } && _next.TryGetValue(previous, out var followers))
            {
                foreach (var (word, count) in followers)
                {
                    if (!word.StartsWith(prefix, StringComparison.OrdinalIgnoreCase) || !scores.TryGetValue(word, out var found))
                    {
                        continue;
                    }

                    // A plain word takes the spelling the code used after the previous word; a declared name keeps its own.
                    var text = found.Kind is null ? word : found.Text;
                    scores[word] = found with { Text = text, Score = found.Score + 20 + 5 * Math.Log2(1 + count) };
                }
            }

            return [.. scores.Values
                .OrderByDescending(c => c.Score)
                .ThenBy(c => c.Text.Length)
                .Take(max)
                .Select(c => new WorkspaceSuggestion(c.Text, c.Kind, c.Files))];
        }
    }

    /// <summary>True when the workspace uses <paramref name="word"/>: as a word in some file, or as a declared name.</summary>
    public bool Knows(string word)
    {
        if (word.Length == 0)
        {
            return false;
        }

        lock (_gate)
        {
            return _termFiles.ContainsKey(word.ToLowerInvariant()) || _symbols.ContainsKey(word);
        }
    }

    /// <summary>
    /// The workspace's words spelled closest to <paramref name="word"/>, for a word it does not know:
    /// fewest edits first (a swap of two letters is one), then the most widely used. One edit is allowed
    /// up to four letters, two beyond. A declared name keeps its own casing; a plain word is lower case.
    /// </summary>
    /// <param name="maxEdits">A tighter limit than the default, e.g. 1 for a word still being typed.</param>
    public IReadOnlyList<WorkspaceSuggestion> Correct(string word, int max = 3, int maxEdits = 2)
    {
        if (word.Length < 3 || word.Length > SourceTokens.MaxLength)
        {
            return [];
        }

        var lower = word.ToLowerInvariant();
        var limit = Math.Min(maxEdits, word.Length <= 4 ? 1 : 2);
        var found = new Dictionary<string, (string Text, int Distance, SourceSymbolKind? Kind, int Files)>(StringComparer.OrdinalIgnoreCase);

        lock (_gate)
        {
            foreach (var (term, files) in _termFiles)
            {
                if (Math.Abs(term.Length - lower.Length) > limit || term.Length < 3)
                {
                    continue;
                }

                var distance = EditDistance(lower, term, limit);
                if (distance <= limit)
                {
                    found[term] = (term, distance, null, files);
                }
            }

            foreach (var (name, kinds) in _symbols)
            {
                if (Math.Abs(name.Length - lower.Length) > limit)
                {
                    continue;
                }

                var distance = EditDistance(lower, name.ToLowerInvariant(), limit);
                if (distance <= limit)
                {
                    var files = kinds.Values.Sum();
                    var kind = kinds.MaxBy(k => k.Value).Key;
                    var known = found.TryGetValue(name, out var term) ? term.Files : 0;
                    found[name] = (name, distance, kind, Math.Max(files, known));
                }
            }
        }

        return [.. found.Values
            .Where(c => c.Distance > 0)
            .OrderBy(c => c.Distance)
            .ThenByDescending(c => c.Files)
            .ThenBy(c => c.Text.Length)
            .Take(max)
            .Select(c => new WorkspaceSuggestion(c.Text, c.Kind, c.Files))];
    }

    /// <summary>Edits from <paramref name="a"/> to <paramref name="b"/> (a swap of neighbours counts once), or more than <paramref name="limit"/> once it is sure to exceed it.</summary>
    internal static int EditDistance(string a, string b, int limit)
    {
        var n = a.Length;
        var m = b.Length;
        Span<int> before = stackalloc int[m + 1];
        Span<int> previous = stackalloc int[m + 1];
        Span<int> current = stackalloc int[m + 1];
        for (var j = 0; j <= m; j++)
        {
            previous[j] = j;
        }

        for (var i = 1; i <= n; i++)
        {
            current[0] = i;
            var best = current[0];
            for (var j = 1; j <= m; j++)
            {
                var cost = a[i - 1] == b[j - 1] ? 0 : 1;
                var value = Math.Min(Math.Min(previous[j] + 1, current[j - 1] + 1), previous[j - 1] + cost);
                if (i > 1 && j > 1 && a[i - 1] == b[j - 2] && a[i - 2] == b[j - 1])
                {
                    value = Math.Min(value, before[j - 2] + 1);
                }

                current[j] = value;
                best = Math.Min(best, value);
            }

            if (best > limit)
            {
                return limit + 1;
            }

            var spare = before;
            before = previous;
            previous = current;
            current = spare;
        }

        return previous[m];
    }

    /// <summary>Loads the saved model once. A missing, unreadable or other-format file starts empty.</summary>
    public void Load()
    {
        lock (_gate)
        {
            if (_loaded)
            {
                return;
            }

            if (_directory is null)
            {
                _loaded = true;
                return;
            }

            IReadOnlyList<RecordEntry<StoredFile>> stored;
            try
            {
                stored = UseStorage(db => db.Record<StoredFile>().FindPrefix(StoragePrefix));
            }
            catch (Exception ex) when (StorageFailure(ex))
            {
                return;
            }

            _loaded = true;
            foreach (var row in stored)
            {
                var file = row.Value;
                // A file stored under another channel version is left out: it is read again on the next scan.
                if (!ChannelsCurrent(file.Channels) || _files.ContainsKey(file.Path) || _dirty.ContainsKey(file.Path))
                {
                    continue;
                }

                Intern(file);
                var entry = new Entry(
                    file.Path,
                    file.Length,
                    file.LastWriteTicks,
                    file.Terms,
                    file.Counts,
                    file.TokenCount,
                    file.Symbols,
                    file.Transitions);
                _files[entry.Path] = entry;
                Apply(entry, +1);
            }
        }
    }

    /// <summary>Saves the model if anything changed since it was last saved. Failures leave it dirty for the next try.</summary>
    public void Save()
    {
        if (_directory is null)
        {
            return;
        }

        lock (_saveGate) SaveCore();
    }

    private void SaveCore()
    {
        (string Path, long Revision, StoredFile? File)[] snapshot;
        lock (_gate)
        {
            if (_dirty.Count == 0) return;
            snapshot = [.. _dirty.Select(change => (change.Key, change.Value,
                _files.TryGetValue(change.Key, out var e) ? new StoredFile
                {
                    Path = e.Path,
                    Length = e.Length,
                    LastWriteTicks = e.LastWriteTicks,
                    Channels = CurrentChannels,
                    Terms = e.Terms,
                    Counts = e.Counts,
                    TokenCount = e.TokenCount,
                    Symbols = e.Symbols,
                    Transitions = e.Transitions,
                } : null))];
        }

        try
        {
            UseStorage(db =>
            {
                db.WriteBatch(batch =>
                {
                    foreach (var (path, _, file) in snapshot)
                        if (file is null) batch.Delete<StoredFile>(StorageKey(path));
                        else batch.Put(StorageKey(path), file);
                });
                return true;
            });
        }
        catch (Exception ex) when (StorageFailure(ex))
        {
            return;
        }

        lock (_gate)
        {
            foreach (var (path, revision, _) in snapshot)
                if (_dirty.GetValueOrDefault(path) == revision) _dirty.Remove(path);
        }
    }

    private static bool StorageFailure(Exception exception) => exception is IOException or JsonException or UnauthorizedAccessException
        or InvalidOperationException or InvalidDataException;

    private string StoragePrefix => FormattableString.Invariant($"{_key.Length}:{_key}:");
    private string StorageKey(string path) => StoragePrefix + path.ToUpperInvariant();

    private T UseStorage<T>(Func<CodaleDatabase, T> action) => WorkspaceDb.Use(_directory!.Value, action);

    /// <summary>A loaded file's words become the pool's, so it holds no copies of its own.</summary>
    private void Intern(StoredFile file)
    {
        for (var t = 0; t < file.Terms.Length; t++)
        {
            file.Terms[t] = Pool.Intern(file.Terms[t]);
        }

        for (var s = 0; s < file.Symbols.Length; s++)
        {
            var symbol = file.Symbols[s];
            var name = Pool.Intern(symbol.Name);
            if (!ReferenceEquals(name, symbol.Name))
            {
                file.Symbols[s] = symbol with { Name = name };
            }
        }

        for (var p = 0; p < file.Transitions.Length; p++)
        {
            var pair = file.Transitions[p];
            file.Transitions[p] = new WordPair(Pool.Intern(pair.Previous), Pool.Intern(pair.Next), pair.Count);
        }
    }

    private static bool SameStatistics(Entry a, Entry b) =>
        a.TokenCount == b.TokenCount &&
        a.Terms.AsSpan().SequenceEqual(b.Terms) &&
        a.Counts.AsSpan().SequenceEqual(b.Counts) &&
        a.Symbols.AsSpan().SequenceEqual(b.Symbols) &&
        a.Transitions.AsSpan().SequenceEqual(b.Transitions);

    private static bool ChannelsCurrent(Dictionary<string, int>? channels) =>
        channels is not null &&
        ModelChannels.Current.All(c => channels.TryGetValue(c.Key, out var version) && version == c.Value);

    private static IEnumerable<string> Range(string[] sorted, string prefix, StringComparer comparer, StringComparison compare)
    {
        var index = Array.BinarySearch(sorted, prefix, comparer);
        if (index < 0)
        {
            index = ~index;
        }

        for (var i = index; i < sorted.Length && sorted[i].StartsWith(prefix, compare); i++)
        {
            yield return sorted[i];
        }
    }

    /// <summary>Adds (+1) or takes back (-1) one file's contribution to the aggregates.</summary>
    private void Apply(Entry entry, int sign)
    {
        for (var t = 0; t < entry.Terms.Length; t++)
        {
            Adjust(_termFiles, entry.Terms[t], sign);
        }

        foreach (var symbol in entry.Symbols)
        {
            if (!_symbols.TryGetValue(symbol.Name, out var kinds))
            {
                if (sign < 0)
                {
                    continue;
                }

                _symbols[symbol.Name] = kinds = [];
            }

            Adjust(kinds, symbol.Kind, sign);
            if (kinds.Count == 0)
            {
                _symbols.Remove(symbol.Name);
            }
        }

        foreach (var pair in entry.Transitions)
        {
            if (!_next.TryGetValue(pair.Previous, out var followers))
            {
                if (sign < 0)
                {
                    continue;
                }

                _next[pair.Previous] = followers = [with(StringComparer.Ordinal)];
            }

            Adjust(followers, pair.Next, sign * pair.Count);
            if (followers.Count == 0)
            {
                _next.Remove(pair.Previous);
            }
        }

        _termIndex = null;
        _symbolIndex = null;
    }

    private static void Adjust<TKey>(Dictionary<TKey, int> counts, TKey key, int delta) where TKey : notnull
    {
        counts.TryGetValue(key, out var value);
        value += delta;
        if (value <= 0)
        {
            counts.Remove(key);
        }
        else
        {
            counts[key] = value;
        }
    }

    /// <summary>A word being ranked for a prefix: its text, its score and, for a declared name, its kind.</summary>
    private sealed record Candidate(string Text, double Score, SourceSymbolKind? Kind, int Files);

    private sealed record Entry(
        string Path,
        long Length,
        long LastWriteTicks,
        string[] Terms,
        int[] Counts,
        int TokenCount,
        SourceSymbol[] Symbols,
        WordPair[] Transitions);

    // The saved form. Kept apart from Entry so the file format can change without touching the model.
    private sealed class StoredFile
    {
        [JsonPropertyName("path")]
        public string Path { get; set; } = "";

        [JsonPropertyName("length")]
        public long Length { get; set; }

        [JsonPropertyName("written")]
        public long LastWriteTicks { get; set; }

        [JsonPropertyName("channels")]
        public Dictionary<string, int>? Channels { get; set; }

        [JsonPropertyName("terms")]
        public string[] Terms { get; set; } = [];

        [JsonPropertyName("counts")]
        public int[] Counts { get; set; } = [];

        [JsonPropertyName("tokens")]
        public int TokenCount { get; set; }

        [JsonPropertyName("symbols")]
        public SourceSymbol[] Symbols { get; set; } = [];

        [JsonPropertyName("transitions")]
        public WordPair[] Transitions { get; set; } = [];
    }
}
