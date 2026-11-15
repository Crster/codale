using Codale.Core.Markdown;

namespace Codale.Core.Tests;

public sealed class MarkdownLinksTests
{
    [Fact]
    public void AWebAddressIsNotAFile() =>
        Assert.Null(MarkdownLinks.FileTarget("https://example.com/docs"));

    [Fact]
    public void ARelativeFileLinkIsAFileReference()
    {
        var target = MarkdownLinks.FileTarget("src/a.cs");

        Assert.NotNull(target);
        Assert.EndsWith("a.cs", target.Path);
        Assert.Equal(0, target.Line);
    }

    [Fact]
    public void AFragmentGivesTheLine()
    {
        var target = MarkdownLinks.FileTarget("src/a.cs#L42");

        Assert.NotNull(target);
        Assert.Equal(42, target.Line);
    }

    [Fact]
    public void AFileUriIsReadAsALocalPath()
    {
        var target = MarkdownLinks.FileTarget("file:///C:/work/a.cs#L3");

        Assert.NotNull(target);
        Assert.EndsWith("a.cs", target.Path);
        Assert.Equal(3, target.Line);
    }

    [Fact]
    public void InlineTextIsTheVisibleTextOfNestedRuns()
    {
        IReadOnlyList<MarkdownInline> content =
        [
            new MarkdownInline.Text("see "),
            new MarkdownInline.Emphasis([new MarkdownInline.Text("this")], Strong: true),
            new MarkdownInline.Text(" and "),
            new MarkdownInline.Link([new MarkdownInline.CodeSpan("code")], "https://x"),
        ];

        Assert.Equal("see this and code", MarkdownLinks.InlineText(content));
    }

    [Fact]
    public void ADataImageDecodesFromBase64() =>
        Assert.Equal("ABC"u8.ToArray(), MarkdownLinks.DecodeDataImage("data:image/png;base64,QUJD"));

    [Fact]
    public void ADataImageWithoutBase64IsRefused() =>
        Assert.Null(MarkdownLinks.DecodeDataImage("data:image/png,QUJD"));

    [Fact]
    public void ADataImageWithCorruptBase64IsRefused() =>
        Assert.Null(MarkdownLinks.DecodeDataImage("data:image/png;base64,!!!"));

    [Fact]
    public void ADataImageWithoutAPayloadIsRefused() =>
        Assert.Null(MarkdownLinks.DecodeDataImage("data:image/png;base64"));
}
