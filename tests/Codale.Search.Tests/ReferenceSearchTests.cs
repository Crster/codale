using Codale.Db;
using Codale.Search.Engine;

namespace Codale.Search.Tests;

public sealed class ReferenceSearchTests : IDisposable
{
    private readonly string _root = Directory.CreateTempSubdirectory("codale-references-").FullName;
    private string Project => Path.Combine(_root, "project");

    [Fact]
    public async Task Member_lookup_follows_its_receiver_and_keeps_other_titles_as_text()
    {
        Write("Menu.cs", """
            class Menu
            {
                public string Title { get; set; }
                public Menu(string title) { Title = title; }
            }
            """);
        Write("Page.cs", """
            class Page
            {
                public string Title { get; set; }
            }
            """);
        Write("Other.cs", """
            class Other
            {
                void Render(Menu menu) { Console.WriteLine(menu.Title); }
                void Display(Page page) { Console.WriteLine(page.Title); }
                void Example(Menu menu) { Console.WriteLine("menu.Title"); }
            }
            """);
        const string saved = """
            class MainView
            {
                void Build()
                {
                    var menu1 = new Page();
                    menu1.Title = "Before";
                }
            }
            """;
        var current = saved.Replace("new Page()", """new Menu("blala")""").Replace("Before", "Blele");
        var path = Write("MainView.cs", saved);
        using var db = new CodaleDatabase(Path.Combine(_root, "references.db"));
        using var knowledge = new ProjectKnowledgeIndex(db);
        await knowledge.RefreshAsync(Project);
        using var index = new SourceIndex(Project);
        var at = current.IndexOf(".Title", StringComparison.Ordinal) + 1;
        var target = Assert.IsType<ReferenceQuery>(ReferenceQuery.At(path, current, at, at));

        var result = await new ReferenceSearch(index, knowledge).RunAsync(target);

        Assert.Equal("Definition", result.Files[0].Reason);
        Assert.Equal("Menu.cs", result.Files[0].RelativePath);
        Assert.Equal(3, Assert.Single(result.Files[0].Ranges).StartLine);
        Assert.Equal("Parent flow", result.Files[1].Reason);
        Assert.Contains("""new Menu("blala")""", Assert.Single(result.Files[1].Ranges).Code);
        Assert.Contains(result.Files, f => f.Reason == "Usage" && f.RelativePath == "MainView.cs" && f.Ranges.Any(r => r.StartLine == 6));
        Assert.Contains(result.Files, f => f.Reason == "Usage" && f.RelativePath == "Other.cs" && f.Ranges.Any(r => r.StartLine == 3));
        Assert.Contains(result.Files, f => f.Reason == "Text match" && f.RelativePath == "Page.cs");
        Assert.Contains(result.Files, f => f.Reason == "Text match" && f.RelativePath == "Other.cs" && f.Ranges.Any(r => r.StartLine == 4));
        Assert.Contains(result.Files, f => f.Reason == "Text match" && f.RelativePath == "Other.cs" && f.Ranges.Any(r => r.StartLine == 5));
        Assert.DoesNotContain(result.Files, f => f.Reason == "Definition" && f.RelativePath != "Menu.cs");
    }

    [Fact]
    public async Task Literal_selection_preserves_punctuation_and_matches_across_line_endings()
    {
        const string text = "start\nroute =>\n/path!?\nend";
        var path = Write("notes.txt", text);
        Write("config.txt", text.ReplaceLineEndings("\r\n"));
        Write("near.txt", text.Replace("!", ""));
        using var index = new SourceIndex(Project);
        var target = Assert.IsType<ReferenceQuery>(ReferenceQuery.At(path, text, 6, text.IndexOf("\nend", StringComparison.Ordinal)));

        var result = await new ReferenceSearch(index).RunAsync(target);

        Assert.Equal(["config.txt", "notes.txt"], result.Files.Select(f => f.RelativePath));
        Assert.All(result.Files, file =>
        {
            Assert.Equal("Text match", file.Reason);
            Assert.Equal([2, 3], Assert.Single(file.Ranges).MatchLines);
        });
    }

    [Theory]
    [InlineData("Menu.cs", "class Menu { public string Title = \"\"; }", "Use.cs", "var menu = new Menu();\nmenu.Title = \"new\";")]
    [InlineData("menu.ts", "class Menu { Title: string = \"\"; }", "use.ts", "const menu = new Menu();\nmenu.Title = \"new\";")]
    public async Task Member_field_definitions_use_the_language_grammar(string typePath, string type, string callerPath, string caller)
    {
        Write(typePath, type);
        var path = Write(callerPath, caller);
        using var index = new SourceIndex(Project);
        var offset = caller.IndexOf("Title", StringComparison.Ordinal);
        var query = Assert.IsType<ReferenceQuery>(ReferenceQuery.At(path, caller, offset, offset));

        var result = await new ReferenceSearch(index).RunAsync(query);

        Assert.Equal("Definition", result.Files[0].Reason);
        Assert.Equal(typePath, result.Files[0].RelativePath);
        Assert.Equal("Parent flow", result.Files[1].Reason);
        Assert.Equal(1, Assert.Single(result.Files[1].Ranges).StartLine);
    }

    [Fact]
    public async Task An_unresolved_receiver_does_not_claim_a_definition_with_the_same_name()
    {
        Write("Menu.cs", "class Menu { public string Title { get; set; } }");
        const string text = """
            var menu = new Menu();
            other.menu.Title = "new";
            """;
        var path = Write("Use.cs", text);
        using var index = new SourceIndex(Project);
        var offset = text.IndexOf("Title", StringComparison.Ordinal);
        var query = Assert.IsType<ReferenceQuery>(ReferenceQuery.At(path, text, offset, offset));

        var result = await new ReferenceSearch(index).RunAsync(query);

        Assert.NotEmpty(result.Files);
        Assert.All(result.Files, file => Assert.Equal("Text match", file.Reason));
    }

    [Fact]
    public async Task A_word_selected_in_prose_stays_a_literal_lookup()
    {
        Write("Menu.cs", "class Menu {}");
        const string prose = "Open the Menu.";
        var path = Write("notes.txt", prose);
        using var index = new SourceIndex(Project);
        var query = Assert.IsType<ReferenceQuery>(ReferenceQuery.At(path, prose, 9, 9));

        var result = await new ReferenceSearch(index).RunAsync(query);

        Assert.Equal(2, result.Files.Count);
        Assert.All(result.Files, file => Assert.Equal("Text match", file.Reason));
    }

    private string Write(string name, string text)
    {
        Directory.CreateDirectory(Project);
        var path = Path.Combine(Project, name);
        File.WriteAllText(path, text);
        return path;
    }

    public void Dispose() => Directory.Delete(_root, recursive: true);
}
