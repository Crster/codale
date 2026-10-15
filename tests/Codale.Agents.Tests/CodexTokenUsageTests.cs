using System.Text.Json;

using Codale.Agents.Codex;
using Codale.Core.Agents;

namespace Codale.Agents.Tests;

/// <summary>thread/tokenUsage/updated feeds the context meter - the only source a BYOK endpoint has.</summary>
public sealed class CodexTokenUsageTests
{
    private static JsonElement Json(string json) => JsonDocument.Parse(json).RootElement.Clone();

    private static List<AgentEvent> Drain(CodexAgentSession session)
    {
        var events = new List<AgentEvent>();
        while (session.Events.TryRead(out var e))
        {
            events.Add(e);
        }

        return events;
    }

    [Fact]
    public async Task Token_usage_reports_the_last_calls_context_and_the_clis_window()
    {
        var session = new CodexAgentSession(new CodexSessionOptions { WorkingDirectory = "." });
        session.AdoptThread("main");

        // As codex 0.162 sends it for a custom (Z.ai) provider.
        await session.HandleNotificationAsync("thread/tokenUsage/updated", Json("""
            {"threadId":"main","turnId":"t1","tokenUsage":{
              "total":{"totalTokens":40000,"inputTokens":39000,"cachedInputTokens":20000,"outputTokens":1000,"reasoningOutputTokens":26},
              "last":{"totalTokens":27649,"inputTokens":27549,"cachedInputTokens":7000,"outputTokens":100,"reasoningOutputTokens":26},
              "modelContextWindow":258400}}
            """));

        var events = Drain(session);
        var usage = Assert.Single(events.OfType<UsageUpdated>());
        Assert.Equal(27549, usage.Usage.ContextTokens); // cached is part of the input, not added on top
        Assert.Equal(7000, usage.Usage.CacheReadInputTokens);
        Assert.Equal(258400, usage.ContextWindow);

        var running = Assert.Single(events.OfType<SessionTokensUpdated>());
        Assert.Equal(40000, running.Total);
    }
}