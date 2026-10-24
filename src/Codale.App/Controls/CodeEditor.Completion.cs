using System.Text.RegularExpressions;

using Codale.Core.Syntax;

using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Input;
using Microsoft.UI.Xaml.Media;

using Windows.System;

namespace Codale.App.Controls;

/// <summary>
/// Suggestions while typing: the language's keywords and built-in types, plus every identifier
/// already in the file, classified as class / interface / struct / enum / function / variable
/// from how the file declares them. The list is a popup that never takes focus, so the
/// editor keeps the keyboard; Up/Down choose, Enter or Tab accepts, Esc closes, Ctrl+Space opens it.
/// </summary>
public sealed partial class CodeEditor
{
    private enum CompletionKind
    {
        Keyword,
        Type,
        Class,
        Interface,
        Struct,
        Enum,
        Function,
        Variable,
        Tag,
        Attribute,
        Property,
    }

    private sealed record CompletionItem(string Text, CompletionKind Kind, int Score);

    private const int MaxCompletionItems = 50;

    /// <summary>Files past this size reuse a stale word index for a moment instead of rescanning on every key.</summary>
    private const int LargeDocumentChars = 200_000;

    [GeneratedRegex(@"[A-Za-z_][A-Za-z0-9_]*")]
    private static partial Regex WordRegex();

    [GeneratedRegex(@"\b(class|interface|struct|enum|record|trait|protocol|type|object|impl|namespace)\s+([A-Za-z_][A-Za-z0-9_]*)")]
    private static partial Regex DeclarationRegex();

    [GeneratedRegex(@"\b([A-Za-z_][A-Za-z0-9_]*)\s*\(")]
    private static partial Regex CallRegex();

    [GeneratedRegex(@"<([A-Za-z][\w:.-]*)")]
    private static partial Regex TagRegex();

    [GeneratedRegex(@"\s([A-Za-z_@:][\w:.-]*)\s*=")]
    private static partial Regex AttributeRegex();

    private readonly HashSet<string> _docTags = new(StringComparer.Ordinal);
    private readonly HashSet<string> _docAttrs = new(StringComparer.Ordinal);

    private bool IsMarkup => _languageId is "html" or "xml" or "xsl" or "vue" or "svelte" || _syntax is CodeSyntax.Html or CodeSyntax.XML;

    /// <summary>Tag when the caret names an element (just after '&lt;' or '&lt;/'), Attribute inside an open tag, else null.</summary>
    private static CompletionKind? MarkupContext(string line, int start)
    {
        var before = line.AsSpan(0, start);
        var open = before.LastIndexOf('<');
        if (open < 0 || before[open..].Contains('>'))
        {
            return null;
        }

        var inner = before[(open + 1)..].TrimStart('/');
        return inner.Contains(' ') || inner.Contains('\t') ? CompletionKind.Attribute : CompletionKind.Tag;
    }

    private static readonly string[] HtmlTags =
    [
        "a", "abbr", "address", "article", "aside", "audio", "b", "body", "br", "button", "canvas", "caption", "code", "col", "div", "em", "fieldset", "figure", "footer", "form",
        "h1", "h2", "h3", "h4", "h5", "h6", "head", "header", "hr", "html", "i", "iframe", "img", "input", "label", "legend", "li", "link", "main", "meta", "nav", "ol", "option",
        "p", "pre", "script", "section", "select", "small", "source", "span", "strong", "style", "sub", "summary", "svg", "table", "tbody", "td", "template", "textarea", "tfoot",
        "th", "thead", "title", "tr", "ul", "video",
    ];

    private static readonly string[] HtmlAttributes =
    [
        "alt", "aria-label", "autofocus", "checked", "class", "colspan", "content", "data-", "disabled", "href", "id", "lang", "name", "onclick", "placeholder", "readonly",
        "rel", "required", "role", "rowspan", "selected", "src", "srcset", "style", "tabindex", "target", "title", "type", "value", "width", "height",
    ];

    private readonly Dictionary<string, (CompletionKind Kind, int Count)> _docWords = new(StringComparer.Ordinal);
    private bool _docWordsDirty = true;
    private DateTime _docWordsBuiltUtc = DateTime.MinValue;

    private List<CompletionItem> _completionItems = [];
    private int _completionStartCol;
    private int _completionLine;

    private bool CompletionOpen => CompletionPopup.IsOpen;

    /// <summary>Every live editor, so each one can suggest words from the files open in the others.</summary>
    private static readonly List<WeakReference<CodeEditor>> OpenEditors = [];

    /// <summary>Words of the other open files, merged: a declared kind beats a plain variable.</summary>
    private Dictionary<string, CompletionKind> OtherFileWords()
    {
        var merged = new Dictionary<string, CompletionKind>(StringComparer.Ordinal);
        lock (OpenEditors)
        {
            OpenEditors.RemoveAll(r => !r.TryGetTarget(out _));
            foreach (var reference in OpenEditors)
            {
                if (!reference.TryGetTarget(out var other) || ReferenceEquals(other, this))
                {
                    continue;
                }

                other.EnsureDocWords();
                foreach (var (word, info) in other._docWords)
                {
                    if (!merged.TryGetValue(word, out var kind) || kind == CompletionKind.Variable)
                    {
                        merged[word] = info.Kind;
                    }
                }
            }
        }

        return merged;
    }

    private void InitCompletion()
    {
        lock (OpenEditors)
        {
            OpenEditors.Add(new WeakReference<CodeEditor>(this));
        }

        LostFocus += (_, _) =>
        {
            // Clicking an entry moves focus onto the list for a moment; only close when it really left.
            DispatcherQueue.TryEnqueue(() =>
            {
                if (FocusState == FocusState.Unfocused && CompletionList.FocusState == FocusState.Unfocused)
                {
                    CloseCompletion();
                }
            });
        };
        ScrollChanged += (_, _) => CloseCompletion();
        Canvas.PointerPressed += (_, _) => CloseCompletion();
    }

    // ------------------------------------------------------------------ candidates

    private static readonly Dictionary<string, string[]> ExtraKeywords = new(StringComparer.OrdinalIgnoreCase)
    {
        ["java"] = ["abstract", "assert", "boolean", "break", "byte", "case", "catch", "char", "class", "const", "continue", "default", "do", "double", "else", "enum", "extends", "final", "finally", "float", "for", "if", "implements", "import", "instanceof", "int", "interface", "long", "new", "null", "package", "private", "protected", "public", "return", "short", "static", "super", "switch", "synchronized", "this", "throw", "throws", "true", "false", "try", "var", "void", "volatile", "while"],
        ["go"] = ["break", "case", "chan", "const", "continue", "default", "defer", "else", "fallthrough", "for", "func", "go", "goto", "if", "import", "interface", "map", "package", "range", "return", "select", "struct", "switch", "type", "var", "bool", "byte", "error", "float32", "float64", "int", "int32", "int64", "rune", "string", "uint", "nil", "true", "false"],
        ["rust"] = ["as", "async", "await", "break", "const", "continue", "crate", "dyn", "else", "enum", "extern", "false", "fn", "for", "if", "impl", "in", "let", "loop", "match", "mod", "move", "mut", "pub", "ref", "return", "self", "Self", "static", "struct", "super", "trait", "true", "type", "unsafe", "use", "where", "while", "bool", "char", "i32", "i64", "u8", "u32", "u64", "usize", "f32", "f64", "str", "String", "Vec", "Option", "Result", "Some", "None", "Ok", "Err"],
        ["kotlin"] = ["abstract", "as", "break", "by", "class", "companion", "const", "continue", "data", "do", "else", "enum", "false", "final", "for", "fun", "if", "import", "in", "interface", "internal", "is", "lateinit", "null", "object", "open", "override", "package", "private", "protected", "public", "return", "sealed", "super", "suspend", "this", "throw", "true", "try", "typealias", "val", "var", "when", "while", "Int", "Long", "String", "Boolean", "Double", "Float", "Unit", "Any"],
        ["php"] = ["abstract", "array", "as", "break", "case", "catch", "class", "const", "continue", "declare", "default", "do", "echo", "else", "elseif", "extends", "false", "final", "finally", "fn", "for", "foreach", "function", "if", "implements", "include", "interface", "isset", "namespace", "new", "null", "private", "protected", "public", "require", "return", "static", "switch", "this", "throw", "trait", "true", "try", "use", "var", "while", "yield"],
        ["ruby"] = ["alias", "and", "begin", "break", "case", "class", "def", "defined?", "do", "else", "elsif", "end", "ensure", "false", "for", "if", "in", "module", "next", "nil", "not", "or", "redo", "rescue", "retry", "return", "self", "super", "then", "true", "undef", "unless", "until", "when", "while", "yield", "attr_accessor", "require"],
        ["swift"] = ["actor", "as", "associatedtype", "async", "await", "break", "case", "catch", "class", "continue", "default", "defer", "do", "else", "enum", "extension", "false", "fileprivate", "for", "func", "guard", "if", "import", "in", "init", "inout", "internal", "is", "let", "nil", "open", "operator", "private", "protocol", "public", "return", "self", "static", "struct", "subscript", "super", "switch", "throw", "throws", "true", "try", "typealias", "var", "where", "while", "Int", "String", "Bool", "Double", "Float", "Array", "Dictionary", "Optional"],
    };

    private List<(string Text, CompletionKind Kind)> LanguageEntries()
    {
        var list = new List<(string, CompletionKind)>();
        foreach (var word in CodeTokenizer.KeywordsOf(_syntax))
        {
            list.Add((word, CompletionKind.Keyword));
        }

        foreach (var word in CodeTokenizer.TypesOf(_syntax))
        {
            list.Add((word, CompletionKind.Type));
        }

        if (_languageId is not null && ExtraKeywords.TryGetValue(_languageId, out var extra))
        {
            foreach (var word in extra)
            {
                list.Add((word, char.IsUpper(word[0]) ? CompletionKind.Type : CompletionKind.Keyword));
            }
        }

        if (_languageId is not null && StandardNames.TryGetValue(_languageId, out var standard))
        {
            foreach (var word in standard)
            {
                list.Add((word, char.IsUpper(word[0]) ? CompletionKind.Type : CompletionKind.Function));
            }
        }

        return list;
    }

    private static CompletionKind KindOfDeclaration(string keyword) => keyword switch
    {
        "class" or "object" or "impl" => CompletionKind.Class,
        "interface" or "trait" or "protocol" => CompletionKind.Interface,
        "struct" or "record" => CompletionKind.Struct,
        "enum" => CompletionKind.Enum,
        _ => CompletionKind.Type,
    };

    private void EnsureDocWords()
    {
        if (!_docWordsDirty)
        {
            return;
        }

        var big = _doc.LineCount * 40 > LargeDocumentChars;
        if (big && (DateTime.UtcNow - _docWordsBuiltUtc).TotalMilliseconds < 1500 && _docWords.Count > 0)
        {
            return;
        }

        _docWordsDirty = false;
        _docWordsBuiltUtc = DateTime.UtcNow;
        _docWords.Clear();
        _docTags.Clear();
        _docAttrs.Clear();

        var text = _doc.GetText();
        if (IsMarkup)
        {
            foreach (Match m in TagRegex().Matches(text))
            {
                _docTags.Add(m.Groups[1].Value);
            }

            foreach (Match m in AttributeRegex().Matches(text))
            {
                _docAttrs.Add(m.Groups[1].Value);
            }
        }

        CollectLanguageNames(text);

        foreach (Match m in (DocWordRegex() ?? WordRegex()).Matches(text))
        {
            if (m.Length < 2 || m.Length > 64)
            {
                continue;
            }

            _docWords[m.Value] = _docWords.TryGetValue(m.Value, out var seen)
                ? (seen.Kind, seen.Count + 1)
                : (CompletionKind.Variable, 1);
        }

        foreach (Match m in CallRegex().Matches(text))
        {
            var name = m.Groups[1].Value;
            if (_docWords.TryGetValue(name, out var seen) && seen.Kind == CompletionKind.Variable)
            {
                _docWords[name] = (CompletionKind.Function, seen.Count);
            }
        }

        foreach (Match m in DeclarationRegex().Matches(text))
        {
            var name = m.Groups[2].Value;
            if (_docWords.TryGetValue(name, out var seen))
            {
                _docWords[name] = (KindOfDeclaration(m.Groups[1].Value), seen.Count);
            }
        }
    }

    private static int MatchScore(string candidate, string prefix)
    {
        if (candidate.StartsWith(prefix, StringComparison.Ordinal))
        {
            return 0;
        }

        if (candidate.StartsWith(prefix, StringComparison.OrdinalIgnoreCase))
        {
            return 1;
        }

        if (prefix.Length >= 2 && candidate.Contains(prefix, StringComparison.OrdinalIgnoreCase))
        {
            return 2;
        }

        // Camel-hump style: every prefix letter appears in order ("gcp" -> GetCaretPosition).
        if (prefix.Length >= 2)
        {
            var at = 0;
            foreach (var ch in prefix)
            {
                var hit = candidate.AsSpan(at).IndexOf(ch.ToString(), StringComparison.OrdinalIgnoreCase);
                if (hit < 0)
                {
                    return -1;
                }

                at += hit + 1;
            }

            return 3;
        }

        return -1;
    }

    // ------------------------------------------------------------------ opening and updating

    /// <summary>Re-evaluates the list for the word before the caret; closes it when nothing fits.</summary>
    private void UpdateCompletion(bool manual = false)
    {
        if (HasSelection || _caret.Line >= _doc.LineCount)
        {
            CloseCompletion();
            return;
        }

        var line = _doc.GetLine(_caret.Line);
        var start = _caret.Col;
        while (start > 0 && IsWordChar(line[start - 1]))
        {
            start--;
        }

        var markup = IsMarkup;

        // Some languages carry '-', ':' or '$' inside names (data-id, border-radius, $this); keep them in the prefix.
        while (start > 0 && (IsWordChar(line[start - 1]) || IsExtraNameChar(line[start - 1])))
        {
            start--;
        }

        var prefix = line[start.._caret.Col];
        if ((prefix.Length == 0 && !manual) || (prefix.Length > 0 && char.IsDigit(prefix[0])))
        {
            CloseCompletion();
            return;
        }

        // Nothing to suggest inside a comment or a string.
        EnsureTokens(_caret.Line);
        if (_caret.Line < _lineCache.Count && _caret.Col > 0)
        {
            var probe = TextDocument.ExpandedColFromRaw(line, _caret.Col - 1);
            foreach (var token in _lineCache[_caret.Line].Tokens)
            {
                if (token.Kind is CodeTokenKind.Comment or CodeTokenKind.String
                    && probe >= token.Start && probe < token.Start + token.Length)
                {
                    CloseCompletion();
                    return;
                }
            }
        }

        var afterDot = start > 0 && line[start - 1] == '.';
        EnsureDocWords();

        var found = new Dictionary<string, CompletionItem>(StringComparer.Ordinal);

        void Offer(string text, CompletionKind kind)
        {
            if (text == prefix && prefix.Length > 0 && !found.ContainsKey(text) && kind is CompletionKind.Variable or CompletionKind.Function)
            {
                // The word being typed is itself in the file; only a second use makes it a suggestion.
                if (!_docWords.TryGetValue(text, out var seen) || seen.Count <= 1)
                {
                    return;
                }
            }

            var score = prefix.Length == 0 ? 0 : MatchScore(text, prefix);
            if (score < 0 || found.ContainsKey(text))
            {
                return;
            }

            found[text] = new CompletionItem(text, kind, score);
        }

        if (markup && MarkupContext(line, start) is { } place)
        {
            var isTag = place == CompletionKind.Tag;
            var builtIn = _languageId is "xml" or "xsl" ? Array.Empty<string>() : isTag ? HtmlTags : HtmlAttributes;
            foreach (var word in isTag ? _docTags : _docAttrs)
            {
                Offer(word, place);
            }

            foreach (var word in builtIn)
            {
                Offer(word, place);
            }

            ShowItems(found, start);
            return;
        }

        if (NativeCandidates(line, start) is { } native)
        {
            foreach (var (word, kind) in native)
            {
                Offer(word, kind);
            }

            ShowItems(found, start);
            return;
        }

        // Declared names first, so a class beats the plain variable of the same spelling.
        foreach (var (word, info) in _docWords)
        {
            if (info.Kind != CompletionKind.Variable)
            {
                Offer(word, info.Kind);
            }
        }

        if (!afterDot)
        {
            foreach (var (word, kind) in LanguageEntries())
            {
                Offer(word, kind);
            }
        }

        foreach (var (word, info) in _docWords)
        {
            if (info.Kind == CompletionKind.Variable)
            {
                Offer(word, info.Kind);
            }
        }

        // Then the other open files, after everything local so the current file wins ties.
        var others = OtherFileWords();
        foreach (var (word, kind) in others)
        {
            if (kind != CompletionKind.Variable)
            {
                Offer(word, kind);
            }
        }

        foreach (var (word, kind) in others)
        {
            if (kind == CompletionKind.Variable)
            {
                Offer(word, kind);
            }
        }

        ShowItems(found, start);
    }

    private void ShowItems(Dictionary<string, CompletionItem> found, int start)
    {
        var prefix = _doc.GetLine(_caret.Line)[start.._caret.Col];
        var items = found.Values
            .OrderBy(i => i.Score)
            .ThenBy(i => i.Text.Length)
            .ThenBy(i => i.Text, StringComparer.OrdinalIgnoreCase)
            .Take(MaxCompletionItems)
            .ToList();

        // Already fully typed and nothing else to offer: stay out of the way.
        if (items.Count == 0 || (items.Count == 1 && items[0].Text == prefix))
        {
            CloseCompletion();
            return;
        }

        _completionItems = items;
        _completionLine = _caret.Line;
        _completionStartCol = start;
        ShowCompletion();
    }

    private void ShowCompletion()
    {
        CompletionList.Items.Clear();
        foreach (var item in _completionItems)
        {
            CompletionList.Items.Add(BuildRow(item));
        }

        CompletionList.SelectedIndex = 0;
        CompletionList.ScrollIntoView(CompletionList.Items[0]);

        var anchor = PositionBounds(_completionLine, _completionStartCol);
        var height = Math.Min(220, _completionItems.Count * 28) + 4;
        var below = anchor.Y + _lineHeight + 2;
        CompletionPopup.XamlRoot = XamlRoot;
        CompletionPopup.HorizontalOffset = Math.Max(0, anchor.X);
        CompletionPopup.VerticalOffset = below + height > Canvas.ActualHeight && anchor.Y - height - 2 > 0
            ? anchor.Y - height - 2
            : below;
        CompletionPopup.IsOpen = true;
    }

    private void CloseCompletion()
    {
        if (CompletionPopup.IsOpen)
        {
            CompletionPopup.IsOpen = false;
        }
    }

    private static (string Glyph, string Label, CodeTokenKind Colour) Describe(CompletionKind kind) => kind switch
    {
        CompletionKind.Keyword => ("K", "keyword", CodeTokenKind.Keyword),
        CompletionKind.Type => ("T", "type", CodeTokenKind.Type),
        CompletionKind.Class => ("C", "class", CodeTokenKind.Type),
        CompletionKind.Interface => ("I", "interface", CodeTokenKind.Type),
        CompletionKind.Struct => ("S", "struct", CodeTokenKind.Type),
        CompletionKind.Enum => ("E", "enum", CodeTokenKind.Type),
        CompletionKind.Function => ("ƒ", "function", CodeTokenKind.Property),
        CompletionKind.Tag => ("<>", "tag", CodeTokenKind.Tag),
        CompletionKind.Attribute => ("a", "attribute", CodeTokenKind.Attribute),
        CompletionKind.Property => ("p", "property", CodeTokenKind.Property),
        _ => ("v", "variable", CodeTokenKind.Variable),
    };

    private static UIElement BuildRow(CompletionItem item)
    {
        var (glyph, label, colour) = Describe(item.Kind);
        var tint = new SolidColorBrush(CodePalette.Get(colour));

        var row = new Grid { ColumnSpacing = 8, MinWidth = 224 };
        row.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(18) });
        row.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
        row.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });

        row.Children.Add(new TextBlock
        {
            Text = glyph,
            Foreground = tint,
            FontWeight = Microsoft.UI.Text.FontWeights.SemiBold,
            HorizontalAlignment = HorizontalAlignment.Center,
            VerticalAlignment = VerticalAlignment.Center,
        });

        var name = new TextBlock
        {
            Text = item.Text,
            FontFamily = new FontFamily("Consolas"),
            VerticalAlignment = VerticalAlignment.Center,
            TextTrimming = TextTrimming.CharacterEllipsis,
        };
        Grid.SetColumn(name, 1);
        row.Children.Add(name);

        var kind = new TextBlock
        {
            Text = label,
            FontSize = 11,
            Opacity = 0.55,
            VerticalAlignment = VerticalAlignment.Center,
        };
        Grid.SetColumn(kind, 2);
        row.Children.Add(kind);
        return row;
    }

    // ------------------------------------------------------------------ accepting and keys

    private void AcceptCompletion(int index)
    {
        if (index < 0 || index >= _completionItems.Count)
        {
            return;
        }

        var text = _completionItems[index].Text;
        CloseCompletion();

        BeginUndoStep(canCoalesce: false);
        var from = new DocumentPosition(_completionLine, Math.Min(_completionStartCol, _caret.Col));
        _caret = Edit(from, _caret, text);
        _anchor = _caret;
        AfterEdit();
    }

    private void OnCompletionItemClick(object sender, ItemClickEventArgs e)
    {
        AcceptCompletion(CompletionList.Items.IndexOf(e.ClickedItem));
        Focus(FocusState.Programmatic);
    }

    private void MoveCompletion(int delta)
    {
        var count = _completionItems.Count;
        var next = CompletionList.SelectedIndex + delta;
        next = Math.Abs(delta) > 1 ? Math.Clamp(next, 0, count - 1) : (next % count + count) % count;
        CompletionList.SelectedIndex = next;
        CompletionList.ScrollIntoView(CompletionList.Items[next]);
    }

    /// <summary>Gives the open list first refusal on a key. Returns true when the key was consumed.</summary>
    private bool HandleCompletionKey(KeyRoutedEventArgs e)
    {
        if (!CompletionOpen)
        {
            return false;
        }

        switch (e.Key)
        {
            case VirtualKey.Down: MoveCompletion(1); return true;
            case VirtualKey.Up: MoveCompletion(-1); return true;
            case VirtualKey.PageDown: MoveCompletion(8); return true;
            case VirtualKey.PageUp: MoveCompletion(-8); return true;
            case VirtualKey.Enter or VirtualKey.Tab: AcceptCompletion(CompletionList.SelectedIndex); return true;
            case VirtualKey.Escape: CloseCompletion(); return true;
            case VirtualKey.Back:
                // The edit runs after this returns; refresh once it has.
                DispatcherQueue.TryEnqueue(() => UpdateCompletion());
                return false;
            case VirtualKey.Shift: return false;
            default:
                CloseCompletion();
                return false;
        }
    }

    /// <summary>Called after a typed character: a word character opens or narrows the list, anything else closes it.</summary>
    private void OnCharacterTyped(char ch)
    {
        if (IsWordChar(ch))
        {
            UpdateCompletion();
        }
        else
        {
            CloseCompletion();
        }
    }
}