using Codale.Core.Agents;
using Codale.Git;

using CommunityToolkit.Mvvm.ComponentModel;

namespace Codale.App.ViewModels;

/// <summary>
/// One file the agent changed this session, as the session panel lists it: what
/// happened to it overall, how big the change is against how the file looked before
/// the agent first touched it, and whether it moved just now.
/// </summary>
public sealed partial class SessionFileChange : ObservableObject
{
    /// <summary>Largest file the panel diffs on the UI thread; bigger ones show per-edit diffs only.</summary>
    private const long MaxDiffBytes = 2 * 1024 * 1024;

    private readonly List<FileDiff> _editDiffs = [];

    public required string Path { get; init; }

    // The name and folder split lives on ToolFileRef; built once, since the panel reads these on every row bind.
    private ToolFileRef? _fileRef;

    private ToolFileRef FileRef => _fileRef ??= new ToolFileRef { Path = Path, IsEdit = true };

    public string Name => FileRef.Name;

    public string? Directory => FileRef.ShortDirectory;

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(KindText))]
    public partial FileChangeKind Kind { get; set; }

    public string KindText => Kind switch
    {
        FileChangeKind.Create => "created",
        FileChangeKind.Delete => "deleted",
        _ => "edited",
    };

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(EditCountText))]
    public partial int EditCount { get; set; }

    public string EditCountText => EditCount > 1 ? $"{EditCount} edits" : "";

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(AddedText))]
    public partial int Added { get; set; }

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(RemovedText))]
    public partial int Removed { get; set; }

    public string AddedText => $"+{Added}";

    public string RemovedText => $"−{Removed}";

    /// <summary>True for a few seconds after the agent touched the file; the panel pulses it.</summary>
    [ObservableProperty]
    public partial bool IsFresh { get; set; }

    [ObservableProperty]
    public partial DateTimeOffset UpdatedAt { get; set; }

    /// <summary>
    /// The file as it was before the agent's first change this session - null for a
    /// file the session created. Kept across later edits: each edit's own "original"
    /// is only the previous edit's output, not the session's starting point.
    /// </summary>
    private string? _baseline;

    private bool _baselineKnown;

    /// <summary>The file's latest content as the agent last reported it, when it did.</summary>
    private string? _latest;

    /// <summary>Only small fallback contents are retained; for big ones the disk is the record.</summary>
    private const int MaxRetainedContentChars = 64 * 1024;

    private long _lastComputeTick;
    private bool _countsDirty;

    public void Record(FileChange change, FileDiff? diff)
    {
        if (EditCount == 0)
        {
            Kind = change.Kind == FileChangeKind.Unknown ? FileChangeKind.Update : change.Kind;
            _baselineKnown = change.Kind == FileChangeKind.Create || change.OriginalContent is not null;
            _baseline = change.Kind == FileChangeKind.Create ? null : change.OriginalContent;
        }
        else if (change.Kind == FileChangeKind.Delete)
        {
            Kind = FileChangeKind.Delete;
        }
        else if (Kind == FileChangeKind.Delete)
        {
            // Deleted then written again: it is a change against the original, not a creation.
            Kind = _baseline is null && _baselineKnown ? FileChangeKind.Create : FileChangeKind.Update;
        }

        _latest = change.Kind == FileChangeKind.Delete
            ? null
            : change.NewContent is { Length: <= MaxRetainedContentChars } retained ? retained : null;

        // Per-edit diffs are only the fallback when the baseline is unknown; keeping them
        // otherwise retains a diff per edit for the whole session for nothing.
        if (!_baselineKnown && diff is { Hunks.Count: > 0 })
        {
            _editDiffs.Add(diff);
        }

        EditCount++;
        UpdatedAt = DateTimeOffset.Now;

        // The read + full diff are the hottest UI-thread path of a write burst; coalesce
        // a burst to one recompute per second per file and settle the rest at turn end.
        if (Environment.TickCount64 - _lastComputeTick >= 1000)
        {
            RefreshCounts();
        }
        else
        {
            _countsDirty = true;
        }
    }

    /// <summary>Recomputes the totals if a burst coalesced them away; called at turn end.</summary>
    public void RefreshCountsIfDirty()
    {
        if (_countsDirty)
        {
            RefreshCounts();
        }
    }

    private void RefreshCounts()
    {
        _countsDirty = false;
        _lastComputeTick = Environment.TickCount64;
        var total = CumulativeDiff();
        Added = total.Added;
        Removed = total.Removed;
    }

    /// <summary>
    /// The whole session's change to this file: baseline against what is on disk now
    /// when both are known, otherwise the edits' own diffs one after another.
    /// </summary>
    public FileDiff CumulativeDiff()
    {
        if (_baselineKnown && CurrentContent() is var (exists, content) && (exists || Kind == FileChangeKind.Delete))
        {
            return TextDiff.Compute(Path, _baseline, exists ? content : null);
        }

        return TextDiff.Concat(Path, _editDiffs);
    }

    /// <summary>What the file holds now: the disk wins (it has every later edit), the last report is the fallback.</summary>
    private (bool Exists, string? Content) CurrentContent()
    {
        try
        {
            var info = new FileInfo(Path);
            if (info.Exists)
            {
                return info.Length <= MaxDiffBytes ? (true, File.ReadAllText(Path)) : (false, null);
            }

            return Kind == FileChangeKind.Delete ? (false, null) : (_latest is not null, _latest);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or ArgumentException or NotSupportedException)
        {
            return (_latest is not null, _latest);
        }
    }
}
