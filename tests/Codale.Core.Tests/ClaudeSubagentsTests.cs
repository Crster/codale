using System.Text.Json;

using Codale.Core.Agents;

namespace Codale.Core.Tests;

public sealed class ClaudeSubagentsTests
{
    [Fact]
    public void Built_ins_are_replaced_with_cheaper_models()
    {
        var root = JsonDocument.Parse(ClaudeSubagents.Build("claude-sonnet-5-5", assist: true)).RootElement;

        var explore = root.GetProperty("Explore");
        Assert.Equal("haiku", explore.GetProperty("model").GetString());
        Assert.Contains("Edit", explore.GetProperty("disallowedTools").EnumerateArray().Select(t => t.GetString()));
        Assert.Contains("mcp__codale-tasks__explore", explore.GetProperty("prompt").GetString());

        var worker = root.GetProperty("general-purpose");
        Assert.Equal("sonnet", worker.GetProperty("model").GetString());
        Assert.Equal("medium", worker.GetProperty("effort").GetString());
    }

    [Fact]
    public void Without_the_assist_tools_the_prompts_do_not_mention_them()
    {
        var json = ClaudeSubagents.Build(null, assist: false);
        Assert.DoesNotContain("mcp__codale-tasks", json);
    }

    [Fact]
    public void A_chat_on_haiku_keeps_its_workers_on_its_own_model() =>
        Assert.Equal(
            "inherit",
            JsonDocument.Parse(ClaudeSubagents.Build("haiku", assist: false)).RootElement
                .GetProperty("general-purpose").GetProperty("model").GetString());
}
