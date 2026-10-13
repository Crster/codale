using Codale.Agents.Antigravity;

namespace Codale.Agents.History;

/// <summary>
/// Registry of history readers for all supported AI coding agent CLIs.
/// Resolves the appropriate <see cref="ICliHistoryReader"/> for any CLI running in a terminal tab.
/// </summary>
public sealed class CliHistoryRegistry
{
    private readonly string _projectPath;
    private readonly Dictionary<string, ICliHistoryReader> _readers = new(StringComparer.OrdinalIgnoreCase);

    public CliHistoryRegistry(string projectPath)
    {
        _projectPath = projectPath;
        RegisterDefaults();
    }

    private void RegisterDefaults()
    {
        var home = Environment.GetFolderPath(Environment.SpecialFolder.UserProfile);
        var appData = Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData);

        // Dedicated custom readers
        Register(new AgyHistoryReader(_projectPath));
        Register(new AiderHistoryReader(_projectPath));
        Register(new GooseHistoryReader(_projectPath));

        // JSONL / prompt log readers for common CLIs
        Register(new JsonlPromptHistoryReader("gemini", _projectPath,
        [
            Path.Combine(home, ".gemini", "history.jsonl"),
            Path.Combine(home, ".gemini", "history.json"),
            Path.Combine(_projectPath, ".gemini", "history.jsonl"),
        ]));

        Register(new JsonlPromptHistoryReader("qwen", _projectPath,
        [
            Path.Combine(home, ".qwen", "history.jsonl"),
            Path.Combine(home, ".qwen-code", "history.jsonl"),
            Path.Combine(_projectPath, ".qwen", "history.jsonl"),
        ]));

        Register(new JsonlPromptHistoryReader("copilot", _projectPath,
        [
            Path.Combine(home, ".copilot", "history"),
            Path.Combine(appData, "github-copilot", "history.jsonl"),
            Path.Combine(_projectPath, ".copilot", "history.jsonl"),
        ]));

        Register(new JsonlPromptHistoryReader("codex", _projectPath,
        [
            Path.Combine(home, ".codex", "history.jsonl"),
            Path.Combine(_projectPath, ".codex", "history.jsonl"),
        ]));

        Register(new JsonlPromptHistoryReader("opencode", _projectPath,
        [
            Path.Combine(home, ".opencode", "history.jsonl"),
            Path.Combine(_projectPath, ".opencode", "history.jsonl"),
        ]));

        Register(new JsonlPromptHistoryReader("cursor", _projectPath,
        [
            Path.Combine(home, ".cursor", "history.jsonl"),
            Path.Combine(_projectPath, ".cursor", "history.jsonl"),
        ]));
    }

    public void Register(ICliHistoryReader reader) => _readers[reader.CliId] = reader;

    public bool IsSupported(string? cliId) =>
        !string.IsNullOrWhiteSpace(cliId) && _readers.ContainsKey(cliId);

    public ICliHistoryReader? GetReader(string? cliId) =>
        !string.IsNullOrWhiteSpace(cliId) && _readers.TryGetValue(cliId, out var reader) ? reader : null;

    public IReadOnlyCollection<ICliHistoryReader> AllReaders => _readers.Values;
}
