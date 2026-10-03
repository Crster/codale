using Codale.Core.Syntax;

namespace Codale.Core.Tests.Syntax;

public sealed class SyntaxTests : IDisposable
{
    private readonly string _root = Path.Combine(Path.GetTempPath(), "codale-syntax-" + Guid.NewGuid().ToString("N"));

    public SyntaxTests() => SyntaxService.Initialize(_root);

    public void Dispose()
    {
        try { Directory.Delete(_root, recursive: true); } catch { /* temp */ }
    }

    private const string MyLang = """
        {
          "scopeName": "source.mylang",
          "name": "MyLang",
          "fileTypes": ["myl"],
          "patterns": [
            { "match": "\\b(spin|twirl)\\b", "name": "keyword.control.mylang" },
            { "begin": "<<", "end": ">>", "name": "comment.block.mylang" }
          ]
        }
        """;

    private static List<CodeToken> Tokenize(string language, string line, ref object? state)
    {
        var tokens = new List<CodeToken>();
        Assert.True(SyntaxService.Tokenizer.TryTokenizeLine(language, line, state, tokens, out state));
        return tokens;
    }

    [Fact]
    public void Shipped_languages_resolve_by_extension_and_by_fence_name()
    {
        var catalog = SyntaxService.Catalog;

        Assert.Equal("csharp", catalog.ForFile(@"C:\src\Program.cs")?.Id);
        Assert.Equal("python", catalog.ForFile("tool.py")?.Id);
        Assert.Equal("xml", catalog.ForFile("App.xaml")?.Id);
        Assert.Equal("xml", catalog.ForFile("Codale.csproj")?.Id);
        Assert.Equal("csharp", catalog.ByName("cs")?.Id);
        Assert.Null(catalog.ForFile("notes.unknownext"));
    }

    [Fact]
    public void Previously_coloured_languages_are_still_available()
    {
        var catalog = SyntaxService.Catalog;
        foreach (var file in new[] { "a.cs", "a.json", "a.xml", "a.cpp", "a.py", "a.js", "a.ts", "a.html", "a.css", "a.sql", "a.md", "a.toml", "a.ps1", "a.bat", "a.sh" })
        {
            Assert.True(catalog.ForFile(file) is not null, $"no language for {file}");
        }
    }

    [Fact]
    public void A_csharp_line_colours_keywords_strings_and_comments()
    {
        object? state = null;
        var line = "public string Name = \"x\"; // note";
        var tokens = Tokenize("csharp", line, ref state);

        Assert.Contains(tokens, t => t.Kind == CodeTokenKind.Keyword && line.Substring(t.Start, t.Length) == "public");
        Assert.Contains(tokens, t => t.Kind == CodeTokenKind.String);
        Assert.Contains(tokens, t => t.Kind == CodeTokenKind.Comment && line.Substring(t.Start, t.Length).Contains("note"));
    }

    [Fact]
    public void A_block_comment_stays_open_across_lines()
    {
        object? state = null;
        Tokenize("csharp", "/* start", ref state);
        var middle = Tokenize("csharp", "still inside", ref state);

        Assert.Single(middle);
        Assert.Equal(CodeTokenKind.Comment, middle[0].Kind);
    }

    [Fact]
    public void An_installed_grammar_colours_its_files_and_can_be_removed()
    {
        var store = SyntaxService.Store;
        var error = GrammarValidator.Validate(MyLang, out var meta);
        Assert.Null(error);

        store.Add(new LanguageEntry { Id = "mylang", Name = "MyLang", ScopeName = meta.ScopeName, Extensions = ["myl"], Source = GrammarSource.Generated }, MyLang);

        Assert.Equal("mylang", SyntaxService.Catalog.ForFile("x.MYL")?.Id);
        object? state = null;
        var tokens = Tokenize("mylang", "spin and twirl", ref state);
        Assert.Equal(2, tokens.Count(t => t.Kind == CodeTokenKind.Keyword));

        Assert.True(store.Remove("mylang"));
        Assert.Null(SyntaxService.Catalog.ForFile("x.myl"));
    }

    [Fact]
    public void Installed_languages_survive_a_restart()
    {
        SyntaxService.Store.Add(new LanguageEntry { Id = "mylang", Name = "MyLang", ScopeName = "source.mylang", Extensions = [".MYL"] }, MyLang);

        var reloaded = new SyntaxStore(_root);

        var entry = Assert.Single(reloaded.Languages);
        Assert.Equal("mylang", entry.Id);
        Assert.Equal(["myl"], entry.Extensions);
    }

    [Fact]
    public void A_corrupt_manifest_is_set_aside_and_start_up_continues()
    {
        Directory.CreateDirectory(_root);
        File.WriteAllText(Path.Combine(_root, "languages.json"), "{ not json");

        var store = new SyntaxStore(_root);

        Assert.Empty(store.Languages);
        Assert.True(File.Exists(Path.Combine(_root, "languages.json.broken")));
    }

    [Theory]
    [InlineData("", "empty")]
    [InlineData("[1,2]", "object")]
    [InlineData("{ \"patterns\": [] }", "scopeName")]
    [InlineData("{ \"scopeName\": \"source.x\" }", "patterns")]
    [InlineData("{ nope", "JSON")]
    public void Invalid_grammars_are_rejected_with_a_reason(string json, string mention)
    {
        var error = GrammarValidator.Validate(json, out _);

        Assert.NotNull(error);
        Assert.Contains(mention, error, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void A_grammar_with_a_broken_regex_is_rejected_not_stored()
    {
        var bad = """{ "scopeName": "source.bad", "patterns": [ { "match": "(unclosed", "name": "keyword.bad" } ] }""";

        Assert.NotNull(GrammarValidator.Validate(bad, out _, sample: "(unclosed here"));
    }

    [Fact]
    public void Scopes_map_to_the_nearest_meaningful_kind()
    {
        Assert.Equal(CodeTokenKind.String, ScopeMapper.Map(["source.cs", "string.quoted.double.cs", "punctuation.definition.string.begin.cs"]));
        Assert.Equal(CodeTokenKind.Number, ScopeMapper.Map(["source.cs", "constant.numeric.decimal.cs"]));
        Assert.Equal(CodeTokenKind.Tag, ScopeMapper.Map(["text.xml", "meta.tag.xml", "entity.name.tag.xml"]));
        Assert.Equal(CodeTokenKind.Plain, ScopeMapper.Map(["source.cs", "variable.other.readwrite.cs"]));
        Assert.Equal(CodeTokenKind.Plain, ScopeMapper.Map(["source.cs"]));
    }
}
