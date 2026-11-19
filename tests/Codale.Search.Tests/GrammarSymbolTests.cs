using Codale.Core.Syntax;

namespace Codale.Search.Tests;

public sealed class GrammarSymbolTests
{
    private const string FallbackSource = """
        class Widget
        {
            int count = 0;
            void Run()
            {
                Other();
            }
        };
        """;

    [Theory]
    [InlineData("a.unknown")]
    [InlineData("no-extension")]
    [InlineData("a.cpp")]
    public void Cpp_grammar_extracts_symbols_for_unrecognized_files(string path)
    {
        var symbols = GrammarSymbols.Extract(path, FallbackSource, null).ToArray();
        Assert.Contains(symbols, s => s.Name == "Widget" && s.Kind == SourceSymbolKind.Type);
        Assert.Contains(symbols, s => s.Name == "Run" && s.Kind == SourceSymbolKind.Function && s.Line == 4 && s.EndLine == 7);
        Assert.Contains(symbols, s => s.Name == "count" && s.Kind == SourceSymbolKind.Variable);
        Assert.DoesNotContain(symbols, s => s.Name == "Other");
    }

    [Theory]
    [InlineData("not-json")]
    [InlineData("{\"scopeName\":\"source.empty\",\"patterns\":[]}")]
    public void Unusable_or_empty_language_grammar_falls_back_to_cpp(string grammar)
    {
        var root = Directory.CreateTempSubdirectory("codale-fallback-grammar-").FullName;
        try
        {
            var store = new SyntaxStore(root);
            store.Add(new LanguageEntry { Id = "empty", Name = "Empty", ScopeName = "source.empty", Extensions = ["empty"] }, grammar);
            var symbols = GrammarSymbols.Extract("a.empty", FallbackSource, null, store).ToArray();
            Assert.Contains(symbols, s => s.Name == "Widget" && s.Kind == SourceSymbolKind.Type);
            Assert.Contains(symbols, s => s.Name == "Run" && s.Kind == SourceSymbolKind.Function);
        }
        finally { Directory.Delete(root, recursive: true); }
    }

    [Fact]
    public void Cpp_fallback_preserves_multiline_comment_state()
    {
        var symbols = GrammarSymbols.Extract("a.unknown", "/*\nclass Fake {};\nvoid FakeMethod() {}\n*/\n" + FallbackSource, null).ToArray();
        Assert.Contains(symbols, s => s.Name == "Widget");
        Assert.DoesNotContain(symbols, s => s.Name is "Fake" or "FakeMethod");
    }

    [Theory]
    [InlineData("example.cs", "class Store { void Save(string customer) { int count = 0; } }", "count")]
    [InlineData("example.cs", "class Store { void Save(string customer) { int count = 0; } }", "customer")]
    [InlineData("example.ts", "const retryLimit = 3;", "retryLimit")]
    [InlineData("example.ps1", "$serverName = 'web'", "serverName")]
    public void Grammar_supplies_variable_names(string path, string text, string name)
    {
        Assert.Contains(GrammarSymbols.Extract(path, text, null), s => s.Name == name);
    }

    [Fact]
    public void Comments_strings_keywords_and_type_references_are_not_variables()
    {
        var text = """
            class Store
            {
                /* var commentName = 1;
                var anotherComment = 2; */
                void Save()
                {
                    var actualName = "var stringName = 3;";
                    RetryPolicy.Execute();
                }
            }
            """;
        var variables = GrammarSymbols.Extract("store.cs", text, null).ToArray();
        Assert.Contains(variables, s => s.Name == "actualName" && s.Line == 7);
        Assert.DoesNotContain(variables, s => new[] { "commentName", "anotherComment", "stringName", "var", "RetryPolicy", "void" }.Contains(s.Name));
    }

    [Theory]
    [InlineData("a.cs", "class Example : Base { void Run() { Other.Call(); var x = new Referenced(); } }", "Example", "Run")]
    [InlineData("a.ts", "class Example extends Base {} function Run(value: Referenced) { Other(); }", "Example", "Run")]
    [InlineData("a.py", "class Example(Base):\n    def Run(value):\n        return Other(value)", "Example", "Run")]
    [InlineData("a.java", "class Example extends Base { public void Run() { Other(); } }", "Example", "Run")]
    public void Definitions_exclude_calls_inheritance_and_annotations(string path, string text, string type, string function)
    {
        var symbols = GrammarSymbols.Extract(path, text, null).ToArray();
        Assert.Contains(symbols, s => s.Name == type && s.Kind == SourceSymbolKind.Type);
        Assert.Contains(symbols, s => s.Name == function && s.Kind == SourceSymbolKind.Function);
        Assert.DoesNotContain(symbols, s => new[] { "Base", "Referenced", "Other", "Call" }.Contains(s.Name));
    }

    [Fact]
    public void Multiline_comments_and_strings_do_not_define_types_or_functions()
    {
        var text = "class Actual {\n/*\nclass Fake {}\nvoid FakeMethod() {}\n*/\nstring text = \"class Imaginary {} void ImaginaryMethod() {}\";\nvoid RealMethod() {}\n}";
        var symbols = GrammarSymbols.Extract("a.cs", text, null).ToArray();
        Assert.Contains(symbols, s => s.Name == "Actual" && s.Kind == SourceSymbolKind.Type);
        Assert.Contains(symbols, s => s.Name == "RealMethod" && s.Kind == SourceSymbolKind.Function && s.Line == 7);
        Assert.DoesNotContain(symbols, s => new[] { "Fake", "FakeMethod", "Imaginary", "ImaginaryMethod" }.Contains(s.Name));
    }

    [Fact]
    public void Installed_grammar_drives_extraction_and_reloads_after_replacement()
    {
        var root = Directory.CreateTempSubdirectory("codale-variable-grammar-").FullName;
        try
        {
            var store = new SyntaxStore(root);
            var entry = new LanguageEntry { Id = "custom", Name = "Custom", ScopeName = "source.custom", Extensions = ["custom"] };
            const string grammar = """
                {"scopeName":"source.custom","patterns":[
                  {"match":"(?<=bind )\\w+","name":"entity.name.variable.custom"},
                  {"match":"(?<=shape )\\w+","name":"entity.name.type.class.custom"},
                  {"match":"(?<=perform )\\w+","name":"entity.name.function.custom"}
                ]}
                """;
            store.Add(entry, grammar);
            Assert.Equal(SourceSymbolKind.Type, Assert.Single(GrammarSymbols.Extract("a.custom", "shape Project", null, store)).Kind);
            Assert.Equal(SourceSymbolKind.Function, Assert.Single(GrammarSymbols.Extract("a.custom", "perform Build", null, store)).Kind);
            Assert.Equal("projectName", Assert.Single(GrammarSymbols.Extract("a.custom", "bind projectName", null, store)).Name);
            store.Add(entry, grammar.Replace("bind ", "hold "));
            Assert.Empty(GrammarSymbols.Extract("a.custom", "bind projectName", null, store));
            Assert.Equal("newName", Assert.Single(GrammarSymbols.Extract("a.custom", "hold newName", null, store)).Name);
        }
        finally { Directory.Delete(root, recursive: true); }
    }
}
