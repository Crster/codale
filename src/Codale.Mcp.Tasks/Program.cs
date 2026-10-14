using Codale.Core.Tasks;
using Codale.Mcp;
using Codale.Mcp.Tasks;

// One exe, two roles: the codale-tasks MCP server, and the CLI's PreToolUse hook (run as a command).
var pipe = Environment.GetEnvironmentVariable("CODALE_TASKS_PIPE");
var token = Environment.GetEnvironmentVariable("CODALE_TASKS_TOKEN");
var client = string.IsNullOrEmpty(pipe) || string.IsNullOrEmpty(token) ? null : new TaskPipeClient(pipe, token);

if (args.Contains("--pretooluse-hook"))
{
    var payload = await Console.In.ReadToEndAsync();
    try
    {
        if (BackgroundShellGuard.Evaluate(payload) is { } reply)
        {
            Console.Out.Write(reply);
        }
    }
    catch (Exception e)
    {
        // A guard that fails must never block the user's tool call: say nothing and let it through.
        Console.Error.WriteLine($"codale-tasks: guard hook skipped: {e.Message}");
    }

    return 0;
}

if (client is null)
{
    Console.Error.WriteLine("codale-tasks: CODALE_TASKS_PIPE / CODALE_TASKS_TOKEN are not set; Codale starts this server.");
    return 1;
}

var server = new McpServer("codale-tasks", "1.0.0", TaskTools.Create(
    client,
    Environment.GetEnvironmentVariable("CODALE_PROJECT_DIR"),
    Environment.GetEnvironmentVariable("CODALE_RUN_DIR")))
{
    Instructions = McpInstructions.Enabled ? McpInstructions.Tasks : null,
};
await server.RunStdioAsync();
return 0;
