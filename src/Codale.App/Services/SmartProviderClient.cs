using System.Diagnostics;
using System.Net.Http;
using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Text.Json;
using Codale.Agents;
using Codale.Core.Agents;

namespace Codale.App.Services;

/// <summary>Text-only requests through installed CLIs or the selected API profile.</summary>
public sealed class SmartProviderClient
{
    private static readonly HttpClient SharedHttp = new() { Timeout = TimeSpan.FromSeconds(120) };
    private readonly HttpClient _http;
    private readonly string _provider;
    private readonly ByokProvider? _profile;
    private readonly Dictionary<string, string> _models;

    public SmartProviderClient(string provider, ByokProvider? profile, IReadOnlyDictionary<string, string> models, HttpClient? http = null)
    {
        _provider = provider;
        _profile = profile;
        _models = models.ToDictionary();
        _http = http ?? SharedHttp;
    }

    public string FeatureIdentity(string feature) => JsonSerializer.Serialize(new { _provider, Profile = _profile?.Key,
        _profile?.BaseUrl, _profile?.AnthropicBaseUrl, Model = ModelFor(feature) });
    public string DefaultModel => _profile?.FlashModel.Trim() ?? "";
    public string ModelFor(string feature) => _models.GetValueOrDefault(feature, "").Trim() is var model
        && model.Length > 0 && !model.Equals("default", StringComparison.OrdinalIgnoreCase) ? model : DefaultModel;
    public string ConfigurationKey => Convert.ToHexString(System.Security.Cryptography.SHA256.HashData(
        System.Text.Encoding.UTF8.GetBytes(JsonSerializer.Serialize(new { _provider, _profile, _models }))));
    public string? AccountId => _provider is "claude" or "codex" && _profile is { IsAccount: true } ? _profile.AccountId : null;
    public bool IsFeatureEnabled(string feature) => _provider != "off" && !_models.GetValueOrDefault(feature, "").Equals("off", StringComparison.OrdinalIgnoreCase);

    public string? ConfigurationError => _provider switch
    {
        "off" => null,
        "claude" or "codex" => CliLocator.Find(_provider) is null ? $"Install and sign in to {_provider} first." : null,
        _ => _profile is not { IsAccount: false, ApiKey.Length: > 0 } ? "Choose an API endpoint profile with an API key." : null,
    };

    public async Task<string[]> ModelsAsync(CancellationToken ct)
    {
        using var request = Request(RequireProfile(), "models");
        request.Method = HttpMethod.Get;
        using var json = await SendAsync(request, ct).ConfigureAwait(false);
        return json.RootElement.GetProperty("data").EnumerateArray().Select(m => m.GetProperty("id").GetString()!)
            .Where(m => !string.IsNullOrWhiteSpace(m)).Order(StringComparer.OrdinalIgnoreCase).ToArray();
    }

    public async Task<string> CompleteAsync(string feature, string system, string input, int tokens, CancellationToken ct)
    {
        feature = feature switch { "shell-command" => "shell", "assessment" => "assess", "rank" => "ranking", _ => feature };
        var model = ModelFor(feature);
        if (!IsFeatureEnabled(feature)) throw new InvalidOperationException("This Smart feature is off.");
        if (_provider is "claude" or "codex") return await CliAsync(model, system, input, ct).ConfigureAwait(false);
        var anthropic = _provider == "anthropic";
        var profile = RequireProfile();
        if (model.Length == 0) throw new InvalidOperationException("Choose a model for this feature or set the profile's Flash model.");
        using var request = Request(profile, anthropic ? "messages" : "chat/completions");
        request.Content = anthropic
            ? JsonContent.Create(new { model, system, max_tokens = Math.Max(1024, tokens), messages = new[] { new { role = "user", content = input } } })
            : JsonContent.Create(new { model, max_completion_tokens = Math.Max(4096, tokens), messages = new[] { new { role = "system", content = system }, new { role = "user", content = input } } });
        using var json = await SendAsync(request, ct).ConfigureAwait(false);
        var text = anthropic ? string.Concat(json.RootElement.GetProperty("content").EnumerateArray()
            .Where(x => x.GetProperty("type").GetString() == "text").Select(x => x.GetProperty("text").GetString()))
            : json.RootElement.GetProperty("choices")[0].GetProperty("message").GetProperty("content").GetString();
        return string.IsNullOrWhiteSpace(text) ? throw new InvalidDataException("The provider returned an empty answer.") : text;
    }

    public string EmbeddingIdentity => _provider == "openai" && _models.GetValueOrDefault("embedding", "") is { Length: > 0 } model && !model.Equals("off", StringComparison.OrdinalIgnoreCase)
        ? $"openai:{_profile?.Key}:{_profile?.BaseUrl}:{model}" : "off";

    public async Task<float[]> EmbedAsync(string text, CancellationToken ct)
    {
        if (EmbeddingIdentity == "off") return [];
        var profile = RequireProfile();
        using var request = Request(profile, "embeddings");
        request.Content = JsonContent.Create(new { model = _models["embedding"], input = text, encoding_format = "float" });
        using var json = await SendAsync(request, ct).ConfigureAwait(false);
        var vector = json.RootElement.GetProperty("data")[0].GetProperty("embedding").EnumerateArray().Select(x => x.GetSingle()).ToArray();
        if (vector.Length == 0 || vector.Any(v => !float.IsFinite(v))) throw new InvalidDataException("Invalid embedding vector.");
        var norm = Math.Sqrt(vector.Sum(v => (double)v * v));
        if (norm <= 0) throw new InvalidDataException("Empty embedding vector.");
        return vector.Select(v => (float)(v / norm)).ToArray();
    }

    private ByokProvider RequireProfile() => _profile is { IsAccount: false, ApiKey.Length: > 0 } p ? p
        : throw new InvalidOperationException("Choose an API endpoint profile with an API key in Smart experience settings.");

    private HttpRequestMessage Request(ByokProvider profile, string path)
    {
        var anthropic = _provider == "anthropic";
        var root = (anthropic ? profile.AnthropicBaseUrl : profile.BaseUrl).TrimEnd('/');
        if (root.Length == 0) throw new InvalidOperationException("The profile does not support this API.");
        if (anthropic && !root.EndsWith("/v1", StringComparison.OrdinalIgnoreCase)) root += "/v1";
        var request = new HttpRequestMessage(HttpMethod.Post, root + "/" + path);
        if (anthropic)
        {
            request.Headers.Add("x-api-key", profile.ApiKey);
            request.Headers.Add("anthropic-version", "2023-06-01");
        }
        else request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", profile.ApiKey);
        return request;
    }

    private async Task<JsonDocument> SendAsync(HttpRequestMessage request, CancellationToken ct)
    {
        using var response = await _http.SendAsync(request, ct).ConfigureAwait(false);
        if (!response.IsSuccessStatusCode) throw new HttpRequestException($"Smart provider returned HTTP {(int)response.StatusCode}. Check the endpoint, model, and credentials.");
        return JsonDocument.Parse(await response.Content.ReadAsStringAsync(ct).ConfigureAwait(false));
    }

    private async Task<string> CliAsync(string model, string system, string input, CancellationToken ct)
    {
        var executable = CliLocator.Find(_provider) ?? throw new InvalidOperationException($"Install and sign in to {_provider} first.");
        if (CliLocator.IsShim(executable)) throw new InvalidOperationException("Smart features require the native CLI executable; install the native CLI package.");
        var folder = Path.Combine(Path.GetTempPath(), "codale-smart-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(folder);
        var output = Path.Combine(folder, "answer.txt");
        var start = BuildCliStartInfo(executable, folder, output, model, system);
        try
        {
            using var process = new Process { StartInfo = start };
            process.Start();
            using var cancellation = ct.Register(() => { try { process.Kill(entireProcessTree: true); } catch (InvalidOperationException) { } });
            var stdout = process.StandardOutput.ReadToEndAsync(ct);
            var stderr = process.StandardError.ReadToEndAsync(ct);
            await process.StandardInput.WriteAsync((_provider == "codex" ? system + "\n\n" : "") + input).ConfigureAwait(false);
            process.StandardInput.Close();
            await process.WaitForExitAsync(ct).ConfigureAwait(false);
            var text = await stdout.ConfigureAwait(false);
            await stderr.ConfigureAwait(false);
            if (process.ExitCode != 0) throw new InvalidOperationException($"{_provider} exited with code {process.ExitCode}. Check CLI sign-in and model access.");
            if (_provider == "codex") text = await File.ReadAllTextAsync(output, ct).ConfigureAwait(false);
            return string.IsNullOrWhiteSpace(text) ? throw new InvalidDataException("The CLI returned an empty answer.") : text.Trim();
        }
        finally { try { Directory.Delete(folder, recursive: true); } catch (IOException) { } catch (UnauthorizedAccessException) { } }
    }

    internal ProcessStartInfo BuildCliStartInfo(string executable, string folder, string output, string model, string system)
    {
        var start = new ProcessStartInfo(executable) { WorkingDirectory = folder, UseShellExecute = false, CreateNoWindow = true,
            RedirectStandardInput = true, RedirectStandardOutput = true, RedirectStandardError = true };
        start.Environment[_provider == "codex" ? CodaleAccount.CodexVariable : CodaleAccount.ClaudeVariable] =
            CodaleAccount.ProfileHome(_provider, AccountId);
        void Add(params string[] args) { foreach (var arg in args) start.ArgumentList.Add(arg); }
        if (_provider == "claude")
        {
            Add("--print", "--output-format", "text", "--tools", "", "--strict-mcp-config", "--mcp-config", "{\"mcpServers\":{}}", "--no-session-persistence");
            Add("--system-prompt", system);
        }
        else Add("exec", "--ephemeral", "--skip-git-repo-check", "--sandbox", "read-only", "--output-last-message", output);
        if (model.Length > 0) Add("--model", model);
        if (_provider == "codex") Add("-");
        return start;
    }
}
