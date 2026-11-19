using Codale.Core.Agents;

namespace Codale.Agents.Tests;

/// <summary>Opt-in native protocol smoke tests; only a disposable fixture is exposed to the agents.</summary>
public sealed class SpecialistSessionLiveTests
{
    [Theory]
    [InlineData("claude", false)]
    [InlineData("codex", false)]
    [InlineData("claude", true)]
    [InlineData("codex", true)]
    public async Task Native_children_answer_and_interview_without_editing_the_fixture(string cli, bool planner)
    {
        if (Environment.GetEnvironmentVariable("CODALE_SPECIALIST_LIVE_TESTS") != "1") return;
        using var deadline = new CancellationTokenSource(TimeSpan.FromMinutes(3));
        var account = "";
        foreach (var candidate in CodaleAccount.AccountIds(cli))
            if (await CodaleAccountCli.IsSignedInAsync(cli, deadline.Token, candidate)) { account = candidate; break; }
        Assert.True(await CodaleAccountCli.IsSignedInAsync(cli, deadline.Token, account), $"No signed-in {cli} account available.");
        var work = Directory.CreateTempSubdirectory("codale-specialist-live-");
        try
        {
            var file = Path.Combine(work.FullName, "README.md");
            const string original = "# Specialist smoke fixture\nThis file must stay unchanged during planning.\n";
            await File.WriteAllTextAsync(file, original, deadline.Token);
            var request = new AgentSessionRequest
            {
                CliId = cli, WorkingDirectory = work.FullName,
                Environment = new Dictionary<string, string>
                { [cli == "codex" ? CodaleAccount.CodexVariable : CodaleAccount.ClaudeVariable] = CodaleAccount.ProfileHome(cli, account) },
            };
            await using var child = new SpecialistSession(new AgentSessionFactory(), request, new()
            {
                Planner = planner, Model = cli == "claude" ? planner ? "opus" : "haiku" : null,
                Effort = planner ? "high" : "low", Briefing = "This is an isolated verification fixture. Never implement application changes.",
            });
            await child.SendAsync(planner
                ? "Plan adding one greeting sentence to README.md. Conduct your complete fixed interview first; do not implement."
                : "What is 2 + 2? Reply in one short sentence; no tools are needed.", ct: deadline.Token);
            var rounds = 0;
            while (true)
            {
                deadline.Token.ThrowIfCancellationRequested();
                if (child.Snapshot.Questions.Count > 0)
                {
                    Assert.True(++rounds <= 10, "Interview exceeded ten rounds.");
                    await child.AnswerAsync(child.Snapshot.Questions.ToDictionary(q => q.Id,
                        q => "For this disposable fixture, use your recommended default. " + q.Options.FirstOrDefault()), deadline.Token);
                }
                else if (!child.IsRunning)
                {
                    Assert.DoesNotContain("Failed", child.Snapshot.Status);
                    if (!planner) break;
                    if (child.Snapshot.Plan is not null && child.Snapshot.InterviewAnswered) break;
                    Assert.True(++rounds <= 10, "No approvable plan after ten interview rounds.");
                    await child.SendAsync("I confirm the stated requirements and recommended defaults for this disposable fixture. " +
                        "Only add the sentence 'Hello, world!' after the heading, preserve all existing content, no new dependencies or interfaces, " +
                        "no deployment. Success means that exact sentence is present. Ask any remaining interview questions or present the final plan.", ct: deadline.Token);
                }
                await Task.Delay(100, deadline.Token);
            }
            Assert.Contains(child.Snapshot.Messages, m => m.Role == "assistant" && m.Text.Length > 0);
            Assert.Equal(original, await File.ReadAllTextAsync(file, deadline.Token));
            if (planner) Assert.True(child.ClaimPlan(child.Snapshot.PlanVersion));
            else Assert.Contains("4", child.Snapshot.Messages.Last(m => m.Role == "assistant").Text);
        }
        finally
        {
            // Windows may retain a just-exited CLI's working-directory handle for a short interval.
            for (var attempt = 0; ; attempt++)
            {
                try { work.Delete(true); break; }
                catch (IOException) when (attempt < 20) { await Task.Delay(100); }
            }
        }
    }
}
