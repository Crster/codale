using System.Diagnostics;

using Codale.Core.Syntax;

namespace Codale.Editor;

/// <summary>
/// The colours of one line and the grammar state it ends in. Instances are never mutated once
/// published, so a renderer can hold the reference it drew with and know it is still current.
/// </summary>
public sealed class LineTokens
{
    public static readonly LineTokens Empty = new(string.Empty, [], null, null, provisional: false);

    internal LineTokens(string source, CodeToken[] tokens, object? stateIn, object? stateOut, bool provisional)
    {
        Source = source;
        Tokens = tokens;
        StateIn = stateIn;
        StateOut = stateOut;
        Provisional = provisional;
    }

    /// <summary>The line text these tokens were computed for (compared by reference).</summary>
    public string Source { get; }

    /// <summary>Coloured spans, in order, as UTF-16 offsets into <see cref="Source"/>.</summary>
    public CodeToken[] Tokens { get; }

    internal object? StateIn { get; }

    internal object? StateOut { get; }

    /// <summary>True when computed from a guessed starting state (the verified pass has not reached it yet).</summary>
    public bool Provisional { get; }

    public CodeTokenKind KindAt(int col)
    {
        foreach (var token in Tokens)
        {
            if (col < token.Start)
            {
                break;
            }

            if (col < token.Start + token.Length)
            {
                return token.Kind;
            }
        }

        return CodeTokenKind.Plain;
    }

    internal bool SameTokens(LineTokens other)
    {
        if (Tokens.Length != other.Tokens.Length)
        {
            return false;
        }

        for (var i = 0; i < Tokens.Length; i++)
        {
            if (Tokens[i] != other.Tokens[i])
            {
                return false;
            }
        }

        return true;
    }
}

/// <summary>
/// Incremental, time-sliced syntax colouring. Lines are verified top-down in short slices on the
/// UI thread's idle time (so a TextMate grammar, which is not thread-safe, never races an edit and
/// never stalls a frame). An edit only invalidates from its first line; the verified pass reuses
/// every following line whose text and incoming state are unchanged, so typing re-lexes one line
/// unless it opens or closes a multi-line construct. Lines the verified pass has not reached yet
/// are coloured provisionally from a nearby state, so jumping to the end of a large file shows
/// colour at once instead of plain text.
/// </summary>
public sealed class SyntaxHighlighter
{
    /// <summary>Lines longer than this are left plain: grammars are regex engines and minified lines would stall them.</summary>
    public const int MaxTokenizedLineLength = 10_000;

    /// <summary>How far above a line the provisional pass starts from a blank state.</summary>
    private const int ProvisionalLookBack = 40;

    /// <summary>
    /// Tokenizes one line: (language, line, state in, tokens out) → (handled, state out). The default
    /// is the app's TextMate service; tests swap in a fake.
    /// </summary>
    public delegate bool LineTokenizer(string languageId, string line, object? stateIn, List<CodeToken> tokens, out object? stateOut);

    private readonly TextBuffer _buffer;
    private readonly LineTokenizer _tokenize;
    private readonly List<LineTokens?> _lines = [];
    private readonly List<CodeToken> _scratch = [];
    private int _validUpTo;
    private string? _languageId;
    private bool _enabled = true;

    public SyntaxHighlighter(TextBuffer buffer, LineTokenizer? tokenizer = null)
    {
        _buffer = buffer;
        _tokenize = tokenizer ?? DefaultTokenizer;
        _buffer.Changed += OnChanged;
        _buffer.Reloaded += Reset;
        Reset();
    }

    private static bool DefaultTokenizer(string languageId, string line, object? stateIn, List<CodeToken> tokens, out object? stateOut) =>
        SyntaxService.Tokenizer.TryTokenizeLine(languageId, line, stateIn, tokens, out stateOut);

    /// <summary>Raised when tokens of already-shown lines were replaced; the arguments are the first and last line affected.</summary>
    public event Action<int, int>? TokensChanged;

    public string? LanguageId
    {
        get => _languageId;
        set
        {
            if (_languageId != value)
            {
                _languageId = value;
                Reset();
            }
        }
    }

    public bool Enabled
    {
        get => _enabled;
        set
        {
            if (_enabled != value)
            {
                _enabled = value;
                Reset();
            }
        }
    }

    /// <summary>Lines [0, ValidUpTo) carry verified tokens.</summary>
    public int ValidUpTo => _validUpTo;

    private bool Active => _enabled && _languageId is not null;

    /// <summary>Forgets every token (language or theme change, a new document).</summary>
    public void Reset()
    {
        _lines.Clear();
        _validUpTo = 0;
        for (var i = 0; i < _buffer.LineCount; i++)
        {
            _lines.Add(null);
        }
    }

    private void OnChanged(TextChange change)
    {
        var removed = change.OldLineCount;
        var added = change.NewLineCount;
        var start = change.StartLine;

        // Keep the edited lines' old tokens as a stale hint (drawn until re-verified): the
        // first line of the edit keeps its entry, inserted lines get none.
        var common = Math.Min(removed, added);
        if (removed > added)
        {
            _lines.RemoveRange(start + common, removed - added);
        }
        else if (added > removed)
        {
            _lines.InsertRange(start + common, Enumerable.Repeat<LineTokens?>(null, added - removed));
        }

        if (_validUpTo > start)
        {
            _validUpTo = start;
        }
    }

    /// <summary>
    /// The tokens to draw a line with: verified when available, otherwise provisional or stale.
    /// Never blocks for more than a few dozen lines of tokenizing.
    /// </summary>
    public LineTokens Get(int line)
    {
        if (!Active || (uint)line >= (uint)_lines.Count)
        {
            return LineTokens.Empty;
        }

        var entry = _lines[line];
        var source = _buffer[line];
        if (entry is not null && (line < _validUpTo || ReferenceEquals(entry.Source, source)))
        {
            return entry;
        }

        return Provisional(line);
    }

    /// <summary>
    /// Continues the verified pass toward <paramref name="targetLine"/> for at most
    /// <paramref name="budget"/>. Returns true while there is more to do before the target.
    /// </summary>
    public bool Advance(int targetLine, TimeSpan budget)
    {
        if (!Active)
        {
            return false;
        }

        targetLine = Math.Min(targetLine, _buffer.LineCount - 1);
        if (_validUpTo > targetLine)
        {
            return false;
        }

        var clock = Stopwatch.StartNew();
        var changedFrom = int.MaxValue;
        var changedTo = -1;
        var state = _validUpTo > 0 ? _lines[_validUpTo - 1]?.StateOut : null;
        var line = _validUpTo;
        for (; line <= targetLine; line++)
        {
            var source = _buffer[line];
            var entry = _lines[line];
            if (entry is { Provisional: false } && ReferenceEquals(entry.Source, source) && Equals(entry.StateIn, state))
            {
                state = entry.StateOut;
                continue;
            }

            var fresh = Tokenize(source, state, provisional: false);
            if (entry is not null && ReferenceEquals(entry.Source, source) && entry.SameTokens(fresh))
            {
                // Same colours as what is on screen: keep the token array the renderer drew with
                // (it caches by that reference) and only take the verified state.
                fresh = new LineTokens(source, entry.Tokens, fresh.StateIn, fresh.StateOut, provisional: false);
            }
            else
            {
                changedFrom = Math.Min(changedFrom, line);
                changedTo = line;
            }

            _lines[line] = fresh;
            state = fresh.StateOut;

            if ((line & 7) == 7 && clock.Elapsed >= budget)
            {
                line++;
                break;
            }
        }

        _validUpTo = line;
        if (changedTo >= 0)
        {
            TokensChanged?.Invoke(changedFrom, changedTo);
        }

        return _validUpTo <= targetLine;
    }

    private LineTokens Provisional(int line)
    {
        // Start from the nearest line above with a trusted end state, or from a blank state a
        // little way up (which recovers from most open constructs within a few lines).
        var from = Math.Max(_validUpTo, line - ProvisionalLookBack);
        object? state = from > 0 && from - 1 < _validUpTo ? _lines[from - 1]?.StateOut : null;
        LineTokens? result = null;
        for (var i = from; i <= line; i++)
        {
            var source = _buffer[i];
            var existing = _lines[i];
            if (existing is not null && ReferenceEquals(existing.Source, source) && Equals(existing.StateIn, state))
            {
                result = existing;
                state = existing.StateOut;
                continue;
            }

            result = Tokenize(source, state, provisional: true);
            _lines[i] = result;
            state = result.StateOut;
        }

        return result ?? LineTokens.Empty;
    }

    private LineTokens Tokenize(string source, object? stateIn, bool provisional)
    {
        if (source.Length > MaxTokenizedLineLength)
        {
            return new LineTokens(source, [], stateIn, stateIn, provisional);
        }

        _scratch.Clear();
        object? stateOut = null;
        bool handled;
        try
        {
            handled = _tokenize(_languageId!, source, stateIn, _scratch, out stateOut);
        }
        catch (Exception)
        {
            handled = false;
        }

        if (!handled)
        {
            return new LineTokens(source, [], null, null, provisional);
        }

        return new LineTokens(source, _scratch.ToArray(), stateIn, stateOut, provisional);
    }
}
