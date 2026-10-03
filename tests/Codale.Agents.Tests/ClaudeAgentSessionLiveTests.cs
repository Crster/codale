using Codale.Agents.Claude;
using Codale.Core.Agents;

namespace Codale.Agents.Tests;

/// <summary>
/// End-to-end tests against the real <c>claude</c> CLI. These spend tokens and need
/// a logged-in CLI, so they are opt-in: set <c>CODALE_LIVE_TESTS=1</c> to run them.
/// Everything they cover that can be covered offline is also covered by the replay
/// tests in <see cref="ClaudeStreamParserTests"/>.
/// </summary>
public sealed class ClaudeAgentSessionLiveTests
{
    private static bool Enabled =>
        Environment.GetEnvironmentVariable("CODALE_LIVE_TESTS") == "1";

    [Fact]
    public async Task Host_approves_a_write_and_the_file_appears_on_disk()
    {
        if (!Enabled)
        {
            return;
        }

        var work = Directory.CreateTempSubdirectory("codale-live-");
        try
        {
            await using var session = new ClaudeAgentSession(new ClaudeSessionOptions
            {
                WorkingDirectory = work.FullName,
                Model = "haiku",
                PermissionMode = "manual",
                SessionId = Guid.NewGuid(),

                // Without this the model may reach for PowerShell to create the file.
                // Pinning the tool set keeps the assertions deterministic and exercises
                // the structured tool_use_result that the file-changes panel depends on.
                ExtraArguments = ["--tools", "Write"],

                RawLogPath = Environment.GetEnvironmentVariable("CODALE_LIVE_RAW_LOG"),
            });

            using var cts = new CancellationTokenSource(TimeSpan.FromMinutes(3));

            await session.StartAsync(cts.Token);
            Assert.NotEmpty(session.SlashCommands);

            await session.SendAsync(
                new UserTurn("Create a file called hello.txt containing the single word: hi"),
                cts.Token);

            var sawApproval = false;
            var sawFileChange = false;
            TurnCompleted? turn = null;
            var seen = new List<string>();

            await foreach (var e in session.Events.ReadAllAsync(cts.Token))
            {
                seen.Add(e.GetType().Name);

                switch (e)
                {
                    case ApprovalRequested approval:
                        sawApproval = true;
                        Assert.Equal("Write", approval.ToolName);
                        await session.RespondToApprovalAsync(
                            approval.RequestId, ApprovalDecision.Allow(), cts.Token);
                        break;

                    case ToolCallCompleted { FileChange: { } change }:
                        sawFileChange = true;
                        Assert.Equal(FileChangeKind.Create, change.Kind);
                        break;

                    case TurnCompleted completed:
                        turn = completed;
                        break;
                }

                if (turn is not null)
                {
                    break;
                }
            }

            var trace = string.Join(", ", seen);
            Assert.True(sawApproval, $"The CLI never asked the host for permission. Saw: {trace}");
            Assert.True(sawFileChange, $"No file change was reported. Saw: {trace}");
            Assert.NotNull(turn);
            Assert.False(turn!.IsError);
            Assert.NotNull(session.SessionId);

            var written = Path.Combine(work.FullName, "hello.txt");
            Assert.True(File.Exists(written), $"Expected {written} to exist.");
            Assert.Equal("hi", File.ReadAllText(written).Trim());
        }
        finally
        {
            try { work.Delete(recursive: true); } catch (IOException) { }
        }
    }

    [Fact]
    public async Task Denying_an_approval_stops_the_write()
    {
        if (!Enabled)
        {
            return;
        }

        var work = Directory.CreateTempSubdirectory("codale-live-deny-");
        try
        {
            await using var session = new ClaudeAgentSession(new ClaudeSessionOptions
            {
                WorkingDirectory = work.FullName,
                Model = "haiku",
                PermissionMode = "manual",
                SessionId = Guid.NewGuid(),
                ExtraArguments = ["--tools", "Write"],
            });

            using var cts = new CancellationTokenSource(TimeSpan.FromMinutes(3));
            await session.StartAsync(cts.Token);
            await session.SendAsync(new UserTurn("Create a file called nope.txt containing: x"), cts.Token);

            await foreach (var e in session.Events.ReadAllAsync(cts.Token))
            {
                if (e is ApprovalRequested approval)
                {
                    await session.RespondToApprovalAsync(
                        approval.RequestId,
                        ApprovalDecision.Deny("The user declined this write."),
                        cts.Token);
                }

                if (e is TurnCompleted)
                {
                    break;
                }
            }

            Assert.False(
                File.Exists(Path.Combine(work.FullName, "nope.txt")),
                "A denied approval must not write the file.");
        }
        finally
        {
            try { work.Delete(recursive: true); } catch (IOException) { }
        }
    }
}
