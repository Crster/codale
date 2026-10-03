using System.Diagnostics;
using System.Text;
using System.Text.Json;
using System.Threading.Channels;

using Codale.Core.Agents;
using Codale.Core.Processes;

namespace Codale.Agents.Claude;

/// <summary>
/// Drives one <c>claude</c> child process over the stream-json protocol and
/// projects it onto <see cref="IAgentSession"/>.
/// </summary>
public sealed class ClaudeAgentSession : IAgentSession
{
    private static readonly JsonSerializerOptions WireJson = new(JsonSerializerDefaults.Web);

    private readonly ClaudeSessionOptions _options;
    private readonly ClaudeStreamParser _parser = new();
    private readonly Channel<AgentEvent> _events =
        Channel.CreateUnbounded<AgentEvent>(new UnboundedChannelOptions { SingleReader = false, SingleWriter = true });

    /// <summary>Guards stdin: the UI thread and the read loop both write control frames.</summary>
    private readonly SemaphoreSlim _stdinLock = new(1, 1);

    /// <summary>Open once for the session; replaces re-opening the raw log per streamed line.</summary>
    private StreamWriter? _rawLog;
    private long _rawLogBytes;

    /// <summary>A raw log past this size is rotated to <c>.1</c> (replacing the previous one) so it cannot grow without bound.</summary>
    private const long RawLogMaxBytes = 32 * 1024 * 1024;

    /// <summary>Control requests awaiting their response, keyed by the request_id we chose.</summary>
    private readonly Dictionary<string, TaskCompletionSource<JsonElement>> _pendingControl = [];

    private Process? _process;
    private Task? _readLoop;
    private Task? _errorLoop;
    private int _controlRequestCounter;
    private readonly List<string> _stderr = [];

    public ClaudeAgentSession(ClaudeSessionOptions options) => _options = options;

    public string? SessionId { get; private set; }

    public ChannelReader<AgentEvent> Events => _events.Reader;

    /// <summary>Slash commands the CLI reported during the initialize handshake.</summary>
    public IReadOnlyList<string> SlashCommands { get; private set; } = [];

    public async Task StartAsync(CancellationToken ct = default)
    {
        if (_process is not null)
        {
            throw new InvalidOperationException("Session already started.");
        }

        // A cmd.exe shim re-parses the command line, so a model name carrying `&` or `%` would run as a command.
        if (CliLocator.IsShim(_options.Executable) && _options.Model is { } model &&
            model.IndexOfAny(['&', '|', '<', '>', '^', '%', '"', '\r', '\n']) >= 0)
        {
            throw new InvalidOperationException($"The model name '{model}' cannot be passed through '{_options.Executable}'.");
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

        // The CLI gates suggestions behind a rollout flag and an interactive-terminal check that a
        // stream-json host fails; this env var is its explicit opt-in that skips both.
        startInfo.EnvironmentVariables["CLAUDE_CODE_ENABLE_PROMPT_SUGGESTION"] = _options.PromptSuggestions ? "1" : "0";

        // The CLI moves an MCP call still running after 120 s to the background, and the
        // explore tool may search for up to its own timeout: it waits for the answer
        // rather than handing the model a placeholder. A value the user set wins.
        if (string.IsNullOrEmpty(Environment.GetEnvironmentVariable("CLAUDE_CODE_MCP_AUTO_BACKGROUND_MS")))
        {
            startInfo.EnvironmentVariables["CLAUDE_CODE_MCP_AUTO_BACKGROUND_MS"] =
                ((long)(Codale.Core.Tasks.TaskPipeClient.ExploreTimeout + TimeSpan.FromMinutes(1)).TotalMilliseconds).ToString(System.Globalization.CultureInfo.InvariantCulture);
        }

        if (_options.Environment is { } environment)
        {
            foreach (var (name, value) in environment)
            {
                startInfo.EnvironmentVariables[name] = value;
            }
        }

        _process = Process.Start(startInfo)
            ?? throw new InvalidOperationException($"Failed to start '{_options.Executable}'.");

        _readLoop = Task.Run(() => ReadLoopAsync(_process), CancellationToken.None);
        _errorLoop = Task.Run(() => ReadStderrAsync(_process), CancellationToken.None);

        // Declare ourselves as the host. The CLI replies with the slash command list.
        using var initTimeout = CancellationTokenSource.CreateLinkedTokenSource(ct);
        initTimeout.CancelAfter(TimeSpan.FromSeconds(30));
        JsonElement init;

        try
        {
            init = await SendControlRequestAsync(
                "initialize",
                request =>
                {
                    if (_options.PromptSuggestions)
                    {
                        request["promptSuggestions"] = true;
                    }
                },
                initTimeout.Token).ConfigureAwait(false);
        }
        catch (OperationCanceledException) when (!ct.IsCancellationRequested)
        {
            throw new TimeoutException(
                $"'{_options.Executable}' did not answer the initialize handshake within 30s. " +
                $"stderr: {string.Join(Environment.NewLine, StderrSnapshot().TakeLast(5))}");
        }

        SlashCommands = init.Prop("commands") is { } cmds
            ? cmds.EnumerateArray().Select(c => c.Str("name")).OfType<string>().ToList()
            : [];
    }

    public Task SendAsync(UserTurn turn, CancellationToken ct = default) =>
        SendAsync(turn, File.ReadAllBytesAsync, ct);

    /// <summary>Sends the user frame, reading attachment bytes through <paramref name="readAllBytes"/> (injected for tests).</summary>
    internal async Task SendAsync(UserTurn turn, Func<string, CancellationToken, Task<byte[]>> readAllBytes, CancellationToken ct = default)
    {
        var payloads = new List<AttachmentPayload>(turn.Attachments.Count);
        foreach (var attachment in turn.Attachments)
        {
            var bytes = await readAllBytes(attachment.Path, ct).ConfigureAwait(false);
            payloads.Add(new AttachmentPayload(attachment, Convert.ToBase64String(bytes)));
        }

        await WriteLineAsync(BuildUserMessageFrame(turn.ProviderText, payloads), ct).ConfigureAwait(false);
    }

    public Task InterruptAsync(CancellationToken ct = default) =>
        SendControlRequestAsync("interrupt", _ => { }, ct);

    /// <summary>Stops a background task (a subagent that outlived its turn); false when the CLI refuses.</summary>
    public async Task<bool> CancelTaskAsync(string taskId, CancellationToken ct = default)
    {
        try
        {
            await SendControlRequestAsync("stop_task", request => request["task_id"] = taskId, ct).ConfigureAwait(false);
            return true;
        }
        catch (Exception ex) when (ex is InvalidOperationException or IOException)
        {
            return false;
        }
    }

    /// <summary>
    /// The stream-json control channel can switch the permission mode of a running
    /// session - the same switch the CLI's own plan-mode toggle uses - so a turn can
    /// run as plan without restarting the process.
    /// </summary>
    public async Task<bool> TrySetPermissionModeAsync(string mode, CancellationToken ct = default)
    {
        try
        {
            await SendControlRequestAsync("set_permission_mode", request => request["mode"] = mode, ct)
                .ConfigureAwait(false);
            return true;
        }
        catch (Exception ex) when (ex is InvalidOperationException or IOException)
        {
            // Older CLI without the subtype, or the process is gone; the caller phrases the request instead.
            return false;
        }
    }

    /// <summary>The efforts apply_flag_settings accepts; "max" exists only as a spawn flag.</summary>
    private static readonly HashSet<string> LiveSwitchEfforts = new(StringComparer.Ordinal)
    {
        "low", "medium", "high", "xhigh",
    };

    /// <summary>
    /// Live model and effort switches over the control channel: <c>set_model</c> for the
    /// model (omitting it resets to the CLI default), <c>apply_flag_settings</c> with an
    /// <c>effortLevel</c> for the effort - the same session-scoped toggle the CLI's own
    /// /model and /effort pickers use. Effort "max" (spawn-only) or an older CLI that
    /// rejects the subtypes returns false so the caller restarts instead.
    /// </summary>
    public async Task<bool> TrySetModelEffortAsync(string? model, string? effort, CancellationToken ct = default)
    {
        if (effort is { Length: > 0 } && !LiveSwitchEfforts.Contains(effort))
        {
            return false;
        }

        using var timeout = CancellationTokenSource.CreateLinkedTokenSource(ct);
        timeout.CancelAfter(TimeSpan.FromSeconds(15));

        try
        {
            await SendControlRequestAsync(id => BuildSetModelFrame(id, model), timeout.Token)
                .ConfigureAwait(false);

            if (effort is { Length: > 0 })
            {
                await SendControlRequestAsync(id => BuildApplyEffortFrame(id, effort), timeout.Token)
                    .ConfigureAwait(false);
            }

            return true;
        }
        catch (Exception ex) when (ex is InvalidOperationException or TimeoutException or IOException)
        {
            // Older CLI without the subtypes, or the process is gone; the caller restarts with new spawn arguments.
            return false;
        }
    }

    /// <summary>
    /// Builds the <c>set_model</c> control frame. An absent model resets to the session
    /// default - the CLI documents omitted, null and "default" as equivalent. Exposed
    /// for the regression test that pins the shape.
    /// </summary>
    public static object BuildSetModelFrame(string requestId, string? model) =>
        BuildControlRequestFrame(requestId, "set_model", request =>
        {
            if (model is { Length: > 0 } picked)
            {
                request["model"] = picked;
            }
        });

    /// <summary>
    /// Builds the <c>apply_flag_settings</c> frame that switches effort live - the
    /// session-scoped toggle behind the CLI's own /effort picker. Exposed for the
    /// regression test that pins the shape.
    /// </summary>
    public static object BuildApplyEffortFrame(string requestId, string effort) =>
        BuildControlRequestFrame(requestId, "apply_flag_settings", request =>
            request["settings"] = new Dictionary<string, object?> { ["effortLevel"] = effort });

    /// <summary>An attachment with its bytes already base64-encoded, ready for the frame builder.</summary>
    public readonly record struct AttachmentPayload(TurnAttachment Attachment, string Base64);

    /// <summary>
    /// Builds the <c>user</c> frame: the text block plus one base64 block per
    /// attachment - <c>image</c> for pictures, <c>document</c> for PDFs - in API
    /// content-block shape, which is what stream-json input forwards.
    /// Exposed for the regression test that pins the shape.
    /// </summary>
    public static object BuildUserMessageFrame(string text, IReadOnlyList<AttachmentPayload> payloads)
    {
        var content = new List<object>();

        if (text.Length > 0)
        {
            content.Add(new { type = "text", text });
        }

        foreach (var payload in payloads)
        {
            var mediaType = payload.Attachment.Kind switch
            {
                TurnAttachmentKind.Image => MediaTypes.TryGetValue(
                    System.IO.Path.GetExtension(payload.Attachment.Path).ToLowerInvariant(), out var image) ? image : "image/png",
                _ => "application/pdf",
            };

            content.Add(new
            {
                type = payload.Attachment.Kind == TurnAttachmentKind.Image ? "image" : "document",
                source = new { type = "base64", media_type = mediaType, data = payload.Base64 },
            });
        }

        return new
        {
            type = "user",
            message = new { role = "user", content },
        };
    }

    /// <summary>The image formats the API's base64 image source accepts.</summary>
    private static readonly Dictionary<string, string> MediaTypes = new(StringComparer.Ordinal)
    {
        [".png"] = "image/png",
        [".jpg"] = "image/jpeg",
        [".jpeg"] = "image/jpeg",
        [".gif"] = "image/gif",
        [".webp"] = "image/webp",
    };

    public Task RespondToApprovalAsync(string requestId, ApprovalDecision decision, CancellationToken ct = default) =>
        WriteLineAsync(BuildApprovalResponse(requestId, decision), ct);

    /// <summary>
    /// Builds the <c>control_response</c> frame that answers a <c>can_use_tool</c> request.
    /// </summary>
    /// <remarks>
    /// The CLI validates this shape strictly and wants exactly
    /// <c>{behavior:'allow', updatedInput?: object}</c> or <c>{behavior:'deny', message: string}</c>.
    /// Sending <c>updatedInput: null</c> is rejected — and the rejection surfaces only as a
    /// tool_result error inside the transcript, so the key must be omitted unless the user
    /// actually edited the tool input. Exposed for the regression test that pins this.
    /// </remarks>
    public static object BuildApprovalResponse(string requestId, ApprovalDecision decision)
    {
        var payload = new Dictionary<string, object?>();

        if (decision.Behavior == ApprovalBehavior.Allow)
        {
            payload["behavior"] = "allow";

            if (decision.UpdatedInput is { ValueKind: JsonValueKind.Object } updated)
            {
                payload["updatedInput"] = updated;
            }
        }
        else
        {
            payload["behavior"] = "deny";
            payload["message"] = decision.Message ?? "Denied by the user.";
        }

        return new
        {
            type = "control_response",
            response = new
            {
                subtype = "success",
                request_id = requestId,
                response = payload,
            },
        };
    }

    /// <summary>
    /// The request id and subtype of a CLI-to-host <c>control_request</c> this host has
    /// no handler for - anything but <c>can_use_tool</c> (hook callbacks, SDK MCP
    /// messages, MCP elicitations...) - or null for every other line.
    /// </summary>
    public static (string RequestId, string Subtype)? UnsupportedControlRequest(string line)
    {
        if (!line.Contains("\"control_request\"", StringComparison.Ordinal))
        {
            return null;
        }

        try
        {
            using var doc = JsonDocument.Parse(line);
            var root = doc.RootElement;

            if (root.Str("type") != "control_request" ||
                root.Str("request_id") is not { Length: > 0 } requestId ||
                root.Prop("request") is not { } request)
            {
                return null;
            }

            var subtype = request.Str("subtype") ?? "unknown";
            return subtype == "can_use_tool" ? null : (requestId, subtype);
        }
        catch (JsonException)
        {
            return null;
        }
    }

    /// <summary>The error <c>control_response</c> that tells the CLI a request is not supported here.</summary>
    public static object BuildUnsupportedControlResponse(string requestId, string subtype) => new
    {
        type = "control_response",
        response = new
        {
            subtype = "error",
            request_id = requestId,
            error = $"Codale does not support the '{subtype}' control request.",
        },
    };

    /// <summary>Serialises a frame exactly as it is written to the CLI's stdin.</summary>
    public static string SerializeFrame(object frame) => JsonSerializer.Serialize(frame, WireJson);

    /// <summary>
    /// Sends a <c>control_request</c> and awaits its <c>control_response</c>, returning the
    /// success payload (empty for subtypes the CLI merely acknowledges). Throws
    /// <see cref="InvalidOperationException"/> when the CLI answers with an error - e.g.
    /// an older build without the subtype.
    /// </summary>
    private Task<JsonElement> SendControlRequestAsync(
        string subtype, Action<Dictionary<string, object?>> configure, CancellationToken ct) =>
        SendControlRequestAsync(id => BuildControlRequestFrame(id, subtype, configure), ct);

    private async Task<JsonElement> SendControlRequestAsync(Func<string, object> buildFrame, CancellationToken ct)
    {
        var id = $"codale_{Interlocked.Increment(ref _controlRequestCounter)}";
        var completion = new TaskCompletionSource<JsonElement>(TaskCreationOptions.RunContinuationsAsynchronously);

        lock (_pendingControl)
        {
            _pendingControl[id] = completion;
        }

        try
        {
            await WriteLineAsync(buildFrame(id), ct).ConfigureAwait(false);

            return await completion.Task.WaitAsync(ct).ConfigureAwait(false);
        }
        catch (OperationCanceledException) when (!ct.IsCancellationRequested)
        {
            // The read loop failed the request (process died); surface it as a timeout.
            throw new TimeoutException($"claude did not answer the '{id}' control request.");
        }
        finally
        {
            lock (_pendingControl)
            {
                _pendingControl.Remove(id);
            }
        }
    }

    /// <summary>Builds a <c>control_request</c> envelope. Exposed for the regression tests that pin frame shapes.</summary>
    public static object BuildControlRequestFrame(string requestId, string subtype, Action<Dictionary<string, object?>>? configure = null)
    {
        var request = new Dictionary<string, object?> { ["subtype"] = subtype };
        configure?.Invoke(request);

        return new { type = "control_request", request_id = requestId, request };
    }

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

    private async Task WriteLineAsync(object frame, CancellationToken ct)
    {
        var process = _process ?? throw new InvalidOperationException("Session not started.");
        var json = JsonSerializer.Serialize(frame, WireJson);

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

    private async Task ReadLoopAsync(Process process)
    {
        try
        {
            while (await process.StandardOutput.ReadLineAsync().ConfigureAwait(false) is { } line)
            {
                if (_options.RawLogPath is { Length: > 0 } rawLog)
                {
                    // One open stream instead of re-opening the file per streamed line;
                    // flush per line so the tail a stall diagnosis needs is on disk.
                    await AppendRawLogAsync(rawLog, line).ConfigureAwait(false);
                }

                HandleControlResponse(line);

                if (UnsupportedControlRequest(line) is var (requestId, subtype))
                {
                    // The CLI blocks until every control request is answered. One the
                    // host has no handler for used to be dropped silently, which froze
                    // the turn for good; an error reply lets the CLI carry on.
                    await WriteLineAsync(BuildUnsupportedControlResponse(requestId, subtype), CancellationToken.None)
                        .ConfigureAwait(false);
                    await _events.Writer.WriteAsync(new AgentError(
                        $"The CLI asked Codale for \"{subtype}\", which Codale does not support yet; it was declined so the turn can continue."))
                        .ConfigureAwait(false);
                    continue;
                }

                foreach (var e in _parser.Parse(line))
                {
                    if (e.SessionId is { Length: > 0 } id)
                    {
                        SessionId = id;
                    }

                    await _events.Writer.WriteAsync(e).ConfigureAwait(false);
                }
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
            lock (_pendingControl)
            {
                foreach (var completion in _pendingControl.Values)
                {
                    completion.TrySetCanceled();
                }

                _pendingControl.Clear();
            }

            _events.Writer.TryComplete();
        }
    }

    /// <summary>
    /// Routes a <c>control_response</c> to its awaiting sender. The envelope carries
    /// <c>subtype:"success"</c> with the payload under <c>response</c>, or
    /// <c>subtype:"error"</c> with a human-readable <c>error</c> string.
    /// </summary>
    private void HandleControlResponse(string line)
    {
        if (!line.Contains("\"control_response\"", StringComparison.Ordinal))
        {
            return;
        }

        try
        {
            using var doc = JsonDocument.Parse(line);
            var root = doc.RootElement;

            if (root.Str("type") != "control_response" || root.Prop("response") is not { } response)
            {
                return;
            }

            var requestId = response.Str("request_id");

            lock (_pendingControl)
            {
                if (requestId is null || !_pendingControl.Remove(requestId, out var completion))
                {
                    return;
                }

                if (response.Str("subtype") == "error")
                {
                    completion.TrySetException(new InvalidOperationException(
                        $"claude rejected the control request: {response.Str("error") ?? "unknown error"}"));
                }
                else
                {
                    completion.TrySetResult(
                        response.Prop("response") is { } payload ? payload.Clone() : default);
                }
            }
        }
        catch (JsonException)
        {
            // Not our frame; the parser will classify it.
        }
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

    /// <summary>
    /// The current Claude family by full id - the same ids the CLI reports in its init
    /// frame, so the connected model matches its row. Sonnet 5.5 at medium effort is
    /// what the CLI starts with; haiku has no effort picker. Claude has no stable
    /// catalogue protocol, so this is the fallback when <c>list_models</c> is unavailable.
    /// </summary>
    public static readonly AgentModelInfo[] DocumentedModels =
    [
        new()
        {
            Id = "claude-sonnet-5-5", DisplayName = "Sonnet 5.5", Description = "Fast and capable for everyday tasks",
            IsDefault = true, DefaultEffort = "medium", SupportedEfforts = ["low", "medium", "high", "xhigh", "max"],
        },
        new()
        {
            Id = "claude-opus-5-5", DisplayName = "Opus 5.5", Description = "Most capable, for complex work",
            DefaultEffort = "medium", SupportedEfforts = ["low", "medium", "high", "xhigh", "max"],
        },
        new()
        {
            Id = "claude-fable-5-1", DisplayName = "Fable 5.1",
            DefaultEffort = "medium", SupportedEfforts = ["low", "medium", "high", "xhigh", "max"],
        },
        new()
        {
            Id = "claude-haiku-4-5-20251001", DisplayName = "Haiku 4.5", Description = "Fastest, for quick answers",
        },
    ];

    /// <summary>
    /// A readable name for a Claude model id: the catalogue's name when it is listed,
    /// else "claude-opus-5-5[1m]" style ids spelled out ("Opus 5.5 · 1M"). Aliases
    /// ("opus") and anything unrecognised come back capitalised as they are.
    /// </summary>
    public static string FriendlyModelName(string id)
    {
        if (DocumentedModels.FirstOrDefault(m => string.Equals(m.Id, id, StringComparison.OrdinalIgnoreCase)) is { } known)
        {
            return known.DisplayName;
        }

        var suffix = "";
        var bare = id;
        if (bare.EndsWith("[1m]", StringComparison.OrdinalIgnoreCase))
        {
            suffix = " · 1M";
            bare = bare[..^4];
        }

        var parts = bare.Split('-', StringSplitOptions.RemoveEmptyEntries).ToList();
        if (parts.Count >= 2 && string.Equals(parts[0], "claude", StringComparison.OrdinalIgnoreCase))
        {
            parts.RemoveAt(0);

            // A trailing date stamp (20251001) is a snapshot, not part of the name.
            if (parts.Count > 1 && parts[^1].Length == 8 && parts[^1].All(char.IsDigit))
            {
                parts.RemoveAt(parts.Count - 1);
            }

            var family = parts[0];
            var version = string.Join('.', parts.Skip(1));
            var name = char.ToUpperInvariant(family[0]) + family[1..];
            return (version.Length > 0 ? $"{name} {version}" : name) + suffix;
        }

        return id.Length > 0 ? char.ToUpperInvariant(id[0]) + id[1..] : id;
    }

    public async Task<IReadOnlyList<AgentModelInfo>> ListModelsAsync(CancellationToken ct = default)
    {
        // Newer CLIs answer a list_models control request with the same catalogue their
        // own /model picker shows - provider-backed custom models included. The response
        // shape is undocumented, so parsing is optional-tolerant and anything the CLI
        // cannot supply falls back to the documented aliases.
        try
        {
            using var timeout = CancellationTokenSource.CreateLinkedTokenSource(ct);
            timeout.CancelAfter(TimeSpan.FromSeconds(15));

            var catalog = await SendControlRequestAsync("list_models", _ => { }, timeout.Token)
                .ConfigureAwait(false);

            var listed = ParseModelCatalog(catalog);
            if (listed.Count > 0)
            {
                return listed;
            }
        }
        catch (Exception ex) when (ex is InvalidOperationException or TimeoutException or OperationCanceledException)
        {
            // Older CLI without the subtype; the static list below is the answer.
        }

        return DocumentedModels;
    }

    /// <summary>
    /// Reads <c>{models:[{id,name,...}]}</c>-shaped catalogues, tolerating field renames.
    /// Exposed for the regression test that pins the tolerant parsing.
    /// </summary>
    public static List<AgentModelInfo> ParseModelCatalog(JsonElement catalog)
    {
        var models = new List<AgentModelInfo>();

        if (catalog.ValueKind is not JsonValueKind.Object ||
            catalog.Prop("models") is not { ValueKind: JsonValueKind.Array } entries)
        {
            return models;
        }

        foreach (var entry in entries.EnumerateArray())
        {
            var id = entry.Str("id") ?? entry.Str("model");
            if (id is not { Length: > 0 })
            {
                continue;
            }

            var efforts = new List<string>();
            if ((entry.Prop("supportedReasoningEfforts") ?? entry.Prop("supportedEfforts"))
                is { ValueKind: JsonValueKind.Array } effortList)
            {
                efforts.AddRange(effortList.EnumerateArray()
                    .Select(e => e.Str("reasoningEffort") ?? e.Str("effort") ?? e.GetString())
                    .OfType<string>());
            }

            models.Add(new AgentModelInfo
            {
                Id = id,
                DisplayName = entry.Str("displayName") ?? entry.Str("name") ?? entry.Str("label") ?? id,
                Description = entry.Str("description"),
                IsDefault = entry.Bool("isDefault") || entry.Bool("default"),
                DefaultEffort = entry.Str("defaultReasoningEffort") ?? entry.Str("defaultEffort"),
                SupportedEfforts = efforts,
            });
        }

        return models;
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
