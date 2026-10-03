using Codale.Core.Helper;

namespace Codale.Core.Tests;

public sealed class HelperToolCallsTests
{
    private static readonly IReadOnlyList<ToolDefinition> Tools = [MessageRouting.RouteTool];

    [Fact]
    public void Parses_a_bare_json_call()
    {
        var call = HelperToolCalls.Parse(
            """{"tool":"route","arguments":{"message":"fix it","intent":"act","related":"yes"}}""", Tools);

        Assert.NotNull(call);
        Assert.Equal("route", call.Tool);
        Assert.Equal("act", call.GetString("intent"));
    }

    [Fact]
    public void Finds_the_object_inside_fences_and_chatter()
    {
        var reply = "Sure!\n```json\n{\"tool\": \"route\", \"arguments\": {\"message\": \"a {b} c\", \"intent\": \"ask\", \"related\": \"no\"}}\n```\nDone.";

        var call = HelperToolCalls.Parse(reply, Tools);

        Assert.Equal("a {b} c", call?.GetString("message"));
    }

    [Theory]
    [InlineData("")]
    [InlineData("no json here")]
    [InlineData("""{"tool":"other","arguments":{}}""")]
    [InlineData("""{"tool":"route","arguments":{"message":"x","intent":"act"}}""")]
    [InlineData("""{"tool":"route","arguments":{"message":"x","intent":"destroy","related":"yes"}}""")]
    public void Rejects_replies_that_are_not_a_valid_call(string reply) =>
        Assert.Null(HelperToolCalls.Parse(reply, Tools));

    [Fact]
    public void System_prompt_lists_the_tools()
    {
        var prompt = HelperToolCalls.BuildSystemPrompt("Base.", Tools);

        Assert.StartsWith("Base.", prompt);
        Assert.Contains("- route:", prompt);
    }
}
