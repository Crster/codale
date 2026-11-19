using System.Text.Json;

using Codale.Agents.Codex;
using Codale.Core.Agents;

namespace Codale.Agents.Tests;

public sealed class CodexRetryTests
{
    [Theory]
    [InlineData(2)]
    [InlineData(3)]
    public async Task Reconnect_error_becomes_a_pinned_retry_notice(int attempt)
    {
        var session = CreateSession();
        var notification = JsonSerializer.SerializeToElement(new
        {
            @params = new
            {
                error = new
                {
                    message = $"Reconnecting... {attempt}/5",
                    codexErrorInfo = new { responseStreamDisconnected = new { httpStatusCode = (int?)null } },
                    additionalDetails = "stream disconnected before completion: No such host is known. (os error 11001)"
                },
                willRetry = true,
                threadId = "main",
                turnId = "turn-1"
            }
        });

        await session.HandleNotificationAsync("error", notification);

        Assert.True(session.Events.TryRead(out var e));
        var notice = Assert.IsType<CliInformation>(e);
        Assert.Equal($"Reconnecting... {attempt}/5\nstream disconnected before completion: No such host is known. (os error 11001)", notice.Text);
        Assert.True(notice.IsWarning);
        Assert.True(notice.IsRetry);
        Assert.False(session.Events.TryRead(out _));
    }

    [Theory]
    [InlineData("{\"error\":{\"message\":\"Connection failed\"},\"willRetry\":false}")]
    [InlineData("{\"error\":{\"message\":\"Connection failed\"}}")]
    [InlineData("{\"message\":\"Connection failed\"}")]
    public async Task Final_and_legacy_errors_remain_errors_with_readable_text(string json)
    {
        var session = CreateSession();
        using var doc = JsonDocument.Parse(json);
        await session.HandleNotificationAsync("error", doc.RootElement);

        Assert.True(session.Events.TryRead(out var e));
        Assert.Equal("Connection failed", Assert.IsType<AgentError>(e).Message);
        Assert.False(session.Events.TryRead(out _));
    }

    [Fact]
    public async Task Failed_turn_preserves_the_clis_error_details_for_the_banner()
    {
        var session = CreateSession();
        using var doc = JsonDocument.Parse("""
            {"threadId":"main","turn":{"id":"turn-1","status":"failed",
              "error":{"message":"Usage limit reached","additionalDetails":"Try again later."}}}
            """);
        await session.HandleNotificationAsync("turn/completed", doc.RootElement);

        Assert.True(session.Events.TryRead(out var e));
        var turn = Assert.IsType<TurnCompleted>(e);
        Assert.True(turn.IsError);
        Assert.Equal("Usage limit reached\nTry again later.", Assert.Single(turn.Errors));
        Assert.False(session.Events.TryRead(out _));
    }

    [Fact]
    public async Task Retry_without_message_has_a_readable_fallback()
    {
        var session = CreateSession();
        using var doc = JsonDocument.Parse("""{"willRetry":true}""");
        await session.HandleNotificationAsync("error", doc.RootElement);

        Assert.True(session.Events.TryRead(out var e));
        Assert.Equal("The Codex request failed: retrying.", Assert.IsType<CliInformation>(e).Text);
    }

    private static CodexAgentSession CreateSession()
    {
        var session = new CodexAgentSession(new CodexSessionOptions { WorkingDirectory = "." });
        session.AdoptThread("main");
        return session;
    }
}
