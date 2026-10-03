using Codale.App.ViewModels;
using Codale.Core.Helper;

using Microsoft.UI.Input;
using Microsoft.UI.Text;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Hosting;
using Microsoft.UI.Xaml.Media;

using Windows.System;
using Windows.UI.Core;

namespace Codale.App.Views;

/// <summary>
/// The editor's right-click menu: the everyday edit commands, plus the model-backed
/// ones (format, ask Claude, check for issues) and the rendered preview.
/// </summary>
public sealed partial class EditorTab
{
    /// <summary>Longest stretch of the file sent to the model around the cursor or in a whole-file job.</summary>
    private const int MaxContextChars = 60_000;

    private MenuFlyoutItem _cutItem = null!;
    private MenuFlyoutItem _copyItem = null!;
    private MenuFlyoutItem _pasteItem = null!;
    private MenuFlyoutItem _deleteItem = null!;
    private MenuFlyoutItem _undoItem = null!;
    private MenuFlyoutItem _redoItem = null!;
    private MenuFlyoutItem _formatItem = null!;
    private MenuFlyoutItem _askItem = null!;
    private MenuFlyoutItem _checkItem = null!;
    private MenuFlyoutItem _previewItem = null!;

    private bool _modelBusy;
    private bool _previewing;

    /// <summary>The one-shot model for format, ask and check; set by the workspace.</summary>
    public IHelperModel? Helper { get; set; }

    /// <summary>The project folder Ask Claude searches for code related to the request; set by the workspace.</summary>
    public string? ProjectRoot { get; set; }

    /// <summary>The Ask panel's model job: the log it streams, its clock and its cancel.</summary>
    private readonly AskSessionViewModel _session = new();

    /// <summary>Resolves with the instruction when the composer sends, or null when it closes first.</summary>
    private TaskCompletionSource<string?>? _askSend;

    /// <summary>Bumped per job so a finished job's delayed panel hide cannot hide the next job's.</summary>
    private int _askVersion;

    /// <summary>
    /// The text the composer floats beside: the selection's first and last positions
    /// (0-based line, raw column), or the cursor twice. Null docks the panel at the bottom.
    /// </summary>
    private (int Line, int Col, int EndLine, int EndCol)? _askAnchor;

    /// <summary>The outcome a job ends with when it is stopped before the reply is applied.</summary>
    private const string CancelledOutcome = "Stopped - the document was left as it was";

    private const double AskPanelWidth = 560;
    private const double AskPanelInset = 12;
    private const double AskPanelGap = 6;

    /// <summary>Room below the anchor that keeps the panel under it rather than flipping above.</summary>
    private const double AskPanelRoom = 300;

    /// <summary>How long a finished Format or Check job's log stays up when there is no composer to close.</summary>
    private static readonly TimeSpan LogOnlyLinger = TimeSpan.FromSeconds(6);

    private static MenuFlyoutItem Item(string text, Action click, string? accelerator = null, string? glyph = null)
    {
        var item = new MenuFlyoutItem { Text = text, KeyboardAcceleratorTextOverride = accelerator ?? string.Empty };
        if (glyph is not null)
        {
            item.Icon = new FontIcon { Glyph = glyph };
        }
        item.Click += (_, _) => click();
        return item;
    }

    private void BuildContextMenu()
    {
        _undoItem = Item("Undo", FileEditor.Undo, "Ctrl+Z", "");
        _redoItem = Item("Redo", FileEditor.Redo, "Ctrl+Y", "");
        _cutItem = Item("Cut", FileEditor.CutToClipboard, "Ctrl+X", "");
        _copyItem = Item("Copy", FileEditor.CopyToClipboard, "Ctrl+C", "");
        _pasteItem = Item("Paste", FileEditor.PasteFromClipboard, "Ctrl+V", "");
        _deleteItem = Item("Delete", FileEditor.DeleteSelection, null, "");
        var selectAll = Item("Select All", FileEditor.SelectEverything, "Ctrl+A", "");
        _formatItem = Item("Format Document", () => _ = FormatDocumentAsync(), null, "");
        _askItem = Item("Ask Claude…", () => _ = AskClaudeAsync(), null, "");
        _checkItem = Item("Check Issues", () => _ = CheckIssuesAsync(), null, "");
        _previewItem = Item("Preview", TogglePreview, null, "");

        var menu = new MenuFlyout();
        foreach (var item in new MenuFlyoutItemBase[]
        {
            _undoItem, _redoItem, new MenuFlyoutSeparator(),
            _cutItem, _copyItem, _pasteItem, _deleteItem, selectAll, new MenuFlyoutSeparator(),
            _formatItem, _askItem, _checkItem, _previewItem,
        })
        {
            menu.Items.Add(item);
        }

        _session.Entries.CollectionChanged += OnAskEntriesChanged;
        _session.PropertyChanged += OnAskSessionChanged;
        FileEditor.ScrollChanged += (_, _) => PositionAskPanel();
        EditorSurface.SizeChanged += (_, _) => PositionAskPanel();
        AskPanel.SizeChanged += (_, _) => PositionAskPanel();

        // A tab switched away from must not leave the panel floating over the next one;
        // coming back brings it back where it was.
        Loaded += (_, _) =>
        {
            AskPanel.RequestedTheme = ActualTheme;
            if (_askShown)
            {
                AskPopup.IsOpen = true;
                PositionAskPanel();
            }
        };
        Unloaded += (_, _) => AskPopup.IsOpen = false;
        ActualThemeChanged += (_, _) => AskPanel.RequestedTheme = ActualTheme;

        menu.Opening += (_, _) => RefreshContextMenu();
        FileEditor.ContextFlyout = menu;

        // While previewing, the only thing to do is go back to the text.
        var back = new MenuFlyout();
        back.Items.Add(Item("Edit Markdown", TogglePreview, null, ""));
        MarkdownPreviewHost.ContextFlyout = back;
    }

    private void RefreshContextMenu()
    {
        var editable = ViewModel is { CanEdit: true };
        var hasSelection = FileEditor.HasSelection;

        _undoItem.IsEnabled = editable && FileEditor.CanUndo;
        _redoItem.IsEnabled = editable && FileEditor.CanRedo;
        _cutItem.IsEnabled = editable && hasSelection;
        _copyItem.IsEnabled = hasSelection;
        _pasteItem.IsEnabled = editable;
        _deleteItem.IsEnabled = editable && hasSelection;

        var canAsk = editable && Helper is { IsAvailable: true } && !IsModelBusy;
        _formatItem.IsEnabled = canAsk;
        _askItem.IsEnabled = canAsk;
        _checkItem.IsEnabled = canAsk;

        _checkItem.Text = hasSelection ? "Check Issues in Selection" : "Check Issues";

        var kind = PreviewKind();
        _previewItem.Visibility = kind == PreviewTarget.None ? Visibility.Collapsed : Visibility.Visible;
        _previewItem.Text = kind == PreviewTarget.Browser ? "Preview in Browser" : "Preview";
    }

    // ------------------------------------------------------------------ preview

    private enum PreviewTarget
    {
        None,
        Markdown,
        Browser,
    }

    private PreviewTarget PreviewKind()
    {
        if (ViewModel?.FilePath is not { } path)
        {
            return PreviewTarget.None;
        }

        return Path.GetExtension(path).ToLowerInvariant() switch
        {
            ".md" or ".markdown" => PreviewTarget.Markdown,
            ".html" or ".htm" => PreviewTarget.Browser,
            _ => PreviewTarget.None,
        };
    }

    private void TogglePreview()
    {
        switch (PreviewKind())
        {
            case PreviewTarget.Markdown:
                _previewing = !_previewing;
                if (_previewing)
                {
                    MarkdownPreview.BasePath = ViewModel.FilePath is { } file ? Path.GetDirectoryName(file) : null;
                    MarkdownPreview.Markdown = FileEditor.GetText();
                }

                MarkdownPreviewHost.Visibility = _previewing ? Visibility.Visible : Visibility.Collapsed;
                FileEditor.Visibility = _previewing ? Visibility.Collapsed : Visibility.Visible;
                if (!_previewing)
                {
                    FileEditor.Focus(FocusState.Programmatic);
                }

                break;

            case PreviewTarget.Browser:
                OnOpenExternallyClick(this, new RoutedEventArgs());
                break;
        }
    }

    // ------------------------------------------------------------------ model-backed actions

    private bool IsModelBusy => _modelBusy;

    /// <summary>
    /// Runs a model job with the busy flag and the Ask panel's log, which can cancel it.
    /// <paramref name="apply"/> turns the reply into the edit and says how it went; failures
    /// become the tab's message too. Null when nothing came back. <paramref name="fromComposer"/>
    /// says the composer is already open; otherwise the panel opens in log-only mode.
    /// </summary>
    private async Task<string?> RunModelAsync(
        string label,
        string systemPrompt,
        string prompt,
        Func<string, (string Outcome, bool IsError)> apply,
        Func<CancellationToken, Task<string>>? gather = null,
        bool fromComposer = false)
    {
        if (Helper is not { } helper || IsModelBusy)
        {
            return null;
        }

        _modelBusy = true;
        ViewModel.Message = null;
        var version = ++_askVersion;
        if (!fromComposer)
        {
            OpenAskPanel(composer: false);
        }

        var ct = _session.Begin();
        AskLogTitle.Text = label;
        (string Outcome, bool IsError) result = (CancelledOutcome, false);
        string? reply = null;
        try
        {
            if (gather is not null)
            {
                prompt += await gather(ct);
            }

            _session.Log(AskLogKind.Info, "Waiting for Claude's reply…");
            reply = await helper.CompleteAsync(systemPrompt, prompt, 32_000, ct);
            _session.Log(AskLogKind.Info, "Applying the result…");
            result = apply(reply);
            return reply;
        }
        catch (OperationCanceledException)
        {
            return null;
        }
        catch (HelperModelException ex)
        {
            ViewModel.Message = ex.Message;
            result = (ex.Message, true);
            return null;
        }
        finally
        {
            _modelBusy = false;
            if (result.IsError && reply is not null)
            {
                ViewModel.Message = result.Outcome;
            }

            _session.End(result.Outcome, result.IsError);
            AskLogTitle.Text = result.IsError ? "Failed" : result.Outcome == CancelledOutcome ? "Stopped" : "Done";
            if (!fromComposer)
            {
                _ = HideAskPanelAfterLingerAsync(version);
            }
        }
    }

    private async Task HideAskPanelAfterLingerAsync(int version)
    {
        await Task.Delay(LogOnlyLinger);
        if (version == _askVersion && !_modelBusy && AskComposer.Visibility == Visibility.Collapsed)
        {
            HideAskPopup();
        }
    }

    private static int CountLines(string text) => text.Length == 0 ? 0 : text.TrimEnd('\r', '\n').Count(c => c == '\n') + 1;

    private static string Lines(int count) => count == 1 ? "1 line" : $"{count} lines";

    /// <summary>Models like to wrap code in a fence; the buffer wants the code alone.</summary>
    private static string StripFence(string reply)
    {
        var text = reply.Trim();
        if (!text.StartsWith("```", StringComparison.Ordinal))
        {
            return reply;
        }

        var firstBreak = text.IndexOf('\n');
        var lastFence = text.LastIndexOf("```", StringComparison.Ordinal);
        return firstBreak < 0 || lastFence <= firstBreak ? reply : text[(firstBreak + 1)..lastFence].TrimEnd('\r', '\n');
    }

    private async Task FormatDocumentAsync()
    {
        var original = FileEditor.GetText();
        if (original.Length == 0 || original.Length > MaxContextChars)
        {
            if (original.Length > MaxContextChars)
            {
                ViewModel.Message = "This file is too large to format with the model.";
            }

            return;
        }

        var language = Path.GetExtension(ViewModel.FilePath ?? string.Empty);
        await RunModelAsync(
            "Formatting document…",
            "You are a code formatter. Reformat the file the user sends: fix indentation, spacing, line breaks and "
            + "wrapping to the language's conventional style. Never change behaviour, names, comments' meaning or the "
            + "order of code. Reply with the complete formatted file only - no explanation, no code fence.",
            $"File type: {language}\n\n{original}",
            reply =>
            {
                var formatted = StripFence(reply).Replace("\r\n", "\n").Replace('\r', '\n');
                if (formatted.Trim().Length == 0)
                {
                    return ("The model returned nothing; the document was left as it was.", true);
                }

                // The buffer moved on while the model worked: applying would clobber those edits.
                if (FileEditor.GetText() != original)
                {
                    return ("The document changed while formatting; run Format Document again.", true);
                }

                FileEditor.ReplaceAllText(formatted);
                return ("Document formatted", false);
            });
    }

    /// <summary>Most selected lines the Ask Claude dialog shows back before eliding the rest.</summary>
    private const int MaxPreviewLines = 6;

    private async Task AskClaudeAsync()
    {
        var text = FileEditor.GetText();
        var (start, end) = FileEditor.SelectionOffsets;
        var selected = text[start..end];
        var cursor = FileEditor.CursorPosition;
        var hasSelection = start != end;

        var instruction = await PromptForInstructionAsync(text, start, end, selected, cursor.LineNumber + 1);
        if (instruction is null)
        {
            return;
        }
        var before = text[Math.Max(0, start - MaxContextChars / 2)..start];
        var after = text[end..Math.Min(text.Length, end + MaxContextChars / 2)];

        await RunModelAsync(
            hasSelection ? "Claude is rewriting the selection…" : "Claude is writing at the cursor…",
            "You edit code at a precise spot. The user sends the text before the edit point, the selected text "
            + "(possibly empty), the text after, and an instruction. Reply with only the text that should replace "
            + "the selection - or be inserted at the cursor when the selection is empty. Match the file's indentation "
            + "and style. When <related> code from other files is given, use it: follow the names, signatures and "
            + "conventions it shows instead of guessing. No explanation and no code fence.",
            $"File: {ViewModel.FileName} (cursor line {cursor.LineNumber + 1})\n\n"
            + $"<before>\n{before}\n</before>\n<selection>\n{selected}\n</selection>\n<after>\n{after}\n</after>\n\n"
            + $"Instruction: {instruction}",
            reply =>
            {
                if (FileEditor.GetText() != text)
                {
                    return ("The document changed while Claude worked; ask again.", true);
                }

                var edit = StripFence(reply);
                FileEditor.ReplaceSelection(edit);
                var lines = Lines(CountLines(edit));
                return (hasSelection ? $"Replaced selection with {lines}" : $"Inserted {lines}", false);
            },
            ct => GatherRelatedCodeAsync(instruction, selected, ct),
            fromComposer: true);
    }

    private const int RelatedChars = 12_000;
    private const int RelatedSectionLines = 60;

    /// <summary>
    /// Lets the search agent look through the project for code the request touches - callers,
    /// definitions, sibling implementations - so the edit is not written from one file alone.
    /// A failed or empty search just means the edit is made with the file's own context.
    /// </summary>
    private async Task<string> GatherRelatedCodeAsync(string instruction, string selected, CancellationToken ct)
    {
        if (Helper is not { } helper || string.IsNullOrEmpty(ProjectRoot) || !Directory.Exists(ProjectRoot))
        {
            return "";
        }

        try
        {
            var snippet = selected.Length > 600 ? selected[..600] : selected;
            var query =
                $"I am editing {ViewModel.FileName}. Task: {instruction}\n" +
                (snippet.Length > 0 ? $"Selected code:\n{snippet}\n" : "") +
                "Find the code in OTHER files that this task depends on or must stay consistent with " +
                "(definitions, callers, related types, conventions).";
            var loop = new Codale.Search.SearchAgentLoop(ProjectRoot, new Codale.Search.HelperSearchModel(helper))
            {
                Budget = TimeSpan.FromSeconds(75),
                MaxSteps = 8,
                OnStep = step => _session.Log(
                    AskLogKind.Search,
                    step.ResultCount > 0 ? $"{step.Description} · {step.ResultCount} found" : step.Description),
            };
            _session.Log(AskLogKind.Info, "Searching the project for related code…");
            var answer = await loop.RunAsync(query, ct);

            var text = new System.Text.StringBuilder();
            foreach (var section in answer.Sections)
            {
                var path = section.RelativePath.Length > 0 ? section.RelativePath : section.FilePath;
                if (string.Equals(Path.GetFullPath(section.FilePath), ViewModel.FilePath is null ? "" : Path.GetFullPath(ViewModel.FilePath), StringComparison.OrdinalIgnoreCase))
                {
                    continue;
                }

                var code = section.Code.Replace("\r\n", "\n").Split('\n').Take(RelatedSectionLines);
                text.Append($"// {path}:{section.StartLine}-{section.EndLine}\n").Append(string.Join('\n', code)).Append("\n\n");
                if (text.Length > RelatedChars)
                {
                    break;
                }
            }

            if (text.Length == 0 && answer.Summary.Length == 0)
            {
                return "";
            }

            return $"\n\n<related>\n{answer.Summary.Trim()}\n\n{text}</related>";
        }
        catch (HelperModelException)
        {
            return "";
        }
    }
    // ------------------------------------------------------------------ Ask panel

    /// <summary>
    /// Opens the composer over the editor: where the edit lands, the selection it will
    /// replace, and the instruction box. Enter sends, Shift+Enter breaks the line, Esc
    /// closes. Null when closed or left blank; otherwise the panel stays up to stream progress.
    /// </summary>
    private async Task<string?> PromptForInstructionAsync(string text, int start, int end, string selected, int cursorLine)
    {
        _askSend?.TrySetResult(null);
        var hasSelection = start != end;

        string where;
        if (hasSelection)
        {
            var firstLine = LineOf(text, start);
            var lastLine = LineOf(text, end);
            where = firstLine == lastLine
                ? $"Rewrite the selection on line {firstLine}"
                : $"Rewrite {Lines(CountLines(selected))} selected (lines {firstLine}–{lastLine})";
        }
        else
        {
            where = $"Write at the cursor on line {cursorLine}";
        }

        AskContextIcon.Glyph = hasSelection ? "\uE70F" : "\uE710";
        AskContextText.Text = $"{ViewModel.FileName}  ·  {where}";
        _askAnchor = (LineOf(text, start) - 1, ColumnOf(text, start), LineOf(text, end) - 1, ColumnOf(text, end));

        if (hasSelection)
        {
            var previewLines = selected.Replace("\r\n", "\n").TrimEnd('\n').Split('\n');
            var preview = string.Join('\n', previewLines.Take(MaxPreviewLines));
            if (previewLines.Length > MaxPreviewLines)
            {
                preview += $"\n… {Lines(previewLines.Length - MaxPreviewLines)} more";
            }

            AskSelectionText.Text = preview;
        }

        AskSelectionBox.Visibility = hasSelection ? Visibility.Visible : Visibility.Collapsed;
        AskInput.PlaceholderText = hasSelection
            ? "What should Claude do with this? e.g. add null checks, convert to async…"
            : "What should Claude write here? e.g. a method that parses the header…";
        AskInput.Text = string.Empty;
        AskInput.IsReadOnly = false;
        RefreshAskSendButton();
        AskLogRows.Children.Clear();
        AskLogArea.Visibility = Visibility.Collapsed;

        OpenAskPanel(composer: true);

        // The popup's content is only in the tree once it has been laid out.
        DispatcherQueue.TryEnqueue(
            Microsoft.UI.Dispatching.DispatcherQueuePriority.Low,
            () => AskInput.Focus(FocusState.Programmatic));

        var send = _askSend = new TaskCompletionSource<string?>();
        var instruction = await send.Task;
        return string.IsNullOrWhiteSpace(instruction) ? null : instruction.Trim();
    }

    private void OpenAskPanel(bool composer)
    {
        if (!composer)
        {
            _askAnchor = null;
        }

        _askMoved = null;
        AskComposer.Visibility = composer ? Visibility.Visible : Visibility.Collapsed;
        AskStopButton.Visibility = !composer && _session.IsRunning ? Visibility.Visible : Visibility.Collapsed;
        _askShown = true;
        RefreshAskLightDismiss();
        AskPopup.IsOpen = IsLoaded;
        PositionAskPanel();
    }

    /// <summary>Whether the panel is meant to be up; the popup itself is closed while the tab is off screen.</summary>
    private bool _askShown;

    private void HideAskPopup()
    {
        _askShown = false;
        AskPopup.IsOpen = false;
    }

    /// <summary>
    /// A composer waiting for an instruction (or showing a finished reply) closes on a click
    /// outside it, like a flyout. While a job runs it stays up until Stop or the close button.
    /// </summary>
    private void RefreshAskLightDismiss() =>
        AskPopup.IsLightDismissEnabled = AskComposer.Visibility == Visibility.Visible && !_session.IsRunning;

    /// <summary>Closed by light dismiss rather than by our own code: treat it as the close button.</summary>
    private void OnAskPopupClosed(object? sender, object e)
    {
        if (_askShown && IsLoaded)
        {
            CloseAskPanel();
        }
    }

    /// <summary>
    /// Floats the panel just under the anchored text, or just above it when the room
    /// below is short - aligned so it grows away from the text either way - and keeps it
    /// inside the editor. Without an anchor it docks at the bottom centre.
    /// </summary>
    private void PositionAskPanel()
    {
        if (!AskPopup.IsOpen || EditorSurface.ActualWidth <= 0)
        {
            return;
        }

        var surfaceWidth = EditorSurface.ActualWidth;
        var surfaceHeight = EditorSurface.ActualHeight;
        AskLayer.Width = surfaceWidth;
        AskLayer.Height = surfaceHeight;
        var width = Math.Max(0, Math.Min(AskPanelWidth, surfaceWidth - 2 * AskPanelInset));
        AskPanel.Width = width;

        // Moved by hand: stay put, only kept inside the editor as it resizes.
        if (_askMoved is { } moved)
        {
            PlaceAskPanel(moved.X, moved.Y);
            return;
        }

        if (_askAnchor is not { } anchor)
        {
            AskPanel.HorizontalAlignment = HorizontalAlignment.Center;
            AskPanel.VerticalAlignment = VerticalAlignment.Bottom;
            AskPanel.Margin = new Thickness(AskPanelInset, AskPanelInset, AskPanelInset, 16);
            return;
        }

        var origin = FileEditor.TransformToVisual(EditorSurface).TransformPoint(default);
        var first = FileEditor.PositionBounds(anchor.Line, anchor.Col);
        var last = FileEditor.PositionBounds(anchor.EndLine, anchor.EndCol);

        // A selection ending at column 0 stops on the line before; do not cover the line after it.
        var spansLines = anchor.EndLine > anchor.Line;
        var top = Math.Clamp(origin.Y + first.Y, 0, surfaceHeight);
        var bottom = Math.Clamp(origin.Y + (spansLines && anchor.EndCol == 0 ? last.Y : last.Bottom), 0, surfaceHeight);

        // Start a little left of the text so the input's own inset lines up with it.
        var x = origin.X + (spansLines ? Math.Min(first.X, last.X) : first.X) - 20;
        var left = Math.Clamp(x, AskPanelInset, Math.Max(AskPanelInset, surfaceWidth - width - AskPanelInset));

        AskPanel.HorizontalAlignment = HorizontalAlignment.Left;
        var below = surfaceHeight - bottom;
        if (below >= AskPanelRoom || below >= top)
        {
            AskPanel.VerticalAlignment = VerticalAlignment.Top;
            AskPanel.Margin = new Thickness(left, bottom + AskPanelGap, 0, AskPanelInset);
        }
        else
        {
            AskPanel.VerticalAlignment = VerticalAlignment.Bottom;
            AskPanel.Margin = new Thickness(left, AskPanelInset, 0, surfaceHeight - top + AskPanelGap);
        }
    }

    /// <summary>Puts the panel's top-left corner at a point in the editor, kept inside it; returns where it went.</summary>
    private Windows.Foundation.Point PlaceAskPanel(double x, double y)
    {
        var left = Math.Clamp(x, AskPanelInset, Math.Max(AskPanelInset, EditorSurface.ActualWidth - AskPanel.ActualWidth - AskPanelInset));
        var top = Math.Clamp(y, AskPanelInset, Math.Max(AskPanelInset, EditorSurface.ActualHeight - AskPanel.ActualHeight - AskPanelInset));
        AskPanel.HorizontalAlignment = HorizontalAlignment.Left;
        AskPanel.VerticalAlignment = VerticalAlignment.Top;
        AskPanel.Margin = new Thickness(left, top, 0, AskPanelInset);
        return new Windows.Foundation.Point(left, top);
    }

    // ------------------------------------------------------------------ dragging

    /// <summary>Where the panel was dragged to, in the editor's coordinates; null while it follows its anchor.</summary>
    private Windows.Foundation.Point? _askMoved;

    /// <summary>The pointer's offset from the panel's corner while a drag is under way.</summary>
    private Windows.Foundation.Point? _askGrab;

    private void OnAskDragStarted(object sender, Microsoft.UI.Xaml.Input.PointerRoutedEventArgs e)
    {
        var point = e.GetCurrentPoint(AskLayer);
        if (!point.Properties.IsLeftButtonPressed || sender is not UIElement handle)
        {
            return;
        }

        var corner = AskPanel.TransformToVisual(AskLayer).TransformPoint(default);
        _askGrab = new Windows.Foundation.Point(point.Position.X - corner.X, point.Position.Y - corner.Y);
        handle.CapturePointer(e.Pointer);
        e.Handled = true;
    }

    private void OnAskDragMoved(object sender, Microsoft.UI.Xaml.Input.PointerRoutedEventArgs e)
    {
        if (_askGrab is not { } grab)
        {
            return;
        }

        // The layer sits on the editor's top-left corner, so its coordinates are the editor's.
        var point = e.GetCurrentPoint(AskLayer).Position;
        _askMoved = PlaceAskPanel(point.X - grab.X, point.Y - grab.Y);
        e.Handled = true;
    }

    private void OnAskDragEnded(object sender, Microsoft.UI.Xaml.Input.PointerRoutedEventArgs e)
    {
        if (_askGrab is null)
        {
            return;
        }

        _askGrab = null;
        (sender as UIElement)?.ReleasePointerCapture(e.Pointer);
        if (AskComposer.Visibility == Visibility.Visible)
        {
            AskInput.Focus(FocusState.Programmatic);
        }
    }

    /// <summary>Closes the panel; a job still running is cancelled, a composer still waiting is dismissed.</summary>
    private void CloseAskPanel()
    {
        _session.CancelCommand.Execute(null);
        var send = _askSend;
        _askSend = null;
        send?.TrySetResult(null);
        HideAskPopup();
        FileEditor.Focus(FocusState.Programmatic);
    }

    private void OnAskCloseClick(object sender, RoutedEventArgs e) => CloseAskPanel();

    private void OnAskInputTextChanged(object sender, TextChangedEventArgs e) => RefreshAskSendButton();

    /// <summary>The field around the text box shows focus, since the box itself is stripped of chrome.</summary>
    private void OnAskInputFocusChanged(object sender, RoutedEventArgs e) =>
        AskInputField.BorderBrush = Resource(
            AskInput.FocusState == FocusState.Unfocused ? "ControlStrokeColorDefaultBrush" : "AccentFillColorDefaultBrush");

    private void OnAskPanelPointerPressed(object sender, Microsoft.UI.Xaml.Input.PointerRoutedEventArgs e)
    {
        if (AskPopup.IsOpen)
        {
            e.Handled = true;
            if (AskComposer.Visibility == Visibility.Visible && AskInput.FocusState == FocusState.Unfocused)
            {
                AskInput.Focus(FocusState.Programmatic);
            }
        }
    }

    private void RefreshAskSendButton() =>
        AskSendButton.IsEnabled = _session.IsRunning || AskInput.Text.Trim().Length > 0;

    private void OnAskSendClick(object sender, RoutedEventArgs e)
    {
        if (_session.IsRunning)
        {
            _session.CancelCommand.Execute(null);
        }
        else
        {
            SubmitAsk();
        }
    }

    private void SubmitAsk()
    {
        var instruction = AskInput.Text.Trim();
        if (instruction.Length == 0 || _askSend is not { } send)
        {
            return;
        }

        _askSend = null;
        send.TrySetResult(instruction);
    }

    private void OnAskInputKeyDown(object sender, Microsoft.UI.Xaml.Input.KeyRoutedEventArgs e)
    {
        if (e.Key == VirtualKey.Escape)
        {
            e.Handled = true;
            CloseAskPanel();
            return;
        }

        var shift = InputKeyboardSource.GetKeyStateForCurrentThread(VirtualKey.Shift).HasFlag(CoreVirtualKeyStates.Down);
        if (e.Key == VirtualKey.Enter && !shift)
        {
            e.Handled = true;
            if (!_session.IsRunning)
            {
                SubmitAsk();
            }
        }
    }

    private void OnAskEntriesChanged(object? sender, System.Collections.Specialized.NotifyCollectionChangedEventArgs e)
    {
        if (e.Action == System.Collections.Specialized.NotifyCollectionChangedAction.Reset)
        {
            AskLogRows.Children.Clear();
            return;
        }

        if (e.NewItems is null)
        {
            return;
        }

        AskLogArea.Visibility = Visibility.Visible;
        foreach (AskLogEntry entry in e.NewItems)
        {
            // The previous step is no longer the current one: run the rail down to the new
            // row and let its text settle back.
            if (AskLogRows.Children.Count > 0 && AskLogRows.Children[^1] is Grid { Tag: LogRowParts previous })
            {
                previous.Connector.Visibility = Visibility.Visible;
                if (previous.Kind is AskLogKind.Info or AskLogKind.Search)
                {
                    previous.Body.Foreground = Resource("TextFillColorSecondaryBrush");
                }
            }

            AskLogRows.Children.Add(BuildLogRow(entry));
        }

        AskLogScroll.UpdateLayout();
        AskLogScroll.ChangeView(null, AskLogScroll.ScrollableHeight, null, disableAnimation: true);
    }

    /// <summary>The parts of a log row that change once a later step arrives.</summary>
    private sealed record LogRowParts(AskLogKind Kind, UIElement Connector, TextBlock Body);

    private static Brush Resource(string key) => (Brush)Application.Current.Resources[key];

    /// <summary>
    /// One step on the timeline: a marker on the rail (a dot, or an icon for searches and
    /// the outcome), what happened, and how far into the job it happened.
    /// </summary>
    private static UIElement BuildLogRow(AskLogEntry entry)
    {
        const double markerSize = 14;
        const double markerTop = 2;

        var row = new Grid { ColumnSpacing = 10 };
        row.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(16) });
        row.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
        row.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });

        // The rail segment down to the next step's marker; shown once there is a next step.
        var connector = new Microsoft.UI.Xaml.Shapes.Rectangle
        {
            Width = 1,
            Margin = new Thickness(0, markerTop + markerSize + 2, 0, 0),
            HorizontalAlignment = HorizontalAlignment.Center,
            Fill = Resource("DividerStrokeColorDefaultBrush"),
            Visibility = Visibility.Collapsed,
        };
        row.Children.Add(connector);

        UIElement marker = entry.Kind switch
        {
            AskLogKind.Search => new FontIcon { Glyph = "\uE721", FontSize = 11, Foreground = Resource("TextFillColorSecondaryBrush") },
            AskLogKind.Done => new FontIcon { Glyph = "\uE930", FontSize = 14, Foreground = Resource("SystemFillColorSuccessBrush") },
            AskLogKind.Error => new FontIcon { Glyph = "\uEA39", FontSize = 14, Foreground = Resource("SystemFillColorCriticalBrush") },
            _ => new Microsoft.UI.Xaml.Shapes.Ellipse
            {
                Width = 7,
                Height = 7,
                HorizontalAlignment = HorizontalAlignment.Center,
                VerticalAlignment = VerticalAlignment.Center,
                Fill = Resource("AccentFillColorDefaultBrush"),
            },
        };
        row.Children.Add(new Grid
        {
            Width = markerSize,
            Height = markerSize,
            Margin = new Thickness(0, markerTop, 0, 0),
            VerticalAlignment = VerticalAlignment.Top,
            Children = { marker },
        });

        var outcome = entry.Kind is AskLogKind.Done or AskLogKind.Error;
        var body = new TextBlock
        {
            Text = entry.Text,
            FontSize = 12,
            LineHeight = 18,
            Margin = new Thickness(0, 0, 0, 8),
            TextWrapping = TextWrapping.Wrap,
            IsTextSelectionEnabled = true,
            FontWeight = outcome ? FontWeights.SemiBold : FontWeights.Normal,
            Foreground = Resource(entry.Kind == AskLogKind.Error ? "SystemFillColorCriticalBrush" : "TextFillColorPrimaryBrush"),
        };
        Grid.SetColumn(body, 1);
        row.Children.Add(body);

        var time = new TextBlock
        {
            Text = entry.Time,
            FontSize = 11,
            Margin = new Thickness(0, 1, 0, 0),
            FontFamily = new FontFamily("Cascadia Mono, Consolas"),
            Foreground = Resource("TextFillColorTertiaryBrush"),
        };
        Grid.SetColumn(time, 2);
        row.Children.Add(time);

        row.Tag = new LogRowParts(entry.Kind, connector, body);
        return row;
    }

    private void OnAskSessionChanged(object? sender, System.ComponentModel.PropertyChangedEventArgs e)
    {
        switch (e.PropertyName)
        {
            case nameof(AskSessionViewModel.ElapsedText):
                AskElapsed.Text = _session.ElapsedText;
                break;

            case nameof(AskSessionViewModel.IsRunning):
                var running = _session.IsRunning;
                AskProgress.IsActive = running;
                AskProgress.Visibility = running ? Visibility.Visible : Visibility.Collapsed;
                AskStatusIcon.Visibility = running ? Visibility.Collapsed : Visibility.Visible;
                if (!running)
                {
                    var (glyph, brush) = _session switch
                    {
                        { OutcomeIsError: true } => ("\uEA39", "SystemFillColorCriticalBrush"),
                        { Outcome: CancelledOutcome } => ("\uE71A", "TextFillColorSecondaryBrush"),
                        _ => ("\uE930", "SystemFillColorSuccessBrush"),
                    };
                    AskStatusIcon.Glyph = glyph;
                    AskStatusIcon.Foreground = Resource(brush);
                }

                AskInput.IsReadOnly = running;
                RefreshAskLightDismiss();
                AskSendIcon.Glyph = running ? "" : "";
                AskStopButton.Visibility = running && AskComposer.Visibility == Visibility.Collapsed
                    ? Visibility.Visible
                    : Visibility.Collapsed;
                RefreshAskSendButton();
                break;
        }
    }


    private async Task CheckIssuesAsync()
    {
        var text = FileEditor.GetText();
        if (text.Length == 0 || text.Length > MaxContextChars)
        {
            if (text.Length > MaxContextChars)
            {
                ViewModel.Message = "This file is too large to check with the model.";
            }

            return;
        }

        var (start, end) = FileEditor.SelectionOffsets;
        var scope = start == end ? "the whole file" : "only the lines overlapping the selection";
        var numbered = string.Join(
            '\n',
            text.Split("\r\n").Select((line, index) => $"{index + 1}| {line}"));
        var selectionNote = start == end
            ? string.Empty
            : $"\n\nSelected range: lines {LineOf(text, start)}-{LineOf(text, end)}.";

        var reply = await RunModelAsync(
            start == end ? "Checking the file for issues…" : "Checking the selection for issues…",
            "You are a careful code reviewer. Find real problems: bugs, crashes, wrong logic, unsafe code, syntax "
            + "errors, missed edge cases. Skip style nitpicks. Check " + scope + ". Reply with one issue per line as "
            + "'Line N: description', most severe first. If there is nothing wrong, reply exactly 'No issues found.'",
            $"File: {ViewModel.FileName}\n\n{numbered}{selectionNote}",
            reply =>
            {
                var found = reply.Split('\n').Count(l => l.TrimStart().StartsWith("Line ", StringComparison.OrdinalIgnoreCase));
                return (found == 0 ? "No issues found" : found == 1 ? "1 issue found" : $"{found} issues found", false);
            });

        if (reply is null)
        {
            return;
        }

        var dialog = new ContentDialog
        {
            XamlRoot = XamlRoot,
            Title = "Check Issues",
            Content = new ScrollViewer
            {
                MaxHeight = 360,
                Content = new TextBlock { Text = reply.Trim(), TextWrapping = TextWrapping.Wrap, IsTextSelectionEnabled = true },
            },
            CloseButtonText = "Close",
        };
        await dialog.ShowAsync();
    }

    /// <summary>The 0-based column of an offset within its line.</summary>
    private static int ColumnOf(string text, int offset)
    {
        offset = Math.Clamp(offset, 0, text.Length);
        return offset - (offset == 0 ? 0 : text.LastIndexOf('\n', offset - 1) + 1);
    }

    private static int LineOf(string text, int offset)
    {
        var line = 1;
        for (var i = 0; i < offset && i < text.Length; i++)
        {
            if (text[i] == '\n')
            {
                line++;
            }
        }

        return line;
    }
}
