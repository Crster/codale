using System.Collections.Specialized;
using System.ComponentModel;

using Codale.App.Controls;
using Codale.App.Services;
using Codale.App.ViewModels;
using Codale.Core.Text;

using Microsoft.UI.Input;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Controls.Primitives;
using Microsoft.UI.Xaml.Input;
using Microsoft.UI.Xaml.Media;

using Windows.System;
using Windows.UI.Core;

namespace Codale.App.Views;

/// <summary>
/// A notebook terminal in a centre tab, created on demand from the + button. Commands are typed into
/// the input cell at the end of the flow; each becomes a <see cref="CommandBlockView"/> above it,
/// running in a process of its own while the folder and environment carry over (see
/// <see cref="TerminalViewModel"/>). Every block can be regular, two-column or one-and-a-half
/// sized, or maximized into a tab of its own. Closing the tab ends every block still running.
/// An agent CLI gets a <see cref="CliTerminalTab"/> instead.
/// </summary>
public sealed partial class TerminalTab : UserControl, ITerminalHost
{
    /// <summary>Output views this far outside the visible area are released; closer ones are kept.</summary>
    private const double RealizeMargin = 800;

    private readonly List<CommandBlockView> _views = [];
    private TerminalViewModel? _viewModel;
    private CommandBlockViewModel? _target;
    private bool _stickToBottom = true;
    private bool _closed;

    private IReadOnlyList<string> _history = [];
    private int _historyIndex = -1;
    private string _draft = "";
    /// <summary>
    /// The text this view last put in the input itself (history recall, a suggestion, a clear).
    /// TextChanged arrives asynchronously, after any flag set around the assignment is long gone, so
    /// the change is recognised by its text instead - typing is what resets history browsing.
    /// </summary>
    private string? _programmaticText;
    private List<string> _suggestions = [];
    private int _suggestionIndex;

    public TerminalTab()
    {
        InitializeComponent();
        Loaded += OnLoaded;

        Input.FontFamily = TerminalLook.MonoFont;
        Input.FontSize = TerminalLook.MonoSize;
        PromptGlyph.FontFamily = TerminalLook.MonoFont;
        PromptGlyph.FontSize = TerminalLook.MonoSize;

        Scroller.ViewChanged += (_, _) => OnScrolled();
        Scroller.SizeChanged += (_, _) => UpdateFillHeight();
        InputCell.SizeChanged += (_, _) => UpdateFillHeight();
        Notebook.SizeChanged += (_, _) => KeepInputInView();

        // A click on the notebook's empty space goes to the input.
        Scroller.AddHandler(PointerPressedEvent, new PointerEventHandler(OnScrollerPointerPressed), handledEventsToo: false);
    }

    /// <summary>A URL or file path in the output was Ctrl+clicked.</summary>
    public event EventHandler<LinkMatch>? LinkActivated;

    /// <summary>"Maximize in new tab": the workspace moves the block into a terminal tab of its own.</summary>
    public event EventHandler<CommandBlockViewModel>? BlockPopOutRequested;

    /// <summary>A block asked to dock beside the centre (its header menu); the workspace owns the dock.</summary>
    public event EventHandler<(CommandBlockViewModel Block, BlockDock Dock)>? BlockDockRequested;

    /// <summary>A block's header is being dragged: the workspace shows where it can dock.</summary>
    public event EventHandler<CommandBlockViewModel>? BlockDragStarted;

    /// <summary>The header drag ended.</summary>
    public event EventHandler<CommandBlockViewModel>? BlockDragEnded;

    public TerminalViewModel ViewModel
    {
        get => _viewModel!;
        set
        {
            Unsubscribe();
            _viewModel = value;
            _viewModel.Blocks.CollectionChanged += OnBlocksChanged;
            _viewModel.PropertyChanged += OnViewModelPropertyChanged;
            _history = [];
            ApplySingleBlockMode();
            foreach (var block in _viewModel.Blocks)
            {
                AddView(block, _views.Count);
            }

            UpdateContext();
        }
    }

    /// <summary>
    /// Tears the display down. Called only when the tab is actually closing - a TabViewItem's content
    /// leaves the visual tree whenever another tab is selected, so Unloaded is tab-hide, not
    /// tab-close, and must not tear the views down or the blocks would come back blank.
    /// </summary>
    public void CloseHost()
    {
        _closed = true;
        Unsubscribe();
        foreach (var view in _views)
        {
            CloseView(view);
        }

        _views.Clear();
    }

    /// <summary>Live-applies Settings changes (font size, scrollback, block height) to every block.</summary>
    public void ApplyAppSettings()
    {
        Input.FontFamily = TerminalLook.MonoFont;
        Input.FontSize = TerminalLook.MonoSize;
        PromptGlyph.FontFamily = TerminalLook.MonoFont;
        PromptGlyph.FontSize = TerminalLook.MonoSize;
        foreach (var view in _views)
        {
            view.ApplyAppSettings();
        }
    }

    /// <summary>The tab header's menu: notebook-wide actions.</summary>
    public void ShowContextMenu(UIElement anchor, ContextRequestedEventArgs args)
    {
        args.Handled = true;
        if (_viewModel is not { } viewModel)
        {
            return;
        }

        var menu = new MenuFlyout();
        var copyAll = new MenuFlyoutItem { Text = "Copy all output", Icon = new FontIcon { Glyph = "" } };
        copyAll.Click += (_, _) => _ = ClipboardWriter.SetTextAsync(viewModel.Transcript.Read().Text);
        menu.Items.Add(copyAll);

        var clear = new MenuFlyoutItem { Text = "Clear finished blocks", Icon = new SymbolIcon(Symbol.Clear) };
        ToolTipService.SetToolTip(clear, "Remove every finished command from the history. Running ones stay.");
        clear.Click += (_, _) => viewModel.ClearFinishedBlocks();

        // A maximized block's tab is that one command; clearing it would leave an empty tab.
        if (!viewModel.IsSingleBlock)
        {
            menu.Items.Add(clear);
        }

        if (viewModel.Blocks.Any(b => b.IsRunning))
        {
            var stop = new MenuFlyoutItem { Text = "Stop all running", Icon = new FontIcon { Glyph = "" } };
            stop.Click += (_, _) =>
            {
                foreach (var block in viewModel.Blocks.Where(b => b.IsRunning).ToList())
                {
                    _ = block.StopAsync();
                }
            };
            menu.Items.Add(stop);
        }

        if (viewModel.IsCommandTerminal)
        {
            menu.Items.Add(new MenuFlyoutSeparator());
            var restart = new MenuFlyoutItem { Text = "Restart Command", Icon = new SymbolIcon(Symbol.Refresh) };
            ToolTipService.SetToolTip(restart, "Stop what still runs, clear the history and run the project command again.");
            restart.Click += async (_, _) => await viewModel.RefreshAsync();
            menu.Items.Add(restart);
        }

        menu.ShowAt(anchor, new FlyoutShowOptions { Position = args.TryGetPosition(anchor, out var position) ? position : null });
    }

    private void OnLoaded(object sender, RoutedEventArgs e)
    {
        // Loaded fires again every time the tab is selected (its content leaves the tree when
        // hidden), so the input takes the keyboard on each switch. Queued behind the tab strip's
        // own focus handling, which would otherwise win.
        DispatcherQueue.TryEnqueue(Microsoft.UI.Dispatching.DispatcherQueuePriority.Low, () =>
        {
            if (IsSingleBlock)
            {
                // A plain terminal: the keyboard goes straight to the command.
                _views.FirstOrDefault()?.FocusOutput();
            }
            else if (_target is null || !FocusRunningFullScreen())
            {
                Input.Focus(FocusState.Programmatic);
            }

            UpdateFillHeight();
            UpdateRealization();
        });
    }

    private bool FocusRunningFullScreen()
    {
        if (_views.FirstOrDefault(v => v.Block.InAlternate && v.Block.IsRunning) is { } view)
        {
            view.FocusOutput();
            return true;
        }

        return false;
    }

    // ------------------------------------------------------------------ blocks

    private void OnBlocksChanged(object? sender, NotifyCollectionChangedEventArgs e)
    {
        if (_closed)
        {
            return;
        }

        switch (e.Action)
        {
            case NotifyCollectionChangedAction.Add:
                var index = e.NewStartingIndex;
                foreach (CommandBlockViewModel block in e.NewItems!)
                {
                    AddView(block, index < 0 ? _views.Count : index++);
                }

                break;
            case NotifyCollectionChangedAction.Remove:
                foreach (CommandBlockViewModel block in e.OldItems!)
                {
                    RemoveView(block);
                }

                break;
            default:
                foreach (var view in _views.ToList())
                {
                    if (!_viewModel!.Blocks.Contains(view.Block))
                    {
                        RemoveView(view.Block);
                    }
                }

                break;
        }

        UpdateContext();
    }

    private void AddView(CommandBlockViewModel block, int index)
    {
        var view = new CommandBlockView(block);
        view.Selected += OnBlockSelected;
        view.PopOutRequested += OnBlockPopOut;
        view.RemoveRequested += OnBlockRemove;
        view.RerunRequested += OnBlockRerun;
        view.LinkActivated += OnBlockLink;
        view.LayoutChanged += OnBlockLayoutChanged;
        view.DockRequested += OnBlockDockRequested;
        view.DragStarted += OnBlockDragStarted;
        view.DragEnded += OnBlockDragEnded;
        block.Exited += OnBlockExited;

        index = Math.Clamp(index, 0, _views.Count);
        _views.Insert(index, view);

        // The input cell stays the last child.
        Notebook.Children.Insert(index, view);
    }

    private void RemoveView(CommandBlockViewModel block)
    {
        if (_views.FirstOrDefault(v => ReferenceEquals(v.Block, block)) is not { } view)
        {
            return;
        }

        _views.Remove(view);
        Notebook.Children.Remove(view);
        CloseView(view);
        if (ReferenceEquals(_target, block))
        {
            SetTarget(null);
        }
    }

    private void CloseView(CommandBlockView view)
    {
        view.Selected -= OnBlockSelected;
        view.PopOutRequested -= OnBlockPopOut;
        view.RemoveRequested -= OnBlockRemove;
        view.RerunRequested -= OnBlockRerun;
        view.LinkActivated -= OnBlockLink;
        view.LayoutChanged -= OnBlockLayoutChanged;
        view.DockRequested -= OnBlockDockRequested;
        view.DragStarted -= OnBlockDragStarted;
        view.DragEnded -= OnBlockDragEnded;
        view.Block.Exited -= OnBlockExited;
        view.Close();
    }

    private void OnBlockSelected(object? sender, CommandBlockViewModel block) =>
        SetTarget(block.IsRunning && !ReferenceEquals(_target, block) ? block : null);

    private void OnBlockPopOut(object? sender, CommandBlockViewModel block)
    {
        // A block already alone in its tab has nowhere bigger to go.
        if (block.Size == BlockSize.Fill && _viewModel?.Blocks.Count == 1)
        {
            return;
        }

        BlockPopOutRequested?.Invoke(this, block);
    }

    private void OnBlockRemove(object? sender, CommandBlockViewModel block)
    {
        if (block.IsRunning)
        {
            _ = StopThenRemoveAsync(block);
            return;
        }

        _viewModel?.RemoveBlock(block);
        Input.Focus(FocusState.Programmatic);
    }

    private async Task StopThenRemoveAsync(CommandBlockViewModel block)
    {
        await block.StopAsync();
        _viewModel?.RemoveBlock(block);
    }

    private void OnBlockRerun(object? sender, CommandBlockViewModel block)
    {
        _stickToBottom = true;
        _viewModel?.Rerun(block);
        Input.Focus(FocusState.Programmatic);
    }

    private void OnBlockLink(object? sender, LinkMatch link) => LinkActivated?.Invoke(this, link);

    private void OnBlockDockRequested(object? sender, (CommandBlockViewModel Block, BlockDock Dock) request) =>
        BlockDockRequested?.Invoke(this, request);

    private void OnBlockDragStarted(object? sender, CommandBlockViewModel block) => BlockDragStarted?.Invoke(this, block);

    private void OnBlockDragEnded(object? sender, CommandBlockViewModel block) => BlockDragEnded?.Invoke(this, block);

    private void OnBlockLayoutChanged(object? sender, EventArgs e)
    {
        Notebook.InvalidateMeasure();
        DispatcherQueue.TryEnqueue(UpdateRealization);
    }

    private void OnBlockExited(object? sender, EventArgs e)
    {
        if (ReferenceEquals(sender, _target))
        {
            SetTarget(null);
            Input.Focus(FocusState.Programmatic);
        }
    }

    private void OnViewModelPropertyChanged(object? sender, PropertyChangedEventArgs e)
    {
        if (e.PropertyName == nameof(TerminalViewModel.State))
        {
            UpdateContext();
        }
    }

    /// <summary>Where the next command runs, and which shell runs it.</summary>
    private void UpdateContext()
    {
        if (_viewModel is not { } viewModel)
        {
            return;
        }

        CwdText.Text = CommandBlockView.ShortPath(viewModel.State.Cwd);
        ToolTipService.SetToolTip(CwdText, viewModel.State.Cwd);
        ShellText.Text = TerminalLook.ShellLabel;
    }

    // ------------------------------------------------------------------ target

    /// <summary>
    /// Points the input at a running block: lines then go to its stdin (a y/n prompt, a REPL) instead
    /// of starting new commands. Null returns to running new commands.
    /// </summary>
    private void SetTarget(CommandBlockViewModel? block)
    {
        _target = block;
        foreach (var view in _views)
        {
            view.IsSelected = ReferenceEquals(view.Block, block);
        }

        if (block is null)
        {
            TargetChip.Visibility = Visibility.Collapsed;
            PromptGlyph.Text = "❯";
        }
        else
        {
            var first = block.Command.Split('\n')[0];
            TargetText.Text = "→ " + (first.Length > 40 ? first[..40] + "…" : first);
            TargetChip.Visibility = Visibility.Visible;
            PromptGlyph.Text = "→";

            // Queued: the click that selected the block is still being handled, and the header's
            // selectable text would take the focus back from an immediate call.
            DispatcherQueue.TryEnqueue(Microsoft.UI.Dispatching.DispatcherQueuePriority.Low,
                () => Input.Focus(FocusState.Programmatic));
        }
    }

    private void OnTargetChipClick(object sender, RoutedEventArgs e)
    {
        SetTarget(null);
        Input.Focus(FocusState.Programmatic);
    }

    // ------------------------------------------------------------------ input

    private bool _translatingCommand;

    private async void Submit()
    {
        if (_viewModel is not { } viewModel)
        {
            return;
        }

        var text = Input.Text.Replace("\r\n", "\n").Replace('\r', '\n');
        if (_target is { IsRunning: true } target)
        {
            // A line for the running command: sent as typed, Enter included.
            _ = target.WriteAsync(text.Replace("\n", "\r") + "\r");
            SetInput("");
            return;
        }

        if (text.Trim().Length == 0)
        {
            return;
        }

        if (_translatingCommand) return;
        if (AppSettings.SmartFeatureEnabled("shell"))
        {
            _translatingCommand = true;
            Input.IsEnabled = false;
            try
            {
                var cwd = viewModel.State.Cwd;
                var shell = Codale.Terminal.TerminalSession.ResolveShell(AppSettings.TerminalShell);
                var translated = await SmartExperienceHost.Instance.ShellCommandAsync(
                    new(cwd, cwd, "shell"), text, shell);
                if (translated?.Value is not { Length: > 0 } command)
                {
                    Input.PlaceholderText = "Smart could not translate this request. Try again.";
                    return;
                }
                if (!IsLoaded || !ReferenceEquals(_viewModel, viewModel)) return;
                text = command;
            }
            catch (Exception)
            {
                Input.PlaceholderText = "Smart could not translate this request. Check Developer Tools settings.";
                return;
            }
            finally
            {
                _translatingCommand = false;
                Input.IsEnabled = true;
                Input.Focus(FocusState.Programmatic);
            }
        }

        _stickToBottom = true;
        SetInput("");
        _history = [];
        _historyIndex = -1;
        HideSuggestions();
        viewModel.RunCommand(text);
        KeepInputInView();
    }

    private void SetInput(string text)
    {
        _programmaticText = text;
        Input.Text = text;
        Input.SelectionStart = text.Length;
    }

    private void OnInputPreviewKeyDown(object sender, KeyRoutedEventArgs e)
    {
        var shift = IsDown(VirtualKey.Shift);
        var ctrl = IsDown(VirtualKey.Control);

        switch (e.Key)
        {
            case VirtualKey.Enter when !shift:
                e.Handled = true;
                Submit();
                return;

            case VirtualKey.Tab when SuggestionList.Visibility == Visibility.Visible && !shift && !ctrl:
                e.Handled = true;
                AcceptSuggestion(_suggestionIndex);
                return;

            case VirtualKey.Escape:
                if (SuggestionList.Visibility == Visibility.Visible)
                {
                    HideSuggestions();
                }
                else if (_target is not null)
                {
                    SetTarget(null);
                }
                else if (_historyIndex >= 0)
                {
                    _historyIndex = -1;
                    SetInput(_draft);
                }

                e.Handled = true;
                return;

            case VirtualKey.C when ctrl && Input.SelectionLength == 0:
                // Ctrl+C with nothing selected interrupts, like a shell: the targeted block, else the newest running one.
                if ((_target ?? _viewModel?.NewestRunningBlock) is { } running)
                {
                    _ = running.WriteAsync("\x03");
                    e.Handled = true;
                }

                return;

            case VirtualKey.Up when !shift && !ctrl:
                if (SuggestionList.Visibility == Visibility.Visible)
                {
                    MoveSuggestion(-1);
                    e.Handled = true;
                }
                else if (!Input.Text[..Input.SelectionStart].Contains('\r'))
                {
                    // Only from the first line, so Up still moves within a multi-line command.
                    e.Handled = StepHistory(+1);
                }

                return;

            case VirtualKey.Down when !shift && !ctrl:
                if (SuggestionList.Visibility == Visibility.Visible)
                {
                    MoveSuggestion(+1);
                    e.Handled = true;
                }
                else if (_historyIndex >= 0 && !Input.Text[Input.SelectionStart..].Contains('\r'))
                {
                    e.Handled = StepHistory(-1);
                }

                return;
        }
    }

    /// <summary>Walks the project's command history: +1 older, -1 newer; past the newest is the draft again.</summary>
    private bool StepHistory(int direction)
    {
        if (_viewModel is not { } viewModel)
        {
            return false;
        }

        if (_historyIndex < 0)
        {
            _history = CommandHistory.Recent(viewModel.HistoryKey);
            _draft = Input.Text;
        }

        var next = _historyIndex + direction;
        if (next >= _history.Count)
        {
            return true;
        }

        _historyIndex = Math.Max(-1, next);
        HideSuggestions();
        SetInput(_historyIndex < 0 ? _draft : _history[_historyIndex]);
        return true;
    }

    private void OnInputTextChanged(object sender, TextChangedEventArgs e)
    {
        if (_viewModel is not { } viewModel)
        {
            return;
        }

        if (_programmaticText is not null && string.Equals(Input.Text, _programmaticText, StringComparison.Ordinal))
        {
            _programmaticText = null;
            return;
        }

        _programmaticText = null;

        _historyIndex = -1;
        var typed = Input.Text;
        if (typed.Trim().Length == 0 || typed.Contains('\r') || _target is not null)
        {
            HideSuggestions();
            return;
        }

        _suggestions = CommandHistory.Suggest(viewModel.HistoryKey, typed)
            .Where(s => !string.Equals(s, typed, StringComparison.Ordinal))
            .Take(5)
            .ToList();
        _suggestionIndex = 0;
        RenderSuggestions();
    }

    private void RenderSuggestions()
    {
        SuggestionList.Children.Clear();
        if (_suggestions.Count == 0)
        {
            SuggestionList.Visibility = Visibility.Collapsed;
            return;
        }

        for (var i = 0; i < _suggestions.Count; i++)
        {
            var index = i;
            var row = new Button
            {
                Style = (Style)Application.Current.Resources["ShellTextButton"],
                HorizontalAlignment = HorizontalAlignment.Stretch,
                HorizontalContentAlignment = HorizontalAlignment.Left,
                Padding = new Thickness(8, 0, 8, 0),
                Height = 24,
                Content = new TextBlock
                {
                    Text = _suggestions[i],
                    FontFamily = Input.FontFamily,
                    FontSize = 12,
                    Foreground = TerminalLook.Brush(i == _suggestionIndex ? TerminalLook.Command : TerminalLook.Muted),
                    TextTrimming = TextTrimming.CharacterEllipsis,
                },
                Background = i == _suggestionIndex
                    ? TerminalLook.Brush(TerminalLook.Selected)
                    : new SolidColorBrush(Microsoft.UI.Colors.Transparent),
            };
            if (i == _suggestionIndex)
            {
                ToolTipService.SetToolTip(row, "Tab to use");
            }

            row.Click += (_, _) => AcceptSuggestion(index);
            SuggestionList.Children.Add(row);
        }

        SuggestionList.Visibility = Visibility.Visible;
    }

    private void MoveSuggestion(int delta)
    {
        _suggestionIndex = (_suggestionIndex + delta + _suggestions.Count) % _suggestions.Count;
        RenderSuggestions();
    }

    private void AcceptSuggestion(int index)
    {
        if (index >= 0 && index < _suggestions.Count)
        {
            SetInput(_suggestions[index]);
        }

        HideSuggestions();
        Input.Focus(FocusState.Programmatic);
    }

    private void HideSuggestions()
    {
        _suggestions = [];
        SuggestionList.Children.Clear();
        SuggestionList.Visibility = Visibility.Collapsed;
    }

    private void OnScrollerPointerPressed(object sender, PointerRoutedEventArgs e)
    {
        // Only a press on the notebook's own background; blocks and the input handle theirs.
        if (ReferenceEquals(e.OriginalSource, Notebook) || ReferenceEquals(e.OriginalSource, Scroller))
        {
            Input.Focus(FocusState.Programmatic);
        }
    }

    private static bool IsDown(VirtualKey key) =>
        InputKeyboardSource.GetKeyStateForCurrentThread(key).HasFlag(CoreVirtualKeyStates.Down);

    // ------------------------------------------------------------------ scrolling and realization

    private void OnScrolled()
    {
        _stickToBottom = Scroller.VerticalOffset >= Scroller.ScrollableHeight - 24;
        UpdateRealization();
    }

    /// <summary>While the user is at the bottom, growing output keeps the input cell in view, like a chat.</summary>
    private void KeepInputInView()
    {
        if (_stickToBottom && !_closed)
        {
            Scroller.UpdateLayout();
            Scroller.ChangeView(null, Scroller.ScrollableHeight, null, disableAnimation: true);
        }
    }

    /// <summary>A block that fills (alone in its tab, or a full-screen app) gets the visible height, less the input cell.</summary>
    private void UpdateFillHeight()
    {
        var height = IsSingleBlock
            ? Scroller.ViewportHeight
            : Scroller.ViewportHeight - Scroller.Padding.Top - Scroller.Padding.Bottom - InputCell.ActualHeight - 4;
        Notebook.FillHeight = Math.Max(IsSingleBlock ? 1 : 160, height);
    }

    private bool IsSingleBlock => _viewModel?.IsSingleBlock == true;

    /// <summary>
    /// A maximized block's tab looks like the classic terminal: the output edge to edge, no input
    /// cell, no padding and no notebook scrolling (the output scrolls itself).
    /// </summary>
    private void ApplySingleBlockMode()
    {
        if (!IsSingleBlock)
        {
            return;
        }

        InputCell.Visibility = Visibility.Collapsed;
        Scroller.Padding = new Thickness(0);
        Scroller.VerticalScrollBarVisibility = ScrollBarVisibility.Disabled;
        Scroller.VerticalScrollMode = ScrollMode.Disabled;
    }

    /// <summary>Keeps output views only for blocks on or near the screen.</summary>
    private void UpdateRealization()
    {
        if (_closed || Scroller.ViewportHeight <= 0)
        {
            return;
        }

        var top = Scroller.VerticalOffset - RealizeMargin;
        var bottom = Scroller.VerticalOffset + Scroller.ViewportHeight + RealizeMargin;
        foreach (var view in _views)
        {
            try
            {
                var y = view.TransformToVisual(Notebook).TransformPoint(default).Y;
                // A docked block is drawn by the dock's own view; its notebook view stays released.
                view.Realize(view.Block.Dock == BlockDock.None && y + view.ActualHeight >= top && y <= bottom);
            }
            catch (ArgumentException)
            {
                // Not in the tree yet; it is realized on creation.
            }
        }
    }

    private void Unsubscribe()
    {
        if (_viewModel is null)
        {
            return;
        }

        _viewModel.Blocks.CollectionChanged -= OnBlocksChanged;
        _viewModel.PropertyChanged -= OnViewModelPropertyChanged;
    }
}
