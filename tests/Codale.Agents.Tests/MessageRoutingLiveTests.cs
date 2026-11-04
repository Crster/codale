using System.Diagnostics;
using Codale.Core.Helper;
using Xunit.Abstractions;

namespace Codale.Agents.Tests;

// Opt in separately from tests that ask agents to edit files.
public sealed class MessageRoutingLiveTests(ITestOutputHelper output)
{
    [Theory]
    [InlineData("Claude")]
    [InlineData("Codex")]
    public async Task Real_helper_classifies_the_routing_corpus(string provider)
    {
        if (Environment.GetEnvironmentVariable("CODALE_ROUTING_LIVE_TESTS") != "1") return;
        using var helper = new HelperModel(provider: () => provider);
        Assert.True(helper.IsAvailable, $"{provider} CLI unavailable");
        (string Message, RouteIntent Expected)[] cases =
        [
            ("the header is ugly", RouteIntent.Plan),
            ("why is login slow? fix the redirect and add tests", RouteIntent.Act),
            ("plan and implement the migration across every service", RouteIntent.Plan),
            ("fix the router but do not edit yet; propose a plan first", RouteIntent.Plan),
            ("compare the two authentication approaches and explain the tradeoffs", RouteIntent.Ask),
            ("can you explain how the topic detector works?", RouteIntent.Ask),
            ("yes, why is it broken?", RouteIntent.Ask),
            ("build a complete authentication system with sessions, roles and tests", RouteIntent.Plan),
            ("add CSV export for invoices", RouteIntent.Plan),
            ("make the dashboard more modern", RouteIntent.Plan),
            ("implement the approved plan", RouteIntent.Act),
        ];
        var failures = new List<string>();
        foreach (var (message, expected) in cases)
        {
            using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(30));
            var timer = Stopwatch.StartNew();
            var call = await helper.CallToolAsync(MessageRouting.SystemPrompt,
                MessageRouting.BuildConversation(message, "The login redirect handler loses its OAuth state. Would you like me to fix it?"),
                [MessageRouting.RouteTool], cts.Token);
            Assert.NotNull(call); // A fallback must not conceal a provider failure.
            var actual = MessageRouting.Interpret(call, message, false).Intent;
            output.WriteLine($"{provider}: {timer.ElapsedMilliseconds}ms {actual} expected {expected}: {message}");
            if (actual != expected) failures.Add($"{message}: expected {expected}, got {actual}");
        }
        Assert.True(failures.Count == 0, string.Join("\n", failures));
    }
}
