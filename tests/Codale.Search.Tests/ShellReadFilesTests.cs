namespace Codale.Search.Tests;
public sealed class ShellReadFilesTests
{
    [Theory]
    [InlineData("Get-Content -LiteralPath 'src/Reader.cs' | Select-Object -First 10")]
    [InlineData("cat src/Reader.cs")]
    [InlineData("python -c \"from pathlib import Path; print(Path('src/Reader.cs').read_text())\"")]
    [InlineData("node -e \"console.log(require('fs').readFileSync('src/Reader.cs','utf8'))\"")]
    [InlineData("sed -n '1,20p' src/Reader.cs")]
    [InlineData("head -20 src/Reader.cs")]
    [InlineData("rg -n value src/Reader.cs")]
    [InlineData("Get-Content src/*.cs")]
    [InlineData("cd src; cat Reader.cs")]
    [InlineData("powershell -Command \"Get-Content 'src/Reader.cs'\"")]
    public void TracksReaders(string command)
    {
        WithProject(root => Assert.Contains("src/Reader.cs", ShellReadFiles.Find(root, command, root, null)));
    }
    [Fact]
    public void TracksSearchOutputAndHonorsCwd()
    {
        WithProject(root =>
        {
            Assert.Contains("src/Reader.cs", ShellReadFiles.Find(root, "rg -n value .", root, "src/Reader.cs:3:value"));
            Assert.Contains("src/Reader.cs", ShellReadFiles.Find(root, "cat Reader.cs", Path.Combine(root, "src"), null));
            Assert.Empty(ShellReadFiles.Find(root, "rg --files", root, "src/Reader.cs"));
            Assert.Empty(ShellReadFiles.Find(root, "dotnet build", root, "src/Reader.cs:3:value"));
            Assert.Empty(ShellReadFiles.Find(root, "cat ../outside.cs", root, null));
        });
    }
    private static void WithProject(Action<string> action)
    {
        var root = Directory.CreateTempSubdirectory("shell-reads-").FullName;
        try
        {
            Directory.CreateDirectory(Path.Combine(root, "src"));
            File.WriteAllText(Path.Combine(root, "src", "Reader.cs"), "class Reader { int value; }");
            action(root);
        }
        finally { Directory.Delete(root, true); }
    }
}
