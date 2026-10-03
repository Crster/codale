using System.Text.Json;

using Codale.Core.Helper;

namespace Codale.Core.Tests;

public sealed class MessageRoutingTests
{
    private static ToolCall Call(string intent, string related) =>
        new("route", JsonSerializer.SerializeToElement(new { intent, related }));

    [Fact]
    public void No_call_sends_the_original_as_a_plain_turn()
    {
        var decision = MessageRouting.Interpret(null, "fix teh bug", canMoveTopic: true);

        Assert.Equal(new RouteDecision("fix teh bug", RouteIntent.Act, NewTopic: false), decision);
    }

    [Fact]
    public void The_message_is_sent_as_typed()
    {
        // A model that still writes a message back is ignored: the user's words go out.
        var call = new ToolCall("route", JsonSerializer.SerializeToElement(
            new { message = "What does SendCoreAsync do?", intent = "ask", related = "yes" }));

        var decision = MessageRouting.Interpret(call, "what does SendCoreAsync do", canMoveTopic: true);

        Assert.Equal("what does SendCoreAsync do", decision.Text);
        Assert.Equal(RouteIntent.Ask, decision.Intent);
        Assert.False(decision.NewTopic);
    }

    [Fact]
    public void Plan_intent_and_new_topic_are_read()
    {
        var decision = MessageRouting.Interpret(Call("plan", "no"), "refactor the git panel", canMoveTopic: true);

        Assert.Equal(RouteIntent.Plan, decision.Intent);
        Assert.True(decision.NewTopic);
    }

    [Fact]
    public void Without_an_agent_summary_the_message_never_moves()
    {
        var decision = MessageRouting.Interpret(Call("act", "no"), "add a dark theme to the settings page", canMoveTopic: false);

        Assert.False(decision.NewTopic);
    }

    [Fact]
    public void A_change_request_the_model_calls_ask_is_sent_as_act()
    {
        var decision = MessageRouting.Interpret(
            Call("ask", "yes"), "lets make the snake design more snake. not like a stitch balloon", canMoveTopic: true);

        Assert.Equal(RouteIntent.Act, decision.Intent);
    }

    [Theory]
    [InlineData("lets make the snake design more snake", true)]
    [InlineData("can you please fix the login redirect?", true)]
    [InlineData("Add a dark theme", true)]
    [InlineData("why does the terminal flicker?", false)]
    [InlineData("how do I make the snake longer", false)]
    [InlineData("what does SendCoreAsync do", false)]
    [InlineData("show me how the router works", false)]
    public void Change_requests_are_told_from_questions(string message, bool isChange)
    {
        Assert.Equal(isChange, MessageRouting.IsChangeRequest(message));
    }

    [Theory]
    [InlineData("the file list has a dot before every name")]
    [InlineData("next issue is the retitle modal looks ugly")]
    [InlineData("yes go ahead")]
    [InlineData("why is the panel slow? speed it up")]
    [InlineData("I want the center panel to fill the window")]
    public void A_statement_or_mixed_request_the_model_calls_ask_is_sent_as_act(string original)
    {
        var decision = MessageRouting.Interpret(Call("ask", "yes"), original, canMoveTopic: true);

        Assert.Equal(RouteIntent.Act, decision.Intent);
    }

    [Theory]
    [InlineData("how does the router decide?")]
    [InlineData("what is the default mode")]
    [InlineData("can you explain the todo flow")]
    [InlineData("does the todo depend on the mode?")]
    [InlineData("why does the terminal flicker")]
    public void A_pure_question_stays_ask_when_the_model_says_ask(string original)
    {
        var decision = MessageRouting.Interpret(Call("ask", "yes"), original, canMoveTopic: true);

        Assert.Equal(RouteIntent.Ask, decision.Intent);
    }

    [Fact]
    public void An_unknown_intent_becomes_act()
    {
        var decision = MessageRouting.Interpret(Call("maybe", "yes"), "do it", canMoveTopic: true);

        Assert.Equal(RouteIntent.Act, decision.Intent);
    }

    [Theory]
    [InlineData("yes")]
    [InlineData("go ahead")]
    [InlineData("ok do it")]
    [InlineData("continue")]
    public void A_bare_go_ahead_is_routed_without_the_model(string message)
    {
        Assert.Equal(new RouteDecision(message, RouteIntent.Act, NewTopic: false), MessageRouting.TryRouteLocally(message));
    }

    [Theory]
    [InlineData("ok now add a dark theme to the settings page and wire it to the toggle")]
    [InlineData("why does the terminal flicker")]
    public void Anything_more_than_a_go_ahead_goes_to_the_model(string message)
    {
        Assert.Null(MessageRouting.TryRouteLocally(message));
    }

    [Fact]
    public void Without_a_model_a_pure_question_is_ask_and_a_request_is_act()
    {
        Assert.Equal(RouteIntent.Ask, MessageRouting.Interpret(null, "how does the router decide?", true).Intent);
        Assert.Equal(RouteIntent.Act, MessageRouting.Interpret(null, "can you add a dark theme?", true).Intent);
        Assert.Equal(RouteIntent.Act, MessageRouting.RouteWithoutModel("the header is ugly").Intent);
    }

    [Theory]
    [InlineData("also make it blue")]
    [InlineData("it still crashes on start")]
    [InlineData("the button overlaps again after resizing")]
    [InlineData("that broke the sidebar")]
    [InlineData("please try again with a smaller font")]
    [InlineData("revert the last change")]
    [InlineData("nope")]
    public void A_follow_up_never_moves_to_a_new_session(string original)
    {
        var decision = MessageRouting.Interpret(Call("act", "no"), original, canMoveTopic: true);

        Assert.False(decision.NewTopic);
    }

    [Theory]
    [InlineData("add a dark theme to the settings page")]
    [InlineData("write unit tests for the git status parser")]
    public void An_unrelated_task_moves_to_a_new_session(string original)
    {
        var decision = MessageRouting.Interpret(Call("act", "no"), original, canMoveTopic: true);

        Assert.True(decision.NewTopic);
    }

    [Fact]
    public void The_conversation_holds_only_the_clipped_last_agent_message()
    {
        var conversation = MessageRouting.BuildConversation("next", new string('b', 5000));

        Assert.StartsWith("Last agent message: ", conversation);
        Assert.True(conversation.Length < 1000);
        Assert.EndsWith("New message:\nnext".ReplaceLineEndings(), conversation);
    }

    [Fact]
    public void Without_an_agent_message_only_the_new_message_is_sent()
    {
        var conversation = MessageRouting.BuildConversation("hello", lastAssistantText: null);

        Assert.Equal("New message:" + Environment.NewLine + "hello", conversation);
    }
}
