using Codale.Agents.History;
using Codale.Workspace;

namespace Codale.Agents.Tests;

public sealed class FeatureLogIndexPageTests
{
    [Fact]
    public void Index_pages_resume_when_the_current_log_moves_to_an_archive()
    {
        var root = Directory.CreateTempSubdirectory("codale-topic-history-").FullName;
        try
        {
            using var data = new WorkspaceDataFactory(_ => root);
            var log = new FeatureLog(data.Open("project").History, "feature", "chat");
            log.AppendUser("improve credential storage");
            var first = log.ReadIndexPage(default, 1);
            Assert.Equal("user", Assert.Single(first.Records).GetProperty("type").GetString());
            log.EndTurn("success", root, ["Writer.cs"], []);
            log.Archive();
            log.AppendUser("follow up");

            var archived = log.ReadIndexPage(first.Next, 2);
            var end = Assert.Single(archived.Records);
            Assert.Equal("turnEnd", end.GetProperty("type").GetString());
            Assert.Equal("Writer.cs", end.GetProperty("edited")[0].GetString());
            Assert.Equal(1, archived.Next.Generation);
            var current = log.ReadIndexPage(archived.Next, 2);
            Assert.Equal("follow up", Assert.Single(current.Records).GetProperty("text").GetString());
            Assert.Equal(current.Next, log.ReadIndexPage(current.Next, 2).Next);
        }
        finally { Directory.Delete(root, recursive: true); }
    }
}
