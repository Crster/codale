using System.Text;
using System.Text.Json.Nodes;

using TextMateSharp.Grammars;
using TextMateSharp.Internal.Grammars.Reader;
using TextMateSharp.Internal.Types;
using TextMateSharp.Registry;
using TextMateSharp.Themes;

namespace Codale.Core.Syntax;

/// <summary>
/// Colours one line at a time with a TextMate grammar, carrying the grammar's rule stack from
/// line to line so a block comment or string that spans lines stays coloured. The registry is
/// not thread-safe, so tokenizing is serialized; each line is time-limited so a pathological
/// regex costs a plain line rather than a frozen editor.
/// </summary>
public sealed class GrammarTokenizer(SyntaxStore store, LanguageCatalog catalog, RegistryOptions builtin, TimeSpan? lineBudget = null)
{
    private readonly TimeSpan _lineBudget = lineBudget ?? TimeSpan.FromMilliseconds(20);

    private readonly SyntaxStore _store = store;
    private readonly LanguageCatalog _catalog = catalog;
    private readonly RegistryOptions _builtin = builtin;
    private readonly Lock _gate = new();
    private readonly Dictionary<string, IGrammar?> _grammars = [with(StringComparer.OrdinalIgnoreCase)];
    private Registry? _registry;
    private int _version = -1;

    /// <summary>True when a grammar can be loaded for the language id.</summary>
    public bool Supports(string? languageId)
    {
        if (languageId is null)
        {
            return false;
        }

        lock (_gate)
        {
            return GetGrammar(languageId) is not null;
        }
    }

    /// <summary>
    /// Tokenizes <paramref name="line"/>. <paramref name="tokens"/> is cleared and filled with the
    /// scope-bearing spans, including plain tokens. Returns false when the language has no usable grammar, in which
    /// case the caller falls back to plain text or its own scanner.
    /// </summary>
    public bool TryTokenizeLine(string languageId, string line, object? stateIn, List<CodeToken> tokens, out object? stateOut)
    {
        tokens.Clear();
        stateOut = null;
        try
        {
            lock (_gate)
            {
                var grammar = GetGrammar(languageId);
                if (grammar is null)
                {
                    return false;
                }

                var result = stateIn is IStateStack stack
                    ? grammar.TokenizeLine(line, stack, _lineBudget)
                    : grammar.TokenizeLine(line);
                Collect(result.Tokens, line.Length, tokens);
                stateOut = result.RuleStack;
                return true;
            }
        }
        catch (Exception)
        {
            tokens.Clear();
            stateOut = null;
            return false;
        }
    }

    /// <summary>
    /// Loads <paramref name="json"/> in an isolated registry and tokenizes the sample with it.
    /// Returns null if that works, otherwise why not.
    /// </summary>
    internal static string? TrialTokenize(string json, string scopeName, string sample)
    {
        try
        {
            var builtin = Shared.Builtin;
            var registry = new Registry(new Options(builtin, null, new Dictionary<string, string>(StringComparer.Ordinal) { [scopeName] = json }));
            var grammar = registry.LoadGrammar(scopeName);
            if (grammar is null)
            {
                return "The grammar could not be loaded. Check its patterns and scope names.";
            }

            IStateStack? state = null;
            var start = DateTime.UtcNow;
            foreach (var line in sample.Split('\n'))
            {
                var result = state is null
                    ? grammar.TokenizeLine(line)
                    : grammar.TokenizeLine(line, state, TimeSpan.FromMilliseconds(250));
                state = result.RuleStack;
                if (DateTime.UtcNow - start > TimeSpan.FromSeconds(2))
                {
                    return "The grammar is too slow: one of its patterns backtracks badly.";
                }
            }

            return null;
        }
        catch (Exception ex)
        {
            return $"The grammar could not be used: {ex.Message}";
        }
    }

    private IGrammar? GetGrammar(string languageId)
    {
        if (_registry is null || _version != _store.Version)
        {
            _registry = new Registry(new Options(_builtin, _store, null));
            _grammars.Clear();
            _version = _store.Version;
        }

        if (_grammars.TryGetValue(languageId, out var cached))
        {
            return cached;
        }

        IGrammar? grammar = null;
        var info = _catalog.ById(languageId);
        if (info is not null && info.ScopeName.Length > 0)
        {
            try
            {
                grammar = _registry.LoadGrammar(info.ScopeName);
            }
            catch (Exception)
            {
            }
        }

        _grammars[languageId] = grammar;
        return grammar;
    }

    private static void Collect(IToken[] source, int lineLength, List<CodeToken> tokens)
    {
        foreach (var token in source)
        {
            var start = Math.Min(token.StartIndex, lineLength);
            var end = Math.Min(token.EndIndex, lineLength);
            if (end <= start)
            {
                continue;
            }

            var kind = ScopeMapper.Map(token.Scopes);
            var scopes = string.Join('\u001f', token.Scopes);

            // Merge only identical scopes: two tokens of one semantic kind may have different theme styles.
            if (tokens.Count > 0 && tokens[^1] is { } last && last.Kind == kind && last.ScopeStack == scopes && last.Start + last.Length == start)
            {
                tokens[^1] = last with { Length = last.Length + (end - start) };
            }
            else
            {
                tokens.Add(new CodeToken(start, end - start, kind) { ScopeStack = scopes });
            }
        }
    }

    /// <summary>The shipped grammars, shared because loading them is the expensive part.</summary>
    internal static class Shared
    {
        public static readonly RegistryOptions Builtin = new(ThemeName.DarkPlus);
    }

    /// <summary>Resolves a scope name from the installed grammars first, then the shipped ones.</summary>
    private sealed class Options(RegistryOptions builtin, SyntaxStore? store, Dictionary<string, string>? extra) : IRegistryOptions
    {
        private readonly RegistryOptions _builtin = builtin;
        private readonly SyntaxStore? _store = store;
        private readonly Dictionary<string, string>? _extra = extra;

        public IRawGrammar GetGrammar(string scopeName)
        {
            string? json = null;
            if (_extra is not null && _extra.TryGetValue(scopeName, out var supplied))
            {
                json = supplied;
            }
            else if (_store is not null)
            {
                var entry = _store.Languages.FirstOrDefault(l => string.Equals(l.ScopeName, scopeName, StringComparison.Ordinal));
                json = entry is null ? null : _store.ReadGrammar(entry);
            }

            json ??= BundledGrammars.Read(scopeName);
            if (json is null)
            {
                return _builtin.GetGrammar(scopeName);
            }

            using var reader = new StreamReader(new MemoryStream(Encoding.UTF8.GetBytes(NormalizeCaptureMaps(json))));
            return GrammarReader.ReadGrammarSync(reader);
        }

        // Tolerate upstream capture-map metadata and begin-only rules that TextMateSharp
        // cannot read directly. The original downloaded payload remains unchanged on disk.
        private static string NormalizeCaptureMaps(string json)
        {
            var root = JsonNode.Parse(json, documentOptions: new() { AllowTrailingCommas = true, CommentHandling = System.Text.Json.JsonCommentHandling.Skip });
            static void Visit(JsonNode? node)
            {
                if (node is JsonObject obj)
                {
                    foreach (var (key, value) in obj.ToArray())
                    {
                        if (key == "repository" && value is JsonObject repository)
                            foreach (var rule in repository.ToArray())
                                if (rule.Value is JsonArray patterns)
                                    repository[rule.Key] = new JsonObject { ["patterns"] = patterns.DeepClone() };
                        if (key is "captures" or "beginCaptures" or "endCaptures" or "whileCaptures" && value is JsonObject captures)
                            foreach (var capture in captures.ToArray())
                                if (!int.TryParse(capture.Key, out _) || capture.Value is not JsonObject)
                                {
                                    // Recover misplaced rule fields (notably XML's comment end/name).
                                    if (capture.Key is "end" or "name" && !obj.ContainsKey(capture.Key))
                                        obj[capture.Key] = capture.Value?.DeepClone();
                                    captures.Remove(capture.Key);
                                }
                        Visit(value);
                    }
                    // A begin-only TextMate rule stays open until EOF; TextMateSharp needs an explicit end.
                    if (obj.ContainsKey("begin") && !obj.ContainsKey("end") && !obj.ContainsKey("while"))
                        obj["end"] = "(?!)";
                }
                else if (node is JsonArray array) foreach (var value in array) Visit(value);
            }
            Visit(root);
            return root!.ToJsonString();
        }

        public ICollection<string> GetInjections(string scopeName) =>
            (_builtin.GetInjections(scopeName) ?? []).Concat(
                _store?.Languages.Where(l => l.InjectionScopes.Contains(scopeName)).Select(l => l.ScopeName) ?? [])
                .Distinct().ToArray();

        public IRawTheme GetTheme(string scopeName) => _builtin.GetTheme(scopeName);

        public IRawTheme GetDefaultTheme() => _builtin.GetDefaultTheme();
    }
}
