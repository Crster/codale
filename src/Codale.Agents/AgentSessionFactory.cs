using Codale.Agents.Claude;
using Codale.Agents.Codex;
using Codale.Core.Agents;

namespace Codale.Agents;

/// <summary>Shared inputs for launching a managed chat session.</summary>
public sealed record AgentSessionRequest
{
    public required string CliId { get; init; }
    public required string WorkingDirectory { get; init; }
    public string? Executable { get; init; }
    public string? Model { get; init; }
    public string? Effort { get; init; }
    public string? PermissionMode { get; init; } = AgentPermissionMode.Default;
    public string? ResumeSessionId { get; init; }
    public string? SystemPromptAppend { get; init; }
    public IReadOnlyDictionary<string, string>? Environment { get; init; }
    public IReadOnlyList<McpServerSpec> McpServers { get; init; } = [];
    public IReadOnlyList<string> AdditionalDirectories { get; init; } = [];
    public Func<IReadOnlyList<string>>? AdditionalDirectoriesSource { get; init; }
    public bool PromptSuggestions { get; init; }
    public int? ContextWindow { get; init; }
    public IReadOnlyDictionary<string, bool>? CodexFeatures { get; init; }
    public CodexCustomProvider? CodexProvider { get; init; }
    public string? BackgroundShellGuardCommand { get; init; }
    public string? DisallowedTools { get; init; }
    public bool? Chrome { get; init; }
    public Guid? ClaudeSessionId { get; init; }
}

public interface IAgentSessionFactory
{
    IAgentSession Create(AgentSessionRequest request);
}

/// <summary>Translates common session requests into provider-specific protocol options.</summary>
public sealed class AgentSessionFactory : IAgentSessionFactory
{
    public IAgentSession Create(AgentSessionRequest request)
    {
        ArgumentNullException.ThrowIfNull(request);
        return request.CliId.ToLowerInvariant() switch
        {
            "codex" => new CodexAgentSession(new CodexSessionOptions
            {
                WorkingDirectory = request.WorkingDirectory,
                Executable = request.Executable ?? CliLocator.Find("codex") ?? "codex",
                Model = request.Model, Effort = request.Effort, PermissionMode = request.PermissionMode,
                ResumeSessionId = request.ResumeSessionId, SystemPromptAppend = request.SystemPromptAppend,
                Environment = request.Environment, McpServers = request.McpServers,
                AdditionalDirectoriesSource = request.AdditionalDirectoriesSource,
                PromptSuggestions = request.PromptSuggestions, ContextWindow = request.ContextWindow,
                Features = request.CodexFeatures, CustomProvider = request.CodexProvider,
            }),
            "claude" => new ClaudeAgentSession(new ClaudeSessionOptions
            {
                WorkingDirectory = request.WorkingDirectory,
                Executable = request.Executable ?? CliLocator.Find(CliLocator.Executable) ?? CliLocator.Executable,
                Model = request.Model, Effort = request.Effort, PermissionMode = request.PermissionMode,
                ResumeSessionId = request.ResumeSessionId, SystemPromptAppend = request.SystemPromptAppend,
                Environment = request.Environment, McpServers = request.McpServers,
                AdditionalDirectories = request.AdditionalDirectories, PromptSuggestions = request.PromptSuggestions,
                BackgroundShellGuardCommand = request.BackgroundShellGuardCommand,
                DisallowedTools = request.DisallowedTools, Chrome = request.Chrome, SessionId = request.ClaudeSessionId,
            }),
            _ => throw new ArgumentException($"Unsupported agent CLI: {request.CliId}", nameof(request)),
        };
    }
}
