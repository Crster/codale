namespace Codale.Core.Helper;

/// <summary>Independent dimensions of a fresh Auto turn. A question can also change topic or state a preference.</summary>
public sealed record AutoAssessment
{
    public string Topic { get; init; } = "current";
    public string Kind { get; init; } = "action";
    public bool Hard { get; init; }
    public bool NeedsPlan { get; init; }
    public bool Unclear { get; init; }
    public string Configuration { get; init; } = "none";
    public double Confidence { get; init; }
    public string Source { get; init; } = "Local";
    public IReadOnlyDictionary<string, string> FieldSources { get; init; } = new Dictionary<string, string>();
    /// <summary>The message opens a new topic, or the chat has no earlier messages.</summary>
    public bool FreshTopic { get; init; }
    /// <summary>
    /// The planner is an expensive model in a separate session, so only a fresh topic that needs a plan and is
    /// unclear or hard goes to it. Everything else acts in the main chat, which has the conversation.
    /// </summary>
    public RouteIntent Intent => Kind == "question" ? RouteIntent.Ask
        : (FreshTopic || Topic == "new") && (LocalPlan || NeedsPlan && (Unclear || Hard)) ? RouteIntent.Plan : RouteIntent.Act;

    /// <summary>
    /// The local rules are sure the request is big or new ("create a snake game", a rewrite): a model that
    /// answers "not hard" cannot talk a fresh topic out of its plan.
    /// </summary>
    public bool LocalPlan { get; init; }

    public static AutoAssessment Local(string text)
    {
        var route = MessageRouting.Assess(text);
        var preference = System.Text.RegularExpressions.Regex.IsMatch(text,
            @"\b(remember|i prefer|always use|never use|from now on)\b", System.Text.RegularExpressions.RegexOptions.IgnoreCase);
        var configuration = System.Text.RegularExpressions.Regex.IsMatch(text,
            @"\b(configure|configuration|settings?|default model|default tier)\b", System.Text.RegularExpressions.RegexOptions.IgnoreCase);
        return new()
        {
            Kind = route.Intent == RouteIntent.Ask ? "question" : "action",
            NeedsPlan = route.Intent == RouteIntent.Plan,
            LocalPlan = route is { Intent: RouteIntent.Plan, Confident: true },
            Hard = route.Intent == RouteIntent.Plan && text.Length > 120,
            Unclear = text.Trim().ToLowerInvariant() is "make it better" or "fix it" or "do it" or "make the app better",
            Configuration = preference && configuration ? "both" : preference ? "preference" : configuration ? "configuration" : "none",
        };
    }
}
