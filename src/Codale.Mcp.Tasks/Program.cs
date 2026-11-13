using Codale.Core.Tasks;
using Codale.Mcp;
using Codale.Mcp.Tasks;

// One exe, two roles: each of Codale's workspace MCP servers (--server NAME, see CodaleServers),
// and the CLI's PreToolUse hook (run as a command).
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
        Console.Error.WriteLine($"codale: guard hook skipped: {e.Message}");
    }

    return 0;
}

if (client is null)
{
    Console.Error.WriteLine("codale: CODALE_TASKS_PIPE / CODALE_TASKS_TOKEN are not set; Codale starts this server.");
    return 1;
}

var index = Array.IndexOf(args, "--server");
var name = index >= 0 && index + 1 < args.Length ? args[index + 1] : null;
if (name is null || !CodaleServers.All.Contains(name))
{
    Console.Error.WriteLine($"codale: pass --server with one of {string.Join(", ", CodaleServers.All)}.");
    return 1;
}

var server = new McpServer(name, "1.0.0", TaskTools.Create(
    client,
    Environment.GetEnvironmentVariable("CODALE_PROJECT_DIR"),
    Environment.GetEnvironmentVariable("CODALE_RUN_DIR"),
    name))
{
    Instructions = McpInstructions.Enabled ? CodaleServers.Instructions(name) : null,
};
await server.RunStdioAsync();
return 0;
