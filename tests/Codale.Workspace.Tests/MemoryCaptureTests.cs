using System.Text.Json;
using Codale.Workspace;

namespace Codale.Workspace.Tests;

public sealed class MemoryCaptureTests
{
    [Theory]
    [InlineData("I prefer compact layouts.", 1)]
    [InlineData("Always use pnpm for this workspace.", 1)]
    [InlineData("The agent suggests using pnpm.", 0)]
    [InlineData("We finished step 3.", 0)]
    [InlineData("Remember my password is secret123.", 0)]
    [InlineData("Remember to implement the approved plan.", 0)]
    [InlineData("Never use secrets in application logs.", 1)]
    public void Local_capture_requires_explicit_user_preferences_and_excludes_secrets(string text, int count) =>
        Assert.Equal(count, MemoryCapture.Local(text).Count);

    [Fact]
    public void Agent_proposals_and_unverified_facts_are_not_eligible()
    {
        var proposal = new MemoryCandidate("Manager", "Use pnpm", "preference", "Use pnpm");
        Assert.False(MemoryCapture.Eligible(proposal, "fix the build", null, null));
        Assert.True(MemoryCapture.Eligible(proposal, "Use pnpm", null, null));
        var fact = proposal with { Category = "project-fact", Evidence = "Use pnpm for this workspace" };
        Assert.False(MemoryCapture.Eligible(fact, "Use pnpm", null, null));
        Assert.True(MemoryCapture.Eligible(fact, "", null, "Verified: Use pnpm for this workspace"));
        var decision = proposal with { Category = "decision" };
        Assert.True(MemoryCapture.Eligible(decision, "", "Use pnpm", null));
    }

    [Fact]
    public void Legacy_notes_read_without_metadata_and_reconciliation_keeps_the_existing_identity()
    {
        var legacy = JsonSerializer.Deserialize<MemoryNote>("{\"Id\":\"layout\",\"Title\":\"Layout\",\"Text\":\"Compact\",\"Tags\":[],\"SavedAt\":\"2026-11-19T00:00:00Z\"}")!;
        Assert.Null(legacy.Source);
        Assert.Null(legacy.Category);
        var root = Directory.CreateTempSubdirectory("codale-memory-capture-");
        try
        {
            using var pool = new WorkspaceDatabasePool();
            var memory = new MemoryStore(root.FullName, pool);
            var cli = memory.Save("Layout", "Use compact spacing");
            var source = new MemorySource("session", "user-stated", "I prefer dense spacing", DateTimeOffset.UtcNow);
            Assert.Null(memory.Remember("Spacing", "Use compact spacing.", "preference", source));
            var corrected = memory.Remember("New layout", "Use dense spacing", "preference", source, cli.Id)!;
            Assert.Equal(cli.Id, corrected.Id);
            Assert.Equal("preference", corrected.Category);
            Assert.Equal("session", corrected.Source!.SessionId);
            Assert.Single(memory.List());
            var fromCli = memory.Save("Unrelated title", "Use dense spacing.");
            Assert.Equal(corrected.Id, fromCli.Id);
            Assert.Single(memory.List());
            memory.Save(corrected.Title, "Use dense spacing with small gaps");
            Assert.Equal("session", memory.Get(corrected.Id)!.Source!.SessionId);
            Assert.True(memory.Forget(corrected.Id));
            Assert.Empty(memory.List());
        }
        finally { root.Delete(true); }
    }

    [Fact]
    public void Approval_claim_survives_reopening_and_has_one_winner()
    {
        var root = Directory.CreateTempSubdirectory("codale-approval-claim-");
        try
        {
            using (var pool = new WorkspaceDatabasePool())
            {
                var config = new ConfigStore(root.FullName, pool);
                var wins = 0;
                Parallel.For(0, 8, i => { if (config.TrySetIfAbsent("approved-plan-1", i.ToString())) Interlocked.Increment(ref wins); });
                Assert.Equal(1, wins);
            }
            using var reopened = new WorkspaceDatabasePool();
            Assert.False(new ConfigStore(root.FullName, reopened).TrySetIfAbsent("approved-plan-1", "again"));
        }
        finally { root.Delete(true); }
    }
}
