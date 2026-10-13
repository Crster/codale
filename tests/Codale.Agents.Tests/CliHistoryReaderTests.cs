using Codale.Agents.Antigravity;
using Codale.Agents.History;
using Codale.Core.Agents;

namespace Codale.Agents.Tests;

public class CliHistoryReaderTests
{
    [Fact]
    public void CliHistoryRegistry_resolves_all_known_clis()
    {
        var tempDir = Path.Combine(Path.GetTempPath(), "codale-test-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(tempDir);
        try
        {
            var registry = new CliHistoryRegistry(tempDir);

            Assert.True(registry.IsSupported("agy"));
            Assert.True(registry.IsSupported("aider"));
            Assert.True(registry.IsSupported("goose"));
            Assert.True(registry.IsSupported("gemini"));
            Assert.True(registry.IsSupported("qwen"));
            Assert.True(registry.IsSupported("copilot"));
            Assert.True(registry.IsSupported("codex"));
            Assert.True(registry.IsSupported("opencode"));
            Assert.True(registry.IsSupported("cursor"));

            Assert.False(registry.IsSupported("unknown_cli"));
            Assert.Null(registry.GetReader("unknown_cli"));

            var agyReader = registry.GetReader("agy");
            Assert.NotNull(agyReader);
            Assert.Equal("agy", agyReader.CliId);

            var aiderReader = registry.GetReader("aider");
            Assert.NotNull(aiderReader);
            Assert.Equal("aider", aiderReader.CliId);

            var gooseReader = registry.GetReader("goose");
            Assert.NotNull(gooseReader);
            Assert.Equal("goose", gooseReader.CliId);
        }
        finally
        {
            if (Directory.Exists(tempDir))
            {
                Directory.Delete(tempDir, true);
            }
        }
    }

    [Fact]
    public void AiderHistoryReader_parses_markdown_chat_history()
    {
        var tempDir = Path.Combine(Path.GetTempPath(), "codale-test-aider-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(tempDir);
        try
        {
            var chatFile = Path.Combine(tempDir, ".aider.chat.history.md");
            var markdownContent =
@"# aider chat started at 2026-03-15 10:00:00

#### Can you add a login form?

I will add the login form for you.
```html
<form id=""login""></form>
```

#### Also add a password reset link

Here is the password reset link.
";
            File.WriteAllText(chatFile, markdownContent);

            var reader = new AiderHistoryReader(tempDir);
            Assert.True(reader.IsSessionFile(chatFile));

            var sessions = reader.ListSessions();
            Assert.Single(sessions);

            var session = sessions[0];
            Assert.Equal(2, session.UserTurns);
            Assert.Equal("Can you add a login form?", session.FirstPrompt);
            Assert.Equal("Also add a password reset link", session.LastPrompt);

            var events = reader.Replay(session.SessionId).ToList();
            Assert.NotEmpty(events);
            Assert.Contains(events, e => e is UserMessageRecorded u && u.Text == "Can you add a login form?");
            Assert.Contains(events, e => e is AssistantMessageCompleted a && a.Text.Contains("<form id=\"login\">"));
        }
        finally
        {
            if (Directory.Exists(tempDir))
            {
                Directory.Delete(tempDir, true);
            }
        }
    }

    [Fact]
    public void GooseHistoryReader_parses_session_json()
    {
        var tempDir = Path.Combine(Path.GetTempPath(), "codale-test-goose-" + Guid.NewGuid().ToString("N"));
        var sessionsDir = Path.Combine(tempDir, "sessions");
        Directory.CreateDirectory(sessionsDir);
        try
        {
            var projectDir = Path.Combine(tempDir, "my-project");
            Directory.CreateDirectory(projectDir);

            var sessionFile = Path.Combine(sessionsDir, "session-abc.json");
            var json = $$"""
{
  "id": "session-abc",
  "working_dir": "{{projectDir.Replace("\\", "\\\\")}}",
  "created_at": "2026-03-15T12:00:00Z",
  "updated_at": "2026-03-15T12:05:00Z",
  "messages": [
    {
      "role": "user",
      "content": "Create a new React component",
      "created_at": "2026-03-15T12:00:00Z"
    },
    {
      "role": "assistant",
      "content": "Here is the React component:\nexport default function App() {}",
      "created_at": "2026-03-15T12:01:00Z"
    }
  ]
}
""";
            File.WriteAllText(sessionFile, json);

            var reader = new GooseHistoryReader(projectDir, sessionsDir);
            Assert.True(reader.IsSessionFile(sessionFile));

            var sessions = reader.ListSessions();
            Assert.Single(sessions);

            var s = sessions[0];
            Assert.Equal("session-abc", s.SessionId);
            Assert.Equal(1, s.UserTurns);
            Assert.Equal("Create a new React component", s.FirstPrompt);

            var events = reader.Replay(s.SessionId).ToList();
            Assert.Equal(2, events.Count);
            Assert.IsType<UserMessageRecorded>(events[0]);
            Assert.IsType<AssistantMessageCompleted>(events[1]);
            Assert.Equal("Create a new React component", ((UserMessageRecorded)events[0]).Text);
            Assert.Contains("export default function App", ((AssistantMessageCompleted)events[1]).Text);
        }
        finally
        {
            if (Directory.Exists(tempDir))
            {
                Directory.Delete(tempDir, true);
            }
        }
    }

    [Fact]
    public void AgyHistoryReader_replays_transcript_jsonl_when_present()
    {
        var tempDir = Path.Combine(Path.GetTempPath(), "codale-test-agy-" + Guid.NewGuid().ToString("N"));
        var brainDir = Path.Combine(tempDir, "brain", "conv-1", ".system_generated", "logs");
        Directory.CreateDirectory(brainDir);
        try
        {
            var projectDir = Path.Combine(tempDir, "workspace");
            Directory.CreateDirectory(projectDir);

            var historyFile = Path.Combine(tempDir, "history.jsonl");
            var historyLine = $$"""{"conversationId":"conv-1","workspace":"{{projectDir.Replace("\\", "\\\\")}}","display":"Original prompt in history","timestamp":1773500000000}""";
            File.WriteAllText(historyFile, historyLine + Environment.NewLine);

            var transcriptFile = Path.Combine(brainDir, "transcript.jsonl");
            var turn1 = """{"step_index":0,"type":"USER_INPUT","created_at":"2026-03-15T10:00:00Z","content":"<USER_REQUEST>\nClean prompt from request\n</USER_REQUEST>"}""";
            var turn2 = """{"step_index":1,"type":"PLANNER_RESPONSE","created_at":"2026-03-15T10:01:00Z","content":"I have completed the task."}""";
            File.WriteAllText(transcriptFile, turn1 + "\n" + turn2 + "\n");

            var reader = new AgyHistoryReader(projectDir, tempDir);
            var sessions = reader.ListSessions();
            Assert.Single(sessions);
            Assert.Equal("conv-1", sessions[0].SessionId);

            var events = reader.Replay("conv-1").ToList();
            Assert.Equal(2, events.Count);

            var userEvent = Assert.IsType<UserMessageRecorded>(events[0]);
            Assert.Equal("Clean prompt from request", userEvent.Text);

            var assistantEvent = Assert.IsType<AssistantMessageCompleted>(events[1]);
            Assert.Equal("I have completed the task.", assistantEvent.Text);
        }
        finally
        {
            if (Directory.Exists(tempDir))
            {
                Directory.Delete(tempDir, true);
            }
        }
    }

    [Fact]
    public void JsonlPromptHistoryReader_filters_by_workspace_and_replays()
    {
        var tempDir = Path.Combine(Path.GetTempPath(), "codale-test-jsonl-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(tempDir);
        try
        {
            var targetProject = Path.Combine(tempDir, "project-a");
            var otherProject = Path.Combine(tempDir, "project-b");
            Directory.CreateDirectory(targetProject);
            Directory.CreateDirectory(otherProject);

            var historyFile = Path.Combine(tempDir, "history.jsonl");
            var lineA = $$"""{"conversationId":"session-a","workspace":"{{targetProject.Replace("\\", "\\\\")}}","prompt":"Add tests for Project A","timestamp":"2026-03-15T14:00:00Z"}""";
            var lineB = $$"""{"conversationId":"session-b","workspace":"{{otherProject.Replace("\\", "\\\\")}}","prompt":"Something else in Project B","timestamp":"2026-03-15T14:05:00Z"}""";
            File.WriteAllText(historyFile, lineA + "\n" + lineB + "\n");

            var reader = new JsonlPromptHistoryReader("gemini", targetProject, [historyFile]);
            var sessions = reader.ListSessions();

            Assert.Single(sessions);
            Assert.Equal("session-a", sessions[0].SessionId);
            Assert.Equal("Add tests for Project A", sessions[0].FirstPrompt);

            var events = reader.Replay("session-a").ToList();
            Assert.Single(events);
            Assert.Equal("Add tests for Project A", ((UserMessageRecorded)events[0]).Text);
        }
        finally
        {
            if (Directory.Exists(tempDir))
            {
                Directory.Delete(tempDir, true);
            }
        }
    }
}
