using System.Text.Json;

using Codale.Core.Helper;

namespace Codale.Core.Tests;

public sealed class MessageRoutingTests
{
    private static ToolCall Call(string message, string intent, string related) =>
        new("route", JsonSerializer.SerializeToElement(new { message, intent, related }));

    [Fact]
    public void No_call_sends_the_original_as_a_plain_turn()
    {
        var decision = MessageRouting.Interpret(null, "fix teh bug", hasConversation: true);

        Assert.Equal(new RouteDecision("fix teh bug", RouteIntent.Act, NewTopic: false), decision);
    }

    [Fact]
    public void A_faithful_rewrite_is_kept_with_its_intent()
    {
        var decision = MessageRouting.Interpret(
            Call("What does SendCoreAsync do?", "ask", "yes"), "wat does SendCoreAsync do", hasConversation: true);

        Assert.Equal("What does SendCoreAsync do?", decision.Text);
        Assert.Equal(RouteIntent.Ask, decision.Intent);
        Assert.False(decision.NewTopic);
    }

    [Fact]
    public void Plan_intent_and_new_topic_are_read()
    {
        var decision = MessageRouting.Interpret(
            Call("Refactor the git panel.", "plan", "no"), "refactor the git panel", hasConversation: true);

        Assert.Equal(RouteIntent.Plan, decision.Intent);
        Assert.True(decision.NewTopic);
    }

    [Fact]
    public void The_first_message_is_never_a_new_topic()
    {
        var decision = MessageRouting.Interpret(Call("Hello.", "ask", "no"), "hello", hasConversation: false);

        Assert.False(decision.NewTopic);
    }

    [Fact]
    public void A_change_request_the_model_calls_ask_is_sent_as_act()
    {
        const string original = "lets make the snake design more snake. not like a stitch balloon";

        var decision = MessageRouting.Interpret(
            Call("Let's make the snake design look more like a snake, not like a stitched balloon.", "ask", "yes"),
            original,
            hasConversation: true);

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
        var decision = MessageRouting.Interpret(Call(original, "ask", "yes"), original, hasConversation: true);

        Assert.Equal(RouteIntent.Act, decision.Intent);
    }

    [Theory]
    [InlineData("how does the router decide?")]
    [InlineData("what is the default mode")]
    [InlineData("can you explain the todo flow")]
    [InlineData("does the todo depend on the mode?")]
    public void A_pure_question_stays_ask_when_the_model_says_ask(string original)
    {
        var decision = MessageRouting.Interpret(Call(original, "ask", "yes"), original, hasConversation: true);

        Assert.Equal(RouteIntent.Ask, decision.Intent);
    }

    [Fact]
    public void A_real_question_stays_ask()
    {
        var decision = MessageRouting.Interpret(
            Call("Why does the terminal flicker?", "ask", "yes"), "why does the terminal flicker", hasConversation: true);

        Assert.Equal(RouteIntent.Ask, decision.Intent);
    }

    [Fact]
    public void An_unknown_intent_becomes_act()
    {
        var decision = MessageRouting.Interpret(Call("Do it.", "maybe", "yes"), "do it", hasConversation: true);

        Assert.Equal(RouteIntent.Act, decision.Intent);
    }

    [Fact]
    public void An_empty_rewrite_falls_back_to_the_original()
    {
        var decision = MessageRouting.Interpret(Call("  ", "act", "yes"), "rename foo to bar", hasConversation: true);

        Assert.Equal("rename foo to bar", decision.Text);
    }

    [Fact]
    public void A_rewrite_that_shrinks_too_much_falls_back()
    {
        const string original = "please rename the variable in the parser loop and also update every caller of it";

        var decision = MessageRouting.Interpret(Call("Rename it.", "act", "yes"), original, hasConversation: true);

        Assert.Equal(original, decision.Text);
    }

    [Fact]
    public void A_rewrite_that_loses_inline_code_falls_back()
    {
        const string original = "change `MaxTokens = 512` to something bigger pls";

        var decision = MessageRouting.Interpret(
            Call("Change MaxTokens = 512 to something bigger, please.", "act", "yes"), original, hasConversation: true);

        Assert.Equal(original, decision.Text);
    }

    [Fact]
    public void A_short_message_may_grow_a_little_but_not_balloon()
    {
        Assert.True(MessageRouting.IsFaithful("Fix the test.", "fix test"));
        Assert.False(MessageRouting.IsFaithful(new string('x', 200), "fix test"));
    }

    [Theory]
    [InlineData("lets make the snake design more snake. not like a stitch balloon",
        "Let's make the snake design look more like a snake, not like a stitched balloon.")]
    [InlineData("wat does sendcoreasync do", "What does SendCoreAsync do?")]
    [InlineData("fix teh bug in the parser", "Fix the bug in the parser.")]
    public void A_clarity_edit_is_kept(string original, string rewrite)
    {
        Assert.True(MessageRouting.IsFaithful(rewrite, original));
    }

    [Theory]
    [InlineData("lets make the snake design more snake. not like a stitch balloon",
        "Redesign the serpent sprite with realistic scales instead of an inflated, stitched look.")]
    [InlineData("the terminal flickers when i resize",
        "Investigate rendering performance issues during window resize events in the console control.")]
    public void A_rewrite_that_changes_the_content_falls_back(string original, string rewrite)
    {
        Assert.False(MessageRouting.IsFaithful(rewrite, original));
    }

    [Theory]
    [InlineData("lets make teh buton more nicer and fix the aligment of teh heder",
        "Let's make the button nicer and fix the alignment of the header.")]
    [InlineData("the sesion list dosent refrsh when i delet a sesion",
        "The session list doesn't refresh when I delete a session.")]
    [InlineData("i want the panel to be resizable and it should remeber the width, also the colapse button is to small",
        "I want the panel to be resizable and remember its width. Also, the collapse button is too small.")]
    public void A_heavily_misspelled_message_keeps_its_correction(string original, string rewrite)
    {
        Assert.True(MessageRouting.IsFaithful(rewrite, original));
    }

    [Fact]
    public void A_prefix_match_needs_the_same_start_so_redesign_is_not_design()
    {
        Assert.False(MessageRouting.IsFaithful("Redesign everything now.", "design the pages"));
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
        var decision = MessageRouting.Interpret(Call(original, "act", "no"), original, hasConversation: true);

        Assert.False(decision.NewTopic);
    }

    [Theory]
    [InlineData("add a dark theme to the settings page")]
    [InlineData("write unit tests for the git status parser")]
    public void An_unrelated_task_moves_to_a_new_session(string original)
    {
        var decision = MessageRouting.Interpret(Call(original, "act", "no"), original, hasConversation: true);

        Assert.True(decision.NewTopic);
    }

    [Fact]
    public void The_opening_message_is_kept_once_it_scrolls_out_of_the_recent_turns()
    {
        var conversation = MessageRouting.BuildConversation(
            "next", "Login", ["b", "c", "d"], lastAssistantText: null, firstUserTurn: "fix the login redirect");
        Assert.Contains("First message: fix the login redirect", conversation);

        var shortSession = MessageRouting.BuildConversation(
            "next", "Login", ["fix the login redirect"], lastAssistantText: null, firstUserTurn: "fix the login redirect");
        Assert.DoesNotContain("First message", shortSession);
    }

    [Fact]
    public void The_conversation_clips_long_context_and_marks_a_first_message()
    {
        var first = MessageRouting.BuildConversation("hello", sessionTitle: null, [], lastAssistantText: null);
        Assert.Contains("none yet", first);
        Assert.EndsWith("hello", first);

        var longTurn = new string('a', 5000);
        var ongoing = MessageRouting.BuildConversation("next", "Git panel", [longTurn], new string('b', 5000));
        Assert.Contains("Title: Git panel", ongoing);
        Assert.True(ongoing.Length < 2000);
        Assert.EndsWith("next", ongoing);
    }
}
