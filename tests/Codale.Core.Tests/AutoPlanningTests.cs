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
    [InlineData("add recurring task schedules")]
    [InlineData("ok now add a dark theme to the settings page and wire it to the toggle")]
    public void Specific_features_and_ui_complaints_are_not_forced_into_plan(string text)
    {
        Assert.NotEqual(RouteIntent.Plan, MessageRouting.TryRouteLocally(text)?.Intent);
        Assert.Equal(RouteIntent.Act, MessageRouting.RouteWithoutModel(text).Intent);
        var call = new ToolCall("route", JsonSerializer.SerializeToElement(new { intent = "act" }));
        Assert.Equal(RouteIntent.Act, MessageRouting.Interpret(call, text).Intent);
    }

    // A one-line request for a whole new feature cannot say what it needs: the answer is a plan, not a guess.
    [Theory]
    [InlineData("implement login form")]
    [InlineData("implement notifications")]
    [InlineData("build a timetable editor")]
    [InlineData("add user authentication")]
    [InlineData("create a signup flow with email verification")]
    [InlineData("make the app better")]
    [InlineData("clean up the codebase")]
    [InlineData("migrate all functionality to the new project")]
    public void New_features_big_changes_and_unclear_requests_are_planned_first(string text)
    {
        Assert.Equal(RouteIntent.Plan, MessageRouting.TryRouteLocally(text)?.Intent);
        Assert.Equal(RouteIntent.Plan, MessageRouting.RouteWithoutModel(text).Intent);
        var call = new ToolCall("route", JsonSerializer.SerializeToElement(new { intent = "act" }));
        Assert.Equal(RouteIntent.Plan, MessageRouting.Interpret(call, text).Intent);
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

    [Fact]
    public void A_confident_local_decision_stands_and_the_model_only_settles_the_rest()
    {
        var confident = MessageRouting.Assess("implement login form");
        Assert.True(confident.Confident);
        Assert.Equal(RouteIntent.Plan, MessageRouting.Reconcile(confident, RouteIntent.Act).Intent);

        // The header complaint is unsure, so the model's reading is used, within limits.
        var unsure = MessageRouting.Assess("the header is ugly");
        Assert.False(unsure.Confident);
        Assert.Equal(RouteIntent.Act, MessageRouting.Reconcile(unsure, RouteIntent.Ask).Intent);   // an ask has to read as a question
        Assert.Equal(RouteIntent.Act, MessageRouting.Reconcile(unsure, RouteIntent.Plan).Intent);  // a plan needs more than a complaint's few words
    }

    [Fact]
    public void A_long_list_of_changes_is_only_a_weak_plan_that_a_model_may_confirm()
    {
        var text = "lets remove all logging code. then in chat history remove the session not visible anymore and only keep the latest four prompts to reduce ram usage. " +
                   "restart terminal should also clear the view and restart so it wont grow very big. reduce all ram usage and remove the performance counter and unused library.";
        var weak = MessageRouting.Assess(text);
        Assert.Equal(RouteIntent.Plan, weak.Intent);
        Assert.False(weak.Confident);
        Assert.Equal(RouteIntent.Act, MessageRouting.RouteWithoutModel(text).Intent);   // nobody confirmed it: do not interrupt
        Assert.Equal(RouteIntent.Plan, MessageRouting.Reconcile(weak, RouteIntent.Plan).Intent);
        Assert.Equal(RouteIntent.Act, MessageRouting.Reconcile(weak, RouteIntent.Act).Intent);
    }

    [Theory]
    [InlineData("ok lets implement this feature")]
    [InlineData("implement that")]
    [InlineData("build the project")]
    [InlineData("add a login button to the header")]
    [InlineData("make the login form fields required")]
    [InlineData("add a column to the database")]
    [InlineData("add a search box next to the filters")]
    public void Approvals_commands_and_changes_to_existing_things_are_not_new_features(string text) =>
        Assert.Equal(RouteIntent.Act, MessageRouting.RouteWithoutModel(text).Intent);

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
