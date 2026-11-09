using System.Text.Json;
using System.Text.Json.Serialization;

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
        [Symbols] = 1,
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
/// The model is saved next to the project's knowledge and loaded on the next start. A file whose
/// length and write time still match its stored entry is not read again, so a restart needs no
/// re-tokenising of an unchanged workspace.
/// </para>
/// </remarks>
public sealed class WorkspaceModel
{
    private const int FormatVersion = 1;

    private readonly Lock _gate = new();
    private readonly string? _path;
    private readonly Dictionary<string, Entry> _files = new(StringComparer.OrdinalIgnoreCase);
    private readonly Dictionary<string, int> _termFiles = new(StringComparer.Ordinal);
    private readonly Dictionary<string, Dictionary<SourceSymbolKind, int>> _symbols = new(StringComparer.OrdinalIgnoreCase);
    private readonly Dictionary<string, Dictionary<string, int>> _next = new(StringComparer.Ordinal);

    // Sorted views for prefix lookups; rebuilt on the next query after a change.
    private string[]? _termIndex;
    private string[]? _symbolIndex;
    private bool _loaded;
    private bool _dirty;

    /// <param name="path">Where the model is saved and loaded; null keeps it in memory only.</param>
    public WorkspaceModel(string? path = null) => _path = path;

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
                return _dirty;
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
            if (_files.TryGetValue(file.RelativePath, out var known) &&
                known.Length == file.Length && known.LastWriteTicks == file.LastWriteUtc.Ticks)
            {
                return;
            }

            var entry = new Entry(
                file.RelativePath,
                file.Length,
                file.LastWriteUtc.Ticks,
                (string[])file.TermKeys.Clone(),
                (int[])file.TermCounts.Clone(),
                file.TokenCount,
                file.Symbols.ToArray(),
                file.Transitions.ToArray());

            if (known is not null)
            {
                Apply(known, -1);
            }

            _files[entry.Path] = entry;
            Apply(entry, +1);
            _dirty = true;
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
            _dirty = true;
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
            if (!_files.TryGetValue(relativePath, out var entry) ||
                entry.Length != length || entry.LastWriteTicks != lastWriteUtc.Ticks)
            {
                return null;
            }

            return new SourceFile(
                relativePath,
                fullPath,
                entry.Length,
                lastWriteUtc,
                null,
                (string[])entry.Terms.Clone(),
                (int[])entry.Counts.Clone(),
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
                if (scores.ContainsKey(term))
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

            return scores.Values
                .OrderByDescending(c => c.Score)
                .ThenBy(c => c.Text.Length)
                .Take(max)
                .Select(c => new WorkspaceSuggestion(c.Text, c.Kind, c.Files))
                .ToList();
        }
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

            _loaded = true;
            if (_path is null || !File.Exists(_path))
            {
                return;
            }

            StoredModel? stored;
            try
            {
                using var stream = File.OpenRead(_path);
                stored = JsonSerializer.Deserialize<StoredModel>(stream);
            }
            catch (Exception ex) when (ex is IOException or JsonException or UnauthorizedAccessException)
            {
                return;
            }

            if (stored?.Format != FormatVersion || stored.Files is null)
            {
                return;
            }

            foreach (var file in stored.Files)
            {
                // A file stored under another channel version is left out: it is read again on the next scan.
                if (!ChannelsCurrent(file.Channels) || _files.ContainsKey(file.Path))
                {
                    continue;
                }

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
        if (_path is null)
        {
            return;
        }

        StoredModel snapshot;
        lock (_gate)
        {
            if (!_dirty)
            {
                return;
            }

            snapshot = new StoredModel
            {
                Format = FormatVersion,
                Files = _files.Values.Select(e => new StoredFile
                {
                    Path = e.Path,
                    Length = e.Length,
                    LastWriteTicks = e.LastWriteTicks,
                    Channels = new Dictionary<string, int>(ModelChannels.Current),
                    Terms = e.Terms,
                    Counts = e.Counts,
                    TokenCount = e.TokenCount,
                    Symbols = e.Symbols,
                    Transitions = e.Transitions,
                }).ToList(),
            };
        }

        var directory = Path.GetDirectoryName(_path)!;
        Directory.CreateDirectory(directory);
        var temp = _path + ".tmp";
        try
        {
            using (var stream = File.Create(temp))
            {
                JsonSerializer.Serialize(stream, snapshot);
            }

            if (File.Exists(_path))
            {
                File.Replace(temp, _path, destinationBackupFileName: null);
            }
            else
            {
                File.Move(temp, _path);
            }
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            return;
        }

        lock (_gate)
        {
            _dirty = false;
        }
    }

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

                _next[pair.Previous] = followers = new Dictionary<string, int>(StringComparer.Ordinal);
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
    private sealed class StoredModel
    {
        [JsonPropertyName("format")]
        public int Format { get; set; }

        [JsonPropertyName("files")]
        public List<StoredFile>? Files { get; set; }
    }

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
