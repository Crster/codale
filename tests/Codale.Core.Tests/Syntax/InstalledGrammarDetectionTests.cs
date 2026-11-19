using Codale.Core.Syntax;
using TextMateSharp.Grammars;

namespace Codale.Core.Tests.Syntax;

public sealed class InstalledGrammarDetectionTests : IDisposable
{
    private readonly string _root = Path.Combine(Path.GetTempPath(), "codale-associations-" + Guid.NewGuid().ToString("N"));
    private SyntaxStore Store => field ??= new SyntaxStore(_root);
    private LanguageCatalog Catalog => new(Store, new RegistryOptions(ThemeName.DarkPlus));

    public void Dispose() { try { Directory.Delete(_root, true); } catch { } }

    private void Install(string id, string scope, string? json = null) => Store.Add(new LanguageEntry
    {
        Id = id, Name = id, ScopeName = scope, Source = GrammarSource.Downloaded,
    }, json ?? $$"""{ "scopeName": "{{scope}}", "patterns": [] }""");

    public static IEnumerable<object[]> CatalogSamples() => SyntaxCatalogClient.Entries.Where(e => !e.Hidden)
        .Select(e => new object[] { e.Id, e.Extensions.Length > 0 ? "example." + e.Extensions[0] : e.Filenames.First() });

    [Theory]
    [MemberData(nameof(CatalogSamples))]
    public void Every_catalog_language_recovers_file_associations_for_old_downloads(string id, string filename)
    {
        var entry = SyntaxCatalogClient.Entries.Single(e => e.Id == id);
        Install(id, entry.ScopeName);
        var catalog = Catalog;
        foreach (var sample in entry.Extensions.Select(extension => "example." + extension).Concat(entry.Filenames).Append(filename).Distinct())
            Assert.True(catalog.ForFile(sample)?.Id == id, $"{id} did not detect declared association '{sample}'; detected {catalog.ForFile(sample)?.Id ?? "plain text"}.");
        var reloaded = new LanguageCatalog(new SyntaxStore(_root), new RegistryOptions(ThemeName.DarkPlus));
        Assert.Equal(id, reloaded.ForFile(filename)?.Id);
    }

    [Theory]
    [InlineData("vue-vine", "source.vue-vine", "src/Component.vine.ts")]
    [InlineData("docker", "source.dockerfile", "Dockerfile.development")]
    [InlineData("docker", "source.dockerfile", "Containerfile.test")]
    [InlineData("systemd", "source.systemd", "/etc/systemd/system.conf")]
    [InlineData("systemd", "source.systemd", "/etc/example.service.d/override.conf")]
    [InlineData("systemd", "source.systemd", "/etc/repart.d/example.conf")]
    [InlineData("ssh-config", "source.ssh-config", "/home/example/.ssh/config")]
    public void Multipart_extensions_and_publisher_filename_patterns_are_recognized(string id, string scope, string path)
    {
        // Use a different installed id to exercise recovery by grammar scope.
        Install("custom-" + id, scope);
        Assert.Equal("custom-" + id, Catalog.ForFile(path)?.Id);
    }

    [Fact]
    public void Grammar_declared_names_patterns_and_first_line_survive_missing_manifest_metadata()
    {
        Install("custom", "source.custom", """
            { "scopeName": "source.custom", "patterns": [], "fileTypes": ["MYL", "Specialfile", "*.myl.*"],
              "filenamePatterns": ["**/configs/{one,two}.custom"], "firstLineMatch": "^#!.*custom-script" }
            """);
        var catalog = Catalog;
        foreach (var path in new[] { "a.MYL", "Specialfile", "a.myl.local", "configs/two.custom", "C:/project/configs/one.custom" })
            Assert.Equal("custom", catalog.ForFile(path)?.Id);
        Assert.Equal("custom", catalog.ForFile("extensionless", "#!/usr/bin/env custom-script")?.Id);
        Assert.NotEqual("custom", catalog.ForFile("configs/three.custom")?.Id);
        Assert.NotEqual("custom", catalog.ForFile("not-myl.local")?.Id);
        Assert.DoesNotContain("*.myl.*", catalog.ById("custom")!.Extensions);
    }

    [Fact]
    public void Publisher_shebang_hints_and_shipped_associations_are_recovered_by_scope()
    {
        Install("custom-python", "source.python");
        var catalog = Catalog;
        Assert.Equal("custom-python", catalog.ForFile("extensionless", "#!/usr/bin/env python3")?.Id);
        Assert.Equal("custom-python", catalog.ForFile("script.PY")?.Id);
        Assert.Equal("custom-python", catalog.ByName("python3")?.Id);
    }

    [Fact]
    public void Dependency_downloads_do_not_claim_file_types_and_catalog_metadata_does_not_add_languages()
    {
        Assert.Null(Catalog.ById("vue-vine"));
        Store.Add(new LanguageEntry { Id = "vue-vine", Name = "Vue Vine", ScopeName = "source.vue-vine",
            Source = GrammarSource.Downloaded, IsDependency = true }, """{ "scopeName": "source.vue-vine", "patterns": [] }""");
        Assert.Null(Catalog.ById("vue-vine"));
        Assert.NotEqual("vue-vine", Catalog.ForFile("component.vine.ts")?.Id);
    }
}
