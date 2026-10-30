using System.Diagnostics;
using System.Text;
using System.Text.Json;
using System.Threading.Channels;

using Codale.Agents.Claude;
using Codale.Core.Agents;
using Codale.Core.Processes;

namespace Codale.Agents.Codex;

/// <summary>
/// Drives one <c>codex app-server</c> child process over its JSON-RPC-over-stdio
/// protocol and projects it onto <see cref="IAgentSession"/>. Threads become sessions,
/// turns become turns, thread items become tool calls and messages, and the app
/// server's approval requests become <see cref="ApprovalRequested"/>.
/// </summary>
public sealed class CodexAgentSession : IAgentSession
{
    private static readonly JsonSerializerOptions WireJson = new(JsonSerializerDefaults.Web);

    private readonly CodexSessionOptions _options;
    private readonly Channel<AgentEvent> _events =
        Channel.CreateUnbounded<AgentEvent>(new UnboundedChannelOptions { SingleReader = false, SingleWriter = true });

    /// <summary>Guards stdin: the UI thread and the read loop both write frames.</summary>
    private readonly SemaphoreSlim _stdinLock = new(1, 1);

    /// <summary>Open once for the session; replaces re-opening the raw log per streamed line.</summary>
    private StreamWriter? _rawLog;
    private long _rawLogBytes;

    /// <summary>A raw log past this size is rotated to <c>.1</c> so it cannot grow without bound.</summary>
    private const long RawLogMaxBytes = 32 * 1024 * 1024;

    /// <summary>Our JSON-RPC requests awaiting their response, keyed by the id we chose.</summary>
    private readonly Dictionary<int, TaskCompletionSource<JsonElement>> _pendingRequests = [];

    /// <summary>The app server's requests awaiting our answer, keyed by its id: pending approvals.</summary>
    private readonly Dictionary<int, PendingApproval> _pendingApprovals = [];

    private Process? _process;
    private Task? _readLoop;
    private Task? _errorLoop;
    private int _requestCounter;
    private readonly List<string> _stderr = [];

    /// <summary>The mode and model/effort applied to the next turn; codex takes them per turn, not mid-turn.</summary>
    private string _permissionMode = CodexSessionOptions.ClampPermissionMode("");
    private string? _liveModel;
    private string? _liveEffort;

    private string? _threadId;
    private string? _currentTurnId;

    /// <summary>The model codex reported for the thread; null until it says, so the CLI default is never guessed.</summary>
    private string? _threadModel;

    private sealed record PendingApproval(string ThreadId, string Method, string ToolName, JsonElement Input);

    public CodexAgentSession(CodexSessionOptions options)
    {
        _options = options;
        _permissionMode = CodexSessionOptions.ClampPermissionMode(options.PermissionMode ?? "");
        _liveModel = options.Model;
        _liveEffort = CodexSessionOptions.ClampEffort(options.Effort);
        SessionId = options.ResumeSessionId;
    }

    public string? SessionId { get; private set; }

    /// <summary>Test seam: the thread notifications are judged against, without a running app-server.</summary>
    internal void AdoptThread(string threadId) => _threadId = threadId;

    public ChannelReader<AgentEvent> Events => _events.Reader;

    public async Task StartAsync(CancellationToken ct = default)
    {
        if (_process is not null)
        {
            throw new InvalidOperationException("Session already started.");
        }

        var startInfo = new ProcessStartInfo
        {
            FileName = _options.Executable,
            WorkingDirectory = _options.WorkingDirectory,
            RedirectStandardInput = true,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            UseShellExecute = false,
            CreateNoWindow = true,

            // The protocol is UTF-8 JSON. A BOM would corrupt the first frame.
            StandardInputEncoding = new UTF8Encoding(encoderShouldEmitUTF8Identifier: false),
            StandardOutputEncoding = new UTF8Encoding(encoderShouldEmitUTF8Identifier: false),
            StandardErrorEncoding = new UTF8Encoding(encoderShouldEmitUTF8Identifier: false),
        };

        foreach (var arg in _options.BuildArguments())
        {
            startInfo.ArgumentList.Add(arg);
        }

        startInfo.EnvironmentVariables[CodaleAccount.CodexVariable] = CodaleAccount.CodexHome;

        if (_options.Environment is { } environment)
        {
            foreach (var (name, value) in environment)
            {
                startInfo.EnvironmentVariables[name] = value;
            }
        }

        _process = await ProcessUtil.StartAsync(startInfo).ConfigureAwait(false)
            ?? throw new InvalidOperationException($"Failed to start '{_options.Executable}'.");

        _readLoop = Task.Run(() => ReadLoopAsync(_process), CancellationToken.None);
        _errorLoop = Task.Run(() => ReadStderrAsync(_process), CancellationToken.None);

        using var startTimeout = CancellationTokenSource.CreateLinkedTokenSource(ct);
        startTimeout.CancelAfter(TimeSpan.FromSeconds(30));

        try
        {
            // Declare ourselves as the host, then open the conversation. The CLI gates
            // everything else on the initialize handshake.
            await SendRequestAsync("initialize", request =>
                request["clientInfo"] = new { name = "codale", title = "Codale", version = typeof(CodexAgentSession).Assembly.GetName().Version?.ToString(3) ?? "0.0.0" },
                startTimeout.Token).ConfigureAwait(false);
            await WriteNotificationAsync("initialized", new { }, startTimeout.Token).ConfigureAwait(false);

            var threadParams = new Dictionary<string, object?> { ["cwd"] = _options.WorkingDirectory };
            if (_options.CustomProvider is not null)
            {
                threadParams["modelProvider"] = "custom";
            }
            if (_liveModel is { Length: > 0 } model)
            {
                threadParams["model"] = model;
            }

            var (approvalPolicy, sandbox) = CodexSessionOptions.PermissionPolicy(_permissionMode);
            threadParams["approvalPolicy"] = approvalPolicy;
            threadParams["sandboxPolicy"] = CodexSessionOptions.SandboxPolicy(sandbox, ExtraFolders());

            if (_options.SystemPromptAppend is { Length: > 0 } instructions)
            {
                threadParams["developerInstructions"] = instructions;
            }

            var method = _options.ResumeSessionId is { Length: > 0 } resume ? "thread/resume" : "thread/start";
            if (_options.ResumeSessionId is { Length: > 0 } id)
            {
                threadParams["threadId"] = id;
            }

            var thread = await SendRequestAsync(method, request => CopyParams(request, threadParams), startTimeout.Token)
                .ConfigureAwait(false);

            _threadId = thread.Prop("thread")?.Str("id") ?? thread.Str("id") ?? thread.Str("sessionId") ?? thread.Str("threadId");
            if (_threadId is not { Length: > 0 })
            {
                throw new InvalidOperationException("codex did not return a thread id; it may predate the app-server thread protocol.");
            }

            SessionId = _threadId;

            // The thread object echoes the model codex actually resolved (a forced pick,
            // a config default, or the CLI's own); when it does, the status bar shows
            // the real thing instead of the "codex default" placeholder.
            var threadBody = thread.Prop("thread") ?? thread;
            _threadModel = threadBody.Str("model") is { Length: > 0 } reported ? reported : null;

            await _events.Writer.WriteAsync(new SessionInitialized
            {
                Model = _threadModel ?? _liveModel ?? "CLI",
                WorkingDirectory = _options.WorkingDirectory,
                PermissionMode = _permissionMode,
                Version = thread.Prop("thread")?.Str("version") ?? thread.Str("version"),
                ContextWindow = _options.ContextWindow,
            }).ConfigureAwait(false);

            await ReadRateLimitsAsync(CancellationToken.None).ConfigureAwait(false);
        }
        catch (OperationCanceledException) when (!ct.IsCancellationRequested)
        {
            throw new TimeoutException(
                $"'{_options.Executable}' did not answer the app-server handshake within 30s. " +
                $"stderr: {string.Join(Environment.NewLine, StderrSnapshot().TakeLast(5))}");
        }
    }

    private static JsonElement? ParseObject(string? json)
    {
        try
        {
            return string.IsNullOrWhiteSpace(json) ? null : JsonDocument.Parse(json).RootElement.Clone();
        }
        catch (JsonException)
        {
            return null;
        }
    }

    /// <summary>Merges the given parameters into a JSON-RPC request object under construction.</summary>
    private static void CopyParams(Dictionary<string, object?> request, Dictionary<string, object?> parameters)
    {
        foreach (var (name, value) in parameters)
        {
            request[name] = value;
        }
    }

    public async Task SendAsync(UserTurn turn, CancellationToken ct = default)
    {
        var threadId = RequireThread();

        var input = new List<object>();

        // codex only knows its working directory; say when other folders are in play.
        var folders = ExtraFolders();
        var folderKey = string.Join('|', folders);
        if (folderKey != _announcedFolders && (folders.Count > 0 || _announcedFolders.Length > 0))
        {
            _announcedFolders = folderKey;
            input.Add(new
            {
                type = "text",
                text = folders.Count > 0
                    ? $"[Workspace folders you can read and write besides the working directory: {string.Join("; ", folders)}]"
                    : "[The extra workspace folders mentioned earlier are no longer available.]",
            });
        }

        if (turn.ProviderText.Length > 0)
        {
            input.Add(new { type = "text", text = turn.ProviderText });
        }

        foreach (var attachment in turn.Attachments)
        {
            switch (attachment.Kind)
            {
                case TurnAttachmentKind.Image:
                    input.Add(new { type = "localImage", path = attachment.Path });
                    break;
                default:
                    // codex reads files from the workspace itself; hand it the path as text.
                    input.Add(new { type = "text", text = $"[Attached file: {attachment.Path}]" });
                    break;
            }
        }

        var parameters = new Dictionary<string, object?> { ["threadId"] = threadId, ["input"] = input };
        ApplyTurnOverrides(parameters, turn.AskOnly);

        var started = await SendRequestAsync("turn/start", request => CopyParams(request, parameters), ct)
            .ConfigureAwait(false);
        _currentTurnId = started.Prop("turn")?.Str("id") ?? started.Str("turnId") ?? started.Str("id");
    }

    /// <summary>Folds the live model, effort and permission mode into a turn's parameters.</summary>
    private void ApplyTurnOverrides(Dictionary<string, object?> parameters, bool askOnly)
    {
        if (_liveModel is { Length: > 0 } model)
        {
            parameters["model"] = model;
        }

        if (_liveEffort is { Length: > 0 } effort)
        {
            parameters["effort"] = effort;
        }

        var (approvalPolicy, sandbox) = askOnly
            ? CodexSessionOptions.AskPolicy
            : CodexSessionOptions.PermissionPolicy(_permissionMode);
        parameters["approvalPolicy"] = approvalPolicy;
        parameters["sandboxPolicy"] = CodexSessionOptions.SandboxPolicy(sandbox, ExtraFolders());
    }

    private IReadOnlyList<string> ExtraFolders() => _options.AdditionalDirectoriesSource?.Invoke() ?? [];

    /// <summary>The folder set the thread was last told about, so a change is announced once.</summary>
    private string _announcedFolders = "";

    public async Task InterruptAsync(CancellationToken ct = default)
    {
        var threadId = RequireThread();
        var parameters = new Dictionary<string, object?> { ["threadId"] = threadId };
        if (_currentTurnId is { Length: > 0 } turnId)
        {
            parameters["turnId"] = turnId;
        }

        await SendRequestAsync("turn/interrupt", request => CopyParams(request, parameters), ct).ConfigureAwait(false);
    }

    /// <summary>
    /// The app-server has no slash commands - a "/compact" prompt would be answered as
    /// chat - so compaction is its own request. It runs as a turn whose
    /// <c>contextCompaction</c> item marks the boundary. False on a CLI without it.
    /// </summary>
    public async Task<bool> TryCompactAsync(CancellationToken ct = default)
    {
        var threadId = RequireThread();
        try
        {
            await SendRequestAsync("thread/compact/start", request => request["threadId"] = threadId, ct).ConfigureAwait(false);
            return true;
        }
        catch (InvalidOperationException)
        {
            return false;
        }
    }

    /// <summary>
    /// Codex takes the permission mode per turn rather than mid-turn: recording it here
    /// means the very next turn (including a plan-mode turn raised while one runs, which
    /// the host restarts anyway) runs under it.
    /// </summary>
    public Task<bool> TrySetPermissionModeAsync(string mode, CancellationToken ct = default)
    {
        _permissionMode = CodexSessionOptions.ClampPermissionMode(mode);
        return Task.FromResult(true);
    }

    /// <summary>
    /// Same per-turn story for model and effort: recorded and applied from the next
    /// turn/start on. Never false, so the host does not restart the session for a
    /// change codex can carry across turns.
    /// </summary>
    public Task<bool> TrySetModelEffortAsync(string? model, string? effort, CancellationToken ct = default)
    {
        if (model is not null)
        {
            _liveModel = model.Length > 0 ? model : null;
        }

        if (effort is not null)
        {
            _liveEffort = CodexSessionOptions.ClampEffort(effort);
        }

        return Task.FromResult(true);
    }

    public async Task RespondToApprovalAsync(string requestId, ApprovalDecision decision, CancellationToken ct = default)
    {
        if (!int.TryParse(requestId, out var id) || !_pendingApprovals.Remove(id, out var pending))
        {
            return;
        }

        if (pending.Method.Contains("requestUserInput", StringComparison.Ordinal) || pending.ToolName == "AskUserQuestion")
        {
            var response = BuildRequestUserInputResult(pending.Input, decision);
            await WriteResponseAsync(id, response, ct).ConfigureAwait(false);
            return;
        }

        if (pending.Method.Contains("permissions", StringComparison.Ordinal))
        {
            if (decision.Behavior == ApprovalBehavior.Allow)
            {
                var permissions = pending.Input.Prop("requestedPermissions") ?? pending.Input.Prop("permissions");
                var result = new { permissions = (object?)permissions ?? new { }, scope = "turn" };
                await WriteResponseAsync(id, result, ct).ConfigureAwait(false);
            }
            else
            {
                await WriteErrorResponseAsync(id, -32000, decision.Message ?? "Denied by the user.", ct).ConfigureAwait(false);
            }
            return;
        }

        var approvalResult = decision.Behavior == ApprovalBehavior.Allow
            ? (object)new { decision = "accept" }
            : new { decision = "decline" };
        await WriteResponseAsync(id, approvalResult, ct).ConfigureAwait(false);
    }

    /// <summary>
    /// Builds the result payload for an <c>item/tool/requestUserInput</c> server request matching
    /// Codex app-server's <c>ToolRequestUserInputResponse</c> schema: <c>{ answers: { [id]: { answers: [string] } } }</c>.
    /// </summary>
    internal static object BuildRequestUserInputResult(JsonElement originalInput, ApprovalDecision decision)
    {
        if (decision.Behavior != ApprovalBehavior.Allow ||
            decision.UpdatedInput is not { ValueKind: JsonValueKind.Object } updatedInput ||
            updatedInput.Prop("answers") is not { ValueKind: JsonValueKind.Object } userAnswers)
        {
            return new { answers = new Dictionary<string, object>() };
        }

        var codexAnswers = new Dictionary<string, object>();
        var questionsArray = originalInput.Prop("questions");

        if (questionsArray is { ValueKind: JsonValueKind.Array } array)
        {
            foreach (var q in array.EnumerateArray())
            {
                var id = q.Str("id") ?? q.Str("question") ?? "";
                var text = q.Str("question") ?? q.Str("title") ?? id;

                // Answers may be keyed by question ID or question text
                var answerElement = userAnswers.Prop(id) ?? userAnswers.Prop(text);
                if (answerElement is { } answer)
                {
                    var list = new List<string>();
                    if (answer.ValueKind == JsonValueKind.Array)
                    {
                        foreach (var item in answer.EnumerateArray())
                        {
                            if (item.GetString() is { } s)
                            {
                                list.Add(s);
                            }
                        }
                    }
                    else if (answer.ValueKind == JsonValueKind.String && answer.GetString() is { } single)
                    {
                        list.Add(single);
                    }

                    codexAnswers[id] = new { answers = list };
                }
            }
        }
        else
        {
            foreach (var prop in userAnswers.EnumerateObject())
            {
                var list = new List<string>();
                if (prop.Value.ValueKind == JsonValueKind.Array)
                {
                    foreach (var item in prop.Value.EnumerateArray())
                    {
                        if (item.GetString() is { } s)
                        {
                            list.Add(s);
                        }
                    }
                }
                else if (prop.Value.ValueKind == JsonValueKind.String && prop.Value.GetString() is { } single)
                {
                    list.Add(single);
                }

                codexAnswers[prop.Name] = new { answers = list };
            }
        }

        return new { answers = codexAnswers };
    }

    /// <summary>
    /// The codex model catalogue, read live from the app server's <c>model/list</c> so
    /// the picker shows what this account can actually run (the catalogue rotates; the
    /// documented list below is only the stand-in when the read fails).
    /// </summary>
    public async Task<IReadOnlyList<AgentModelInfo>> ListModelsAsync(CancellationToken ct = default)
    {
        try
        {
            var models = new List<AgentModelInfo>();
            string? cursor = null;
            do
            {
                var result = await SendRequestAsync("model/list", request =>
                {
                    if (cursor is { Length: > 0 } page)
                    {
                        request["cursor"] = page;
                    }
                }, ct).ConfigureAwait(false);

                if (result.Prop("data") is { ValueKind: JsonValueKind.Array } page)
                {
                    foreach (var model in page.EnumerateArray())
                    {
                        if (model.Bool("hidden") || model.Str("id") is not { Length: > 0 } id)
                        {
                            continue;
                        }

                        var efforts = model.Prop("supportedReasoningEfforts") is { ValueKind: JsonValueKind.Array } effortList
                            ? effortList.EnumerateArray()
                                .Select(effort => effort.Str("reasoningEffort") ?? "")
                                .Where(effort => effort.Length > 0)
                                .ToList()
                            : [];
                        models.Add(new AgentModelInfo
                        {
                            Id = id,
                            DisplayName = model.Str("displayName") ?? id,
                            Description = model.Str("description"),
                            IsDefault = model.Bool("isDefault"),
                            DefaultEffort = model.Str("defaultReasoningEffort"),
                            SupportedEfforts = efforts,
                        });
                    }
                }

                cursor = result.Str("nextCursor");
            }
            while (cursor is { Length: > 0 });

            if (models.Count > 0)
            {
                return models;
            }
        }
        catch (Exception ex) when (ex is InvalidOperationException or TimeoutException or OperationCanceledException)
        {
            // No catalogue behind this endpoint (BYOK, older CLI): the picker stays on "CLI".
        }

        return [];
    }

    private string RequireThread() => _threadId is { Length: > 0 } id
        ? id
        : throw new InvalidOperationException("Session not started.");

    private string[] StderrSnapshot()
    {
        lock (_stderr)
        {
            return [.. _stderr];
        }
    }

    /// <summary>Appends one line to the raw log, rotating it once it passes <see cref="RawLogMaxBytes"/>. Logging never fails the session.</summary>
    private async Task AppendRawLogAsync(string path, string line)
    {
        try
        {
            if (_rawLog is not null && _rawLogBytes > RawLogMaxBytes)
            {
                await _rawLog.DisposeAsync().ConfigureAwait(false);
                _rawLog = null;
                File.Move(path, path + ".1", overwrite: true);
            }

            if (_rawLog is null)
            {
                var stream = new FileStream(path, FileMode.Append, FileAccess.Write, FileShare.Read);
                _rawLogBytes = stream.Length;
                _rawLog = new StreamWriter(stream, new UTF8Encoding(false)) { AutoFlush = true };
            }

            await _rawLog.WriteLineAsync(line).ConfigureAwait(false);
            _rawLogBytes += line.Length + 1;
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
        }
    }

    private async Task WriteMessageAsync(object message, CancellationToken ct)
    {
        var process = _process ?? throw new InvalidOperationException("Session not started.");
        var json = JsonSerializer.Serialize(message, WireJson);

        await _stdinLock.WaitAsync(ct).ConfigureAwait(false);
        try
        {
            await process.StandardInput.WriteAsync(json.AsMemory(), ct).ConfigureAwait(false);
            await process.StandardInput.WriteAsync("\n".AsMemory(), ct).ConfigureAwait(false);
            await process.StandardInput.FlushAsync(ct).ConfigureAwait(false);
        }
        finally
        {
            _stdinLock.Release();
        }
    }

    private async Task<JsonElement> SendRequestAsync(string method, Action<Dictionary<string, object?>> configure, CancellationToken ct)
    {
        var id = Interlocked.Increment(ref _requestCounter);
        var parameters = new Dictionary<string, object?>();
        configure(parameters);

        var completion = new TaskCompletionSource<JsonElement>(TaskCreationOptions.RunContinuationsAsynchronously);
        lock (_pendingRequests)
        {
            _pendingRequests[id] = completion;
        }

        try
        {
            await WriteMessageAsync(new { jsonrpc = "2.0", id, method, @params = parameters }, ct).ConfigureAwait(false);
            return await completion.Task.WaitAsync(ct).ConfigureAwait(false);
        }
        catch (OperationCanceledException) when (!ct.IsCancellationRequested)
        {
            if (_errorLoop is { } errorLoop)
            {
                await Task.WhenAny(errorLoop, Task.Delay(500, CancellationToken.None)).ConfigureAwait(false);
            }

            var stderr = string.Join(Environment.NewLine, StderrSnapshot().TakeLast(8));
            throw new TimeoutException(
                $"codex did not answer the '{method}' request." +
                (stderr.Length > 0 ? $" It exited with: {stderr}" : " It exited without any error output."));
        }
        finally
        {
            lock (_pendingRequests)
            {
                _pendingRequests.Remove(id);
            }
        }
    }

    private Task WriteNotificationAsync(string method, object parameters, CancellationToken ct) =>
        WriteMessageAsync(new { jsonrpc = "2.0", method, @params = parameters }, ct);

    /// <summary>Answers a server-initiated request (an approval). Exposed shape pinned by tests.</summary>
    private Task WriteResponseAsync(int id, object result, CancellationToken ct) =>
        WriteMessageAsync(new { jsonrpc = "2.0", id, result }, ct);

    /// <summary>Answers a server-initiated request with a JSON-RPC error.</summary>
    private Task WriteErrorResponseAsync(int id, int code, string message, CancellationToken ct) =>
        WriteMessageAsync(new { jsonrpc = "2.0", id, error = new { code, message } }, ct);

    private async Task ReadLoopAsync(Process process)
    {
        try
        {
            while (await process.StandardOutput.ReadLineAsync().ConfigureAwait(false) is { } line)
            {
                if (_options.RawLogPath is { Length: > 0 } rawLog)
                {
                    await AppendRawLogAsync(rawLog, line).ConfigureAwait(false);
                }

                if (line.Length == 0)
                {
                    continue;
                }

                JsonElement message;
                try
                {
                    message = JsonDocument.Parse(line).RootElement.Clone();
                }
                catch (JsonException)
                {
                    continue;
                }

                if (message.Prop("method") is { } methodElement)
                {
                    var method = methodElement.GetString() ?? "";
                    if (message.ValueKind == JsonValueKind.Object && message.TryGetProperty("id", out var serverRequestId))
                    {
                        await HandleServerRequestAsync(serverRequestId, method, message).ConfigureAwait(false);
                    }
                    else
                    {
                        await HandleNotificationAsync(method, message).ConfigureAwait(false);
                    }

                    continue;
                }

                HandleResponse(message);
            }

            await process.WaitForExitAsync().ConfigureAwait(false);
            await _events.Writer.WriteAsync(
                new SessionEnded(process.ExitCode, StderrSnapshot() is { Length: > 0 } stderr ? string.Join(Environment.NewLine, stderr) : null))
                .ConfigureAwait(false);
        }
        catch (Exception ex)
        {
            await _events.Writer.WriteAsync(new SessionEnded(-1, ex.Message)).ConfigureAwait(false);
        }
        finally
        {
            lock (_pendingRequests)
            {
                foreach (var completion in _pendingRequests.Values)
                {
                    completion.TrySetCanceled();
                }

                _pendingRequests.Clear();
            }

            _events.Writer.TryComplete();
        }
    }

    /// <summary>Routes our requests' responses; a JSON-RPC error rejects with <see cref="InvalidOperationException"/>.</summary>
    private void HandleResponse(JsonElement message)
    {
        if (message.Int("id") is { } id)
        {
            lock (_pendingRequests)
            {
                if (_pendingRequests.Remove(id, out var completion))
                {
                    if (message.Prop("error") is { } error)
                    {
                        completion.TrySetException(new InvalidOperationException(
                            $"codex rejected the request: {error.Str("message") ?? error.GetRawText()}"));
                    }
                    else
                    {
                        completion.TrySetResult(message.Prop("result")?.Clone() ?? default);
                    }
                }
            }
        }
    }

    /// <summary>
    /// A server-initiated request. Approvals and input requests become <see cref="ApprovalRequested"/>;
    /// anything else is declined with a JSON-RPC error so the server never blocks on us.
    /// </summary>
    private async Task HandleServerRequestAsync(JsonElement id, string method, JsonElement message)
    {
        if (message.Prop("params") is not { } parameters || !IsSupportedServerRequest(method))
        {
            await WriteErrorResponseAsync(id.GetInt32(), -32601, $"Codale does not support the '{method}' server request.", CancellationToken.None)
                .ConfigureAwait(false);
            return;
        }

        var threadId = parameters.Str("threadId") ?? _threadId ?? "";
        var item = parameters.Prop("item");
        var (toolName, input) = DescribeApproval(method, parameters, item);

        var approvalId = id.GetInt32();
        _pendingApprovals[approvalId] = new PendingApproval(threadId, method, toolName, input);
        await _events.Writer.WriteAsync(new ApprovalRequested
        {
            RequestId = approvalId.ToString(System.Globalization.CultureInfo.InvariantCulture),
            ToolName = toolName,
            DisplayName = toolName,
            // The item the approval is about, so the question lands on its transcript row.
            ToolUseId = parameters.Str("itemId") ?? item?.Str("id"),
            Input = input,
        }).ConfigureAwait(false);
    }

    private static bool IsSupportedServerRequest(string method) =>
        method.Contains("requestApproval", StringComparison.Ordinal) ||
        method.Contains("requestUserInput", StringComparison.Ordinal) ||
        method is "execCommandApproval" or "applyPatchApproval" or "mcpServer/elicitation/request";

    /// <summary>
    /// The tool call an approval or input request is about: AskUserQuestion for user prompts,
    /// mcp__{server}__{tool} for MCP tools, Bash for exec, Edit for patch/fileChange, and Permissions.
    /// </summary>
    private static (string ToolName, JsonElement Input) DescribeApproval(string method, JsonElement parameters, JsonElement? item)
    {
        if (method.Contains("requestUserInput", StringComparison.Ordinal) || parameters.Prop("questions") is not null)
        {
            return ("AskUserQuestion", parameters);
        }

        var source = item ?? parameters;
        var type = source.Str("type") ?? "commandExecution";

        if (string.Equals(type, "mcpToolCall", StringComparison.OrdinalIgnoreCase) ||
            source.Str("server") is { Length: > 0 } || parameters.Str("server") is { Length: > 0 })
        {
            var server = source.Str("server") ?? parameters.Str("server") ?? "";
            var tool = source.Str("tool") ?? parameters.Str("tool") ?? "";
            var arguments = source.Prop("arguments") ?? parameters.Prop("arguments");
            var input = arguments is { ValueKind: JsonValueKind.Object } args
                ? args
                : JsonSerializer.SerializeToElement(new { server, tool }, WireJson);
            return ($"mcp__{server}__{tool}", input);
        }

        var command = source.Prop("command") ?? parameters.Prop("command");
        if (command is { ValueKind: JsonValueKind.Array or JsonValueKind.String })
        {
            var text = command.Value.ValueKind == JsonValueKind.Array
                ? string.Join(' ', command.Value.EnumerateArray().Select(part => part.GetString() ?? ""))
                : command.Value.GetString() ?? "";
            var input = new Dictionary<string, object?> { ["command"] = text };
            var cwd = source.Str("cwd") ?? parameters.Str("cwd");
            if (cwd is { Length: > 0 })
            {
                input["cwd"] = cwd;
            }

            return ("Bash", JsonSerializer.SerializeToElement(input, WireJson));
        }

        var changes = source.Prop("changes") ?? source.Prop("fileChanges") ?? parameters.Prop("fileChanges");
        if (changes is { ValueKind: JsonValueKind.Object } changesObj)
        {
            var paths = changesObj.EnumerateObject().Select(property => property.Name).ToList();
            return ("Edit", JsonSerializer.SerializeToElement(new { paths }, WireJson));
        }

        if (method.Contains("permissions", StringComparison.Ordinal))
        {
            return ("Permissions", parameters);
        }

        return (type, JsonSerializer.SerializeToElement(new { type }, WireJson));
    }

    /// <summary>Projects one app-server notification onto the normalised event stream.</summary>
    internal async Task HandleNotificationAsync(string method, JsonElement message)
    {
        var parameters = message.Prop("params") ?? message;

        if (ForeignThreadOf(parameters) is { } foreign)
        {
            await ReportSubagentThreadAsync(method, foreign, parameters).ConfigureAwait(false);
            return;
        }

        switch (method)
        {
            case "item/agentMessage/delta":
                if (parameters.Str("delta") is { Length: > 0 } delta)
                {
                    await _events.Writer.WriteAsync(new AssistantTextDelta(delta)).ConfigureAwait(false);
                }

                return;

            case "item/reasoning/summaryTextDelta":
            case "item/reasoning/textDelta":
                if (parameters.Str("delta") is { Length: > 0 } thinking)
                {
                    await _events.Writer.WriteAsync(new AssistantThinkingDelta(thinking)).ConfigureAwait(false);
                }

                return;

            case "item/started":
            case "item/completed":
                await HandleItemAsync(method == "item/completed", parameters).ConfigureAwait(false);
                return;

            case "turn/started":
                _currentTurnId = parameters.Prop("turn")?.Str("id") ?? parameters.Str("turnId") ?? _currentTurnId;
                return;

            case "turn/completed":
                await HandleTurnCompletedAsync(parameters).ConfigureAwait(false);
                return;

            case "thread/tokenUsage/updated":
                // tokenUsage.last is the latest model call - what the window holds now;
                // .total sums every call of the thread, the running count BYOK shows.
                if (parameters.Prop("tokenUsage") is { } tokenUsage)
                {
                    if (tokenUsage.Prop("last") is { } last && ReadUsage(last) is { } known)
                    {
                        int? window = tokenUsage.Prop("modelContextWindow") is { ValueKind: JsonValueKind.Number } size
                            && size.TryGetInt32(out var reported) && reported > 0 ? reported : null;
                        await _events.Writer.WriteAsync(new UsageUpdated(known) { ContextWindow = window }).ConfigureAwait(false);
                    }

                    if (tokenUsage.Prop("total")?.Prop("totalTokens") is { ValueKind: JsonValueKind.Number } total
                        && total.TryGetInt64(out var totalTokens))
                    {
                        await _events.Writer.WriteAsync(new SessionTokensUpdated(totalTokens)).ConfigureAwait(false);
                    }
                }

                return;

            case "account/rateLimits/updated":
                if (parameters.Prop("rateLimits") is { } limitsUpdate)
                {
                    await PublishRateLimitsAsync(ParseRateLimits(limitsUpdate)).ConfigureAwait(false);
                }

                return;

            case "turn/diff/updated":
                // The workspace git panel refreshes on its own; the aggregated diff is redundant here.
                return;

            case "error":
                var errorText = parameters.Str("message") ?? parameters.GetRawText();
                await _events.Writer.WriteAsync(new AgentError(errorText)).ConfigureAwait(false);
                return;

            default:
                // Unknown notifications are protocol drift: kept out of the transcript, but
                // visible in the raw log this session writes when one is requested.
                return;
        }
    }

    /// <summary>Subagent threads codex spawned, by thread id: they are listed, never replayed into the transcript.</summary>
    private readonly HashSet<string> _subagentThreads = [];

    /// <summary>The id of another thread this notification belongs to (a subagent's), or null when it is the main thread's.</summary>
    private string? ForeignThreadOf(JsonElement parameters)
    {
        var id = parameters.Str("threadId") ?? parameters.Prop("thread")?.Str("id");
        return _threadId is { Length: > 0 } main && id is { Length: > 0 } && id != main ? id : null;
    }

    /// <summary>The turn each subagent thread is running, so a stop can interrupt it.</summary>
    private readonly Dictionary<string, string> _subagentTurns = [];

    /// <summary>
    /// A subagent runs on its own thread. Its work stays out of the transcript: it surfaces
    /// as one <c>Agent</c> tool call, the shape the panel already lists subagents by, plus a
    /// background task keyed by the thread id - so it stays listed and stoppable after the
    /// main turn ends - and its items are reported as that call's nested activity.
    /// </summary>
    private async Task ReportSubagentThreadAsync(string method, string threadId, JsonElement parameters)
    {
        if (_subagentThreads.Add(threadId))
        {
            var thread = parameters.Prop("thread");
            var title = thread?.Str("agentNickname") ?? thread?.Str("name") ?? thread?.Str("preview");
            var input = JsonSerializer.SerializeToElement(
                new { description = string.IsNullOrWhiteSpace(title) ? $"Subagent {_subagentThreads.Count}" : title.Trim() }, WireJson);
            await _events.Writer.WriteAsync(new ToolCallStarted { ToolUseId = threadId, ToolName = "Agent", Input = input })
                .ConfigureAwait(false);
            await _events.Writer.WriteAsync(new BackgroundTaskChanged(threadId, threadId, null)).ConfigureAwait(false);
        }

        switch (method)
        {
            case "turn/started":
                if ((parameters.Prop("turn")?.Str("id") ?? parameters.Str("turnId")) is { Length: > 0 } turnId)
                {
                    _subagentTurns[threadId] = turnId;
                }

                // Sent another message after it finished: it is working again.
                await _events.Writer.WriteAsync(new BackgroundTaskChanged(threadId, threadId, null)).ConfigureAwait(false);
                break;

            case "item/started":
            case "item/completed":
                await HandleItemAsync(method == "item/completed", parameters, parent: threadId).ConfigureAwait(false);
                break;

            case "turn/completed":
                _subagentTurns.Remove(threadId);
                var turn = parameters.Prop("turn") ?? parameters;
                var status = turn.Str("status") ?? "completed";

                // The task's status first: the host keeps a background task running until told it ended.
                await _events.Writer.WriteAsync(new BackgroundTaskChanged(threadId, threadId, status switch
                {
                    "completed" => "completed",
                    "interrupted" => "stopped",
                    _ => "failed",
                })).ConfigureAwait(false);
                await _events.Writer.WriteAsync(new ToolCallCompleted
                {
                    ToolUseId = threadId,
                    IsError = status is not ("completed" or "interrupted"),
                }).ConfigureAwait(false);
                break;
        }
    }

    /// <summary>Stops a subagent thread's running turn; false for an id that is no subagent of this session.</summary>
    public async Task<bool> CancelTaskAsync(string taskId, CancellationToken ct = default)
    {
        if (!_subagentThreads.Contains(taskId))
        {
            return false;
        }

        var parameters = new Dictionary<string, object?> { ["threadId"] = taskId };
        if (_subagentTurns.TryGetValue(taskId, out var turnId))
        {
            parameters["turnId"] = turnId;
        }

        try
        {
            await SendRequestAsync("turn/interrupt", request => CopyParams(request, parameters), ct).ConfigureAwait(false);
            return true;
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            return false;
        }
    }

    /// <summary>
    /// Writes an item's event; for a subagent thread's item it is marked as the subagent's
    /// (its tool calls and words go to its task log, never the main transcript).
    /// </summary>
    private ValueTask EmitAsync(string? parent, AgentEvent e) => _events.Writer.WriteAsync(parent is null ? e : e switch
    {
        ToolCallStarted started => started with { ParentToolUseId = parent },
        AssistantMessageCompleted message => message with { ParentToolUseId = parent },
        _ => e,
    });

    /// <summary>Projects a thread item's start or completion onto tool-call and message events.</summary>
    private async Task HandleItemAsync(bool completed, JsonElement parameters, string? parent = null)
    {
        var item = parameters.Prop("item") ?? parameters;
        var itemId = item.Str("id") ?? parameters.Str("itemId") ?? Guid.NewGuid().ToString("N");

        switch (item.Str("type"))
        {
            case "agentMessage":
                if (completed)
                {
                    await EmitAsync(parent, new AssistantMessageCompleted
                    {
                        MessageId = itemId,
                        Model = _threadModel ?? _liveModel ?? "CLI",
                        Text = item.Str("text") ?? "",
                    }).ConfigureAwait(false);
                }

                return;

            case "reasoning":
                return; // summaries arrive as deltas; nothing to add on completion

            case "contextCompaction":
                // Requested or automatic alike; the next tokenUsage update carries the new occupancy.
                if (parent is null)
                {
                    await _events.Writer.WriteAsync(completed
                        ? new ConversationCompacted(Trigger: null, PreTokens: null, PostTokens: null)
                        : new CliStatusChanged("compacting")).ConfigureAwait(false);
                }

                return;

            case "commandExecution":
                if (!completed)
                {
                    var command = item.Prop("command") is { ValueKind: JsonValueKind.Array } parts
                        ? string.Join(' ', parts.EnumerateArray().Select(part => part.GetString() ?? ""))
                        : item.Str("command") ?? "";
                    var input = JsonSerializer.SerializeToElement(new { command, cwd = item.Str("cwd") }, WireJson);
                    await EmitAsync(parent, new ToolCallStarted { ToolUseId = itemId, ToolName = "Bash", Input = input })
                        .ConfigureAwait(false);
                }
                else
                {
                    var output = item.Str("aggregatedOutput");
                    // exitCode is null when the command never ran (declined, or the sandbox refused it).
                    var exitCode = item.Int("exitCode") ?? 0;
                    var status = item.Str("status");
                    var declined = string.Equals(status, "declined", StringComparison.OrdinalIgnoreCase);
                    await EmitAsync(parent, new ToolCallCompleted
                    {
                        ToolUseId = itemId,
                        IsError = declined || exitCode != 0 || string.Equals(status, "failed", StringComparison.OrdinalIgnoreCase),
                        ResultText = declined && string.IsNullOrEmpty(output) ? "Command declined." : output,
                    }).ConfigureAwait(false);
                }

                return;

            case "fileChange":
                if (!completed)
                {
                    var paths = item.Prop("changes") is { ValueKind: JsonValueKind.Object } changes
                        ? changes.EnumerateObject().Select(property => property.Name).ToList()
                        : [];
                    var input = JsonSerializer.SerializeToElement(new { paths }, WireJson);
                    await EmitAsync(parent, new ToolCallStarted { ToolUseId = itemId, ToolName = "Edit", Input = input })
                        .ConfigureAwait(false);
                }
                else
                {
                    await EmitAsync(parent, new ToolCallCompleted
                    {
                        ToolUseId = itemId,
                        IsError = string.Equals(item.Str("status"), "failed", StringComparison.OrdinalIgnoreCase),
                    }).ConfigureAwait(false);
                }

                return;

            case "mcpToolCall":
                if (!completed)
                {
                    // Named as Claude names MCP tools (mcp__server__tool), with the call's own arguments as
                    // its input: the panel recognizes Codale's todos_set and artifact_add by exactly that.
                    var tool = item.Str("tool") ?? "";
                    var server = item.Str("server") ?? "";
                    var arguments = item.Prop("arguments") is { } raw && raw.ValueKind == JsonValueKind.String
                        ? ParseObject(raw.GetString())
                        : item.Prop("arguments");
                    var input = arguments is { ValueKind: JsonValueKind.Object } given
                        ? given
                        : JsonSerializer.SerializeToElement(new { tool, server }, WireJson);
                    await EmitAsync(parent, new ToolCallStarted { ToolUseId = itemId, ToolName = $"mcp__{server}__{tool}", Input = input })
                        .ConfigureAwait(false);
                }
                else
                {
                    await EmitAsync(parent, new ToolCallCompleted
                    {
                        ToolUseId = itemId,
                        IsError = string.Equals(item.Str("status"), "failed", StringComparison.OrdinalIgnoreCase),
                        ResultText = item.Str("result"),
                    }).ConfigureAwait(false);
                }

                return;

            case "userMessage":
                // A subagent's "user message" is the brief its parent wrote, not the reader's prompt.
                if (completed && parent is null)
                {
                    await _events.Writer.WriteAsync(new UserMessageRecorded(item.Str("text") ?? "")).ConfigureAwait(false);
                }

                return;

            default:
                if (!completed)
                {
                    await EmitAsync(parent, new CliMessageIgnored(item.Str("type") ?? "item", item.GetRawText()))
                        .ConfigureAwait(false);
                }

                return;
        }
    }

    private async Task HandleTurnCompletedAsync(JsonElement parameters)
    {
        var turn = parameters.Prop("turn") ?? parameters;
        var status = turn.Str("status") ?? parameters.Str("status") ?? "completed";
        var subtype = status switch
        {
            "completed" => "success",
            "interrupted" => "interrupted",
            _ => "error",
        };

        UsageSnapshot? usage = turn.Prop("usage") is { } rawUsage ? ReadUsage(rawUsage) : null;
        await _events.Writer.WriteAsync(new TurnCompleted { Subtype = subtype, Usage = usage }).ConfigureAwait(false);
        _currentTurnId = null;
    }

    /// <summary>Reads codex usage figures, tolerating snake_case and camelCase field names.</summary>
    private static UsageSnapshot? ReadUsage(JsonElement usage)
    {
        long Field(params string[] names) => names
            .Select(name => usage.Prop(name))
            .FirstOrDefault(value => value is { ValueKind: JsonValueKind.Number }) is { } number
            && number.TryGetInt64(out var parsed) ? parsed : 0;

        // Codex counts cached tokens inside the input total (OpenAI style); the snapshot
        // keeps them apart, as ContextTokens adds the two back together.
        var cached = Field("cached_input_tokens", "cachedInputTokens", "cacheReadInputTokens");
        var snapshot = new UsageSnapshot
        {
            InputTokens = (int)Math.Max(0, Field("input_tokens", "inputTokens", "preTokens") - cached),
            CacheReadInputTokens = (int)cached,
            OutputTokens = (int)Field("output_tokens", "outputTokens"),
        };
        return snapshot.InputTokens == 0 && snapshot.OutputTokens == 0 ? null : snapshot;
    }

    /// <summary>
    /// The latest account quota merged from <c>account/rateLimits/read</c> and the
    /// rolling <c>account/rateLimits/updated</c> notifications, so a sparse update
    /// never blanks a window the last read reported.
    /// </summary>
    private RateLimitSnapshot? _rateLimits;

    /// <summary>
    /// Fetches the subscription quota once after the handshake. BYOK endpoints and
    /// older CLIs have no account quota behind this method; failing it just leaves
    /// the status bar's usage indicator empty.
    /// </summary>
    private async Task ReadRateLimitsAsync(CancellationToken ct)
    {
        try
        {
            var result = await SendRequestAsync("account/rateLimits/read", _ => { }, ct).ConfigureAwait(false);
            if (result.Prop("rateLimits") is { } limits)
            {
                await PublishRateLimitsAsync(ParseRateLimits(limits)).ConfigureAwait(false);
            }
        }
        catch (Exception ex) when (ex is InvalidOperationException or TimeoutException or OperationCanceledException)
        {
        }
    }

    /// <summary>Folds a sparse rate-limit update into the last full read before publishing.</summary>
    private async Task PublishRateLimitsAsync(RateLimitSnapshot updated)
    {
        var previous = _rateLimits;
        var merged = new RateLimitSnapshot
        {
            Status = updated.Status ?? previous?.Status,
            FiveHourUtilization = updated.FiveHourUtilization ?? previous?.FiveHourUtilization,
            FiveHourResetsAt = updated.FiveHourResetsAt ?? previous?.FiveHourResetsAt,
            SevenDayUtilization = updated.SevenDayUtilization ?? previous?.SevenDayUtilization,
            SevenDayResetsAt = updated.SevenDayResetsAt ?? previous?.SevenDayResetsAt,
            LongWindowMins = updated.LongWindowMins ?? previous?.LongWindowMins,
        };
        _rateLimits = merged;
        await _events.Writer.WriteAsync(new RateLimitUpdated(merged)).ConfigureAwait(false);
    }

    /// <summary>
    /// Maps an app-server <c>RateLimitSnapshot</c> onto the normalised one. Codex names
    /// its windows primary/secondary with the duration in minutes; the five-hour window
    /// is anything at or under ten hours, the weekly one everything longer.
    /// </summary>
    internal static RateLimitSnapshot ParseRateLimits(JsonElement snapshot)
    {
        var limits = new RateLimitSnapshot();

        void Apply(JsonElement window)
        {
            if (window.Prop("usedPercent") is not { ValueKind: JsonValueKind.Number } used
                || !used.TryGetDouble(out var percent))
            {
                return;
            }

            var durationMins = window.Prop("windowDurationMins") is { ValueKind: JsonValueKind.Number } duration
                && duration.TryGetInt64(out var minutes) ? minutes : 300;
            DateTimeOffset? resetsAt = window.Prop("resetsAt") is { ValueKind: JsonValueKind.Number } resets
                && resets.TryGetInt64(out var seconds) ? DateTimeOffset.FromUnixTimeSeconds(seconds) : null;

            if (durationMins > 600)
            {
                limits = limits with { SevenDayUtilization = percent / 100.0, SevenDayResetsAt = resetsAt, LongWindowMins = durationMins };
            }
            else
            {
                limits = limits with { FiveHourUtilization = percent / 100.0, FiveHourResetsAt = resetsAt };
            }
        }

        if (snapshot.Prop("primary") is { } primary)
        {
            Apply(primary);
        }

        if (snapshot.Prop("secondary") is { } secondary)
        {
            Apply(secondary);
        }

        if (snapshot.Str("rateLimitReachedType") is { Length: > 0 } reached)
        {
            limits = limits with { Status = reached };
        }

        return limits;
    }

    private async Task ReadStderrAsync(Process process)
    {
        while (await process.StandardError.ReadLineAsync().ConfigureAwait(false) is { } line)
        {
            lock (_stderr)
            {
                _stderr.Add(line);
                if (_stderr.Count > 200)
                {
                    _stderr.RemoveAt(0);
                }
            }
        }
    }

    public async ValueTask DisposeAsync()
    {
        if (_process is { } process)
        {
            // Kill the tree: the agent spawns its own shells and MCP servers.
            ProcessUtil.TryKillTree(process);

            await Task.WhenAll(_readLoop ?? Task.CompletedTask, _errorLoop ?? Task.CompletedTask)
                .WaitAsync(TimeSpan.FromSeconds(5))
                .ConfigureAwait(ConfigureAwaitOptions.SuppressThrowing);

            process.Dispose();
            _process = null;
        }

        if (_rawLog is not null)
        {
            await _rawLog.DisposeAsync().ConfigureAwait(false);
            _rawLog = null;
        }

        _stdinLock.Dispose();
    }
}