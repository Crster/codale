using Codale.Smart;

namespace Codale.Smart.Tests;

public sealed class SmartCompletionTextTests
{
    [Fact]
    public void AnOverlapWithWhatIsTypedIsDropped() =>
        Assert.Equal("tem -File", SmartCompletionText.WithoutRepeat("Get-ChildI", "Get-ChildItem -File"));

    [Fact]
    public void OnlyTheLastLineOfThePrefixCounts() =>
        Assert.Equal("temX", SmartCompletionText.WithoutRepeat("first\nGet-ChildI", "Get-ChildItemX"));

    [Fact]
    public void AnOverlapShorterThanThreeCharactersIsKept() =>
        Assert.Equal("ab", SmartCompletionText.WithoutRepeat("xa", "ab"));

    [Fact]
    public void ACompletionThatIsOnlyTheOverlapIsNothing() =>
        Assert.Null(SmartCompletionText.WithoutRepeat("Get-Child", "Get-Child"));

    [Fact]
    public void CleanKeepsTheFirstLine() =>
        Assert.Equal("first", SmartCompletionText.Clean("first\nsecond"));

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("   ")]
    [InlineData("```csharp")]
    [InlineData("<tool_call>x")]
    public void CleanRejectsEmptyFencesAndToolSyntax(string? text) =>
        Assert.Null(SmartCompletionText.Clean(text));

    [Fact]
    public void CleanRejectsOverlongLines() =>
        Assert.Null(SmartCompletionText.Clean(new string('a', 161)));

    [Fact]
    public void CleanRejectsControlCharacters() =>
        Assert.Null(SmartCompletionText.Clean("bad\u0001line"));
}
