using System.ComponentModel;

using Codale.App.Controls;
using Codale.App.Services;
using Codale.App.ViewModels;

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
    private readonly ProgressRing _statusRing = new() { IsActive = false, Width = 10, Height = 10, MinWidth = 0, MinHeight = 0, Foreground = TerminalLook.Brush(TerminalLook.Accent) };
    private readonly TextBlock _where = new() { FontSize = 11.5, VerticalAlignment = VerticalAlignment.Center, TextTrimming = TextTrimming.CharacterEllipsis };
    private readonly TextBlock _timing = new() { FontSize = 11.5, VerticalAlignment = VerticalAlignment.Center };
    private readonly TextBlock _result = new() { FontSize = 11.5, VerticalAlignment = VerticalAlignment.Center };
    private readonly TextBlock _command = new();
    private readonly TextBlock _right = new() { FontSize = 11.5, VerticalAlignment = VerticalAlignment.Center };
    private readonly StackPanel _actions = new() { Orientation = Orientation.Horizontal, Margin = new Thickness(0, 0, 6, 0) };
    private readonly Border _body = new() { MinHeight = 0 };
    private readonly TextBlock _shell = new() { FontSize = 11.5, VerticalAlignment = VerticalAlignment.Center };
    private readonly Button _stopButton;
    private readonly Button _rerunButton;
    private readonly DispatcherQueueTimer _clock;
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
        _root.Padding = new Thickness(0, 2, 0, docked ? 0 : 8);

        // ---- the prompt line, two rows like the input it came from:
        //   status  X:\full\path  1.2s  exit 1                            12:04:31
        //           pwsh > command                                      [run again] [more]
        _header.Padding = new Thickness(8, 4, 0, 2);
        _header.ColumnSpacing = 8;
        _header.RowSpacing = 1;
        _header.CornerRadius = new CornerRadius(4);
        _header.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
        _header.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
        _header.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
        _header.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
        _header.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });

        _statusHost.Children.Add(_statusGlyph);
        _statusHost.Children.Add(_statusRing);

        // Row 0: where it ran, how long, how it ended. Left-aligned, so the path trims before the rest.
        _where.Text = block.Cwd;
        _where.Foreground = TerminalLook.Brush(TerminalLook.Muted);
        ToolTipService.SetToolTip(_where, block.Cwd);
        _timing.Foreground = TerminalLook.Brush(TerminalLook.Faint);
        _result.Foreground = TerminalLook.Brush(TerminalLook.Failure);
        var meta = new Grid { ColumnSpacing = 8, HorizontalAlignment = HorizontalAlignment.Left, VerticalAlignment = VerticalAlignment.Center };
        meta.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
        meta.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
        meta.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
        Grid.SetColumn(_timing, 1);
        Grid.SetColumn(_result, 2);
        meta.Children.Add(_where);
        meta.Children.Add(_timing);
        meta.Children.Add(_result);
        Grid.SetColumn(meta, 1);

        _right.Foreground = TerminalLook.Brush(TerminalLook.Faint);
        _right.Margin = new Thickness(0, 0, 10, 0);
        _right.HorizontalAlignment = HorizontalAlignment.Right;
        Grid.SetColumn(_right, 2);

        // Row 1: the shell, the prompt and the command.
        _shell.Foreground = TerminalLook.Brush(TerminalLook.Faint);
        _shell.Text = Block.ShellName.Length > 0 ? Block.ShellName : TerminalLook.ShellLabel(Block.Settings);
        var prompt = new TextBlock
        {
            Text = "❯",
            FontFamily = TerminalLook.MonoFont(Block.Settings),
            FontSize = TerminalLook.MonoSize(Block.Settings),
            Foreground = TerminalLook.Brush(TerminalLook.Accent),
            VerticalAlignment = VerticalAlignment.Center,
        };

        _command.FontFamily = TerminalLook.MonoFont(Block.Settings);
        _command.FontSize = TerminalLook.MonoSize(Block.Settings);
        _command.Foreground = TerminalLook.Brush(TerminalLook.Command);
        _command.TextTrimming = TextTrimming.CharacterEllipsis;
        _command.TextWrapping = TextWrapping.NoWrap;
        _command.VerticalAlignment = VerticalAlignment.Center;
        // Not selectable: the prompt line is the block's drag handle (Copy command copies the text).
        _command.IsTextSelectionEnabled = false;
        var firstLine = block.Command.Split('\n')[0].TrimEnd('\r');
        _command.Text = firstLine.Length < block.Command.Length ? firstLine + " …" : firstLine;
        ToolTipService.SetToolTip(_command, block.Command);

        var line = new Grid { ColumnSpacing = 7, MinHeight = 22 };
        line.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
        line.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
        line.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
        Grid.SetColumn(prompt, 1);
        Grid.SetColumn(_command, 2);
        line.Children.Add(_shell);
        line.Children.Add(prompt);
        line.Children.Add(_command);
        Grid.SetRow(line, 1);
        Grid.SetColumn(line, 1);

        // Two controls: the one that fits the moment (Stop while it runs, Run again after) and a
        // menu with the rest - the same menu a right-click on the prompt line opens.
        _rerunButton = IconButton("\uE72C", "Run again", (_, _) => RerunRequested?.Invoke(this, Block));
        _stopButton = IconButton("\uE71A", "Stop (Ctrl+C)", (_, _) => _ = Block.StopAsync());
        var more = IconButton("\uE712", "More", (sender, _) => BuildMenu().ShowAt((FrameworkElement)sender));
        _actions.Children.Add(_rerunButton);
        _actions.Children.Add(_stopButton);
        _actions.Children.Add(more);
        _actions.VerticalAlignment = VerticalAlignment.Center;
        Grid.SetRow(_actions, 1);
        Grid.SetColumn(_actions, 2);

        _header.Children.Add(_statusHost);
        _header.Children.Add(meta);
        _header.Children.Add(_right);
        _header.Children.Add(line);
        _header.Children.Add(_actions);

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
    public bool IsSelected { get; set
        {
            if (field != value)
            {
                field = value;
                UpdateChrome();
            }
        } }

    /// <summary>Every other block of the notebook is drawn a shade lighter, so each one reads as a unit.</summary>
    public bool IsStriped { get; set
        {
            if (field != value)
            {
                field = value;
                ApplySurface();
            }
        } }

    private Windows.UI.Color SurfaceColor => IsStriped && !IsDocked && !IsBare ? TerminalLook.Stripe : TerminalLook.Background;

    private void ApplySurface()
    {
        _root.Background = TerminalLook.Brush(SurfaceColor);
        Terminal?.SurfaceColor = SurfaceColor;
    }

    /// <summary>The live output view, while the block is realized.</summary>
    public TerminalView? Terminal { get; private set; }

    /// <summary>
    /// Builds (true) or releases (false) the output view. Released, the body keeps its last height so
    /// the flow does not jump, and the emulator keeps receiving output for when it comes back.
    /// </summary>
    public void Realize(bool realize)
    {
        if (_closed || realize == (Terminal is not null))
        {
            return;
        }

        if (!realize)
        {
            _lastBodyHeight = Terminal!.ActualHeight;
            DetachTerminal();
            _body.Height = FillsViewport ? double.NaN : _lastBodyHeight;
            return;
        }

        _body.ClearValue(HeightProperty);
        var terminal = new TerminalView();
        terminal.ApplyAppSettings(Block.Settings);
        terminal.SurfaceColor = SurfaceColor;
        terminal.Response += OnTerminalInput;
        terminal.Resized += OnTerminalResized;
        terminal.LinkActivated += OnTerminalLink;
        terminal.BlockHeightChanged += OnTerminalHeightChanged;
        terminal.ContextRequested += OnTerminalContextRequested;
        Terminal = terminal;
        ApplyRows();
        terminal.ShowCursor = Block.IsRunning;
        terminal.Attach(Block.Emulator);
        _body.Child = terminal;
    }

    private void DetachTerminal()
    {
        if (Terminal is not { } terminal)
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
        Terminal = null;
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
        Terminal?.Focus(FocusState.Programmatic);
    }

    public void ApplyAppSettings()
    {
        Terminal?.ApplyAppSettings(Block.Settings);
        _command.FontFamily = TerminalLook.MonoFont(Block.Settings);
        _command.FontSize = TerminalLook.MonoSize(Block.Settings);
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
        if (Terminal is null)
        {
            return;
        }

        Terminal.BlockRows = fill ? 0 : Block.Settings.TerminalBlockLines;
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
            case nameof(CommandBlockViewModel.ShellName):
            case nameof(CommandBlockViewModel.Status):
                UpdateStatus();
                Terminal?.ShowCursor = Block.IsRunning;

                break;
        }
    }

    // ------------------------------------------------------------------ input and output

    private void OnTerminalInput(object? sender, string text) => _ = Block.WriteAsync(text);

    private void OnTerminalResized(object? sender, (int Cols, int Rows) size) => Block.Resize(size.Cols, size.Rows);

    private void OnTerminalLink(object? sender, LinkMatch link) => LinkActivated?.Invoke(this, link);

    private void OnTerminalContextRequested(UIElement sender, ContextRequestedEventArgs args)
    {
        if (Terminal is { } terminal)
        {
            TerminalContextMenu.Show(terminal, args);
        }
    }

    private void OnHeaderContextRequested(UIElement sender, ContextRequestedEventArgs args)
    {
        args.Handled = true;
        BuildMenu().ShowAt(_header, new FlyoutShowOptions { Position = args.TryGetPosition(_header, out var p) ? p : null });
    }

    /// <summary>Everything a block can do, for the "More" button and the right-click on its prompt line.</summary>
    private MenuFlyout BuildMenu()
    {
        var menu = new MenuFlyout();
        if (Block.IsRunning)
        {
            Add("Stop", "\uE71A", () => _ = Block.StopAsync());
        }
        else
        {
            Add("Run again", "\uE72C", () => RerunRequested?.Invoke(this, Block));
        }

        Add("Copy command", "\uE8C8", () => _ = ClipboardWriter.SetTextAsync(Block.Command));
        Add("Copy output", "\uE8C8", () => _ = ClipboardWriter.SetTextAsync(Block.OutputText));
        menu.Items.Add(new MenuFlyoutSeparator());
        var dock = new MenuFlyoutSubItem { Text = "Dock", Icon = new FontIcon { Glyph = "\uE90C" } };
        foreach (var (text, side) in new[] { ("Right", BlockDock.Right), ("Top right", BlockDock.TopRight), ("Bottom right", BlockDock.BottomRight) })
        {
            var item = new MenuFlyoutItem { Text = text };
            item.Click += (_, _) => DockRequested?.Invoke(this, (Block, side));
            dock.Items.Add(item);
        }

        menu.Items.Add(dock);
        if (IsDocked)
        {
            Add("Back to its terminal", "\uE72B", () => UndockRequested?.Invoke(this, Block));
        }

        Add("Maximize in new tab", "\uE740", () => PopOutRequested?.Invoke(this, Block));
        menu.Items.Add(new MenuFlyoutSeparator());
        Add("Remove from history", "\uE74D", () => RemoveRequested?.Invoke(this, Block));
        return menu;

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
        var showActions = _pointerOver || IsSelected || IsDocked;
        _actions.Opacity = showActions ? 1 : 0;
        _actions.IsHitTestVisible = showActions;
        _header.Background = TerminalLook.Brush(IsSelected ? TerminalLook.Selected
            : _pointerOver ? TerminalLook.Hover
            : Microsoft.UI.Colors.Transparent);
    }

    /// <summary>The result mark: a spinner while running, then a tick, a cross with the exit code, or "stopped".</summary>
    private void UpdateStatus()
    {
        _stopButton.Visibility = Block.IsRunning ? Visibility.Visible : Visibility.Collapsed;
        _rerunButton.Visibility = Block.IsRunning ? Visibility.Collapsed : Visibility.Visible;
        // A collapsed ring that is still active keeps its animation ticking on the compositor,
        // so every finished block in the scrollback would redraw the window each frame.
        _statusRing.IsActive = Block.IsRunning;
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
        _right.Text = $"{started.ToLocalTime():HH:mm:ss}";
        _shell.Text = Block.ShellName.Length > 0 ? Block.ShellName : TerminalLook.ShellLabel(Block.Settings);

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
        var themes = element.ActualTheme == ElementTheme.Dark ? new[] { "Dark", "Default" } : ["Light"];
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