using System.Diagnostics;
using System.IO.Pipes;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;

namespace Codale.Core.Tasks;

/// <summary>What the agent sees of one task.</summary>
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
/// The operations the agent's <c>codale-tasks</c> tools perform. The app implements it
/// over <see cref="HostedTaskManager"/>; the MCP exe reaches it over a named pipe.
/// </summary>
public interface ITaskService
{
    /// <summary>Starts a command, waits up to <paramref name="waitSeconds"/> (or until it exits) and returns what it printed.</summary>
    Task<TaskSnapshot> StartAsync(string command, string? name, int waitSeconds, CancellationToken ct);

    /// <summary>Output since an offset (or the last <paramref name="tailChars"/> characters) and the current state.</summary>
    Task<TaskSnapshot> ReadAsync(string id, long? since, int? tailChars, CancellationToken ct);

    Task<IReadOnlyList<TaskSnapshot>> ListAsync(CancellationToken ct);

    Task<TaskSnapshot> StopAsync(string id, CancellationToken ct);
}

/// <summary>
/// Work the app does for the agent on its background-task model (a BYOK provider), so the
/// chat model spends fewer tokens: exploring the codebase, answering questions about files
/// it has not read, and condensing large shell output. Reached over the same pipe as the
/// task tools; the hook and the MCP exe hold no model of their own.
/// </summary>
public interface ITaskAssist
{
    /// <summary>Answers an open-ended codebase question with file:line pointers.</summary>
    Task<string> ExploreAsync(string question, CancellationToken ct);

    /// <summary>A digest of a command's output that keeps every error, or null when no model is available for it.</summary>
    Task<string?> DigestAsync(string command, string output, CancellationToken ct);

    /// <summary>A tool result reached the agent as <paramref name="afterChars"/> instead of <paramref name="beforeChars"/>.</summary>
    void RecordSaved(long beforeChars, long afterChars);
}

/// <summary><see cref="ITaskService"/> straight over the manager that owns the processes.</summary>
public sealed class LocalTaskService(HostedTaskManager manager) : ITaskService
{
    public async Task<TaskSnapshot> StartAsync(string command, string? name, int waitSeconds, CancellationToken ct)
    {
        var task = manager.Start(command, name);

        // Early output (a port number, a compile error) is what the agent needs next.
        var deadline = DateTime.UtcNow + TimeSpan.FromSeconds(Math.Clamp(waitSeconds, 0, 60));
        while (task.State == HostedTaskState.Running && DateTime.UtcNow < deadline)
        {
            await Task.Delay(100, ct).ConfigureAwait(false);
        }

        return Snapshot(task, since: null, tailChars: 8000);
    }

    public Task<TaskSnapshot> ReadAsync(string id, long? since, int? tailChars, CancellationToken ct) =>
        Task.FromResult(Snapshot(Find(id), since, tailChars ?? 8000));

    public Task<IReadOnlyList<TaskSnapshot>> ListAsync(CancellationToken ct) =>
        Task.FromResult<IReadOnlyList<TaskSnapshot>>([.. manager.List().Select(t => Snapshot(t, null, 0))]);

    public Task<TaskSnapshot> StopAsync(string id, CancellationToken ct)
    {
        var task = Find(id);
        task.Stop();
        return Task.FromResult(Snapshot(task, null, 2000));
    }

    private HostedTask Find(string id) =>
        manager.Get(id) ?? throw new TaskServiceException($"No task '{id}'. Use list_tasks to see the running ones.");

    private static TaskSnapshot Snapshot(HostedTask task, long? since, int tailChars)
    {
        var (text, next) = tailChars == 0 ? ("", task.Read().NextOffset) : task.Read(since, tailChars);
        return new TaskSnapshot(task.Id, task.Name, task.Command, task.State.ToString().ToLowerInvariant(), task.ExitCode, text, next);
    }
}

/// <summary>
/// Serves an <see cref="ITaskService"/> on a per-session named pipe. The pipe is
/// current-user only and every request must carry the session's secret, so nothing else
/// on the machine can start or read the session's tasks.
/// </summary>
public sealed class TaskPipeServer : IDisposable
{
    private readonly ITaskService _service;
    private readonly ITaskAssist? _assist;
    private readonly byte[] _token;
    private readonly CancellationTokenSource _cts = new();

    /// <param name="assist">The model-backed helpers; null answers their requests with an error.</param>
    public TaskPipeServer(ITaskService service, ITaskAssist? assist = null)
    {
        _service = service;
        _assist = assist;
        PipeName = "codale-tasks-" + Guid.NewGuid().ToString("N");
        Token = Convert.ToHexString(RandomNumberGenerator.GetBytes(24));
        _token = Encoding.UTF8.GetBytes(Token);
    }

    public string PipeName { get; }

    public string Token { get; }

    public void Start() => _ = Task.Run(async () =>
    {
        try
        {
            await AcceptLoopAsync().ConfigureAwait(false);
        }
        catch (Exception e)
        {
            Trace.WriteLine($"Task pipe accept loop ended: {e}");
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
                    Trace.WriteLine($"Task pipe accept failed: {e.Message}");
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
            catch (Exception e)
            {
                // Runs unobserved on the thread pool: log it rather than lose it.
                Trace.WriteLine($"Task pipe connection failed: {e}");
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
                case "start":
                    return Ok(await _service.StartAsync(
                        request["command"]?.GetValue<string>() ?? throw new TaskServiceException("command is required."),
                        request["name"]?.GetValue<string>(),
                        request["waitSeconds"]?.GetValue<int>() ?? 3,
                        ct).ConfigureAwait(false));

                case "read":
                    return Ok(await _service.ReadAsync(
                        request["id"]?.GetValue<string>() ?? throw new TaskServiceException("id is required."),
                        request["since"]?.GetValue<long>(),
                        request["tailChars"]?.GetValue<int>(),
                        ct).ConfigureAwait(false));

                case "stop":
                    return Ok(await _service.StopAsync(
                        request["id"]?.GetValue<string>() ?? throw new TaskServiceException("id is required."),
                        ct).ConfigureAwait(false));

                case "list":
                    var all = await _service.ListAsync(ct).ConfigureAwait(false);
                    return new JsonObject
                    {
                        ["ok"] = true,
                        ["tasks"] = new JsonArray([.. all.Select(t => (JsonNode)Json(t))]),
                    };

                case "explore":
                    return Text(await Assist.ExploreAsync(
                        request["question"]?.GetValue<string>() ?? throw new TaskServiceException("question is required."),
                        ct).ConfigureAwait(false));

                case "digest":
                    return Text(await Assist.DigestAsync(
                        request["command"]?.GetValue<string>() ?? "",
                        request["output"]?.GetValue<string>() ?? throw new TaskServiceException("output is required."),
                        ct).ConfigureAwait(false));

                case "saved":
                    Assist.RecordSaved(request["before"]?.GetValue<long>() ?? 0, request["after"]?.GetValue<long>() ?? 0);
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
            Trace.WriteLine($"Task request failed: {e}");
            return new JsonObject { ["ok"] = false, ["error"] = e.Message };
        }
    }

    private ITaskAssist Assist =>
        _assist ?? throw new TaskServiceException("Codale has no background-task model for this; use the normal tools.");

    private static JsonObject Text(string? text) => new() { ["ok"] = true, ["text"] = text };

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
public sealed class TaskPipeClient(string pipeName, string token) : ITaskService, ITaskAssist
{
    /// <summary>
    /// The hard limit on one explore. Claude sessions Codale starts raise the CLI's 120 s
    /// background cut-over past this, so the agent waits for the answer itself.
    /// </summary>
    public static readonly TimeSpan ExploreTimeout = TimeSpan.FromMinutes(30);

    /// <summary>A hook holds the agent up while it waits, so a slow digest is given up on.</summary>
    public static readonly TimeSpan DigestTimeout = TimeSpan.FromSeconds(25);

    private readonly SemaphoreSlim _gate = new(1, 1);

    public async Task<string> ExploreAsync(string question, CancellationToken ct) =>
        TextOf(await CallAsync(new JsonObject { ["op"] = "explore", ["question"] = question }, ct, ExploreTimeout, exclusive: false)
            .ConfigureAwait(false)) ?? "";

    public async Task<string?> DigestAsync(string command, string output, CancellationToken ct) =>
        TextOf(await CallAsync(
                new JsonObject { ["op"] = "digest", ["command"] = command, ["output"] = output },
                ct,
                DigestTimeout,
                exclusive: false)
            .ConfigureAwait(false));

    /// <remarks>Blocks for the 3 s the async form is bounded by; hosts that can await should call <see cref="RecordSavedAsync"/>.</remarks>
    public void RecordSaved(long beforeChars, long afterChars) =>
        Task.Run(() => RecordSavedAsync(beforeChars, afterChars, CancellationToken.None)).GetAwaiter().GetResult();

    /// <summary>Reports a saving. Never throws: a lost tally is no reason to fail a tool call.</summary>
    public async Task RecordSavedAsync(long beforeChars, long afterChars, CancellationToken ct)
    {
        try
        {
            await CallAsync(
                    new JsonObject { ["op"] = "saved", ["before"] = beforeChars, ["after"] = afterChars },
                    ct,
                    TimeSpan.FromSeconds(3),
                    exclusive: false)
                .ConfigureAwait(false);
        }
        catch (Exception e) when (e is TaskServiceException or IOException or OperationCanceledException or JsonException)
        {
        }
    }

    private static string? TextOf(JsonObject response) => response["text"]?.GetValue<string>();

    public Task<TaskSnapshot> StartAsync(string command, string? name, int waitSeconds, CancellationToken ct) =>
        CallOneAsync(new JsonObject { ["op"] = "start", ["command"] = command, ["name"] = name, ["waitSeconds"] = waitSeconds }, ct);

    public Task<TaskSnapshot> ReadAsync(string id, long? since, int? tailChars, CancellationToken ct) =>
        CallOneAsync(new JsonObject { ["op"] = "read", ["id"] = id, ["since"] = since, ["tailChars"] = tailChars }, ct);

    public Task<TaskSnapshot> StopAsync(string id, CancellationToken ct) =>
        CallOneAsync(new JsonObject { ["op"] = "stop", ["id"] = id }, ct);

    public async Task<IReadOnlyList<TaskSnapshot>> ListAsync(CancellationToken ct)
    {
        var response = await CallAsync(new JsonObject { ["op"] = "list" }, ct).ConfigureAwait(false);
        return [.. response["tasks"]!.AsArray().Select(n => Parse(n!.AsObject()))];
    }

    private async Task<TaskSnapshot> CallOneAsync(JsonObject request, CancellationToken ct) =>
        Parse(await CallAsync(request, ct).ConfigureAwait(false));

    private Task<JsonObject> CallAsync(JsonObject request, CancellationToken ct) =>
        CallAsync(request, ct, timeout: null, exclusive: true);

    /// <param name="exclusive">
    /// Task calls take turns. A long model call runs on its own connection instead, so it
    /// never holds a read_task up for a minute.
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
                throw new TaskServiceException("Codale is not reachable (its task host is gone). Is the chat still open?");
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
