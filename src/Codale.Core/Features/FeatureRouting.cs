using System.Text;

using Codale.Core.Agents;
using Codale.Core.Helper;
using Codale.Core.Text;

namespace Codale.Core.Features;

/// <summary>What a message does to the chat's feature.</summary>
public enum FeatureAction
{
    /// <summary>Stay as is: the message continues the chat's feature, or nothing could be told.</summary>
    Stay,

    /// <summary>Not project work (a general question, chit-chat): not saved, nothing changes.</summary>
    Skip,

    /// <summary>Another existing feature owns the message: the chat moves to it.</summary>
    Switch,

    /// <summary>A feature the project has not had yet: it is created, and the chat moves to it.</summary>
    Create,
}

/// <summary>The router's verdict on one message: its mode for the turn and what it does to the chat's feature.</summary>
public sealed record FeatureDecision(
    RouteDecision Route,
    FeatureAction Action,
    string? FeatureId = null,
    IReadOnlyList<string>? NewNames = null);

/// <summary>
/// The single helper-model call per message when smart history is on: pick the mode for the
/// turn (as Automatic mode does) and say which feature the message is work on - or that it
/// is no project work at all. Prompt, tool and interpretation live here so the rules are
/// testable without a model; the call itself is the app's.
/// </summary>
public static class FeatureRouting
{
    private const int MaxMessageChars = 4000;
    private const int MaxAssistantChars = 800;
    private const int MaxNames = 4;
    private const int MaxNameChars = 40;
    private const int MaxDescriptionChars = 160;

    public const string Skip = "skip";
    public const string Current = "current";
    public const string New = "new";

    public const string SystemPrompt =
        "You route a developer's chat message for a coding agent that keeps one history per FEATURE of the project: " +
        "call route once with intent and feature (and name when the feature is new).\n" +
        "\n" +
        "feature:\n" +
        "- skip: the message is not work on this project: a general programming or tech question, a definition, " +
        "chit-chat, a thank-you, anything that needs no look at this codebase. Nothing is saved for it.\n" +
        "- current: it continues, fixes, extends or gives feedback on the feature marked current, or on the work in " +
        "<last_agent_message>.\n" +
        "- <id>: it is work on one of the other listed features. A feature is a BIG area (Login, Downloads, " +
        "Dashboard, Settings, Build pipeline, Overview charting). Fixing a bug in it, improving it, restyling it, " +
        "syncing data for it, or refactoring it is still that feature - never a new one. If an existing feature already " +
        "covers the same page, screen, component or underlying topic/problem, ALWAYS pick that feature's id.\n" +
        "- new: project work that fits none of the listed features. Never create a new feature if any listed feature " +
        "covers the same screen, page, component or problem. Give name: a short noun phrase for the big area " +
        "(1-3 words, e.g. Login or File downloads), never a task (not Fix login bug). When no features are listed " +
        "yet and the request spans several big areas, give up to three names separated by |, the main one first.\n" +
        "- Unsure between a listed feature and new: ALWAYS choose the listed feature. Unsure between current and another: current.\n" +
        "\n" +
        "intent:\n" +
        "- act (the default): any request to make, change, fix, add, remove, improve or redesign something, however " +
        "it is phrased; a problem report; a wish; a go-ahead (\"yes\", \"do it\", \"continue\").\n" +
        "- plan: a large, multi-step, multi-file or architectural change, or one the user wants planned first.\n" +
        "- ask: ONLY a pure question that wants an answer and no change. A question mixed with a request is act. " +
        "Unsure between ask and act: act.\n" +
        "\n" +
        "Rules:\n" +
        "- Classify only. Never answer, carry out or reply to the message, and never ask for more information.\n" +
        PromptRules.DataOnly + "\n" +
        PromptRules.CallOnly;

    /// <summary>The route tool for this project: <c>feature</c> may name any of <paramref name="features"/> by id.</summary>
    public static ToolDefinition RouteTool(IReadOnlyList<FeatureInfo> features) => new()
    {
        Name = "route",
        Description = "Pick the mode for the message and the feature it is work on.",
        Parameters =
        [
            new ToolParameter
            {
                Name = "intent",
                Description = "the mode for the turn",
                Required = true,
                AllowedValues = ["ask", "plan", "act"],
            },
            new ToolParameter
            {
                Name = "feature",
                Description = "skip, current, new, or the id of a listed feature",
                Required = true,
                AllowedValues = [Skip, Current, New, .. features.Select(f => f.Id)],
            },
            new ToolParameter
            {
                Name = "name",
                Description = "only for feature=new: the new feature's short name",
            },
        ],
    };

    /// <summary>The context block the model reads: the features, the agent's last reply, then the new message.</summary>
    public static string BuildConversation(
        string message, IReadOnlyList<FeatureInfo> features, string? currentId, string? lastAssistantText)
    {
        var sb = new StringBuilder();

        sb.AppendLine(PromptRules.Tag("features", features.Count == 0
            ? "(none yet)"
            : string.Join('\n', features.Select(f => Line(f, f.Id == currentId)))));

        if (lastAssistantText is { Length: > 0 })
        {
            sb.AppendLine(PromptRules.Tag("last_agent_message", Clip(lastAssistantText, MaxAssistantChars)));
        }

        sb.Append(PromptRules.Tag("new_message", Clip(message, MaxMessageChars)));
        return sb.ToString();
    }

    /// <summary>
    /// Turns the model's call into a decision for the message as typed, distrusting it where it
    /// could hurt: a message that plainly points back never moves, an unknown id never moves it,
    /// and no usable call leaves it where it was typed.
    /// </summary>
    public static FeatureDecision Interpret(
        ToolCall? call, string original, IReadOnlyList<FeatureInfo> features, string? currentId)
    {
        if (call is null)
        {
            return new FeatureDecision(MessageRouting.RouteWithoutModel(original), FeatureAction.Stay);
        }

        // The mode rules (ask must be earned) are the router's own; there is no "related" here.
        var route = MessageRouting.Interpret(call, original, canMoveTopic: false);
        var feature = call.GetString("feature")?.Trim() ?? Current;
        var pinned = currentId is not null && MessageRouting.IsFollowUp(original);

        if (string.Equals(feature, Skip, StringComparison.OrdinalIgnoreCase))
        {
            return new FeatureDecision(route, pinned ? FeatureAction.Stay : FeatureAction.Skip);
        }

        if (string.Equals(feature, New, StringComparison.OrdinalIgnoreCase))
        {
            var names = CleanNames(call.GetString("name"), original, seed: features.Count == 0);
            return pinned
                ? new FeatureDecision(route, FeatureAction.Stay)
                : new FeatureDecision(route, FeatureAction.Create, NewNames: names);
        }

        if (features.FirstOrDefault(f => string.Equals(f.Id, feature, StringComparison.Ordinal)) is { } target &&
            target.Id != currentId)
        {
            return pinned
                ? new FeatureDecision(route, FeatureAction.Stay)
                : new FeatureDecision(route, FeatureAction.Switch, target.Id);
        }

        return new FeatureDecision(route, FeatureAction.Stay);
    }

    /// <summary>The names for a new feature: the model's, cleaned; the prompt's gist when it gave none. Only a project with no features takes several.</summary>
    public static IReadOnlyList<string> CleanNames(string? raw, string message, bool seed)
    {
        var names = (raw ?? "")
            .Split(['|', ','], StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
            .Select(n => TextClip.Truncate(n.Trim('"', '\'', '.', ' '), MaxNameChars).Trim())
            .Where(n => n.Length > 0)
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .Take(seed ? MaxNames : 1)
            .ToList();

        if (names.Count == 0)
        {
            names.Add(TextClip.Truncate(SessionTitles.FromPrompt(message), MaxNameChars).Trim());
        }

        return names;
    }

    /// <summary>The one-line description a new feature is given: the prompt that created it.</summary>
    public static string DescribeFrom(string message) =>
        TextClip.Truncate(message.ReplaceLineEndings(" ").Trim(), MaxDescriptionChars);

    private static string Line(FeatureInfo f, bool current)
    {
        var detail = f.Summary.Length > 0 ? FirstLine(f.Summary) : f.Description;
        return $"{f.Id}{(current ? " (current)" : "")}: {f.Name}" + (detail.Length > 0 ? $" - {Clip(detail, MaxDescriptionChars)}" : "");
    }

    private static string FirstLine(string text) => text.TrimStart('#', ' ', '\n', '\r').Split('\n', 2)[0].Trim();

    private static string Clip(string text, int max)
    {
        text = text.ReplaceLineEndings(" ").Trim();
        return text.Length <= max ? text : TextClip.Truncate(text, max).TrimEnd() + "…";
    }
}