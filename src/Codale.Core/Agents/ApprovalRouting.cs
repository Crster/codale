using Codale.Core.Shell;

namespace Codale.Core.Agents;

/// <summary>
/// The first answers the host gives to an approval request, before the shell and planning checks.
/// Each step either answers yes without asking the reader, sends the request to the governing
/// policy, or passes it on.
/// </summary>
public enum ApprovalStep
{
    /// <summary>Not decided here: the host's later checks (shell risk, planning, full mode, the reader) decide it.</summary>
    Continue,

    /// <summary>A file the reader attached is being read; it is what they asked for, wherever it lives.</summary>
    AllowAttachedRead,

    /// <summary>Codale's own sandboxed tools, which run unasked outside Manual mode.</summary>
    AllowUnasked,

    /// <summary>Ask, Plan, Interactive and Auto are decided by <see cref="ApprovalPolicy.Decide"/>.</summary>
    ConsultPolicy,

    /// <summary>File edits go through unasked in the accept-edits mode.</summary>
    AllowEdit,
}

/// <summary>Orders the host's first approval checks. Pure: the caller performs the answer it returns.</summary>
public static class ApprovalRouting
{
    private static readonly HashSet<string> EditTools = ["Edit", "Write", "MultiEdit", "NotebookEdit"];

    /// <param name="permissionMode">The host mode (or CLI mode) the session runs in.</param>
    /// <param name="toolName">The tool the agent asked to use.</param>
    /// <param name="isAttachedRead">True when the request reads a file the reader attached.</param>
    /// <param name="runsUnasked">True when Codale's own sandboxed tool is asking.</param>
    public static ApprovalStep Route(string permissionMode, string toolName, bool isAttachedRead, bool runsUnasked)
    {
        if (isAttachedRead)
        {
            return ApprovalStep.AllowAttachedRead;
        }

        return permissionMode is not ("manual" or "default") && runsUnasked
            ? ApprovalStep.AllowUnasked
            : ApprovalPolicy.Governs(permissionMode)
            ? ApprovalStep.ConsultPolicy
            : permissionMode == "acceptEdits" && EditTools.Contains(toolName) ? ApprovalStep.AllowEdit : ApprovalStep.Continue;
    }
}