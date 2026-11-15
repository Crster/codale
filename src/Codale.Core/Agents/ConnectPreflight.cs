namespace Codale.Core.Agents;

/// <summary>Decisions made before a CLI session is started, and about a failure to start one.</summary>
public static class ConnectPreflight
{
    /// <summary>
    /// Whether Codale's own signed-in account must be checked before the CLI starts. A custom endpoint or a
    /// provider brings its own credentials, and so does an ambient Anthropic override for Claude.
    /// </summary>
    /// <param name="hasEndpointEnvironment">True when a custom endpoint supplies the spawn environment.</param>
    /// <param name="isCodex">True for the Codex CLI.</param>
    /// <param name="environmentOverridesEndpoint">True when the app was launched with an ambient endpoint override.</param>
    public static bool NeedsAccountCheck(bool hasEndpointEnvironment, bool isCodex, bool environmentOverridesEndpoint) =>
        !hasEndpointEnvironment && (isCodex || !environmentOverridesEndpoint);

    /// <summary>
    /// True when a failed start means the saved CLI conversation no longer exists, so a resumed chat should start
    /// a fresh one. Only a start that was resuming can lose its conversation.
    /// </summary>
    /// <param name="hasResumeId">True when the start was asked to resume a saved conversation.</param>
    /// <param name="message">The error the CLI reported.</param>
    public static bool IsResumeLost(bool hasResumeId, string message)
    {
        if (!hasResumeId)
        {
            return false;
        }

        return message.Contains("No conversation found with session ID", StringComparison.OrdinalIgnoreCase) ||
            message.Contains("no rollout found", StringComparison.OrdinalIgnoreCase) ||
            message.Contains("thread not found", StringComparison.OrdinalIgnoreCase) ||
            (message.Contains("thread", StringComparison.OrdinalIgnoreCase) && message.Contains("not found", StringComparison.OrdinalIgnoreCase));
    }
}
