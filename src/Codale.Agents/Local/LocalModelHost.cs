using System.Text;
using System.Text.Json;

using LLama;
using LLama.Common;
using LLama.Sampling;

using Codale.Core.Helper;

namespace Codale.Agents.Local;

/// <summary>
/// The fine-tuned GGUF loaded in-process through llama.cpp. One instance per model file; the weights load
/// on the first request and requests run one at a time.
/// </summary>
public sealed class LocalModelHost(string modelPath) : IDisposable
{
    private const int TaskContextSize = 8192;
    private const int DefaultMaxTokens = 1800;

    private const int InlineContextSize = 2048;

    private readonly SemaphoreSlim _gate = new(1, 1);

    /// <summary>Completions run beside long background jobs, not behind them; only one completion runs at a time.</summary>
    private readonly SemaphoreSlim _inlineGate = new(1, 1);

    private readonly Lock _loadLock = new();
    private LLamaWeights? _weights;
    private bool _disposed;

    public string ModelPath { get; } = modelPath;

    /// <summary>Why the model cannot be used (missing file, wrong prompt format), or null when it looks usable.</summary>
    public string? Problem()
    {
        if (!File.Exists(ModelPath))
        {
            return $"The local model file was not found: {ModelPath}";
        }

        var manifest = Path.Combine(Path.GetDirectoryName(ModelPath) ?? "", "model.json");
        if (!File.Exists(manifest))
        {
            return null;
        }

        try
        {
            using var document = JsonDocument.Parse(File.ReadAllText(manifest));
            var format = document.RootElement.TryGetProperty("format", out var f) ? f.GetString() : null;
            return format == CodalePromptFormat.FormatId
                ? null
                : $"The local model uses prompt format '{format}', but Codale implements '{CodalePromptFormat.FormatId}'.";
        }
        catch (JsonException ex)
        {
            return $"model.json is not valid: {ex.Message}";
        }
    }

    /// <summary>Runs one fully rendered prompt and returns the reply, without the stop token.</summary>
    public async Task<string> GenerateAsync(string prompt, int maxTokens, string? grammar, CancellationToken ct)
    {
        await _gate.WaitAsync(ct).ConfigureAwait(false);
        try
        {
            ObjectDisposedException.ThrowIf(_disposed, this);
            var weights = await Task.Run(EnsureLoaded, ct).ConfigureAwait(false);
            var executor = new StatelessExecutor(weights, CreateParams());
            var inference = new InferenceParams
            {
                MaxTokens = maxTokens > 0 ? maxTokens : DefaultMaxTokens,
                AntiPrompts = [.. CodalePromptFormat.Stops],
                SamplingPipeline = new DefaultSamplingPipeline
                {
                    Temperature = 0,
                    Grammar = grammar is null ? null : new Grammar(grammar, "root"),
                },
            };

            var reply = new StringBuilder();
            await foreach (var piece in executor.InferAsync(prompt, inference, ct).ConfigureAwait(false))
            {
                reply.Append(piece);
            }

            var text = reply.ToString();
            foreach (var stop in CodalePromptFormat.Stops)
            {
                var at = text.IndexOf(stop, StringComparison.Ordinal);
                if (at >= 0)
                {
                    text = text[..at];
                }
            }

            return text.Trim();
        }
        finally
        {
            _gate.Release();
        }
    }

    /// <summary>
    /// Continues a fully rendered prompt for a keystroke-driven completion: greedy, a small context, and
    /// cut at the first line break once some text has come (a reply that opens with a break keeps its first line).
    /// A newer request cancels the older one through <paramref name="ct"/>.
    /// </summary>
    public async Task<string> CompleteInlineAsync(string prompt, int maxTokens, CancellationToken ct)
    {
        await _inlineGate.WaitAsync(ct).ConfigureAwait(false);
        try
        {
            ObjectDisposedException.ThrowIf(_disposed, this);

            // An uncontended gate finishes on the caller's thread, which for a keystroke is the UI:
            // the weights load (seconds, the first time) must happen off it.
            var weights = await Task.Run(EnsureLoaded, ct).ConfigureAwait(false);
            var executor = new StatelessExecutor(weights, CreateParams(InlineContextSize));
            var inference = new InferenceParams
            {
                MaxTokens = maxTokens,
                AntiPrompts = [.. CodalePromptFormat.Stops],
                SamplingPipeline = new DefaultSamplingPipeline { Temperature = 0 },
            };

            var reply = new StringBuilder();
            await foreach (var piece in executor.InferAsync(prompt, inference, ct).ConfigureAwait(false))
            {
                reply.Append(piece);
                if (reply.ToString().TrimStart('\n').Contains('\n'))
                {
                    break;
                }
            }

            var text = reply.ToString();
            foreach (var stop in CodalePromptFormat.Stops)
            {
                var at = text.IndexOf(stop, StringComparison.Ordinal);
                if (at >= 0)
                {
                    text = text[..at];
                }
            }

            var lead = text.Length - text.TrimStart('\n').Length;
            var end = text.IndexOf('\n', lead);
            return end < 0 ? text : text[..end];
        }
        finally
        {
            _inlineGate.Release();
        }
    }

    private ModelParams CreateParams(int contextSize = TaskContextSize) => new(ModelPath)
    {
        ContextSize = (uint)contextSize,
        GpuLayerCount = 99,
        FlashAttention = true,
    };

    private LLamaWeights EnsureLoaded()
    {
        lock (_loadLock)
        {
            return _weights ?? LoadWeights();
        }
    }

    private LLamaWeights LoadWeights()
    {
        if (Problem() is { } problem)
        {
            throw new HelperModelException(problem);
        }

        try
        {
            LocalRuntime.Configure();
            return _weights = LLamaWeights.LoadFromFile(CreateParams());
        }
        catch (Exception ex)
        {
            throw new HelperModelException($"Could not load the local model: {ex.Message}", ex);
        }
    }

    public void Dispose()
    {
        _gate.Wait();
        _inlineGate.Wait();
        try
        {
            _disposed = true;
            _weights?.Dispose();
            _weights = null;
        }
        finally
        {
            _inlineGate.Release();
            _gate.Release();
        }
    }
}
