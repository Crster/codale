using System.Collections.Concurrent;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;

namespace Codale.Mcp;

/// <summary>
/// A minimal MCP server over newline-delimited JSON-RPC (the stdio transport).
/// Only what a tools-only server needs: initialize, ping, tools/list, tools/call.
/// stdout carries protocol messages exclusively - diagnostics go to stderr.
/// </summary>
public sealed class McpServer(string name, string version, IReadOnlyList<McpTool> tools)
{
    public const string ProtocolVersion = "2025-06-18";

    private readonly Dictionary<string, McpTool> _tools = tools.ToDictionary(t => t.Name, StringComparer.Ordinal);

    /// <summary>
    /// How many tools/call requests may run at once. A server whose tools drive shared
    /// state that must not interleave (synthetic input, say) sets 1; calls still queue
    /// without blocking ping or cancellation.
    /// </summary>
    public int MaxConcurrentCalls { get; init; } = int.MaxValue;

    /// <summary>
    /// Reads requests until the input closes. tools/call runs concurrently so a slow tool
    /// (a 15-minute browser handoff) cannot starve ping or other calls; everything else is
    /// quick and answered in order. notifications/cancelled aborts the matching call, which
    /// then sends no reply, as the protocol asks.
    /// </summary>
    public async Task RunAsync(TextReader input, TextWriter output, CancellationToken ct = default)
    {
        // One writer at a time: replies finish on arbitrary threads but must not interleave on the wire.
        using var writeLock = new SemaphoreSlim(1, 1);
        using var slots = new SemaphoreSlim(Math.Max(1, MaxConcurrentCalls));
        var inFlight = new ConcurrentDictionary<string, CancellationTokenSource>();
        var running = new List<Task>();

        async Task WriteAsync(JsonObject reply)
        {
            await writeLock.WaitAsync(ct);
            try
            {
                await output.WriteLineAsync(reply.ToJsonString());
                await output.FlushAsync(ct);
            }
            finally
            {
                writeLock.Release();
            }
        }

        while (await input.ReadLineAsync(ct) is { } line)
        {
            if (string.IsNullOrWhiteSpace(line))
            {
                continue;
            }

            JsonNode? node;
            try
            {
                node = JsonNode.Parse(line);
            }
            catch (JsonException)
            {
                await WriteAsync(Error(null, -32700, "Parse error"));
                continue;
            }

            if (node is JsonObject msg)
            {
                var method = msg["method"] is JsonValue m && m.TryGetValue<string>(out var s) ? s : null;

                if (method == "notifications/cancelled")
                {
                    Cancel(inFlight, msg["params"]?["requestId"]);
                    continue;
                }

                if (method == "tools/call" && msg["id"] is { } idNode)
                {
                    running.RemoveAll(t => t.IsCompletedSuccessfully);
                    running.Add(RunCallAsync(msg, idNode.ToJsonString(), inFlight, slots, WriteAsync, ct));
                    continue;
                }
            }

            var reply = await HandleAsync(node, ct);
            if (reply is not null)
            {
                await WriteAsync(reply);
            }
        }

        // Input closed: let the calls still running deliver their replies.
        await Task.WhenAll(running);
    }

    private Task RunCallAsync(
        JsonObject msg, string key, ConcurrentDictionary<string, CancellationTokenSource> inFlight,
        SemaphoreSlim slots, Func<JsonObject, Task> write, CancellationToken ct)
    {
        // Registered before the task starts, so a cancellation right behind the call finds it.
        var cts = CancellationTokenSource.CreateLinkedTokenSource(ct);
        inFlight[key] = cts;

        return Task.Run(async () =>
        {
            try
            {
                await slots.WaitAsync(cts.Token);
                JsonObject? reply;
                try
                {
                    reply = await HandleAsync(msg, cts.Token);
                }
                finally
                {
                    slots.Release();
                }

                if (reply is not null && !cts.IsCancellationRequested)
                {
                    await write(reply);
                }
            }
            catch (Exception) when (cts.IsCancellationRequested)
            {
                // Cancelled by the client or by shutdown: the protocol wants no reply.
            }
            finally
            {
                inFlight.TryRemove(new KeyValuePair<string, CancellationTokenSource>(key, cts));
                cts.Dispose();
            }
        }, CancellationToken.None);
    }

    private static void Cancel(ConcurrentDictionary<string, CancellationTokenSource> inFlight, JsonNode? requestId)
    {
        if (requestId is not null && inFlight.TryGetValue(requestId.ToJsonString(), out var cts))
        {
            try
            {
                cts.Cancel();
            }
            catch (ObjectDisposedException)
            {
                // The call finished in the instant before the cancel arrived.
            }
        }
    }

    /// <summary>Handles one JSON-RPC message; returns the reply, or null for notifications.</summary>
    public async Task<JsonObject?> HandleLineAsync(string line, CancellationToken ct = default)
    {
        JsonNode? node;
        try
        {
            node = JsonNode.Parse(line);
        }
        catch (JsonException)
        {
            return Error(null, -32700, "Parse error");
        }

        return await HandleAsync(node, ct);
    }

    private async Task<JsonObject?> HandleAsync(JsonNode? node, CancellationToken ct)
    {
        JsonNode? id = null;
        try
        {
            if (node is not JsonObject msg)
            {
                return Error(null, -32600, "Invalid request");
            }

            id = msg["id"]?.DeepClone();
            var method = msg["method"]?.GetValue<string>();
            if (id is null)
            {
                return null; // notification (notifications/initialized, cancelled, ...)
            }

            return method switch
            {
                "initialize" => Result(id, new JsonObject
                {
                    ["protocolVersion"] = ProtocolVersion,
                    ["capabilities"] = new JsonObject { ["tools"] = new JsonObject() },
                    ["serverInfo"] = new JsonObject { ["name"] = name, ["version"] = version },
                }),
                "ping" => Result(id, new JsonObject()),
                "tools/list" => Result(id, new JsonObject
                {
                    ["tools"] = new JsonArray(_tools.Values.Select(t => (JsonNode)new JsonObject
                    {
                        ["name"] = t.Name,
                        ["description"] = t.Description,
                        ["inputSchema"] = t.InputSchema.DeepClone(),
                    }).ToArray()),
                }),
                "tools/call" => Result(id, await CallToolAsync(msg["params"] as JsonObject, ct)),
                _ => Error(id, -32601, $"Method not found: {method}"),
            };
        }
        catch (JsonException)
        {
            return Error(null, -32700, "Parse error");
        }
        catch (Exception ex) when (!ct.IsCancellationRequested)
        {
            return Error(id, -32603, ex.Message);
        }
    }

    private async Task<JsonObject> CallToolAsync(JsonObject? p, CancellationToken ct)
    {
        var toolName = p?["name"]?.GetValue<string>();
        if (toolName is null || !_tools.TryGetValue(toolName, out var tool))
        {
            return ToolResult([McpContent.FromText($"Unknown tool: {toolName}")], isError: true);
        }

        JsonElement? args = p?["arguments"] is { } a ? JsonSerializer.SerializeToElement(a) : null;
        try
        {
            return ToolResult(await tool.Handler(new McpArgs(args), ct), isError: false);
        }
        catch (McpToolException ex)
        {
            return ToolResult([McpContent.FromText(ex.Message)], isError: true);
        }
        catch (Exception ex) when (!ct.IsCancellationRequested)
        {
            // A failing tool is a result the model can read and recover from, not a protocol error.
            return ToolResult([McpContent.FromText($"{tool.Name} failed: {ex.Message}")], isError: true);
        }
    }

    private static JsonObject ToolResult(IReadOnlyList<McpContent> content, bool isError) => new()
    {
        ["content"] = new JsonArray(content.Select(c => (JsonNode)c.ToJson()).ToArray()),
        ["isError"] = isError,
    };

    private static JsonObject Result(JsonNode id, JsonObject result) =>
        new() { ["jsonrpc"] = "2.0", ["id"] = id, ["result"] = result };

    private static JsonObject Error(JsonNode? id, int code, string message) => new()
    {
        ["jsonrpc"] = "2.0",
        ["id"] = id,
        ["error"] = new JsonObject { ["code"] = code, ["message"] = message },
    };

    /// <summary>Runs the server on the process's stdio until stdin closes.</summary>
    public async Task RunStdioAsync(CancellationToken ct = default)
    {
        var utf8 = new UTF8Encoding(false);
        // 64KB buffer: a screenshot reply is a megabyte-plus of base64, and the default
        // 1KB buffer fragmented each one into hundreds of small console writes.
        var stdout = new StreamWriter(Console.OpenStandardOutput(), utf8, bufferSize: 64 * 1024) { AutoFlush = false };
        var stdin = new StreamReader(Console.OpenStandardInput(), utf8);
        await RunAsync(stdin, stdout, ct);
    }
}
