using Codale.Core.Projects;
namespace Codale.Core.Tests;

public sealed class ProjectKnowledgePathsTests
{
    [Fact]
    public void Temporary_directory_is_under_local_app_data()
    {
        Assert.Equal(Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "Codale", "temp"), CodalePaths.TempDirectory);
    }

    [Theory]
    [InlineData("MyWorkspace", "my_workspace")]
    [InlineData("HTTPServer-Example", "http_server_example")]
    [InlineData("con", "con_workspace")]
    public void Workspace_directory_name_uses_safe_snake_case(string name, string expected)
    {
        Assert.Equal(expected, ProjectPaths.WorkspaceName(Path.Combine(Path.GetTempPath(), name)));
    }

    [Fact]
    public void Linked_worktrees_share_the_repository_identity()
    {
        var root = Directory.CreateTempSubdirectory("codale-worktree-identity-").FullName;
        try
        {
            var main = Path.Combine(root, "main");
            var other = Path.Combine(root, "other");
            var git = Path.Combine(main, ".git");
            var linked = Path.Combine(git, "worktrees", "other");
            Directory.CreateDirectory(linked);
            Directory.CreateDirectory(other);
            Directory.CreateDirectory(Path.Combine(main, "src"));
            File.WriteAllText(Path.Combine(other, ".git"), "gitdir: " + linked);
            File.WriteAllText(Path.Combine(linked, "commondir"), "../..");
            Assert.Equal(ProjectPaths.RepositoryIdentity(main), ProjectPaths.RepositoryIdentity(other));
            Assert.Equal(ProjectPaths.RepositoryIdentity(main), ProjectPaths.RepositoryIdentity(Path.Combine(main, "src")));
            Assert.NotEqual(ProjectPaths.RepositoryIdentity(main), ProjectPaths.RepositoryIdentity(root));
        }
        finally { Directory.Delete(root, true); }
    }
}
