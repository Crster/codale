using Codale.Core.Agents;

namespace Codale.Agents.Tests;

public class SubagentTranscriptTests
{
    [Fact]
    public void Reads_a_tool_call()
    {
        var steps = SubagentTranscript.Parse(
            """{"type":"assistant","isSidechain":true,"agentId":"ac210eafb8b6f802b","message":{"id":"msg_01","role":"assistant","content":[{"type":"tool_use","id":"toolu_1","name":"Grep","input":{"pattern":"fathom"}}]}}""");

        var use = Assert.IsType<SubagentToolUse>(Assert.Single(steps));
        Assert.Equal("toolu_1", use.Id);
        Assert.Equal("Grep", use.Name);
        Assert.Equal("fathom", use.Input.GetProperty("pattern").GetString());
    }

    [Fact]
    public void Reads_a_tool_result_in_either_content_shape()
    {
        var plain = SubagentTranscript.Parse(
            """{"type":"user","message":{"role":"user","content":[{"type":"tool_result","tool_use_id":"toolu_1","content":"Found 20 files"}]}}""");
        var blocks = SubagentTranscript.Parse(
            """{"type":"user","message":{"role":"user","content":[{"type":"tool_result","tool_use_id":"toolu_2","is_error":true,"content":[{"type":"text","text":"denied"}]}]}}""");

        Assert.Equal(new SubagentToolResult("toolu_1", "Found 20 files", false), Assert.Single(plain));
        Assert.Equal(new SubagentToolResult("toolu_2", "denied", true), Assert.Single(blocks));
    }

    [Fact]
    public void Reads_words_and_thinking()
    {
        var steps = SubagentTranscript.Parse(
            """{"type":"assistant","message":{"id":"msg_02","content":[{"type":"thinking","thinking":"look at prisma"},{"type":"text","text":"The schema has five entities."}]}}""");

        Assert.Equal(
            [new SubagentText("msg_02", "look at prisma", true), new SubagentText("msg_02", "The schema has five entities.", false)],
            steps);
    }

    [Theory]
    [InlineData("""{"type":"user","message":{"role":"user","content":"Explore X:\\Hike\\jando\\dashboard"}}""")]
    [InlineData("""{"type":"api-request-blob","message":{"role":"user","content":[{"type":"text","text":"<system-reminder>"}]}}""")]
    [InlineData("""{"type":"attachment","attachment":{"type":"date"}}""")]
    [InlineData("""{"type":"assistant","message":{"content":[{"type":"tool_use","na""")]
    [InlineData("")]
    public void Skips_the_brief_bookkeeping_and_broken_lines(string line) =>
        Assert.Empty(SubagentTranscript.Parse(line));

    [Fact]
    public void Finds_the_transcript_under_any_project()
    {
        var home = Directory.CreateTempSubdirectory("codale-subagent-").FullName;
        try
        {
            var folder = Directory.CreateDirectory(Path.Combine(home, "projects", "X--Hike-jando", "6b93b39c", "subagents")).FullName;
            var path = Path.Combine(folder, "agent-ac210eafb8b6f802b.jsonl");
            File.WriteAllText(path, "");

            Assert.Equal(path, SubagentTranscript.Find(home, "6b93b39c", "ac210eafb8b6f802b"));
            Assert.Null(SubagentTranscript.Find(home, "6b93b39c", "other"));
            Assert.Null(SubagentTranscript.Find(home, "6b93b39c", "..\\x"));
        }
        finally
        {
            Directory.Delete(home, recursive: true);
        }
    }

    [Fact]
    public void Reads_the_agent_id_from_the_launch_receipt()
    {
        Assert.Equal("ac210eafb8b6f802b", BackgroundTaskDetector.ParseAgentId(
            "Async agent launched successfully. (This tool result is internal metadata.)\nagentId: ac210eafb8b6f802b (internal ID - do not mention to user.)"));
        Assert.Null(BackgroundTaskDetector.ParseAgentId("Done."));
        Assert.Null(BackgroundTaskDetector.ParseAgentId(null));
    }
}
