using Codale.Core.Features;

namespace Codale.Core.Tests;

public sealed class SymbolChunkingTests
{
    private const string Source = """
        using System;

        namespace Demo;

        /// <summary>Wraps text.</summary>
        public static class Briefing
        {
            private const int Limit = 5;

            /// <summary>Opens the block.</summary>
            [Obsolete]
            public static string Open(string text) => "<" + text + ">";

            public static string Close(string text) => "</" + text + ">";
        }
        """;

    [Fact]
    public void A_method_keeps_its_comment_and_attribute_and_names_its_enclosing_type()
    {
        var chunk = Assert.Single(FileChunker.Chunk(Source, "Briefing.cs"));
        Assert.Contains("Briefing", chunk.Symbols);
        Assert.Contains("Open", chunk.Symbols);
        Assert.Contains("Close", chunk.Symbols);
        Assert.DoesNotContain("Limit", chunk.Symbols);   // a constant is not a unit of code
        var lines = chunk.Text.Split('\n').Select(l => l.Trim()).ToList();
        var comment = lines.IndexOf("/// <summary>Opens the block.</summary>");
        Assert.True(comment >= 0 && lines[comment + 1] == "[Obsolete]" && lines[comment + 2].StartsWith("public static string Open"));
    }

    [Fact]
    public void Cuts_fall_on_symbol_boundaries_not_in_the_middle_of_a_method()
    {
        var methods = string.Join("\n", Enumerable.Range(0, 12).Select(i =>
            $"    public void Step{i}()\n    {{\n" + string.Join("\n", Enumerable.Range(0, 9).Select(j => $"        Work({i}, {j});")) + "\n    }\n"));
        var chunks = FileChunker.Chunk("class Pipeline\n{\n" + methods + "}\n", "Pipeline.cs");

        Assert.True(chunks.Count > 1);
        foreach (var chunk in chunks)
        {
            // Every chunk starts at a declaration and ends on a closing brace: no method is cut in two.
            Assert.Matches(@"^(class Pipeline|\s*public void Step\d+)", chunk.Text);
            Assert.EndsWith("}", chunk.Text.TrimEnd());
        }
        Assert.Contains(chunks, c => c.Symbols.Contains("Pipeline") && c.Symbols.Contains("Step7"));
    }

    [Fact]
    public void A_method_too_big_for_one_chunk_is_windowed_and_every_window_names_it()
    {
        var body = string.Join("\n", Enumerable.Range(0, 120).Select(i => $"        Step({i});"));
        var chunks = FileChunker.Chunk("class Big\n{\n    public void Huge()\n    {\n" + body + "\n    }\n}\n", "Big.cs");

        var windows = chunks.Where(c => c.Symbols.Contains("Huge")).ToList();
        Assert.True(windows.Count >= 3);
        Assert.All(windows, w => Assert.Contains("Big", w.Symbols));
        Assert.True(windows[1].StartLine < windows[0].StartLine + 40, "windows overlap");
        Assert.Contains("Step(119)", chunks[^1].Text);
    }

    [Fact]
    public void Markdown_is_cut_at_headings_and_named_after_them()
    {
        var chunks = FileChunker.Chunk("# Guide\nintro\n\n## Install\nrun setup\n\n## Usage\nrun it\n", "README.md");
        Assert.Contains(chunks, c => c.Symbols.Contains("Install"));
        Assert.Contains(chunks, c => c.Symbols.Contains("Usage"));
    }

    [Fact]
    public void Arrow_functions_and_go_methods_are_units()
    {
        Assert.Equal("function", SymbolScanner.Match("const handleClick = (event) => {")!.Value.Kind);
        Assert.Equal("handleClick", SymbolScanner.Match("export const handleClick = async (e) => {")!.Value.Name);
        Assert.Equal("Serve", SymbolScanner.Match("func (s *Server) Serve(addr string) error {")!.Value.Name);
        Assert.Null(SymbolScanner.Match("const int Max = 6;"));
        Assert.Null(SymbolScanner.Match("namespace Codale.Search;"));
    }

    [Fact]
    public void Large_hand_written_files_are_indexable_but_minified_bundles_are_not()
    {
        var source = string.Join("\n", Enumerable.Range(0, 12_000).Select(i => $"    var value{i} = Compute({i});"));
        Assert.True(source.Length > 250_000);
        Assert.True(FileChunker.IsIndexable(source));
        Assert.False(FileChunker.IsIndexable(new string('x', 400_000)));
        Assert.False(FileChunker.IsIndexable(string.Join(' ', Enumerable.Range(0, 60_000).Select(i => "a" + i))));
    }
}
