using System.Text.Json.Nodes;

using Codale.Core.Agents;

namespace Codale.Mcp.Tests;

public sealed class McpServerTests
{
    private static McpServer Server() => new("test", "1.0", [
        new McpTool("echo", "Echoes text.", McpTool.Schema([("text", "string", "What to echo")], "text"),
            (a, _) => McpTool.Text(a.RequiredString("text"))),
        new McpTool("boom", "Always fails.", McpTool.Schema(),
            (_, _) => throw new InvalidOperationException("kaput")),
        new McpTool("shot", "Returns an image.", McpTool.Schema(),
            (_, _) => Task.FromResult<IReadOnlyList<McpContent>>([McpContent.FromImage([1, 2, 3])])),
    ]);

    private static async Task<JsonObject> Call(McpServer server, string json) =>
        (await server.HandleLineAsync(json))!;

    [Fact]
    public async Task Initialize_reports_tools_capability_and_server_info()
    {
        var reply = await Call(Server(), """{"jsonrpc":"2.0","id":1,"method":"initialize","params":{}}""");

        Assert.Equal(1, (int)reply["id"]!);
        Assert.Equal("test", (string)reply["result"]!["serverInfo"]!["name"]!);
        Assert.NotNull(reply["result"]!["capabilities"]!["tools"]);
    }

    [Fact]
    public async Task Notifications_get_no_reply()
    {
        Assert.Null(await Server().HandleLineAsync("""{"jsonrpc":"2.0","method":"notifications/initialized"}"""));
    }

    [Fact]
    public async Task Tools_list_carries_name_description_and_schema()
    {
        var reply = await Call(Server(), """{"jsonrpc":"2.0","id":2,"method":"tools/list"}""");

        var tools = reply["result"]!["tools"]!.AsArray();
        var echo = tools.Single(t => (string)t!["name"]! == "echo")!;
        Assert.Equal("Echoes text.", (string)echo["description"]!);
        Assert.Equal("text", (string)echo["inputSchema"]!["required"]![0]!);
    }

    [Fact]
    public async Task Tools_call_runs_the_handler_with_its_arguments()
    {
        var reply = await Call(Server(),
            """{"jsonrpc":"2.0","id":3,"method":"tools/call","params":{"name":"echo","arguments":{"text":"hi"}}}""");

        Assert.False((bool)reply["result"]!["isError"]!);
        Assert.Equal("hi", (string)reply["result"]!["content"]![0]!["text"]!);
    }

    [Fact]
    public async Task A_missing_argument_is_a_tool_error_the_model_can_read()
    {
        var reply = await Call(Server(),
            """{"jsonrpc":"2.0","id":4,"method":"tools/call","params":{"name":"echo","arguments":{}}}""");

        Assert.True((bool)reply["result"]!["isError"]!);
        Assert.Contains("text", (string)reply["result"]!["content"]![0]!["text"]!);
    }

    [Fact]
    public async Task A_throwing_handler_is_an_error_result_not_a_protocol_error()
    {
        var reply = await Call(Server(),
            """{"jsonrpc":"2.0","id":5,"method":"tools/call","params":{"name":"boom","arguments":{}}}""");

        Assert.Null(reply["error"]);
        Assert.True((bool)reply["result"]!["isError"]!);
        Assert.Contains("kaput", (string)reply["result"]!["content"]![0]!["text"]!);
    }

    [Fact]
    public async Task An_unknown_tool_is_an_error_result()
    {
        var reply = await Call(Server(),
            """{"jsonrpc":"2.0","id":6,"method":"tools/call","params":{"name":"nope"}}""");

        Assert.True((bool)reply["result"]!["isError"]!);
    }

    [Fact]
    public async Task An_unknown_method_is_a_protocol_error()
    {
        var reply = await Call(Server(), """{"jsonrpc":"2.0","id":7,"method":"resources/list"}""");

        Assert.Equal(-32601, (int)reply["error"]!["code"]!);
    }

    [Fact]
    public async Task Garbage_is_a_parse_error_and_does_not_throw()
    {
        var reply = await Call(Server(), "{not json");

        Assert.Equal(-32700, (int)reply["error"]!["code"]!);
    }

    [Fact]
    public async Task Images_are_returned_as_base64_content()
    {
        var reply = await Call(Server(),
            """{"jsonrpc":"2.0","id":8,"method":"tools/call","params":{"name":"shot"}}""");

        var content = reply["result"]!["content"]![0]!;
        Assert.Equal("image", (string)content["type"]!);
        Assert.Equal("image/png", (string)content["mimeType"]!);
        Assert.Equal(Convert.ToBase64String([1, 2, 3]), (string)content["data"]!);
    }

    [Fact]
    public async Task RunAsync_answers_each_line_and_stops_at_end_of_input()
    {
        var input = new StringReader(
            """{"jsonrpc":"2.0","id":1,"method":"ping"}""" + "\n\n" +
            """{"jsonrpc":"2.0","method":"notifications/initialized"}""" + "\n" +
            """{"jsonrpc":"2.0","id":2,"method":"ping"}""" + "\n");
        var output = new StringWriter();

        await Server().RunAsync(input, output);

        var lines = output.ToString().Split('\n', StringSplitOptions.RemoveEmptyEntries);
        Assert.Equal(2, lines.Length);
    }

    /// <summary>A writer that signals when a reply containing the marker has been written.</summary>
    private sealed class SignalWriter(string marker, TaskCompletionSource seen) : StringWriter
    {
        public override Task WriteLineAsync(string? value)
        {
            if (value?.Contains(marker) == true)
            {
                seen.TrySetResult();
            }

            return base.WriteLineAsync(value);
        }
    }

    [Fact]
    public async Task A_slow_call_does_not_block_ping()
    {
        var pingAnswered = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var server = new McpServer("test", "1.0", [
            new McpTool("slow", "Waits for the ping reply.", McpTool.Schema(), async (_, _) =>
            {
                // Serial handling would never answer the ping, so this would time out.
                await pingAnswered.Task.WaitAsync(TimeSpan.FromSeconds(10));
                return [McpContent.FromText("finished")];
            }),
        ]);
        var input = new StringReader(
            """{"jsonrpc":"2.0","id":"call","method":"tools/call","params":{"name":"slow"}}""" + "\n" +
            """{"jsonrpc":"2.0","id":"ping","method":"ping"}""" + "\n");
        var output = new SignalWriter("\"ping\"", pingAnswered);

        await server.RunAsync(input, output);

        var lines = output.ToString().Split('\n', StringSplitOptions.RemoveEmptyEntries);
        Assert.Equal(2, lines.Length);
        Assert.Contains("\"ping\"", lines[0]);
        Assert.Contains("finished", lines[1]);
    }

    [Fact]
    public async Task A_cancelled_call_is_aborted_and_gets_no_reply()
    {
        var started = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var observedCancel = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var server = new McpServer("test", "1.0", [
            new McpTool("hang", "Waits until cancelled.", McpTool.Schema(), async (_, ct) =>
            {
                started.SetResult();
                try
                {
                    await Task.Delay(Timeout.Infinite, ct);
                }
                catch (OperationCanceledException)
                {
                    observedCancel.SetResult();
                    throw;
                }

                return [];
            }),
        ]);
        var input = new BlockingReader(
            """{"jsonrpc":"2.0","id":7,"method":"tools/call","params":{"name":"hang"}}""",
            started.Task,
            """{"jsonrpc":"2.0","method":"notifications/cancelled","params":{"requestId":7}}""",
            """{"jsonrpc":"2.0","id":8,"method":"ping"}""");
        var output = new StringWriter();

        await server.RunAsync(input, output);

        await observedCancel.Task.WaitAsync(TimeSpan.FromSeconds(10));
        var line = Assert.Single(output.ToString().Split('\n', StringSplitOptions.RemoveEmptyEntries));
        Assert.Contains("\"id\":8", line);
    }

    /// <summary>Hands out its lines in order, waiting on a task between them where one is given.</summary>
    private sealed class BlockingReader(params object[] script) : TextReader
    {
        private int _next;

        public override async ValueTask<string?> ReadLineAsync(CancellationToken cancellationToken)
        {
            while (_next < script.Length)
            {
                var step = script[_next++];
                if (step is Task gate)
                {
                    await gate.WaitAsync(TimeSpan.FromSeconds(10), cancellationToken);
                    continue;
                }

                return (string)step;
            }

            return null;
        }
    }

    [Fact]
    public async Task Max_concurrent_calls_of_one_runs_calls_one_at_a_time()
    {
        var running = 0;
        var overlapped = false;
        var server = new McpServer("test", "1.0", [
            new McpTool("work", "Detects overlap.", McpTool.Schema(), async (_, _) =>
            {
                overlapped |= Interlocked.Increment(ref running) > 1;
                await Task.Delay(50);
                Interlocked.Decrement(ref running);
                return [McpContent.FromText("ok")];
            }),
        ]) { MaxConcurrentCalls = 1 };
        var input = new StringReader(string.Join("\n", Enumerable.Range(1, 4).Select(i =>
            """{"jsonrpc":"2.0","id":""" + i + ""","method":"tools/call","params":{"name":"work"}}""")) + "\n");
        var output = new StringWriter();

        await server.RunAsync(input, output);

        Assert.False(overlapped);
        Assert.Equal(4, output.ToString().Split('\n', StringSplitOptions.RemoveEmptyEntries).Length);
    }

    [Fact]
    public void Claude_config_lists_each_server_as_stdio()
    {
        var json = McpServerSpec.ToClaudeConfig([
            new McpServerSpec { Name = "codale-browser", Command = @"C:\x\b.exe", Args = ["--a"] },
        ]);

        var server = JsonNode.Parse(json!)!["mcpServers"]!["codale-browser"]!;
        Assert.Equal("stdio", (string)server["type"]!);
        Assert.Equal(@"C:\x\b.exe", (string)server["command"]!);
        Assert.Equal("--a", (string)server["args"]![0]!);
    }

    [Fact]
    public void No_servers_means_no_config_and_no_ask_settings()
    {
        Assert.Null(McpServerSpec.ToClaudeConfig([]));
        Assert.Null(McpServerSpec.ToClaudeSettings([], new ClaudeHookSettings()));
    }

    [Fact]
    public void Only_approval_required_servers_get_an_ask_rule()
    {
        var json = McpServerSpec.ToClaudeSettings([
            new McpServerSpec { Name = "codale-browser", Command = "b", AutoApprove = true },
            new McpServerSpec { Name = "codale-computer", Command = "c", RequireApproval = true },
        ], new ClaudeHookSettings());

        var ask = JsonNode.Parse(json!)!["permissions"]!["ask"]!.AsArray();
        Assert.Equal("mcp__codale-computer", (string)Assert.Single(ask)!);
    }
}
