using System.Xml.Linq;

namespace Codale.Agents.Tests;

public sealed class ArchitectureBoundaryTests
{
    [Fact]
    public void PresentationDoesNotLocateApplicationServices()
    {
        var directory = Path.Combine(FindRoot(), "src", "Codale.App");
        foreach (var file in Directory.EnumerateFiles(directory, "*.cs", SearchOption.AllDirectories)
            .Where(file => !Path.GetRelativePath(directory, file).Split(Path.DirectorySeparatorChar)
                .Any(part => part is "obj" or "bin")))
        {
            var source = File.ReadAllText(file);
            Assert.False(source.Contains("App.Current.Services", StringComparison.Ordinal),
                $"{Path.GetRelativePath(directory, file)} must receive application services explicitly.");
            Assert.False(source.Contains("AppSettings.", StringComparison.Ordinal),
                $"{Path.GetRelativePath(directory, file)} must receive its settings store explicitly.");
            Assert.False(source.Contains("App.Current.MainWindow", StringComparison.Ordinal),
                $"{Path.GetRelativePath(directory, file)} must receive its project window explicitly.");
            Assert.False(source.Contains("OpenProjectInPlace", StringComparison.Ordinal),
                $"{Path.GetRelativePath(directory, file)} must open projects through its project window.");
        }
    }

    [Fact]
    public void ViewModelsDoNotLocateApplicationServices()
    {
        var directory = Path.Combine(FindRoot(), "src", "Codale.App", "ViewModels");
        foreach (var file in Directory.EnumerateFiles(directory, "*.cs", SearchOption.AllDirectories))
        {
            var source = File.ReadAllText(file);
            Assert.False(source.Contains("App.Current", StringComparison.Ordinal),
                $"{Path.GetFileName(file)} must receive its application dependencies explicitly.");
            Assert.False(source.Contains("AppDb.Open", StringComparison.Ordinal),
                $"{Path.GetFileName(file)} must receive its application database explicitly.");
        }
    }

    [Fact]
    public void ApplicationDoesNotUseStaticWorkspaceDatabaseFallbacks()
    {
        var directory = Path.Combine(FindRoot(), "src", "Codale.App");
        string[] forbidden = ["WorkspaceDb.", "CommandCatalog.", "new HistoryStore(", "new MemoryStore(", "new ConfigStore(", "new EnvStore("];
        foreach (var file in Directory.EnumerateFiles(directory, "*.cs", SearchOption.AllDirectories)
            .Where(file => !Path.GetRelativePath(directory, file).Split(Path.DirectorySeparatorChar)
                .Any(part => part is "obj" or "bin")))
        {
            var source = File.ReadAllText(file);
            foreach (var token in forbidden)
            {
                Assert.False(source.Contains(token, StringComparison.Ordinal),
                    $"{Path.GetRelativePath(directory, file)} must use the composed workspace data context, not {token}.");
            }
        }
    }

    [Fact]
    public void LibrariesDoNotReferenceThePresentationProject()
    {
        var root = FindRoot();
        foreach (var project in Directory.EnumerateFiles(Path.Combine(root, "src"), "*.csproj", SearchOption.AllDirectories))
        {
            if (Path.GetFileName(project) == "Codale.App.csproj") continue;
            var references = XDocument.Load(project).Descendants("ProjectReference")
                .Select(element => (string?)element.Attribute("Include") ?? "");
            Assert.DoesNotContain(references, reference => reference.Contains("Codale.App", StringComparison.OrdinalIgnoreCase));
        }
    }

    [Fact]
    public void ProviderTestsUseTheProductionAssembly()
    {
        var project = XDocument.Load(Path.Combine(FindRoot(), "tests", "Codale.Agents.Tests", "Codale.Agents.Tests.csproj"));
        Assert.DoesNotContain(project.Descendants("Compile"), element =>
            ((string?)element.Attribute("Include") ?? "").Contains("Services", StringComparison.OrdinalIgnoreCase));
        Assert.Equal("Codale.Agents", typeof(Codale.Agents.Providers.SmartProviderClient).Assembly.GetName().Name);
    }

    private static string FindRoot()
    {
        for (var directory = new DirectoryInfo(AppContext.BaseDirectory); directory is not null; directory = directory.Parent)
            if (File.Exists(Path.Combine(directory.FullName, "Codale.slnx"))) return directory.FullName;
        throw new DirectoryNotFoundException("Could not locate the solution root.");
    }
}
