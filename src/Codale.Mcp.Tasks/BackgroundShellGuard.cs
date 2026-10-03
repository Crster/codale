using System.Text.Json;
using System.Text.Json.Nodes;

using Codale.Core.Agents;

namespace Codale.Mcp.Tasks;

/// <summary>
/// Claude Code <c>PreToolUse</c> hook: refuses a shell call that asks to run in the
/// background and points the model at <c>start_task</c>. A hook runs before permission
/// checks, so it holds in every permission mode - unlike an approval prompt, which
/// auto and bypass modes never show.
/// </summary>
public static class BackgroundShellGuard
{
    /// <summary>The hook's stdout for this call, or null to let it through untouched.</summary>
    public static string? Evaluate(string hookInputJson)
    {
        JsonElement input;
        string toolName;

        try
        {
            using var doc = JsonDocument.Parse(hookInputJson);
            var root = doc.RootElement;
            if (root.ValueKind != JsonValueKind.Object)
            {
                return null;
            }

            toolName = root.TryGetProperty("tool_name", out var n) && n.ValueKind == JsonValueKind.String ? n.GetString() ?? "" : "";
            input = root.TryGetProperty("tool_input", out var i) && i.ValueKind == JsonValueKind.Object ? i.Clone() : default;
        }
        catch (JsonException)
        {
            return null; // never let a malformed hook payload block real work
        }

        if (toolName.Length == 0 || input.ValueKind != JsonValueKind.Object)
        {
            return null;
        }

        if (!BackgroundTaskDetector.IsBackgroundLaunch(toolName, input))
        {
            return null;
        }

        return new JsonObject
        {
            ["hookSpecificOutput"] = new JsonObject
            {
                ["hookEventName"] = "PreToolUse",
                ["permissionDecision"] = "deny",
                ["permissionDecisionReason"] = BackgroundTaskDetector.BackgroundBlockedMessage,
            },
        }.ToJsonString();
    }
}
