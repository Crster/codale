using System.Diagnostics;
using System.Text.Json;
using Codale.Core.Projects;
using Codale.Db;
using Codale.Search.Engine;
using Xunit.Abstractions;

namespace Codale.Search.Tests;

/// <summary>
/// Auto Act context hints on a real project. Set CODALE_LOCATOR_PROJECT to a git checkout and
/// CODALE_LOCATOR_CASES to a JSON list of {"prompt": "...", "files": ["edited/path.ts", ...]}
/// (labels taken from the project's own commits: the subject is the prompt, the files it changed
/// are the answer). Prints each prompt's hints with precision and whether the answer was found.
/// </summary>
public sealed class PromptFileLocatorEvalTests(ITestOutputHelper output) : IDisposable
{
    private sealed record Case(string Prompt, string[] Files);

    private readonly string _work = Directory.CreateTempSubdirectory("codale-locator-eval-").FullName;

    public void Dispose()
    {
        try { Directory.Delete(_work, true); } catch (IOException) { } catch (UnauthorizedAccessException) { }
    }

    private string CopyTracked(string project)
    {
        var target = Path.Combine(_work, "project");
        var git = Process.Start(new ProcessStartInfo("git", "ls-files -z")
        {
            WorkingDirectory = project, RedirectStandardOutput = true, UseShellExecute = false,
        })!;
        var listing = git.StandardOutput.ReadToEnd();
        git.WaitForExit();
        foreach (var relative in listing.Split('\0', StringSplitOptions.RemoveEmptyEntries))
        {
            var from = Path.Combine(project, relative);
            if (!File.Exists(from) || new FileInfo(from).Length > 1_000_000) continue;
            var to = Path.Combine(target, relative);
            Directory.CreateDirectory(Path.GetDirectoryName(to)!);
            File.Copy(from, to);
        }

        return ProjectPaths.Normalize(target);
    }

    [Fact]
    public async Task Hints_name_the_files_a_request_edits()
    {
        var project = Environment.GetEnvironmentVariable("CODALE_LOCATOR_PROJECT");
        var casesPath = Environment.GetEnvironmentVariable("CODALE_LOCATOR_CASES");
        if (string.IsNullOrWhiteSpace(project) || string.IsNullOrWhiteSpace(casesPath)) return;
        var cases = JsonSerializer.Deserialize<List<Case>>(File.ReadAllText(casesPath),
            new JsonSerializerOptions { PropertyNameCaseInsensitive = true })!;

        var root = CopyTracked(project);
        using var db = new CodaleDb(Path.Combine(_work, "db"));
        using var knowledge = new ProjectKnowledgeIndex(db);
        var useKnowledge = Environment.GetEnvironmentVariable("CODALE_LOCATOR_NO_KNOWLEDGE") is null;
        if (useKnowledge) await knowledge.RefreshAsync(root);

        int found = 0, listed = 0, correct = 0, top = 0;
        long bytes = 0;
        var baseline = Environment.GetEnvironmentVariable("CODALE_LOCATOR_BASELINE") is not null;
        try
        {
            foreach (var test in cases)
            {
                var clock = Stopwatch.StartNew();
                var result = baseline
                    ? await Baseline(root, test.Prompt, useKnowledge ? knowledge : null)
                    : await PromptFileLocator.RetrieveAsync(root, test.Prompt, useKnowledge ? knowledge : null);
                clock.Stop();
                var wanted = test.Files.ToHashSet(StringComparer.OrdinalIgnoreCase);
                var hits = result.Files.Count(wanted.Contains);
                found += hits > 0 ? 1 : 0;
                top += result.Files.Count > 0 && wanted.Contains(result.Files[0]) ? 1 : 0;
                listed += result.Files.Count;
                correct += hits;
                bytes += System.Text.Encoding.UTF8.GetByteCount(result.Text);
                var first = result.Files.Count > 0 && wanted.Contains(result.Files[0]) ? "✓" : "✗";
                output.WriteLine($"## {first} {test.Prompt}  [{clock.ElapsedMilliseconds} ms, {hits}/{result.Files.Count} right, want {string.Join(", ", test.Files)}]");
                output.WriteLine(result.Text.Length == 0 ? "(nothing)" : result.Text);
            }
        }
        finally { SourceIndex.For(root).Dispose(); }

        output.WriteLine($"found {found}/{cases.Count} · first right {top}/{cases.Count} · precision {correct}/{listed}" +
            $" · {bytes / Math.Max(1, cases.Count)} bytes per prompt");
    }

    /// <summary>The locator before line-anchored hints (path | keywords, up to six files), kept to compare against.</summary>
    private static async Task<FileLocatorContext> Baseline(string root, string prompt, ProjectKnowledgeIndex? knowledge)
    {
        var result = await new SourceExplorer(SourceIndex.For(root), null)
        {
            Knowledge = knowledge, MaxRelated = 0, MaxRankedResults = 12, Budget = TimeSpan.FromSeconds(3),
        }.RunAsync(prompt);
        var terms = Codale.Core.Features.SearchText.RetrievalTerms(prompt).Take(12).ToArray();
        var text = new System.Text.StringBuilder();
        var files = new List<string>();
        foreach (var file in result.Files)
        {
            if (!ProjectKnowledgeIndex.Eligible(file.RelativePath) || !File.Exists(file.FilePath)) continue;
            var searchable = file.RelativePath + "\n" + string.Join("\n", file.Ranges.Select(r => r.Code));
            var verified = terms.Where(t => searchable.Contains(t, StringComparison.OrdinalIgnoreCase)).Take(4).ToArray();
            if (verified.Length == 0 || files.Count == 6) continue;
            var path = file.RelativePath.Replace('\\', '/');
            text.Append($"- {path} | {string.Join(", ", verified)}\n");
            files.Add(path);
        }

        return new(files.Count == 0 ? "" : "Project file search hints (paths | matching keywords; verify as needed):\n" + text + "\n", files);
    }
}
