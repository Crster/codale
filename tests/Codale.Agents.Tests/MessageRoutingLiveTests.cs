using System.Diagnostics;
using Codale.Agents.OpenRouter;
using Codale.Core.Helper;
using Xunit.Abstractions;

namespace Codale.Agents.Tests;

// Opt in: needs an OpenRouter API key.
// CODALE_OPENROUTER_LIVE_TESTS=1, optionally CODALE_OPENROUTER_MODEL (with CODALE_OPENROUTER_KEY).
public sealed class MessageRoutingLiveTests(ITestOutputHelper output)
{
    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task Real_decision_model_classifies_the_routing_corpus(bool thinking)
    {
        if (Environment.GetEnvironmentVariable("CODALE_OPENROUTER_LIVE_TESTS") != "1") return;
        var model = Environment.GetEnvironmentVariable("CODALE_OPENROUTER_MODEL") is { Length: > 0 } m ? m : "google/gemini-2.5-flash-lite";
        var helper = new OpenRouterHelperModel(new OpenRouterClient(Environment.GetEnvironmentVariable("CODALE_OPENROUTER_KEY")), model, thinking);
        var provider = $"{model}{(thinking ? " (thinking)" : "")}";
        (string Message, RouteIntent Expected)[] cases =
        [
            ("the header is ugly", RouteIntent.Act),
            ("why is login slow? fix the redirect and add tests", RouteIntent.Act),
            ("plan and implement the migration across every service", RouteIntent.Plan),
            ("fix the router but do not edit yet; propose a plan first", RouteIntent.Plan),
            ("compare the two authentication approaches and explain the tradeoffs", RouteIntent.Ask),
            ("can you explain how the topic detector works?", RouteIntent.Ask),
            ("yes, why is it broken?", RouteIntent.Ask),
            ("build a complete authentication system with sessions, roles and tests", RouteIntent.Plan),
            ("add CSV export for invoices", RouteIntent.Act),
            ("add new toolbar in the milestone row for attaching note, it will reflect as a comment with #Task as a reference anchor", RouteIntent.Act),
            ("this wide spacing is annoying. can we redesign it", RouteIntent.Act),
            ("please arrange the permissions correctly according to menu and where the page belongs", RouteIntent.Act),
            ("make it better", RouteIntent.Plan),
            ("implement the approved plan", RouteIntent.Act),
        ];
        var failures = new List<string>();
        foreach (var (message, expected) in cases)
        {
            using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(60));
            var timer = Stopwatch.StartNew();
            var call = await helper.CallToolAsync(MessageRouting.SystemPrompt,
                MessageRouting.BuildConversation(message, "The login redirect handler loses its OAuth state. Would you like me to fix it?"),
                [MessageRouting.RouteTool], cts.Token);
            Assert.NotNull(call); // A fallback must not conceal a provider failure.
            var actual = MessageRouting.Interpret(call, message).Intent;
            output.WriteLine($"{provider}: {timer.ElapsedMilliseconds}ms {actual} expected {expected}: {message}");
            if (actual != expected) failures.Add($"{message}: expected {expected}, got {actual}");
        }
        Assert.True(failures.Count == 0, string.Join("\n", failures));
    }
}
