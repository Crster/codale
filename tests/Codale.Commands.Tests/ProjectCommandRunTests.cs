using Codale.Commands;

namespace Codale.Commands.Tests;

public sealed class ProjectCommandRunTests
{
    private static ProjectCommand Cmd(string name, string command = "run", string? cwd = null) =>
        new() { Name = name, Command = command, WorkingDirectory = cwd, Source = CommandSource.Codale };

    private static readonly IReadOnlyList<ProjectCommand> Commands =
    [
        Cmd("dev server", "npm run dev"),
        Cmd("build", "dotnet build"),
        Cmd("test", "dotnet test"),
    ];

    [Fact]
    public void AnExactNameWinsCaseInsensitively() =>
        Assert.Equal("build", ProjectCommandRun.Resolve(Commands, " BUILD ").Name);

    [Fact]
    public void AUniqueSubstringResolves() =>
        Assert.Equal("dev server", ProjectCommandRun.Resolve(Commands, "dev").Name);

    [Fact]
    public void AnAmbiguousSubstringNamesTheMatches()
    {
        var error = Assert.Throws<InvalidOperationException>(() => ProjectCommandRun.Resolve(Commands, "e"));

        Assert.Contains("matches several commands", error.Message);
        Assert.Contains("\"dev server\"", error.Message);
        Assert.Contains("\"test\"", error.Message);
    }

    [Fact]
    public void AnUnknownNameListsTheAvailableCommands()
    {
        var error = Assert.Throws<InvalidOperationException>(() => ProjectCommandRun.Resolve(Commands, "deploy"));

        Assert.Contains("No command named \"deploy\"", error.Message);
        Assert.Contains("\"dev server\"", error.Message);
    }

    [Fact]
    public void NoSavedCommandsSaysHowToAddOne()
    {
        var error = Assert.Throws<InvalidOperationException>(() => ProjectCommandRun.Resolve([], "anything"));

        Assert.Contains("add_command", error.Message);
    }

    [Fact]
    public void AWorkingDirectoryIsResolvedUnderTheRoot()
    {
        var root = Path.GetFullPath(Path.Combine(Path.GetTempPath(), "codale-run"));

        Assert.Equal(Path.GetFullPath(Path.Combine(root, "web")), ProjectCommandRun.WorkingDirectoryFor(Cmd("web", cwd: "web"), root));
    }

    [Fact]
    public void NoWorkingDirectoryMeansTheRoot()
    {
        var root = Path.GetFullPath(Path.GetTempPath());

        Assert.Equal(root, ProjectCommandRun.WorkingDirectoryFor(Cmd("build"), root));
    }

    [Fact]
    public void AnInvalidWorkingDirectoryFallsBackToTheRoot()
    {
        var root = Path.GetFullPath(Path.GetTempPath());

        Assert.Equal(root, ProjectCommandRun.WorkingDirectoryFor(Cmd("build", cwd: "bad\0path"), root));
    }

    [Fact]
    public void ALiveRunWithTheSameNameCoversTheCommand() =>
        Assert.True(ProjectCommandRun.IsCoveredBy(Cmd("dev server", "npm run dev"), "Dev Server", "something else"));

    [Fact]
    public void ALiveRunWithTheSameCommandLineCoversTheCommand() =>
        Assert.True(ProjectCommandRun.IsCoveredBy(Cmd("dev server", "npm run dev"), "other", "npm run dev"));

    [Fact]
    public void ALiveRunForAnotherCommandDoesNotCover() =>
        Assert.False(ProjectCommandRun.IsCoveredBy(Cmd("dev server", "npm run dev"), "build", "dotnet build"));

    [Fact]
    public void ACommandLineIsComparedExactly() =>
        Assert.False(ProjectCommandRun.IsCoveredBy(Cmd("dev server", "npm run dev"), null, "NPM RUN DEV"));
}
