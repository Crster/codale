using Codale.Core.Syntax;

namespace Codale.Core.Tests.Syntax;

public sealed class SyntaxThemeTests
{
    private static SyntaxTheme Rules(string rules) => SyntaxTheme.Parse("""
        { "name": "Scope tests", "type": "dark", "colors": { "editor.foreground": "#abcdef", "editor.background": "#123456" },
          "tokenColors": [
        """ + rules + "] }");

    [Fact]
    public void Full_scope_stacks_distinguish_same_kind_tokens_and_inherit_individual_properties()
    {
        var theme = Rules("""
            { "scope": "entity.name", "settings": { "fontStyle": "bold italic" } },
            { "scope": "source.cs entity.name.function", "settings": { "foreground": "#ff0000" } },
            { "scope": "source.js entity.name.function", "settings": { "foreground": "#00ff00" } },
            { "scope": "entity.name.function.special", "settings": { "fontStyle": "" } }
            """);
        var cs = theme.Resolve("source.cs\u001fmeta.class.cs\u001fentity.name.function.cs");
        var js = theme.Resolve("source.js\u001fentity.name.function.js");
        Assert.Equal(0xffff0000u, cs.Foreground);
        Assert.Equal(0xff00ff00u, js.Foreground);
        Assert.Equal(SyntaxFontStyle.Bold | SyntaxFontStyle.Italic, cs.FontStyle);
        var reset = theme.Resolve("source.cs\u001fentity.name.function.special.cs");
        Assert.Equal(SyntaxFontStyle.None, reset.FontStyle);
        Assert.Equal(0xffff0000u, reset.Foreground);
        Assert.Equal(0xffabcdefu, theme.Resolve("source.cs\u001fvariable.other.cs").Foreground);
    }

    [Theory]
    [InlineData("source.json meta.dictionary > string", "source.json\u001fmeta.dictionary.json\u001fstring.quoted.json", true)]
    [InlineData("source.json meta.dictionary > string", "source.json\u001fmeta.dictionary.json\u001fmeta.value\u001fstring.quoted.json", false)]
    [InlineData("source.json meta.dictionary string", "source.json\u001fmeta.dictionary.json\u001fmeta.value\u001fstring.quoted.json", true)]
    [InlineData("variable - meta.import", "source.ts\u001fmeta.import.ts\u001fvariable.other.ts", false)]
    [InlineData("variable - meta.import", "source.ts\u001fvariable.other.ts", true)]
    [InlineData("source.cs (string | comment)", "source.cs\u001fcomment.line.cs", true)]
    [InlineData("* - (string | comment)", "source.cs\u001fcomment.line.cs", false)]
    [InlineData("* - (string | comment)", "source.cs\u001fstring.quoted.cs", false)]
    [InlineData("* - (string | comment)", "source.cs\u001fkeyword.control.cs", true)]
    [InlineData("markup.heading | markup.heading entity.name", "text.md\u001fmarkup.heading.1\u001fentity.name.section", true)]
    public void Selectors_match_ancestors_children_alternatives_and_exclusions(string selector, string scopes, bool matches)
    {
        var json = System.Text.Json.JsonSerializer.Serialize(selector);
        var theme = Rules("{\"scope\":" + json + ",\"settings\":{\"foreground\":\"#ff0000\"}}");
        Assert.Equal(matches ? 0xffff0000u : 0xffabcdefu, theme.Resolve(scopes).Foreground);
    }

    [Fact]
    public void Styles_backgrounds_defaults_and_alpha_are_preserved()
    {
        var theme = Rules("""
            { "settings": { "fontStyle": "italic", "foreground": "#111111" } },
            { "scope": "comment", "settings": { "fontStyle": "bold underline strikethrough", "background": "#1234" } }
            """);
        var style = theme.Resolve("source.cs\u001fcomment.line.cs");
        Assert.Equal(SyntaxFontStyle.Bold | SyntaxFontStyle.Underline | SyntaxFontStyle.Strikethrough, style.FontStyle);
        Assert.Equal(0x44112233u, style.Background);
        Assert.Equal(0xffabcdefu, style.Foreground);
        Assert.Equal(SyntaxFontStyle.Italic, theme.Resolve("source.cs").FontStyle);
        Assert.Equal(0xff123456u, theme.EditorColors["editor.background"]);
        Assert.Null(theme.Resolve("source.cs").Background);
    }

    [Fact]
    public void Legacy_global_settings_supply_default_editor_colors()
    {
        var theme = SyntaxTheme.Parse("""
            { "name": "Legacy", "type": "light", "settings": [
              { "settings": { "foreground": "#123", "background": "#fefefe", "fontStyle": "italic" } },
              { "scope": "comment", "settings": { "foreground": "#456" } }
            ] }
            """);
        Assert.Equal(0xff112233u, theme.EditorColors["editor.foreground"]);
        Assert.Equal(0xfffefefeu, theme.EditorColors["editor.background"]);
        Assert.Equal(0xff445566u, theme.Resolve("source.cs\u001fcomment.line").Foreground);
    }

    [Fact]
    public void Non_color_ui_metadata_does_not_reject_an_otherwise_usable_theme()
    {
        var theme = SyntaxTheme.Parse("""
            { "name": "Metadata", "type": "dark", "colors": {
              "editor.background": "#123456", "symbolIcon.constantForeground": ["#fff", "#000"], "editor.lineHighlightBorder": null
            }, "tokenColors": [] }
            """);
        Assert.Equal(0xff123456u, theme.EditorColors["editor.background"]);
    }
    [Fact]
    public void Theme_rules_use_specificity_then_last_rule_and_accept_scope_arrays()
    {
        var theme = SyntaxTheme.Parse("""
            { "name": "Test", "type": "dark", "tokenColors": [
              { "scope": "keyword.operator", "settings": { "foreground": "#112233" } },
              { "scope": "keyword", "settings": { "foreground": "#fff" } },
              { "scope": ["comment, string", "constant"], "settings": { "foreground": "#12345680" } },
              { "scope": "keyword.operator", "settings": { "foreground": "#abcdef" } }
            ] }
            """);
        Assert.Equal(0xffabcdefu, theme.Colors[CodeTokenKind.Operator]);
        Assert.Equal(0xffffffffu, theme.Colors[CodeTokenKind.Keyword]);
        Assert.Equal(0x80123456u, theme.Colors[CodeTokenKind.Comment]);
        Assert.Equal(0x80123456u, theme.Colors[CodeTokenKind.String]);
    }

    [Theory]
    [InlineData("{}")]
    [InlineData("<html>")]
    [InlineData("{\"name\":\"Test\",\"type\":\"dark\",\"tokenColors\":[]}")]
    public void Invalid_themes_are_rejected(string json) => Assert.Throws<InvalidDataException>(() => SyntaxTheme.Parse(json));

    [Fact]
    public void Catalog_has_both_light_and_dark_themes_and_unique_ids()
    {
        var entries = SyntaxThemeCatalog.Entries;
        Assert.Contains(entries, t => t.Type == "light");
        Assert.Contains(entries, t => t.Type == "dark");
        Assert.Equal(entries.Count, entries.Select(t => t.Id).Distinct().Count());
        Assert.All(entries, t => Assert.StartsWith("https://raw.githubusercontent.com/shikijs/textmate-grammars-themes/", t.Url));
    }
}
