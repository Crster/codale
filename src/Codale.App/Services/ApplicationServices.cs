using Codale.Workspace;
using Codale.Commands;
using Codale.Agents;
using Codale.App.ViewModels;
using Codale.Git;
using Codale.Agents.History;

namespace Codale.App.Services;

/// <summary>Application composition root. Workspaces own the resources they create.</summary>
public sealed class ApplicationServices : IAsyncDisposable
{
    public AppSettingsStore Settings { get; }
    public KeyboardShortcuts Shortcuts { get; }
    public SyntaxSelection Syntax { get; }
    public RecentProjects RecentProjects { get; }
    public CommandHistory CommandHistory { get; }
    public ApplicationDatabase Database { get; }
    public IAgentSessionFactory AgentSessions { get; }
    public IGitRepositoryFactory GitRepositories { get; }
    public ICliHistoryReaderFactory CliHistory { get; }
    public ISmartDecider Decisions { get; }
    public IWorkspaceDataFactory WorkspaceData { get; } = new WorkspaceDataFactory();
    public IProjectCommandService Commands { get; }
    public SmartExperienceHost Smart { get; }

    public ApplicationServices(IAgentSessionFactory? agentSessions = null, IGitRepositoryFactory? gitRepositories = null, ICliHistoryReaderFactory? cliHistory = null)
    {
        Database = new(Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "Codale"));
        Settings = new(Database.Folder);
        Shortcuts = new(Settings);
        Codale.Core.Syntax.SyntaxService.Initialize(Path.Combine(Database.Folder, "syntax"));
        Syntax = new(Settings, Database.Folder, Codale.Core.Syntax.SyntaxService.Store, Codale.Core.Syntax.SyntaxService.Catalog);
        RecentProjects = new(Database);
        CommandHistory = new(Database);
        Smart = new(Settings);
        AgentSessions = agentSessions ?? new AgentSessionFactory();
        GitRepositories = gitRepositories ?? new GitRepositoryFactory();
        CliHistory = cliHistory ?? new CliHistoryReaderFactory();
        Decisions = new SmartDecider(Smart);
        Commands = new ProjectCommandService(configFor: project => WorkspaceData.Open(project).Config);
    }

    private Task? _shutdown;
    public ValueTask DisposeAsync() => new(_shutdown ??= ShutdownAsync());

    private async Task ShutdownAsync()
    {
        try { await Smart.DisposeAsync(); }
        finally
        {
            (WorkspaceData as IDisposable)?.Dispose();
            Shortcuts.Dispose();
            Settings.Dispose();
            Database.Dispose();
        }
    }

    public WorkspaceViewModel CreateWorkspace(string projectPath)
    {
        ObjectDisposedException.ThrowIf(_shutdown is not null, this);
        return new(projectPath, AgentSessions, GitRepositories, Smart, Decisions, Commands, WorkspaceData, CliHistory, Database, CommandHistory, Shortcuts, Syntax);
    }
}
