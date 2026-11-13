using Codale.App.Services;
using Codale.Editor;

using Microsoft.UI.Input;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Media;

using Windows.System;
using Windows.UI.Core;

namespace Codale.App.Controls;

/// <summary>
/// Codale's native code editor. <see cref="EditorModel"/> (Codale.Editor) owns the text, the
/// selections, every editing command and undo; <see cref="SyntaxHighlighter"/> colours lines in
/// idle time slices. This control is the view: it renders one frame per change into a swap chain
/// (text, highlights, selections, carets, gutter, diff bars and scrollbars all in the same pass)
/// and maps keyboard and pointer input onto the model.
///
/// The partial files split the view up: Layout (metrics, wrapping, column ↔ pixel mapping),
/// Rendering (swap chain and the frame), Input (keys, pointer, commands), ScrollBars (scrolling
/// and the minimap bars), Highlights (occurrences, pinned words, search results), DiffGutter,
/// Completion and Ghost.
/// </summary>
public sealed partial class CodeEditor : UserControl
{
    private const float MinFontSize = 8;
    private const float MaxFontSize = 40;
    private const float DefaultFontSize = 13;

    private readonly EditorModel _model = new();
    private readonly SyntaxHighlighter _highlighter;
    private readonly LineMarkers _bookmarks;
    private readonly Microsoft.UI.Dispatching.DispatcherQueueTimer _caretTimer;

    /// <summary>The font size Settings last handed this editor, so a zoomed tab can be told apart from one that follows the setting.</summary>
    private double _settingsFontSize;

    private bool _closed;
    private bool _caretVisible = true;

    /// <summary>Whether the gutter draws line numbers (Settings).</summary>
    public bool ShowLineNumbers { get; set; } = true;

    /// <summary>Whether tokens colour the text (Settings).</summary>
    public bool EnableSyntaxHighlighting
    {
        get => _highlighter.Enabled;
        set => _highlighter.Enabled = value;
    }

    /// <summary>Whether long lines fold at the viewport edge instead of scrolling sideways (Settings).</summary>
    public bool WordWrap { get; set; }

    public CodeEditor()
    {
        InitializeComponent();

        _highlighter = new SyntaxHighlighter(_model.Buffer);
        _bookmarks = new LineMarkers(_model.Buffer);
        _model.TabWidth = AppSettings.EditorTabWidth;

        _model.Buffer.Changed += OnBufferChanged;
        _model.Buffer.Reloaded += OnBufferReloaded;
        _model.SelectionsChanged += OnSelectionsChanged;
        _model.Edited += OnModelEdited;
        _highlighter.TokensChanged += OnTokensChanged;
        _bookmarks.Changed += () =>
        {
            _marksDirty = true;
            Invalidate();
        };

        RefreshThemeColors();
        ActualThemeChanged += (_, _) =>
        {
            RefreshThemeColors();
            DropAllVisuals();
            _marksDirty = true;
            Invalidate();
        };

        _caretTimer = Microsoft.UI.Dispatching.DispatcherQueue.GetForCurrentThread().CreateTimer();
        _caretTimer.Interval = TimeSpan.FromMilliseconds(530);
        _caretTimer.Tick += (_, _) =>
        {
            if (_closed)
            {
                return;
            }

            _caretVisible = !_caretVisible;
            Invalidate();
        };

        GotFocus += (_, _) =>
        {
            RestartCaretBlink();
        };
        LostFocus += (_, _) =>
        {
            _caretTimer.Stop();
            _caretVisible = false;
            Invalidate();
        };
        Loaded += (_, _) =>
        {
            Surface.CompositionScaleChanged += OnCompositionScaleChanged;
            Invalidate();
        };
        Unloaded += (_, _) =>
        {
            Surface.CompositionScaleChanged -= OnCompositionScaleChanged;
            _caretTimer.Stop();
            _autoScrollTimer?.Stop();
            _occurrenceTimer?.Stop();
            _ghostTimer?.Stop();
            StopRenderLoop();
        };

        // Tunnelling: the focus system takes Tab before a bubbling KeyDown would see it.
        PreviewKeyDown += OnEditorKeyDown;
        CharacterReceived += OnEditorCharacterReceived;
        LosingFocus += OnEditorLosingFocus;
        InitCompletion();

        RegisterPropertyChangedCallback(FontSizeProperty, (_, _) => OnFontMetricsChanged());

        // Settings own the defaults; zoom overrides per tab until the next ApplyAppSettings.
        _settingsFontSize = Math.Clamp(AppSettings.EditorFontSize, MinFontSize, MaxFontSize);
        FontSize = _settingsFontSize;
        ShowLineNumbers = AppSettings.EditorLineNumbers;
        _highlighter.Enabled = AppSettings.EditorHighlighting;
        WordWrap = AppSettings.EditorWordWrap;
    }

    // ------------------------------------------------------------------ lifetime

    /// <summary>Releases native graphics when the owner permanently closes.</summary>
    public void Close()
    {
        if (_closed)
        {
            return;
        }

        _closed = true;
        _caretTimer.Stop();
        _autoScrollTimer?.Stop();
        _occurrenceTimer?.Stop();
        _ghostTimer?.Stop();
        _ghostCts?.Cancel();
        CloseCompletion();
        StopRenderLoop();
        DropAllVisuals();
        DisposeGraphics();
        Surface.RemoveFromVisualTree();
    }

    // ------------------------------------------------------------------ public surface

    /// <summary>The whole buffer text, joined with CRLF. Cached until the next edit.</summary>
    public string GetText() => _model.Buffer.GetText();

    public int NumberOfLines => _model.Buffer.LineCount;

    /// <summary>The height of one rendered line in dips; overlays key off it.</summary>
    public double ActualLineHeight
    {
        get
        {
            EnsureMetrics();
            return _lineHeight;
        }
    }

    /// <summary>Vertical scroll offset in dips from the top of the buffer.</summary>
    public double VerticalScroll => _scrollY;

    /// <summary>Scrolls so <paramref name="offset"/> dips of the buffer are at the top.</summary>
    public void SetVerticalScroll(double offset) => ScrollTo(null, Math.Max(0, offset), animate: false);

    /// <summary>The primary caret: line 0-based, column 1-based.</summary>
    public EditorCursorPosition CursorPosition => new(_model.Primary.Caret.Line, _model.Primary.Caret.Col + 1);

    public bool HasSelection => _model.HasSelection;

    /// <summary>Characters selected across every selection, line breaks counted as CRLF.</summary>
    public int SelectedTextLength => (int)Math.Min(int.MaxValue, _model.SelectedLength(2));

    /// <summary>The selected text (several selections one per line), or an empty string.</summary>
    public string SelectedText => _model.GetSelectedText();

    /// <summary>The primary selection as character offsets into <see cref="GetText"/>.</summary>
    public (int Start, int End) SelectionOffsets
    {
        get
        {
            var primary = _model.Primary;
            return ((int)_model.Buffer.OffsetOf(primary.Start), (int)_model.Buffer.OffsetOf(primary.End));
        }
    }

    // Menu-facing wrappers over the keyboard paths.
    public void CopyToClipboard() => CopySelection();

    public void CutToClipboard() => CutSelection();

    public void PasteFromClipboard() => _ = PasteAsync();

    public void SelectEverything() => _model.SelectAll();

    public void DeleteSelection() => _model.Delete();

    public bool CanUndo => _model.CanUndo;

    public bool CanRedo => _model.CanRedo;

    public void Undo() => _model.Undo();

    public void Redo() => _model.Redo();

    /// <summary>Replaces the selections (or inserts at the carets) as one undo step.</summary>
    public void ReplaceSelection(string text) => _model.Paste(text);

    /// <summary>Swaps the whole buffer for new text as one undo step, keeping the caret near its old line.</summary>
    public void ReplaceAllText(string text)
    {
        var line = _model.Primary.Caret.Line;
        _model.ReplaceRange(default, _model.Buffer.End, text);
        _model.SetCaret(new DocumentPosition(Math.Min(line, _model.Buffer.LineCount - 1), 0));
    }

    /// <summary>Replaces the buffer. Undo history restarts; the caret goes to the top.</summary>
    public void LoadText(string text)
    {
        _model.Load(text);
        SetScroll(0, 0);
        RaiseTextChanged();
    }

    /// <summary>Puts the caret (and optionally extends the selection) at a position.</summary>
    public void SetCursorPosition(int line, int col, bool select = false, bool scrollIntoView = true)
    {
        _model.SetCaret(new DocumentPosition(Math.Max(0, line), Math.Max(0, col)), extend: select);
        if (scrollIntoView)
        {
            RevealCaret();
        }
    }

    /// <summary>Scrolls a line into the vertical middle of the view.</summary>
    public void ScrollLineToCenter(int line)
    {
        EnsureMetrics();
        line = Math.Clamp(line, 0, _model.Buffer.LineCount - 1);
        var target = (FirstRowOf(line) + 0.5) * _lineHeight - ViewportHeight / 2;
        ScrollTo(null, Math.Max(0, target), animate: false);
    }

    /// <summary>Moves the view so the caret's column is visible.</summary>
    public void ScrollIntoViewHorizontally() => RevealCaret();

    /// <summary>Sets the language the buffer is coloured with.</summary>
    public void SelectSyntaxHighlightingById(string? languageId)
    {
        _highlighter.LanguageId = languageId;
        DropAllVisuals();
        QueueTokenizing();
        Invalidate();
    }

    /// <summary>The id of the language the buffer is coloured with, or null for plain text.</summary>
    public string? LanguageId => _highlighter.LanguageId;

    private string? _languageId => _highlighter.LanguageId;

    /// <summary>The buffer-space visual row a position starts in, for overlays mapping lines to y.</summary>
    public int VisualRowOfPosition(int line, int col)
    {
        EnsureMetrics();
        var position = _model.Buffer.Clamp(new DocumentPosition(line, col));
        return FirstRowOf(position.Line) + RowInLineOf(position.Line, position.Col);
    }

    /// <summary>How many visual rows a logical line folds into.</summary>
    public int VisualRowsOf(int line)
    {
        EnsureMetrics();
        return RowCountOf(Math.Clamp(line, 0, _model.Buffer.LineCount - 1));
    }

    /// <summary>The row box a position is drawn in, relative to this control at the current scroll.</summary>
    public Windows.Foundation.Rect PositionBounds(int line, int col)
    {
        EnsureMetrics();
        var position = _model.Buffer.Clamp(new DocumentPosition(line, col));
        var (row, x) = Locate(position);
        return new Windows.Foundation.Rect(TextLeft + x - _scrollX, row * _lineHeight - _scrollY, CaretWidth, _lineHeight);
    }

    // ------------------------------------------------------------------ events

    public event EditorTextEventHandler? TextChanged;
    public event EditorSelectionChangedEventHandler? SelectionChanged;
    public event EventHandler? ZoomChanged;

    /// <summary>Raised whenever the scroll offsets changed, so overlays can move with the text.</summary>
    public event EventHandler? ScrollChanged;

    private void RaiseTextChanged()
    {
        _docWordsDirty = true;
        TextChanged?.Invoke(this);
    }

    private void OnModelEdited()
    {
        RevealCaret();
        RaiseTextChanged();
    }

    private void OnSelectionsChanged()
    {
        RestartCaretBlink();
        QueueOccurrences();
        _marksDirty = true;
        Invalidate();
        var caret = _model.Primary.Caret;
        SelectionChanged?.Invoke(this, new EditorSelectionEventArgs
        {
            LineNumber = caret.Line,
            CharacterPositionInLine = caret.Col + 1,
        });
    }

    private void RestartCaretBlink()
    {
        if (_closed)
        {
            return;
        }

        _caretVisible = true;
        _caretTimer.Stop();
        if (FocusState != FocusState.Unfocused)
        {
            _caretTimer.Start();
        }

        Invalidate();
    }

    // ------------------------------------------------------------------ settings

    private void OnFontMetricsChanged()
    {
        ResetMetrics();
        Invalidate();
        ZoomChanged?.Invoke(this, EventArgs.Empty);
    }

    /// <summary>
    /// Applies the Settings values: font size (unless this tab was zoomed away from the last
    /// setting), tab width, line numbers, highlighting and wrap.
    /// </summary>
    public void ApplyAppSettings()
    {
        var tabWidthChanged = _model.TabWidth != AppSettings.EditorTabWidth;
        _model.TabWidth = AppSettings.EditorTabWidth;

        var size = Math.Clamp(AppSettings.EditorFontSize, MinFontSize, MaxFontSize);
        if (double.IsFinite(FontSize) && Math.Abs(FontSize - _settingsFontSize) < 0.1)
        {
            FontSize = size;
        }

        _settingsFontSize = size;
        var wrapChanged = WordWrap != AppSettings.EditorWordWrap;
        WordWrap = AppSettings.EditorWordWrap;
        ShowLineNumbers = AppSettings.EditorLineNumbers;
        _highlighter.Enabled = AppSettings.EditorHighlighting;

        if (tabWidthChanged || wrapChanged)
        {
            ResetMetrics();
        }

        DropAllVisuals();
        QueueTokenizing();
        Invalidate();
    }

    // ------------------------------------------------------------------ theme colours

    private Windows.UI.Color _baseText = Microsoft.UI.Colors.Black;
    private Windows.UI.Color _lineNumberText = Microsoft.UI.Colors.Gray;
    private Windows.UI.Color _activeLineNumberText = Microsoft.UI.Colors.Black;
    private Windows.UI.Color _selectionColor = Microsoft.UI.Colors.LightBlue;
    private Windows.UI.Color _inactiveSelectionColor = Microsoft.UI.Colors.LightGray;
    private Windows.UI.Color _activeLineColor = Microsoft.UI.Colors.LightGray;
    private Windows.UI.Color _caretColor = Microsoft.UI.Colors.Black;
    private Windows.UI.Color _occurrenceColor = Microsoft.UI.Colors.LightGray;
    private Windows.UI.Color _wordHighlightColor = Microsoft.UI.Colors.LightGray;
    private Windows.UI.Color _bookmarkColor = Microsoft.UI.Colors.SteelBlue;
    private Windows.UI.Color _bookmarkLineColor = Microsoft.UI.Colors.LightGray;
    private Windows.UI.Color _panelColor = Microsoft.UI.Colors.White;
    private Windows.UI.Color _panelBorderColor = Microsoft.UI.Colors.Gray;
    private Windows.UI.Color _thumbColor = Microsoft.UI.Colors.Gray;
    private Windows.UI.Color _trackColor = Microsoft.UI.Colors.Transparent;
    private Windows.UI.Color _surfaceColor = Microsoft.UI.Colors.White;
    private bool _light;

    private void RefreshThemeColors()
    {
        // Application.Resources resolves the app theme, not this element's, so a Light override
        // on the window would still hand back dark values: look in the matching theme dictionary.
        var resources = Application.Current.Resources;
        _light = ActualTheme == ElementTheme.Light;
        var themeKey = _light ? "Light" : "Default";
        Windows.UI.Color FromBrush(string key, Windows.UI.Color fallback)
        {
            foreach (var merged in resources.MergedDictionaries)
            {
                if (merged.ThemeDictionaries.TryGetValue(themeKey, out var theme)
                    && theme is ResourceDictionary dictionary
                    && dictionary.TryGetValue(key, out var value)
                    && value is SolidColorBrush brush)
                {
                    return brush.Color;
                }
            }

            return resources.TryGetValue(key, out var any) && any is SolidColorBrush b ? b.Color : fallback;
        }

        _baseText = FromBrush("TextFillColorPrimaryBrush", Microsoft.UI.Colors.White);
        _lineNumberText = FromBrush("TextFillColorTertiaryBrush", Microsoft.UI.Colors.Gray);
        if (_light)
        {
            // Fluent's light text is a translucent black that greys out over the editor's pale
            // surface; code reads better in a solid, near-black ink.
            _baseText = Windows.UI.Color.FromArgb(0xFF, 0x1F, 0x23, 0x28);
            _lineNumberText = Windows.UI.Color.FromArgb(0xFF, 0x8A, 0x92, 0x9A);
            _activeLineNumberText = Windows.UI.Color.FromArgb(0xFF, 0x24, 0x29, 0x2F);
        }
        else
        {
            _lineNumberText = Windows.UI.Color.FromArgb(0xFF, 0x6E, 0x76, 0x81);
            _activeLineNumberText = Windows.UI.Color.FromArgb(0xFF, 0xD0, 0xD7, 0xDE);
        }

        _caretColor = _baseText;
        var accent = FromBrush("AccentFillColorDefaultBrush", Windows.UI.Color.FromArgb(0xFF, 0x3B, 0x82, 0xF6));
        _selectionColor = Windows.UI.Color.FromArgb(0x48, accent.R, accent.G, accent.B);
        _inactiveSelectionColor = Windows.UI.Color.FromArgb(0x28, accent.R, accent.G, accent.B);
        _occurrenceColor = Windows.UI.Color.FromArgb(0x30, accent.R, accent.G, accent.B);
        _wordHighlightColor = _light
            ? Windows.UI.Color.FromArgb(0x24, 0x00, 0x00, 0x00)
            : Windows.UI.Color.FromArgb(0x26, 0xFF, 0xFF, 0xFF);
        _activeLineColor = _light
            ? Windows.UI.Color.FromArgb(0x0C, 0x00, 0x00, 0x00)
            : Windows.UI.Color.FromArgb(0x0E, 0xFF, 0xFF, 0xFF);
        _bookmarkColor = Windows.UI.Color.FromArgb(0xFF, 0x3B, 0x82, 0xF6);
        _bookmarkLineColor = Windows.UI.Color.FromArgb(0x16, 0x3B, 0x82, 0xF6);
        // An opaque surface for floating panels (the diff peek); fixed per theme so a resource
        // that resolves against the app theme can never hand a dark panel to a light editor.
        _panelColor = _light ? Windows.UI.Color.FromArgb(0xFF, 0xFB, 0xFB, 0xFB) : Windows.UI.Color.FromArgb(0xFF, 0x25, 0x25, 0x26);
        _panelBorderColor = _light ? Windows.UI.Color.FromArgb(0x30, 0, 0, 0) : Windows.UI.Color.FromArgb(0x40, 0xFF, 0xFF, 0xFF);
        _thumbColor = _light ? Windows.UI.Color.FromArgb(0xFF, 0x50, 0x50, 0x50) : Windows.UI.Color.FromArgb(0xFF, 0xC8, 0xC8, 0xC8);
        _trackColor = _light ? Windows.UI.Color.FromArgb(0x0C, 0, 0, 0) : Windows.UI.Color.FromArgb(0x10, 0xFF, 0xFF, 0xFF);

        // The swap chain is cleared to this each frame. It must be opaque: a transparent swap
        // chain is composited against the window's Mica backdrop, not the XAML pane behind it,
        // so the wallpaper's colours bled through as a tinted gradient behind the code.
        var surface = FromBrush("ShellCentreBackgroundBrush",
            _light ? Microsoft.UI.Colors.White : Windows.UI.Color.FromArgb(0xFF, 0x1B, 0x1C, 0x20));
        _surfaceColor = Windows.UI.Color.FromArgb(0xFF, surface.R, surface.G, surface.B);
    }

    // ------------------------------------------------------------------ helpers

    private static bool IsDown(VirtualKey key) =>
        InputKeyboardSource.GetKeyStateForCurrentThread(key).HasFlag(CoreVirtualKeyStates.Down);

    private static bool IsCtrlDown() => IsDown(VirtualKey.Control);

    private static bool IsShiftDown() => IsDown(VirtualKey.Shift);

    private static bool IsAltDown() => IsDown(VirtualKey.Menu);
}
