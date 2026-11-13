using System.Diagnostics;
using System.IO.Pipes;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;

namespace Codale.Core.Tasks;

/// <summary>What the agent sees of one terminal tab: its command, state and output.</summary>
public sealed record TaskSnapshot(
    string Id,
    string Name,
    string Command,
    string State,
    int? ExitCode,
    string Output,
    long NextOffset);

public sealed class TaskServiceException(string message) : Exception(message);

/// <summary>
/// What the tools of Codale's workspace servers (codale-terminal, codale-command, codale-panel) ask the app
/// for: the terminal tabs. The app implements it over its terminals; the MCP exe reaches it over a named pipe.
/// </summary>
public interface ITaskService
{
    /// <summary>The terminal tabs open in the workspace, the user's and the agent's.</summary>
    Task<IReadOnlyList<TaskSnapshot>> ListTerminalsAsync(CancellationToken ct);

    /// <summary>What a terminal tab printed since an offset (or its last <paramref name="tailChars"/> characters), with its state.</summary>
    Task<TaskSnapshot> ReadTerminalAsync(string id, long? since, int? tailChars, CancellationToken ct);

    /// <summary>Runs a command in a new terminal tab, waits up to <paramref name="waitSeconds"/> (or until it ends) and returns what it printed.</summary>
    Task<TaskSnapshot> RunInTerminalAsync(string command, string? name, int waitSeconds, CancellationToken ct);

    /// <summary>Interrupts the command of a terminal tab the agent opened; the tab stays, with its output.</summary>
    Task<TaskSnapshot> StopTerminalAsync(string id, CancellationToken ct);
}

/// <summary><see cref="ITaskService"/> straight over the workspace's terminals.</summary>
public sealed class LocalTaskService(ITerminalSource? terminals = null) : ITaskService
{
    private ITerminalSource Terminals =>
        terminals ?? throw new TaskServiceException("Terminals are not available here.");

    public Task<IReadOnlyList<TaskSnapshot>> ListTerminalsAsync(CancellationToken ct) =>
        Task.FromResult(terminals?.List() ?? []);

    public Task<TaskSnapshot> ReadTerminalAsync(string id, long? since, int? tailChars, CancellationToken ct) =>
        Task.FromResult(terminals?.Read(id, since, tailChars ?? 8000)
            ?? throw new TaskServiceException($"No terminal '{id}'. Use list_terminals to see the open ones."));

    public async Task<TaskSnapshot> RunInTerminalAsync(string command, string? name, int waitSeconds, CancellationToken ct) =>
        await Terminals.StartAsync(command, name, Math.Clamp(waitSeconds, 0, 60), ct).ConfigureAwait(false)
            ?? throw new TaskServiceException("No terminal could be opened: the Codale window is not available.");

    public async Task<TaskSnapshot> StopTerminalAsync(string id, CancellationToken ct) =>
        await Terminals.StopAsync(id, ct).ConfigureAwait(false)
            ?? throw new TaskServiceException($"No terminal '{id}' that you opened. You can only stop terminals opened with run_in_terminal or run_command.");
}

/// <summary>
/// Serves an <see cref="ITaskService"/> on a per-session named pipe. The pipe is
/// current-user only and every request must carry the session's secret, so nothing else
/// on the machine can run commands in or read the session's terminals.
/// </summary>
public sealed class TaskPipeServer : IDisposable
{
    private readonly ITaskService _service;
    private readonly byte[] _token;
    private readonly CancellationTokenSource _cts = new();

    public TaskPipeServer(ITaskService service)
    {
        _service = service;
        PipeName = "codale-tasks-" + Guid.NewGuid().ToString("N");
        Token = Convert.ToHexString(RandomNumberGenerator.GetBytes(24));
        _token = Encoding.UTF8.GetBytes(Token);
    }

    public string PipeName { get; }

    public string Token { get; }

    /// <summary>
    /// Called (on a pool thread) when an agent CLI outside the chat reports to the session panel:
    /// the kind ("todos" or "artifact") and the tool's input. Null drops such reports.
    /// </summary>
    public Action<string, JsonElement>? PanelPublished { get; set; }

    public void Start() => _ = Task.Run(async () =>
    {
        try
        {
            await AcceptLoopAsync().ConfigureAwait(false);
        }
        catch (Exception)
        {
        }
    });

    private async Task AcceptLoopAsync()
    {
        while (!_cts.IsCancellationRequested)
        {
            NamedPipeServerStream? pipe = null;
            try
            {
                pipe = new NamedPipeServerStream(
                    PipeName,
                    PipeDirection.InOut,
                    NamedPipeServerStream.MaxAllowedServerInstances,
                    PipeTransmissionMode.Byte,
                    PipeOptions.Asynchronous | PipeOptions.CurrentUserOnly);

                await pipe.WaitForConnectionAsync(_cts.Token).ConfigureAwait(false);

                // ServeAsync owns the pipe from here.
                var connected = pipe;
                pipe = null;
                _ = Task.Run(() => ServeAsync(connected));
            }
            catch (OperationCanceledException)
            {
                pipe?.Dispose();
                return;
            }
            catch (Exception e)
            {
                // A failed accept (IO error, pipe limit, ...) must not end the loop for good: the
                // agent's tools would silently stop working for the rest of the session.
                pipe?.Dispose();
                if (e is not IOException)
                {
                }

                await Task.Delay(200, _cts.Token).ConfigureAwait(ConfigureAwaitOptions.SuppressThrowing);
            }
        }
    }

    private async Task ServeAsync(NamedPipeServerStream pipe)
    {
        await using (pipe)
        {
            try
            {
                using var reader = new StreamReader(pipe, new UTF8Encoding(false));
                await using var writer = new StreamWriter(pipe, new UTF8Encoding(false)) { AutoFlush = true };

                while (!_cts.IsCancellationRequested && await reader.ReadLineAsync(_cts.Token).ConfigureAwait(false) is { } line)
                {
                    await writer.WriteLineAsync((await HandleAsync(line).ConfigureAwait(false)).ToJsonString()).ConfigureAwait(false);
                }
            }
            catch (Exception e) when (e is IOException or OperationCanceledException or ObjectDisposedException)
            {
                // The MCP process went away.
            }
            catch (Exception)
            {
                // Runs unobserved on the thread pool: log it rather than lose it.
            }
        }
    }

    /// <summary>One request line to one response object; public so tests need no pipe.</summary>
    public async Task<JsonObject> HandleAsync(string line)
    {
        try
        {
            var request = JsonNode.Parse(line)?.AsObject() ?? throw new TaskServiceException("Bad request.");

            var supplied = Encoding.UTF8.GetBytes(request["token"]?.GetValue<string>() ?? "");
            if (!CryptographicOperations.FixedTimeEquals(supplied, _token))
            {
                throw new TaskServiceException("Unauthorized.");
            }

            var ct = _cts.Token;
            switch (request["op"]?.GetValue<string>())
            {
                case "run":
                    return Ok(await _service.RunInTerminalAsync(
                        request["command"]?.GetValue<string>() ?? throw new TaskServiceException("command is required."),
                        request["name"]?.GetValue<string>(),
                        request["waitSeconds"]?.GetValue<int>() ?? 3,
                        ct).ConfigureAwait(false));

                case "stop":
                    return Ok(await _service.StopTerminalAsync(
                        request["id"]?.GetValue<string>() ?? throw new TaskServiceException("id is required."),
                        ct).ConfigureAwait(false));

                case "terminals":
                    var open = await _service.ListTerminalsAsync(ct).ConfigureAwait(false);
                    return new JsonObject
                    {
                        ["ok"] = true,
                        ["tasks"] = new JsonArray([.. open.Select(t => (JsonNode)Json(t))]),
                    };

                case "terminal":
                    return Ok(await _service.ReadTerminalAsync(
                        request["id"]?.GetValue<string>() ?? throw new TaskServiceException("id is required."),
                        request["since"]?.GetValue<long>(),
                        request["tailChars"]?.GetValue<int>(),
                        ct).ConfigureAwait(false));

                case "panel":
                    var kind = request["kind"]?.GetValue<string>() ?? throw new TaskServiceException("kind is required.");
                    var input = request["input"] ?? throw new TaskServiceException("input is required.");
                    using (var doc = JsonDocument.Parse(input.ToJsonString()))
                    {
                        PanelPublished?.Invoke(kind, doc.RootElement.Clone());
                    }

                    return new JsonObject { ["ok"] = true };

                default:
                    throw new TaskServiceException("Unknown operation.");
            }
        }
        catch (TaskServiceException e)
        {
            return new JsonObject { ["ok"] = false, ["error"] = e.Message };
        }
        catch (Exception e) when (e is JsonException or InvalidOperationException or FormatException or System.ComponentModel.Win32Exception)
        {
            return new JsonObject { ["ok"] = false, ["error"] = e.Message };
        }
        catch (Exception e) when (e is not OperationCanceledException)
        {
            // Whatever a service throws is the caller's error response, not a dropped connection.
            return new JsonObject { ["ok"] = false, ["error"] = e.Message };
        }
    }

    internal static JsonObject Json(TaskSnapshot t) => new()
    {
        ["id"] = t.Id,
        ["name"] = t.Name,
        ["command"] = t.Command,
        ["state"] = t.State,
        ["exitCode"] = t.ExitCode,
        ["output"] = t.Output,
        ["nextOffset"] = t.NextOffset,
    };

    private static JsonObject Ok(TaskSnapshot t)
    {
        var o = Json(t);
        o["ok"] = true;
        return o;
    }

    public void Dispose() => _cts.Cancel();
}

/// <summary>The MCP exe's end of the pipe: an <see cref="ITaskService"/> that asks the app.</summary>
public sealed class TaskPipeClient(string pipeName, string token) : ITaskService
{
    private readonly SemaphoreSlim _gate = new(1, 1);

    /// <summary>
    /// Shows a todos_set / artifact_add call of an agent CLI outside the chat in the session panel
    /// (<paramref name="kind"/> is "todos" or "artifact"). Never throws: the panel is a courtesy;
    /// the result is null when it was delivered, otherwise why it was not.
    /// </summary>
    public async Task<string?> PublishPanelAsync(string kind, JsonElement input, CancellationToken ct)
    {
        try
        {
            await CallAsync(
                    new JsonObject { ["op"] = "panel", ["kind"] = kind, ["input"] = JsonNode.Parse(input.GetRawText()) },
                    ct,
                    TimeSpan.FromSeconds(3),
                    exclusive: false)
                .ConfigureAwait(false);
            return null;
        }
        catch (Exception e) when (e is TaskServiceException or IOException or OperationCanceledException or JsonException)
        {
            return e.Message;
        }
    }

    public Task<TaskSnapshot> RunInTerminalAsync(string command, string? name, int waitSeconds, CancellationToken ct) =>
        CallOneAsync(new JsonObject { ["op"] = "run", ["command"] = command, ["name"] = name, ["waitSeconds"] = waitSeconds }, ct);

    public Task<TaskSnapshot> StopTerminalAsync(string id, CancellationToken ct) =>
        CallOneAsync(new JsonObject { ["op"] = "stop", ["id"] = id }, ct);

    public async Task<IReadOnlyList<TaskSnapshot>> ListTerminalsAsync(CancellationToken ct)
    {
        var response = await CallAsync(new JsonObject { ["op"] = "terminals" }, ct).ConfigureAwait(false);
        return [.. response["tasks"]!.AsArray().Select(n => Parse(n!.AsObject()))];
    }

    public Task<TaskSnapshot> ReadTerminalAsync(string id, long? since, int? tailChars, CancellationToken ct) =>
        CallOneAsync(new JsonObject { ["op"] = "terminal", ["id"] = id, ["since"] = since, ["tailChars"] = tailChars }, ct);

    private async Task<TaskSnapshot> CallOneAsync(JsonObject request, CancellationToken ct) =>
        Parse(await CallAsync(request, ct).ConfigureAwait(false));

    private Task<JsonObject> CallAsync(JsonObject request, CancellationToken ct) =>
        CallAsync(request, ct, timeout: null, exclusive: true);

    /// <param name="exclusive">
    /// Terminal calls take turns. A long model call runs on its own connection instead, so it
    /// never holds a read_terminal up for a minute.
    /// </param>
    private async Task<JsonObject> CallAsync(JsonObject request, CancellationToken ct, TimeSpan? timeout, bool exclusive)
    {
        request["token"] = token;

        using var limit = CancellationTokenSource.CreateLinkedTokenSource(ct);
        if (timeout is { } after)
        {
            limit.CancelAfter(after);
        }

        var outer = ct;
        ct = limit.Token;

        // One short-lived connection per call keeps the client stateless and lets the
        // app restart its pipe without wedging a long-running agent.
        if (exclusive)
        {
            await _gate.WaitAsync(ct).ConfigureAwait(false);
        }

        try
        {
            await using var pipe = new NamedPipeClientStream(".", pipeName, PipeDirection.InOut, PipeOptions.Asynchronous | PipeOptions.CurrentUserOnly);
            try
            {
                await pipe.ConnectAsync(5000, ct).ConfigureAwait(false);
            }
            catch (TimeoutException)
            {
                throw new TaskServiceException("Codale is not reachable (its terminal host is gone). Is the chat still open?");
            }

            using var reader = new StreamReader(pipe, new UTF8Encoding(false));
            await using var writer = new StreamWriter(pipe, new UTF8Encoding(false)) { AutoFlush = true };
            await writer.WriteLineAsync(request.ToJsonString().AsMemory(), ct).ConfigureAwait(false);

            var line = await reader.ReadLineAsync(ct).ConfigureAwait(false)
                ?? throw new TaskServiceException("Codale closed the connection.");
            var response = JsonNode.Parse(line)!.AsObject();

            if (response["ok"]?.GetValue<bool>() != true)
            {
                throw new TaskServiceException(response["error"]?.GetValue<string>() ?? "Request failed.");
            }

            return response;
        }
        catch (OperationCanceledException) when (timeout is { } waited && !outer.IsCancellationRequested)
        {
            throw new TaskServiceException($"Codale did not answer within {waited.TotalSeconds:0} s.");
        }
        finally
        {
            if (exclusive)
            {
                _gate.Release();
            }
        }
    }

    private static TaskSnapshot Parse(JsonObject o) => new(
        o["id"]!.GetValue<string>(),
        o["name"]!.GetValue<string>(),
        o["command"]!.GetValue<string>(),
        o["state"]!.GetValue<string>(),
        o["exitCode"]?.GetValue<int>(),
        o["output"]?.GetValue<string>() ?? "",
        o["nextOffset"]?.GetValue<long>() ?? 0);
}