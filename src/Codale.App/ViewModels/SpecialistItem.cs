using System.Collections.ObjectModel;
using Codale.Agents;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;

namespace Codale.App.ViewModels;

/// <summary>A compact link in the main history; the child conversation lives in the plan preview pane.</summary>
public sealed partial class SpecialistHistoryItem(SpecialistItem specialist) : ChatItem
{
    public override TranscriptBlock Block => TranscriptBlock.Tool;
    public SpecialistItem Specialist { get; } = specialist;
}

/// <summary>One choice on a planner question: a label with an optional description, picked by click.</summary>
public sealed partial class SpecialistOptionItem(SpecialistQuestionItem owner, string text) : ObservableObject
{
    private readonly int _split = text.IndexOf(": ", StringComparison.Ordinal);
    public string Label => _split > 0 ? text[.._split] : text;
    public string Description => _split > 0 ? text[(_split + 2)..] : "";
    public bool HasDescription => Description.Length > 0;
    public string Glyph => owner.MultiSelect ? IsSelected ? "\uE73A" : "\uE739" : IsSelected ? "\uECCB" : "\uECCA";
    [ObservableProperty, NotifyPropertyChangedFor(nameof(Glyph))] public partial bool IsSelected { get; set; }
    [RelayCommand] private void Toggle() => owner.Pick(this);
}

public sealed partial class SpecialistQuestionItem : ObservableObject
{
    private readonly SpecialistQuestion _question;
    public SpecialistQuestionItem(SpecialistQuestion question, int index, int count)
    {
        _question = question;
        Step = count > 1 ? $"QUESTION {index + 1} OF {count}" : "QUESTION";
        Options = [.. question.Options.Select(o => new SpecialistOptionItem(this, o))];
    }
    public string Id => _question.Id;
    public string Text => _question.Text;
    public bool MultiSelect => _question.MultiSelect;
    public string Step { get; }
    public IReadOnlyList<SpecialistOptionItem> Options { get; }
    public bool HasOptions => Options.Count > 0;
    public string CustomPlaceholder => HasOptions ? "Something else…" : "Your answer";
    [ObservableProperty, NotifyPropertyChangedFor(nameof(Answer), nameof(IsAnswered))] public partial string Custom { get; set; } = "";
    /// <summary>The picked labels, then any typed answer.</summary>
    public string Answer => string.Join(", ", Options.Where(o => o.IsSelected).Select(o => o.Label)
        .Append(Custom.Trim()).Where(a => a.Length > 0));
    public bool IsAnswered => Answer.Length > 0;
    public event Action? Changed;

    internal void Pick(SpecialistOptionItem option)
    {
        if (MultiSelect) option.IsSelected = !option.IsSelected;
        else foreach (var o in Options) o.IsSelected = ReferenceEquals(o, option) && !o.IsSelected;
        OnPropertyChanged(nameof(Answer));
        OnPropertyChanged(nameof(IsAnswered));
        Changed?.Invoke();
    }

    partial void OnCustomChanged(string value) => Changed?.Invoke();
}

public sealed partial class SpecialistItem : ChatItem
{
    public override TranscriptBlock Block => TranscriptBlock.Prose;
    public required SpecialistSession Session { get; init; }
    public Func<SpecialistItem, Task>? Send { get; init; }
    public Func<SpecialistItem, Task>? Approve { get; init; }
    public Func<SpecialistItem, Task>? Retry { get; init; }
    public Func<SpecialistItem, Task>? Continue { get; init; }
    public Func<SpecialistItem, Task>? Skip { get; init; }
    public Action<SpecialistItem>? ShowDetails { get; init; }
    public ObservableCollection<SpecialistQuestionItem> Questions { get; } = [];
    public ObservableCollection<ChatItem> Messages { get; } = [];
    private int _projectedMessages;
    /// <summary>The chat item each snapshot message became, by message index.</summary>
    private readonly List<ChatItem> _messageItems = [];
    private string? _projectedPlan;
    [ObservableProperty] public partial string Input { get; set; } = "";
    [ObservableProperty] public partial string Header { get; set; } = "";
    [ObservableProperty] public partial string Status { get; set; } = "";
    [ObservableProperty] public partial string Transcript { get; set; } = "";
    [ObservableProperty] public partial string Answer { get; set; } = "";
    [ObservableProperty] public partial string Plan { get; set; } = "";
    [ObservableProperty] public partial bool CanApprove { get; set; }
    [ObservableProperty] public partial bool HasQuestions { get; set; }
    [ObservableProperty] public partial bool CanSend { get; set; }
    [ObservableProperty] public partial bool CanRetry { get; set; }
    [ObservableProperty, NotifyPropertyChangedFor(nameof(IsIdle))] public partial bool IsWorking { get; set; }
    /// <summary>Nothing in the pane but Stop takes input while the specialist works.</summary>
    public bool IsIdle => !IsWorking;
    /// <summary>Auto Mode planning offers exactly two actions: Continue (answer / implement the plan) and Skip (implement without a plan).</summary>
    [ObservableProperty] public partial bool CanContinue { get; set; }
    [ObservableProperty] public partial bool CanSkip { get; set; }
    /// <summary>The planner's Revise / Reply step: the footer swaps its actions for an input with Back and Send.</summary>
    [ObservableProperty, NotifyPropertyChangedFor(nameof(ShowActionBar), nameof(ShowComposer))] public partial bool IsRevising { get; set; }
    [ObservableProperty] public partial bool CanRevise { get; set; }
    public bool ShowActions => CanContinue || CanSkip;
    public bool ShowActionBar => ShowActions && !IsRevising;
    /// <summary>Answers always have the follow-up input; the planner shows it only while revising.</summary>
    public bool ShowComposer => !IsPlanner || IsRevising;
    public bool ShowStatusStop => IsPlanner && IsWorking;
    public bool IsPlanner => Session.Snapshot.Planner;
    public string PrimaryLabel => HasQuestions ? "Continue" : "Accept";
    public string ReviseLabel => Session.Snapshot.Plan is not null ? "Revise" : "Reply";
    public string ActionHint => !IsPlanner || Session.Snapshot.HandedOff ? ""
        : HasQuestions ? Questions.All(q => q.IsAnswered) ? "All answered." : "Unanswered questions use sensible defaults."
        : CanContinue ? "Accept implements this plan in the main chat."
        : IsWorking ? "Planning… Stop above interrupts it." : "Skip ends planning without implementing.";
    public string ModelLabel => Session.Snapshot.Model ?? (Session.Snapshot.Planner ? "Plus" : "Lite");
    public string EntryTitle => Session.Snapshot.Skipped ? "Planning skipped" : Session.Snapshot.HandedOff ? "Plan approved" : CanRetry && !Session.Snapshot.Messages.Any(m => m.Role == "assistant")
        ? Session.Snapshot.Planner ? "Planning paused" : "Answer paused" : Session.Snapshot.Planner
        ? CanApprove ? "Plan ready for review" : "Planning your request"
        : IsWorking ? "Answering your question" : "Answer ready";
    public string EntrySubtitle => Session.Snapshot.Skipped ? "Planning stopped. Open to review the conversation."
        : Session.Snapshot.HandedOff ? "Implementation continues in the main chat."
        : Session.Snapshot.Planner ? HasQuestions ? "The planner has questions. Open it to answer them or skip planning."
        : Session.Snapshot.Plan is not null ? "The plan is ready. Open it to continue or skip." : "Shaping a plan beside the chat."
        : "Read the answer and ask follow-up questions.";
    public string EntryGlyph => Session.Snapshot.Planner ? "\uE9D5" : "\uE8BD";
    public string InputPlaceholder => Session.Snapshot.HandedOff ? "This planner is closed"
        : IsWorking ? Session.Snapshot.Planner ? "Planning… Stop to interrupt" : "Answering… Stop to interrupt"
        : HasQuestions ? "Or reply in your own words…"
        : Session.Snapshot.Planner ? Session.Snapshot.Plan is not null ? "What should change in the plan?" : "Reply to the planner…" : "Ask a follow-up…";
    public string Details => $"{Header}\n{Status}\nCLI: {Session.Snapshot.CliId}\nEndpoint: {Session.Snapshot.Endpoint}\nModel: {ModelLabel}\nEffort: {Session.Snapshot.Effort}\nSession: {Session.Snapshot.Id}\nParent: {Session.Snapshot.ParentId}" +
        (Session.Snapshot.Planner ? $"\nPlan version: {Session.Snapshot.PlanVersion}\nHanded to parent: {Session.Snapshot.HandedOff}" : "") +
        (Session.Snapshot.Attachments.Count > 0 ? "\nAttachments:\n" + string.Join("\n", Session.Snapshot.Attachments.Select(a => a.Path)) : "");
    public void Refresh()
    {
        var state = Session.Snapshot;
        Header = state.Planner ? "Planner · Plus" : "Answer · Lite";
        Status = state.Status;
        // A plan proposed after a draft reply takes that reply's place rather than following it.
        var draft = state.Plan is { Length: > 0 } proposed ? SpecialistSession.DraftIndexOf(state.Messages, proposed) : -1;
        Transcript = string.Join("\n\n---\n\n", state.Messages.Select((m, i) =>
            $"**{(m.Role == "user" ? "You" : "Agent")}**\n\n{(i == draft ? state.Plan : SpecialistSession.ExtractPlan(m.Text) ?? m.Text)}"));
        if (state.Plan is { Length: > 0 } plan && draft < 0 && !state.Messages.Any(m => m.Text == plan || SpecialistSession.ExtractPlan(m.Text) == plan))
            Transcript += "\n\n---\n\n" + plan;
        Answer = state.Plan is null ? state.Messages.LastOrDefault(m => m.Role == "assistant")?.Text ?? "" : "";
        Plan = state.Plan ?? "";
        CanApprove = state.Plan is not null && state.InterviewAnswered && !state.HandedOff && !Session.IsRunning;
        CanSend = !state.HandedOff && (!Session.IsRunning || state.Questions.Count > 0);
        CanRetry = !Session.IsRunning && !state.HandedOff && (state.Status.StartsWith("Failed") || state.Status.StartsWith("Stopped"));
        if (!Questions.Select(q => q.Id).SequenceEqual(state.Questions.Select(q => q.Id)))
        {
            foreach (var q in Questions) q.Changed -= OnAnswerChanged;
            Questions.Clear();
            for (var i = 0; i < state.Questions.Count; i++)
            {
                var question = new SpecialistQuestionItem(state.Questions[i], i, state.Questions.Count);
                question.Changed += OnAnswerChanged;
                Questions.Add(question);
            }
        }
        HasQuestions = Questions.Count > 0;
        IsWorking = Session.IsRunning && !HasQuestions;
        CanContinue = state.Planner && !state.HandedOff && (HasQuestions || state.Plan is not null && !Session.IsRunning);
        CanSkip = state.Planner && !state.HandedOff;
        CanRevise = state.Planner && !state.HandedOff && !HasQuestions && !Session.IsRunning;
        if (!CanRevise) IsRevising = false;
        while (_projectedMessages < state.Messages.Count)
        {
            var message = state.Messages[_projectedMessages++];
            ChatItem projected = message.Role == "user"
                ? SpecialistAnswersItem.From(message) is { } answered ? answered : new UserMessageItem { Text = message.Text }
                : new AssistantMessageItem { Text = SpecialistSession.ExtractPlan(message.Text) ?? message.Text, Model = ModelLabel, IsStreaming = false };
            Messages.Add(projected);
            _messageItems.Add(projected);
        }
        if (state.Plan is { Length: > 0 } document && document != _projectedPlan)
        {
            if (draft >= 0 && draft < _messageItems.Count && _messageItems[draft] is AssistantMessageItem reply)
                reply.Text = document;
            else if (!state.Messages.Any(m => m.Text == document || SpecialistSession.ExtractPlan(m.Text) == document))
                Messages.Add(new AssistantMessageItem { Text = document, Model = ModelLabel, IsStreaming = false });
            _projectedPlan = document;
        }
        OnPropertyChanged(nameof(EntryTitle));
        OnPropertyChanged(nameof(EntrySubtitle));
        OnPropertyChanged(nameof(Details));
        OnPropertyChanged(nameof(ModelLabel));
        OnPropertyChanged(nameof(ShowActions));
        OnPropertyChanged(nameof(ShowActionBar));
        OnPropertyChanged(nameof(ShowComposer));
        OnPropertyChanged(nameof(ShowStatusStop));
        OnPropertyChanged(nameof(PrimaryLabel));
        OnPropertyChanged(nameof(ReviseLabel));
        OnPropertyChanged(nameof(ActionHint));
        OnPropertyChanged(nameof(InputPlaceholder));
    }

    private void OnAnswerChanged() => OnPropertyChanged(nameof(ActionHint));

    [RelayCommand] private Task SendInputAsync() => Send?.Invoke(this) ?? Task.CompletedTask;
    [RelayCommand] private Task ApprovePlanAsync() => Approve?.Invoke(this) ?? Task.CompletedTask;
    [RelayCommand] private Task RetrySessionAsync() => Retry?.Invoke(this) ?? Task.CompletedTask;
    [RelayCommand] private Task CancelSessionAsync() => Session.StopAsync();
    [RelayCommand] private Task ContinuePlanningAsync() => Continue?.Invoke(this) ?? Task.CompletedTask;
    [RelayCommand] private Task SkipPlanningAsync() => Skip?.Invoke(this) ?? Task.CompletedTask;
    [RelayCommand] private void OpenDetails() => ShowDetails?.Invoke(this);
    [RelayCommand] private void BeginRevise() { if (CanRevise) IsRevising = true; }
    [RelayCommand] private void CancelRevise() { IsRevising = false; Input = ""; }
}

/// <summary>The user's answers to a round of planner questions, shown as question/answer pairs instead of one prompt bubble.</summary>
public sealed class SpecialistAnswersItem : ChatItem
{
    private const string LegacyPrefix = "Answers to your interview questions:";
    public override TranscriptBlock Block => TranscriptBlock.Prose;
    public required IReadOnlyList<SpecialistAnswer> Pairs { get; init; }
    public required string Text { get; init; }

    public static SpecialistAnswersItem? From(SpecialistMessage message)
    {
        if (message.Answers is { Count: > 0 } answers) return new() { Pairs = answers, Text = message.Text };
        // Older saved transcripts kept only "Question?: answer" lines.
        var body = message.Text.StartsWith(LegacyPrefix, StringComparison.Ordinal) ? message.Text[LegacyPrefix.Length..] : message.Text;
        var lines = body.Split('\n',StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
        if (lines.Length == 0 || !lines.All(l => l.Contains("?: ", StringComparison.Ordinal))) return null;
        return new()
        {
            Text = message.Text,
            Pairs = [.. lines.Select(l => l.IndexOf("?: ", StringComparison.Ordinal) + 1).Zip(lines, (i, l) => new SpecialistAnswer(l[..i], l[(i + 1)..].Trim()))],
        };
    }
}

public sealed partial class MemoryNoticeItem : ChatItem
{
    public override TranscriptBlock Block => TranscriptBlock.Prose;
    public required string Text { get; init; }
    public required Action Open { get; init; }
    public required Func<Task<bool>> Forget { get; init; }
    [ObservableProperty, NotifyPropertyChangedFor(nameof(Label), nameof(CanForget))] public partial bool IsForgotten { get; set; }
    public string Label => IsForgotten ? "Forgotten" : "Remembered";
    public bool CanForget => !IsForgotten;
    [RelayCommand] private void OpenMemory() => Open();
    [RelayCommand] private async Task ForgetMemoryAsync() { IsForgotten = await Forget(); }
}
