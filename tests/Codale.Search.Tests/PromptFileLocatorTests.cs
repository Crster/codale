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
        Assert.Equal(expected, PromptFileLocator.ShouldRetrieve(prompt, false));

    [Fact]
    public void AttachmentsSkipRetrieval() =>
        Assert.False(PromptFileLocator.ShouldRetrieve("fix login timeout", true));

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
    /// The request behind this locator's rewrite: the old hints listed migrations ("add" in ADD
    /// COLUMN), a dropdown "menu" and every "growth", and never the nav entries the agent edited.
    /// </summary>
    [Fact]
    public async Task PointsAtTheNavigationEntryNotMigrationsOrDropdowns()
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

            Assert.Equal("src/lib/constants.ts", result.Files.FirstOrDefault());
            Assert.DoesNotContain(result.Files, f => f.Contains("migrations"));
            Assert.DoesNotContain("src/model/campaign.ts", result.Files);
            Assert.Contains("title: \"Marketing\"", result.Text);
            Assert.True(result.Text.Contains("| in navGroups"), result.Text);
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
}
