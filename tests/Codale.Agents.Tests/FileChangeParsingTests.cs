using Codale.Agents.Claude;
using Codale.Core.Agents;

namespace Codale.Agents.Tests;

public sealed class FileChangeParsingTests
{
    [Fact]
    public void Write_tool_result_yields_a_create_file_change()
    {
        var parser = new ClaudeStreamParser();
        var events = File.ReadLines(Fixtures.Path("write-tool-only.jsonl"))
            .SelectMany(parser.Parse)
            .ToList();

        var completed = events.OfType<ToolCallCompleted>().ToList();
        Assert.NotEmpty(completed);

        var change = completed.Select(c => c.FileChange).OfType<FileChange>().FirstOrDefault();
        Assert.NotNull(change);
        Assert.Equal(FileChangeKind.Create, change!.Kind);
        Assert.EndsWith("hello.txt", change.FilePath);
        Assert.Equal("hi", change.NewContent);
    }

    /// <summary>
    /// An Edit result has no "type" - only the swapped strings, the original file and
    /// the CLI's patch - and must still read as an update carrying that patch.
    /// </summary>
    [Fact]
    public void Edit_tool_result_yields_an_update_with_its_patch()
    {
        const string line = """
            {"type":"user","message":{"role":"user","content":[{"type":"tool_result","tool_use_id":"toolu_1","content":"ok"}]},
             "tool_use_result":{"filePath":"C:\\p\\a.cs","oldString":"old","newString":"new","originalFile":"a\nold\nb\n",
             "structuredPatch":[{"oldStart":1,"oldLines":3,"newStart":1,"newLines":3,"lines":[" a","-old","+new"," b"]}],
             "userModified":false,"replaceAll":false}}
            """;

        var completed = new ClaudeStreamParser().Parse(line).OfType<ToolCallCompleted>().Single();

        var change = Assert.IsType<FileChange>(completed.FileChange);
        Assert.Equal(FileChangeKind.Update, change.Kind);
        Assert.Equal("a\nold\nb\n", change.OriginalContent);
        var hunk = Assert.Single(change.Patch!);
        Assert.Equal(1, hunk.OldStart);
        Assert.Equal([" a","-old", "+new", " b"], hunk.Lines);
    }

    [Fact]
    public void Read_style_result_without_edit_fields_stays_unknown()
    {
        const string line = """
            {"type":"user","message":{"role":"user","content":[{"type":"tool_result","tool_use_id":"t","content":"x"}]},
             "tool_use_result":{"filePath":"a.txt"}}
            """;

        var completed = new ClaudeStreamParser().Parse(line).OfType<ToolCallCompleted>().Single();

        Assert.Equal(FileChangeKind.Unknown, completed.FileChange!.Kind);
        Assert.Null(completed.FileChange.Patch);
    }
}
