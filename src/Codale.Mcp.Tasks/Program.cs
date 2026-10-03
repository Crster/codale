using Codale.Core.Tasks;
using Codale.Mcp;
using Codale.Mcp.Tasks;

// One exe, three roles: the codale-tasks MCP server, and the CLI's PreToolUse and
// PostToolUse hooks (run as commands). A hook reaches the app through the CLI's own
// environment, which Codale gives the same pipe as the server.
var pipe = Environment.GetEnvironmentVariable("CODALE_TASKS_PIPE");
var token = Environment.GetEnvironmentVariable("CODALE_TASKS_TOKEN");
var client = string.IsNullOrEmpty(pipe) || string.IsNullOrEmpty(token) ? null : new TaskPipeClient(pipe, token);
var dataRoot = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "Codale");

if (args.Contains("--pretooluse-hook"))
{
    var payload = await Console.In.ReadToEndAsync();
    try
    {
        var reply = BackgroundShellGuard.Evaluate(payload);
        if (reply is null && ArgValue(args, "--read-guard") is { } limit)
        {
            reply = ReadGuard.Evaluate(
                payload,
                Path.Combine(dataRoot, "hook-state"),
                int.TryParse(limit, out var lines) && lines > 0 ? lines : ReadGuard.DefaultMaxLines);
        }

        if (reply is not null)
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

if (args.Contains("--posttooluse-hook"))
{
    var payload = await Console.In.ReadToEndAsync();
    try
    {
        if (await ShellOutputHook.EvaluateAsync(payload, client, Path.Combine(dataRoot, "shell-output"), args.Contains("--digest")) is { } reply)
        {
            Console.Out.Write(reply);
        }
    }
    catch (Exception e)
    {
        // A hook that fails must never cost the agent its result: say nothing and let it through.
        Console.Error.WriteLine($"codale-tasks: output hook skipped: {e.Message}");
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
    Environment.GetEnvironmentVariable("CODALE_RUN_DIR"),
    assist: Environment.GetEnvironmentVariable("CODALE_ASSIST") == "1" ? client : null));
await server.RunStdioAsync();
return 0;

// "--name value" on the command line; the bare flag reads as an empty value.
static string? ArgValue(string[] args, string name)
{
    var i = Array.IndexOf(args, name);
    return i < 0 ? null : i + 1 < args.Length && !args[i + 1].StartsWith("--", StringComparison.Ordinal) ? args[i + 1] : "";
}
