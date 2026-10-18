using Codale.App.Controls;
using Codale.App.Services;
using Codale.App.ViewModels;
using Codale.Search;

using Microsoft.UI.Dispatching;
using Microsoft.UI.Xaml.Controls;


namespace Codale.App.Views;

/// <summary>
/// A text editor, hosted in a centre-area tab. Created on demand from the + button
/// (open an existing file or start an untitled one); the file tree and search
/// results route through <see cref="OpenFile"/> on the live tab.
/// </summary>
public sealed partial class EditorTab : UserControl
{
    /// <summary>False for a tab started as a new file: the first save picks a path.</summary>
    public bool IsUntitled => ViewModel.FilePath is null;

    /// <summary>
    /// Raised when the user changes the text or saves. The workspace listens for this
    /// to promote a preview tab to a pinned one: an edited file must survive the next
    /// file click, which would otherwise replace the preview's contents.
    /// </summary>
    public event EventHandler? Edited;

    /// <summary>Word counting defers until typing pauses; the count is not worth a stall per keystroke.</summary>
    private readonly DispatcherQueueTimer _wordCountTimer;

    /// <summary>
    /// Zoom re-lays the text after the event, so the scroll re-anchor runs on a short
    /// delay with the new metrics, keeping the caret where it was on screen.
    /// </summary>
    private readonly DispatcherQueueTimer _zoomSettleTimer;
    private double _preZoomCaretViewPx = double.NaN;
    private int _preZoomCursorLine;
    private int _preZoomCursorCol;

    /// <summary>True while LoadText runs: programmatic text changes are neither dirty nor a word-count event.</summary>
    private bool _loadingText;

    public EditorTab()
    {
        InitializeComponent();

        _zoomSettleTimer = DispatcherQueue.GetForCurrentThread().CreateTimer();
        _zoomSettleTimer.Interval = TimeSpan.FromMilliseconds(120);
        _zoomSettleTimer.IsRepeating = false;
        _zoomSettleTimer.Tick += (_, _) => OnZoomSettled();

        _wordCountTimer = DispatcherQueue.GetForCurrentThread().CreateTimer();
        _wordCountTimer.Interval = TimeSpan.FromMilliseconds(300);
        _wordCountTimer.IsRepeating = false;
        _wordCountTimer.Tick += (_, _) =>
        {
            if (ViewModel is not null)
            {
                ViewModel.SetWordCount(EditorViewModel.CountWords(FileEditor.GetText()));
            }
        };

        // A grammar installed or replaced elsewhere (catalog, AI, import) recolours open files.
        Loaded += (_, _) => Codale.Core.Syntax.SyntaxService.Store.Changed += OnSyntaxStoreChanged;
        Unloaded += (_, _) => Codale.Core.Syntax.SyntaxService.Store.Changed -= OnSyntaxStoreChanged;

        BuildContextMenu();
        FileEditor.FilePathProvider = () => ViewModel?.FilePath;
        FileEditor.SelectionChanged += OnEditorSelectionChanged;
        FileEditor.TextChanged += _ => OnEditorTextChanged();
        FileEditor.ZoomChanged += (_, _) => OnZoomChanged();

        // Save lives on the control now that the toolbar button is gone; the
        // accelerator only fires while this tab's subtree has focus.
        var saveAccelerator = new Microsoft.UI.Xaml.Input.KeyboardAccelerator
        {
            Key = Windows.System.VirtualKey.S,
            Modifiers = Windows.System.VirtualKeyModifiers.Control,
        };
        saveAccelerator.Invoked += OnSaveAcceleratorInvoked;
        KeyboardAccelerators.Add(saveAccelerator);

        // Find (Ctrl+F by default) is a window shortcut: the workspace scopes the title bar box to this file.
        AddAccelerator(Windows.System.VirtualKey.F3, Windows.System.VirtualKeyModifiers.None, args =>
            args.Handled = MoveHighlight(+1));
        AddAccelerator(Windows.System.VirtualKey.F3, Windows.System.VirtualKeyModifiers.Shift, args =>
            args.Handled = MoveHighlight(-1));
        AddAccelerator(Windows.System.VirtualKey.Escape, Windows.System.VirtualKeyModifiers.None, args =>
        {
            // Esc is only claimed while there is something to clear.
            args.Handled = _highlights.Count > 0 || HighlightBar.Visibility == Microsoft.UI.Xaml.Visibility.Visible;
            ClearHighlights();
        });

        // Bands are laid out from the scroll offset, so they must redraw in the same
        // frame the text moves - anything polled later reads as floating while scrolling.
        FileEditor.ScrollChanged += (_, _) => RenderHighlights(force: true);

        HighlightLayer.SizeChanged += (_, e) =>
        {
            HighlightLayer.Clip = new Microsoft.UI.Xaml.Media.RectangleGeometry
            {
                Rect = new Windows.Foundation.Rect(0, 0, e.NewSize.Width, e.NewSize.Height),
            };
            RenderHighlights(force: true);
        };

        GotFocus += OnTabGotFocus;
        PointerPressed += OnTabPointerPressed;
    }

    private void OnTabGotFocus(object sender, Microsoft.UI.Xaml.RoutedEventArgs e) =>
        _ = CheckAndUpdateIfModifiedAsync();

    private void OnTabPointerPressed(object sender, Microsoft.UI.Xaml.Input.PointerRoutedEventArgs e) =>
        _ = CheckAndUpdateIfModifiedAsync();

    private void AddAccelerator(
        Windows.System.VirtualKey key,
        Windows.System.VirtualKeyModifiers modifiers,
        Action<Microsoft.UI.Xaml.Input.KeyboardAcceleratorInvokedEventArgs> invoked)
    {
        var accelerator = new Microsoft.UI.Xaml.Input.KeyboardAccelerator { Key = key, Modifiers = modifiers };
        accelerator.Invoked += (_, args) => invoked(args);
        KeyboardAccelerators.Add(accelerator);
    }

    /// <summary>Puts the caret back in the text, so F3 and Esc reach this tab.</summary>
    public void FocusText() => FileEditor.Focus(Microsoft.UI.Xaml.FocusState.Programmatic);

    /// <summary>Live-applies Settings changes to the editor (font size, tabs, gutter, colours, wrap).</summary>
    public void ApplyAppSettings()
    {
        FileEditor.ApplyAppSettings();
        RenderHighlights(force: true);
    }

    private IReadOnlyList<LineSpan> _highlights = [];
    private int _currentHighlight = -1;
    private string _highlightLabel = "";
    private CancellationTokenSource? _focusSearch;
    private string _focusQuery = "";
    private IReadOnlyList<string> _focusLines = [];

    private bool _bands;
    private string _bandQuery = "";
    private IReadOnlyList<string>? _bandLines;

    private IReadOnlyList<string> BandLines() =>
        _bandLines ??= FileEditor.GetText().Split('\n').Select(l => l.TrimEnd('\r')).ToList();

    private static readonly Microsoft.UI.Xaml.Media.SolidColorBrush BandBrush =
        new(Windows.UI.Color.FromArgb(0x30, 0xE5, 0xB2, 0x3C));
    private static readonly Microsoft.UI.Xaml.Media.SolidColorBrush WordBrush =
        new(Windows.UI.Color.FromArgb(0x90, 0xE5, 0xB2, 0x3C));
    private (double Scroll, double LineHeight, double Width, double Height, int Current) _drawn;

    private int _targetColumn;
    private int _targetEndColumn;

    /// <summary>
    /// Marks line spans in the open file and, when <paramref name="reveal"/> is set,
    /// scrolls the first one into the middle of the view.
    /// </summary>
    public void ShowHighlights(
        IReadOnlyList<LineSpan> spans,
        string status,
        bool reveal,
        bool busy = false,
        int current = 0,
        bool moveCaret = false,
        int column = 0,
        int endColumn = 0,
        bool bands = false,
        string query = "")
    {
        // Global search is research: its results band the matching lines. In-file search marks only the keyword.
        _bands = bands;
        _bandQuery = query;
        _bandLines = null;
        if (bands)
        {
            _focusQuery = "";
        }

        if (_previewing)
        {
            _previewing = false;
            MarkdownPreviewHost.Visibility = Microsoft.UI.Xaml.Visibility.Collapsed;
            FileEditor.Visibility = Microsoft.UI.Xaml.Visibility.Visible;
        }

        _highlights = spans;
        _currentHighlight = spans.Count > 0 ? Math.Clamp(current, 0, spans.Count - 1) : -1;
        _targetColumn = column;
        _targetEndColumn = endColumn;
        PushSearchMarks();

        _highlightLabel = status;
        HighlightStatus.Text = status;
        HighlightProgress.IsActive = busy;
        HighlightProgress.Visibility = busy ? Microsoft.UI.Xaml.Visibility.Visible : Microsoft.UI.Xaml.Visibility.Collapsed;
        HighlightBar.Visibility = Microsoft.UI.Xaml.Visibility.Visible;

        RenderHighlights(force: true);

        if (reveal && spans.Count > 0)
        {
            // A file loaded a moment ago has no line metrics yet; reveal after layout.
            DispatcherQueue.TryEnqueue(DispatcherQueuePriority.Low, () => Reveal(_currentHighlight, moveCaret));
        }
    }

    public void ClearHighlights()
    {
        _focusSearch?.Cancel();
        _highlights = [];
        _focusQuery = "";
        _currentHighlight = -1;
        PushSearchMarks();
        HighlightLayer.Children.Clear();
        HighlightBar.Visibility = Microsoft.UI.Xaml.Visibility.Collapsed;
    }

    /// <summary>Mirrors the highlighted spans onto the editor's scrollbar track (spans are 1-based lines).</summary>
    private void PushSearchMarks() =>
        FileEditor.SetSearchMarks(
            _highlights.Select(span => (Math.Max(0, span.Start - 1), Math.Max(0, span.End - 1))).ToList(),
            _currentHighlight);

    /// <summary>
    /// Highlights what in this file relates to <paramref name="query"/>: the search's own
    /// words at once, then the ranges the helper model picks after reading the whole file.
    /// </summary>
    public async Task<int> FocusOnQueryAsync(string query, Func<CancellationToken, Task<ISearchModel?>> connectModel)
    {
        var found = 0;
        _focusSearch?.Cancel();
        var cts = new CancellationTokenSource();
        _focusSearch = cts;

        var lines = FileEditor.GetText().Split('\n').Select(l => l.TrimEnd('\r')).ToList();
        _focusQuery = query;
        _focusLines = lines;

        // Semantic only: no keyword pass to flash misleading bands while the model reads.
        ShowHighlights([], "Searching the file…", reveal: false, busy: true);

        try
        {
            var model = await connectModel(cts.Token);
            var spans = model is null
                ? null
                : await FileFocus.AskAsync(model, query, ViewModel.FileName ?? "", lines, cts.Token);

            if (cts.IsCancellationRequested)
            {
                return 0;
            }

            if (spans is { Count: > 0 })
            {
                found = spans.Count;
                ShowHighlights(spans, spans.Count == 1 ? "1 related part" : $"{spans.Count} related parts", reveal: true);
            }
            else
            {
                ShowHighlights([], model is null ? "No Claude CLI found" : spans is null ? "Search failed" : "Nothing related", reveal: false);
            }
        }
        catch (OperationCanceledException)
        {
        }
        finally
        {
            if (_focusSearch == cts)
            {
                _focusSearch = null;
            }

            cts.Dispose();
        }

        return found;
    }

    private void OnPreviousHighlightClick(object sender, Microsoft.UI.Xaml.RoutedEventArgs e) => MoveHighlight(-1);

    private void OnNextHighlightClick(object sender, Microsoft.UI.Xaml.RoutedEventArgs e) => MoveHighlight(+1);

    private void OnClearHighlightClick(object sender, Microsoft.UI.Xaml.RoutedEventArgs e) => ClearHighlights();

    private bool MoveHighlight(int step)
    {
        if (_highlights.Count == 0)
        {
            return false;
        }

        _targetColumn = 0;
        _targetEndColumn = 0;
        Reveal((_currentHighlight + step + _highlights.Count) % _highlights.Count, moveCaret: true);
        return true;
    }

    private void Reveal(int index, bool moveCaret)
    {
        if (index < 0 || index >= _highlights.Count)
        {
            return;
        }

        _currentHighlight = index;
        PushSearchMarks();
        var line = Math.Clamp(_highlights[index].Start - 1, 0, Math.Max(0, FileEditor.NumberOfLines - 1));

        // Centre the span; the caret only follows on explicit navigation. Results
        // arriving on their own (the instant pool, or the model finishing seconds
        // later while the user types) must scroll only - parking the caret at the
        // span start would yank it away from the position being typed at.
        FileEditor.ScrollLineToCenter(line);
        if (moveCaret)
        {
            var col = Math.Max(0, _targetColumn > 0 ? _targetColumn - 1 : 0);
            FileEditor.SetCursorPosition(line, col, false, false);

            if (_targetEndColumn > _targetColumn && _highlights[index].End == _highlights[index].Start)
            {
                FileEditor.SetCursorPosition(line, Math.Max(0, _targetEndColumn - 1), true, false);
            }
        }

        if (_highlights.Count > 1)
        {
            HighlightStatus.Text = $"{index + 1} of {_highlights.Count} · {_highlightLabel}";
        }

        RenderHighlights(force: true);
    }

    /// <summary>
    /// Lays a band over each highlighted span that is on screen. Line y comes from the
    /// same scroll arithmetic the zoom re-anchor uses; bands span the text width, so
    /// horizontal scrolling never moves them.
    /// </summary>
    /// <summary>
    /// Marks the search's own terms (newline separated) the way the search preview does:
    /// longest first, no overlaps, short words only at a word start.
    /// </summary>
    private static List<(int Line, int Col, int Length)> TermOccurrences(
        string terms, IReadOnlyList<string> lines, int firstLine, int lastLine)
    {
        var hits = new List<(int Line, int Col, int Length)>();
        lastLine = Math.Min(lastLine, lines.Count - 1);

        foreach (var term in terms.Split('\n', StringSplitOptions.RemoveEmptyEntries).OrderByDescending(t => t.Length))
        {
            var wordStart = term.Length <= 5 && char.IsLetterOrDigit(term[0]);
            for (var n = Math.Max(0, firstLine); n <= lastLine; n++)
            {
                var line = lines[n];
                var index = 0;
                while ((index = line.IndexOf(term, index, StringComparison.OrdinalIgnoreCase)) >= 0)
                {
                    var at = index;
                    var overlaps = hits.Any(h => h.Line == n && at < h.Col + h.Length && h.Col < at + term.Length);
                    var midWord = wordStart && at > 0 && (char.IsLetterOrDigit(line[at - 1]) || line[at - 1] == '_');
                    if (!overlaps && !midWord)
                    {
                        hits.Add((n, at, term.Length));
                    }

                    index += term.Length;
                }
            }
        }

        return hits;
    }

    private void RenderHighlights(bool force)
    {
        var lineHeight = FileEditor.ActualLineHeight;
        var layout = (FileEditor.VerticalScroll, lineHeight, HighlightLayer.ActualWidth, HighlightLayer.ActualHeight, _currentHighlight);

        if (!force && layout == _drawn)
        {
            return;
        }

        _drawn = layout;
        HighlightLayer.Children.Clear();

        if (_highlights.Count == 0 || lineHeight <= 0)
        {
            return;
        }

        var offset = FileEditor.VerticalScroll;

        // Leave the scrollbar uncovered.
        var width = Math.Max(0, HighlightLayer.ActualWidth - 14);

        for (var i = 0; i < _highlights.Count; i++)
        {
            var span = _highlights[i];
            var firstLine = Math.Max(0, span.Start - 1);
            var lastLine = Math.Max(firstLine, span.End - 1);

            // Wrapped lines stack rows, so a band's top and height come from the
            // editor's visual-row map rather than the line index.
            var top = FileEditor.VisualRowOfPosition(firstLine, 0) * lineHeight - offset;
            var bottomRow = FileEditor.VisualRowOfPosition(lastLine, 0) + FileEditor.VisualRowsOf(lastLine);
            var height = bottomRow * lineHeight - top - offset;

            if (top + height < 0 || top > HighlightLayer.ActualHeight)
            {
                continue;
            }

            if (_bands)
            {
                // Same look as the search preview: a band on each matching line with the
                // keyword marked. Without a query (path clicks) the whole span is banded.
                var hits = _bandQuery.Length > 0
                    ? TermOccurrences(_bandQuery, BandLines(), firstLine, lastLine)
                    : [];

                if (hits.Count == 0)
                {
                    var band = new Microsoft.UI.Xaml.Shapes.Rectangle { Width = width, Height = Math.Max(0, height), Fill = BandBrush };
                    Canvas.SetTop(band, top);
                    HighlightLayer.Children.Add(band);
                    continue;
                }

                foreach (var lineNo in hits.Select(h => h.Item1).Distinct())
                {
                    var lineTop = FileEditor.VisualRowOfPosition(lineNo, 0) * lineHeight - offset;
                    var lineHeightPx = FileEditor.VisualRowsOf(lineNo) * lineHeight;
                    if (lineTop + lineHeightPx < 0 || lineTop > HighlightLayer.ActualHeight)
                    {
                        continue;
                    }

                    var band = new Microsoft.UI.Xaml.Shapes.Rectangle { Width = width, Height = lineHeightPx, Fill = BandBrush };
                    Canvas.SetTop(band, lineTop);
                    HighlightLayer.Children.Add(band);
                }

                foreach (var (line, col, length) in hits)
                {
                    var start = FileEditor.PositionBounds(line, col);
                    if (start.Y + start.Height < 0 || start.Y > HighlightLayer.ActualHeight)
                    {
                        continue;
                    }

                    var end = FileEditor.PositionBounds(line, col + length);
                    var mark = new Microsoft.UI.Xaml.Shapes.Rectangle
                    {
                        Width = end.Y == start.Y ? end.X - start.X : Math.Max(0, HighlightLayer.ActualWidth - start.X - 14),
                        Height = start.Height,
                        Fill = WordBrush,
                        RadiusX = 2,
                        RadiusY = 2,
                    };
                    Canvas.SetLeft(mark, start.X);
                    Canvas.SetTop(mark, start.Y);
                    HighlightLayer.Children.Add(mark);
                }

                continue;
            }

            // Only the search's exact words are marked, not the whole span, so the
            // highlights stay easy to tell apart while navigating.
            if (_focusQuery.Length > 0)
            {
                foreach (var (line, col, length) in FileFocus.Occurrences(_focusQuery, _focusLines, firstLine, lastLine))
                {
                    var start = FileEditor.PositionBounds(line, col);
                    if (start.Y + start.Height < 0 || start.Y > HighlightLayer.ActualHeight)
                    {
                        continue;
                    }

                    var end = FileEditor.PositionBounds(line, col + length);
                    // A word folded onto the next wrapped row is marked up to its first row's end only roughly.
                    var wordWidth = end.Y == start.Y ? end.X - start.X : Math.Max(0, HighlightLayer.ActualWidth - start.X - 14);

                    var mark = new Microsoft.UI.Xaml.Shapes.Rectangle
                    {
                        Width = wordWidth,
                        Height = start.Height,
                        Fill = WordBrush,
                        RadiusX = 2,
                        RadiusY = 2,
                    };
                    Canvas.SetLeft(mark, start.X);
                    Canvas.SetTop(mark, start.Y);
                    HighlightLayer.Children.Add(mark);
                }
            }
        }
    }

    public EditorViewModel ViewModel { get; private set; } = null!;

    public EditorTab WithViewModel(EditorViewModel viewModel)
    {
        ViewModel = viewModel;
        Bindings.Update();
        ViewModel.Saved += (_, _) => _ = RefreshDiffMarkersAsync(refetchHead: true);
        return this;
    }

    // ------------------------------------------------------------------ diff gutter

    private DispatcherQueueTimer? _diffTimer;
    private string? _headText;
    private int _diffSequence;

    /// <summary>
    /// Recomputes the change bars in the editor gutter against the file's text at HEAD. The
    /// baseline is only re-read from git when the file was loaded or saved; plain typing just
    /// re-diffs the buffer against the cached one.
    /// </summary>
    private async Task RefreshDiffMarkersAsync(bool refetchHead)
    {
        if (ViewModel?.FilePath is not { } path || ViewModel.LoadedText is null)
        {
            FileEditor.SetDiffMarkers([]);
            return;
        }

        var sequence = ++_diffSequence;
        try
        {
            if (refetchHead)
            {
                var directory = System.IO.Path.GetDirectoryName(path);
                _headText = directory is null || !System.IO.Directory.Exists(directory)
                    ? null
                    : await new Codale.Git.GitRepository(directory).GetHeadTextAsync(path);
            }

            if (sequence != _diffSequence)
            {
                return;
            }

            if (_headText is not { } head)
            {
                FileEditor.SetDiffMarkers([]);
                return;
            }

            var current = FileEditor.GetText();
            var markers = await Task.Run(() => DiffMarkers.Build(head, current));
            if (sequence == _diffSequence)
            {
                FileEditor.SetDiffMarkers(markers);
            }
        }
        catch
        {
            // A missing git or a locked repository just means no bars.
            if (sequence == _diffSequence)
            {
                FileEditor.SetDiffMarkers([]);
            }
        }
    }

    private void ScheduleDiffRefresh()
    {
        if (_diffTimer is null)
        {
            _diffTimer = DispatcherQueue.GetForCurrentThread().CreateTimer();
            _diffTimer.Interval = TimeSpan.FromMilliseconds(400);
            _diffTimer.IsRepeating = false;
            _diffTimer.Tick += (_, _) => _ = RefreshDiffMarkersAsync(refetchHead: false);
        }

        _diffTimer.Stop();
        _diffTimer.Start();
    }

    /// <summary>Loads a file into this tab and applies per-language highlighting.</summary>
    public async Task OpenFileAsync(string path)
    {
        // Highlights belong to the file they were found in.
        ClearHighlights();
        ImagePreview.Source = null;
        HexView.Close();

        var sequence = ++_loadSequence;
        await ViewModel.LoadAsync(path);

        if (sequence != _loadSequence)
        {
            return; // a newer open superseded this one while the read was in flight
        }

        if (ViewModel.IsImage)
        {
            _ = ShowImageAsync(path);
        }
        else if (ViewModel.IsBinaryPlaceholder)
        {
            HexView.Open(path);
        }

        if (ViewModel.LoadedText is { } text)
        {
            _loadingText = true;
            try
            {
                FileEditor.LoadText(text);
            }
            finally
            {
                _loadingText = false;
            }

            ApplyHighlighting(path);
            _ = RefreshDiffMarkersAsync(refetchHead: true);
        }
    }

    private bool _isCheckingUpdate;

    /// <summary>
    /// Checks if the open file has been modified on disk by an agent or other program,
    /// and reloads the display if so.
    /// </summary>
    public async Task<bool> CheckAndUpdateIfModifiedAsync()
    {
        if (ViewModel is null || !ViewModel.HasFile || ViewModel.FilePath is not { } path)
        {
            return false;
        }

        if (_isCheckingUpdate)
        {
            return false;
        }

        _isCheckingUpdate = true;
        try
        {
            if (ViewModel.DeletedOnDisk())
            {
                ViewModel.Message = "File was deleted on disk.";
                return false;
            }

            if (!ViewModel.ChangedOnDisk())
            {
                return false;
            }

            // If the user has unsaved changes in Codale, don't silently overwrite them.
            if (ViewModel.IsDirty)
            {
                ViewModel.Message = "This file changed on disk since you opened it. Save again to overwrite it with your version.";
                return false;
            }

            await ReloadAsync();
            return true;
        }
        finally
        {
            _isCheckingUpdate = false;
        }
    }

    /// <summary>
    /// Reloads the file from disk while preserving the cursor position and scroll offset if possible.
    /// </summary>
    public async Task ReloadAsync()
    {
        if (ViewModel.FilePath is not { } path)
        {
            return;
        }

        var cursor = FileEditor.CursorPosition;
        var scroll = FileEditor.VerticalScroll;

        ClearHighlights();
        ImagePreview.Source = null;
        HexView.Close();

        var sequence = ++_loadSequence;
        await ViewModel.LoadAsync(path);

        if (sequence != _loadSequence)
        {
            return;
        }

        if (ViewModel.IsImage)
        {
            _ = ShowImageAsync(path);
        }
        else if (ViewModel.IsBinaryPlaceholder)
        {
            HexView.Open(path);
        }

        if (ViewModel.LoadedText is { } text)
        {
            _loadingText = true;
            try
            {
                FileEditor.LoadText(text);
                FileEditor.SetCursorPosition(cursor.LineNumber, Math.Max(0, cursor.CharacterPositionInLine - 1), select: false, scrollIntoView: false);
                FileEditor.SetVerticalScroll(scroll);
            }
            finally
            {
                _loadingText = false;
            }

            ApplyHighlighting(path);
            _ = RefreshDiffMarkersAsync(refetchHead: true);
        }
    }

    /// <summary>Guards against two overlapping async opens of the same preview tab.</summary>
    private int _loadSequence;

    /// <summary>
    /// Decodes from a stream rather than a file URI: works for any path the app can read
    /// and leaves the file unlocked, so an agent can keep overwriting it.
    /// </summary>
    private async Task ShowImageAsync(string path)
    {
        try
        {
            using var stream = new MemoryStream(await File.ReadAllBytesAsync(path));
            using var random = stream.AsRandomAccessStream();

            Microsoft.UI.Xaml.Media.ImageSource source;
            if (string.Equals(Path.GetExtension(path), ".svg", StringComparison.OrdinalIgnoreCase))
            {
                var svg = new Microsoft.UI.Xaml.Media.Imaging.SvgImageSource();
                await svg.SetSourceAsync(random);
                source = svg;
            }
            else
            {
                var bitmap = new Microsoft.UI.Xaml.Media.Imaging.BitmapImage();
                await bitmap.SetSourceAsync(random);
                source = bitmap;
            }

            // A newer file may have been opened while this one decoded.
            if (ViewModel.FilePath == path)
            {
                ImagePreview.Source = source;
            }
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or System.Runtime.InteropServices.COMException)
        {
            if (ViewModel.FilePath == path)
            {
                ViewModel.Message = "This image could not be displayed.";
                ViewModel.IsImage = false;
            }
        }
    }

    private void OpenExternally()
    {
        if (ViewModel.FilePath is not { } path)
        {
            return;
        }

        try
        {
            System.Diagnostics.Process.Start(new System.Diagnostics.ProcessStartInfo(path) { UseShellExecute = true });
        }
        catch (Exception ex) when (ex is System.ComponentModel.Win32Exception or IOException)
        {
            ViewModel.Message = ex.Message;
        }
    }

    /// <summary>Starts an empty untitled buffer; the first save asks for a path.</summary>
    public void StartNewFile()
    {
        ViewModel.FileName = "Untitled";
        ViewModel.Message = null;
        ViewModel.HasFile = true;
        ViewModel.CanEdit = true;
        ViewModel.IsDirty = false;
        ViewModel.SetWordCount(0);
        ViewModel.UpdateCursorPosition(1, 1, 0);

        _loadingText = true;
        try
        {
            FileEditor.LoadText(string.Empty);
        }
        finally
        {
            _loadingText = false;
        }
    }

    /// <summary>
    /// Cursor moves are cheap; the control reports the line 0-based and the column already 1-based.
    /// </summary>
    private void OnEditorSelectionChanged(CodeEditor sender, EditorSelectionEventArgs args)
    {
        if (_loadingText)
        {
            ViewModel.UpdateCursorPosition(1, 1, 0);
            return;
        }

        ViewModel.UpdateCursorPosition(
            args.LineNumber + 1, args.CharacterPositionInLine,
            sender.HasSelection ? sender.SelectedTextLength : 0);
    }

    private void OnEditorTextChanged()
    {
        if (!_loadingText)
        {
            ViewModel.MarkDirty();
            Edited?.Invoke(this, EventArgs.Empty);
        }

        if (!_loadingText)
        {
            ScheduleDiffRefresh();
        }

        // Restarting on every keystroke leaves only the last one counted.
        _wordCountTimer.Stop();
        _wordCountTimer.Start();
    }

    /// <summary>
    /// Zoom: keep the caret where it was on screen once the control has re-laid the
    /// text at the new metrics - it re-lays after the event, so the scroll re-anchor
    /// runs on a short delay with the new metrics. The anchor is the caret's visual
    /// row (a wrapped line spans several), remembered in view pixels.
    /// </summary>
    private void OnZoomChanged()
    {
        var lineHeight = FileEditor.ActualLineHeight;
        if (lineHeight > 0)
        {
            _preZoomCursorLine = FileEditor.CursorPosition.LineNumber;
            _preZoomCursorCol = FileEditor.CursorPosition.CharacterPositionInLine - 1;
            _preZoomCaretViewPx = FileEditor.VisualRowOfPosition(_preZoomCursorLine, _preZoomCursorCol) * lineHeight
                - FileEditor.VerticalScroll;
        }

        _zoomSettleTimer.Stop();
        _zoomSettleTimer.Start();
    }

    private void OnZoomSettled()
    {
        var lineHeight = FileEditor.ActualLineHeight;
        if (double.IsFinite(_preZoomCaretViewPx) && lineHeight > 0)
        {
            // Scroll so the caret's visual row lands back where it sat before the
            // zoom; with wrap on, the same character can fold to a different row now.
            var row = FileEditor.VisualRowOfPosition(_preZoomCursorLine, _preZoomCursorCol);
            FileEditor.SetVerticalScroll(row * lineHeight - _preZoomCaretViewPx);
            FileEditor.ScrollIntoViewHorizontally();
        }

        _preZoomCaretViewPx = double.NaN;
    }

    private async void OnSaveAcceleratorInvoked(Microsoft.UI.Xaml.Input.KeyboardAccelerator sender,
        Microsoft.UI.Xaml.Input.KeyboardAcceleratorInvokedEventArgs args)
    {
        args.Handled = true;
        await SaveAsync();
    }

    /// <summary>
    /// Saves the buffer, asking for a path the first time an untitled one is saved.
    /// </summary>
    private async Task SaveAsync()
    {
        if (ViewModel is null || !ViewModel.CanEdit)
        {
            return;
        }

        if (ViewModel.FilePath is null)
        {
            // The App SDK picker, unlike the WinRT one, can open in an arbitrary folder,
            // so an untitled buffer starts out in the workspace it was created from.
            var windowId = App.Current.MainWindowHandle is { } hwnd
                ? Microsoft.UI.Win32Interop.GetWindowIdFromWindow(hwnd)
                : default;
            var picker = new Microsoft.Windows.Storage.Pickers.FileSavePicker(windowId)
            {
                SuggestedFileName = ViewModel.FileName ?? "Untitled",
            };
            picker.FileTypeChoices.Add("Any file", [".txt"]);
            if (!string.IsNullOrEmpty(ProjectRoot) && Directory.Exists(ProjectRoot))
            {
                picker.SuggestedFolder = ProjectRoot;
            }

            if (await picker.PickSaveFileAsync() is not { } file)
            {
                return;
            }

            ViewModel.FilePath = file.Path;
            ViewModel.FileName = Path.GetFileName(file.Path);

            // Keep a hand-picked language across the first save, otherwise detect from the new name.
            if (_untitledLanguage is not null)
            {
                SyntaxSelection.SetOverride(file.Path, _untitledLanguage);
                _untitledLanguage = null;
            }

            ApplyHighlighting(file.Path);
        }

        // Saving claims the tab even with no edits behind it, matching the pin-on-save
        // behaviour the preview flow relies on.
        ViewModel.SetText(FileEditor.GetText());
        Edited?.Invoke(this, EventArgs.Empty);
        await ViewModel.SaveAsync();
    }

    private void OnSyntaxStoreChanged()
    {
        DispatcherQueue.TryEnqueue(() =>
        {
            if (!string.IsNullOrEmpty(ViewModel?.FilePath))
            {
                ApplyHighlighting(ViewModel.FilePath);
            }
        });
    }

    /// <summary>The buffer's text, which the AI grammar generator reads a sample from.</summary>
    public string GetEditorText() => FileEditor.GetText();

    /// <summary>The id of the language colouring this file, or null for plain text.</summary>
    public string? LanguageId => FileEditor.LanguageId;

    /// <summary>Raised when the language colouring this tab changes (opened, overridden, or its grammar replaced).</summary>
    public event EventHandler? LanguageChanged;

    private void ApplyHighlighting(string path)
    {
        var id = SyntaxSelection.IdFor(path);
        if (id is null && !SyntaxSelection.HasOverride(path) && FileEditor.GetText() is { Length: > 0 } text)
        {
            // Only files the name does not identify pay for reading a first line (a shebang, "<?xml").
            var end = text.IndexOf('\n');
            id = SyntaxSelection.IdFor(path, text[..Math.Min(end < 0 ? text.Length : end, 200)]);
        }

        FileEditor.SelectSyntaxHighlightingById(id);
        LanguageChanged?.Invoke(this, EventArgs.Empty);
    }

    /// <summary>Colours this file with <paramref name="languageId"/> (null re-detects it) and remembers the choice.</summary>
    public void SetLanguage(string? languageId)
    {
        if (string.IsNullOrEmpty(ViewModel.FilePath))
        {
            // An untitled buffer has no path to key an override on: hold the pick until the first save.
            if (!ViewModel.HasFile)
            {
                return;
            }

            _untitledLanguage = languageId;
            FileEditor.SelectSyntaxHighlightingById(languageId == SyntaxSelection.PlainText ? null : languageId);
            LanguageChanged?.Invoke(this, EventArgs.Empty);
            return;
        }

        SyntaxSelection.SetOverride(ViewModel.FilePath, languageId);
        ApplyHighlighting(ViewModel.FilePath);
    }

    private string? _untitledLanguage;

    /// <summary>True when the language was picked by hand rather than detected from the file.</summary>
    public bool HasManualLanguage => string.IsNullOrEmpty(ViewModel.FilePath)
        ? _untitledLanguage is not null
        : SyntaxSelection.HasOverride(ViewModel.FilePath);
}