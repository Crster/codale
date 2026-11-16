using Codale.Search.Engine;
namespace Codale.Search.Tests;

public sealed class PromptFileLocatorTests
{
    [Theory]
    [InlineData("commit all changes", false)]
    [InlineData("please build the project", false)]
    [InlineData("rename src/A.cs to src/B.cs", false)]
    [InlineData("fix the login timeout", true)]
    [InlineData("why does the build fail on login?", true)]
    public void SkipsOperations(string prompt, bool expected) =>
        Assert.Equal(expected, PromptFileLocator.ShouldRetrieve(prompt, false));

    [Fact]
    public void AttachmentsSkipRetrieval() =>
        Assert.False(PromptFileLocator.ShouldRetrieve("fix login timeout", true));

    [Fact]
    public async Task ReturnsUsefulPathsAndKeywordsWithoutSourceAndExcludesKnownFiles()
    {
        var root = Directory.CreateTempSubdirectory("locator-").FullName;
        try
        {
            Directory.CreateDirectory(Path.Combine(root, "src"));
            File.WriteAllText(Path.Combine(root, "src", "LoginTimeout.cs"),
                "class LoginTimeout { /* private implementation marker */ int timeout = 30; }");
            File.WriteAllText(Path.Combine(root, "src", "Unrelated.cs"), "class Unrelated { }");
            var result = await PromptFileLocator.RetrieveAsync(root, "fix login timeout");
            Assert.Contains("src/LoginTimeout.cs", result.Files);
            Assert.DoesNotContain("src/Unrelated.cs", result.Files);
            Assert.Contains("timeout", result.Text.ToLowerInvariant());
            Assert.DoesNotContain("private implementation marker", result.Text);
            Assert.True(System.Text.Encoding.UTF8.GetByteCount(result.Text) < 1600);
            var shellKnown = ShellReadFiles.Find(root, "Get-Content src/LoginTimeout.cs", root, null);
            var afterShellRead = await PromptFileLocator.RetrieveAsync(root, "fix login timeout",
                knownFiles: new HashSet<string>(shellKnown, StringComparer.OrdinalIgnoreCase));
            Assert.DoesNotContain("src/LoginTimeout.cs", afterShellRead.Files);
            var excluded = await PromptFileLocator.RetrieveAsync(root, "fix login timeout",
                knownFiles: new HashSet<string>(result.Files, StringComparer.OrdinalIgnoreCase));
            Assert.Empty(excluded.Files);
            Assert.Empty(excluded.Text);
        }
        finally { SourceIndex.For(root).Dispose(); Directory.Delete(root, true); }
    }
}
