using System.Text.RegularExpressions;

using Microsoft.Extensions.FileSystemGlobbing;

namespace Codale.Search;

/// <summary>Where a symbol is declared.</summary>
public sealed record SymbolLocation(SourceFile File, SourceSymbol Symbol);

/// <summary>
/// The project as of one scan: every listed path, the text files with their tokens and
/// declarations, and the inverted index over them. Immutable and safe to share between
/// threads; a refresh builds the next one alongside.
/// </summary>
public sealed class SourceSnapshot
{
    private readonly Dictionary<string, int> _byPath;
    private readonly Dictionary<string, Posting> _postings;
    private readonly Dictionary<string, List<SymbolLocation>> _definitions;
    private readonly string[] _vocabulary;

    internal sealed class Posting(string term)
    {
        public string Term { get; } = term;
        public List<int> Files { get; } = [];
        public List<int> Counts { get; } = [];
    }

    internal SourceSnapshot(
        IReadOnlyList<string> allFiles,
        IReadOnlyList<SourceFile> files,
        long version,
        int filesRead)
    {
        AllFiles = allFiles;
        Files = files;
        Version = version;
        FilesRead = filesRead;
        BuiltAt = DateTime.UtcNow;

        _byPath = new Dictionary<string, int>(files.Count, StringComparer.OrdinalIgnoreCase);
        _postings = new Dictionary<string, Posting>(StringComparer.Ordinal);
        _definitions = new Dictionary<string, List<SymbolLocation>>(StringComparer.OrdinalIgnoreCase);

        long tokens = 0;
        for (var id = 0; id < files.Count; id++)
        {
            var file = files[id];
            _byPath[file.RelativePath] = id;
            tokens += file.TokenCount;

            var keys = file.TermKeys;
            for (var t = 0; t < keys.Length; t++)
            {
                if (!_postings.TryGetValue(keys[t], out var posting))
                {
                    _postings[keys[t]] = posting = new Posting(keys[t]);
                }
                else
                {
                    // One string per term across every file: the per-file copies go.
                    keys[t] = posting.Term;
                }

                posting.Files.Add(id);
                posting.Counts.Add(file.TermCounts[t]);
            }

            foreach (var symbol in file.Symbols)
            {
                if (!_definitions.TryGetValue(symbol.Name, out var list))
                {
                    _definitions[symbol.Name] = list = [];
                }

                list.Add(new SymbolLocation(file, symbol));
            }
        }

        AverageTokens = files.Count == 0 ? 1 : Math.Max(1, (double)tokens / files.Count);
        _vocabulary = [.. _postings.Keys];
        Array.Sort(_vocabulary, StringComparer.Ordinal);
    }

    /// <summary>Every file the listing found, root-relative: text, binary and oversized alike.</summary>
    public IReadOnlyList<string> AllFiles { get; }

    /// <summary>The indexed text files, in path order.</summary>
    public IReadOnlyList<SourceFile> Files { get; }

    /// <summary>The index's change counter when this scan started; a later change makes it stale.</summary>
    public long Version { get; }

    public DateTime BuiltAt { get; }

    /// <summary>How many files this scan had to read - all of them the first time, only the changed ones after.</summary>
    public int FilesRead { get; }

    internal double AverageTokens { get; }

    /// <summary>Every indexed token, sorted.</summary>
    internal IReadOnlyList<string> Vocabulary => _vocabulary;

    public SourceFile? Find(string relativePath)
    {
        var normal = Normalise(relativePath);
        return _byPath.TryGetValue(normal, out var id) ? Files[id] : null;
    }

    internal int IdOf(SourceFile file) => _byPath.TryGetValue(file.RelativePath, out var id) ? id : -1;

    /// <summary>How many files contain the token (already lower case).</summary>
    public int DocumentFrequency(string token) => _postings.TryGetValue(token, out var p) ? p.Files.Count : 0;

    internal Posting? PostingOf(string token) => _postings.GetValueOrDefault(token);

    public bool Contains(string token) => _postings.ContainsKey(token);

    /// <summary>Indexed tokens starting with <paramref name="prefix"/> (lower case), most widespread first.</summary>
    public IReadOnlyList<string> TokensWithPrefix(string prefix, int max = 16)
    {
        var index = Array.BinarySearch(_vocabulary, prefix, StringComparer.Ordinal);
        if (index < 0)
        {
            index = ~index;
        }

        var found = new List<string>();
        for (var i = index; i < _vocabulary.Length && _vocabulary[i].StartsWith(prefix, StringComparison.Ordinal); i++)
        {
            found.Add(_vocabulary[i]);
        }

        return found
            .OrderByDescending(DocumentFrequency)
            .ThenBy(t => t.Length)
            .Take(max)
            .ToList();
    }

    /// <summary>Where a name is declared, case-insensitively.</summary>
    public IReadOnlyList<SymbolLocation> Definitions(string name) =>
        _definitions.TryGetValue(name, out var list) ? list : [];

    /// <summary>
    /// Types declared in a file named after something else - <c>HelperToolCalls</c> inside
    /// IHelperModel.cs - which a file list alone cannot show. Tests and docs are left out.
    /// </summary>
    public IReadOnlyDictionary<string, List<string>> TypesNamedApart()
    {
        var types = new Dictionary<string, List<string>>(StringComparer.OrdinalIgnoreCase);
        foreach (var file in Files)
        {
            if (file.IsTestOrDoc)
            {
                continue;
            }

            foreach (var symbol in file.Symbols)
            {
                if (symbol.Kind != SourceSymbolKind.Type || !char.IsUpper(symbol.Name[0]) ||
                    string.Equals(symbol.Name, file.NameKey, StringComparison.OrdinalIgnoreCase))
                {
                    continue;
                }

                if (!types.TryGetValue(file.RelativePath, out var names))
                {
                    types[file.RelativePath] = names = [];
                }

                if (!names.Contains(symbol.Name))
                {
                    names.Add(symbol.Name);
                }
            }
        }

        return types;
    }

    /// <summary>
    /// Line-by-line regex search over the indexed files, in parallel, in path order -
    /// ripgrep's semantics (one line at a time, so <c>^</c> and <c>$</c> are line anchors)
    /// without starting a process.
    /// </summary>
    /// <param name="include">Optional glob a path must match, e.g. <c>**/*.cs</c> or <c>*.ts</c>.</param>
    public IReadOnlyList<SearchHit> Grep(Regex pattern, string? include = null, int maxResults = 500, int maxPerFile = 50, CancellationToken ct = default)
    {
        var matcher = include is { Length: > 0 } ? GlobMatcher(include) : null;
        var candidates = Files.Where(f => matcher is null || Matches(matcher, f.RelativePath)).ToList();
        var results = new List<SearchHit>[candidates.Count];
        var found = 0;

        try
        {
            Parallel.For(0, candidates.Count, new ParallelOptions { CancellationToken = ct }, (i, state) =>
            {
                // Path order is kept by slot, so stopping early may only drop files after
                // ones already full - a later slot never displaces an earlier one.
                if (Volatile.Read(ref found) >= maxResults * 4)
                {
                    state.Break();
                    return;
                }

                var file = candidates[i];
                if (file.ReadText() is not { } text)
                {
                    return;
                }

                List<SearchHit>? hits = null;
                var number = 0;
                foreach (var line in text.AsSpan().EnumerateLines())
                {
                    number++;
                    var enumerator = pattern.EnumerateMatches(line);
                    if (!enumerator.MoveNext())
                    {
                        continue;
                    }

                    var match = enumerator.Current;
                    (hits ??= []).Add(new SearchHit
                    {
                        FilePath = file.FullPath,
                        RelativePath = file.RelativePath,
                        LineNumber = number,
                        Line = line.ToString(),
                        MatchStart = match.Index,
                        MatchLength = match.Length,
                    });

                    if (hits.Count >= maxPerFile)
                    {
                        break;
                    }
                }

                if (hits is not null)
                {
                    results[i] = hits;
                    Interlocked.Add(ref found, hits.Count);
                }
            });
        }
        catch (AggregateException ex) when (ex.InnerExceptions.All(e => e is RegexMatchTimeoutException))
        {
            throw ex.InnerExceptions[0];
        }

        var all = new List<SearchHit>(Math.Min(maxResults, found));
        foreach (var hits in results)
        {
            if (hits is null)
            {
                continue;
            }

            foreach (var hit in hits)
            {
                if (all.Count >= maxResults)
                {
                    return all;
                }

                all.Add(hit);
            }
        }

        return all;
    }

    /// <summary>Paths from the full listing that match a glob - binaries included, as <c>find_files</c> wants.</summary>
    public IReadOnlyList<string> FindFiles(string glob, int max = 200)
    {
        var matcher = GlobMatcher(glob);
        return AllFiles.Where(f => Matches(matcher, f)).Take(max).ToList();
    }

    /// <summary>A glob without a folder part ("*.cs") matches at any depth, as ripgrep's --glob does.</summary>
    private static Matcher GlobMatcher(string glob)
    {
        var matcher = new Matcher(StringComparison.OrdinalIgnoreCase);
        var normal = glob.Replace('\\', '/').TrimStart('.', '/');
        matcher.AddInclude(normal);
        if (!normal.Contains('/'))
        {
            matcher.AddInclude("**/" + normal);
        }

        return matcher;
    }

    private static bool Matches(Matcher matcher, string relativePath) =>
        matcher.Match(relativePath.Replace('\\', '/')).HasMatches;

    internal static string Normalise(string relativePath)
    {
        var path = relativePath.Replace('/', Path.DirectorySeparatorChar).Replace('\\', Path.DirectorySeparatorChar);
        return path.StartsWith("." + Path.DirectorySeparatorChar, StringComparison.Ordinal) ? path[2..] : path;
    }
}
