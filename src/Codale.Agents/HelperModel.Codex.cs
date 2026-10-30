using System.Diagnostics;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;

using Codale.Agents.Codex;
using Codale.Core.Agents;
using Codale.Core.Helper;
using Codale.Core.Processes;

namespace Codale.Agents;

public sealed partial class HelperModel
{
    // Include all endpoint settings without retaining credentials in the dictionary key.
    internal static string CodexSpareKey(string prompt, string model, HelperCliEndpoint? endpoint) =>
        "codex:" + Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(JsonSerializer.Serialize(new
        {
            prompt, model, endpoint?.CodexProvider,
            environment = endpoint?.Environment.OrderBy(e => e.Key, StringComparer.Ordinal).ToArray(),
        }))));

    internal static IReadOnlyList<string> BuildCodexArguments(string model, HelperCliEndpoint? endpoint = null)
    {
        var args = new List<string>();
        if (endpoint?.CodexProvider is { } provider)
        {
            args.AddRange(CodexSessionOptions.ProviderArguments(provider));
        }

        args.AddRange([
            "-c", $"model='{model}'",
            "-c", "model_reasoning_effort='low'",
            "-c", "mcp_servers={}",
            "-c", "features.shell_tool=false",
            "-c", "features.apps=false",
            "-c", "web_search='disabled'",
            "app-server",
        ]);
        return args;
    }

    /// <summary>
    /// An initialized app-server with an ephemeral thread, waiting for one request.
    /// Unlike exec (which waits for stdin before initializing), the handshake and thread
    /// creation happen during prewarming. No inference is performed until AskAsync.
    /// </summary>
    internal sealed class WarmCodexProcess : IWarmProcess
    {
        private readonly Process _process;
        private readonly Task<string> _errors;
        private readonly CancellationTokenSource _lifetime = new();
        private readonly Task<string> _ready;
        private int _nextId;
        private int _disposed;
        private readonly Queue<JsonElement> _notifications = new();

        internal Task<string> Ready => _ready;

        private WarmCodexProcess(Process process, string systemPrompt, string model, HelperCliEndpoint? endpoint)
        {
            _process = process;
            _errors = process.StandardError.ReadToEndAsync();
            _ready = InitializeAsync(systemPrompt, model, endpoint);
            // Observe failure even if this spare expires without ever being consumed.
            _ = _ready.ContinueWith(t => Trace.WriteLine($"Codex helper prewarm failed: {t.Exception?.GetBaseException().Message}"),
                TaskContinuationOptions.OnlyOnFaulted);
        }

        public bool IsAlive
        {
            get
            {
                try
                {
                    return Volatile.Read(ref _disposed) == 0 && !_ready.IsFaulted && !_ready.IsCanceled && !_process.HasExited;
                }
                catch (InvalidOperationException)
                {
                    return false;
                }
            }
        }

        public static WarmCodexProcess? TryStart(string systemPrompt, string model, HelperCliEndpoint? endpoint)
        {
            if (CliLocator.Find("codex") is not { } executable)
            {
                return null;
            }

            var startInfo = BuildStartInfo(executable, endpoint);
            startInfo.Environment[CodaleAccount.CodexVariable] = CodaleAccount.HelperCodexHome;
            foreach (var arg in BuildCodexArguments(model, endpoint))
            {
                startInfo.ArgumentList.Add(arg);
            }

            return TryStart(startInfo, systemPrompt, model, endpoint);
        }

        internal static WarmCodexProcess? TryStart(ProcessStartInfo startInfo, string systemPrompt, string model, HelperCliEndpoint? endpoint = null)
        {
            try
            {
                return Process.Start(startInfo) is { } process ? new(process, systemPrompt, model, endpoint) : null;
            }
            catch (Exception ex)
            {
                Trace.WriteLine($"Could not start a warm Codex helper: {ex.Message}");
                return null;
            }
        }

        private async Task<string> InitializeAsync(string systemPrompt, string model, HelperCliEndpoint? endpoint)
        {
            using var timeout = CancellationTokenSource.CreateLinkedTokenSource(_lifetime.Token);
            timeout.CancelAfter(TimeSpan.FromSeconds(30));
            await RequestAsync("initialize", new
            {
                clientInfo = new { name = "codale-helper", version = "1.0.0" },
                capabilities = new { experimentalApi = true },
            }, timeout.Token).ConfigureAwait(false);
            await WriteAsync(new { method = "initialized", @params = new { } }, timeout.Token).ConfigureAwait(false);
            var thread = await RequestAsync("thread/start", new
            {
                model,
                modelProvider = endpoint?.CodexProvider is null ? null : "custom",
                cwd = Path.GetTempPath(),
                ephemeral = true,
                approvalPolicy = "never",
                sandbox = "read-only",
                baseInstructions = "Answer the supplied background task directly. Do not use tools or inspect the environment.",
                developerInstructions = systemPrompt,
            }, timeout.Token).ConfigureAwait(false);
            return thread.GetProperty("thread").GetProperty("id").GetString()
                ?? throw new HelperModelException("The Codex CLI did not return a helper thread id.");
        }

        public async Task<string> AskAsync(string prompt, CancellationToken ct, TimeSpan timeoutAfter, Action<UsageSnapshot>? onUsage = null)
        {
            using (this)
            using (var timeout = CancellationTokenSource.CreateLinkedTokenSource(ct, _lifetime.Token))
            {
                timeout.CancelAfter(timeoutAfter);
                try
                {
                    var threadId = await _ready.WaitAsync(timeout.Token).ConfigureAwait(false);
                    await RequestAsync("turn/start", new
                    {
                        threadId,
                        input = new[] { new { type = "text", text = prompt, text_elements = Array.Empty<object>() } },
                        effort = "low",
                        summary = "none",
                    }, timeout.Token).ConfigureAwait(false);

                    string? reply = null;
                    UsageSnapshot usage = new();
                    while (true)
                    {
                        var frame = _notifications.TryDequeue(out var queued)
                            ? queued : await ReadAsync(timeout.Token).ConfigureAwait(false);
                        if (!frame.TryGetProperty("method", out var method) || !frame.TryGetProperty("params", out var body))
                        {
                            continue;
                        }

                        switch (method.GetString())
                        {
                            case "item/completed" when body.TryGetProperty("item", out var item) &&
                                item.GetProperty("type").GetString() == "agentMessage":
                                reply = item.GetProperty("text").GetString();
                                break;
                            case "thread/tokenUsage/updated":
                                if (body.TryGetProperty("tokenUsage", out var tokens) && tokens.TryGetProperty("total", out var total))
                                {
                                    usage = new UsageSnapshot
                                    {
                                        InputTokens = total.TryGetProperty("inputTokens", out var input) ? input.GetInt32() : 0,
                                        OutputTokens = total.TryGetProperty("outputTokens", out var output) ? output.GetInt32() : 0,
                                    };
                                }
                                break;
                            case "turn/completed":
                                var turn = body.GetProperty("turn");
                                if (turn.GetProperty("status").GetString() != "completed" || string.IsNullOrWhiteSpace(reply))
                                {
                                    throw new HelperModelException($"The Codex CLI returned nothing usable: {turn}");
                                }
                                onUsage?.Invoke(usage);
                                return reply;
                        }
                    }
                }
                catch (OperationCanceledException) when (!ct.IsCancellationRequested)
                {
                    throw new HelperModelException("The Codex helper timed out while starting or answering.");
                }
                catch (Exception ex) when (ex is IOException or JsonException or InvalidOperationException)
                {
                    ct.ThrowIfCancellationRequested();
                    throw new HelperModelException($"The Codex helper failed: {ex.Message}", ex);
                }
            }
        }

        private async Task<JsonElement> RequestAsync(string method, object parameters, CancellationToken ct)
        {
            var id = ++_nextId;
            await WriteAsync(new { id, method, @params = parameters }, ct).ConfigureAwait(false);
            while (true)
            {
                var frame = await ReadAsync(ct).ConfigureAwait(false);
                if (!frame.TryGetProperty("id", out var responseId) || !responseId.TryGetInt32(out var value) || value != id)
                {
                    if (frame.TryGetProperty("method", out _))
                    {
                        _notifications.Enqueue(frame);
                    }
                    continue;
                }
                if (frame.TryGetProperty("error", out var error))
                {
                    throw new HelperModelException($"The Codex helper rejected {method}: {error}");
                }
                return frame.GetProperty("result");
            }
        }

        private async Task WriteAsync(object frame, CancellationToken ct)
        {
            await _process.StandardInput.WriteLineAsync(JsonSerializer.Serialize(frame).AsMemory(), ct).ConfigureAwait(false);
            await _process.StandardInput.FlushAsync(ct).ConfigureAwait(false);
        }

        private async Task<JsonElement> ReadAsync(CancellationToken ct)
        {
            while (await _process.StandardOutput.ReadLineAsync(ct).ConfigureAwait(false) is { } line)
            {
                using var document = JsonDocument.Parse(line);
                var frame = document.RootElement.Clone();
                if (frame.TryGetProperty("method", out _) && frame.TryGetProperty("id", out var id))
                {
                    // Helpers cannot service tools, approvals or interactive questions.
                    await WriteAsync(new { id, error = new { code = -32601, message = "Background helper tools are disabled." } }, ct).ConfigureAwait(false);
                    continue;
                }
                return frame;
            }
            var detail = (await _errors.WaitAsync(ct).ConfigureAwait(false)).Trim();
            throw new HelperModelException($"The Codex helper exited without a reply: {detail}");
        }

        public void Dispose()
        {
            if (Interlocked.Exchange(ref _disposed, 1) != 0)
            {
                return;
            }
            _lifetime.Cancel();
            ProcessUtil.TryKillTree(_process);
            // Let an in-flight handshake finish before disposing its streams.
            _ = _ready.ContinueWith(_ =>
            {
                _process.Dispose();
                _lifetime.Dispose();
            }, TaskScheduler.Default);
        }
    }
}
