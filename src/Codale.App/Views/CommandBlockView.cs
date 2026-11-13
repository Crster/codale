using System.ComponentModel;

using Codale.App.Controls;
using Codale.App.Services;
using Codale.App.ViewModels;
using Codale.Core.Text;

using Microsoft.UI.Dispatching;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Controls.Primitives;
using Microsoft.UI.Xaml.Input;
using Microsoft.UI.Xaml.Media;

namespace Codale.App.Views;

/// <summary>
/// One command of the notebook terminal, drawn like a native terminal: a prompt line - the result,
/// the folder and how long it took as quiet text, then the command - with the command's output right
/// under it, on the same black. The output is a block-mode <see cref="TerminalView"/> drawing the
/// block's own emulator: as tall as the output, up to the block height, scrolling inside beyond
/// that. Clicking into the output types straight into the running command. Off-screen, the output
/// view is released (the body keeps its height) and rebuilt from the emulator when it scrolls back -
/// a Win2D surface per block would add up.
/// </summary>
public sealed partial class CommandBlockView : UserControl, INotebookCell
{
    private readonly Grid _root = new();
    private readonly Grid _header = new();

    // The prompt line: result, folder and duration as quiet text; the command; on the right the
    // shell and when it ran. Actions appear on hover.
    private readonly Grid _statusHost = new() { Width = 14, Height = 14, VerticalAlignment = VerticalAlignment.Center };
    private readonly FontIcon _statusGlyph = new() { FontSize = 10, HorizontalAlignment = HorizontalAlignment.Center, VerticalAlignment = VerticalAlignment.Center };
    private readonly ProgressRing _statusRing = new() { IsActive = true, Width = 10, Height = 10, MinWidth = 0, MinHeight = 0, Foreground = TerminalLook.Brush(TerminalLook.Accent) };
    private readonly TextBlock _where = new() { FontSize = 11.5, VerticalAlignment = VerticalAlignment.Center, TextTrimming = TextTrimming.CharacterEllipsis, MaxWidth = 280 };
    private readonly TextBlock _timing = new() { FontSize = 11.5, VerticalAlignment = VerticalAlignment.Center };
    private readonly TextBlock _result = new() { FontSize = 11.5, VerticalAlignment = VerticalAlignment.Center };
    private readonly TextBlock _command = new();
    private readonly TextBlock _right = new() { FontSize = 11.5, VerticalAlignment = VerticalAlignment.Center };
    private readonly StackPanel _actions = new() { Orientation = Orientation.Horizontal, Margin = new Thickness(0, 0, 6, 0) };
    private readonly Border _body = new() { MinHeight = 0 };
    private readonly Button _stopButton;
    private readonly DispatcherQueueTimer _clock;
    private TerminalView? _terminal;
    private double _lastBodyHeight;
    private bool _pointerOver;
    private bool _closed;

    /// <param name="block">The block shown.</param>
    /// <param name="docked">True for the view in the dock beside the centre: it fills its slot, and
    /// offers "back to its terminal" instead of being hidden while the block is docked.</param>
    public CommandBlockView(CommandBlockViewModel block, bool docked = false)
    {
        Block = block;
        IsDocked = docked;
        IsTabStop = false;
        RequestedTheme = ElementTheme.Dark;

        _root.Background = TerminalLook.Brush(TerminalLook.Background);
        _root.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
        _root.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
        _root.Padding = new Thickness(0, 0, 0, docked ? 0 : 6);

        // ---- the prompt line
        _header.Padding = new Thickness(8, 3, 0, 3);
        _header.ColumnSpacing = 8;
        _header.CornerRadius = new CornerRadius(4);
        _header.Background = TerminalLook.Brush(TerminalLook.Background);
        _header.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
        _header.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
        _header.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
        _header.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });

        _statusHost.Children.Add(_statusGlyph);
        _statusHost.Children.Add(_statusRing);
        _where.Text = ShortPath(block.Cwd);
        _where.Foreground = TerminalLook.Brush(TerminalLook.Muted);
        ToolTipService.SetToolTip(_where, block.Cwd);
        _timing.Foreground = TerminalLook.Brush(TerminalLook.Faint);
        _result.Foreground = TerminalLook.Brush(TerminalLook.Failure);
        var prompt = new TextBlock
        {
            Text = "❯",
            FontFamily = TerminalLook.MonoFont,
            FontSize = TerminalLook.MonoSize,
            Foreground = TerminalLook.Brush(TerminalLook.Accent),
            VerticalAlignment = VerticalAlignment.Center,
        };
        var left = new StackPanel { Orientation = Orientation.Horizontal, Spacing = 7, VerticalAlignment = VerticalAlignment.Center };
        left.Children.Add(_statusHost);
        left.Children.Add(_where);
        left.Children.Add(_timing);
        left.Children.Add(_result);
        left.Children.Add(prompt);

        _command.FontFamily = TerminalLook.MonoFont;
        _command.FontSize = TerminalLook.MonoSize;
        _command.Foreground = TerminalLook.Brush(TerminalLook.Command);
        _command.TextTrimming = TextTrimming.CharacterEllipsis;
        _command.TextWrapping = TextWrapping.NoWrap;
        _command.VerticalAlignment = VerticalAlignment.Center;
        // Not selectable: the prompt line is the block's drag handle (Copy command copies the text).
        _command.IsTextSelectionEnabled = false;
        var firstLine = block.Command.Split('\n')[0].TrimEnd('\r');
        _command.Text = firstLine.Length < block.Command.Length ? firstLine + " …" : firstLine;
        ToolTipService.SetToolTip(_command, block.Command);
        Grid.SetColumn(_command, 1);

        _actions.Children.Add(IconButton("", "Copy command", (_, _) => _ = ClipboardWriter.SetTextAsync(Block.Command)));
        _actions.Children.Add(IconButton("", "Copy output", (_, _) => _ = ClipboardWriter.SetTextAsync(Block.OutputText)));
        _actions.Children.Add(IconButton("", "Run again", (_, _) => RerunRequested?.Invoke(this, Block)));
        _stopButton = IconButton("", "Stop (Ctrl+C)", (_, _) => _ = Block.StopAsync());
        _actions.Children.Add(_stopButton);
        if (docked)
        {
            _actions.Children.Add(IconButton("", "Back to its terminal", (_, _) => UndockRequested?.Invoke(this, Block)));
        }

        var dock = IconButton("", "Dock to the right - or drag this line to the right edge", (_, _) => { });
        dock.Flyout = BuildDockMenu();
        _actions.Children.Add(dock);
        _actions.Children.Add(IconButton("", "Maximize in a new tab", (_, _) => PopOutRequested?.Invoke(this, Block)));
        _actions.Children.Add(IconButton("", "Remove from history (stops it if running)", (_, _) => RemoveRequested?.Invoke(this, Block)));
        Grid.SetColumn(_actions, 2);

        _right.Foreground = TerminalLook.Brush(TerminalLook.Faint);
        _right.Margin = new Thickness(0, 0, 10, 0);
        Grid.SetColumn(_right, 3);

        _header.Children.Add(left);
        _header.Children.Add(_command);
        _header.Children.Add(_actions);
        _header.Children.Add(_right);

        // A tap, not a press: pressing the line may be the start of a drag to the dock. Presses on
        // the line's own buttons are theirs, not a selection.
        _header.AddHandler(TappedEvent, new TappedEventHandler((_, e) =>
        {
            if (!IsDocked && !IsInsideButton(e.OriginalSource as DependencyObject))
            {
                Selected?.Invoke(this, Block);
            }
        }), handledEventsToo: true);
        _header.ContextRequested += OnHeaderContextRequested;

        // Dragging the prompt line to the right edge of the centre docks the block there.
        _header.CanDrag = true;
        _header.DragStarting += (_, e) =>
        {
            e.Data.SetText(Block.Command);
            e.Data.RequestedOperation = Windows.ApplicationModel.DataTransfer.DataPackageOperation.Move;
            e.Data.Properties["CodaleBlock"] = true;
            DragStarted?.Invoke(this, Block);
        };
        _header.DropCompleted += (_, _) => DragEnded?.Invoke(this, Block);

        // A block maximized into a tab of its own is just a terminal: no prompt line above its output.
        IsBare = !docked && block.Size == BlockSize.Fill;
        if (IsBare)
        {
            _root.Padding = new Thickness(0);
            _header.Visibility = Visibility.Collapsed;
        }

        Grid.SetRow(_body, 1);
        _root.Children.Add(_header);
        _root.Children.Add(_body);
        Content = _root;

        PointerEntered += (_, _) => { _pointerOver = true; UpdateChrome(); };
        PointerExited += (_, _) => { _pointerOver = false; UpdateChrome(); };

        _clock = DispatcherQueue.GetForCurrentThread().CreateTimer();
        _clock.Interval = TimeSpan.FromSeconds(1);
        _clock.Tick += (_, _) => UpdateTiming();

        Block.PropertyChanged += OnBlockPropertyChanged;
        ApplyDockVisibility();
        UpdateStatus();
        UpdateChrome();
    }

    public CommandBlockViewModel Block { get; }

    /// <summary>True for a block maximized into its own tab: the output alone, no prompt line.</summary>
    public bool IsBare { get; }

    /// <summary>True for the view in the dock column (as opposed to the one in the block's notebook).</summary>
    public bool IsDocked { get; }

    public bool FillsViewport => IsDocked || Block.Size == BlockSize.Fill || Block.InAlternate;

    /// <summary>"Dock right / top right / bottom right" from the line's menu.</summary>
    public event EventHandler<(CommandBlockViewModel Block, BlockDock Dock)>? DockRequested;

    /// <summary>The docked view's "back to its terminal".</summary>
    public event EventHandler<CommandBlockViewModel>? UndockRequested;

    /// <summary>The prompt line started being dragged: the workspace shows where it can dock.</summary>
    public event EventHandler<CommandBlockViewModel>? DragStarted;

    /// <summary>The drag ended, dropped or not.</summary>
    public event EventHandler<CommandBlockViewModel>? DragEnded;

    /// <summary>The prompt line was clicked: the notebook selects the block (its input then types to it while it runs).</summary>
    public event EventHandler<CommandBlockViewModel>? Selected;

    public event EventHandler<CommandBlockViewModel>? PopOutRequested;

    public event EventHandler<CommandBlockViewModel>? RemoveRequested;

    public event EventHandler<CommandBlockViewModel>? RerunRequested;

    /// <summary>A URL or file path in the output was Ctrl+clicked.</summary>
    public event EventHandler<LinkMatch>? LinkActivated;

    /// <summary>Raised when the block's place in the flow may have changed (full screen, output height, docking).</summary>
    public event EventHandler? LayoutChanged;

    /// <summary>Highlights the prompt line while the notebook's input targets this block.</summary>
    public bool IsSelected
    {
        get => _isSelected;
        set
        {
            if (_isSelected != value)
            {
                _isSelected = value;
                UpdateChrome();
            }
        }
    }

    private bool _isSelected;

    /// <summary>The live output view, while the block is realized.</summary>
    public TerminalView? Terminal => _terminal;

    /// <summary>
    /// Builds (true) or releases (false) the output view. Released, the body keeps its last height so
    /// the flow does not jump, and the emulator keeps receiving output for when it comes back.
    /// </summary>
    public void Realize(bool realize)
    {
        if (_closed || realize == (_terminal is not null))
        {
            return;
        }

        if (!realize)
        {
            _lastBodyHeight = _terminal!.ActualHeight;
            DetachTerminal();
            _body.Height = FillsViewport ? double.NaN : _lastBodyHeight;
            return;
        }

        _body.ClearValue(HeightProperty);
        var terminal = new TerminalView();
        terminal.Response += OnTerminalInput;
        terminal.Resized += OnTerminalResized;
        terminal.LinkActivated += OnTerminalLink;
        terminal.BlockHeightChanged += OnTerminalHeightChanged;
        terminal.ContextRequested += OnTerminalContextRequested;
        _terminal = terminal;
        ApplyRows();
        terminal.ShowCursor = Block.IsRunning;
        terminal.Attach(Block.Emulator);
        _body.Child = terminal;
    }

    private void DetachTerminal()
    {
        if (_terminal is not { } terminal)
        {
            return;
        }

        terminal.Response -= OnTerminalInput;
        terminal.Resized -= OnTerminalResized;
        terminal.LinkActivated -= OnTerminalLink;
        terminal.BlockHeightChanged -= OnTerminalHeightChanged;
        terminal.ContextRequested -= OnTerminalContextRequested;
        _body.Child = null;
        terminal.Close();
        _terminal = null;
    }

    /// <summary>Sends the keyboard to the running command's output.</summary>
    public void FocusOutput()
    {
        // While docked, the dock's view has the output; the notebook's stays released.
        if (!IsDocked && Block.Dock != BlockDock.None)
        {
            return;
        }

        Realize(true);
        _terminal?.Focus(FocusState.Programmatic);
    }

    public void ApplyAppSettings()
    {
        _terminal?.ApplyAppSettings();
        _command.FontFamily = TerminalLook.MonoFont;
        _command.FontSize = TerminalLook.MonoSize;
        ApplyRows();
    }

    private bool IsInsideButton(DependencyObject? element)
    {
        for (var current = element; current is not null && !ReferenceEquals(current, _header); current = VisualTreeHelper.GetParent(current))
        {
            if (current is ButtonBase)
            {
                return true;
            }
        }

        return false;
    }

    /// <summary>Tears the view down for good (the block left this notebook, or the tab closed). The block itself lives on.</summary>
    public void Close()
    {
        if (_closed)
        {
            return;
        }

        _clock.Stop();
        Block.PropertyChanged -= OnBlockPropertyChanged;
        DetachTerminal();
        _closed = true;
    }

    // ------------------------------------------------------------------ layout

    /// <summary>Rows the output may take: the block height, or the whole height it is given when it fills.</summary>
    private void ApplyRows()
    {
        var fill = FillsViewport;
        _root.RowDefinitions[1].Height = fill ? new GridLength(1, GridUnitType.Star) : GridLength.Auto;
        if (_terminal is null)
        {
            return;
        }

        _terminal.BlockRows = fill ? 0 : AppSettings.TerminalBlockLines;
    }

    /// <summary>
    /// The notebook's view steps aside while its block is docked: hidden, and its output view released
    /// so the dock's view is the only one drawing (and sizing) the block's emulator.
    /// </summary>
    private void ApplyDockVisibility()
    {
        if (IsDocked)
        {
            Realize(true);
            return;
        }

        var docked = Block.Dock != BlockDock.None;
        if (docked)
        {
            Realize(false);
        }

        Visibility = docked ? Visibility.Collapsed : Visibility.Visible;
        if (!docked)
        {
            Realize(true);
        }

        (Parent as UIElement)?.InvalidateMeasure();
        LayoutChanged?.Invoke(this, EventArgs.Empty);
    }

    private MenuFlyout BuildDockMenu()
    {
        var menu = new MenuFlyout();
        AddDockItems(menu);
        return menu;
    }

    private void AddDockItems(MenuFlyout menu)
    {
        foreach (var (text, dock) in new[] { ("Dock right", BlockDock.Right), ("Dock top right", BlockDock.TopRight), ("Dock bottom right", BlockDock.BottomRight) })
        {
            var item = new MenuFlyoutItem { Text = text };
            item.Click += (_, _) => DockRequested?.Invoke(this, (Block, dock));
            menu.Items.Add(item);
        }
    }

    private void OnTerminalHeightChanged(object? sender, EventArgs e) => LayoutChanged?.Invoke(this, EventArgs.Empty);

    private void OnBlockPropertyChanged(object? sender, PropertyChangedEventArgs e)
    {
        switch (e.PropertyName)
        {
            case nameof(CommandBlockViewModel.Dock):
                ApplyDockVisibility();
                break;
            case nameof(CommandBlockViewModel.Size):
            case nameof(CommandBlockViewModel.InAlternate):
                ApplyRows();
                (Parent as UIElement)?.InvalidateMeasure();
                LayoutChanged?.Invoke(this, EventArgs.Empty);
                if (e.PropertyName == nameof(CommandBlockViewModel.InAlternate) && Block.InAlternate)
                {
                    // A full-screen app wants the keyboard; it gets it until it leaves the alternate screen.
                    DispatcherQueue.TryEnqueue(DispatcherQueuePriority.Low, FocusOutput);
                }

                break;
            case nameof(CommandBlockViewModel.Status):
                UpdateStatus();
                if (_terminal is not null)
                {
                    _terminal.ShowCursor = Block.IsRunning;
                }

                break;
        }
    }

    // ------------------------------------------------------------------ input and output

    private void OnTerminalInput(object? sender, string text) => _ = Block.WriteAsync(text);

    private void OnTerminalResized(object? sender, (int Cols, int Rows) size) => Block.Resize(size.Cols, size.Rows);

    private void OnTerminalLink(object? sender, LinkMatch link) => LinkActivated?.Invoke(this, link);

    private void OnTerminalContextRequested(UIElement sender, ContextRequestedEventArgs args)
    {
        if (_terminal is { } terminal)
        {
            TerminalContextMenu.Show(terminal, args);
        }
    }

    private void OnHeaderContextRequested(UIElement sender, ContextRequestedEventArgs args)
    {
        args.Handled = true;
        var menu = new MenuFlyout();
        Add("Copy command", "", () => _ = ClipboardWriter.SetTextAsync(Block.Command));
        Add("Copy output", "", () => _ = ClipboardWriter.SetTextAsync(Block.OutputText));
        Add("Run again", "", () => RerunRequested?.Invoke(this, Block));
        if (Block.IsRunning)
        {
            Add("Stop", "", () => _ = Block.StopAsync());
        }

        menu.Items.Add(new MenuFlyoutSeparator());
        AddDockItems(menu);
        if (IsDocked)
        {
            Add("Back to its terminal", "", () => UndockRequested?.Invoke(this, Block));
        }

        Add("Maximize in new tab", "", () => PopOutRequested?.Invoke(this, Block));
        menu.Items.Add(new MenuFlyoutSeparator());
        Add("Remove from history", "", () => RemoveRequested?.Invoke(this, Block));
        menu.ShowAt(_header, new FlyoutShowOptions { Position = args.TryGetPosition(_header, out var p) ? p : null });

        void Add(string text, string glyph, Action action)
        {
            var item = new MenuFlyoutItem { Text = text, Icon = new FontIcon { Glyph = glyph } };
            item.Click += (_, _) => action();
            menu.Items.Add(item);
        }
    }

    // ------------------------------------------------------------------ chrome

    private void UpdateChrome()
    {
        var showActions = _pointerOver || _isSelected || IsDocked;
        _actions.Opacity = showActions ? 1 : 0;
        _actions.IsHitTestVisible = showActions;
        _header.Background = TerminalLook.Brush(_isSelected ? TerminalLook.Selected
            : _pointerOver ? TerminalLook.Hover
            : TerminalLook.Background);
    }

    /// <summary>The result mark: a spinner while running, then a tick, a cross with the exit code, or "stopped".</summary>
    private void UpdateStatus()
    {
        _stopButton.Visibility = Block.IsRunning ? Visibility.Visible : Visibility.Collapsed;
        _statusRing.Visibility = Block.IsRunning ? Visibility.Visible : Visibility.Collapsed;
        _statusGlyph.Visibility = Block.IsRunning ? Visibility.Collapsed : Visibility.Visible;

        (_statusGlyph.Glyph, _statusGlyph.Foreground) = Block.Status switch
        {
            BlockStatus.Succeeded => ("", TerminalLook.Brush(TerminalLook.Success)),
            BlockStatus.Failed => ("", TerminalLook.Brush(TerminalLook.Failure)),
            _ => ("", TerminalLook.Brush(TerminalLook.Muted)),
        };

        _result.Text = Block.Status switch
        {
            BlockStatus.Failed => Block.ExitCode is { } code and not -1 ? $"exit {code}" : "failed",
            BlockStatus.Stopped => "stopped",
            _ => "",
        };
        _result.Foreground = TerminalLook.Brush(Block.Status == BlockStatus.Failed ? TerminalLook.Failure : TerminalLook.Muted);
        _result.Visibility = _result.Text.Length > 0 ? Visibility.Visible : Visibility.Collapsed;

        var started = Block.StartedAt == default ? DateTimeOffset.Now : Block.StartedAt;
        _right.Text = $"{TerminalLook.ShellLabel}  {started.ToLocalTime():HH:mm:ss}";

        if (Block.IsRunning)
        {
            _clock.Start();
        }
        else
        {
            _clock.Stop();
        }

        UpdateTiming();
    }

    private void UpdateTiming() => _timing.Text = FormatElapsed(Block.Elapsed);

    private static string FormatElapsed(TimeSpan elapsed) =>
        elapsed.TotalSeconds < 1 ? $"{Math.Max(0, elapsed.TotalMilliseconds):0}ms"
        : elapsed.TotalMinutes < 1 ? $"{elapsed.TotalSeconds:0.0}s"
        : elapsed.TotalHours < 1 ? $"{(int)elapsed.TotalMinutes}m {elapsed.Seconds}s"
        : $"{(int)elapsed.TotalHours}h {elapsed.Minutes}m";

    /// <summary>The last two folders of a path: enough to tell where a command ran without crowding the line.</summary>
    internal static string ShortPath(string path)
    {
        var trimmed = path.TrimEnd('\\', '/');
        var parts = trimmed.Split(['\\', '/'], StringSplitOptions.RemoveEmptyEntries);
        return parts.Length <= 2 ? trimmed : "…\\" + string.Join('\\', parts[^2..]);
    }

    private static Button IconButton(string glyph, string tip, RoutedEventHandler click)
    {
        var button = new Button
        {
            Style = (Style)Application.Current.Resources["ShellIconButton"],
            Width = 24,
            Height = 22,
            Foreground = TerminalLook.Brush(TerminalLook.Muted),
            Content = new FontIcon { Glyph = glyph, FontSize = 11 },
        };
        ToolTipService.SetToolTip(button, tip);
        Microsoft.UI.Xaml.Automation.AutomationProperties.SetName(button, tip);
        button.Click += click;
        return button;
    }

    /// <summary>
    /// A theme brush for the theme <paramref name="element"/> actually shows. Looking a key up on
    /// Application.Resources from code answers for the app's theme, which is not the page's when the
    /// user picked Light or Dark in Settings - text came out white on white. So the theme
    /// dictionaries are searched directly, by the element's ActualTheme.
    /// </summary>
    internal static Brush ThemeBrush(FrameworkElement element, string key)
    {
        var themes = element.ActualTheme == ElementTheme.Dark ? new[] { "Dark", "Default" } : new[] { "Light" };
        return FindThemed(Application.Current.Resources, key, themes) as Brush ?? Resource(key);
    }

    private static object? FindThemed(ResourceDictionary dictionary, string key, string[] themes)
    {
        foreach (var theme in themes)
        {
            if (dictionary.ThemeDictionaries.TryGetValue(theme, out var themed) &&
                themed is ResourceDictionary themedDictionary &&
                themedDictionary.TryGetValue(key, out var value))
            {
                return value;
            }
        }

        foreach (var merged in dictionary.MergedDictionaries)
        {
            if (FindThemed(merged, key, themes) is { } found)
            {
                return found;
            }
        }

        return null;
    }

    /// <summary>An app brush by key, for brushes that are the same in every theme.</summary>
    internal static Brush Resource(string key)
    {
        try
        {
            if (Application.Current.Resources[key] is Brush brush)
            {
                return brush;
            }
        }
        catch (Exception)
        {
            // Missing key: fall through to a neutral brush.
        }

        return new SolidColorBrush(Microsoft.UI.Colors.Gray);
    }
}
