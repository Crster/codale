using Codale.Core.Projects;
namespace Codale.Core.Tests;

public sealed class ProjectKnowledgePathsTests
{
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
