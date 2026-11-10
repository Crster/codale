using Codale.Smart;

namespace Codale.Smart.Tests;

public sealed class SmartBriefTests : IDisposable
{
    private readonly string _root = Directory.CreateTempSubdirectory("brief").FullName;

    public SmartBriefTests() => File.WriteAllText(Path.Combine(_root, "Auth.cs"), string.Join("\n", Enumerable.Range(1, 40).Select(i => "line " + i)));

    [Fact]
    public void Verified_citations_survive_and_the_layout_is_normalised()
    {
        var brief = SmartBrief.Validate("Task: rotate tokens\nWhere:\n- Auth.cs:10-20 — Rotate\nFacts:\n- uses clock (Auth.cs:5)\nnoise", _root);
        Assert.Equal("Task: rotate tokens\nWhere:\n- Auth.cs:10-20 — Rotate\nFacts:\n- uses clock (Auth.cs:5)", brief);
    }

    [Theory]
    [InlineData("Task: t\nWhere:\n- Ghost.cs:1-2 — nothing")]
    [InlineData("Task: t\nWhere:\n- Auth.cs:30-90 — past the end")]
    [InlineData("Task: t\nWhere:\n- Auth.cs:20-10 — reversed")]
    [InlineData("Task: t\nWhere:\n- ../Outside.cs:1-2 — escapes the project")]
    [InlineData("Task: t\nWhere:\n- no citation at all")]
    [InlineData("Where:\n- Auth.cs:1-2 — no task")]
    [InlineData("")]
    public void Unverifiable_briefs_are_rejected(string text) => Assert.Null(SmartBrief.Validate(text, _root));

    [Fact]
    public void Only_the_unverifiable_lines_are_dropped()
    {
        var brief = SmartBrief.Validate("Task: t\nWhere:\n- Auth.cs:1-3 — ok\n- Fake.cs:1-3 — invented\nFacts:\n- invented (Fake.cs:9)", _root);
        Assert.Equal("Task: t\nWhere:\n- Auth.cs:1-3 — ok", brief);
    }

    [Fact]
    public void Copied_placeholder_brackets_are_removed_from_the_task()
    {
        Assert.Equal("Task: explain the addition helper\nWhere:\n- Auth.cs:1-3 — ok",
            SmartBrief.Validate("Task: <explain the addition helper>\nWhere:\n- Auth.cs:1-3 — ok", _root));
        Assert.StartsWith("Task: <T> is generic, keep it", SmartBrief.Validate("Task: <T> is generic, keep it\nWhere:\n- Auth.cs:1-3 — ok", _root));
    }

    public void Dispose() => Directory.Delete(_root, true);
}
