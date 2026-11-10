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
    /// <param name="first">
    /// True for the first briefing of a session, which carries the full rules. Later ones carry a one-line tag:
    /// the rules are already in the conversation, and repeating them every turn only spends tokens.
    /// The wording is constant so the text before the newest message stays byte-identical for the provider's prompt cache.
    /// </param>
    public static string ContextBlock(string summary, bool first = true) =>
        (first
            ? $"{ContextOpen}\nPre-explored by Codale for this request; paths and excerpts below were verified current. " +
              "Treat this as your own exploration and act on it directly: do not grep, glob, search or re-read these files to confirm it. " +
              "If your edit tool requires a prior read, read only the listed line range. " +
              "Edit straight away, and do not search for other usages afterwards unless you changed a signature or a name. " +
              "Command output is historical, not current verification. Explore only what is genuinely missing. " +
              "Earlier work describes the past, not new requests."
            : $"{ContextOpen}\nPre-explored by Codale (verified current; do not re-search it).") +
        $"\n\n{summary.Trim()}\n{ContextClose}\n\n";

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
