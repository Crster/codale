using Codale.Core.Helper;
using Codale.Core.Features;

namespace Codale.Agents.Providers;

/// <summary>The same independent questions are used for Decision and General; local rules only fill missing answers.</summary>
public static class AutoDecisions
{
    public const double MinimumConfidence = 0.6;
    public static Dictionary<string, SmartQuestion> Questions() => new()
    {
        ["kind"] = SmartQuestion.Choice("Classify the user's intent in context. A request phrased as a question ('can you fix X', 'can we implement a login page', " +
            "'could you add Y') is an action, and so is a pasted handoff or task description. When unsure, choose action.", new Dictionary<string, string>
        { ["question"] = "Deliberately asks only for an explanation, investigation or advice, and explicitly wants nothing changed", ["action"] = "Requests a change or execution", ["mixed"] = "Both explanation and requested changes" }),
        ["hard"] = Binary("Is this a large, risky change: a new subsystem, a rewrite or migration, or many interacting changes across several files? " +
            "Bug fixes, tweaks, follow-ups and changes to a known area are not hard, even when they need some investigation."),
        ["plan"] = Binary("Would implementing without an agreed plan first be risky or wasteful? Answer yes only for broad or open-ended work " +
            "with real design choices, or when the user explicitly asks for a plan. Most requests: no."),
        ["unclear"] = Binary("Are material instructions missing or ambiguous after considering the conversation? Approvals and follow-ups inherit context."),
        ["configuration"] = SmartQuestion.Choice("Does the user request configuration changes or state a durable preference?", new Dictionary<string, string>
        { ["none"] = "Neither", ["configuration"] = "Explicit configuration request", ["preference"] = "Durable user preference", ["both"] = "Both" }),
    };

    private static SmartQuestion Binary(string question) => SmartQuestion.Choice(question,
        new Dictionary<string, string> { ["yes"] = "Yes", ["no"] = "No" });

    public static SmartQuestion SessionQuestion(IReadOnlyList<FeatureInfo> sessions, string currentId)
    {
        var options = new Dictionary<string, string>((IReadOnlyDictionary<string, string>)SmartDecisions.SessionQuestion(sessions, currentId).Criteria!);
        options[SmartDecisions.Current] = "Continues the current topic, feedback, an approval, or a follow-up question about the last reply";
        options[SmartDecisions.New] = "A different topic, question or action, not covered by any listed session";
        return SmartQuestion.Choice("Where does this message belong? Consider topic independently of whether it is a question or action. " +
            "Use the current conversation for contextual follow-ups; suggest another session only when its subject clearly matches. Unsure: current.", options);
    }

    public static AutoAssessment Merge(AutoAssessment local, IReadOnlyDictionary<string, SmartDecision>? answers,
        IReadOnlyDictionary<string, SmartQuestion> questions, string source, out HashSet<string> resolved)
    {
        resolved = [];
        if (answers is null) return local;
        var result = local;
        var confidences = new List<double>();
        foreach (var (key, question) in questions)
        {
            if (!answers.TryGetValue(key, out var answer) || answer.Type != "choice" || answer.Choice is not { } choice ||
                !double.IsFinite(answer.Certainty) || answer.Certainty is < MinimumConfidence or > 1 || question.Criteria is not IReadOnlyDictionary<string, string> choices || !choices.ContainsKey(choice)) continue;
            resolved.Add(key);
            confidences.Add(answer.Certainty);
            result = key switch
            {
                "kind" => result with { Kind = choice }, "hard" => result with { Hard = choice == "yes" },
                "plan" => result with { NeedsPlan = choice == "yes" }, "unclear" => result with { Unclear = choice == "yes" },
                "configuration" => result with { Configuration = choice }, "session" => result with { Topic = choice }, _ => result,
            };
        }
        var fieldSources = new Dictionary<string, string>(local.FieldSources);
        foreach (var key in resolved) fieldSources[key] = source;
        return resolved.Count == 0 ? result : result with { Source = source, FieldSources = fieldSources,
            Confidence = local.FieldSources.Count == 0 ? confidences.Min() : Math.Min(local.Confidence, confidences.Min()) };
    }
}
