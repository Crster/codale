using System.Text.Json;

namespace Codale.Core.Agents;

public enum TodoStatus
{
    Pending,
    InProgress,
    Completed,
}

public sealed record TodoItem
{
    public required string Content { get; init; }
    public TodoStatus Status { get; init; }

    public bool IsDone => Status == TodoStatus.Completed;
}

/// <summary>
/// Reads the input of Codale's own <c>todos_set</c> tool (codale-tasks MCP server):
/// <c>{ "todos": [ { "content": "...", "status": "pending|in_progress|completed" } ] }</c>,
/// always the whole list. The schema is ours, so there is exactly one shape to read.
/// </summary>
public static class TodoParser
{
    /// <summary>The tool's bare name; the CLI adds an MCP prefix (<c>mcp__codale-tasks__</c>).</summary>
    public const string ToolName = "todos_set";

    /// <summary>The artifact tool's bare name.</summary>
    public const string ArtifactToolName = "artifact_add";

    public static bool IsTodosTool(string toolName) => IsCodaleTool(toolName, ToolName);

    public static bool IsArtifactTool(string toolName) => IsCodaleTool(toolName, ArtifactToolName);

    /// <summary>The CLI's prefix always names the codale-tasks server; another server's tool of the same name is not ours.</summary>
    private static bool IsCodaleTool(string toolName, string bareName) =>
        toolName == bareName ||
        (toolName.EndsWith(bareName, StringComparison.Ordinal) && toolName.Contains("codale", StringComparison.OrdinalIgnoreCase));

    public static IReadOnlyList<TodoItem> FromToolInput(JsonElement input)
    {
        if (input.ValueKind != JsonValueKind.Object ||
            !input.TryGetProperty("todos", out var todos) ||
            todos.ValueKind != JsonValueKind.Array)
        {
            return [];
        }

        var items = new List<TodoItem>();
        foreach (var entry in todos.EnumerateArray())
        {
            // The schema names "content", but a CLI model sometimes fills the list with its own
            // field names (Antigravity sends "text"); a step that is still readable is kept.
            if (entry.ValueKind == JsonValueKind.Object &&
                ContentFields.Select(f => Text(entry, f)).FirstOrDefault(t => !string.IsNullOrWhiteSpace(t)) is { } content)
            {
                items.Add(new TodoItem { Content = content.Trim(), Status = ParseStatus(Text(entry, "status")) });
            }
            else if (entry.ValueKind == JsonValueKind.String && !string.IsNullOrWhiteSpace(entry.GetString()))
            {
                items.Add(new TodoItem { Content = entry.GetString()!.Trim() });
            }
        }

        return items;
    }

    private static readonly string[] ContentFields = ["content", "text", "title", "task", "step", "description", "name"];

    private static string? Text(JsonElement element, string name) =>
        element.TryGetProperty(name, out var value) && value.ValueKind == JsonValueKind.String
            ? value.GetString()
            : null;

    private static TodoStatus ParseStatus(string? status) => status?.ToLowerInvariant().Replace("-", "_").Replace(" ", "_") switch
    {
        "completed" or "complete" or "done" => TodoStatus.Completed,
        "in_progress" or "inprogress" or "active" or "running" => TodoStatus.InProgress,
        _ => TodoStatus.Pending,
    };
}
