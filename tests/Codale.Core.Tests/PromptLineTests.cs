using Codale.Core.Shell;

namespace Codale.Core.Tests;

public sealed class PromptLineTests
{
    [Theory]
    [InlineData(@"PS X:\CrsterSite\codale> git status", "git status")]
    [InlineData(@"X:\CrsterSite\codale>dotnet test", "dotnet test")]
    [InlineData("user@box:~/work$ ls -la", "ls -la")]
    [InlineData("root@box:/# apt update", "apt update")]
    [InlineData(@"PS C:\x> echo a > out.txt", "echo a > out.txt")]
    [InlineData("ls -la", "ls -la")]
    [InlineData("$HOME/bin", "$HOME/bin")]
    public void The_prompt_is_taken_off_and_the_command_kept(string line, string expected) =>
        Assert.Equal(expected, PromptLine.Command(line));

    [Fact]
    public void A_prompt_alone_gives_an_empty_command() =>
        Assert.Equal("", PromptLine.Command(@"PS X:\CrsterSite> "));
}
