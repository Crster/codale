using System.Text;
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
public sealed class GrammarTokenizer
{
    private static readonly TimeSpan LineBudget = TimeSpan.FromMilliseconds(20);

    private readonly SyntaxStore _store;
    private readonly LanguageCatalog _catalog;
    private readonly RegistryOptions _builtin;
    private readonly object _gate = new();
    private readonly Dictionary<string, IGrammar?> _grammars = new(StringComparer.OrdinalIgnoreCase);
    private Registry? _registry;
    private int _version = -1;

    public GrammarTokenizer(SyntaxStore store, LanguageCatalog catalog, RegistryOptions builtin)
    {
        _store = store;
        _catalog = catalog;
        _builtin = builtin;
    }

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
    /// coloured (non-plain) spans. Returns false when the language has no usable grammar, in which
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
                    ? grammar.TokenizeLine(line, stack, LineBudget)
                    : grammar.TokenizeLine(line);
                Collect(result.Tokens, line.Length, tokens);
                stateOut = result.RuleStack;
                return true;
            }
        }
        catch (Exception ex)
        {
            CrashLogHook.Warn("syntax", $"grammar {languageId} failed on a line ({ex.GetType().Name}); line rendered plain");
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
            catch (Exception ex)
            {
                CrashLogHook.Warn("syntax", $"grammar {languageId} failed to load: {ex.Message}");
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
            if (kind == CodeTokenKind.Plain)
            {
                continue;
            }

            // Merge neighbours of one kind so a long comment is one span, not one per word.
            if (tokens.Count > 0 && tokens[^1] is { } last && last.Kind == kind && last.Start + last.Length == start)
            {
                tokens[^1] = last with { Length = last.Length + (end - start) };
            }
            else
            {
                tokens.Add(new CodeToken(start, end - start, kind));
            }
        }
    }

    /// <summary>The shipped grammars, shared because loading them is the expensive part.</summary>
    internal static class Shared
    {
        public static readonly RegistryOptions Builtin = new(ThemeName.DarkPlus);
    }

    /// <summary>Resolves a scope name from the installed grammars first, then the shipped ones.</summary>
    private sealed class Options : IRegistryOptions
    {
        private readonly RegistryOptions _builtin;
        private readonly SyntaxStore? _store;
        private readonly Dictionary<string, string>? _extra;

        public Options(RegistryOptions builtin, SyntaxStore? store, Dictionary<string, string>? extra)
        {
            _builtin = builtin;
            _store = store;
            _extra = extra;
        }

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

            using var reader = new StreamReader(new MemoryStream(Encoding.UTF8.GetBytes(json)));
            return GrammarReader.ReadGrammarSync(reader);
        }

        public ICollection<string> GetInjections(string scopeName) => _builtin.GetInjections(scopeName);

        public IRawTheme GetTheme(string scopeName) => _builtin.GetTheme(scopeName);

        public IRawTheme GetDefaultTheme() => _builtin.GetDefaultTheme();
    }
}
