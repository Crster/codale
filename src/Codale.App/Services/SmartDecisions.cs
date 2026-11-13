using System.Text.Json;
using Codale.Core.Features;
using Codale.Core.Helper;

namespace Codale.App.Services;

/// <summary>One question for a decision model.</summary>
public sealed record SmartQuestion(string Type, string Instructions, object? Criteria = null)
{
    /// <summary>Pick one option; <paramref name="options"/> maps each option to what it means.</summary>
    public static SmartQuestion Choice(string instructions, IReadOnlyDictionary<string, string> options) => new("choice", instructions, options);

    /// <summary>A yes/no question, answered with the probability of yes.</summary>
    public static SmartQuestion YesNo(string instructions) => new("noul", instructions);

    /// <summary>A level on a scale; <paramref name="levels"/> lowest first.</summary>
    public static SmartQuestion Score(string instructions, IReadOnlyList<string> levels) => new("score", instructions, levels);

    public object ToJson() => Criteria is null ? new { type = Type, instructions = Instructions } : new { type = Type, instructions = Instructions, criteria = Criteria };
}

/// <summary>A decision model's answer: the picked option, the probability of yes, or the scored level, with its confidence.</summary>
public sealed record SmartDecision(string Type, string? Choice, double? Yes, double? Score,
    IReadOnlyDictionary<string, double> Probabilities, double? Confidence)
{
    /// <summary>The probability the model gave the option it picked (or the top level of a score).</summary>
    public double Certainty => Choice is { } c && Probabilities.TryGetValue(c, out var p) ? p
        : Probabilities.Count > 0 ? Probabilities.Values.Max() : Confidence ?? 0;

    /// <summary>The most probable level of a score question (0-based), or the rounded score.</summary>
    public int? Level => Probabilities.Count > 0 && Probabilities.MaxBy(p => p.Value).Key is var key && int.TryParse(key, out var level) ? level
        : Score is { } s ? (int)Math.Round(s) : null;

    public static SmartDecision? Parse(JsonElement answer)
    {
        if (answer.ValueKind != JsonValueKind.Object || !answer.TryGetProperty("type", out var type)) return null;
        static double? Number(JsonElement e, string name) => e.TryGetProperty(name, out var v) && v.ValueKind == JsonValueKind.Number ? v.GetDouble() : null;
        var probabilities = new Dictionary<string, double>(StringComparer.Ordinal);
        if (answer.TryGetProperty("probabilities", out var p) && p.ValueKind == JsonValueKind.Object)
            foreach (var option in p.EnumerateObject())
                if (option.Value.ValueKind == JsonValueKind.Number) probabilities[option.Name] = option.Value.GetDouble();
        return type.GetString() switch
        {
            "choice" when answer.TryGetProperty("choice", out var c) && c.ValueKind == JsonValueKind.String =>
                new("choice", c.GetString(), null, null, probabilities, Number(answer, "confidence")),
            "noul" when Number(answer, "noul") is { } yes => new("noul", null, yes, null, probabilities, Number(answer, "confidence")),
            "score" when Number(answer, "score") is { } score => new("score", null, null, score, probabilities, Number(answer, "confidence")),
            _ => null, // a refusal, or a type this client does not ask
        };
    }
}

/// <summary>
/// The questions Codale asks the Decision model, and how the answers are read. Pure: the caller sends them
/// (<see cref="SmartProviderClient.DecideAsync"/>) and keeps the app's own guards (local routing rules, the shell risk floor).
/// </summary>
public static class SmartDecisions
{
    /// <summary>A prompt moves to another session only when the model is at least this sure; a wrong move interrupts the user.</summary>
    public const double RedirectCertainty = 0.6;

    public const string Current = "current";
    public const string New = "new";
    private const int MaxSessionLine = 160;

    public static readonly IReadOnlyDictionary<string, string> Intents = new Dictionary<string, string>
    {
        ["ask"] = "the user only wants an answer and nothing should change: how or why something works, where it is, what a value means; explain, review, compare, summarize; a how-do-I question",
        ["plan"] = "the agent should propose a plan before changing anything: a new feature or screen described in a line, a migration, rewrite or restructure, work across the whole project, a request too vague to act on, or the user asks for a plan or design first",
        ["act"] = "everything else: a bounded change to something that exists (fix, rename, move, restyle, remove, run, commit), a bug report, a follow-up, an approval such as yes or go ahead, or a question mixed with a fix",
    };

    public static SmartQuestion IntentQuestion() =>
        SmartQuestion.Choice("Which mode should the coding agent run in for the new message? When unsure between plan and act, or ask and act, choose act.", Intents);

    public static RouteIntent ToIntent(string? choice) => choice switch { "ask" => RouteIntent.Ask, "plan" => RouteIntent.Plan, _ => RouteIntent.Act };

    /// <summary>
    /// A decision model this sure of the mode is taken as is; below it the local rules' guards apply (an ask must read as
    /// a question, a plan needs substance). Measured: pplx-decider-flash was right on 42 of 42 labelled prompts.
    /// </summary>
    public const double SureIntent = 0.9;

    /// <summary>The turn's mode from the model's answer, checked against the local rules unless the model is sure.</summary>
    public static RouteDecision Route(RouteDecision local, SmartDecision? answer) => answer?.Choice is not { } choice ? local
        : local.Confident ? local
        : answer.Certainty >= SureIntent ? local with { Intent = ToIntent(choice), Confident = true }
        : MessageRouting.Reconcile(local, ToIntent(choice));

    /// <summary>Which of the project's sessions the message continues: the current one, another listed one (by id), or a new one.</summary>
    public static SmartQuestion SessionQuestion(IReadOnlyList<FeatureInfo> sessions, string currentId)
    {
        var options = new Dictionary<string, string>(StringComparer.Ordinal)
        {
            [Current] = "it continues, fixes, extends or gives feedback on the current session's work, or on the agent's last reply; also any go-ahead, thanks or general question",
        };
        foreach (var session in sessions.Where(s => s.Id != currentId))
            options[session.Id] = "it is work on this other session's area: " + Line(session);
        options[New] = "project work on an area none of the sessions covers";
        return SmartQuestion.Choice("Which session does the new message belong to? A session is a big area of the project. " +
            "Fixing, improving or restyling an area belongs to that area's session. Unsure: current.", options);
    }

    /// <summary>What the model reads: the sessions are in the question; this is the conversation it judges.</summary>
    public static object State(string message, FeatureInfo? current, string? lastReply) => new
    {
        current_session = current is null ? null : Line(current),
        last_agent_reply = lastReply is { Length: > 0 } ? Clip(lastReply, 800) : null,
        new_message = Clip(message, 4000),
    };

    /// <summary>The move the answer asks for, or null to stay: unknown ids, low certainty and the current session never move.</summary>
    public static (string? FeatureId, bool New)? Redirect(SmartDecision? answer, IReadOnlyList<FeatureInfo> sessions, string currentId)
    {
        if (answer?.Choice is not { } choice || answer.Certainty < RedirectCertainty || choice == Current || choice == currentId) return null;
        if (choice == New) return (null, true);
        return sessions.Any(s => s.Id == choice) ? (choice, false) : null;
    }

    public static readonly IReadOnlyList<string> RiskLevels =
    [
        "safe: only reads, lists or shows information",
        "caution: changes files inside the project, builds, tests, installs packages, commits or creates branches",
        "danger: deletes or overwrites data recursively or outside the project, discards work (reset --hard, clean -fdx), force-pushes or rewrites shared history, formats disks, changes system or security settings, or downloads and runs a script",
    ];

    public static SmartQuestion RiskQuestion() =>
        SmartQuestion.Score("How risky is it to run this shell command that a coding agent wants to run?", RiskLevels);

    public static string? ToRisk(SmartDecision? answer) => answer?.Level switch { 0 => "safe", 1 => "caution", 2 => "danger", _ => null };

    private static string Line(FeatureInfo f)
    {
        var detail = f.Summary.Length > 0 ? f.Summary.Split('\n')[0] : f.Description;
        return Clip(detail.Length > 0 ? $"{f.Name} - {detail}" : f.Name, MaxSessionLine);
    }

    private static string Clip(string text, int max)
    {
        text = text.ReplaceLineEndings(" ").Trim();
        return text.Length <= max ? text : text[..max] + "…";
    }
}
