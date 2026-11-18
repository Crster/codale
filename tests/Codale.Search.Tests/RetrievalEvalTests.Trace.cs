using System.Text.Json;
using System.Text.Json.Serialization;
using System.Text.RegularExpressions;

using Codale.Core.Features;
using Codale.Core.Projects;
using Codale.Db;
using Codale.Db.Search;
using Codale.Search.Engine;

namespace Codale.Search.Tests;

public sealed partial class RetrievalEvalTests
{
    private sealed record TracedDefinition(string Word, int Index, string Symbol, string Kind);

    private sealed class TracedFile
    {
        public int? ProfilePos { get; set; }
        public float? ProfileScore { get; set; }
        public bool Exact { get; set; }
        public List<TracedDefinition> Defs { get; } = [];
        public int? RankPos { get; set; }
        public double? RankScore { get; set; }
        public int RankMatched { get; set; }
        public bool TestOrDoc { get; set; }
        public int Symbols { get; set; }
        public string[] PathTerms { get; set; } = [];
        public string[] SymbolTerms { get; set; } = [];
        public string[] SubjectTerms { get; set; } = [];
    }

    private sealed record TracedQuery(string Kind, string Query, string Expected, string Symbol, int Words, IReadOnlyList<string> Terms,
        IReadOnlyList<string> Resolved, IReadOnlyList<string> Actual, IReadOnlyDictionary<string, TracedFile> Files);

    /// <summary>
    /// Opt-in (set CODALE_COMPACT_TRACE to an output file): for every labelled query, natural and exact-symbol, writes the
    /// compact reader's ranking and the raw evidence behind it - profile-match position, exact path, declarations a prompt
    /// word names, full-text rank, and which prompt terms each file's path, declarations and comment subjects hold. That
    /// separates a file that never became a candidate from one that lost on rank, and lets weightings be replayed offline.
    /// </summary>
    [Fact]
    public async Task Trace_compact_ranking()
    {
        var output = Environment.GetEnvironmentVariable("CODALE_COMPACT_TRACE");
        if (string.IsNullOrWhiteSpace(output)) return;

        using var store = new CodaleDb(Path.Combine(_directory, "trace.db"));
        using var index = new ProjectKnowledgeIndex(store);
        await index.RefreshAsync(_corpus);
        var root = ProjectPaths.Normalize(_corpus);
        var snapshot = SourceIndex.For(root).Current ?? throw new InvalidOperationException("The refresh left no source snapshot.");
        var profiles = store.Open("compact-source-v1").Open(root);
        static string Slash(string path) => path.Replace('\\', '/');

        var queries = _labels.Select(l => (Kind: "natural", l.Query, l.Path, l.Symbol))
            .Concat(_labels.Select(l => (Kind: "symbol", Query: l.Symbol, l.Path, l.Symbol))).ToList();
        var traced = new List<TracedQuery>();
        foreach (var (kind, query, expected, symbol) in queries)
        {
            var actual = index.Search(_corpus, query, maxFiles: 40, maxChunksPerFile: 1)
                .Select(c => Slash(c.Path)).Distinct().ToList();
            var terms = SearchText.RetrievalTerms(query);
            var files = new Dictionary<string, TracedFile>(StringComparer.OrdinalIgnoreCase);
            TracedFile At(string path) => files.TryGetValue(Slash(path), out var f) ? f : files[Slash(path)] = new TracedFile();

            if (terms.Count > 0)
            {
                var position = 0;
                foreach (var hit in profiles.Match("profile", [.. SearchText.RetrievalQueryTerms(query).Select(t => new KeywordTerm(t.Word, t.Prefix))], 200))
                {
                    var entry = At(hit.Node.Name);
                    entry.ProfilePos = position++;
                    entry.ProfileScore = hit.Score;
                }
            }

            // Every declaration a prompt word names, ignoring case and shape: the replay applies each reader's own filter.
            var words = Regex.Matches(SearchText.WithoutAttachments(query), @"[\w./\\-]+").Select(m => m.Value.TrimEnd('.')).ToArray();
            for (var i = 0; i < words.Length; i++)
            {
                if (snapshot.Find(words[i]) is { } exact) At(exact.RelativePath).Exact = true;
                foreach (var definition in snapshot.Definitions(words[i]).Take(12))
                    At(definition.File.RelativePath).Defs.Add(new(words[i], i, definition.Symbol.Name, definition.Symbol.Kind.ToString()));
            }

            IReadOnlyList<string> resolvedText = [];
            if (terms.Count > 0)
            {
                var resolved = SourceExplorer.Resolve(snapshot, PromptFileLocator.PromptTerms(query, snapshot.Contains));
                resolvedText = [.. resolved.Select(r => $"{r.Term.Text}{(r.Term.Bonus ? "*" : "")}:{r.Weight:F2}:{string.Join('|', r.Keys.Select(k => k.Key))}")];
                var position = 0;
                foreach (var ranked in SourceExplorer.Rank(snapshot, resolved, [], setup: false))
                {
                    var entry = At(ranked.File.RelativePath);
                    entry.RankPos = position++;
                    entry.RankScore = ranked.Score;
                    entry.RankMatched = ranked.Matched.Count;
                }
            }

            At(expected);
            foreach (var (path, entry) in files)
            {
                var file = snapshot.Find(path);
                entry.TestOrDoc = CodeDiscovery.IsTestOrDoc(path.Replace('/', Path.DirectorySeparatorChar));
                if (file is null) continue;
                var symbols = file.Symbols.Select(s => s.Name).ToArray();
                entry.Symbols = symbols.Length;
                var source = ProjectKnowledgeIndex.ReadSource(root, file.RelativePath) ?? "";
                var subjects = Regex.Matches(source, """(?m)^[ \t]*(?:///|//|/\*+|\*|#+|"{3}|'{3})[ \t]*(?:<summary>[ \t]*)?([^<\r\n]+)""")
                    .Select(m => m.Groups[1].Value.Trim()).Where(s => s.Length > 0).Distinct().Take(12);
                string[] Covered(IEnumerable<string> text)
                {
                    var stems = text.SelectMany(t => SearchText.Tokens(t)).Select(SearchText.Stem).ToHashSet(StringComparer.Ordinal);
                    return [.. terms.Where(t => stems.Any(s => s.StartsWith(t, StringComparison.Ordinal)))];
                }
                entry.PathTerms = Covered([path]);
                entry.SymbolTerms = Covered(symbols);
                entry.SubjectTerms = Covered(subjects);
            }
            traced.Add(new(kind, query, expected, symbol, words.Length, terms, resolvedText, actual, files));

            var rank = actual.IndexOf(expected) + 1;
            var mine = files[expected];
            _output.WriteLine($"{kind,-7} {(rank == 0 ? "-" : rank.ToString()),3}  profile={mine.ProfilePos?.ToString() ?? "-",-4} rank={mine.RankPos?.ToString() ?? "-",-4} " +
                              $"defs={mine.Defs.Count}  top={actual.FirstOrDefault()}  {query}");
        }

        Directory.CreateDirectory(Path.GetDirectoryName(Path.GetFullPath(output))!);
        await File.WriteAllTextAsync(output, JsonSerializer.Serialize(traced, new JsonSerializerOptions
        {
            PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
            DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull,
        }));
    }
}
