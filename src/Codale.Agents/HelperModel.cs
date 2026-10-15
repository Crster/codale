using System.Diagnostics;
using System.Text;
using System.Text.Json;

using Codale.Agents.OpenAi;
using Codale.Core.Agents;
using Codale.Core.Helper;
using Codale.Core.Processes;
using Codale.Core.Text;

namespace Codale.Agents;

/// <summary>
/// Answers <see cref="IHelperModel"/> requests with a one-shot run of the Claude CLI
/// (or an OpenAI-compatible API endpoint): prompt in on stdin, reply out on stdout, no tools, no saved session.
/// </summary>
/// <remarks>
/// Without the Claude CLI and without an API endpoint, <see cref="IsAvailable"/> is false
/// and callers fall back to their non-model behaviour.
/// Runs from the temp folder so no project instructions leak into a commit message.
/// A Claude CLI takes seconds to start, so <see cref="Prewarm"/> starts one ahead of time
/// that waits for its request; the next matching call then costs only the model's reply.
/// Each system prompt gets its own spare, so routing and a search's two steps can all be
/// ready at once.
/// </remarks>
public sealed class HelperModel(
    Func<OpenAiEndpoint?>? api = null,
    Func<string?>? provider = null,
    Func<string?>? codexModel = null) : IHelperModel, IDisposable
{
    /// <summary>The cheapest Claude model: these jobs are short and latency is what the user feels.</summary>
    public const string ClaudeModel = "haiku";

    /// <summary>The fast Codex model: helper jobs are short and latency is what the user feels.</summary>
    public const string DefaultCodexModel = "gpt-5.6-terra";

    /// <summary>A warm CLI nobody has asked for in this long is let go.</summary>
    private static readonly TimeSpan SpareIdleLimit = TimeSpan.FromMinutes(2);

    /// <summary>Routing, a search's keyword and pick steps, and one to spare.</summary>
    private const int MaxSpares = 4;

    /// <summary>A one-line job that takes longer than this is hung, not slow; the CLI is killed.</summary>
    private static readonly TimeSpan CliTimeout = TimeSpan.FromMinutes(2);

    private readonly Lock _spareLock = new();

    /// <summary>Waiting CLIs by the system prompt they were started with, and when each was last wanted.</summary>
    private readonly Dictionary<string, (WarmProcess Process, DateTime Wanted)> _spares = new(StringComparer.Ordinal);

    /// <summary>When a start was last tried for each prompt, so a burst of keystrokes tries once.</summary>
    private readonly Dictionary<string, DateTime> _attempts = new(StringComparer.Ordinal);

    private static readonly TimeSpan PrewarmRetry = TimeSpan.FromSeconds(3);

    private Timer? _spareSweep;
    private bool _disposed;

    /// <summary>The API endpoint background jobs use instead of a CLI, or null to use the CLI.</summary>
    private OpenAiEndpoint? ApiEndpoint => api?.Invoke();

    /// <summary>Raised after each background request a provider answered (API endpoint or CLI), with what it reported spending.</summary>
    public event Action<UsageSnapshot>? ApiRequestServed;

    /// <summary>Raised when a background job fails because the CLI is not logged in.</summary>
    public event Action<string>? LoginRequired;

    private void OnApiRequestServed(UsageSnapshot usage) => ApiRequestServed?.Invoke(usage);

    private bool IsCodexSelected =>
        string.Equals(provider?.Invoke()?.Trim(), "codex", StringComparison.OrdinalIgnoreCase);

    public bool IsAvailable =>
        ApiEndpoint is not null ||
        (IsCodexSelected ? CodexInstalled : ClaudeInstalled);

    private static bool ClaudeInstalled => CliLocator.IsInstalled();
    private static bool CodexInstalled => CliLocator.Find("codex") is not null;

    public Task<string> CompleteAsync(string systemPrompt, string prompt, CancellationToken ct = default) =>
        CompleteAsync(systemPrompt, prompt, OpenAiApiClient.MaxTokens, ct);

    public async Task<string> CompleteAsync(string systemPrompt, string prompt, int maxTokens, CancellationToken ct = default)
    {
        if (ApiEndpoint is { } endpoint)
        {
            return ModelOutput.Clean(await OpenAiApiClient.CompleteAsync(endpoint, systemPrompt, prompt, ct, maxTokens, OnApiRequestServed).ConfigureAwait(false));
        }

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

        // Leaked reasoning or template tokens are never part of the answer.
        return ModelOutput.Clean(await RunAsync(systemPrompt, prompt, ct).ConfigureAwait(false));
    }

    public async Task<ToolCall?> CallToolAsync(
        string systemPrompt,
        string conversation,
        IReadOnlyList<ToolDefinition> tools,
        CancellationToken ct = default)
    {
        if (ApiEndpoint is { } endpoint)
        {
            return await OpenAiApiClient.CallToolAsync(endpoint, systemPrompt, conversation, tools, ct, OnApiRequestServed).ConfigureAwait(false);
        }

        if (IsCodexSelected)
        {
            if (!CodexInstalled)
            {
                throw new HelperModelException("The Codex CLI is not installed.");
            }

            var codexFullPrompt = HelperToolCalls.BuildSystemPrompt(systemPrompt, tools);
            var codexReply = await RunCodexAsync(codexFullPrompt, conversation, ct).ConfigureAwait(false);
            return HelperToolCalls.Parse(codexReply, tools);
        }

        if (!ClaudeInstalled)
        {
            throw new HelperModelException("The Claude CLI is not installed.");
        }

        var fullPrompt = HelperToolCalls.BuildSystemPrompt(systemPrompt, tools);
        if (TakeSpare(fullPrompt) is { } spare)
        {
            try
            {
                return HelperToolCalls.Parse(await spare.AskAsync(conversation, ct, CliTimeout, OnApiRequestServed).ConfigureAwait(false), tools);
            }
            catch (HelperModelException ex)
            {
                // A spare that died while it waited is no reason to fail: a cold run still answers.
                Trace.WriteLine($"Warm helper failed, running cold: {ex.Message}");
            }
        }

        var reply = await RunAsync(fullPrompt, conversation, ct).ConfigureAwait(false);
        return HelperToolCalls.Parse(reply, tools);
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
    /// Starts a Claude CLI for this system prompt and these tools unless one is already
    /// waiting, so the next <see cref="CallToolAsync"/> with them skips the start-up.
    /// Cheap enough for every keystroke: a live spare only has its idle clock reset.
    /// </summary>
    public void Prewarm(string systemPrompt, IReadOnlyList<ToolDefinition> tools)
    {
        // An API call or Codex has no Claude process to prewarm.
        if (ApiEndpoint is not null || IsCodexSelected)
        {
            return;
        }

        var fullPrompt = HelperToolCalls.BuildSystemPrompt(systemPrompt, tools);
        lock (_spareLock)
        {
            if (_disposed)
            {
                return;
            }

            _spareSweep ??= new Timer(_ => DropIdleSpares(), null, TimeSpan.FromSeconds(30), TimeSpan.FromSeconds(30));

            if (_spares.TryGetValue(fullPrompt, out var spare) && spare.Process.IsAlive)
            {
                _spares[fullPrompt] = spare with { Wanted = DateTime.UtcNow };
                return;
            }

            // Keystrokes arrive faster than a start can land, and without Claude every
            // attempt is a PATH scan that ends in nothing: one try per prompt per window.
            var now = DateTime.UtcNow;
            if (_attempts.TryGetValue(fullPrompt, out var last) && now - last < PrewarmRetry)
            {
                return;
            }

            _attempts[fullPrompt] = now;
        }

        // Checking for the CLI scans PATH and a process start is not free either:
        // neither belongs on the thread that is handling a keystroke.
        _ = Task.Run(() =>
        {
            if (!ClaudeInstalled)
            {
                return;
            }

            lock (_spareLock)
            {
                if (_disposed)
                {
                    return;
                }

                if (_spares.TryGetValue(fullPrompt, out var existing))
                {
                    if (existing.Process.IsAlive)
                    {
                        return;
                    }

                    existing.Process.Dispose();
                    _spares.Remove(fullPrompt);
                }

                // Room for a new prompt goes to whatever was wanted longest ago.
                while (_spares.Count >= MaxSpares)
                {
                    var oldest = _spares.MinBy(s => s.Value.Wanted);
                    oldest.Value.Process.Dispose();
                    _spares.Remove(oldest.Key);
                }

                if (WarmProcess.TryStart(fullPrompt) is { } started)
                {
                    _spares[fullPrompt] = (started, DateTime.UtcNow);
                }
            }
        });
    }

    /// <summary>The waiting CLI when one was started for this prompt; it answers one request only.</summary>
    private WarmProcess? TakeSpare(string systemPrompt)
    {
        lock (_spareLock)
        {
            if (!_spares.Remove(systemPrompt, out var spare))
            {
                return null;
            }

            if (spare.Process.IsAlive)
            {
                return spare.Process;
            }

            spare.Process.Dispose();
            return null;
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
    internal static IReadOnlyList<string> BuildArguments(string systemPrompt) =>
    [
        "-p",
        "--model", ClaudeModel,
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
    internal static IReadOnlyList<string> BuildStreamArguments(string systemPrompt) =>
    [
        "-p",
        "--model", ClaudeModel,
        "--tools", "",
        "--strict-mcp-config",
        "--no-session-persistence",
        "--disable-slash-commands",
        "--input-format", "stream-json",
        "--output-format", "stream-json",
        "--verbose",
        "--system-prompt", systemPrompt,
    ];

    /// <summary>The process settings every helper run shares, its arguments aside.</summary>
    internal static ProcessStartInfo BuildStartInfo(string executable)
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
        startInfo.Environment[CodaleAccount.ClaudeVariable] = CodaleAccount.ClaudeHome;

        return startInfo;
    }

    private async Task<string> RunAsync(string systemPrompt, string prompt, CancellationToken ct)
    {
        var executable = CliLocator.Find(CliLocator.Executable);
        if (executable is null)
        {
            throw new HelperModelException("The Claude CLI is not installed.");
        }

        var startInfo = BuildStartInfo(executable);
        foreach (var arg in BuildArguments(systemPrompt))
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
        var executable = CliLocator.Find("codex");
        if (executable is null)
        {
            throw new HelperModelException("The Codex CLI is not installed.");
        }

        var startInfo = BuildStartInfo(executable);
        startInfo.ArgumentList.Add("exec");
        startInfo.ArgumentList.Add("--ephemeral");
        startInfo.ArgumentList.Add("--skip-git-repo-check");
        startInfo.ArgumentList.Add("-s");
        startInfo.ArgumentList.Add("read-only");

        var chosenModel = codexModel?.Invoke()?.Trim();
        if (string.IsNullOrWhiteSpace(chosenModel))
        {
            chosenModel = DefaultCodexModel;
        }

        startInfo.ArgumentList.Add("-c");
        startInfo.ArgumentList.Add($"model={chosenModel}");
        startInfo.ArgumentList.Add("-c");
        startInfo.ArgumentList.Add("model_reasoning_effort=low");
        startInfo.ArgumentList.Add("-");

        Process process;
        try
        {
            process = await ProcessUtil.StartAsync(startInfo).ConfigureAwait(false)
                ?? throw new HelperModelException("Could not start the Codex CLI.");
        }
        catch (Exception ex) when (ex is not HelperModelException)
        {
            throw new HelperModelException($"Could not start the Codex CLI: {ex.Message}", ex);
        }

        using (process)
        {
            using var timeout = CancellationTokenSource.CreateLinkedTokenSource(ct);
            timeout.CancelAfter(CliTimeout);
            await using var kill = timeout.Token.Register(() => ProcessUtil.TryKillTree(process));

            var output = process.StandardOutput.ReadToEndAsync(CancellationToken.None);
            var errors = process.StandardError.ReadToEndAsync(CancellationToken.None);

            var fullInput = string.IsNullOrWhiteSpace(systemPrompt)
                ? prompt
                : $"{systemPrompt}\n\n{prompt}";

            try
            {
                await process.StandardInput.WriteAsync(fullInput).ConfigureAwait(false);
                process.StandardInput.Close();
            }
            catch (IOException)
            {
                // The CLI exited before reading its input.
            }

            await process.WaitForExitAsync(CancellationToken.None).ConfigureAwait(false);
            ct.ThrowIfCancellationRequested();
            if (timeout.IsCancellationRequested)
            {
                throw new HelperModelException($"The Codex CLI did not answer within {CliTimeout.TotalSeconds:0} seconds.");
            }

            var text = await output.ConfigureAwait(false);
            if (process.ExitCode != 0 || string.IsNullOrWhiteSpace(text))
            {
                var detail = (await errors.ConfigureAwait(false)).Trim();
                if (detail.Contains("Not logged in", StringComparison.OrdinalIgnoreCase) ||
                    detail.Contains("401 Unauthorized", StringComparison.OrdinalIgnoreCase) ||
                    detail.Contains("Missing bearer or basic authentication", StringComparison.OrdinalIgnoreCase))
                {
                    LoginRequired?.Invoke("Codex");
                    throw new HelperModelException("The Codex CLI is not logged in. Run 'codex login' to authenticate.");
                }

                throw new HelperModelException(
                    $"The Codex CLI returned nothing usable (exit {process.ExitCode})" +
                    (detail.Length > 0 ? $": {TextClip.Truncate(detail, 300)}" : "."));
            }

            // Codex reports one total, not a split: it is tallied as input so the call and its size show.
            var spent = ReadCodexTokens(await errors.ConfigureAwait(false) + " " + text);
            OnApiRequestServed(new UsageSnapshot { InputTokens = spent });
            return text;
        }
    }

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

    /// <summary>A started Claude CLI waiting for one stream-json request.</summary>
    private sealed class WarmProcess : IDisposable
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

        public static WarmProcess? TryStart(string systemPrompt)
        {
            if (CliLocator.Find(CliLocator.Executable) is not { } executable)
            {
                return null;
            }

            var startInfo = BuildStartInfo(executable);
            foreach (var arg in BuildStreamArguments(systemPrompt))
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