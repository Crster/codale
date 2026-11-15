using Codale.Core.Agents;

namespace Codale.Core.Tests;

public sealed class McpHostExecutablesTests : IDisposable
{
    private readonly string _root = Path.Combine(Path.GetTempPath(), "codale-mcp-hosts-" + Guid.NewGuid().ToString("N"));

    public void Dispose()
    {
        try
        {
            Directory.Delete(_root, recursive: true);
        }
        catch (IOException)
        {
        }
        catch (UnauthorizedAccessException)
        {
        }
    }

    private string Install(string baseDirectory, string name, string content = "exe")
    {
        var folder = Path.Combine(baseDirectory, name);
        Directory.CreateDirectory(folder);
        var exe = Path.Combine(folder, name + ".exe");
        File.WriteAllText(exe, content);
        File.WriteAllText(Path.Combine(folder, name + ".dll"), "library");
        return exe;
    }

    [Fact]
    public void TheSourcePathIsTheFolderAndExecutableNamedAfterTheHost()
    {
        var hosts = new McpHostExecutables(Path.Combine(_root, "bin"), Path.Combine(_root, "staging"));

        Assert.Equal(Path.Combine(_root, "bin", "Codale.Mcp.Browser", "Codale.Mcp.Browser.exe"), hosts.SourcePath("Codale.Mcp.Browser"));
    }

    [Fact]
    public void AHostIsPresentOnlyOnceItsExecutableExists()
    {
        var bin = Path.Combine(_root, "bin");
        var hosts = new McpHostExecutables(bin, Path.Combine(_root, "staging"));

        Assert.False(hosts.Exists("Codale.Mcp.Phone"));
        Install(bin, "Codale.Mcp.Phone");
        Assert.True(hosts.Exists("Codale.Mcp.Phone"));
    }

    [Fact]
    public void AnInstalledHostOutsideWindowsAppsIsRunFromItsInstallPath()
    {
        var bin = Path.Combine(_root, "bin");
        var exe = Install(bin, "Codale.Mcp.Tasks");
        var hosts = new McpHostExecutables(bin, Path.Combine(_root, "staging"));

        Assert.Equal(exe, hosts.Runnable("Codale.Mcp.Tasks"));
        Assert.False(Directory.Exists(Path.Combine(_root, "staging")));
    }

    [Fact]
    public void APackagedHostIsCopiedToStagingAndRunFromTheCopy()
    {
        var packaged = Path.Combine(_root, "WindowsApps", "pkg");
        Install(packaged, "Codale.Mcp.Tasks", "exe-contents");
        var staging = Path.Combine(_root, "staging");
        var hosts = new McpHostExecutables(packaged, staging);

        var runnable = hosts.Runnable("Codale.Mcp.Tasks");

        Assert.StartsWith(staging, runnable, StringComparison.OrdinalIgnoreCase);
        Assert.Equal("exe-contents", File.ReadAllText(runnable));
        Assert.True(File.Exists(Path.Combine(Path.GetDirectoryName(runnable)!, "Codale.Mcp.Tasks.dll")));
    }

    [Fact]
    public void ASecondRequestReusesTheStagedCopy()
    {
        var packaged = Path.Combine(_root, "WindowsApps", "pkg");
        Install(packaged, "Codale.Mcp.Tasks");
        var hosts = new McpHostExecutables(packaged, Path.Combine(_root, "staging"));

        var first = hosts.Runnable("Codale.Mcp.Tasks");
        var second = hosts.Runnable("Codale.Mcp.Tasks");

        Assert.Equal(first, second);
    }

    [Fact]
    public void APackagedHostThatIsMissingFallsBackToItsSourcePath()
    {
        var packaged = Path.Combine(_root, "WindowsApps", "pkg");
        Directory.CreateDirectory(packaged);
        var hosts = new McpHostExecutables(packaged, Path.Combine(_root, "staging"));

        Assert.Equal(hosts.SourcePath("Codale.Mcp.Phone"), hosts.Runnable("Codale.Mcp.Phone"));
    }
}