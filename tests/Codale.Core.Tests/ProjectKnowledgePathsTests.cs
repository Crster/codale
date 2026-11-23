using Codale.Core.Projects;
namespace Codale.Core.Tests;

public sealed class ProjectKnowledgePathsTests
{
    [Fact]
    public void Temporary_directory_is_under_local_app_data()
    {
        Assert.Equal(Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "Codale", "Temp"), CodalePaths.TempDirectory);
    }

    [Theory]
    [InlineData("MyWorkspace", "my_workspace")]
    [InlineData("HTTPServer-Example", "http_server_example")]
    [InlineData("con", "con_workspace")]
    public void Workspace_model_directory_uses_a_safe_snake_case_name(string name, string expected)
    {
        var path = ProjectPaths.WorkspaceModelDirectory(Path.Combine(Path.GetTempPath(), name));
        Assert.Equal(expected, Path.GetFileName(path));
        Assert.Equal("workspace", Path.GetFileName(Path.GetDirectoryName(path)));
    }

    [Fact]
    public void Linked_worktrees_share_the_repository_database_and_session_logs()
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
            Assert.Equal(ProjectPaths.FeatureDatabasePath(main), ProjectPaths.FeatureDatabasePath(other));
            Assert.Equal(ProjectPaths.FeatureDatabasePath(main), ProjectPaths.FeatureDatabasePath(Path.Combine(main, "src")));
            Assert.NotEqual(ProjectPaths.FeatureDatabasePath(main), ProjectPaths.FeatureDatabasePath(root));
        }
        finally { Directory.Delete(root, true); }
    }
}
