using Codale.Core.Agents;
using Codale.Core.Helper;
using Codale.Core.Projects;
using Codale.Db;
using Codale.Search.Engine;
using Codale.Smart;
using Xunit.Abstractions;

namespace Codale.Search.Tests;

/// <summary>
/// The app's global search (SearchViewModel's SourceExplorer setup) on a real project with the user's own Smart models.
/// Env-gated: CODALE_SEARCH_PROJECT, CODALE_SEARCH_QUERY (";"-separated), CODALE_LOCATOR_SETTINGS (folder with settings.json),
/// CODALE_LOCATOR_DB (copy of the project's project-db, reused read-only for vectors), CODALE_LOCATOR_EMBED=1.
/// </summary>
public sealed class GlobalSearchEvalTests(ITestOutputHelper output) : IDisposable
{
    private readonly string _work = Directory.CreateTempSubdirectory("codale-search-eval-").FullName;

    public void Dispose()
    {
        try { Directory.Delete(_work, true); } catch (IOException) { } catch (UnauthorizedAccessException) { }
    }

    /// <summary>The Smart experience as an ISearchModel, logging what the model was asked and answered.</summary>
    private sealed class LoggedModel(SmartExperience smart, string project, ITestOutputHelper output) : ISearchModel
    {
        public async Task<ToolCall?> NextCallAsync(string system, string conversation, IReadOnlyList<ToolDefinition> tools, CancellationToken ct)
        {
            output.WriteLine($"   >> model call [{string.Join(",", tools.Select(t => t.Name))}], prompt {conversation.Length} chars");
            string raw = "";
            var result = await smart.GenerateAsync(new SmartRequestContext(project, project, "keywords"),
                HelperToolCalls.BuildSystemPrompt(system, tools), conversation, 512,
                text => { raw = text; return HelperToolCalls.Parse(text, tools); }, background: false, ct);
            output.WriteLine($"   << {raw.ReplaceLineEndings(" ")}");
            return result?.Value;
        }

        public Task<string> WriteAsync(string system, string prompt, CancellationToken ct) => Task.FromResult("");
        public void Prewarm(string systemPrompt, IReadOnlyList<ToolDefinition> tools) { }
    }

    [Fact]
    public async Task Global_search_on_a_real_project()
    {
        var project = Environment.GetEnvironmentVariable("CODALE_SEARCH_PROJECT");
        var queries = Environment.GetEnvironmentVariable("CODALE_SEARCH_QUERY");
        if (string.IsNullOrWhiteSpace(project) || string.IsNullOrWhiteSpace(queries)) return;
        var root = ProjectPaths.Normalize(project);
        var appDb = Environment.GetEnvironmentVariable("CODALE_LOCATOR_DB");
        if (!string.IsNullOrWhiteSpace(appDb)) CopyDirectory(appDb, Path.Combine(_work, "db"));

        await using var models = SmartModels.Open(Environment.GetEnvironmentVariable("CODALE_LOCATOR_SETTINGS"), _work,
            Environment.GetEnvironmentVariable("CODALE_LOCATOR_EMBED") is not null);
        if (models is not null) await models.StartAsync();
        output.WriteLine(models is null ? "models: none" : $"models: general={models.GeneralModel} embedding={models.Embedder?.Model ?? "hashed"}");
        using var db = new CodaleDb(Path.Combine(_work, "db"), new() { Embedder = models?.Embedder });
        using var knowledge = new ProjectKnowledgeIndex(db);
        if (string.IsNullOrWhiteSpace(appDb)) await knowledge.RefreshAsync(root);
        var index = SourceIndex.For(root);
        try
        {
            foreach (var query in queries.Split(';', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries))
            {
                output.WriteLine($"=== {query}");
                ISearchModel? model = models is null || Environment.GetEnvironmentVariable("CODALE_SEARCH_NO_MODEL") is not null
                    ? null : new LoggedModel(models.Experience, root, output);
                var explorer = new SourceExplorer(index, model)
                {
                    Knowledge = knowledge, UseSemanticSearch = true, ExactKeywordFirst = true, FuseSemanticRanks = true,
                    MaxRankedResults = 12, MaxRelated = SearchQueryIntent.IsNaturalLanguage(query) ? 4 : 0,
                };
                explorer.Progress += (_, r) => output.WriteLine($"   progress round {r.Round}: {r.Files.Count} files");
                var clock = System.Diagnostics.Stopwatch.StartNew(); var result = await explorer.RunAsync(query); output.WriteLine($"   took {clock.ElapsedMilliseconds} ms");
                output.WriteLine($"final: {result.Files.Count} files, exact={result.IsExact}, keywords=[{string.Join(", ", result.Keywords)}]");
                foreach (var file in result.Files)
                {
                    output.WriteLine($" - {file.RelativePath} confirmed={file.Confirmed} reason='{file.Reason}' keywords=[{string.Join(", ", file.Keywords)}]");
                    foreach (var range in file.Ranges)
                        output.WriteLine($"     {range.StartLine}-{range.EndLine} match=[{string.Join(",", range.MatchLines)}]");
                }
            }
        }
        finally { index.Dispose(); }
    }

    private static void CopyDirectory(string from, string to)
    {
        foreach (var file in Directory.EnumerateFiles(from, "*", SearchOption.AllDirectories).Where(f => Path.GetFileName(f) != "LOCK"))
        {
            var target = Path.Combine(to, Path.GetRelativePath(from, file));
            Directory.CreateDirectory(Path.GetDirectoryName(target)!);
            using var source = new FileStream(file, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete);
            using var copy = File.Create(target);
            source.CopyTo(copy);
        }
    }
}
