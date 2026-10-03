using System.Text.Json;

using Codale.Core.Agents;

namespace Codale.Core.Tests;

public sealed class TodoParserTests
{
    private static JsonElement Input(string json) => JsonDocument.Parse(json).RootElement.Clone();

    [Fact]
    public void A_todo_list_is_extracted_in_order_with_statuses()
    {
        var todos = TodoParser.FromToolInput(Input("""
            {"todos":[
              {"content":"Read the parser","status":"completed"},
              {"content":"Write the tests","status":"in_progress"},
              {"content":"Wire the panel","status":"pending"}
            ]}
            """));

        Assert.Equal(3, todos.Count);
        Assert.Equal("Read the parser", todos[0].Content);
        Assert.True(todos[0].IsDone);
        Assert.Equal(TodoStatus.InProgress, todos[1].Status);
        Assert.Equal(TodoStatus.Pending, todos[2].Status);
    }

    [Theory]
    [InlineData("completed", TodoStatus.Completed)]
    [InlineData("done", TodoStatus.Completed)]
    [InlineData("in_progress", TodoStatus.InProgress)]
    [InlineData("inProgress", TodoStatus.InProgress)]
    [InlineData("pending", TodoStatus.Pending)]
    [InlineData("something-new", TodoStatus.Pending)]
    [InlineData(null, TodoStatus.Pending)]
    public void Status_spellings_are_tolerated(string? status, TodoStatus expected)
    {
        var json = status is null
            ? """{"todos":[{"content":"x"}]}"""
            : $$"""{"todos":[{"content":"x","status":"{{status}}"}]}""";

        Assert.Equal(expected, TodoParser.FromToolInput(Input(json))[0].Status);
    }

    [Fact]
    public void Entries_without_any_text_are_skipped()
    {
        var todos = TodoParser.FromToolInput(Input("""
            {"todos":[{"status":"pending"},{"content":"   "},{"content":"real"}]}
            """));

        Assert.Equal("real", Assert.Single(todos).Content);
    }

    [Theory]
    [InlineData("""{"todos":[]}""")]
    [InlineData("""{"todos":"not an array"}""")]
    [InlineData("""{"other":"thing"}""")]
    [InlineData("""{}""")]
    [InlineData("""[1,2,3]""")]
    [InlineData("\"a bare json string\"")]
    public void Anything_unexpected_yields_an_empty_list_rather_than_throwing(string json)
    {
        // The shape is unverified against real data, so the panel must degrade to empty
        // rather than taking the chat down.
        Assert.Empty(TodoParser.FromToolInput(Input(json)));
    }

    [Theory]
    [InlineData("mcp__codale-tasks__todos_set", true)]
    [InlineData("codale-tasks-todos_set", true)]
    [InlineData("TodoWrite", false)]
    [InlineData("TaskCreate", false)]
    public void Only_our_todos_tool_is_recognised(string name, bool expected) =>
        Assert.Equal(expected, TodoParser.IsTodosTool(name));
}
