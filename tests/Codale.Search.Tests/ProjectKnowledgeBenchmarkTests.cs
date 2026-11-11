using System.Text;
using Codale.Core.Agents;
using Codale.Core.Features;
using Codale.Core.Helper;
using Codale.Db;
using Xunit.Abstractions;

namespace Codale.Search.Tests;

/// <summary>Offline context-volume comparison; actual provider token/exploration savings require live turns.</summary>
public sealed class ProjectKnowledgeBenchmarkTests(ITestOutputHelper output)
{
    [Fact]
    public async Task Repeated_tasks_reuse_context_and_topic_changes_stay_bounded()
    {
        var root = Directory.CreateTempSubdirectory("codale-retrieval-benchmark-").FullName;
        try
        {
            var source = Path.Combine(root, "source");
            Directory.CreateDirectory(source);
            const string auth = "class Authentication { bool RefreshToken(string credential) => ValidateRotatingCredential(credential); }";
            const string chart = "class RevenueChart { void RenderGraph() { DrawRevenueBars(); } }";
            File.WriteAllText(Path.Combine(source, "Authentication.cs"), auth);
            File.WriteAllText(Path.Combine(source, "RevenueChart.cs"), chart);
            using var store = new CodaleDb(Path.Combine(root, "project.db"));
            using var index = new ProjectKnowledgeIndex(store);
            await index.RefreshAsync(source);
            var delivered = new HashSet<string>();
            var newBytes = 0;
            var oldBytes = 0;
            foreach (var (query, name, code, oldSeedDue) in new[]
            {
                ("RefreshToken credential", "Authentication", auth, true),
                ("RefreshToken credential", "Authentication", auth, false),
                ("RevenueChart RenderGraph", "RevenueChart", chart, true),
                ("RefreshToken credential", "Authentication", auth, true),
            })
            {
                var result = index.Retrieve(source, query, "", delivered);
                delivered.UnionWith(result.Keys);
                var currentBytes = Encoding.UTF8.GetByteCount(query + (result.Text.Length > 0 ? SessionBriefing.ContextBlock(result.Text) : ""));
                newBytes += currentBytes;
                var feature = new FeatureInfo { Id = name, Name = name, Summary = string.Join("\n", Enumerable.Repeat("Earlier verified implementation decisions and completed work.", 30)) };
                var oldSeed = oldSeedDue ? FeatureContext.Build(feature,
                    [new HandoffEntry(HandoffRole.Assistant, string.Join("\n", Enumerable.Repeat("Completed checks; keep existing behavior and source references.", 30)))],
                    [name + ".cs"], [new FileHit(name, name + ".cs", 1, code)]) : "";
                var baselineBytes = Encoding.UTF8.GetByteCount(query + (oldSeed.Length > 0 ? SessionBriefing.ContextBlock(oldSeed) : ""));
                oldBytes += baselineBytes;
                output.WriteLine($"{name}: old fallback input bytes={baselineBytes}, indexed input bytes={currentBytes}, references={result.Keys.Count}, lookup ms={result.ElapsedMilliseconds}");
                Assert.True(currentBytes - Encoding.UTF8.GetByteCount(query) <= 4000);
            }
            output.WriteLine($"TOTAL: old fallback input bytes={oldBytes}; indexed input bytes={newBytes}. UTF-8 bytes are conservative token upper bounds, not measured model tokens. Model output and exploration calls were not measured.");
            Assert.True(newBytes < oldBytes);
        }
        finally
        {
            try { Directory.Delete(root, true); } catch (IOException) { }
        }
    }
}
