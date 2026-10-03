using System.Text.Json;

using Codale.Core.Tasks;
using Codale.Mcp.Tasks;

namespace Codale.Mcp.Tests;

public sealed class TaskToolsTests
{
    private sealed class FakeTasks : ITaskService
    {
        public string? LastCommand;
        public int LastWait;

        public Task<TaskSnapshot> StartAsync(string command, string? name, int waitSeconds, CancellationToken ct)
        {
            LastCommand = command;
            LastWait = waitSeconds;
            return Task.FromResult(new TaskSnapshot("t1", name ?? command, command, "running", null, "ready on :5173\n", 15));
        }

        public Task<TaskSnapshot> ReadAsync(string id, long? since, int? tailChars, CancellationToken ct) =>
            id == "t1"
                ? Task.FromResult(new TaskSnapshot("t1", "dev", "npm run dev", "exited", 1, "", 15))
                : throw new TaskServiceException($"No task '{id}'.");

        public Task<IReadOnlyList<TaskSnapshot>> ListAsync(CancellationToken ct) => Task.FromResult<IReadOnlyList<TaskSnapshot>>([]);

        public Task<TaskSnapshot> StopAsync(string id, CancellationToken ct) =>
            Task.FromResult(new TaskSnapshot(id, "dev", "npm run dev", "stopped", null, "", 15));
    }

    private static McpServer Server(FakeTasks fake, string? projectRoot = null) =>
        new("codale-tasks", "1.0", TaskTools.Create(fake, projectRoot));

    private static string Text(JsonElement result) => result.GetProperty("content")[0].GetProperty("text").GetString()!;

    private static async Task<JsonElement> Call(McpServer server, string tool, string args)
    {
        var reply = await server.HandleLineAsync(
            $$$"""{"jsonrpc":"2.0","id":1,"method":"tools/call","params":{"name":"{{{tool}}}","arguments":{{{args}}}}}""");
        return JsonDocument.Parse(reply!.ToJsonString()).RootElement.GetProperty("result");
    }

    [Fact]
    public async Task Start_passes_the_command_and_reports_output()
    {
        var fake = new FakeTasks();
        var result = await Call(Server(fake), "start_task", """{"command":"npm run dev","wait_seconds":5}""");

        Assert.Equal("npm run dev", fake.LastCommand);
        Assert.Equal(5, fake.LastWait);
        var text = result.GetProperty("content")[0].GetProperty("text").GetString()!;
        Assert.Contains("task t1", text);
        Assert.Contains("ready on :5173", text);
        Assert.Contains("next_offset: 15", text);
    }

    [Fact]
    public async Task Missing_task_is_a_tool_error_not_a_crash()
    {
        var result = await Call(Server(new FakeTasks()), "read_task", """{"id":"zzz"}""");

        Assert.True(result.GetProperty("isError").GetBoolean());
        Assert.Contains("No task 'zzz'", result.GetProperty("content")[0].GetProperty("text").GetString());
    }

    [Fact]
    public async Task Exit_code_is_reported()
    {
        var result = await Call(Server(new FakeTasks()), "read_task", """{"id":"t1"}""");
        Assert.Contains("exit code 1", result.GetProperty("content")[0].GetProperty("text").GetString());
    }

    [Fact]
    public async Task Command_tools_are_absent_without_a_project()
    {
        var reply = await Server(new FakeTasks()).HandleLineAsync("""{"jsonrpc":"2.0","id":1,"method":"tools/list"}""");
        var names = JsonDocument.Parse(reply!.ToJsonString()).RootElement.GetProperty("result").GetProperty("tools")
            .EnumerateArray().Select(t => t.GetProperty("name").GetString()).ToList();

        Assert.Contains("start_task", names);
        Assert.DoesNotContain("add_command", names);
    }

    [Fact]
    public async Task Added_command_is_listed_and_runs_by_name()
    {
        var root = Directory.CreateTempSubdirectory("codale-cmds-").FullName;
        try
        {
            var fake = new FakeTasks();
            var server = Server(fake, root);

            var added = await Call(server, "add_command", """{"name":"dev server","command":"npm run dev"}""");
            Assert.False(added.TryGetProperty("isError", out var err) && err.GetBoolean());
            Assert.Contains("npm run dev", Text(await Call(server, "list_commands", "{}")));

            // Re-adding by name (any case) replaces the entry instead of duplicating it.
            await Call(server, "add_command", """{"name":"Dev Server","command":"npm start"}""");
            var listing = Text(await Call(server, "list_commands", "{}"));
            Assert.Single(listing.Split('\n'));
            Assert.Contains("npm start", listing);

            var run = await Call(server, "run_command", """{"name":"dev"}""");
            Assert.Equal("npm start", fake.LastCommand);
            Assert.Contains("task t1", Text(run));
        }
        finally
        {
            Directory.Delete(root, recursive: true);
        }
    }

    [Fact]
    public async Task Run_command_with_an_unknown_name_lists_the_available_ones()
    {
        var root = Directory.CreateTempSubdirectory("codale-cmds-").FullName;
        try
        {
            var server = Server(new FakeTasks(), root);
            await Call(server, "add_command", """{"name":"build","command":"dotnet build"}""");

            var result = await Call(server, "run_command", """{"name":"deploy"}""");
            Assert.True(result.GetProperty("isError").GetBoolean());
            Assert.Contains("\"build\"", Text(result));
        }
        finally
        {
            Directory.Delete(root, recursive: true);
        }
    }

    [Theory]
    [InlineData("""{"tool_name":"Bash","tool_input":{"command":"npm run dev","run_in_background":true}}""", true)]
    [InlineData("""{"tool_name":"PowerShell","tool_input":{"command":"npm run dev","run_in_background":true}}""", true)]
    [InlineData("""{"tool_name":"Bash","tool_input":{"command":"ls","run_in_background":false}}""", false)]
    [InlineData("""{"tool_name":"Bash","tool_input":{"command":"ls"}}""", false)]
    [InlineData("""{"tool_name":"Read","tool_input":{"run_in_background":true}}""", false)]
    [InlineData("not json", false)]
    public void Guard_denies_only_background_shells(string hookInput, bool denied)
    {
        var output = BackgroundShellGuard.Evaluate(hookInput);

        Assert.Equal(denied, output is not null);
        if (output is not null)
        {
            var decision = JsonDocument.Parse(output).RootElement.GetProperty("hookSpecificOutput");
            Assert.Equal("PreToolUse", decision.GetProperty("hookEventName").GetString());
            Assert.Equal("deny", decision.GetProperty("permissionDecision").GetString());
            Assert.Contains("start_task", decision.GetProperty("permissionDecisionReason").GetString());
        }
    }
}
