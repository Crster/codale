namespace Codale.Settings.Tests;

public sealed class PresentationSettingsTests
{
    private static readonly ShortcutAction[] Actions =
    [new("first", "First", "", "A"), new("second", "Second", "", "B")];

    [Fact]
    public void ShortcutCachesInvalidateAndRemainIsolated()
    {
        var root = Directory.CreateTempSubdirectory("codale-shortcuts-").FullName;
        using var settings = new AppSettingsStore(Path.Combine(root, "first"));
        using var otherSettings = new AppSettingsStore(Path.Combine(root, "other"));
        using var first = new ShortcutBindings(settings, Actions, Parse);
        using var second = new ShortcutBindings(settings, Actions, Parse);
        using var other = new ShortcutBindings(otherSettings, Actions, Parse);
        Assert.Equal("first", first.Find("A"));
        Assert.Equal("first", second.Find("A"));
        first.Set("first", "C");
        Assert.Null(first.Find("A"));
        Assert.Equal("first", second.Find("C"));
        Assert.Equal("first", other.Find("A"));
        first.Set("second", "C");
        Assert.Equal("first", first.Find("C")); // First action wins a hand-edited duplicate.
        first.Set("first", "");
        Assert.Equal("second", first.Find("C"));
        settings.SetShortcut("first", "invalid");
        Assert.Equal("A", first.Get("first"));
        first.Dispose();
        Assert.Throws<ObjectDisposedException>(() => first.Find("C"));
        second.Set("first", "A");
        Assert.Equal("first", second.Find("A"));
    }

    private static string? Parse(string text) => text is "A" or "B" or "C" ? text : null;

    [Fact]
    public void SyntaxOverridesUseTheirOwnersSettingsAndCatalog()
    {
        var root = Directory.CreateTempSubdirectory("codale-syntax-settings-").FullName;
        using var settings = new AppSettingsStore(Path.Combine(root, "first"));
        using var otherSettings = new AppSettingsStore(Path.Combine(root, "other"));
        var first = new SyntaxSelection(settings, Path.Combine(root, "first"));
        var other = new SyntaxSelection(otherSettings, Path.Combine(root, "other"));
        var file = Path.Combine(root, "sample.cs");
        Assert.Equal("csharp", first.IdFor(file));
        first.SetOverride(file, SyntaxSelection.PlainText);
        Assert.Null(first.IdFor(file));
        Assert.True(first.HasOverride(file));
        Assert.False(other.HasOverride(file));
        Assert.Equal("csharp", other.IdFor(file));
        first.SetOverride(file, "missing-language");
        Assert.Equal("csharp", first.IdFor(file));
        first.SetOverride(file, null);
        Assert.False(first.HasOverride(file));
        Assert.Equal("csharp", first.IdFor(file));
    }

    [Fact]
    public void InstalledGrammarsStayInTheirInjectedCatalog()
    {
        var root = Directory.CreateTempSubdirectory("codale-syntax-catalogs-").FullName;
        using var settings = new AppSettingsStore(root);
        var first = new SyntaxSelection(settings, Path.Combine(root, "first"));
        var second = new SyntaxSelection(settings, Path.Combine(root, "second"));
        first.Start();
        second.Start();
        first.Store.Add(new Codale.Core.Syntax.LanguageEntry
        {
            Id = "owned-language",
            Name = "Owned",
            ScopeName = "source.owned",
            Extensions = ["ownedfixture"],
        }, "{\"scopeName\":\"source.owned\",\"patterns\":[]}");
        var file = Path.Combine(root, "file.ownedfixture");
        Assert.Equal("owned-language", first.IdFor(file));
        Assert.Null(second.IdFor(file));
        first.Store.Remove("owned-language");
        Assert.Null(first.IdFor(file));
    }
}