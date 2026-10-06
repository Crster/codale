using System.Diagnostics;

using Codale.Core.Helper;
using Codale.Search;

using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;

using Microsoft.UI.Dispatching;

namespace Codale.App.ViewModels;

/// <summary>
/// One code slice of a result. A wrapper rather than binding <see cref="DiscoveredRange"/>
/// directly: records with init-only collections trip the XAML type-info generator (see
/// PreviewLine), and x:DataType cannot name a nested class.
/// </summary>
public sealed class SearchRangeItem
{
    public string Code { get; init; } = "";

    /// <summary>Line numbers down the gutter, one per code line.</summary>
    public string Gutter { get; init; } = "";

    /// <summary>Search keywords, newline separated, for KeywordHighlight.</summary>
    public string Terms { get; init; } = "";

    /// <summary>0-based indexes of the matched lines within <see cref="Code"/>, comma separated.</summary>
    public string MatchLines { get; init; } = "";

    public string FilePath { get; init; } = "";

    /// <summary>What this slice highlights in the editor: its matched lines, or the whole slice.</summary>
    public LineSpan Span { get; init; } = new(1, 1);
}

/// <summary>A result the editor should open, with the spans it should highlight - current one first.</summary>
public sealed record SearchOpenRequest(string FilePath, IReadOnlyList<LineSpan> Spans, int Current = 0);

/// <summary>One discovered file: its path and the slices that matched.</summary>
public sealed class SearchFileItem
{
    public string FilePath { get; init; } = "";

    public string RelativePath { get; init; } = "";

    /// <summary>"4 matches", or "" for a file found by its name.</summary>
    public string Detail { get; init; } = "";

    public IReadOnlyList<SearchRangeItem> Ranges { get; init; } = [];

    /// <summary>What this item was built from, so a later progress tick with the same file and terms can keep it.</summary>
    internal DiscoveredFile? Source { get; init; }

    internal string Terms { get; init; } = "";

    public SearchOpenRequest OpenRequest(SearchRangeItem? at = null)
    {
        var current = 0;
        if (at is not null)
        {
            for (var i = 0; i < Ranges.Count; i++)
            {
                if (ReferenceEquals(Ranges[i], at))
                {
                    current = i;
                    break;
                }
            }
        }

        return new(FilePath, Ranges.Select(r => r.Span).ToList(), current);
    }

    /// <summary>True when <paramref name="file"/> would build the very item this is.</summary>
    internal bool IsSameAs(DiscoveredFile file, string terms)
    {
        if (Source is not { } source || Terms != terms || source.FilePath != file.FilePath ||
            source.MatchCount != file.MatchCount || source.Ranges.Count != file.Ranges.Count)
        {
            return false;
        }

        for (var i = 0; i < source.Ranges.Count; i++)
        {
            if (source.Ranges[i].StartLine != file.Ranges[i].StartLine ||
                source.Ranges[i].EndLine != file.Ranges[i].EndLine ||
                source.Ranges[i].Code != file.Ranges[i].Code)
            {
                return false;
            }
        }

        return true;
    }

    public static SearchFileItem From(DiscoveredFile file, string terms) => new()
    {
        Source = file,
        Terms = terms,
        FilePath = file.FilePath,
        RelativePath = file.RelativePath.Replace('\\', '/'),
        // A related file is listed for what it is to the matches, not for a count.
        Detail = file.Reason.Length > 0 ? file.Reason : file.MatchCount switch
        {
            0 => "",
            1 => "1 match",
            var n => $"{n} matches",
        },
        Ranges = file.Ranges.Select(r => new SearchRangeItem
        {
            Code = r.Code,
            Gutter = string.Join("\n", Enumerable.Range(r.StartLine, r.EndLine - r.StartLine + 1)),
            Terms = terms,
            MatchLines = string.Join(",", r.MatchLines.Select(n => n - r.StartLine)),
            FilePath = file.FilePath,

            // The matched lines, not the context padding around them.
            Span = r.MatchLines.Count > 0
                ? new LineSpan(r.MatchLines.Min(), r.MatchLines.Max())
                : new LineSpan(r.StartLine, r.EndLine),
        }).ToList(),
    };
}

/// <summary>
/// Code discovery, driven from the title bar box. The query is a plain description of
/// what to find; <see cref="SourceExplorer"/> ranks the project's source index for it,
/// has the background-task model turn it into keywords and pick the files that really
/// answer it, and adds the code those files use and are used by. The result is files and
/// code only - no write-up. Provisional matches stream in first so the list fills while
/// the model is still working. Without a helper model the ranked pass runs alone, so the
/// box is never a dead end.
/// </summary>
public sealed partial class SearchViewModel : ObservableObject
{
    private readonly IHelperModel _helper;
    private readonly SourceIndex _index;
    private readonly DispatcherQueue _dispatcher = DispatcherQueue.GetForCurrentThread();

    private CancellationTokenSource? _inFlight;

    public SearchViewModel(string projectPath, IHelperModel helper)
    {
        _helper = helper;

        // The scan runs in the background from the moment the project opens, so the
        // first search is answered from memory.
        _index = SourceIndex.For(projectPath);
        _index.Warm();
    }

    public RangeObservableCollection<SearchFileItem> Files { get; } = [];

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(HasQuery))]
    public partial string Query { get; set; } = "";

    public bool HasQuery => !string.IsNullOrWhiteSpace(Query);

    [ObservableProperty]
    public partial bool IsSearching { get; set; }

    /// <summary>"Searching…" while running; "3 files · 1.8s" after.</summary>
    [ObservableProperty]
    public partial string? Status { get; set; }

    [ObservableProperty]
    public partial bool HasFiles { get; set; }

    [RelayCommand]
    private async Task RunAsync()
    {
        // A new search supersedes the old one; ripgrep and generation are both abandoned.
        await CancelInFlightAsync();

        var keywordOnly = _keywordOnly;
        _keywordOnly = false;

        var cts = new CancellationTokenSource();
        _inFlight = cts;

        Files.ReplaceAll([]);
        HasFiles = false;
        Status = null;

        if (string.IsNullOrWhiteSpace(Query))
        {
            return;
        }

        IsSearching = true;
        var clock = Stopwatch.StartNew();

        try
        {
            // Without an agent CLI there is no model, and the search stays keyword-only.
            // A references lookup searches for the text itself, so it never asks the model to reinterpret it.
            ISearchModel? model = _helper.IsAvailable && !keywordOnly ? new HelperSearchModel(_helper) : null;

            Status = "Searching…";

            var discovery = new SourceExplorer(_index, model)
            {
                // A references lookup lists every user, not the best few.
                MaxRankedResults = keywordOnly ? 40 : 12,
                MaxRelated = keywordOnly ? 0 : 4,
            };
            discovery.Progress += (_, result) => _dispatcher.TryEnqueue(() =>
            {
                if (_inFlight == cts)
                {
                    Show(result);
                    Status = result.Round == 0 ? "Searching…" : $"Refining · round {result.Round}…";
                }
            });

            var final = await discovery.RunAsync(Query, cts.Token);
            Show(final);

            var count = final.Files.Count;
            Status = (count switch
                     {
                         0 => "Nothing found",
                         1 => "1 file",
                         _ => $"{count} files",
                     }) +
                     $" · {clock.Elapsed.TotalSeconds:0.0}s" +
                     (model is null && !keywordOnly ? " · keyword search, no Claude CLI found" : "");
        }
        catch (OperationCanceledException)
        {
            Status = "Stopped.";
        }
        catch (Exception ex)
        {
            Status = $"Search failed: {ex.Message}";
        }
        finally
        {
            if (_inFlight == cts)
            {
                IsSearching = false;
                _inFlight = null;
            }

            cts.Dispose();
        }
    }

    /// <summary>Set by <see cref="FindReferences"/> for the next run only.</summary>
    private bool _keywordOnly;

    /// <summary>Searches the project for usages of <paramref name="text"/> with the keyword pass alone, no model.</summary>
    public void FindReferences(string text)
    {
        Query = text.Trim();
        _keywordOnly = true;
        if (!RunCommand.CanExecute(null))
        {
            _keywordOnly = false;
            return;
        }

        RunCommand.Execute(null);
    }

    [RelayCommand]
    private async Task StopAsync()
    {
        if (_inFlight is { } running)
        {
            await running.CancelAsync();
        }
    }

    /// <summary>
    /// Shows a result. Progress ticks arrive in bursts and mostly repeat files already
    /// listed, so those keep their items (and the list its scroll and selection); only a
    /// real change swaps the list, in one notification.
    /// </summary>
    private void Show(DiscoveryResult result)
    {
        var terms = string.Join("\n", result.Keywords);
        var current = Files.ToDictionary(item => item.FilePath, StringComparer.OrdinalIgnoreCase);

        var items = new List<SearchFileItem>(result.Files.Count);
        foreach (var file in result.Files)
        {
            items.Add(current.TryGetValue(file.FilePath, out var existing) && existing.IsSameAs(file, terms)
                ? existing
                : SearchFileItem.From(file, terms));
        }

        if (items.Count != Files.Count || !items.SequenceEqual(Files))
        {
            Files.ReplaceAll(items);
        }

        HasFiles = Files.Count > 0;
    }

    /// <summary>
    /// A search is being typed: start the model's CLI now so its start-up is over by the
    /// time the search is submitted. Cheap per keystroke; does nothing without Claude.
    /// </summary>
    public void Prewarm(bool inFile)
    {
        var model = new HelperSearchModel(_helper);
        if (inFile)
        {
            FileFocus.Prewarm(model);
        }
        else
        {
            SourceExplorer.Prewarm(_index, model);
        }
    }

    /// <summary>
    /// The helper model for a one-file search in the editor, or null when no agent CLI is
    /// installed - the editor then keeps its plain keyword highlights.
    /// </summary>
    public ISearchModel? CreateFileModel() => _helper.IsAvailable ? new HelperSearchModel(_helper) : null;

    /// <summary>Task-shaped <see cref="CreateFileModel"/> for the editor's search hook, which takes a cancellable async factory.</summary>
    public Task<ISearchModel?> ConnectModelAsync(CancellationToken ct) => Task.FromResult(CreateFileModel());

    private async Task CancelInFlightAsync()
    {
        if (_inFlight is { } existing)
        {
            await existing.CancelAsync();
            _inFlight = null;
        }
    }
}
