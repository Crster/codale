using System.Collections.ObjectModel;
using System.ComponentModel;
using System.Text;
using System.Text.Json;

using Codale.App.Services;
using Codale.Core.Agents;
using Codale.Git;

using CommunityToolkit.Mvvm.ComponentModel;

using Microsoft.UI.Xaml;

namespace Codale.App.ViewModels;

/// <summary>
/// The transcript's scaled text sizes. Every row's XAML binds FontSize through these, so
/// Settings → chat font size moves all of them together; 12.5 (the built-in base) is the
/// identity. Rows re-raise the scale when the setting changes.
/// </summary>
public static class ChatTypography
{
    public const double BaseSize = 12.5;

    public static double Scaled(double size) => Math.Round(size * AppSettings.ChatFontSize / BaseSize, 2);
}

/// <summary>
/// The scaled font sizes every transcript element binds (S10 is size 10 at the chat's
/// scale, S10_5 is 10.5). One base for rows and for the cards inside them.
/// </summary>
public abstract class ScaledObservable : ObservableObject
{
    public double S8 => ChatTypography.Scaled(8);
    public double S9 => ChatTypography.Scaled(9);
    public double S10 => ChatTypography.Scaled(10);
    public double S10_5 => ChatTypography.Scaled(10.5);
    public double S11 => ChatTypography.Scaled(11);
    public double S11_5 => ChatTypography.Scaled(11.5);
    public double S12 => ChatTypography.Scaled(12);
    public double S12_5 => ChatTypography.Scaled(12.5);
    public double S13 => ChatTypography.Scaled(13);
    public double S13_5 => ChatTypography.Scaled(13.5);
    public double S14 => ChatTypography.Scaled(14);

    /// <summary>Re-announces every scaled size, so live x:Bind rows re-measure.</summary>
    public void RaiseTextScale()
    {
        OnPropertyChanged(nameof(S8));
        OnPropertyChanged(nameof(S9));
        OnPropertyChanged(nameof(S10));
        OnPropertyChanged(nameof(S10_5));
        OnPropertyChanged(nameof(S11));
        OnPropertyChanged(nameof(S11_5));
        OnPropertyChanged(nameof(S12));
        OnPropertyChanged(nameof(S12_5));
        OnPropertyChanged(nameof(S13));
        OnPropertyChanged(nameof(S13_5));
        OnPropertyChanged(nameof(S14));
    }
}

/// <summary>
/// Which visual block a transcript row reads as part of. Rows of one block sit close
/// together - the turn's tool run, the prose answer - and a boundary between blocks
/// gets room to breathe; ChatViewModel.Reflow turns this into each row's RowMargin
/// from what row sits above it.
/// </summary>
public enum TranscriptBlock
{
    User,
    Tool,
    Prose,
    Notice,
}

/// <summary>One row in the transcript.</summary>
public abstract partial class ChatItem : ScaledObservable
{
    public DateTimeOffset Timestamp { get; init; } = DateTimeOffset.Now;

    /// <summary>The block this row belongs to, for neighbor-aware spacing.</summary>
    public abstract TranscriptBlock Block { get; }

    /// <summary>The row's fixed side insets; only the top gap is neighbor-aware.</summary>
    public virtual Thickness RowInset => new(0, 0, 40, 0);

    private Thickness? _rowMargin;

    /// <summary>
    /// The row's full transcript margin, set by ChatViewModel.Reflow from the row
    /// above. Null until then: the templates get the kind's natural fallback, so a
    /// row never renders without its side insets even if a reflow is missed.
    /// </summary>
    public Thickness RowMargin => _rowMargin ?? NaturalMargin;

    /// <summary>The margin the row keeps on its own, as in the template's old static value.</summary>
    protected virtual Thickness NaturalMargin => new(0, 4, 40, 6);

    /// <summary>Applies the neighbor-aware margin; the templates bind this one way.</summary>
    public void SetRowMargin(Thickness margin)
    {
        if (_rowMargin is not { } current || !current.Equals(margin))
        {
            _rowMargin = margin;
            OnPropertyChanged(nameof(RowMargin));
        }
    }
}

public sealed partial class UserMessageItem : ChatItem
{
    public required string Text { get; init; }

    public override TranscriptBlock Block => TranscriptBlock.User;

    public override Thickness RowInset => new(48, 0, 0, 0);

    protected override Thickness NaturalMargin => new(48, 14, 0, 4);

    /// <summary>Files attached to this turn, shown as chips above the text.</summary>
    public IReadOnlyList<TurnAttachment> Attachments { get; init; } = [];

    /// <summary>Sent with Ctrl+Enter: planned rather than executed.</summary>
    public bool SentAsPlan { get; init; }

    /// <summary>Sent while a turn was running: it joins that turn mid-way rather than starting one.</summary>
    public bool IsSteer { get; init; }

    public bool HasText => Text.Length > 0;

    public bool HasAttachments => Attachments.Count > 0;
}

/// <summary>A file waiting in the composer to go out with the next turn.</summary>
public sealed partial class ComposerAttachment
{
    public required TurnAttachmentKind Kind { get; init; }

    public required string Name { get; init; }

    public required string Path { get; init; }

    public bool IsImage => Kind == TurnAttachmentKind.Image;

    public bool IsPdf => Kind == TurnAttachmentKind.Pdf;

    public TurnAttachment ToTurnAttachment() => new() { Kind = Kind, Name = Name, Path = Path };
}

/// <summary>One row of the composer's autocomplete popup: a slash command or a workspace file.</summary>
public sealed partial class SuggestionItem
{
    /// <summary>What the row shows, e.g. "/compact" or "src/Views/ChatTab.xaml".</summary>
    public required string Title { get; init; }

    /// <summary>The qualifier - "command" or the file's folder - so look-alikes are tellable apart.</summary>
    public required string Detail { get; init; }

    /// <summary>Text dropped into the draft when the row is accepted.</summary>
    public required string Insert { get; init; }

    /// <summary>Commands complete and send in one Enter; files only complete.</summary>
    public bool IsCommand { get; init; }
}

/// <summary>
/// Assistant prose. Inside a running turn it is a narration step in the timeline
/// ("Let me look at the parser first"); when the turn lands, the last one is lifted
/// out below the folded timeline as the turn's answer - same object, new place.
/// </summary>
public sealed partial class AssistantMessageItem : ChatItem
{
    public override TranscriptBlock Block => TranscriptBlock.Prose;

    /// <summary>Grows as deltas arrive, then is replaced by the authoritative message.</summary>
    [ObservableProperty]
    public partial string Text { get; set; } = "";

    [ObservableProperty]
    public partial bool IsStreaming { get; set; } = true;

    [ObservableProperty]
    public partial bool HasThinking { get; set; }

    // Authoritative while streaming; a snapshot is published from it at a coarse cadence.
    private readonly StringBuilder _streamed = new();
    private long _lastStreamPublish;

    public void Append(string delta)
    {
        _streamed.Append(delta);

        // Publishing every delta re-copies the whole message each time - O(n²) over a
        // long reply - while MarkdownView debounces its rebuilds anyway. ~80ms keeps
        // the stream visually live for a fraction of the churn.
        var now = Environment.TickCount64;
        if (now - _lastStreamPublish >= 80)
        {
            _lastStreamPublish = now;
            Text = _streamed.ToString();
        }
    }

    /// <summary>Flushes coalesced-but-unpublished deltas; used when a turn is cut short.</summary>
    public void PublishPending()
    {
        if (_streamed.Length > 0)
        {
            Text = _streamed.ToString();
            _streamed.Clear();
        }
    }

    /// <summary>The authoritative final text replaces whatever streamed in.</summary>
    public void Complete(string finalText)
    {
        _streamed.Clear();
        Text = finalText;
        IsStreaming = false;
    }
}

/// <summary>How a user turn ended; the running state is the turn's default.</summary>
public enum TurnOutcome
{
    Running,
    Completed,
    Stopped,
    Failed,
    NotSent,
}

/// <summary>
/// What a tool call is, for colour and wording: the transcript is scanned by hue -
/// blue reads, amber edits, violet shell lines - long before anyone reads the text.
/// </summary>
public enum ToolKind
{
    Read,
    Search,
    Edit,
    Write,
    Shell,
    Web,
    Agent,
    Ask,
    Plan,
    Todo,
    Other,
}

public static class ToolKinds
{
    public static ToolKind Of(string toolName) => toolName switch
    {
        "Read" or "NotebookRead" => ToolKind.Read,
        "Grep" or "Glob" or "LS" => ToolKind.Search,
        "Edit" or "MultiEdit" or "NotebookEdit" => ToolKind.Edit,
        "Write" => ToolKind.Write,
        "Bash" or "PowerShell" or "BashOutput" or "KillShell" => ToolKind.Shell,
        "WebFetch" or "WebSearch" => ToolKind.Web,
        "Task" or "Agent" => ToolKind.Agent,
        "AskUserQuestion" => ToolKind.Ask,
        "ExitPlanMode" => ToolKind.Plan,
        _ when TodoParser.IsTodosTool(toolName) => ToolKind.Todo,
        _ => ToolKind.Other,
    };

    /// <summary>Look-around calls: consecutive runs of these fold into one "Explored" step.</summary>
    public static bool IsExplore(ToolKind kind) => kind is ToolKind.Read or ToolKind.Search or ToolKind.Web;

    /// <summary>The task-list tools: the session panel shows the list, the timeline one quiet row.</summary>
    public static bool IsTodo(string toolName) => Of(toolName) == ToolKind.Todo;

    /// <summary>
    /// The CLI's own machinery rather than work: ToolSearch loads deferred tool
    /// schemas ("select:TaskCreate,..."), and a report_intent narration tool narrates what it
    /// is about to do - neither says anything the timeline does not already show.
    /// </summary>
    public static bool IsPlumbing(string toolName) => toolName is "ToolSearch" or "ReportIntent";

    /// <summary>Units spelled out and trimmed to what matters: 4s, 1m 23s, 1h 04m.</summary>
    public static string FormatElapsed(TimeSpan elapsed) => elapsed.TotalSeconds switch
    {
        < 60 => $"{(int)elapsed.TotalSeconds}s",
        < 3600 => $"{(int)elapsed.TotalMinutes}m {elapsed.Seconds:00}s",
        _ => $"{(int)elapsed.TotalHours}h {elapsed.Minutes:00}m",
    };
}

/// <summary>
/// One user turn's work, as a timeline under the prompt. While the agent works it is
/// open and grows step by step - narration, tool calls, explore groups - with the
/// clock running in its header. When the turn lands the answer is lifted out below
/// it, and the timeline folds into one summary line: how long, and coloured pills for
/// what ran ("3 edits +42 −7", "2 commands"). The header reopens it.
/// </summary>
public sealed partial class TurnActivityItem : ChatItem
{
    public override TranscriptBlock Block => TranscriptBlock.Tool;

    public TurnActivityItem()
    {
        Steps.CollectionChanged += (_, _) =>
        {
            OnPropertyChanged(nameof(HasSteps));
            OnPropertyChanged(nameof(HeaderText));
        };
    }

    /// <summary>Narration (<see cref="AssistantMessageItem"/>), tool calls, explore groups and the todo row.</summary>
    public ObservableCollection<ChatItem> Steps { get; } = [];

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(IsRunning))]
    [NotifyPropertyChangedFor(nameof(IsFinished))]
    [NotifyPropertyChangedFor(nameof(HeaderText))]
    [NotifyPropertyChangedFor(nameof(StatusGlyph))]
    public partial TurnOutcome Outcome { get; set; } = TurnOutcome.Running;

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(HeaderText))]
    public partial TimeSpan Elapsed { get; set; }

    /// <summary>
    /// False when no trustworthy clock exists (a replayed transcript without event
    /// timestamps): the header then omits the duration rather than lie with 0s.
    /// </summary>
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(HeaderText))]
    public partial bool HasClock { get; set; }

    /// <summary>Open while the turn runs; folded to the summary line once it lands.</summary>
    [ObservableProperty]
    public partial bool IsExpanded { get; set; } = true;

    [ObservableProperty]
    public partial IReadOnlyList<ActivityPill> Pills { get; set; } = [];

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(HasProblems))]
    public partial string ProblemText { get; set; } = "";

    [ObservableProperty]
    public partial ToolCallStatus OverallStatus { get; set; } = ToolCallStatus.Succeeded;

    public bool HasProblems => ProblemText.Length > 0;

    public bool IsRunning => Outcome == TurnOutcome.Running;

    public bool IsFinished => !IsRunning;

    public bool HasSteps => Steps.Count > 0;

    public string StatusGlyph => Outcome switch
    {
        TurnOutcome.Completed => "",                          // check mark
        TurnOutcome.Failed or TurnOutcome.NotSent => "",      // error
        _ => "",                                              // stop block
    };

    public string HeaderText
    {
        get
        {
            var time = HasClock ? ToolKinds.FormatElapsed(Elapsed) : null;

            return Outcome switch
            {
                TurnOutcome.Running => time is null ? "Working" : $"Working · {time}",
                TurnOutcome.NotSent => "Not sent",
                TurnOutcome.Completed when !HasSteps => time is null ? "Replied" : $"Replied in {time}",
                TurnOutcome.Completed => time is null ? "Worked" : $"Worked for {time}",
                TurnOutcome.Stopped => time is null ? "Stopped" : $"Stopped after {time}",
                _ => time is null ? "Failed" : $"Failed after {time}",
            };
        }
    }

    /// <summary>Every tool call in the timeline, explore groups opened up.</summary>
    public IEnumerable<ToolCallItem> AllTools => Steps.SelectMany(step => step switch
    {
        ToolCallItem tool => [tool],
        ExploreGroupStep group => group.Tools,
        _ => Enumerable.Empty<ToolCallItem>(),
    });

    /// <summary>Settles the turn: outcome, summary pills and problem tail, then folds.</summary>
    public void Finish(TurnOutcome outcome)
    {
        var tools = AllTools.ToList();

        Pills = ActivityPill.Summarize(tools);

        var failed = tools.Count(t => t.Status == ToolCallStatus.Failed);
        var denied = tools.Count(t => t.Status == ToolCallStatus.Denied);
        var parts = new List<string>();
        if (failed > 0)
        {
            parts.Add($"{failed} failed");
        }

        if (denied > 0)
        {
            parts.Add($"{denied} denied");
        }

        ProblemText = string.Join(" · ", parts);
        OverallStatus = failed > 0 ? ToolCallStatus.Failed : denied > 0 ? ToolCallStatus.Denied : ToolCallStatus.Succeeded;

        Outcome = outcome;
        IsExpanded = false;
    }
}

/// <summary>One coloured chip on a folded turn's summary line: "3 edits +42 −7".</summary>
public sealed class ActivityPill
{
    public required ToolKind Kind { get; init; }

    public required string Label { get; init; }

    public int Added { get; init; }

    public int Removed { get; init; }

    public string AddedText => Added > 0 ? $" +{Added}" : "";

    public string RemovedText => Removed > 0 ? $" −{Removed}" : "";

    /// <summary>
    /// What a turn did, by kind, in the order a reader cares about: changes first,
    /// then commands, then the looking-around. Task-list bookkeeping is left out.
    /// </summary>
    public static IReadOnlyList<ActivityPill> Summarize(IReadOnlyCollection<ToolCallItem> tools)
    {
        var pills = new List<ActivityPill>();

        void Add(ToolKind kind, string singular, string plural, IEnumerable<ToolCallItem> calls, bool withStat = false)
        {
            var list = calls.ToList();
            if (list.Count == 0)
            {
                return;
            }

            pills.Add(new ActivityPill
            {
                Kind = kind,
                Label = $"{list.Count} {(list.Count == 1 ? singular : plural)}",
                Added = withStat ? list.Sum(t => t.Diff?.Added ?? 0) : 0,
                Removed = withStat ? list.Sum(t => t.Diff?.Removed ?? 0) : 0,
            });
        }

        var succeeded = tools.Where(t => t.Status != ToolCallStatus.Denied).ToList();

        Add(ToolKind.Edit, "edit", "edits", succeeded.Where(t => t.Kind == ToolKind.Edit), withStat: true);
        Add(ToolKind.Write, "file written", "files written", succeeded.Where(t => t.Kind == ToolKind.Write), withStat: true);
        Add(ToolKind.Shell, "command", "commands", succeeded.Where(t => t.Kind == ToolKind.Shell));
        Add(ToolKind.Agent, "agent", "agents", succeeded.Where(t => t.Kind == ToolKind.Agent));
        Add(ToolKind.Read, "file read", "files read", succeeded.Where(t => t.Kind == ToolKind.Read));
        Add(ToolKind.Search, "search", "searches", succeeded.Where(t => t.Kind == ToolKind.Search));
        Add(ToolKind.Web, "web lookup", "web lookups", succeeded.Where(t => t.Kind == ToolKind.Web));
        Add(ToolKind.Ask, "question", "questions", succeeded.Where(t => t.Kind == ToolKind.Ask));
        Add(ToolKind.Plan, "plan", "plans", succeeded.Where(t => t.Kind == ToolKind.Plan));
        Add(ToolKind.Other, "tool", "tools", succeeded.Where(t => t.Kind == ToolKind.Other));

        return pills;
    }
}

/// <summary>
/// A run of consecutive look-around calls - reads, searches, fetches - folded into one
/// timeline step: "Explored · 4 files, 2 searches". Reading around is how agents work,
/// and one row per read was most of the clutter; the row opens to the calls themselves.
/// </summary>
public sealed partial class ExploreGroupStep : ChatItem
{
    public override TranscriptBlock Block => TranscriptBlock.Tool;

    public ObservableCollection<ToolCallItem> Tools { get; } = [];

    [ObservableProperty]
    public partial bool IsExpanded { get; set; }

    public void Add(ToolCallItem tool)
    {
        tool.Group = this;
        Tools.Add(tool);
        tool.PropertyChanged += OnToolChanged;
        Refresh();
    }

    private void OnToolChanged(object? sender, PropertyChangedEventArgs e)
    {
        if (e.PropertyName is nameof(ToolCallItem.Status) or nameof(ToolCallItem.Duration))
        {
            Refresh();
        }
    }

    // The readings below are computed in one pass over the tools and announced only when
    // they moved: a child's status change used to raise seven notifications off five LINQ
    // passes, per tool, per change.
    private bool _isWorking;
    private ToolKind _kind = ToolKind.Read;
    private string _title = "";
    private string _problemText = "";
    private string _durationText = "";

    private void Refresh()
    {
        var working = false;
        var allWeb = true;
        var allSearch = true;
        var failed = 0;
        var searches = 0;
        var pages = 0;
        var readFiles = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        var first = DateTimeOffset.MaxValue;
        var last = DateTimeOffset.MinValue;

        foreach (var tool in Tools)
        {
            var kind = tool.Kind;
            working |= tool.IsWorking;
            allWeb &= kind == ToolKind.Web;
            allSearch &= kind == ToolKind.Search;

            if (tool.Status is ToolCallStatus.Failed or ToolCallStatus.Denied)
            {
                failed++;
            }

            switch (kind)
            {
                case ToolKind.Read:
                    readFiles.Add(tool.Files.FirstOrDefault()?.Path ?? tool.Summary);
                    break;
                case ToolKind.Search:
                    searches++;
                    break;
                case ToolKind.Web:
                    pages++;
                    break;
            }

            first = tool.Timestamp < first ? tool.Timestamp : first;
            var end = tool.Timestamp + (tool.Duration ?? TimeSpan.Zero);
            last = end > last ? end : last;
        }

        var parts = new List<string>();
        if (readFiles.Count > 0)
        {
            parts.Add($"{readFiles.Count} {(readFiles.Count == 1 ? "file" : "files")}");
        }

        if (searches > 0)
        {
            parts.Add($"{searches} {(searches == 1 ? "search" : "searches")}");
        }

        if (pages > 0)
        {
            parts.Add($"{pages} {(pages == 1 ? "page" : "pages")}");
        }

        if (SetProperty(ref _isWorking, working, nameof(IsWorking)))
        {
            OnPropertyChanged(nameof(Verb));
        }

        SetProperty(ref _kind, allWeb ? ToolKind.Web : allSearch ? ToolKind.Search : ToolKind.Read, nameof(Kind));
        SetProperty(ref _title, string.Join(", ", parts), nameof(Title));

        if (SetProperty(ref _problemText, failed > 0 ? $"{failed} failed" : "", nameof(ProblemText)))
        {
            OnPropertyChanged(nameof(HasProblems));
        }

        SetProperty(
            ref _durationText,
            working || Tools.Count == 0 ? "" : ToolCallItem.FormatDuration(last - first),
            nameof(DurationText));
    }

    public bool IsWorking => _isWorking;

    public string Verb => _isWorking ? "Exploring" : "Explored";

    /// <summary>The group takes the colour of what it is mostly made of.</summary>
    public ToolKind Kind => _kind;

    public string Title => _title;

    public string ProblemText => _problemText;

    public bool HasProblems => _problemText.Length > 0;

    public string DurationText => _durationText;
}

/// <summary>
/// The turn's task-list bookkeeping as one quiet row, updated in place: the full list
/// lives in the session panel, so the timeline only notes that it moved.
/// </summary>
public sealed partial class TodoStepItem : ChatItem
{
    public override TranscriptBlock Block => TranscriptBlock.Tool;

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(ProgressText))]
    public partial int Done { get; set; }

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(ProgressText))]
    public partial int Total { get; set; }

    /// <summary>What the agent says it is on right now, if anything.</summary>
    [ObservableProperty]
    public partial string? ActiveTask { get; set; }

    public string ProgressText => Total == 0 ? "cleared" : $"{Done}/{Total} done";
}

public enum ToolCallStatus
{
    Running,
    AwaitingApproval,
    Succeeded,
    Failed,
    Denied,
}

/// <summary>
/// A workspace file a tool call touched, as shown in the transcript header. Clicking
/// opens the diff (edits) or the file itself (reads).
/// </summary>
public sealed partial class ToolFileRef
{
    /// <summary>The path as the tool reported it - absolute or repository-relative.</summary>
    public required string Path { get; init; }

    /// <summary>True when the call changed the file and a diff is the right view.</summary>
    public required bool IsEdit { get; init; }

    public string Name => System.IO.Path.GetFileName(Path.Replace('\\', '/'));

    public string? Directory
    {
        get
        {
            var normalized = Path.Replace('\\', '/');
            var directory = System.IO.Path.GetDirectoryName(normalized);
            return string.IsNullOrEmpty(directory) ? null : directory.Replace('\\', '/');
        }
    }

    /// <summary>The last two folders only - enough to tell two Program.cs apart without a full path.</summary>
    public string? ShortDirectory
    {
        get
        {
            if (Directory is not { } directory)
            {
                return null;
            }

            var parts = directory.Split('/', StringSplitOptions.RemoveEmptyEntries);
            return parts.Length <= 2 ? directory : "…/" + string.Join("/", parts[^2..]);
        }
    }
}

/// <summary>
/// A tool call asking the user for permission, rendered inline in the timeline under
/// the call it belongs to - the agent's turn is blocked until it is answered, which
/// the disappearing card makes visible.
/// </summary>
public sealed partial class ApprovalRequestItem : ObservableObject
{
    public required string RequestId { get; init; }

    /// <summary>The tool the question is about, for the "Allow Bash to run?" line.</summary>
    public string ToolName { get; init; } = "";

    /// <summary>The tool input as it arrived; the raw material for an updatedInput answer.</summary>
    public JsonElement Input { get; init; }

    /// <summary>
    /// Parsed questions when the call is AskUserQuestion - the one tool where
    /// "allow" means nothing without the user's selections, which the CLI only
    /// accepts inside <c>updatedInput.answers</c>.
    /// </summary>
    public IReadOnlyList<ApprovalQuestion> Questions
    {
        get => _questions;
        init
        {
            _questions = value;
            foreach (var question in value)
            {
                question.PropertyChanged += (_, e) =>
                {
                    if (e.PropertyName == nameof(ApprovalQuestion.IsAnswered))
                    {
                        OnPropertyChanged(nameof(CanSubmit));
                        OnPropertyChanged(nameof(CanAdvance));
                    }
                };
            }
        }
    }

    private readonly IReadOnlyList<ApprovalQuestion> _questions = [];

    public bool IsAskUserQuestion => Questions.Count > 0;

    public bool IsGeneric => !IsAskUserQuestion && !IsPlan;

    /// <summary>The permission line: "Allow Bash to run?", "Allow Edit?".</summary>
    public string QuestionHeader => ToolKinds.Of(ToolName) switch
    {
        ToolKind.Shell => $"Allow {ToolName} to run this command?",
        ToolKind.Edit => "Allow this edit?",
        ToolKind.Write => "Allow writing this file?",
        ToolKind.Web => $"Allow {ToolName}?",
        _ => ToolName.Length > 0 ? $"Allow {ToolName}?" : "Allow this?",
    };

    /// <summary>
    /// True for an ExitPlanMode approval: the card offers the plan flow instead of the
    /// generic allow/deny pair - accept into a work mode, or send the plan back.
    /// </summary>
    public bool IsPlan { get; init; }

    /// <summary>The reader's revision feedback, sent back to the model on "Revise plan".</summary>
    [ObservableProperty]
    public partial string RevisionFeedback { get; set; } = "";

    /// <summary>"Revise…" was pressed: the feedback box is open and the send button shows.</summary>
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(IsNotRevising))]
    public partial bool IsRevising { get; set; }

    public bool IsNotRevising => !IsRevising;

    /// <summary>One button per CLI-proposed shortcut, e.g. "Allow all edits this session".</summary>
    public IReadOnlyList<SuggestionOption> Suggestions { get; init; } = [];

    // The question stepper: one question on screen at a time, dots for the rest.

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(CurrentQuestion))]
    [NotifyPropertyChangedFor(nameof(StepText))]
    [NotifyPropertyChangedFor(nameof(IsFirst))]
    [NotifyPropertyChangedFor(nameof(IsLast))]
    [NotifyPropertyChangedFor(nameof(IsNotLast))]
    [NotifyPropertyChangedFor(nameof(ShowBack))]
    [NotifyPropertyChangedFor(nameof(CanAdvance))]
    [NotifyPropertyChangedFor(nameof(Dots))]
    public partial int CurrentIndex { get; set; }

    public bool ShowBack => HasManyQuestions && !IsFirst;

    public ApprovalQuestion? CurrentQuestion => Questions.Count > 0 ? Questions[Math.Clamp(CurrentIndex, 0, Questions.Count - 1)] : null;

    public bool HasManyQuestions => Questions.Count > 1;

    public string StepText => $"Question {CurrentIndex + 1} of {Questions.Count}";

    public bool IsFirst => CurrentIndex == 0;

    public bool IsLast => CurrentIndex >= Questions.Count - 1;

    public bool IsNotLast => !IsLast;

    /// <summary>"Next" is live once the question on screen has an answer.</summary>
    public bool CanAdvance => CurrentQuestion?.IsAnswered == true;

    /// <summary>Submit needs every question answered - the CLI treats a gap as "did not answer".</summary>
    public bool CanSubmit => Questions.Count > 0 && Questions.All(q => q.IsAnswered);

    public void Next()
    {
        if (!IsLast)
        {
            CurrentIndex++;
        }
    }

    public void Back()
    {
        if (!IsFirst)
        {
            CurrentIndex--;
        }
    }

    /// <summary>Progress dots: one per question, the current one lit.</summary>
    public IReadOnlyList<QuestionDot> Dots => Questions.Select((_, i) => new QuestionDot(i == CurrentIndex)).ToList();

    /// <summary>
    /// Reads the questions array out of an AskUserQuestion payload; empty for any
    /// other tool or a shape the CLI might change under us.
    /// </summary>
    public static IReadOnlyList<ApprovalQuestion> ParseQuestions(JsonElement input)
    {
        var questions = new List<ApprovalQuestion>();

        if (input.ValueKind != JsonValueKind.Object ||
            !input.TryGetProperty("questions", out var array) ||
            array.ValueKind != JsonValueKind.Array)
        {
            return questions;
        }

        foreach (var entry in array.EnumerateArray())
        {
            if (entry.ValueKind != JsonValueKind.Object ||
                entry.TryGetProperty("question", out var text) is false ||
                text.ValueKind != JsonValueKind.String)
            {
                continue;
            }

            var question = new ApprovalQuestion
            {
                Text = text.GetString() ?? "",
                Header = entry.TryGetProperty("header", out var header) && header.ValueKind == JsonValueKind.String
                    ? header.GetString()
                    : null,
                MultiSelect = entry.TryGetProperty("multiSelect", out var multi) && multi.ValueKind == JsonValueKind.True,
                Options = [.. ParseOptions(entry)],
            };

            var index = 1;
            foreach (var option in question.Options)
            {
                option.Owner = question;
                option.Index = index++;
            }

            questions.Add(question);
        }

        return questions;
    }

    private static IEnumerable<ApprovalQuestionOption> ParseOptions(JsonElement entry)
    {
        if (!entry.TryGetProperty("options", out var array) || array.ValueKind != JsonValueKind.Array)
        {
            yield break;
        }

        foreach (var option in array.EnumerateArray())
        {
            if (option.ValueKind == JsonValueKind.Object &&
                option.TryGetProperty("label", out var label) &&
                label.ValueKind == JsonValueKind.String)
            {
                yield return new ApprovalQuestionOption
                {
                    Label = label.GetString() ?? "",
                    Description = option.TryGetProperty("description", out var description) &&
                                  description.ValueKind == JsonValueKind.String
                        ? description.GetString()
                        : null,
                };
            }
        }
    }
}

/// <summary>One progress dot on a multi-question card.</summary>
public sealed record QuestionDot(bool IsCurrent);

/// <summary>One question inside an AskUserQuestion card.</summary>
public sealed partial class ApprovalQuestion : ScaledObservable
{
    // Question cards are transcript content, so they ride the chat font scale too.
    public required string Text { get; init; }

    /// <summary>The CLI's short chip label for the question, e.g. "Auth method".</summary>
    public string? Header { get; init; }

    public bool HasHeader => Header is { Length: > 0 };

    public required bool MultiSelect { get; init; }

    public IReadOnlyList<ApprovalQuestionOption> Options { get; init; } = [];

    public string SelectHint => MultiSelect ? "Pick any that apply" : "Pick one";

    /// <summary>
    /// A free-text answer typed into the "Other" row. On a single-select question it
    /// replaces any picked option; on a multi-select one it joins them.
    /// </summary>
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(IsAnswered))]
    [NotifyPropertyChangedFor(nameof(HasOtherText))]
    public partial string OtherText { get; set; } = "";

    public bool HasOtherText => OtherText.Trim().Length > 0;

    partial void OnOtherTextChanged(string value)
    {
        if (!MultiSelect && value.Trim().Length > 0)
        {
            foreach (var option in Options)
            {
                option.IsSelected = false;
            }
        }
    }

    public bool IsAnswered => Options.Any(o => o.IsSelected) || HasOtherText;

    /// <summary>Everything the reader chose, the typed answer last.</summary>
    public IReadOnlyList<string> Answers =>
        Options.Where(o => o.IsSelected).Select(o => o.Label)
            .Concat(HasOtherText ? new[] { OtherText.Trim() } : Array.Empty<string>())
            .ToList();

    /// <summary>Single-select questions clear their siblings (and the typed answer); multi ones toggle freely.</summary>
    public void Toggle(ApprovalQuestionOption option)
    {
        if (!option.IsSelected)
        {
            if (!MultiSelect)
            {
                foreach (var other in Options)
                {
                    other.IsSelected = false;
                }

                OtherText = "";
            }

            option.IsSelected = true;
        }
        else
        {
            option.IsSelected = false;
        }

        OnPropertyChanged(nameof(IsAnswered));
    }
}

/// <summary>One selectable answer on an AskUserQuestion card.</summary>
public sealed partial class ApprovalQuestionOption : ScaledObservable
{
    // Question cards are transcript content, so they ride the chat font scale too.
    public ApprovalQuestion? Owner { get; set; }

    public required string Label { get; init; }

    public string? Description { get; init; }

    public bool HasDescription => Description is { Length: > 0 };

    /// <summary>1-based position: the number key that picks it.</summary>
    public int Index { get; set; }

    public string KeyText => Index is > 0 and < 10 ? Index.ToString() : "";

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(Glyph))]
    public partial bool IsSelected { get; set; }

    /// <summary>Radio for pick-one, checkbox for pick-many; filled once chosen.</summary>
    public string Glyph => (Owner?.MultiSelect ?? false)
        ? IsSelected ? "" : ""
        : IsSelected ? "" : "";
}

/// <summary>A suggestion button on an approval card: which request it answers, and what it accepts.</summary>
public sealed partial class SuggestionOption
{
    public required string RequestId { get; init; }

    public required PermissionSuggestion Suggestion { get; init; }

    public string Label => Suggestion.ToDisplayLabel();
}

/// <summary>
/// One tool call as a timeline step: a tinted icon disc in the kind's colour, a verb
/// ("Edited", "Ran"), the target (a file, a command, a pattern) and - once it lands -
/// a diff stat and duration. The body opens for the detail that matters for its kind:
/// the diff for edits, command and output for shell lines, the question for asks.
/// </summary>
public sealed partial class ToolCallItem : ChatItem
{
    public override TranscriptBlock Block => TranscriptBlock.Tool;

    public required string ToolUseId { get; init; }
    public required string ToolName { get; init; }

    /// <summary>The call's input payload, kept for diffs and answers; released when the call finishes.</summary>
    public JsonElement Input { get; internal set; }

    /// <summary>
    /// Drops the raw input payload once the call is done. Write/Edit inputs embed whole
    /// file contents, and a long session would otherwise retain every one of them while
    /// the finished diff (and the file itself) tells the same story.
    /// </summary>
    public void ReleaseInput() => Input = default;

    public ToolKind Kind => ToolKinds.Of(ToolName);

    /// <summary>The explore group this call was folded into, if any.</summary>
    public ExploreGroupStep? Group { get; set; }

    /// <summary>True for the tools that run a shell line; their preview is code, not prose.</summary>
    public bool IsShellTool => IsShellName(ToolName);

    /// <summary>The shell line this call runs, verbatim - it may be a multi-line script.</summary>
    public string Command { get; init; } = "";

    /// <summary>
    /// Command for the detail body, on its own wrapping block. Null when this is not
    /// a shell call or the payload carried no command. The cap is a sanity bound.
    /// </summary>
    public string? ShellCommand
    {
        get
        {
            if (!IsShellTool || Command.Length == 0)
            {
                return null;
            }

            var trimmed = Command.Trim();
            return trimmed.Length > 4000 ? trimmed[..4000] + "…" : trimmed;
        }
    }

    /// <summary>
    /// The plan an ExitPlanMode call submits for approval, shown in full in the body -
    /// in plan mode this text is the turn's whole deliverable. Null for every other tool.
    /// </summary>
    public string? PlanText { get; init; }

    /// <summary>True for the plan card: its body is the point of the turn, not scaffolding.</summary>
    public bool IsPlan => PlanText is { Length: > 0 };

    /// <summary>Files the call reads or writes; the row renders the first as a clickable target.</summary>
    public IReadOnlyList<ToolFileRef> Files { get; init; } = [];

    public bool HasFiles => Files.Count > 0;

    /// <summary>Columns the row's toggle spans: all the way to the meta text unless a file chip sits beside it.</summary>
    public int ToggleSpan => HasFiles ? 1 : 3;

    public ToolFileRef? PrimaryFile => Files.Count > 0 ? Files[0] : null;

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(Target))]
    [NotifyPropertyChangedFor(nameof(CodeTarget))]
    [NotifyPropertyChangedFor(nameof(ProseTarget))]
    [NotifyPropertyChangedFor(nameof(CodeTargetVisible))]
    [NotifyPropertyChangedFor(nameof(ProseTargetVisible))]
    public partial string Summary { get; set; } = "";

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(IsWorking))]
    [NotifyPropertyChangedFor(nameof(IsFinished))]
    [NotifyPropertyChangedFor(nameof(Verb))]
    [NotifyPropertyChangedFor(nameof(StatusLabel))]
    [NotifyPropertyChangedFor(nameof(HasStatusLabel))]
    [NotifyPropertyChangedFor(nameof(MetaStatus))]
    [NotifyPropertyChangedFor(nameof(MetaDuration))]
    [NotifyPropertyChangedFor(nameof(ShowOutput))]
    public partial ToolCallStatus Status { get; set; } = ToolCallStatus.Running;

    /// <summary>True while the call still needs attention: running, or awaiting approval.</summary>
    public bool IsWorking => Status is ToolCallStatus.Running or ToolCallStatus.AwaitingApproval;

    public bool IsFinished => Status is ToolCallStatus.Succeeded or ToolCallStatus.Failed or ToolCallStatus.Denied;

    /// <summary>Only the outcomes worth a word get one: success is the quiet default.</summary>
    public string StatusLabel => Status switch
    {
        ToolCallStatus.Failed => "failed",
        ToolCallStatus.Denied => "denied",
        ToolCallStatus.AwaitingApproval => "needs you",
        _ => "",
    };

    public bool HasStatusLabel => StatusLabel.Length > 0;

    /// <summary>
    /// The row's right side as runs of one TextBlock, so the small mono stat and prose
    /// status/duration share a baseline instead of each being centred in its own line
    /// box (their font metrics differ by about a pixel). Each carries its own leading
    /// en-space when something precedes it, and collapses to "" when absent - the runs
    /// replace the old per-item Visibility toggles.
    /// </summary>
    public string MetaStatus => HasDiff ? "\u2002" + StatusLabel : StatusLabel;

    public string MetaDuration => HasDiff || HasStatusLabel ? "\u2002" + DurationText : DurationText;

    /// <summary>The step's verb, tensed by state: "Editing" live, "Edited" done, "Edit" when it failed.</summary>
    public string Verb
    {
        get
        {
            var (present, past, imperative) = ToolName switch
            {
                "Read" or "NotebookRead" => ("Reading", "Read", "Read"),
                "Grep" => ("Searching", "Searched", "Search"),
                "Glob" => ("Finding files", "Found files", "Find files"),
                "LS" => ("Listing", "Listed", "List"),
                "Edit" or "MultiEdit" or "NotebookEdit" => ("Editing", "Edited", "Edit"),
                "Write" => ("Writing", Diff?.IsNew == true ? "Created" : "Wrote", "Write"),
                "Bash" or "PowerShell" => ("Running", "Ran", "Run"),
                "BashOutput" => ("Reading output", "Read output", "Read output"),
                "KillShell" => ("Stopping shell", "Stopped shell", "Stop shell"),
                "WebFetch" => ("Fetching", "Fetched", "Fetch"),
                "WebSearch" => ("Searching the web", "Searched the web", "Search the web"),
                "Task" or "Agent" => ("Running agent", "Ran agent", "Run agent"),
                "AskUserQuestion" => ("Asking you", "Asked you", "Ask you"),
                "ExitPlanMode" => ("Proposing a plan", "Proposed a plan", "Propose a plan"),
                _ when TodoParser.IsTodosTool(ToolName) => ("Updating tasks", "Updated tasks", "Update tasks"),
                _ when TodoParser.IsArtifactTool(ToolName) => ("Adding artifact", "Added artifact", "Add artifact"),
                _ => (PrettyName(ToolName), PrettyName(ToolName), PrettyName(ToolName)),
            };

            return Status switch
            {
                ToolCallStatus.Running => present,
                ToolCallStatus.Succeeded => past,
                ToolCallStatus.AwaitingApproval when Kind is ToolKind.Ask or ToolKind.Plan => present,
                ToolCallStatus.AwaitingApproval => "Wants to " + char.ToLowerInvariant(imperative[0]) + imperative[1..],
                _ => imperative,
            };
        }
    }

    /// <summary>"mcp__github__create_issue" reads as "github · create issue".</summary>
    private static string PrettyName(string toolName)
    {
        var parts = toolName.Split("__", StringSplitOptions.RemoveEmptyEntries);
        return parts is ["mcp", var server, var tool]
            ? $"{server} · {tool.Replace('_', ' ')}"
            : toolName;
    }

    /// <summary>
    /// What the step acts on, beside the verb: the file name, the command's first line,
    /// the pattern or URL - or, once a question is answered, the answer itself.
    /// </summary>
    public string Target
    {
        get
        {
            if (AnswerSummary is { Length: > 0 } answer)
            {
                return answer;
            }

            if (PrimaryFile is { } file)
            {
                return Files.Count > 1 ? $"{file.Name} +{Files.Count - 1}" : file.Name;
            }

            if (IsShellTool && Command.Length > 0)
            {
                var first = Command.Trim().ReplaceLineEndings("\n").Split('\n')[0];
                return first.Length > 140 ? first[..140] + "…" : first;
            }

            return Summary == ToolName ? "" : Summary;
        }
    }

    /// <summary>Code-ish targets (paths, commands, patterns) set in mono; prose targets are not.</summary>
    public bool IsCodeTarget => AnswerSummary is null && Kind is ToolKind.Shell or ToolKind.Search or ToolKind.Read or ToolKind.Edit or ToolKind.Write;

    /// <summary>A file target is its own link button; everything else rides in the row as text.</summary>
    public bool CodeTargetVisible => !HasFiles && IsCodeTarget && Target.Length > 0;

    public bool ProseTargetVisible => !HasFiles && !IsCodeTarget && Target.Length > 0;

    /// <summary>
    /// The target as one run of the row's single TextBlock. The row used to be separate
    /// TextBlocks per font, each centred in its own line box - and the different font
    /// metrics put their baselines a couple of pixels apart. Runs share one line and one
    /// baseline, so the verb and its target stay on the same optical line. The leading
    /// en-space is the gap the old StackPanel spacing provided; empty when not shown.
    /// </summary>
    public string CodeTarget => CodeTargetVisible ? "\u2002" + Target : "";

    public string ProseTarget => ProseTargetVisible ? "\u2002" + Target : "";

    /// <summary>The directory hint that follows a file chip's name, same run trick.</summary>
    public string FileDirectoryHint
    {
        get
        {
            var directory = PrimaryFile?.ShortDirectory;
            return string.IsNullOrEmpty(directory) ? "" : "\u2002" + directory;
        }
    }

    // Which approval card the body shows; bound from the call so a settled (null)
    // approval hides its card instead of leaving the last visibility in place.

    public bool QuestionCardVisible => Approval?.IsAskUserQuestion == true;

    public bool PlanApprovalVisible => Approval?.IsPlan == true;

    public bool PermissionVisible => Approval?.IsGeneric == true;

    /// <summary>
    /// Whether the step shows its body - diff, command, question, output - or just the
    /// one-line row. Set by kind when the call starts, folded when it lands; the reader
    /// can always reopen it.
    /// </summary>
    [ObservableProperty]
    public partial bool IsExpanded { get; set; }

    /// <summary>Set on the first manual toggle; from then on the step stays as the reader left it.</summary>
    public bool UserToggled { get; private set; }

    /// <summary>Wall time the call took; null while it runs or never finished.</summary>
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(DurationText))]
    [NotifyPropertyChangedFor(nameof(MetaDuration))]
    public partial TimeSpan? Duration { get; set; }

    public string DurationText => Duration is { } taken ? FormatDuration(taken) : "";

    /// <summary>Right of the row: sub-ten-second calls keep a decimal, longer ones count up.</summary>
    public static string FormatDuration(TimeSpan taken) => taken.TotalSeconds switch
    {
        < 10 => $"{taken.TotalSeconds:0.#}s",
        < 60 => $"{(int)taken.TotalSeconds}s",
        < 3600 => $"{(int)taken.TotalMinutes}m {taken.Seconds:00}s",
        _ => $"{(int)taken.TotalHours}h {taken.Minutes:00}m",
    };

    /// <summary>The reader opened or shut this step by hand: their choice outranks the auto-collapse.</summary>
    public void ToggleExpanded()
    {
        UserToggled = true;
        IsExpanded = !IsExpanded;
    }

    /// <summary>
    /// A permission question landed on this call: the step must be open for it to be
    /// answered, so it reopens - with its explore group - and any earlier manual fold
    /// is forgotten.
    /// </summary>
    public void ReopenForApproval()
    {
        UserToggled = false;
        IsExpanded = true;

        if (Group is { } group)
        {
            group.IsExpanded = true;
        }
    }

    /// <summary>
    /// Folds a finished step to its row. Calls still working stay open, a reader-pinned
    /// step is not touched, and the plan never folds: its body is the deliverable.
    /// </summary>
    public void AutoCollapse()
    {
        if (!IsPlan && !UserToggled && IsFinished)
        {
            IsExpanded = false;
        }
    }

    /// <summary>The result as it came back: newlines and columns intact, size-capped.</summary>
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(HasOutput))]
    [NotifyPropertyChangedFor(nameof(ShowOutput))]
    public partial string? Output { get; set; }

    public bool HasOutput => Output is { Length: > 0 };

    /// <summary>
    /// An edit's result text ("The file … has been updated") says less than its diff,
    /// so the diff replaces it - unless the edit failed, when the error is the story.
    /// </summary>
    public bool ShowOutput => HasOutput && (Status == ToolCallStatus.Failed || (!HasDiff && Kind != ToolKind.Ask));

    /// <summary>The change this call makes: previewed from its input, then replaced by the CLI's own patch.</summary>
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(HasDiff))]
    [NotifyPropertyChangedFor(nameof(DiffAddedText))]
    [NotifyPropertyChangedFor(nameof(DiffRemovedText))]
    [NotifyPropertyChangedFor(nameof(MetaStatus))]
    [NotifyPropertyChangedFor(nameof(MetaDuration))]
    [NotifyPropertyChangedFor(nameof(ShowOutput))]
    [NotifyPropertyChangedFor(nameof(Verb))]
    [NotifyPropertyChangedFor(nameof(InlineDiff))]
    public partial FileDiff? Diff { get; set; }

    /// <summary>The transcript renders a diff line by line, so a whole-file Write would realize thousands of elements per step. The inline preview is capped; <see cref="Diff"/> stays whole for the +/− counts and the diff tab.</summary>
    public FileDiff? InlineDiff
    {
        get
        {
            if (Diff is not { } diff)
            {
                return null;
            }

            var total = diff.Hunks.Sum(h => h.Lines.Count);
            if (total <= InlineLineCap)
            {
                return diff;
            }

            var kept = new List<DiffHunk>();
            var remaining = InlineLineCap;
            foreach (var hunk in diff.Hunks)
            {
                if (remaining <= 0)
                {
                    break;
                }

                if (hunk.Lines.Count <= remaining)
                {
                    kept.Add(hunk);
                    remaining -= hunk.Lines.Count;
                }
                else
                {
                    kept.Add(hunk with { Lines = hunk.Lines.Take(remaining).ToList() });
                    remaining = 0;
                }
            }

            kept.Add(new DiffHunk
            {
                Header = $"… {total - InlineLineCap} more lines - open the diff tab for the full change",
                Lines = [],
            });

            return diff with { Hunks = kept };
        }
    }

    private const int InlineLineCap = 500;

    public bool HasDiff => Diff is { Hunks.Count: > 0 };

    public string DiffAddedText => HasDiff && Diff!.Added > 0 ? $"+{Diff.Added}" : "";

    public string DiffRemovedText => HasDiff && Diff!.Removed > 0 ? $"−{Diff.Removed}" : "";

    /// <summary>What the reader answered, once they have: "Auth method → OAuth".</summary>
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(Target))]
    [NotifyPropertyChangedFor(nameof(CodeTarget))]
    [NotifyPropertyChangedFor(nameof(ProseTarget))]
    [NotifyPropertyChangedFor(nameof(IsCodeTarget))]
    [NotifyPropertyChangedFor(nameof(CodeTargetVisible))]
    [NotifyPropertyChangedFor(nameof(ProseTargetVisible))]
    public partial string? AnswerSummary { get; set; }

    /// <summary>
    /// The open permission question for this call, while there is one. Null once
    /// answered - the step folds and its own status shows the outcome.
    /// </summary>
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(IsAwaitingApproval))]
    [NotifyPropertyChangedFor(nameof(QuestionCardVisible))]
    [NotifyPropertyChangedFor(nameof(PlanApprovalVisible))]
    [NotifyPropertyChangedFor(nameof(PermissionVisible))]
    public partial ApprovalRequestItem? Approval { get; set; }

    public bool IsAwaitingApproval => Approval is not null;

    /// <summary>The tools that run a shell line.</summary>
    public static bool IsShellName(string toolName) => toolName is "Bash" or "PowerShell";

    /// <summary>The shell line an input payload carries (its "command" string), or null.</summary>
    public static string? CommandInput(string toolName, JsonElement input) =>
        IsShellName(toolName) && input.ValueKind == JsonValueKind.Object ? CommandText(input) : null;

    /// <summary>
    /// The plan markdown an ExitPlanMode input carries, or null: Claude's plan mode
    /// delivers the whole plan as one tool parameter rather than as task-tool steps.
    /// </summary>
    public static string? PlanInput(string toolName, JsonElement input) =>
        toolName == "ExitPlanMode" &&
        input.ValueKind == JsonValueKind.Object &&
        input.TryGetProperty("plan", out var plan) &&
        plan.ValueKind == JsonValueKind.String
            ? plan.GetString()
            : null;

    /// <summary>
    /// The files a tool call touches, read out of its input payload: Claude's file tools
    /// carry "file_path" (or "notebook_path").
    /// </summary>
    public static IReadOnlyList<ToolFileRef> FileInputs(string toolName, JsonElement input)
    {
        if (input.ValueKind != JsonValueKind.Object)
        {
            return [];
        }

        if (Str(input, "file_path") is { Length: > 0 } filePath)
        {
            return [new ToolFileRef { Path = filePath, IsEdit = toolName is not "Read" }];
        }

        if (Str(input, "notebook_path") is { Length: > 0 } notebookPath)
        {
            return [new ToolFileRef { Path = notebookPath, IsEdit = toolName is not "NotebookRead" }];
        }

        return [];
    }

    private static string? Str(JsonElement element, string name) =>
        element.ValueKind == JsonValueKind.Object &&
        element.TryGetProperty(name, out var v) && v.ValueKind == JsonValueKind.String ? v.GetString() : null;

    private static string? FirstQuestion(JsonElement input) =>
        input.TryGetProperty("questions", out var array) &&
        array.ValueKind == JsonValueKind.Array &&
        array.GetArrayLength() > 0 &&
        array[0].ValueKind == JsonValueKind.Object &&
        array[0].TryGetProperty("question", out var text) &&
        text.ValueKind == JsonValueKind.String
            ? text.GetString()
            : null;

    /// <summary>
    /// The plan's first line as the step's headline: plans open with a markdown
    /// heading naming the work, and "# Snake falls off the board..." reads better
    /// without the hash.
    /// </summary>
    public static string? PlanHeadline(string? plan) =>
        plan?
            .Split('\n')
            .Select(line => line.Trim())
            .FirstOrDefault(line => line.Length > 0)?
            .TrimStart('#')
            .Trim();

    private static string? CommandText(JsonElement element) => Str(element, "command");

    /// <summary>
    /// A one-line "what is this about to do", so the transcript and the approval card
    /// both read as an action rather than as a JSON blob.
    /// </summary>
    public static string Describe(string toolName, JsonElement input)
    {
        if (input.ValueKind != JsonValueKind.Object)
        {
            return toolName;
        }

        string? Str(string name) =>
            input.TryGetProperty(name, out var v) && v.ValueKind == JsonValueKind.String ? v.GetString() : null;

        var described = toolName switch
        {
            "Write" or "Edit" or "MultiEdit" or "NotebookEdit" or "Read" => Str("file_path"),
            "Bash" or "PowerShell" => CommandText(input),
            "Glob" or "Grep" => Str("pattern"),
            "LS" => Str("path"),
            "WebFetch" => Str("url"),
            "WebSearch" => Str("query"),
            "Task" or "Agent" => Str("description"),
            "AskUserQuestion" => FirstQuestion(input),
            "ExitPlanMode" => PlanHeadline(PlanInput("ExitPlanMode", input)),
            _ => null,
        };

        described ??= input.EnumerateObject().FirstOrDefault().Value is { ValueKind: JsonValueKind.String } first
            ? first.GetString()
            : null;

        if (string.IsNullOrWhiteSpace(described))
        {
            return toolName;
        }

        var singleLine = described.ReplaceLineEndings(" ").Trim();
        return singleLine.Length > 160 ? singleLine[..160] + "…" : singleLine;
    }
}

/// <summary>Why a conversation wants the reader back.</summary>
public enum AttentionKind
{
    Question,
    Permission,
    Plan,
    TurnFinished,
    TurnFailed,
}

/// <summary>What to tell the reader when the conversation needs them: the kind, and a line of detail.</summary>
public sealed record AttentionRequest(AttentionKind Kind, string Message)
{
    /// <summary>True when the turn is blocked until the reader answers.</summary>
    public bool NeedsAnswer => Kind is AttentionKind.Question or AttentionKind.Permission or AttentionKind.Plan;
}

public enum NoticeSeverity
{
    Info,
    Warning,
    Error,
}

/// <summary>Out-of-band information: session ended, a tool was denied, protocol drift.</summary>
public sealed partial class NoticeItem : ChatItem
{
    public override TranscriptBlock Block => TranscriptBlock.Notice;

    protected override Thickness NaturalMargin => new(0, 4, 40, 4);

    public required string Text { get; init; }
    public NoticeSeverity Severity { get; init; } = NoticeSeverity.Info;

    /// <summary>The label of the notice's one button, or null for a notice with none.</summary>
    public string? ActionText { get; init; }

    /// <summary>What the button does.</summary>
    public Func<Task>? Action { get; init; }

    public bool HasAction => ActionText is not null && Action is not null;

    /// <summary>Segoe Fluent glyph for the severity: info, warning triangle, error circle.</summary>
    public string Glyph => Severity switch
    {
        NoticeSeverity.Error => "",
        NoticeSeverity.Warning => "",
        _ => "",
    };
}
