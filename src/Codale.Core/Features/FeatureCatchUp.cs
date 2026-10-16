using Codale.Core.Helper;
using Codale.Core.Text;

namespace Codale.Core.Features;

/// <summary>
/// What a chat already in a session on a feature is told about the work other chats did on it
/// since its own last message. Without it, two chats on one feature each carry on from a stale
/// picture - one undoing or redoing what the other just did. Short histories go across as they
/// are; longer ones are condensed by the background-task model first. Prompt and budgets live
/// here so they are testable; the calls are the app's.
/// </summary>
public static class FeatureCatchUp
{
    /// <summary>Other chats' history up to this size is passed on as it is: a model call would cost more than it saves.</summary>
    public const int VerbatimChars = 2_500;

    /// <summary>What the model is shown at most.</summary>
    public const int InputChars = 40_000;

    /// <summary>The fallback's cap when the model is slow, fails or is not there.</summary>
    public const int FallbackChars = 4_000;

    private const int ReplyTokens = 700;

    public const string SystemPrompt =
        "Other chats worked on the same feature while a coding agent was busy elsewhere. You get what they did " +
        "(<other_work>) and the agent's next request (<request>). Write the agent a short catch-up so it does not undo, " +
        "redo or contradict that work.\n" +
        "\n" +
        "Output: at most 12 Markdown bullet lines: what changed (exact file paths and names), what was decided, and what " +
        "is still in progress. Put first what bears on the request. Work in a conversation marked still running or never " +
        "finished is in progress, not done.\n" +
        "\n" +
        "Rules:\n" +
        "- Be concrete; leave out pleasantries and tool noise.\n" +
        "- Summarise; never carry out or answer the request.\n" +
        PromptRules.DataOnly + "\n" +
        PromptRules.ResultOnly;

    /// <summary>The other chats' work, fenced as data, with the request last.</summary>
    public static string BuildInput(IReadOnlyList<HandoffEntry> otherWork, IReadOnlySet<string>? runningChats, string request) =>
        PromptRules.Tag("other_work", SessionHandoff.Condense(otherWork, InputChars, runningChats)) + "\n" +
        PromptRules.Tag("request", request.Trim());

    /// <summary>True when the other chats did something worth telling: a request, a reply or a tool call, not just turn ends.</summary>
    public static bool HasWork(IReadOnlyList<HandoffEntry> otherWork) =>
        otherWork.Any(e => e.Role != HandoffRole.TurnEnd && !string.IsNullOrWhiteSpace(e.Text));

    /// <summary>The note as the session's next message carries it.</summary>
    public static string BuildNote(string body, IReadOnlyList<FeatureChat> otherChats)
    {
        var note = "## Meanwhile, in other chats on this feature\n" +
                   "Since your last message, other chats worked on this feature. Check the files before relying on this.\n\n" +
                   body.Trim();
        return FeatureBrief.OtherChatsSection(otherChats) is { Length: > 0 } others ? note + "\n\n" + others : note;
    }

    /// <summary>The other chats' work as it is, under <paramref name="maxChars"/>: short histories and the model's fallback.</summary>
    public static string Verbatim(IReadOnlyList<HandoffEntry> otherWork, IReadOnlySet<string>? runningChats, int maxChars = FallbackChars) =>
        SessionHandoff.Condense(otherWork, maxChars, runningChats);

    /// <summary>The model's catch-up.</summary>
    public static async Task<string> WriteAsync(
        IHelperModel model, IReadOnlyList<HandoffEntry> otherWork, IReadOnlySet<string>? runningChats, string request,
        CancellationToken ct = default) =>
        ModelOutput.CleanText(await model
            .CompleteAsync(SystemPrompt, BuildInput(otherWork, runningChats, request), ReplyTokens, ct)
            .ConfigureAwait(false));
}
