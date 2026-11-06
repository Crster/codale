using System.Text.Json;

using Codale.Core.Agents;

namespace Codale.Core.Tests;

public sealed class AskedQuestionsTests
{
    private static JsonElement Input(string json) => JsonDocument.Parse(json).RootElement.Clone();

    private static readonly JsonElement TwoQuestions = Input("""
        {"questions":[
          {"question":"Which tiers, by time away?","header":"Absence tiers","options":[],"multiSelect":false},
          {"question":"Which \"meal\" fields change?","header":"Fields","options":[],"multiSelect":true}
        ]}
        """);

    [Fact]
    public void Answers_are_read_back_from_the_result_text()
    {
        var asked = AskedQuestions.Read(TwoQuestions,
            "Your questions have been answered: \"Which tiers, by time away?\"=\"3 tiers (Recommended)\", " +
            "\"Which \"meal\" fields change?\"=\"MealsPerDay, MealServant\". You can now continue with these answers in mind.");

        Assert.Equal(2, asked.Count);
        Assert.Equal("Absence tiers", asked[0].Header);
        Assert.Equal("3 tiers (Recommended)", asked[0].Answer);
        Assert.Equal("MealsPerDay, MealServant", asked[1].Answer);
    }

    [Fact]
    public void A_single_answer_stops_before_the_closing_sentence()
    {
        var asked = AskedQuestions.Read(
            Input("""{"questions":[{"question":"Keep elites?","header":"Elites"}]}"""),
            "Your questions have been answered: \"Keep elites?\"=\"Yes. Under 7 days only\". You can now continue with these answers in mind.");

        Assert.Equal("Yes. Under 7 days only", Assert.Single(asked).Answer);
    }

    [Fact]
    public void An_unrecognised_result_leaves_the_questions_unanswered()
    {
        var asked = AskedQuestions.Read(TwoQuestions, "User declined to answer questions");

        Assert.All(asked, q => Assert.Null(q.Answer));
        Assert.Contains("A: (no answer)", AskedQuestions.Format(asked));
    }

    [Fact]
    public void The_record_lists_each_question_then_its_answer()
    {
        var text = AskedQuestions.Format(
        [
            new AskedQuestion("Absence tiers", "Which tiers?", "3 tiers"),
            new AskedQuestion(null, "Keep elites?", "Yes"),
        ]);

        Assert.Equal("[Absence tiers] Q: Which tiers?\nA: 3 tiers\n\nQ: Keep elites?\nA: Yes", text);
    }

    [Fact]
    public void Input_without_questions_reads_as_none()
    {
        Assert.Empty(AskedQuestions.Read(Input("""{"plan":"x"}"""), "anything"));
    }
}
