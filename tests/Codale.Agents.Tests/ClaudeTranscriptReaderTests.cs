using Codale.Agents.Claude;
using Codale.Core.Agents;

namespace Codale.Agents.Tests;

/// <summary>
/// The stored transcript format differs from the live stream in ways that are easy to
/// get wrong, so each difference gets a test: string-valued user content, sidechain
/// (subagent) records, and the bookkeeping record types the live stream never emits.
/// Fixtures are written by hand from shapes observed in real transcripts rather than
/// copying a real file, which would carry private project content into the repo.
/// </summary>
public sealed class ClaudeTranscriptReaderTests : IDisposable
{
    private readonly string _claudeHome = Directory.CreateTempSubdirectory("codale-transcripts-").FullName;
    private readonly string _projectPath = @"X:\Some\Project";

    private string WriteTranscript(string sessionId, params string[] lines)
    {
        var dir = Codale.Core.Projects.ProjectPaths.ClaudeHistoryDirectory(_projectPath, _claudeHome);
        Directory.CreateDirectory(dir);

        var path = Path.Combine(dir, sessionId + ".jsonl");
        File.WriteAllLines(path, lines);
        return path;
    }

    private ClaudeTranscriptReader Reader() => new(_projectPath, _claudeHome);

    [Fact]
    public void A_user_turn_stored_as_a_plain_string_is_read()
    {
        // The live protocol always sends an array of blocks; transcripts do not.
        var path = WriteTranscript("s1",
            """{"type":"user","message":{"role":"user","content":"do the thing"},"timestamp":"2026-09-17T10:00:00Z","sessionId":"s1"}""");

        var summary = Reader().Summarize(path)!;

        Assert.Equal(1, summary.UserTurns);
        Assert.Equal("do the thing", summary.FirstPrompt);

        var replayed = Reader().Replay(path).OfType<UserMessageRecorded>().Single();
        Assert.Equal("do the thing", replayed.Text);
    }

    [Fact]
    public void A_user_turn_stored_as_content_blocks_is_also_read()
    {
        var path = WriteTranscript("s2",
            """{"type":"user","message":{"role":"user","content":[{"type":"text","text":"blocks form"}]},"timestamp":"2026-09-17T10:00:00Z"}""");

        Assert.Equal("blocks form", Reader().Summarize(path)!.FirstPrompt);
    }

    [Fact]
    public void Tool_results_are_not_counted_as_things_the_user_typed()
    {
        // Tool results are stored as user records; counting them would make every
        // session look far busier than it was.
        var path = WriteTranscript("s3",
            """{"type":"user","message":{"role":"user","content":"real turn"},"timestamp":"2026-09-17T10:00:00Z"}""",
            """{"type":"user","message":{"role":"user","content":[{"type":"tool_result","tool_use_id":"t1","content":"ok"}]},"timestamp":"2026-09-17T10:00:01Z"}""");

        var summary = Reader().Summarize(path)!;

        Assert.Equal(1, summary.UserTurns);
        Assert.Equal("real turn", summary.FirstPrompt);

        var completed = Reader().Replay(path).OfType<ToolCallCompleted>().Single();
        Assert.Equal("t1", completed.ToolUseId);
    }

    [Fact]
    public void Subagent_sidechains_are_excluded_from_the_main_conversation()
    {
        var path = WriteTranscript("s4",
            """{"type":"user","message":{"role":"user","content":"main turn"},"timestamp":"2026-09-17T10:00:00Z"}""",
            """{"type":"user","isSidechain":true,"message":{"role":"user","content":"subagent turn"},"timestamp":"2026-09-17T10:00:01Z"}""");

        Assert.Equal(1, Reader().Summarize(path)!.UserTurns);

        var turns = Reader().Replay(path).OfType<UserMessageRecorded>().ToList();
        Assert.Single(turns);
        Assert.Equal("main turn", turns[0].Text);
    }

    [Fact]
    public void A_custom_title_wins_over_the_opening_prompt()
    {
        var path = WriteTranscript("s5",
            """{"type":"user","message":{"role":"user","content":"some long opening prompt"},"timestamp":"2026-09-17T10:00:00Z"}""",
            """{"type":"custom-title","customTitle":"Launch configuration","sessionId":"s5"}""");

        var summary = Reader().Summarize(path)!;

        Assert.Equal("Launch configuration", summary.CustomTitle);
        Assert.Equal("Launch configuration", summary.Title);
    }

    [Fact]
    public void Without_a_custom_title_the_opening_prompt_is_the_title()
    {
        var path = WriteTranscript("s6",
            """{"type":"user","message":{"role":"user","content":"add retry handling"},"timestamp":"2026-09-17T10:00:00Z"}""");

        Assert.Equal("add retry handling", Reader().Summarize(path)!.Title);
    }

    [Fact]
    public void Assistant_messages_and_tool_calls_replay_in_order()
    {
        var path = WriteTranscript("s7",
            """{"type":"user","message":{"role":"user","content":"go"},"timestamp":"2026-09-17T10:00:00Z"}""",
            """{"type":"assistant","message":{"id":"m1","model":"claude-opus-5","content":[{"type":"text","text":"Working on it"},{"type":"tool_use","id":"t1","name":"Write","input":{"file_path":"a.txt"}}]},"timestamp":"2026-09-17T10:00:01Z"}""");

        var events = Reader().Replay(path).ToList();

        Assert.IsType<UserMessageRecorded>(events[0]);

        var tool = events.OfType<ToolCallStarted>().Single();
        Assert.Equal("Write", tool.ToolName);
        Assert.Equal("a.txt", tool.Input.GetProperty("file_path").GetString());

        var assistant = events.OfType<AssistantMessageCompleted>().Single();
        Assert.Equal("Working on it", assistant.Text);
        Assert.Equal("claude-opus-5", assistant.Model);
    }

    [Fact]
    public void Bookkeeping_records_and_malformed_lines_are_skipped_without_throwing()
    {
        var path = WriteTranscript("s8",
            """{"type":"mode","mode":"normal"}""",
            """{"type":"file-history-snapshot","snapshot":{}}""",
            """{"type":"attachment","x":1}""",
            "not json at all",
            "",
            """{"type":"user","message":{"role":"user","content":"survived"},"timestamp":"2026-09-17T10:00:00Z"}""");

        var summary = Reader().Summarize(path)!;
        Assert.Equal(1, summary.UserTurns);
        Assert.Equal("survived", summary.FirstPrompt);
    }

    [Fact]
    public void Sessions_are_listed_newest_first_and_empty_ones_dropped()
    {
        WriteTranscript("older",
            """{"type":"user","message":{"role":"user","content":"first"},"timestamp":"2026-09-16T10:00:00Z"}""");
        WriteTranscript("newer",
            """{"type":"user","message":{"role":"user","content":"second"},"timestamp":"2026-09-17T10:00:00Z"}""");

        // No user turns: bookkeeping only, so not worth listing.
        WriteTranscript("empty", """{"type":"mode","mode":"normal"}""");

        var sessions = Reader().ListSessions();

        Assert.Equal(2, sessions.Count);
        Assert.Equal("newer", sessions[0].SessionId);
        Assert.Equal("older", sessions[1].SessionId);
    }

    [Fact]
    public void A_project_with_no_history_lists_nothing_rather_than_throwing()
    {
        Assert.Empty(new ClaudeTranscriptReader(@"X:\Never\Opened", _claudeHome).ListSessions());
    }

    public void Dispose()
    {
        try
        {
            Directory.Delete(_claudeHome, recursive: true);
        }
        catch (IOException)
        {
        }
    }
}
