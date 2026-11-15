namespace Codale.Commands.Tests;

public sealed class ProjectCommandServiceTests
{
    [Fact]
    public void SavedCommandsUseTheComposedWorkspaceDataLifetime()
    {
        var root = Directory.CreateTempSubdirectory("codale-command-owner-").FullName;
        using var data = new Codale.Workspace.WorkspaceDataFactory(_ => Path.Combine(root, "database"));
        var service = new ProjectCommandService(configFor: project => data.Open(project).Config);
        service.Add(root, "build", "dotnet build");
        Assert.Contains(service.Discover(root), command => command.Name == "build");
        data.Dispose();
        Assert.Throws<ObjectDisposedException>(() => service.ConfigOf(root));
    }

    private sealed class Detector(string name) : IProjectCommandDetector
    {
        public string Id => name;
        public IReadOnlyList<ProjectCommand> Discover(string projectRoot) =>
            [new ProjectCommand { Name = name, Command = "echo " + name, Source = CommandSource.Codale }];
    }

    [Fact]
    public void InjectedDetectorsDefineTheDiscoveryPipeline()
    {
        IProjectCommandService service = new ProjectCommandService(detectors: [new Detector("first"), new Detector("second")]);
        Assert.Equal(["first", "second"], service.Discover(Path.GetTempPath()).Select(c => c.Name));
    }

    [Fact]
    public void IndependentServicesKeepSavedCommandsInTheirOwnStorage()
    {
        var root = Directory.CreateTempSubdirectory("codale-command-services-").FullName;
        IProjectCommandService first = new ProjectCommandService(_ => Path.Combine(root, "first"));
        IProjectCommandService second = new ProjectCommandService(_ => Path.Combine(root, "second"));
        first.Add(root, "build", "dotnet build");
        Assert.Contains(first.Discover(root), c => c.Name == "build" && c.Command == "dotnet build");
        Assert.DoesNotContain(second.Discover(root), c => c.Name == "build");
    }
}