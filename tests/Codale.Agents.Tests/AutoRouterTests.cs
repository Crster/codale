using System.Text.Json;
using Codale.Agents.Providers;
using Codale.Core.Helper;

namespace Codale.Agents.Tests;

public sealed class AutoRouterTests
{
    private static SmartDecision Choice(string choice, double confidence = .95) =>
        new("choice", choice, null, null, new Dictionary<string, double>(), confidence);

    [Fact]
    public async Task Decision_is_asked_even_when_local_rules_are_confident_and_valid_answers_win()
    {
        var calls = 0;
        var assessment = await AutoRouter.AssessAsync("rename Save to Apply", new { }, AutoDecisions.Questions(),
            (_, questions, _) =>
            {
                calls++;
                Assert.Equal(5, questions.Count);
                return Task.FromResult<IReadOnlyDictionary<string, SmartDecision>?>(new Dictionary<string, SmartDecision>
                {
                    ["kind"] = Choice("mixed"), ["hard"] = Choice("yes"), ["plan"] = Choice("no"),
                    ["unclear"] = Choice("no"), ["configuration"] = Choice("both"),
                });
            }, (_, _, _) => throw new Exception("General must not run"), default);
        Assert.Equal(1, calls);
        Assert.Equal("mixed", assessment.Kind);
        Assert.Equal(RouteIntent.Act, assessment.Intent);
        Assert.Equal("both", assessment.Configuration);
        Assert.Equal("Decision", assessment.Source);
    }

    [Fact]
    public async Task Partial_answers_fall_back_per_field_without_overwriting_valid_decision_fields()
    {
        var questions = AutoDecisions.Questions();
        questions["session"] = SmartQuestion.Choice("topic", new Dictionary<string, string> { ["current"] = "here", ["new"] = "elsewhere" });
        var assessment = await AutoRouter.AssessAsync("make it better", new { }, questions,
            (_, _, _) => Task.FromResult<IReadOnlyDictionary<string, SmartDecision>?>(new Dictionary<string, SmartDecision>
            { ["kind"] = Choice("question"), ["session"] = Choice("current"), ["hard"] = Choice("no", .2) }),
            (_, input, _) =>
            {
                using var parsed = JsonDocument.Parse(input);
                var pending = parsed.RootElement.GetProperty("questions");
                Assert.False(pending.TryGetProperty("kind", out _));
                Assert.False(pending.TryGetProperty("session", out _));
                return Task.FromResult<string?>("{\"hard\":{\"choice\":\"yes\",\"confidence\":0.9},\"unclear\":{\"choice\":\"invalid\",\"confidence\":1}}");
            }, default);
        // "make it better" is not a question: the model's "question" is overruled.
        Assert.Equal(RouteIntent.Act, assessment.Intent);
        Assert.True(assessment.Hard);
        Assert.True(assessment.Unclear);
        Assert.Equal("Decision", assessment.FieldSources["session"]);
        Assert.Equal("General", assessment.FieldSources["hard"]);
        Assert.Equal("Decision + General + Local", assessment.Source);
    }

    [Fact]
    public async Task Provider_errors_and_malformed_general_output_fall_back_locally()
    {
        var assessment = await AutoRouter.AssessAsync("fix the login redirect", new { }, AutoDecisions.Questions(),
            (_, _, _) => throw new IOException("offline"), (_, _, _) => Task.FromResult<string?>("not json"), default);
        Assert.Equal(RouteIntent.Act, assessment.Intent);
        Assert.Equal("Local", assessment.Source);
    }

    [Fact]
    public async Task Model_timeout_falls_through_but_user_cancellation_does_not()
    {
        var assessment = await AutoRouter.AssessAsync("why?", new { }, AutoDecisions.Questions(),
            (_, _, _) => throw new OperationCanceledException(), (_, _, _) => Task.FromResult<string?>(null), default);
        Assert.Equal(RouteIntent.Ask, assessment.Intent);
        using var cancelled = new CancellationTokenSource();
        cancelled.Cancel();
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => AutoRouter.AssessAsync("why?", new { }, AutoDecisions.Questions(),
            (_, _, _) => Task.FromResult<IReadOnlyDictionary<string, SmartDecision>?>(null), (_, _, _) => Task.FromResult<string?>(null), cancelled.Token));
    }

    [Fact]
    public async Task A_backend_that_ignores_cancellation_cannot_hold_routing_open()
    {
        var never = new TaskCompletionSource<IReadOnlyDictionary<string, SmartDecision>?>();
        var watch = System.Diagnostics.Stopwatch.StartNew();
        var assessment = await AutoRouter.AssessAsync("why?", new { }, AutoDecisions.Questions(),
            (_, _, _) => never.Task, (_, _, _) => Task.FromResult<string?>(null), default);
        Assert.Equal("Local", assessment.Source);
        Assert.InRange(watch.Elapsed.TotalSeconds, 3.5, 7);
    }

    [Theory]
    [InlineData("can we implement login page", RouteIntent.Act)]
    [InlineData("can you add a logout button to the header?", RouteIntent.Act)]
    [InlineData("could we fix the login redirect", RouteIntent.Act)]
    [InlineData("Handoff: the settings page is half done.\nNext steps:\n- wire the Save button\n- add validation\nAny questions?", RouteIntent.Act)]
    [InlineData("why does the login page redirect twice?", RouteIntent.Ask)]
    [InlineData("how does the session store work?", RouteIntent.Ask)]
    public async Task A_model_question_must_read_as_a_pure_question(string text, RouteIntent expected)
    {
        var assessment = await AutoRouter.AssessAsync(text, new { }, AutoDecisions.Questions(),
            (_, _, _) => Task.FromResult<IReadOnlyDictionary<string, SmartDecision>?>(new Dictionary<string, SmartDecision>
            {
                ["kind"] = Choice("question"), ["hard"] = Choice("no"), ["plan"] = Choice("no"),
                ["unclear"] = Choice("no"), ["configuration"] = Choice("none"),
            }), (_, _, _) => Task.FromResult<string?>(null), default);
        Assert.Equal(expected, assessment.Intent);
    }

    [Theory]
    [InlineData("question", true, true, true, true, RouteIntent.Ask)]
    [InlineData("mixed", true, false, false, false, RouteIntent.Act)]
    [InlineData("action", true, true, false, false, RouteIntent.Act)]
    [InlineData("action", true, false, true, true, RouteIntent.Act)]
    [InlineData("action", true, true, true, false, RouteIntent.Plan)]
    [InlineData("action", true, true, false, true, RouteIntent.Plan)]
    [InlineData("action", false, true, true, true, RouteIntent.Act)]
    public void Only_a_fresh_topic_that_needs_a_plan_and_is_hard_or_unclear_plans(string kind, bool fresh, bool plan, bool hard, bool unclear, RouteIntent expected) =>
        Assert.Equal(expected, new AutoAssessment { Kind = kind, FreshTopic = fresh, Hard = hard, NeedsPlan = plan, Unclear = unclear }.Intent);

    [Fact]
    public void A_new_topic_counts_as_fresh() =>
        Assert.Equal(RouteIntent.Plan, new AutoAssessment { Topic = "new", NeedsPlan = true, Hard = true }.Intent);

    [Theory]
    [InlineData("I prefer a compact layout", "preference")]
    [InlineData("configure the default model", "configuration")]
    [InlineData("Always use the default model", "both")]
    public void Local_configuration_and_preferences_are_independent_of_action_routing(string text, string expected) =>
        Assert.Equal(expected, AutoAssessment.Local(text).Configuration);
}
