using System.Text.Json;
using Codale.Core.Helper;
using Xunit.Abstractions;

namespace Codale.Core.Tests;

/// <summary>
/// Scores Auto mode's local routing against labelled prompts. <c>Routing/corpus.json</c> is generic and checked in.
/// Set CODALE_ROUTING_CORPUS to a JSON file of <c>{ "text", "label" }</c> rows (for example prompts taken from your own
/// history) to score that as well; it is read, never copied. The score that matters most is how often a plain change
/// is sent to plan or ask, because every such mistake interrupts the user.
/// </summary>
public sealed class RoutingCorpusTests(ITestOutputHelper output)
{
    private sealed record Row(string Text, string Label);

    private static readonly JsonSerializerOptions Options = new() { PropertyNameCaseInsensitive = true };

    private static RouteIntent Parse(string label) => label switch
    {
        "plan" => RouteIntent.Plan,
        "ask" => RouteIntent.Ask,
        _ => RouteIntent.Act,
    };

    private static IReadOnlyList<Row> Load(string path) => JsonSerializer.Deserialize<List<Row>>(File.ReadAllText(path), Options)!;

    private static string CorpusPath()
    {
        var root = new DirectoryInfo(AppContext.BaseDirectory);
        while (root is not null && !File.Exists(Path.Combine(root.FullName, "Codale.slnx"))) root = root.Parent;
        Assert.NotNull(root);
        return Path.Combine(root.FullName, "tests", "Codale.Core.Tests", "Routing", "corpus.json");
    }

    private (double Accuracy, double Interrupted, double PlanRecall, double AskRecall) Score(string name, IReadOnlyList<Row> rows)
    {
        var matrix = new Dictionary<(RouteIntent Expected, RouteIntent Actual), int>();
        var misses = new List<string>();
        foreach (var row in rows)
        {
            var expected = Parse(row.Label);
            var actual = MessageRouting.RouteWithoutModel(row.Text).Intent;
            matrix[(expected, actual)] = matrix.GetValueOrDefault((expected, actual)) + 1;
            if (expected != actual) misses.Add($"  want {expected,-4} got {actual,-4} | {row.Text.ReplaceLineEndings(" ")[..Math.Min(row.Text.Length, 120)]}");
        }

        int Total(RouteIntent e) => matrix.Where(m => m.Key.Expected == e).Sum(m => m.Value);
        int Hit(RouteIntent e) => matrix.GetValueOrDefault((e, e));
        var correct = Hit(RouteIntent.Act) + Hit(RouteIntent.Plan) + Hit(RouteIntent.Ask);
        var acts = Total(RouteIntent.Act);
        var interrupted = acts == 0 ? 0 : (acts - Hit(RouteIntent.Act)) / (double)acts;
        double Rate(RouteIntent e) => Total(e) == 0 ? 1 : Hit(e) / (double)Total(e);

        output.WriteLine($"== {name}: {rows.Count} prompts");
        output.WriteLine("expected \\ got    act  plan   ask");
        foreach (var e in new[] { RouteIntent.Act, RouteIntent.Plan, RouteIntent.Ask })
            output.WriteLine($"{e,-14} {matrix.GetValueOrDefault((e, RouteIntent.Act)),5} {matrix.GetValueOrDefault((e, RouteIntent.Plan)),5} {matrix.GetValueOrDefault((e, RouteIntent.Ask)),5}");
        // Messages the local rules would only plan with a model's confirmation: invisible above, where nobody confirms them.
        var weakPlans = rows.Where(r => MessageRouting.Assess(r.Text) is { Intent: RouteIntent.Plan, Confident: false }).ToList();
        output.WriteLine($"unsure plans (a model decides): {weakPlans.Count(r => r.Label == "plan")} of the plan prompts, {weakPlans.Count(r => r.Label == "act")} of the act prompts");
        output.WriteLine($"accuracy={correct / (double)rows.Count:P0}  plain changes interrupted={interrupted:P1}  plan recall={Rate(RouteIntent.Plan):P0}  ask recall={Rate(RouteIntent.Ask):P0}");
        misses.ForEach(output.WriteLine);
        return (correct / (double)rows.Count, interrupted, Rate(RouteIntent.Plan), Rate(RouteIntent.Ask));
    }

    [Fact]
    public void Local_routing_sends_big_or_unclear_work_to_plan_questions_to_ask_and_leaves_changes_alone()
    {
        var score = Score("corpus", Load(CorpusPath()));
        // Floors: raise as routing improves, never lower to make a change pass.
        Assert.True(score.Interrupted <= 0.03, $"plain changes interrupted: {score.Interrupted:P1}");
        Assert.True(score.PlanRecall >= 0.80, $"plan recall: {score.PlanRecall:P0}");
        Assert.True(score.AskRecall >= 0.85, $"ask recall: {score.AskRecall:P0}");
        Assert.True(score.Accuracy >= 0.90, $"accuracy: {score.Accuracy:P0}");
    }

    [Fact]
    public void Your_own_prompts_are_routed_the_way_you_labelled_them()
    {
        var path = Environment.GetEnvironmentVariable("CODALE_ROUTING_CORPUS");
        if (string.IsNullOrWhiteSpace(path)) return;
        var score = Score("your corpus", Load(path));
        Assert.True(score.Interrupted <= 0.05, $"plain changes interrupted: {score.Interrupted:P1}");
    }
}
