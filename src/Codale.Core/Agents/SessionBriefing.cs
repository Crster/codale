namespace Codale.Core.Agents;

/// <summary>
/// How a briefing on earlier work (a feature's summary) rides into a fresh session: wrapped
/// as the first message's prefix, and stripped back off when a transcript is replayed.
/// </summary>
public static class SessionBriefing
{
    private const string ContextOpen = "<session-briefing>";
    private const string ContextClose = "</session-briefing>";

    /// <summary>
    /// The briefing as the first turn of the new session carries it: hidden from the
    /// transcript (history strips it back off) yet stored in it, so the context survives
    /// a resume - unlike a system prompt, which the CLI does not keep.
    /// </summary>
    public static string ContextBlock(string summary) =>
        $"{ContextOpen}\nThis session continues earlier work on the project. What follows is a briefing on it, for context only - " +
        $"it is not a request. Use it to carry on where that session left off.\n\n{summary.Trim()}\n{ContextClose}\n\n";

    /// <summary>Removes a leading <see cref="ContextBlock"/>, leaving the user's own words.</summary>
    public static string StripContext(string text)
    {
        if (!text.StartsWith(ContextOpen, StringComparison.Ordinal))
        {
            return text;
        }

        var end = text.IndexOf(ContextClose, StringComparison.Ordinal);
        return end < 0 ? text : text[(end + ContextClose.Length)..].TrimStart('\r', '\n');
    }
}