using Codale.Core.Features;
using Codale.Core.Helper;

namespace Codale.Core.Tests;

public sealed class FeatureRoutingTests
{
    private static readonly FeatureInfo Login = new() { Id = "login1", Name = "Login", Description = "Sign in flow" };
    private static readonly FeatureInfo Dashboard = new() { Id = "dash1", Name = "Dashboard" };
    private static readonly FeatureInfo[] Both = [Login, Dashboard];

    private static ToolCall Call(string feature, string? name = null, string intent = "act")
    {
        var nameArgument = name is null ? "" : $",\"name\":\"{name}\"";
        return ToolCall.FromJson(
            $"{{\"tool\":\"route\",\"arguments\":{{\"intent\":\"{intent}\",\"feature\":\"{feature}\"{nameArgument}}}}}")!;
    }

    [Fact]
    public void A_message_that_is_no_project_work_is_skipped()
    {
        var decision = FeatureRouting.Interpret(Call("skip", intent: "ask"), "what is a mutex in general?", Both, "login1");

        Assert.Equal(FeatureAction.Skip, decision.Action);
    }

    [Fact]
    public void Current_and_unknown_ids_leave_the_message_where_it_was_typed()
    {
        Assert.Equal(FeatureAction.Stay, FeatureRouting.Interpret(Call("current"), "fix the redirect after sign in", Both, "login1").Action);
        Assert.Equal(FeatureAction.Stay, FeatureRouting.Interpret(Call("nope"), "fix the redirect after sign in", Both, "login1").Action);
        Assert.Equal(FeatureAction.Stay, FeatureRouting.Interpret(Call("login1"), "fix the redirect after sign in", Both, "login1").Action);
    }

    [Fact]
    public void Another_listed_feature_moves_the_chat()
    {
        var decision = FeatureRouting.Interpret(Call("dash1"), "redesign the charts on the dashboard page", Both, "login1");

        Assert.Equal(FeatureAction.Switch, decision.Action);
        Assert.Equal("dash1", decision.FeatureId);
    }

    [Fact]
    public void A_message_that_plainly_points_back_never_moves()
    {
        var decision = FeatureRouting.Interpret(Call("dash1"), "it still fails", Both, "login1");

        Assert.Equal(FeatureAction.Stay, decision.Action);
    }

    [Fact]
    public void A_new_feature_takes_the_models_name()
    {
        var decision = FeatureRouting.Interpret(Call("new", "File downloads"), "add resumable downloads with progress", Both, "login1");

        Assert.Equal(FeatureAction.Create, decision.Action);
        Assert.Equal(["File downloads"], decision.NewNames);
    }

    [Fact]
    public void Only_a_project_with_no_features_takes_several_names()
    {
        var seeded = FeatureRouting.Interpret(Call("new", "Login | Dashboard | Settings"), "build a todo app with login, dashboard and settings", [], null);
        var later = FeatureRouting.Interpret(Call("new", "Login | Dashboard"), "add reporting with exports and scheduling", Both, "login1");

        Assert.Equal(["Login", "Dashboard", "Settings"], seeded.NewNames);
        Assert.Equal(["Login"], later.NewNames);
    }

    [Fact]
    public void A_new_feature_without_a_name_is_named_from_the_prompt()
    {
        var names = FeatureRouting.CleanNames(null, "add dark mode to the settings page", seed: false);

        Assert.Single(names);
        Assert.False(string.IsNullOrWhiteSpace(names[0]));
    }

    [Fact]
    public void No_usable_call_leaves_the_message_in_place_with_a_mode_read_from_its_words()
    {
        var decision = FeatureRouting.Interpret(null, "why is the build slow?", Both, "login1");

        Assert.Equal(FeatureAction.Stay, decision.Action);
        Assert.Equal(RouteIntent.Ask, decision.Route.Intent);
    }

    [Fact]
    public void The_tool_allows_every_listed_id_and_the_three_words()
    {
        var feature = FeatureRouting.RouteTool(Both).Parameters.Single(p => p.Name == "feature");

        Assert.Equal(["skip", "current", "new", "login1", "dash1"], feature.AllowedValues);
    }

    [Fact]
    public void The_conversation_marks_the_current_feature_and_fences_the_message()
    {
        var text = FeatureRouting.BuildConversation("fix it", Both, "login1", "I changed AuthController.");

        Assert.Contains("login1 (current): Login - Sign in flow", text);
        Assert.Contains("dash1: Dashboard", text);
        Assert.Contains("<new_message>", text);
        Assert.Contains("<last_agent_message>", text);
    }
}
