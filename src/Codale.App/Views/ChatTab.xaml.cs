using System.Windows.Input;

using Codale.App.Services;
using Codale.App.ViewModels;
using Codale.Core.Agents;

using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Controls.Primitives;
using Microsoft.UI.Xaml.Input;
using Microsoft.UI.Xaml.Media;
using Microsoft.UI.Xaml.Media.Animation;

using Windows.ApplicationModel.DataTransfer;
using Windows.Graphics.Imaging;
using Windows.Storage.Pickers;
using Windows.System;

namespace Codale.App.Views;

/// <summary>
/// A workspace file a transcript tool call points at; IsEdit asks for the diff view.
/// Diff, when present, is the call's own change - shown instead of git's whole-file diff.
/// </summary>
public sealed class ToolFileRequestEventArgs(string path, bool isEdit, Codale.Git.FileDiff? diff = null, int line = 0) : EventArgs
{
    public string Path { get; } = path;

    /// <summary>1-based line to reveal and mark, or 0 for the top of the file.</summary>
    public int Line { get; } = line;
    public bool IsEdit { get; } = isEdit;
    public Codale.Git.FileDiff? Diff { get; } = diff;
}

/// <summary>
/// One chat conversation, hosted in a centre-area tab. The tab is created on demand
/// from the + button; the CLI connects the first time a chat tab appears.
/// </summary>
public sealed partial class ChatTab : UserControl
{
    private ChatViewModel? _viewModel;

    /// <summary>Whether the / and @ suggestion popup is on screen.</summary>
    private bool IsSuggestOpen => SuggestPopup.IsOpen;

    /// <summary>The host opens a diff (edits) or the file itself (reads) from a transcript chip.</summary>
    public event EventHandler<ToolFileRequestEventArgs>? ToolFileRequested;

    /// <summary>
    /// Live-applies the chat font settings: the family on the tab root (inherited by
    /// every row), and the scale through each open row's <c>RaiseTextScale</c>, which
    /// the OneWay size bindings follow - MarkdownView re-renders from its new base.
    /// </summary>
    public void ApplyAppSettings()
    {
        ApplyChatTypography();

        if (ViewModel is not { } viewModel)
        {
            return;
        }

        foreach (var item in viewModel.Items)
        {
            item.RaiseTextScale();
            if (item is ToolCallItem { Approval: { } approval })
            {
                foreach (var question in approval.Questions)
                {
                    question.RaiseTextScale();
                    foreach (var option in question.Options)
                    {
                        option.RaiseTextScale();
                    }
                }
            }
        }
    }

    private void ApplyChatTypography() =>
        FontFamily = new FontFamily(Codale.App.Services.InstalledFonts.Resolve(AppSettings.ChatFontFamily, "Segoe UI"));

    /// <summary>The host opens a plan in its own tab, from the plan step's link.</summary>
    public event EventHandler<ToolCallItem>? PlanOpenRequested;

    /// <summary>Whether the transcript should ride the bottom. Only scroll events may
    /// change it: content growth is answered by following, so it must never count as
    /// "the reader moved away".</summary>
    private bool _pinnedToBottom = true;

    /// <summary>A ride-to-bottom is already queued for this layout pass.</summary>
    private bool _followQueued;

    /// <summary>Question cards whose visibility is already watched; a card can load more than once.</summary>
    private readonly System.Runtime.CompilerServices.ConditionalWeakTable<FrameworkElement, object> _hookedCards = new();

    public ChatTab()
    {
        InitializeComponent();

        // The transcript reads in the chat font from Settings; FontFamily set here
        // inherits to every row that does not pin its own (the monospaced runs do).
        ApplyChatTypography();

        // The popup is sized to the composer and has to follow it when the pane resizes.
        ComposerBorder.SizeChanged += (_, _) => PlaceSuggestions();
        DraftBox.LostFocus += (_, _) => DispatcherQueue.TryEnqueue(() =>
        {
            // A click on a row keeps focus in the draft (the list never takes it), so
            // losing focus means the user went elsewhere.
            if (DraftBox.FocusState == FocusState.Unfocused)
            {
                CloseSuggestions();
            }
        });

        // Following is about size, not item count: streaming text grows the last row
        // by deltas, and a finished tool call grows its own row when its output lands
        // - neither adds an item, so an items-added hook would never fire for the
        // cases where following matters most.
        Transcript.SizeChanged += OnTranscriptSizeChanged;
        ChatScroller.ViewChanged += OnScrollerViewChanged;

        // Only Esc presses nothing else claimed (a popup, a question card) reach this.
        KeyDown += OnChatKeyDown;

        Loaded += (_, _) => MarkdownView.FileLinkClicked += OnMarkdownFileLink;
        Unloaded += (_, _) => MarkdownView.FileLinkClicked -= OnMarkdownFileLink;
    }

    private static readonly TimeSpan DoubleEscapeWindow = TimeSpan.FromMilliseconds(600);

    private DateTime _lastEscape;

    /// <summary>Esc twice in quick succession stops the running turn, unless the user turned that off in Settings.</summary>
    private void OnChatKeyDown(object sender, KeyRoutedEventArgs e)
    {
        if (e.Handled || e.Key != VirtualKey.Escape || !KeyboardShortcuts.StopOnDoubleEscape)
        {
            return;
        }

        var now = DateTime.UtcNow;
        if (now - _lastEscape > DoubleEscapeWindow)
        {
            _lastEscape = now;
            return;
        }

        _lastEscape = default;
        if (ViewModel.ShowStop)
        {
            ViewModel.InterruptCommand.Execute(null);
            e.Handled = true;
        }
    }

    /// <summary>A path in the assistant's text was clicked; only the tab holding that text answers.</summary>
    private void OnMarkdownFileLink(MarkdownView source, Codale.App.Services.LinkMatch link)
    {
        if (link.Path is null || !IsAncestorOf(source))
        {
            return;
        }

        ToolFileRequested?.Invoke(this, new ToolFileRequestEventArgs(link.Path, isEdit: false, line: link.Line));
    }

    private bool IsAncestorOf(DependencyObject node)
    {
        for (var parent = Microsoft.UI.Xaml.Media.VisualTreeHelper.GetParent(node); parent is not null;
             parent = Microsoft.UI.Xaml.Media.VisualTreeHelper.GetParent(parent))
        {
            if (ReferenceEquals(parent, this))
            {
                return true;
            }
        }

        return false;
    }

    public ChatViewModel ViewModel
    {
        get => _viewModel!;
        set
        {
            if (_viewModel is not null)
            {
                _viewModel.PropertyChanged -= OnViewModelPropertyChanged;
            }

            _viewModel = value;
            _viewModel.PropertyChanged += OnViewModelPropertyChanged;
            Bindings.Update();
            UpdateStopPulse();
        }
    }

    private Storyboard? _stopPulse;

    private void OnViewModelPropertyChanged(object? sender, System.ComponentModel.PropertyChangedEventArgs e)
    {
        if (e.PropertyName is null or nameof(ChatViewModel.IsBusy))
        {
            UpdateStopPulse();
        }
    }

    /// <summary>
    /// The stop button breathes red for as long as the turn runs - a call to act
    /// that stays visible without shouting. Built in code because the animated
    /// target is the brush resource itself.
    /// </summary>
    private void UpdateStopPulse()
    {
        if (ViewModel?.IsBusy is true)
        {
            if (_stopPulse is not null)
            {
                return;
            }

            var fade = new ColorAnimation
            {
                From = Windows.UI.Color.FromArgb(0x26, 0xCD, 0x5C, 0x5C),
                To = Windows.UI.Color.FromArgb(0xD9, 0xCD, 0x5C, 0x5C),
                Duration = new Duration(TimeSpan.FromMilliseconds(800)),
            };
            Storyboard.SetTarget(fade, StopPulseBrush);
            Storyboard.SetTargetProperty(fade, "Color");

            _stopPulse = new Storyboard
            {
                AutoReverse = true,
                RepeatBehavior = RepeatBehavior.Forever,
            };
            _stopPulse.Children.Add(fade);
            _stopPulse.Begin();
        }
        else if (_stopPulse is not null)
        {
            _stopPulse.Stop();
            _stopPulse = null;
            StopPulseBrush.Color = Microsoft.UI.Colors.Transparent;
        }
    }

    private void OnScrollerViewChanged(object? sender, ScrollViewerViewChangedEventArgs e) =>
        _pinnedToBottom = ChatScroller.ScrollableHeight - ChatScroller.VerticalOffset < 40;

    private void OnTranscriptSizeChanged(object sender, SizeChangedEventArgs e)
    {
        if (e.NewSize.Height <= e.PreviousSize.Height || !_pinnedToBottom || _followQueued)
        {
            return;
        }

        // Low priority: the new row has to be measured first, or the target height
        // is the old one and the scroll lands short. Coalesced per pass - a burst of
        // streaming deltas needs one scroll, not fifty.
        _followQueued = true;
        DispatcherQueue.TryEnqueue(Microsoft.UI.Dispatching.DispatcherQueuePriority.Low, () =>
        {
            _followQueued = false;
            ChatScroller.UpdateLayout();
            ChatScroller.ChangeView(null, ChatScroller.ScrollableHeight, null, disableAnimation: true);
        });
    }

    /// <summary>A step's file target: edits open the call's own diff, reads open the file.</summary>
    private void OnToolFileClick(object sender, RoutedEventArgs e)
    {
        if (((FrameworkElement)sender).Tag is ToolCallItem { PrimaryFile: { } file } call)
        {
            ToolFileRequested?.Invoke(this, new ToolFileRequestEventArgs(file.Path, file.IsEdit, file.IsEdit ? call.Diff : null));
        }
    }

    /// <summary>"Open in diff tab" under an inline diff.</summary>
    private void OnOpenToolDiffClick(object sender, RoutedEventArgs e)
    {
        if (((FrameworkElement)sender).Tag is ToolCallItem { Diff: { } diff } call)
        {
            ToolFileRequested?.Invoke(this, new ToolFileRequestEventArgs(call.PrimaryFile?.Path ?? diff.Path, isEdit: true, diff));
        }
    }

    private void OnOpenPlanClick(object sender, RoutedEventArgs e)
    {
        if (((FrameworkElement)sender).Tag is ToolCallItem call)
        {
            PlanOpenRequested?.Invoke(this, call);
        }
    }

    /// <summary>A step's row: fold or unfold its body. Once done by hand, the step stays as the reader left it.</summary>
    private void OnToolToggleClick(object sender, RoutedEventArgs e)
    {
        if (((FrameworkElement)sender).Tag is ToolCallItem call)
        {
            call.ToggleExpanded();
        }
    }

    /// <summary>A turn's header: opens the folded timeline, or folds it again.</summary>
    private void OnTurnToggleClick(object sender, RoutedEventArgs e)
    {
        if (((FrameworkElement)sender).Tag is TurnActivityItem turn)
        {
            ChatViewModel.ToggleTurn(turn);
        }
    }

    private void OnExploreToggleClick(object sender, RoutedEventArgs e)
    {
        if (((FrameworkElement)sender).Tag is ExploreGroupStep group)
        {
            group.IsExpanded = !group.IsExpanded;
        }
    }

    // Inline approval: the card under the tool call is the whole dialog.
    private void OnApprovalAllow(object sender, RoutedEventArgs e) =>
        RespondToApproval(((FrameworkElement)sender).Tag as ApprovalRequestItem, ApprovalDecision.Allow());

    private void OnApprovalDeny(object sender, RoutedEventArgs e) =>
        RespondToApproval(
            ((FrameworkElement)sender).Tag as ApprovalRequestItem,
            ApprovalDecision.Deny("The user denied this from Codale."));

    /// <summary>A question the reader chose not to answer: the agent carries on without it.</summary>
    private void OnApprovalSkip(object sender, RoutedEventArgs e) =>
        RespondToApproval(
            ((FrameworkElement)sender).Tag as ApprovalRequestItem,
            ApprovalDecision.Deny("The user chose not to answer. Continue with your best judgement."));

    private void OnApprovalStartRevise(object sender, RoutedEventArgs e)
    {
        if (((FrameworkElement)sender).Tag is ApprovalRequestItem approval)
        {
            approval.IsRevising = true;
        }
    }

    private void OnQuestionNext(object sender, RoutedEventArgs e) =>
        (((FrameworkElement)sender).Tag as ApprovalRequestItem)?.Next();

    private void OnQuestionBack(object sender, RoutedEventArgs e) =>
        (((FrameworkElement)sender).Tag as ApprovalRequestItem)?.Back();

    /// <summary>
    /// Keyboard on the question card: 1-9 pick an answer, Enter moves on (or submits
    /// on the last question), Esc skips. Typing in the "Other" box is left alone.
    /// </summary>
    private void OnQuestionCardKeyDown(object sender, KeyRoutedEventArgs e)
    {
        if (((FrameworkElement)sender).Tag is not ApprovalRequestItem { CurrentQuestion: { } question } approval)
        {
            return;
        }

        var typing = e.OriginalSource is TextBox;

        if (!typing && e.Key is >= VirtualKey.Number1 and <= VirtualKey.Number9 or >= VirtualKey.NumberPad1 and <= VirtualKey.NumberPad9)
        {
            var index = e.Key >= VirtualKey.NumberPad1 ? e.Key - VirtualKey.NumberPad1 : e.Key - VirtualKey.Number1;
            if (index < question.Options.Count)
            {
                question.Toggle(question.Options[index]);
                e.Handled = true;
            }

            return;
        }

        switch (e.Key)
        {
            case VirtualKey.Enter when approval.IsLast && approval.CanSubmit:
                ViewModel.RespondToAskUserQuestion(approval);
                e.Handled = true;
                break;

            case VirtualKey.Enter when !approval.IsLast && approval.CanAdvance:
                approval.Next();
                e.Handled = true;
                break;

            case VirtualKey.Escape when !typing:
                OnApprovalSkip(sender, e);
                e.Handled = true;
                break;
        }
    }

    /// <summary>
    /// When a question card appears the agent is blocked on it, so the keyboard goes
    /// there - unless the reader is mid-sentence in the composer. Cards are shown by
    /// visibility, not re-created, so the hook is the visibility change itself.
    /// </summary>
    private void OnQuestionCardLoaded(object sender, RoutedEventArgs e)
    {
        if (sender is not FrameworkElement card || !_hookedCards.TryAdd(card, new object()))
        {
            return;
        }

        void FocusIfShown()
        {
            if (card.Visibility != Visibility.Visible ||
                (DraftBox.FocusState != FocusState.Unfocused && DraftBox.Text.Length > 0))
            {
                return;
            }

            DispatcherQueue.TryEnqueue(Microsoft.UI.Dispatching.DispatcherQueuePriority.Low, () =>
                FindDescendant<Button>(card)?.Focus(FocusState.Programmatic));
        }

        card.RegisterPropertyChangedCallback(VisibilityProperty, (_, _) => FocusIfShown());
        FocusIfShown();
    }

    private static T? FindDescendant<T>(DependencyObject root) where T : DependencyObject
    {
        for (var i = 0; i < Microsoft.UI.Xaml.Media.VisualTreeHelper.GetChildrenCount(root); i++)
        {
            var child = Microsoft.UI.Xaml.Media.VisualTreeHelper.GetChild(root, i);
            if (child is T match && (child is not Button b || b.Tag is ApprovalQuestionOption))
            {
                return match;
            }

            if (FindDescendant<T>(child) is { } deeper)
            {
                return deeper;
            }
        }

        return null;
    }

    private void OnApprovalSuggestion(object sender, RoutedEventArgs e)
    {
        if (((FrameworkElement)sender).Tag is SuggestionOption option)
        {
            ViewModel.RespondToApproval(
                option.RequestId,
                new ApprovalDecision { Behavior = ApprovalBehavior.Allow, AcceptedSuggestion = option.Suggestion });
        }
    }

    /// <summary>
    /// Plan flow: "Accept (edit mode)" / "Accept (auto)" approve the plan and put the
    /// session straight into the chosen work mode, so the follow-up turn runs under it.
    /// </summary>
    private void OnApprovalPlanAccept(object sender, RoutedEventArgs e)
    {
        if (((FrameworkElement)sender).Tag is ApprovalRequestItem approval &&
            ((ButtonBase)sender).CommandParameter is string mode)
        {
            ViewModel.RespondToApproval(
                approval.RequestId,
                new ApprovalDecision { Behavior = ApprovalBehavior.Allow, SwitchToMode = mode });
        }
    }

    /// <summary>
    /// Plan flow: "Revise plan" refuses the plan but tells the model why, so it comes
    /// back with a revision instead of stopping.
    /// </summary>
    private void OnApprovalRevise(object sender, RoutedEventArgs e)
    {
        if (((FrameworkElement)sender).Tag is ApprovalRequestItem approval)
        {
            var feedback = approval.RevisionFeedback.Trim();
            var message = feedback.Length > 0
                ? $"The user reviewed the plan and asked for revisions: {feedback}"
                : "The user reviewed the plan and asked for revisions before it is executed. Revise the plan and present it again.";

            ViewModel.RespondToApproval(
                approval.RequestId,
                new ApprovalDecision { Behavior = ApprovalBehavior.Deny, Message = message });
        }
    }

    /// <summary>Answer row on an AskUserQuestion card; the question normalises siblings.</summary>
    private void OnQuestionOptionClick(object sender, RoutedEventArgs e)
    {
        if (((FrameworkElement)sender).Tag is ApprovalQuestionOption option)
        {
            option.Owner?.Toggle(option);
        }
    }

    private void OnApprovalSubmitAnswers(object sender, RoutedEventArgs e)
    {
        if (((FrameworkElement)sender).Tag is ApprovalRequestItem approval)
        {
            ViewModel.RespondToAskUserQuestion(approval);
        }
    }

    private void RespondToApproval(ApprovalRequestItem? approval, ApprovalDecision decision)
    {
        if (approval is not null)
        {
            ViewModel.RespondToApproval(approval.RequestId, decision);
        }
    }

    private void OnDraftKeyDown(object sender, KeyRoutedEventArgs e)
    {
        // Tab accepts the highlighted / or @ row like Enter does, but only completes it:
        // a command is not sent, so its arguments can still be typed.
        if (e.Key is VirtualKey.Tab && IsSuggestOpen && SuggestList.SelectedItem is SuggestionItem picked)
        {
            e.Handled = true;
            var isCommand = picked.IsCommand;
            ApplySuggestion(picked);

            if (isCommand)
            {
                CloseSuggestions();
            }

            return;
        }

        // Tab (or Right) in an empty composer takes the suggested next prompt.
        if (e.Key is VirtualKey.Tab or VirtualKey.Right && !IsSuggestOpen && ViewModel.AcceptSuggestion())
        {
            e.Handled = true;
            DraftBox.SelectionStart = DraftBox.Text.Length;
            return;
        }

        if (e.Key is VirtualKey.Escape && IsSuggestOpen)
        {
            e.Handled = true;
            CloseSuggestions();
            return;
        }

        if (e.Key is VirtualKey.Up or VirtualKey.Down && IsSuggestOpen)
        {
            e.Handled = true;
            MoveSuggestion(e.Key == VirtualKey.Down ? 1 : -1);
            return;
        }
    }

    private void OnEnterAccelerator(KeyboardAccelerator sender, KeyboardAcceleratorInvokedEventArgs args)
    {
        args.Handled = true;

        // With the popup up, Enter first acts on the highlighted row; a slash command
        // completes and sends in one step, a file reference just completes.
        if (IsSuggestOpen && SuggestList.SelectedItem is SuggestionItem suggestion)
        {
            var isCommand = suggestion.IsCommand;
            ApplySuggestion(suggestion);

            if (isCommand)
            {
                CloseSuggestions();
                ExecuteIfCan(ViewModel.SendCommand);
            }

            return;
        }

        CloseSuggestions();
        ExecuteIfCan(ViewModel.SendCommand);
    }

    private void OnPlanEnterAccelerator(KeyboardAccelerator sender, KeyboardAcceleratorInvokedEventArgs args)
    {
        args.Handled = true;
        CloseSuggestions();
        ExecuteIfCan(ViewModel.SendPlanCommand);
    }

    private static void ExecuteIfCan(ICommand? command)
    {
        if (command?.CanExecute(null) == true)
        {
            command.Execute(null);
        }
    }

    private void OnDraftTextChanged(object sender, TextChangedEventArgs e)
    {
        ViewModel.UpdateSuggestions();

        if (ViewModel.Suggestions.Count > 0)
        {
            SuggestHeader.Text = ViewModel.Suggestions[0].IsCommand ? "Commands" : "Files";

            if (!IsSuggestOpen)
            {
                SuggestPopup.IsOpen = true;
                PlaceSuggestions();
            }

            // Selection after opening: a closed popup's list has no containers yet.
            SuggestList.SelectedIndex = 0;
            SuggestList.ScrollIntoView(SuggestList.SelectedItem);
        }
        else
        {
            CloseSuggestions();
        }
    }

    private void OnSuggestionClick(object sender, ItemClickEventArgs e)
    {
        if (e.ClickedItem is not SuggestionItem suggestion)
        {
            return;
        }

        var isCommand = suggestion.IsCommand;
        ApplySuggestion(suggestion);

        if (isCommand)
        {
            CloseSuggestions();
            ExecuteIfCan(ViewModel.SendCommand);
        }
    }

    /// <summary>Drops the suggestion into the draft: commands replace it, files replace the @token.</summary>
    private void ApplySuggestion(SuggestionItem suggestion)
    {
        var draft = ViewModel.Draft ?? "";

        if (suggestion.IsCommand)
        {
            ViewModel.Draft = suggestion.Insert + " ";
        }
        else
        {
            var at = draft.LastIndexOf('@');
            ViewModel.Draft = at < 0 ? draft : draft[..at] + suggestion.Insert + " ";
        }

        DraftBox.Focus(FocusState.Programmatic);
        DraftBox.SelectionStart = DraftBox.Text.Length;
        DraftBox.SelectionLength = 0;
    }

    private void MoveSuggestion(int delta)
    {
        var count = ViewModel.Suggestions.Count;
        if (count == 0)
        {
            return;
        }

        SuggestList.SelectedIndex = Math.Clamp(SuggestList.SelectedIndex + delta, 0, count - 1);
        SuggestList.ScrollIntoView(SuggestList.SelectedItem);
    }

    private void CloseSuggestions()
    {
        SuggestPopup.IsOpen = false;
    }

    private void OnSuggestPanelSizeChanged(object sender, SizeChangedEventArgs e) => PlaceSuggestions();

    /// <summary>Spans the composer's width and sits just above it; the popup's origin is the composer's top-left.</summary>
    private void PlaceSuggestions()
    {
        if (!SuggestPopup.IsOpen)
        {
            return;
        }

        SuggestPanel.Width = Math.Max(ComposerBorder.ActualWidth, 280);
        SuggestPopup.HorizontalOffset = 0;
        SuggestPopup.VerticalOffset = -SuggestPanel.ActualHeight - 6;
    }

    /// <summary>The / and @ buttons: type the trigger for the user, so the list opens as if they had.</summary>
    private void OnInsertTriggerClick(object sender, RoutedEventArgs e)
    {
        if (((FrameworkElement)sender).Tag is not string trigger)
        {
            return;
        }

        var draft = ViewModel.Draft ?? "";
        if (trigger == "/")
        {
            // Commands only count at the start of an otherwise empty draft.
            ViewModel.Draft = "/";
        }
        else
        {
            ViewModel.Draft = draft.Length == 0 || char.IsWhiteSpace(draft[^1]) ? draft + "@" : draft + " @";
        }

        DraftBox.Focus(FocusState.Programmatic);
        DraftBox.SelectionStart = DraftBox.Text.Length;
        DraftBox.SelectionLength = 0;
    }

    /// <summary>Puts the caret at the end of the prompt box; waits for the tab to load when it was only just shown.</summary>
    public void FocusDraft()
    {
        void Focus()
        {
            if (DraftBox.IsEnabled && DraftBox.Focus(FocusState.Programmatic))
            {
                DraftBox.SelectionStart = DraftBox.Text.Length;
                DraftBox.SelectionLength = 0;
            }
        }

        if (IsLoaded)
        {
            Focus();
            return;
        }

        RoutedEventHandler? onLoaded = null;
        onLoaded = (_, _) =>
        {
            Loaded -= onLoaded;
            DispatcherQueue.TryEnqueue(Focus);
        };
        Loaded += onLoaded;
    }

    private void OnDraftFocusChanged(object sender, RoutedEventArgs e) =>
        VisualStateManager.GoToState(
            this,
            DraftBox.FocusState == FocusState.Unfocused ? "ComposerUnfocused" : "ComposerFocused",
            useTransitions: false);

    /// <summary>
    /// The composer card is the input: a tap on any of its dead space (padding, empty
    /// text rows, placeholder) puts the caret in the draft. Buttons swallow their own
    /// taps, so they never reach here.
    /// </summary>
    private void OnComposerTapped(object sender, TappedRoutedEventArgs e)
    {
        if (DraftBox.FocusState == FocusState.Unfocused && DraftBox.IsEnabled)
        {
            DraftBox.Focus(FocusState.Pointer);
        }
    }

    private void OnCloseComposerNotice(object sender, RoutedEventArgs e)
    {
        ViewModel.ComposerNotice = null;
    }

    private async void OnComposerNoticeAction(object sender, RoutedEventArgs e)
    {
        if (ViewModel.ComposerNotice is { Action: { } action })
        {
            ViewModel.ComposerNotice = null;
            try
            {
                await action();
            }
            catch (Exception ex)
            {
                ViewModel.ComposerNotice = new NoticeItem { Text = ex.Message, Severity = NoticeSeverity.Error };
            }
        }
    }

    private async void OnAttachClick(object sender, RoutedEventArgs e)
    {
        var picker = new FileOpenPicker { SuggestedStartLocation = PickerLocationId.ComputerFolder };

        foreach (var type in new[] { ".png", ".jpg", ".jpeg", ".gif", ".webp", ".pdf" })
        {
            picker.FileTypeFilter.Add(type);
        }

        // A packaged picker needs to be told which window owns it.
        if (App.Current.MainWindowHandle is { } hwnd)
        {
            WinRT.Interop.InitializeWithWindow.Initialize(picker, hwnd);
        }

        foreach (var file in await picker.PickMultipleFilesAsync())
        {
            ViewModel.TryAddAttachment(file.Path);
        }
    }

    /// <summary>
    /// Every paste route (Ctrl+V, Shift+Insert, the context menu) lands here. The
    /// decision is made synchronously - Handled set after an await is too late, and
    /// the TextBox would paste as well. Files and images become attachment chips the
    /// way the picker adds them; text, including text that arrives with a bitmap
    /// rendering of itself (Office, browsers), pastes natively.
    /// </summary>
    private void OnDraftPaste(object sender, TextControlPasteEventArgs e)
    {
        DataPackageView content;
        try
        {
            content = Clipboard.GetContent();
        }
        catch (Exception ex) when (ex is System.Runtime.InteropServices.COMException or InvalidOperationException)
        {
            // The clipboard is briefly locked by whoever wrote it; let the native paste try.
            return;
        }

        if (content.Contains(StandardDataFormats.StorageItems))
        {
            e.Handled = true;
            _ = PasteFilesAsync(content);
        }
        else if (content.Contains(StandardDataFormats.Bitmap) && !content.Contains(StandardDataFormats.Text))
        {
            e.Handled = true;
            _ = PasteImageAsync(content);
        }
        else if (content.Contains(StandardDataFormats.Text))
        {
            // The text has to be read asynchronously, so the native paste is always
            // suppressed and PasteTextAsync inserts it (or swaps a huge block for a file).
            e.Handled = true;
            _ = PasteTextAsync(content);
        }
    }

    /// <summary>Longest clipboard text, in characters, that goes straight into the composer.</summary>
    private const int MaxInlinePasteChars = 40_000;

    /// <summary>
    /// A WinUI TextBox cannot take megabytes of wrapped text - it hangs or crashes
    /// natively - so a paste past the limit is saved to a temp file and mentioned
    /// as @path, the way a dropped file is.
    /// </summary>
    private async Task PasteTextAsync(DataPackageView content)
    {
        try
        {
            var text = await content.GetTextAsync();
            if (string.IsNullOrEmpty(text))
            {
                return;
            }

            if (text.Length <= MaxInlinePasteChars)
            {
                DraftBox.SelectedText = text;
                DraftBox.SelectionStart += DraftBox.SelectedText.Length;
                DraftBox.SelectionLength = 0;
                return;
            }

            var path = Path.Combine(Path.GetTempPath(), $"codale-paste-{DateTime.Now:yyyyMMdd-HHmmss}-{Guid.NewGuid().ToString("N")[..6]}.txt");
            await File.WriteAllTextAsync(path, text);
            InsertAtCaret("@" + (path.Contains(' ') ? $"\"{path}\"" : path) + " ");
        }
        catch (Exception ex)
        {
            CrashLog.Error("chat", "paste of text failed", ex);
        }
    }

    /// <summary>Attachable files become chips; anything else goes in as an @path mention.</summary>
    private async Task PasteFilesAsync(DataPackageView content)
    {
        IReadOnlyList<Windows.Storage.IStorageItem> items;
        try
        {
            items = await content.GetStorageItemsAsync();
        }
        catch (Exception ex) when (ex is System.Runtime.InteropServices.COMException or InvalidOperationException)
        {
            return;
        }

        try
        {
            var mentions = new List<string>();
            foreach (var item in items)
            {
                if (item is Windows.Storage.IStorageFile file && TurnAttachment.KindOf(file.Path) is not null)
                {
                    ViewModel.TryAddAttachment(file.Path);
                }
                else
                {
                    mentions.Add("@" + (item.Path.Contains(' ') ? $"\"{item.Path}\"" : item.Path));
                }
            }

            if (mentions.Count > 0)
            {
                InsertAtCaret(string.Join(" ", mentions));
            }
        }
        catch (Exception ex)
        {
            CrashLog.Error("chat", "paste of files failed", ex);
        }
    }

    private async Task PasteImageAsync(DataPackageView content)
    {
        try
        {
            if (await SaveClipboardImageAsync(content) is { } path)
            {
                ViewModel.TryAddAttachment(path);
            }
        }
        catch (Exception ex)
        {
            CrashLog.Error("chat", "paste of image failed", ex);
        }
    }

    private void InsertAtCaret(string text)
    {
        var start = DraftBox.SelectionStart;
        var current = DraftBox.Text;
        DraftBox.Text = current[..start] + text + current[(start + DraftBox.SelectionLength)..];
        DraftBox.SelectionStart = start + text.Length;
        DraftBox.SelectionLength = 0;
    }

    /// <summary>
    /// Writes the clipboard bitmap to a temp PNG. Re-encodes through a decoder
    /// rather than copying the raw stream, because whatever the source application
    /// wrote - PNG, BMP, a bare DIB - has to come out a real PNG: the attachment
    /// pipeline reads the media type from the extension.
    /// </summary>
    private static async Task<string?> SaveClipboardImageAsync(DataPackageView content)
    {
        try
        {
            var reference = await content.GetBitmapAsync();
            using var clipboard = await reference.OpenReadAsync();

            var name = $"codale-clipboard-{DateTime.Now:yyyyMMdd-HHmmss}-{Guid.NewGuid().ToString("N")[..6]}.png";
            var tempFolder = await Windows.Storage.StorageFolder.GetFolderFromPathAsync(Path.GetTempPath());
            var file = await tempFolder.CreateFileAsync(
                name, Windows.Storage.CreationCollisionOption.ReplaceExisting);
            using var target = await file.OpenAsync(Windows.Storage.FileAccessMode.ReadWrite);

            var decoder = await BitmapDecoder.CreateAsync(clipboard);
            var pixels = (await decoder.GetPixelDataAsync()).DetachPixelData();

            var encoder = await BitmapEncoder.CreateAsync(BitmapEncoder.PngEncoderId, target);
            encoder.SetPixelData(
                decoder.BitmapPixelFormat,
                BitmapAlphaMode.Premultiplied,
                decoder.PixelWidth,
                decoder.PixelHeight,
                decoder.DpiX,
                decoder.DpiY,
                pixels);
            await encoder.FlushAsync();

            return file.Path;
        }
        catch (Exception ex) when (ex is System.Runtime.InteropServices.COMException or TaskCanceledException)
        {
            return null;
        }
    }

    private void OnRemoveAttachment(object sender, RoutedEventArgs e)
    {
        if (((FrameworkElement)sender).Tag is ComposerAttachment attachment)
        {
            ViewModel.RemoveAttachment(attachment);
        }
    }

    /// <summary>
    /// Opens an attachment chip - composer or sent. Images open in a full-window
    /// lightbox; PDFs go to the default app, which already renders them well.
    /// </summary>
    private async void OnPreviewAttachment(object sender, TappedRoutedEventArgs e)
    {
        var (kind, name, path) = ((FrameworkElement)sender).Tag switch
        {
            // The remove button's Click lands before the chip's Tapped; a chip it just
            // took out of the composer is not a preview request.
            ComposerAttachment c when ViewModel.Attachments.Contains(c) => (c.Kind, c.Name, c.Path),
            TurnAttachment t => (t.Kind, t.Name, t.Path),
            _ => default,
        };

        if (path is null)
        {
            return;
        }

        e.Handled = true;

        if (!File.Exists(path))
        {
            await ShowPreviewDialogAsync(name, new TextBlock { Text = "The file is no longer on disk.", TextWrapping = TextWrapping.Wrap });
            return;
        }

        if (kind != TurnAttachmentKind.Image)
        {
            await Launcher.LaunchFileAsync(await Windows.Storage.StorageFile.GetFileFromPathAsync(path));
            return;
        }

        ImageLightbox.Show(XamlRoot, name, path);
    }

    private async Task ShowPreviewDialogAsync(string title, UIElement content)
    {
        var dialog = new ContentDialog
        {
            XamlRoot = XamlRoot,
            Title = title,
            Content = content,
            CloseButtonText = "Close",
            DefaultButton = ContentDialogButton.Close,
        };

        try
        {
            await dialog.ShowAsync();
        }
        catch (System.Runtime.InteropServices.COMException)
        {
            // Another dialog is already open; WinUI allows one at a time.
        }
    }
}
