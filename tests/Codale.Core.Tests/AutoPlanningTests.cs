using System.Text.Json;
using Codale.Core.Agents;
using Codale.Core.Helper;

namespace Codale.Core.Tests;

public sealed class AutoPlanningTests
{
    [Theory]
    [InlineData("build a complete authentication system with sessions, roles and tests")]
    [InlineData("add billing with subscriptions")]
    [InlineData("rewrite the storage architecture")]
    [InlineData("migrate the database across all services")]
    [InlineData("plan and implement the migration")]
    [InlineData("make it better")]
    [InlineData("implement the approved plan and add a new feature for billing")]
    [InlineData("run a migration across all services")]
    [InlineData("overhaul the entire app")]
    public void Large_cross_cutting_or_vague_work_cannot_be_downgraded_to_implementation(string text)
    {
        Assert.Equal(RouteIntent.Plan, MessageRouting.TryRouteLocally(text)?.Intent);
        Assert.Equal(RouteIntent.Plan, MessageRouting.RouteWithoutModel(text).Intent);
        foreach (var intent in new[] { "act", "ask", "invalid" })
        {
            var call = new ToolCall("route", JsonSerializer.SerializeToElement(new { intent }));
            var result = MessageRouting.Interpret(call, text);
            Assert.Equal(RouteIntent.Plan, result.Intent);
            Assert.Equal(text, result.Text);
        }
    }

    // Real Auto mode prompts that were wrongly sent to plan: specific features, UI
    // complaints and reorganisations the agent can carry out directly.
    [Theory]
    [InlineData("add column to flag project that has issue like milestone is due or close to due date. @fa00c8.png")]
    [InlineData("add new toolbar in the milestone row for attaching note @a12cfa.png it will reflect as a comment with #Task as a reference anchor")]
    [InlineData("this wide spacing is annoying @c03874.png . can we redesign it")]
    [InlineData("please arrange the permissions correctly according to menu and where the page belongs. http://localhost:3000/permissions. for example the View/Edit project belongs to Go to Market.")]
    [InlineData("the header is ugly")]
    [InlineData("redesign the dashboard")]
    [InlineData("add CSV export for invoices")]
    [InlineData("implement notifications")]
    [InlineData("add recurring task schedules")]
    [InlineData("build a timetable editor")]
    [InlineData("ok now add a dark theme to the settings page and wire it to the toggle")]
    public void Specific_features_and_ui_complaints_are_not_forced_into_plan(string text)
    {
        Assert.NotEqual(RouteIntent.Plan, MessageRouting.TryRouteLocally(text)?.Intent);
        Assert.Equal(RouteIntent.Act, MessageRouting.RouteWithoutModel(text).Intent);
        var call = new ToolCall("route", JsonSerializer.SerializeToElement(new { intent = "act" }));
        Assert.Equal(RouteIntent.Act, MessageRouting.Interpret(call, text).Intent);
    }

    [Theory]
    [InlineData("add column to flag project that has issue like milestone is due or close to due date. @fa00c8.png")]
    [InlineData("please add a status column")]
    [InlineData("fix the login redirect")]
    public void A_plan_verdict_on_bounded_work_is_overruled(string text)
    {
        var call = new ToolCall("route", JsonSerializer.SerializeToElement(new { intent = "plan" }));
        Assert.Equal(RouteIntent.Act, MessageRouting.Interpret(call, text).Intent);
    }

    [Theory]
    [InlineData("rename Save to Apply")]
    [InlineData("fix the login redirect")]
    [InlineData("run the test suite")]
    [InlineData("commit all changes")]
    [InlineData("implement the approved plan")]
    [InlineData("yes go ahead")]
    [InlineData("write unit tests for the parser")]
    [InlineData("build the project")]
    public void Bounded_work_and_approvals_still_proceed(string text) =>
        Assert.Equal(RouteIntent.Act, MessageRouting.RouteWithoutModel(text).Intent);

    [Theory]
    [InlineData("how would we build a new feature for payments?")]
    [InlineData("explain the authentication architecture")]
    [InlineData("compare migration approaches")]
    public void Pure_questions_stay_read_only(string text) =>
        Assert.Equal(RouteIntent.Ask, MessageRouting.RouteWithoutModel(text).Intent);

    [Fact]
    public void Planning_instructions_expose_missing_requirements_and_are_hidden_on_replay()
    {
        var turn = new UserTurn("add sharing") { PlanOnly = true, ContextPrefix = SessionBriefing.ContextBlock("reference") };
        Assert.Contains(UserTurn.PlanInstruction, turn.ProviderText);
        Assert.Contains("Do not invent requirements", turn.ProviderText);
        Assert.Contains("wait for their approval", turn.ProviderText);
        Assert.Equal("add sharing", UserTurn.StripHostInstructions(turn.ProviderText));
        Assert.StartsWith(UserTurn.AskInstruction, new UserTurn("why?") { PlanOnly = true, AskOnly = true }.ProviderText);
    }
}
