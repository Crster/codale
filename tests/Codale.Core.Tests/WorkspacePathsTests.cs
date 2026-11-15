using Codale.Core.Projects;

namespace Codale.Core.Tests;

public sealed class WorkspacePathsTests
{
    private static readonly string Root = Path.GetFullPath(Path.Combine(Path.GetTempPath(), "codale-paths"));

    [Fact]
    public void ARelativePathResolvesUnderTheRoot() =>
        Assert.Equal(Path.Combine(Root, "src", "a.cs"), WorkspacePaths.ResolveUnder(Root, "src/a.cs"));

    [Fact]
    public void ARootedPathIsNormalisedNotRebased() =>
        Assert.Equal(Path.Combine(Path.GetTempPath(), "other", "b.cs"),
            WorkspacePaths.ResolveUnder(Root, Path.Combine(Path.GetTempPath(), "other", "b.cs")));

    [Fact]
    public void AnInvalidPathResolvesToNothing() =>
        Assert.Null(WorkspacePaths.ResolveUnder(Root, "bad\0name"));

    [Fact]
    public void AFileBeneathTheRootIsWithinIt() =>
        Assert.True(WorkspacePaths.IsWithin(Path.Combine(Root, "src", "a.cs"), Root));

    [Fact]
    public void TheRootItselfIsWithinItOnlyWhenAskedFor()
    {
        Assert.True(WorkspacePaths.IsWithin(Root, Root));
        Assert.False(WorkspacePaths.IsWithin(Root, Root, includeRoot: false));
    }

    [Fact]
    public void ASiblingThatSharesThePrefixIsNotWithinTheRoot() =>
        Assert.False(WorkspacePaths.IsWithin(Root + "2" + Path.DirectorySeparatorChar + "a.cs", Root));

    [Fact]
    public void ContainmentIgnoresCase() =>
        Assert.True(WorkspacePaths.IsWithin(Path.Combine(Root.ToUpperInvariant(), "a.cs"), Root));

    [Fact]
    public void APathOutsideTheRootIsNotWithinIt() =>
        Assert.False(WorkspacePaths.IsWithin(Path.Combine(Path.GetTempPath(), "elsewhere.cs"), Root));

    [Fact]
    public void ARelativePathIsWithForwardSlashes() =>
        Assert.Equal("src/a.cs", WorkspacePaths.RelativeWithin(Root, Path.Combine(Root, "src", "a.cs")));

    [Fact]
    public void ASiblingPrefixHasNoRelativePath() =>
        Assert.Null(WorkspacePaths.RelativeWithin(Root, Root + "2" + Path.DirectorySeparatorChar + "a.cs"));

    [Fact]
    public void TheRootHasTheDotRelativePath() =>
        Assert.Equal(".", WorkspacePaths.RelativeWithin(Root, Root));
}