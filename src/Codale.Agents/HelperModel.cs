using System.Diagnostics;
using System.Text;
using System.Text.Json;

using Codale.Agents.Codex;
using Codale.Agents.OpenAi;
using Codale.Core.Agents;
using Codale.Core.Helper;
using Codale.Core.Processes;
using Codale.Core.Text;

namespace Codale.Agents;

/// <summary>
/// A BYOK provider the Claude or Codex CLI is pointed at instead of its own login: background
/// runs then use the provider's flash model, reached with the same environment (and, for Codex,
/// the same custom-provider config) its chat sessions get.
/// </summary>
public sealed record HelperCliEndpoint(
    string Model,
    IReadOnlyDictionary<string, string> Environment,
    CodexCustomProvider? CodexProvider = null);

/// <summary>
/// Answers <see cref="IHelperModel"/> requests with a one-shot run of the selected CLI
/// (or an OpenAI-compatible API endpoint): prompt in on stdin, reply out on stdout, no tools, no saved session.
/// </summary>
/// <remarks>
/// Without the Claude CLI and without an API endpoint, <see cref="IsAvailable"/> is false
/// and callers fall back to their non-model behaviour.
/// Runs from the temp folder so no project instructions leak into a commit message.
/// A CLI takes seconds to start, so <see cref="Prewarm"/> starts one ahead of time
/// that waits for its request; the next matching call then costs only the model's reply.
/// Each system prompt gets its own spare, so routing and a search's two steps can all be
/// ready at once.
/// </remarks>
public sealed partial class HelperModel(
    Func<OpenAiEndpoint?>? api = null,
    Func<string?>? provider = null,
    Func<string?>? codexModel = null,
    Func<HelperCliEndpoint?>? claudeEndpoint = null,
    Func<HelperCliEndpoint?>? codexEndpoint = null) : IHelperModel, IDisposable
{
    /// <summary>The cheapest Claude model: these jobs are short and latency is what the user feels.</summary>
    public const string ClaudeModel = "haiku";

    /// <summary>The cheapest Codex model: helper jobs are short and latency is what the user feels.</summary>
    public const string DefaultCodexModel = "gpt-5.6-luna";

    /// <summary>A warm CLI nobody has asked for in this long is let go.</summary>
    private static readonly TimeSpan SpareIdleLimit = TimeSpan.FromMinutes(2);

    /// <summary>Routing, a search's keyword and pick steps, and one to spare.</summary>
    private const int MaxSpares = 4;

    /// <summary>A one-line job that takes longer than this is hung, not slow; the CLI is killed.</summary>
    private static readonly TimeSpan CliTimeout = TimeSpan.FromMinutes(2);

    private readonly Lock _spareLock = new();

    /// <summary>Waiting CLIs by <see cref="SpareKey"/> (model, endpoint and system prompt), and when each was last wanted.</summary>
    private readonly Dictionary<string, (IWarmProcess Process, DateTime Wanted)> _spares = new(StringComparer.Ordinal);

    /// <summary>When a start was last tried for each prompt, so a burst of keystrokes tries once.</summary>
    private readonly Dictionary<string, DateTime> _attempts = new(StringComparer.Ordinal);

    private static readonly TimeSpan PrewarmRetry = TimeSpan.FromSeconds(3);

    private Timer? _spareSweep;
    private bool _disposed;

    /// <summary>The API endpoint background jobs use instead of a CLI, or null to use the CLI.</summary>
    private OpenAiEndpoint? ApiEndpoint => api?.Invoke();

    /// <summary>The BYOK provider the Claude CLI is pointed at, or null for its own login (and <see cref="ClaudeModel"/>).</summary>
    private HelperCliEndpoint? ClaudeEndpoint => claudeEndpoint?.Invoke();

    /// <summary>The BYOK provider the Codex CLI is pointed at, or null for its own login.</summary>
    private HelperCliEndpoint? CodexEndpoint => codexEndpoint?.Invoke();

    /// <summary>
    /// What a spare is filed under: the same prompt for another model or endpoint is another
    /// process, so a spare started on the login is never handed a request meant for a provider.
    /// </summary>
    internal static string SpareKey(string fullPrompt, HelperCliEndpoint? endpoint) =>
        endpoint is null
            ? fullPrompt
            : $"{endpoint.Model}\n{endpoint.Environment.GetValueOrDefault("ANTHROPIC_BASE_URL")}\n{fullPrompt}";

    /// <summary>Raised after each background request a provider answered (API endpoint or CLI), with what it reported spending.</summary>
    public event Action<UsageSnapshot>? ApiRequestServed;

    /// <summary>Raised when a background job fails because the CLI is not logged in.</summary>
    public event Action<string>? LoginRequired;

    private void OnApiRequestServed(UsageSnapshot usage) => ApiRequestServed?.Invoke(usage);

    private bool IsCodexSelected =>
        string.Equals(provider?.Invoke()?.Trim(), "codex", StringComparison.OrdinalIgnoreCase);

    /// <summary>Settings picked Off: no prewarm, request or CLI start happens.</summary>
    private bool IsOff =>
        string.Equals(provider?.Invoke()?.Trim(), "off", StringComparison.OrdinalIgnoreCase);

    public bool IsAvailable =>
        !IsOff && (ApiEndpoint is not null ||
        (IsCodexSelected ? CodexInstalled : ClaudeInstalled));

    private static bool ClaudeInstalled => CliLocator.IsInstalled();
    private static bool CodexInstalled => CliLocator.Find("codex") is not null;

    public Task<string> CompleteAsync(string systemPrompt, string prompt, CancellationToken ct = default) =>
        CompleteAsync(systemPrompt, prompt, OpenAiApiClient.MaxTokens, ct);

    public async Task<string> CompleteAsync(string systemPrompt, string prompt, int maxTokens, CancellationToken ct = default)
    {
        if (IsOff)
        {
            throw new HelperModelException("Background tasks are off.");
        }

        if (ApiEndpoint is { } endpoint)
        {
            return ModelOutput.Clean(await OpenAiApiClient.CompleteAsync(endpoint, systemPrompt, prompt, ct, maxTokens, OnApiRequestServed).ConfigureAwait(false));
        }

        try
        {
            if (IsCodexSelected)
            {
                if (!CodexInstalled)
                {
                    throw new HelperModelException("The Codex CLI is not installed.");
                }

                return ModelOutput.Clean(await RunCodexAsync(systemPrompt, prompt, ct).ConfigureAwait(false));
            }

            if (!ClaudeInstalled)
            {
                throw new HelperModelException("The Claude CLI is not installed.");
            }

            return ModelOutput.Clean(await RunAsync(systemPrompt, prompt, ct).ConfigureAwait(false));
        }
        finally
        {
            Prewarm(systemPrompt);
        }
    }

    public async Task<ToolCall?> CallToolAsync(
        string systemPrompt,
        string conversation,
        IReadOnlyList<ToolDefinition> tools,
        CancellationToken ct = default)
    {
        if (IsOff)
        {
            return null;
        }

        if (ApiEndpoint is { } endpoint)
        {
            return await OpenAiApiClient.CallToolAsync(endpoint, systemPrompt, conversation, tools, ct, OnApiRequestServed).ConfigureAwait(false);
        }

        var fullPrompt = HelperToolCalls.BuildSystemPrompt(systemPrompt, tools);
        try
        {
            var reply = IsCodexSelected
                ? await RunCodexAsync(fullPrompt, conversation, ct).ConfigureAwait(false)
                : await RunAsync(fullPrompt, conversation, ct).ConfigureAwait(false);
            return HelperToolCalls.Parse(reply, tools);
        }
        finally
        {
            Prewarm(fullPrompt);
        }
    }

    public async Task<IReadOnlyList<ToolCall>> CallToolsAsync(
        string systemPrompt,
        string conversation,
        IReadOnlyList<ToolDefinition> tools,
        CancellationToken ct = default)
    {
        if (ApiEndpoint is { } endpoint)
        {
            return await OpenAiApiClient.CallToolsAsync(endpoint, systemPrompt, conversation, tools, ct, OnApiRequestServed).ConfigureAwait(false);
        }

        // The CLI's reply contract is a single JSON call.
        return await CallToolAsync(systemPrompt, conversation, tools, ct).ConfigureAwait(false) is { } call ? [call] : [];
    }

    /// <summary>
    /// Starts the selected CLI for this system prompt and these tools unless one is already
    /// waiting, so the next <see cref="CallToolAsync"/> with them skips the start-up.
    /// Cheap enough for every keystroke: a live spare only has its idle clock reset.
    /// </summary>
    public void Prewarm(string systemPrompt, IReadOnlyList<ToolDefinition> tools) =>
        Prewarm(HelperToolCalls.BuildSystemPrompt(systemPrompt, tools));

    /// <summary>Prepares a plain completion, such as a session title, without making a model request.</summary>
    public void Prewarm(string systemPrompt)
    {
        if (IsOff)
        {
            DropAllSpares();
            return;
        }

        if (ApiEndpoint is not null)
        {
            return;
        }

        var fullPrompt = systemPrompt;
        var codex = IsCodexSelected;
        var endpoint = codex ? CodexEndpoint : ClaudeEndpoint;
        var model = codex ? CodexModel(endpoint) : endpoint?.Model ?? ClaudeModel;
        var key = codex ? CodexSpareKey(fullPrompt, model, endpoint) : SpareKey(fullPrompt, endpoint);
        lock (_spareLock)
        {
            if (_disposed)
            {
                return;
            }

            _spareSweep ??= new Timer(_ => DropIdleSpares(), null, TimeSpan.FromSeconds(30), TimeSpan.FromSeconds(30));

            if (_spares.TryGetValue(key, out var spare) && spare.Process.IsAlive)
            {
                _spares[key] = spare with { Wanted = DateTime.UtcNow };
                return;
            }

            // Keystrokes arrive faster than a start can land, and without Claude every
            // attempt is a PATH scan that ends in nothing: one try per prompt per window.
            var now = DateTime.UtcNow;
            if (_attempts.TryGetValue(key, out var last) && now - last < PrewarmRetry)
            {
                return;
            }

            _attempts[key] = now;
        }

        // Checking for the CLI scans PATH and a process start is not free either:
        // neither belongs on the thread that is handling a keystroke.
        _ = Task.Run(() =>
        {
            if (codex ? !CodexInstalled : !ClaudeInstalled)
            {
                return;
            }

            lock (_spareLock)
            {
                if (_disposed || IsOff)
                {
                    return;
                }

                if (_spares.TryGetValue(key, out var existing))
                {
                    if (existing.Process.IsAlive)
                    {
                        return;
                    }

                    existing.Process.Dispose();
                    _spares.Remove(key);
                }

                // Room for a new prompt goes to whatever was wanted longest ago.
                while (_spares.Count >= MaxSpares)
                {
                    var oldest = _spares.MinBy(s => s.Value.Wanted);
                    oldest.Value.Process.Dispose();
                    _spares.Remove(oldest.Key);
                }

                IWarmProcess? started = codex
                    ? WarmCodexProcess.TryStart(fullPrompt, model, endpoint)
                    : WarmProcess.TryStart(fullPrompt, endpoint);
                if (started is not null)
                {
                    _spares[key] = (started, DateTime.UtcNow);
                }
            }
        });
    }

    /// <summary>The waiting CLI when one was started under this <see cref="SpareKey"/>; it answers one request only.</summary>
    private IWarmProcess? TakeSpare(string key)
    {
        lock (_spareLock)
        {
            if (!_spares.Remove(key, out var spare))
            {
                return null;
            }

            _attempts.Remove(key);
            if (spare.Process.IsAlive)
            {
                return spare.Process;
            }

            spare.Process.Dispose();
            return null;
        }
    }

    private void DropAllSpares()
    {
        lock (_spareLock)
        {
            foreach (var spare in _spares.Values)
            {
                spare.Process.Dispose();
            }

            _spares.Clear();
        }
    }

    private void DropIdleSpares()
    {
        lock (_spareLock)
        {
            var cutoff = DateTime.UtcNow - SpareIdleLimit;
            foreach (var (prompt, spare) in _spares.Where(s => s.Value.Wanted < cutoff || !s.Value.Process.IsAlive).ToList())
            {
                spare.Process.Dispose();
                _spares.Remove(prompt);
            }
        }
    }

    public void Dispose()
    {
        lock (_spareLock)
        {
            _disposed = true;
            _spareSweep?.Dispose();
            foreach (var spare in _spares.Values)
            {
                spare.Process.Dispose();
            }

            _spares.Clear();
        }
    }

    /// <summary>The CLI arguments for a one-shot run; the prompt travels on stdin, not here.</summary>
    internal static IReadOnlyList<string> BuildArguments(string systemPrompt, string model = ClaudeModel) =>
    [
        "-p",
        "--model", model,
        "--effort", "low",
        "--tools", "",
        "--strict-mcp-config",
        "--no-session-persistence",
        "--disable-slash-commands",
        "--output-format", "json",
        "--system-prompt", systemPrompt,
    ];

    /// <summary>
    /// A Claude CLI that takes its request as a stream-json line. Unlike a prompt piped to
    /// stdin, which the CLI waits to read in full before it starts up, this finishes
    /// starting before the request arrives - which is what makes a spare worth keeping.
    /// </summary>
    internal static IReadOnlyList<string> BuildStreamArguments(string systemPrompt, string model = ClaudeModel) =>
    [
        "-p",
        "--model", model,
        "--effort", "low",
        "--tools", "",
        "--strict-mcp-config",
        "--no-session-persistence",
        "--disable-slash-commands",
        "--input-format", "stream-json",
        "--output-format", "stream-json",
        "--verbose",
        "--system-prompt", systemPrompt,
    ];

    /// <summary>The process settings every helper run shares, its arguments aside; <paramref name="endpoint"/> adds a BYOK provider's environment.</summary>
    internal static ProcessStartInfo BuildStartInfo(string executable, HelperCliEndpoint? endpoint = null)
    {
        var utf8 = new UTF8Encoding(encoderShouldEmitUTF8Identifier: false);
        var startInfo = new ProcessStartInfo
        {
            FileName = executable,
            WorkingDirectory = Path.GetTempPath(),
            RedirectStandardInput = true,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            UseShellExecute = false,
            CreateNoWindow = true,
            StandardInputEncoding = utf8,
            StandardOutputEncoding = utf8,
            StandardErrorEncoding = utf8,
        };

        // Extended thinking took a routing reply from ~1s to ~4s and changed nothing
        // in what came back: these jobs are one-liners.
        startInfo.Environment["MAX_THINKING_TOKENS"] = "0";
        startInfo.Environment[CodaleAccount.ClaudeVariable] = CodaleAccount.HelperClaudeHome;
        if (endpoint is not null)
        {
            foreach (var (name, value) in endpoint.Environment)
            {
                startInfo.Environment[name] = value;
            }
        }

        return startInfo;
    }

    private async Task<string> RunAsync(string systemPrompt, string prompt, CancellationToken ct)
    {
        if (TakeSpare(SpareKey(systemPrompt, ClaudeEndpoint)) is { } spare)
        {
            try
            {
                return await spare.AskAsync(prompt, ct, CliTimeout, OnApiRequestServed).ConfigureAwait(false);
            }
            catch (HelperModelException ex)
            {
                Trace.WriteLine($"Warm helper failed, running cold: {ex.Message}");
            }
        }

        var executable = CliLocator.Find(CliLocator.Executable);
        if (executable is null)
        {
            throw new HelperModelException("The Claude CLI is not installed.");
        }

        var endpoint = ClaudeEndpoint;
        var startInfo = BuildStartInfo(executable, endpoint);
        foreach (var arg in BuildArguments(systemPrompt, endpoint?.Model ?? ClaudeModel))
        {
            startInfo.ArgumentList.Add(arg);
        }

        Process process;
        try
        {
            process = await ProcessUtil.StartAsync(startInfo).ConfigureAwait(false)
                ?? throw new HelperModelException("Could not start the Claude CLI.");
        }
        catch (Exception ex) when (ex is not HelperModelException)
        {
            throw new HelperModelException($"Could not start the Claude CLI: {ex.Message}", ex);
        }

        using (process)
        {
            // Killed on cancel or timeout: a stuck CLI must not outlive the request that wanted it.
            using var timeout = CancellationTokenSource.CreateLinkedTokenSource(ct);
            timeout.CancelAfter(CliTimeout);
            await using var kill = timeout.Token.Register(() => ProcessUtil.TryKillTree(process));

            var output = process.StandardOutput.ReadToEndAsync(CancellationToken.None);
            var errors = process.StandardError.ReadToEndAsync(CancellationToken.None);

            try
            {
                await process.StandardInput.WriteAsync(prompt).ConfigureAwait(false);
                process.StandardInput.Close();
            }
            catch (IOException)
            {
                // The CLI exited before reading its input; its exit code and stderr explain why.
            }

            await process.WaitForExitAsync(CancellationToken.None).ConfigureAwait(false);
            ct.ThrowIfCancellationRequested();
            if (timeout.IsCancellationRequested)
            {
                throw new HelperModelException($"The Claude CLI did not answer within {CliTimeout.TotalSeconds:0} seconds.");
            }

            var text = await output.ConfigureAwait(false);
            if (process.ExitCode != 0 || string.IsNullOrWhiteSpace(text))
            {
                var detail = (await errors.ConfigureAwait(false)).Trim();
                throw new HelperModelException(
                    $"The Claude CLI returned nothing usable (exit {process.ExitCode})" +
                    (detail.Length > 0 ? $": {TextClip.Truncate(detail, 300)}" : "."));
            }

            return ReadJsonResult(text, OnApiRequestServed);
        }
    }

    /// <summary>
    /// The reply of a one-shot <c>--output-format json</c> run, reporting what it spent. Output that
    /// is not that JSON (an older CLI printing plain text) is the reply itself.
    /// </summary>
    internal static string ReadJsonResult(string output, Action<UsageSnapshot>? onUsage)
    {
        try
        {
            using var document = JsonDocument.Parse(output);
            var root = document.RootElement;
            if (root.ValueKind != JsonValueKind.Object ||
                !root.TryGetProperty("type", out var type) || type.GetString() != "result")
            {
                return output;
            }

            if (root.TryGetProperty("is_error", out var error) && error.ValueKind == JsonValueKind.True)
            {
                throw new HelperModelException("The Claude CLI returned an error: " +
                    (root.TryGetProperty("result", out var failure) ? failure.GetString() : "no detail"));
            }

            onUsage?.Invoke(ReadClaudeUsage(root));
            return root.TryGetProperty("result", out var result) && result.ValueKind == JsonValueKind.String
                ? result.GetString() ?? ""
                : "";
        }
        catch (JsonException)
        {
            return output;
        }
    }

    /// <summary>The token counts of a Claude <c>result</c> event; a call with no counts still counts as a call.</summary>
    internal static UsageSnapshot ReadClaudeUsage(JsonElement result)
    {
        if (!result.TryGetProperty("usage", out var usage) || usage.ValueKind != JsonValueKind.Object)
        {
            return new UsageSnapshot();
        }

        int Field(string name) => usage.TryGetProperty(name, out var v) && v.ValueKind == JsonValueKind.Number && v.TryGetInt32(out var n) ? n : 0;
        return new UsageSnapshot
        {
            InputTokens = Field("input_tokens"),
            OutputTokens = Field("output_tokens"),
            CacheReadInputTokens = Field("cache_read_input_tokens"),
            CacheCreationInputTokens = Field("cache_creation_input_tokens"),
        };
    }

    /// <summary>The total <c>codex exec</c> reports on finishing ("tokens used" then the count), or 0.</summary>
    internal static int ReadCodexTokens(string output)
    {
        var match = System.Text.RegularExpressions.Regex.Match(
            output, @"tokens used\D{0,5}([\d,\.]+)", System.Text.RegularExpressions.RegexOptions.IgnoreCase);
        return match.Success && int.TryParse(match.Groups[1].Value.Replace(",", "").Replace(".", ""), out var tokens)
            ? tokens
            : 0;
    }

    private async Task<string> RunCodexAsync(string systemPrompt, string prompt, CancellationToken ct)
    {
        var endpoint = CodexEndpoint;
        var model = CodexModel(endpoint);
        using var spare = TakeSpare(CodexSpareKey(systemPrompt, model, endpoint))
            ?? await Task.Run(() => WarmCodexProcess.TryStart(systemPrompt, model, endpoint), ct).ConfigureAwait(false)
            ?? throw new HelperModelException("Could not start the Codex CLI.");
        try
        {
            return await spare.AskAsync(prompt, ct, CliTimeout, OnApiRequestServed).ConfigureAwait(false);
        }
        catch (HelperModelException ex) when (
            // A BYOK provider rejecting its key is not a missing CLI login: signing in would not fix it.
            endpoint is null && (
                ex.Message.Contains("Not logged in", StringComparison.OrdinalIgnoreCase) ||
                ex.Message.Contains("401 Unauthorized", StringComparison.OrdinalIgnoreCase) ||
                ex.Message.Contains("Missing bearer or basic authentication", StringComparison.OrdinalIgnoreCase)))
        {
            LoginRequired?.Invoke("Codex");
            throw;
        }
    }

    private string CodexModel(HelperCliEndpoint? endpoint) =>
        endpoint?.Model ?? (codexModel?.Invoke()?.Trim() is { Length: > 0 } model ? model : DefaultCodexModel);

    /// <summary>The stream-json line that asks a warm CLI one question.</summary>
    internal static string BuildStreamRequest(string prompt)
    {
        return OpenAiApiClient.WriteJson(json =>
        {
            json.WriteStartObject();
            json.WriteString("type", "user");
            json.WriteStartObject("message");
            json.WriteString("role", "user");
            json.WriteString("content", prompt);
            json.WriteEndObject();
            json.WriteEndObject();
        });
    }

    /// <summary>The reply carried by a stream-json <c>result</c> event; null for any other line.</summary>
    /// <exception cref="HelperModelException">The result reports an error or carries no text.</exception>
    internal static string? ReadStreamResult(string line)
    {
        if (!line.Contains("\"result\"", StringComparison.Ordinal))
        {
            return null;
        }

        string? text;
        bool failed;
        try
        {
            using var document = JsonDocument.Parse(line);
            var root = document.RootElement;
            if (root.ValueKind != JsonValueKind.Object ||
                !root.TryGetProperty("type", out var type) ||
                type.ValueKind != JsonValueKind.String ||
                type.GetString() != "result")
            {
                return null;
            }

            text = root.TryGetProperty("result", out var result) && result.ValueKind == JsonValueKind.String
                ? result.GetString()
                : null;
            failed = root.TryGetProperty("is_error", out var error) && error.ValueKind == JsonValueKind.True;
        }
        catch (JsonException)
        {
            return null;
        }

        if (failed || string.IsNullOrWhiteSpace(text))
        {
            throw new HelperModelException(
                "The Claude CLI returned nothing usable" + (text is { Length: > 0 } ? $": {text}" : "."));
        }

        return text;
    }

    /// <summary>What a stream-json <c>result</c> line reports spending.</summary>
    internal static UsageSnapshot ReadStreamUsage(string line)
    {
        try
        {
            using var document = JsonDocument.Parse(line);
            return ReadClaudeUsage(document.RootElement);
        }
        catch (JsonException)
        {
            return new UsageSnapshot();
        }
    }

    internal interface IWarmProcess : IDisposable
    {
        bool IsAlive { get; }
        Task<string> AskAsync(string prompt, CancellationToken ct, TimeSpan timeoutAfter, Action<UsageSnapshot>? onUsage = null);
    }

    /// <summary>A started Claude CLI waiting for one stream-json request.</summary>
    private sealed class WarmProcess : IWarmProcess
    {
        private readonly Process _process;
        private readonly Task<string> _errors;

        private WarmProcess(Process process, string systemPrompt)
        {
            _process = process;
            SystemPrompt = systemPrompt;
            _errors = process.StandardError.ReadToEndAsync(CancellationToken.None);
        }

        public string SystemPrompt { get; }

        public bool IsAlive
        {
            get
            {
                try
                {
                    return !_process.HasExited;
                }
                catch (InvalidOperationException)
                {
                    return false;
                }
            }
        }

        public static WarmProcess? TryStart(string systemPrompt, HelperCliEndpoint? endpoint)
        {
            if (CliLocator.Find(CliLocator.Executable) is not { } executable)
            {
                return null;
            }

            var startInfo = BuildStartInfo(executable, endpoint);
            foreach (var arg in BuildStreamArguments(systemPrompt, endpoint?.Model ?? ClaudeModel))
            {
                startInfo.ArgumentList.Add(arg);
            }

            try
            {
                return Process.Start(startInfo) is { } process ? new WarmProcess(process, systemPrompt) : null;
            }
            catch (Exception ex)
            {
                Trace.WriteLine($"Could not start a warm helper: {ex.Message}");
                return null;
            }
        }

        /// <summary>Sends the one request and returns the reply text; the process is spent after.</summary>
        public async Task<string> AskAsync(string prompt, CancellationToken ct, TimeSpan timeoutAfter, Action<UsageSnapshot>? onUsage = null)
        {
            using (this)
            {
                // Killed on cancel or timeout, which ends the read loop below.
                using var timeout = CancellationTokenSource.CreateLinkedTokenSource(ct);
                timeout.CancelAfter(timeoutAfter);
                await using var kill = timeout.Token.Register(Kill);

                var request = BuildStreamRequest(prompt);
                try
                {
                    await _process.StandardInput.WriteLineAsync(request).ConfigureAwait(false);
                    await _process.StandardInput.FlushAsync(CancellationToken.None).ConfigureAwait(false);
                }
                catch (IOException ex)
                {
                    ct.ThrowIfCancellationRequested();
                    throw new HelperModelException($"The warm Claude CLI had already exited: {ex.Message}", ex);
                }

                // The init and assistant events stream past; the result event carries the reply.
                while (await _process.StandardOutput.ReadLineAsync(CancellationToken.None).ConfigureAwait(false) is { } line)
                {
                    if (ReadStreamResult(line) is { } result)
                    {
                        onUsage?.Invoke(ReadStreamUsage(line));
                        return result;
                    }
                }

                ct.ThrowIfCancellationRequested();
                if (timeout.IsCancellationRequested)
                {
                    throw new HelperModelException($"The warm Claude CLI did not answer within {timeoutAfter.TotalSeconds:0} seconds.");
                }

                var detail = _process.HasExited ? (await _errors.ConfigureAwait(false)).Trim() : "";
                throw new HelperModelException(
                    "The warm Claude CLI ended without a reply" +
                    (detail.Length > 0 ? $": {TextClip.Truncate(detail, 300)}" : "."));
            }
        }

        private void Kill() => ProcessUtil.TryKillTree(_process);

        public void Dispose()
        {
            Kill();
            _process.Dispose();
        }
    }
}
