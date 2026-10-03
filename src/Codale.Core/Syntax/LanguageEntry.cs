using System.Text.Json.Serialization;

namespace Codale.Core.Syntax;

/// <summary>Where a grammar came from; shown in Settings and decides whether it can be removed.</summary>
public enum GrammarSource
{
    Builtin,
    Downloaded,
    Generated,
    Imported,
}

/// <summary>
/// One user-installed language in <c>languages.json</c>: how to recognise its files and
/// which grammar file under <c>grammars\</c> colours them.
/// </summary>
public sealed record LanguageEntry
{
    public string Id { get; init; } = "";
    public string Name { get; init; } = "";
    public string ScopeName { get; init; } = "";
    public string[] Extensions { get; init; } = [];
    public string[] Filenames { get; init; } = [];
    public string[] Aliases { get; init; } = [];
    public string? FirstLine { get; init; }
    public string GrammarFile { get; init; } = "";

    [JsonConverter(typeof(JsonStringEnumConverter))]
    public GrammarSource Source { get; init; } = GrammarSource.Imported;
}

/// <summary>A language the editor can colour, whether it ships with the app or was installed.</summary>
public sealed record LanguageInfo(
    string Id,
    string Name,
    string ScopeName,
    IReadOnlyList<string> Extensions,
    IReadOnlyList<string> Filenames,
    IReadOnlyList<string> Aliases,
    string? FirstLine,
    GrammarSource Source)
{
    public bool IsBuiltin => Source == GrammarSource.Builtin;
}
