using Codale.Search.Engine;
namespace Codale.Search.Tests;

public sealed class PromptFileLocatorTests
{
    [Theory]
    [InlineData("commit all changes", false)]
    [InlineData("please build the project", false)]
    [InlineData("rename src/A.cs to src/B.cs", false)]
    [InlineData("fix the login timeout", true)]
    [InlineData("why does the build fail on login?", true)]
    public void SkipsOperations(string prompt, bool expected) =>
        Assert.Equal(expected, PromptFileLocator.ShouldRetrieve(prompt, false, isFreshSession: true, isNewTopic: false));

    [Fact]
    public void AttachmentsSkipRetrieval() =>
        Assert.False(PromptFileLocator.ShouldRetrieve("fix login timeout", true, isFreshSession: true, isNewTopic: false));

    [Theory]
    [InlineData(false, false, false)]
    [InlineData(true, false, true)]
    [InlineData(false, true, true)]
    [InlineData(true, true, true)]
    public void RetrievesOnlyForFreshSessionsOrNewTopics(bool fresh, bool newTopic, bool expected) =>
        Assert.Equal(expected, PromptFileLocator.ShouldRetrieve("fix login timeout", false, fresh, newTopic));

    [Theory]
    [InlineData("/compact", false)]
    [InlineData("commit all changes", false)]
    [InlineData("fix login timeout", true)]
    public void NewTopicsStillRespectRetrievalExclusions(string prompt, bool attachments) =>
        Assert.False(PromptFileLocator.ShouldRetrieve(prompt, attachments, isFreshSession: false, isNewTopic: true));

    [Fact]
    public async Task ReturnsLineAnchoredHintsAndExcludesKnownFiles()
    {
        var root = Directory.CreateTempSubdirectory("locator-").FullName;
        try
        {
            Directory.CreateDirectory(Path.Combine(root, "src"));
            File.WriteAllText(Path.Combine(root, "src", "LoginTimeout.cs"),
                "class LoginTimeout { /* private implementation marker */ int timeout = 30; }");
            File.WriteAllText(Path.Combine(root, "src", "Unrelated.cs"), "class Unrelated { }");
            var result = await PromptFileLocator.RetrieveAsync(root, "fix login timeout");
            Assert.Contains("src/LoginTimeout.cs", result.Files);
            Assert.DoesNotContain("src/Unrelated.cs", result.Files);
            // A path with its line range and matched words, then the matching line itself.
            Assert.Contains("- src/LoginTimeout.cs:1-1 | login, timeout", result.Text);
            Assert.Contains("    1: class LoginTimeout", result.Text);
            Assert.True(System.Text.Encoding.UTF8.GetByteCount(result.Text) < 2200);
            var shellKnown = ShellReadFiles.Find(root, "Get-Content src/LoginTimeout.cs", root, null);
            var afterShellRead = await PromptFileLocator.RetrieveAsync(root, "fix login timeout",
                knownFiles: new HashSet<string>(shellKnown, StringComparer.OrdinalIgnoreCase));
            Assert.DoesNotContain("src/LoginTimeout.cs", afterShellRead.Files);
            var excluded = await PromptFileLocator.RetrieveAsync(root, "fix login timeout",
                knownFiles: new HashSet<string>(result.Files, StringComparer.OrdinalIgnoreCase));
            Assert.Empty(excluded.Files);
            Assert.Empty(excluded.Text);
        }
        finally { SourceIndex.For(root).Dispose(); Directory.Delete(root, true); }
    }

    /// <summary>
    /// The request behind the first locator rewrite: hints must never list migrations ("add" in ADD COLUMN). Which
    /// of the code files leads is measured by the history eval; "navbar" reaching navGroups is the model's job.
    /// </summary>
    [Fact]
    public async Task NavigationRequestNeverListsMigrations()
    {
        var root = Directory.CreateTempSubdirectory("locator-nav-").FullName;
        void Write(string path, string text)
        {
            var full = Path.Combine(root, path.Replace('/', Path.DirectorySeparatorChar));
            Directory.CreateDirectory(Path.GetDirectoryName(full)!);
            File.WriteAllText(full, text);
        }

        try
        {
            Write("src/lib/constants.ts", """
                import { LayoutDashboard, DollarSign, Palette } from "lucide-react";

                export interface NavLink {
                  title: string;
                  href: string;
                }

                export interface NavGroup {
                  title: string;
                  items: NavLink[];
                }

                export const navGroups: NavGroup[] = [
                  {
                    title: "Overview",
                    items: [
                      { title: "Business Overview", href: "/overview", icon: LayoutDashboard },
                    ],
                  },
                  {
                    title: "Marketing",
                    items: [
                      { title: "Spend Breakdown", href: "/marketing-cost", icon: DollarSign },
                      { title: "Creative Output", href: "/marketing/creative", icon: Palette },
                    ],
                  },
                ];
                """);
            Write("src/features/marketing-cost/components/marketing-cost-header.tsx", """
                import { DropdownMenu, DropdownMenuItem } from "@/components/ui/dropdown-menu";

                export function MarketingCostHeader() {
                  return (
                    <div>
                      <h1>Marketing Cost Allocation</h1>
                      <DropdownMenu>
                        <DropdownMenuItem>Export</DropdownMenuItem>
                      </DropdownMenu>
                    </div>
                  );
                }
                """);
            Write("prisma/migrations/20260515092600_add_marketing_cost_cache/migration.sql", """
                -- Add the marketing cost cache
                ALTER TABLE "MarketingCost" ADD COLUMN "growth" DOUBLE PRECISION;
                ALTER TABLE "MarketingCost" ADD COLUMN "menu" TEXT;
                """);
            Write("src/model/campaign.ts", """
                export const CAMPAIGN_MARKETS = ["US", "UK"];
                export function growthRate(current: number, previous: number) {
                  return previous === 0 ? 0 : (current - previous) / previous;
                }
                """);

            var result = await PromptFileLocator.RetrieveAsync(root, "Add growth and ingrowth menu in the navbar under the marketing menu.");
            Assert.NotEmpty(result.Files);
            Assert.DoesNotContain(result.Files, f => f.Contains("migrations"));

            // Words the code does not use ("navbar") find nothing; the model's names for it do.
            var expanded = await PromptFileLocator.RetrieveAsync(root, "rework the navbar",
                smart: new ExpandingSmart(["navGroups", "NavGroup", "NavLink"]));
            Assert.Equal("src/lib/constants.ts", expanded.Files.FirstOrDefault());
            Assert.True(expanded.Text.Contains("navGroups"), expanded.Text);
        }
        finally { SourceIndex.For(root).Dispose(); Directory.Delete(root, true); }
    }

    [Theory]
    [InlineData("Find npm so the Codex update can start", "src/CodexUpdater.cs")]
    [InlineData("Update the npm lookup for Codex", "src/CodexUpdater.cs")]
    public async Task ReadsAVerbAsANounWhereItIsOne(string prompt, string expected)
    {
        var root = Directory.CreateTempSubdirectory("locator-verb-").FullName;
        try
        {
            Directory.CreateDirectory(Path.Combine(root, "src"));
            File.WriteAllText(Path.Combine(root, "src", "CodexUpdater.cs"), """
                class CodexUpdater
                {
                    string FindNpm() => "npm";
                    void StartUpdate() { var codex = FindNpm(); }
                }
                """);
            File.WriteAllText(Path.Combine(root, "src", "Readme.cs"), "class Readme { string Text = \"npm install\"; }");
            var result = await PromptFileLocator.RetrieveAsync(root, prompt);
            Assert.Equal(expected, result.Files.FirstOrDefault());
        }
        finally { SourceIndex.For(root).Dispose(); Directory.Delete(root, true); }
    }
    [Theory]
    [InlineData("I set the database to my localhost. can you deploy the database.")]
    [InlineData("I dont have yet the recharm. please test without it.")]
    [InlineData("can you use the browser and do testing End 2 End testing. so I can see how each steps work")]
    [InlineData("test it on the live site (https://atlas.hike-footwear.com/). I also added load to the anthropic api key")]
    public void AtlasOperationalPromptsDoNotReceiveEditHints(string prompt) =>
        Assert.False(PromptFileLocator.ShouldRetrieve(prompt, false, true, false));

    [Fact]
    public async Task WorkflowFindsImplementationInsteadOfBroadRulesAndCanon()
    {
        var root = Directory.CreateTempSubdirectory("locator-workflow-").FullName;
        void Write(string path, string text)
        {
            var full = Path.Combine(root, path);
            Directory.CreateDirectory(Path.GetDirectoryName(full)!);
            File.WriteAllText(full, text);
        }
        try
        {
            const string prose = "editor script footage product voice duration editing library clips captions audio scenes visual quality";
            Write("brain/canon/SKILL.md", prose);
            Write("src/lib/guardrails/rules.json", "{ \"description\": \"" + prose + "\" }");
            Write("src/lib/brain/editPlan.ts", "export function editPlan(script, footage, clips) { return { script, footage, clips }; }");
            var result = await PromptFileLocator.RetrieveAsync(root,
                "Make the editor workflow work: give editing instructions, search footage in the library, build an edit plan from the script, select clips and generate captions and audio with visual quality.");
            Assert.Contains("src/lib/brain/editPlan.ts", result.Files);
            Assert.DoesNotContain("brain/canon/SKILL.md", result.Files);
            Assert.DoesNotContain("src/lib/guardrails/rules.json", result.Files);
            var docs = await PromptFileLocator.RetrieveAsync(root, "update the documentation for footage clips");
            Assert.Contains("brain/canon/SKILL.md", docs.Files);
        }
        finally { SourceIndex.For(root).Dispose(); Directory.Delete(root, true); }
    }

    [Fact]
    public async Task MissingSubjectDoesNotCollapseToOneUnrelatedMatch()
    {
        var root = Directory.CreateTempSubdirectory("locator-missing-").FullName;
        try
        {
            File.WriteAllText(Path.Combine(root, "Header.ts"), "export const header = 'something unrelated';");
            var result = await PromptFileLocator.RetrieveAsync(root, "fix the recharm header");
            Assert.Empty(result.Files);
        }
        finally { SourceIndex.For(root).Dispose(); Directory.Delete(root, true); }
    }

    [Fact]
    public async Task ExplicitDirectoryOutranksIncidentalPromptOverlap()
    {
        var root = Directory.CreateTempSubdirectory("locator-path-").FullName;
        try
        {
            Directory.CreateDirectory(Path.Combine(root, "src/brain"));
            File.WriteAllText(Path.Combine(root, "src/brain/plan.ts"), "export const plan = { footage: true, clips: true };");
            File.WriteAllText(Path.Combine(root, "Other.ts"), "export const plan = { footage: true, clips: true, script: true, video: true };");
            var result = await PromptFileLocator.RetrieveAsync(root, "fix footage clips script video in @src/brain/...");
            Assert.True(result.Files.FirstOrDefault() == "src/brain/plan.ts", result.Text);
        }
        finally { SourceIndex.For(root).Dispose(); Directory.Delete(root, true); }
    }

    [Fact]
    public async Task LongPromptUsesSubjectsBeyondThirtyOneAndLabelsOnlyTheSelectedRegion()
    {
        var root = Directory.CreateTempSubdirectory("locator-long-").FullName;
        try
        {
            var subjects = Enumerable.Range(0, 36).Select(i => "subject" + (char)('a' + i / 26) + (char)('a' + i % 26)).ToArray();
            var sparse = string.Join("\n", subjects.Take(32).Select(w => "const " + w + " = true;\n" + new string('\n', 20)));
            File.WriteAllText(Path.Combine(root, "Relevant.ts"), sparse + "\nconst selected = [" + string.Join(", ", subjects.Skip(32)) + "];");
            var result = await PromptFileLocator.RetrieveAsync(root, "fix " + string.Join(" ", subjects));
            Assert.Contains("Relevant.ts", result.Files);
            Assert.Contains("| subjectbg, subjectbh, subjectbi, subjectbj", result.Text);
            Assert.DoesNotContain("| subjectaa", result.Text);
        }
        finally { SourceIndex.For(root).Dispose(); Directory.Delete(root, true); }
    }

    [Fact]
    public async Task ConfigurationRequestsCanStillFindJson()
    {
        var root = Directory.CreateTempSubdirectory("locator-json-").FullName;
        try
        {
            File.WriteAllText(Path.Combine(root, "package.json"), "{ \"scripts\": { \"build\": \"next build\" } }");
            var result = await PromptFileLocator.RetrieveAsync(root, "fix the package build scripts");
            Assert.Contains("package.json", result.Files);
        }
        finally { SourceIndex.For(root).Dispose(); Directory.Delete(root, true); }
    }


    [Fact]
    public async Task FollowsTheGraphToAViewsCodeBehind()
    {
        var root = Directory.CreateTempSubdirectory("locator-graph-").FullName;
        var dbPath = Directory.CreateTempSubdirectory("locator-graph-db-").FullName;
        try
        {
            Directory.CreateDirectory(Path.Combine(root, "Views"));
            File.WriteAllText(Path.Combine(root, "Views", "FilesPanel.xaml"),
                """
                <UserControl x:Class="App.FilesPanel">
                  <TextBox x:Name="RenameBox" KeyDown="OnRenameKeyDown" />
                </UserControl>
                """);
            File.WriteAllText(Path.Combine(root, "Views", "FilesPanel.xaml.cs"), """
                namespace App;
                public partial class FilesPanel
                {
                    private void OnRenameKeyDown(object sender, object e)
                    {
                        Commit();
                    }
                    private void Commit() { }
                }
                """);
            File.WriteAllText(Path.Combine(root, "Other.cs"), "class Other { void Render() { } }");
            using var db = new Codale.Db.CodaleDb(dbPath);
            using var knowledge = new ProjectKnowledgeIndex(db);
            await knowledge.RefreshAsync(root);

            Assert.Contains(knowledge.RelatedFiles(root, "Views/FilesPanel.xaml"),
                r => r.Path.EndsWith("FilesPanel.xaml.cs", StringComparison.Ordinal) && r.Relation == ProjectKnowledgeIndex.PairRelation);
            var result = await PromptFileLocator.RetrieveAsync(root, "the rename textbox in the files panel", knowledge);
            Assert.Contains("Views/FilesPanel.xaml", result.Files);
            Assert.Contains("Views/FilesPanel.xaml.cs", result.Files);
        }
        finally
        {
            SourceIndex.For(root).Dispose();
            Directory.Delete(root, true);
            try { Directory.Delete(dbPath, true); } catch (IOException) { }
        }
    }

    /// <summary>A General model that answers query expansion with fixed names and nothing else.</summary>
    private sealed class ExpandingSmart(IReadOnlyList<string> terms) : Codale.Smart.ISmartExperience
    {
        public Codale.Smart.SmartStatus Status => new(Codale.Smart.SmartState.Ready, "stub");
        public event EventHandler? Changed { add { } remove { } }
        public bool Thinking { get; set; }
        public Task SetEnabledAsync(bool enabled, CancellationToken ct = default) => Task.CompletedTask;
        public Task<Codale.Smart.SmartResult<IReadOnlyList<string>>?> ExpandQueryAsync(Codale.Smart.SmartRequestContext context, string request,
            string vocabulary, CancellationToken ct = default) =>
            Task.FromResult<Codale.Smart.SmartResult<IReadOnlyList<string>>?>(new(terms, [], "stub"));
        public Task<Codale.Smart.SmartResult<string>?> SummarizeAsync(Codale.Smart.SmartRequestContext context, string path, CancellationToken ct = default) =>
            Task.FromResult<Codale.Smart.SmartResult<string>?>(null);
        public Task<Codale.Smart.SmartResult<IReadOnlyList<string>>?> RankAsync(Codale.Smart.SmartRequestContext context, string query,
            IReadOnlyList<Codale.Smart.SmartCandidate> candidates, CancellationToken ct = default) =>
            Task.FromResult<Codale.Smart.SmartResult<IReadOnlyList<string>>?>(null);
        public Task<Codale.Smart.SmartResult<Codale.Smart.SmartIntent?>?> ClassifyAsync(Codale.Smart.SmartRequestContext context, string message,
            string? previousReply, CancellationToken ct = default) => Task.FromResult<Codale.Smart.SmartResult<Codale.Smart.SmartIntent?>?>(null);
        public Task<Codale.Smart.SmartResult<string>?> CommitMessageAsync(Codale.Smart.SmartRequestContext context, CancellationToken ct = default) =>
            Task.FromResult<Codale.Smart.SmartResult<string>?>(null);
        public Task<IReadOnlyList<Codale.Smart.SmartEvidence>> DiscoveriesAsync(Codale.Smart.SmartRequestContext context, string query,
            CancellationToken ct = default) => Task.FromResult<IReadOnlyList<Codale.Smart.SmartEvidence>>([]);
    }
}
