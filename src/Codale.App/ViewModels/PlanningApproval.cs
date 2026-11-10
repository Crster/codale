using Codale.Core.Shell;

namespace Codale.App.ViewModels;

/// <summary>Verified reads in Plan, Auto and Ask run before creating any interactive approval card.</summary>
internal static class PlanningApproval
{
    public static async Task<bool> TryApproveInspectionAsync(
        string permissionMode, bool isAutomaticMode, string toolName, string? command, Func<Task> approve)
    {
        if ((permissionMode is not ("plan" or "ask") && !isAutomaticMode) ||
            !PlanningShellPolicy.AllowsToolInspection(toolName, command)) return false;

        await approve();
        return true;
    }
}
