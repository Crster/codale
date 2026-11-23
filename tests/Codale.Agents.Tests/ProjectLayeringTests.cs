using System.Xml.Linq;

namespace Codale.Agents.Tests;

/// <summary>
/// The declared project layering: which source project may reference which. The matrix is the contract. A new project
/// reference fails <see cref="ProjectReferencesMatchTheDeclaredLayering"/> until the matrix is updated on purpose, so the
/// boundary changes only by decision.
/// </summary>
public sealed class ProjectLayeringTests
{
    private static readonly Dictionary<string, string[]> Declared = new()
    {
        ["Codale.Core"] = [],
        ["Codale.Db"] = ["Codale.Core"],
        ["Codale.Editor"] = ["Codale.Core"],
        ["Codale.Git"] = ["Codale.Core"],
        ["Codale.Terminal"] = ["Codale.Core"],
        ["Codale.Smart"] = ["Codale.Core", "Codale.Workspace"],
        ["Codale.Workspace"] = ["Codale.Core", "Codale.Db"],
        ["Codale.Agents"] = ["Codale.Core", "Codale.Workspace"],
        ["Codale.Commands"] = ["Codale.Workspace"],
        ["Codale.Search"] = ["Codale.Core", "Codale.Db", "Codale.Smart", "Codale.Workspace"],
        ["Codale.Settings"] = ["Codale.Agents", "Codale.Db", "Codale.Workspace"],
        ["Codale.Mcp.Common"] = [],
        ["Codale.Browser"] = [],
        ["Codale.Mcp.Browser"] = ["Codale.Core", "Codale.Mcp.Common"],
        ["Codale.Mcp.Computer"] = ["Codale.Mcp.Common"],
        ["Codale.Mcp.Phone"] = ["Codale.Mcp.Common"],
        ["Codale.Mcp.Tasks"] = ["Codale.Core", "Codale.Commands", "Codale.Workspace", "Codale.Mcp.Common"],
        ["Codale.App"] =
        [
            "Codale.Agents", "Codale.Commands", "Codale.Core", "Codale.Db", "Codale.Editor", "Codale.Git",
            "Codale.Browser", "Codale.Mcp.Browser", "Codale.Mcp.Computer", "Codale.Mcp.Phone", "Codale.Mcp.Tasks",
            "Codale.Search", "Codale.Settings", "Codale.Smart", "Codale.Terminal", "Codale.Workspace",
        ],
    };

    [Fact]
    public void ProjectReferencesMatchTheDeclaredLayering()
    {
        var actual = SourceProjectReferences();

        Assert.Equal(Declared.Keys.OrderBy(k => k), actual.Keys.OrderBy(k => k));
        foreach (var (project, references) in actual)
        {
            Assert.True(Declared[project].Order().SequenceEqual(references.Order()),
                $"{project} references [{string.Join(", ", references.Order())}] but the layering declares [{string.Join(", ", Declared[project].Order())}]. " +
                "Update the layering in ProjectLayeringTests if this reference is intended.");
        }
    }

    [Fact]
    public void CoreDependsOnNothing() =>
        Assert.Empty(SourceProjectReferences()["Codale.Core"]);

    [Fact]
    public void SmartOnlyUsesCoreAndWorkspaceStorage() =>
        Assert.Equal(["Codale.Core", "Codale.Workspace"], SourceProjectReferences()["Codale.Smart"].Order().ToArray());

    [Fact]
    public void AgentsDoNotReachTheSmartOrTheApplication()
    {
        var references = SourceProjectReferences()["Codale.Agents"];
        Assert.DoesNotContain("Codale.Smart", references);
        Assert.DoesNotContain("Codale.Settings", references);
        Assert.DoesNotContain("Codale.App", references);
    }

    [Fact]
    public void NothingReferencesTheApplicationExceptItself()
    {
        foreach (var (project, list) in SourceProjectReferences())
        {
            if (project is "Codale.App") continue;
            Assert.DoesNotContain("Codale.App", list);
        }

    }

    [Fact]
    public void TheMcpHostsStayOutOfTheDomainLibraries()
    {
        var references = SourceProjectReferences();
        foreach (var domain in new[] { "Codale.Agents", "Codale.Workspace", "Codale.Search", "Codale.Smart", "Codale.Settings" })
        {
            Assert.DoesNotContain(references[domain], name => name.StartsWith("Codale.Mcp.", StringComparison.Ordinal));
        }
    }

    private static Dictionary<string, string[]> SourceProjectReferences()
    {
        var src = Path.Combine(FindRoot(), "src");
        var result = new Dictionary<string, string[]>(StringComparer.Ordinal);
        foreach (var project in Directory.EnumerateFiles(src, "*.csproj", SearchOption.AllDirectories)
            .Where(p => !p.Split(Path.DirectorySeparatorChar).Any(part => part is "obj" or "bin")))
        {
            var name = Path.GetFileNameWithoutExtension(project);
            // One ProjectReference may name several projects, separated by semicolons (the app bundles its MCP hosts so).
            var references = XDocument.Load(project).Descendants("ProjectReference")
                .SelectMany(element => ((string?)element.Attribute("Include") ?? "").Split(';'))
                .Select(include => Path.GetFileNameWithoutExtension(include.Trim()))
                .Where(reference => reference.Length > 0)
                .Distinct()
                .ToArray();
            result[name] = references;
        }

        return result;
    }

    private static string FindRoot()
    {
        for (var directory = new DirectoryInfo(AppContext.BaseDirectory); directory is not null; directory = directory.Parent)
            if (File.Exists(Path.Combine(directory.FullName, "Codale.slnx"))) return directory.FullName;
        throw new DirectoryNotFoundException("Could not locate the solution root.");
    }
}
