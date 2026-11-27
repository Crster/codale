using System.Text.RegularExpressions;

using Codale.Core.Syntax;
using Codale.Db.Search;

namespace Codale.Search.Engine;

/// <summary>The selected text or caret token, with its location in the live editor buffer.</summary>
public sealed record ReferenceQuery(string Text, string? FilePath = null, string? Buffer = null, int Offset = 0)
{
    public static ReferenceQuery? At(string? path, string buffer, int start, int end)
    {
        if (start == end)
        {
            bool IsWord(int index) => index >= 0 && index < buffer.Length && SourceTokens.IsPart(buffer[index]);
            if (!IsWord(start) && IsWord(start - 1)) start--;
            if (!IsWord(start)) return null;
            end = start + 1;
            while (IsWord(start - 1)) start--;
            while (IsWord(end)) end++;
        }

        while (start < end && char.IsWhiteSpace(buffer[start])) start++;
        while (end > start && char.IsWhiteSpace(buffer[end - 1])) end--;
        return start == end ? null : new(buffer[start..end], path, buffer, start);
    }
}

/// <summary>Traces a reference from its declaration through the receiver to other occurrences.</summary>
public sealed partial class ReferenceSearch(SourceIndex index, ProjectKnowledgeIndex? knowledge = null)
{
    private const string Identifier = @"[\p{L}_$][\p{L}\p{Nd}_$]*";
    private const string MemberOperator = @"(?:\?\.|\.|->|::)";

    [GeneratedRegex("^" + Identifier + "$", RegexOptions.CultureInvariant)]
    private static partial Regex IdentifierPattern();

    [GeneratedRegex(@"(?:(?<![\p{L}\p{Nd}_$.>:)\]])(?<receiver>" + Identifier + @")[ \t]*)?" +
        MemberOperator + @"[ \t]*\z", RegexOptions.CultureInvariant)]
    private static partial Regex ReceiverPattern();

    [GeneratedRegex("^" + Identifier + @"[ \t]*" + MemberOperator + @"[ \t]*(?<member>" + Identifier + @")\z", RegexOptions.CultureInvariant)]
    private static partial Regex QualifiedPattern();

    // Common declaration forms: Type name, name: Type, and name = [new] Type(...).
    [GeneratedRegex(@"(?<![\p{L}\p{Nd}_$.])(?:(?!(?:var|let|const|def|val)\b)(?<type>" + Identifier + @")[ \t]+(?<name>" + Identifier + @")(?=[ \t]*[=;,){]|[ \t]*$)|(?<name>" +
        Identifier + @")[ \t]*:[ \t]*(?<type>" + Identifier + @")|(?<name>" + Identifier + @")[ \t]*=(?!=)[ \t\r\n]*(?:new[ \t]+)?(?<type>" +
        Identifier + @")[ \t]*\()", RegexOptions.CultureInvariant | RegexOptions.Multiline)]
    private static partial Regex BindingPattern();

    private enum Role { Definition, Declaration, Usage, TextMatch }
    private sealed record Hit(SourceDocument Document, int First, int Last, Role Role);
    private sealed record Binding(SymbolLocation? Type, SourceDocument? Document = null, int Line = 0);

    private sealed class SourceDocument(SourceFile? file, string path, string text, CancellationToken ct, SourceSymbol[]? symbols = null)
    {
        private (int Start, int End)[]? _nonCode;
        private ILookup<string, Match>? _bindings;
        private bool? _supportsSymbols;
        public SourceFile? File { get; } = file;
        public string Path { get; } = path;
        public string Text { get; } = text;
        public List<int> Starts { get; } = SourceText.LineStarts(text);
        public IReadOnlyList<SourceSymbol> Symbols { get; } = symbols ?? file?.SymbolArray ?? [];
        public int LineAt(int offset) => SourceText.LineOf(Starts, offset);

        public bool IsCode(int offset)
        {
            _supportsSymbols ??= SyntaxService.Catalog.ForFile(Path, Starts.Count > 1 ? Text[..(Starts[1] - 1)] : Text)?.Id switch
            {
                "markdown" or "plaintext" or "json" or "yaml" or "xml" or "html" or "toml" or "ini" => false,
                null => Symbols.Count > 0,
                _ => true,
            };
            if (_supportsSymbols == false) return false;
            _nonCode ??= [.. GrammarSymbols.NonCodeSpans(Path, Text, ct)];
            var index = Array.BinarySearch(_nonCode, (offset, int.MaxValue));
            index = index >= 0 ? index : ~index - 1;
            return index < 0 || _nonCode[index].End <= offset;
        }

        public IEnumerable<Match> Bindings(string name) =>
            (_bindings ??= BindingPattern().Matches(Text).Cast<Match>().ToLookup(m => m.Groups["name"].Value, StringComparer.Ordinal))[name];

        public SourceSymbol? Enclosing(int line, int column, SourceSymbolKind kind) => Symbols
            .Where(s => s.Kind == kind && s.Line <= line && line <= s.EndLine && (s.Line < line || s.Column <= column))
            .MaxBy(s => (s.Line, s.Column));
    }

    public async Task<DiscoveryResult> RunAsync(ReferenceQuery query, CancellationToken ct = default)
    {
        var snapshot = await index.GetSnapshotAsync(ct).ConfigureAwait(false);
        return await Task.Run(() => Find(snapshot, query, ct), ct).ConfigureAwait(false);
    }

    private DiscoveryResult Find(SourceSnapshot snapshot, ReferenceQuery query, CancellationToken ct)
    {
        var selection = query.Text.Trim().ReplaceLineEndings("\n");
        if (selection.Length == 0) return new DiscoveryResult { IsFinal = true };
        var originPath = query.FilePath is { } sourcePath ? Path.GetRelativePath(index.Root, sourcePath) : null;
        var originFile = originPath is null ? null : snapshot.Find(originPath);
        SourceDocument? origin = null;
        var originOffset = 0;
        if (originPath is not null && query.Buffer is { } buffer && query.Offset >= 0 && query.Offset <= buffer.Length)
        {
            var text = buffer.ReplaceLineEndings("\n");
            var symbols = originFile?.ReadText()?.ReplaceLineEndings("\n") == text ? null : SourceSymbols.Extract(text, path: originPath);
            origin = new(originFile, originPath, text, ct, symbols);
            originOffset = buffer[..query.Offset].ReplaceLineEndings("\n").Length;
        }

        var looksLikeCode = (IdentifierPattern().IsMatch(selection) || QualifiedPattern().IsMatch(selection)) &&
            (origin is null || origin.IsCode(originOffset));
        var qualified = looksLikeCode ? QualifiedPattern().Match(selection) : Match.Empty;
        var memberOffset = qualified.Success ? qualified.Groups["member"].Index : 0;
        var name = selection[memberOffset..];
        var literal = new Regex(looksLikeCode ? $@"(?<![\p{{L}}\p{{Nd}}_$]){Regex.Escape(name)}(?![\p{{L}}\p{{Nd}}_$])" : Regex.Escape(name),
            RegexOptions.CultureInvariant);

        var hits = new Dictionary<(SourceDocument Document, int First), Hit>();
        var documents = new Dictionary<string, SourceDocument>(StringComparer.OrdinalIgnoreCase);
        if (origin is not null) documents.Add(origin.Path, origin);

        SourceDocument? Read(SourceFile file)
        {
            if (documents.TryGetValue(file.RelativePath, out var existing)) return existing;
            if (file.ReadText() is not { } text) return null;
            return new SourceDocument(file, file.RelativePath, text.ReplaceLineEndings("\n"), ct);
        }

        IEnumerable<SymbolLocation> Definitions(string symbol) => snapshot.Definitions(symbol)
            .Where(d => d.Symbol.Name == symbol && d.File != originFile)
            .Concat(originFile is not null && origin is not null
                ? origin.Symbols.Where(s => s.Name == symbol).Select(s => new SymbolLocation(originFile, s)) : []);

        void Add(SourceDocument document, int first, int last, Role role)
        {
            var key = (document, first);
            if (!hits.TryGetValue(key, out var previous) || previous.Role > role || previous.Role == role && previous.Last < last)
                hits[key] = new(document, first, last, role);
        }

        Binding? anchor = null;
        var originLine = 0;
        var memberAccess = qualified.Success;
        if (origin is not null && looksLikeCode)
        {
            var at = originOffset + memberOffset;
            if (at <= origin.Text.Length)
            {
                originLine = origin.LineAt(at);
                var receiver = ReceiverAt(origin, at);
                memberAccess |= receiver is not null;
                if (receiver is { Length: > 0 })
                    anchor = Resolve(origin, at, receiver, Definitions);
            }
        }

        var definitions = new List<(SourceDocument Document, SourceSymbol Symbol)>();
        if (looksLikeCode)
        {
            foreach (var definition in Definitions(name))
            {
                ct.ThrowIfCancellationRequested();
                if (Read(definition.File) is not { } document) continue;
                var symbol = definition.Symbol;
                var owner = document.Enclosing(symbol.Line, symbol.Column, SourceSymbolKind.Type);
                if (memberAccess && (anchor?.Type is not { } type || owner != type.Symbol || document.File != type.File ||
                    symbol.Kind is SourceSymbolKind.Type or SourceSymbolKind.Variable)) continue;
                if (!memberAccess && symbol.Kind == SourceSymbolKind.Variable && origin is not null &&
                    (document != origin || document.Enclosing(symbol.Line, symbol.Column, SourceSymbolKind.Function) !=
                        origin.Enclosing(originLine, int.MaxValue, SourceSymbolKind.Function))) continue;
                documents[document.Path] = document;
                definitions.Add((document, symbol));
            }

            // A declaration under the caret is stronger evidence than a name elsewhere.
            var local = definitions.Where(d => d.Document == origin && (d.Symbol.Line == originLine ||
                d.Symbol.Kind == SourceSymbolKind.Variable && d.Symbol.Line <= originLine)).ToArray();
            if (local.Length > 0) definitions = [.. local];
            foreach (var (document, symbol) in definitions) Add(document, symbol.Line, symbol.Line, Role.Definition);
        }

        if (anchor is { Document: { } parent, Line: > 0 })
            Add(parent, anchor.Line, anchor.Line, Role.Declaration);
        if (anchor?.Type is { } ownerType && Read(ownerType.File) is { } declaringType)
        {
            documents[declaringType.Path] = declaringType;
            Add(declaringType, ownerType.Symbol.Line, ownerType.Symbol.Line, Role.Declaration);
        }

        var priorities = new Dictionary<string, float>(StringComparer.OrdinalIgnoreCase);
        if (knowledge is not null)
        {
            knowledge.Warm(index.Root);
            var terms = SourceTokens.Of(name[..Math.Min(name.Length, 256)]).Select(term => new KeywordTerm(term));
            foreach (var (path, score) in knowledge.MatchProfiles(index.Root, terms, 128)) priorities[path] = score;
            var roots = definitions.Select(d => d.Document.Path).Concat(origin is null ? [] : [origin.Path]);
            foreach (var root in roots.Distinct(StringComparer.OrdinalIgnoreCase))
            {
                ct.ThrowIfCancellationRequested();
                foreach (var (path, _) in knowledge.RelatedFiles(index.Root, root))
                    priorities[path] = priorities.GetValueOrDefault(path) + 1;
            }
        }

        var posting = looksLikeCode ? snapshot.PostingOf(name.ToLowerInvariant()) : null;
        IEnumerable<SourceFile> candidates = posting is { } indexed ? indexed.Files.Select(id => snapshot.Files[id]) : snapshot.Files;
        foreach (var file in candidates)
        {
            ct.ThrowIfCancellationRequested();
            if (Read(file) is { } document && document != origin) FindMatches(document);
        }
        if (origin is not null) FindMatches(origin);

        void FindMatches(SourceDocument document)
        {
            foreach (var match in literal.EnumerateMatches(document.Text))
            {
                ct.ThrowIfCancellationRequested();
                var role = Role.TextMatch;
                var line = document.LineAt(match.Index);
                var column = match.Index - document.Starts[line - 1];
                if (anchor?.Type is { } target && IsMemberUse(document, match.Index, target) && document.IsCode(match.Index))
                    role = Role.Usage;
                else if (!memberAccess &&
                    definitions.Any(d => d.Symbol.Kind != SourceSymbolKind.Variable || d.Document == document &&
                        d.Symbol.Line <= line && document.Enclosing(d.Symbol.Line, d.Symbol.Column, SourceSymbolKind.Function) ==
                            document.Enclosing(line, column, SourceSymbolKind.Function)) && document.IsCode(match.Index))
                    role = Role.Usage;
                if (qualified.Success && role == Role.TextMatch &&
                    (!document.Text.AsSpan(0, match.Index).EndsWith(selection.AsSpan(0, memberOffset), StringComparison.Ordinal) ||
                     match.Index > memberOffset && SourceTokens.IsPart(document.Text[match.Index - memberOffset - 1])))
                    continue;
                Add(document, line, document.LineAt(match.Index + match.Length - 1), role);
            }
        }

        bool IsMemberUse(SourceDocument document, int offset, SymbolLocation target)
        {
            if (ReceiverAt(document, offset) is { } receiver)
                return Resolve(document, offset, receiver, Definitions)?.Type == target;
            if (document.File != target.File) return false;

            var line = document.LineAt(offset);
            var column = offset - document.Starts[line - 1];
            if (document.Enclosing(line, column, SourceSymbolKind.Type) != target.Symbol) return false;
            var function = document.Enclosing(line, column, SourceSymbolKind.Function);
            return !document.Symbols.Any(s => s.Name == name && s.Kind == SourceSymbolKind.Variable &&
                (s.Line < line || s.Line == line && s.Column < column) &&
                document.Enclosing(s.Line, s.Column, SourceSymbolKind.Function) == function);
        }

        return new DiscoveryResult
        {
            IsFinal = true,
            Files = [.. hits.Values.GroupBy(h => (h.Document, h.Role))
                .OrderBy(g => g.Key.Role)
                .ThenByDescending(g => g.Key.Role == Role.Declaration && g.Key.Document == anchor?.Document)
                .ThenByDescending(g => priorities.GetValueOrDefault(g.Key.Document.Path))
                .ThenBy(g => g.Key.Document.Path, StringComparer.OrdinalIgnoreCase)
                .Select(g => new DiscoveredFile
                {
                    FilePath = g.Key.Document.File?.FullPath ?? Path.Combine(index.Root, g.Key.Document.Path),
                    RelativePath = g.Key.Document.Path,
                    Reason = g.Key.Role switch
                    {
                        Role.Definition => "Definition",
                        Role.Declaration => "Parent flow",
                        Role.Usage => "Usage",
                        _ => "Text match",
                    },
                    Keywords = [name],
                    MatchCount = g.Count(),
                    Ranges = [.. g.OrderByDescending(h => h.Role == Role.Declaration &&
                        h.Document == anchor?.Document && h.First == anchor.Line).ThenBy(h => h.First).Select(h => new DiscoveredRange
                    {
                        StartLine = h.First,
                        EndLine = h.Last,
                        Code = h.Document.Text[h.Document.Starts[h.First - 1]..
                            (h.Last < h.Document.Starts.Count ? h.Document.Starts[h.Last] - 1 : h.Document.Text.Length)],
                        MatchLines = [.. Enumerable.Range(h.First, h.Last - h.First + 1)],
                    })],
                })],
        };
    }

    private static string? ReceiverAt(SourceDocument document, int offset)
    {
        var start = document.Starts[document.LineAt(offset) - 1];
        var match = ReceiverPattern().Match(document.Text[start..offset]);
        if (!match.Success) return null;
        var beforeReceiver = document.Text.AsSpan(start, match.Index).TrimEnd();
        return beforeReceiver.EndsWith(".") || beforeReceiver.EndsWith("->") || beforeReceiver.EndsWith("::")
            ? "" : match.Groups["receiver"].Value;
    }

    private static Binding? Resolve(SourceDocument document, int offset, string receiver,
        Func<string, IEnumerable<SymbolLocation>> definitions)
    {
        if (receiver.Length == 0) return null;
        var line = document.LineAt(offset);
        var owner = document.Enclosing(line, offset - document.Starts[line - 1], SourceSymbolKind.Type);
        if (receiver is "this" or "self")
            return owner is not null && document.File is { } file ? new(new(file, owner)) : null;

        var function = document.Enclosing(line, offset - document.Starts[line - 1], SourceSymbolKind.Function);
        Binding? field = null;
        foreach (var match in document.Bindings(receiver).Reverse())
        {
            var position = match.Groups["name"].Index;
            var bindingLine = document.LineAt(position);
            var column = position - document.Starts[bindingLine - 1];
            if (document.Enclosing(bindingLine, column, SourceSymbolKind.Type) != owner) continue;
            var bindingFunction = document.Enclosing(bindingLine, column, SourceSymbolKind.Function);
            if (position < offset && bindingFunction == function && document.IsCode(position))
                return new(FindType(match.Groups["type"].Value), document, bindingLine);
            if (field is null && function is not null && bindingFunction is null && owner is not null && document.IsCode(position))
                field = new(FindType(match.Groups["type"].Value), document, bindingLine);
        }
        return field ?? (FindType(receiver) is { } direct ? new(direct) : null);

        SymbolLocation? FindType(string name)
        {
            var types = definitions(name).Where(d => d.Symbol.Kind == SourceSymbolKind.Type).Take(2).ToArray();
            return types.Length == 1 ? types[0] : null;
        }
    }
}
