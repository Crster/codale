using Codale.App.ViewModels;
using Codale.Core.Helper;

using Microsoft.UI.Input;
using Microsoft.UI.Text;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
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

    /// <summary>How long a finished Format or Check job's log stays up when there is no composer to close.</summary>
    private static readonly TimeSpan LogOnlyLinger = TimeSpan.FromSeconds(6);

    private static MenuFlyoutItem Item(string text, Action click, string? accelerator = null)
    {
        var item = new MenuFlyoutItem { Text = text, KeyboardAcceleratorTextOverride = accelerator ?? string.Empty };
        item.Click += (_, _) => click();
        return item;
    }

    private void BuildContextMenu()
    {
        _undoItem = Item("Undo", FileEditor.Undo, "Ctrl+Z");
        _redoItem = Item("Redo", FileEditor.Redo, "Ctrl+Y");
        _cutItem = Item("Cut", FileEditor.CutToClipboard, "Ctrl+X");
        _copyItem = Item("Copy", FileEditor.CopyToClipboard, "Ctrl+C");
        _pasteItem = Item("Paste", FileEditor.PasteFromClipboard, "Ctrl+V");
        _deleteItem = Item("Delete", FileEditor.DeleteSelection);
        var selectAll = Item("Select All", FileEditor.SelectEverything, "Ctrl+A");
        _formatItem = Item("Format Document", () => _ = FormatDocumentAsync());
        _askItem = Item("Ask Claude…", () => _ = AskClaudeAsync());
        _checkItem = Item("Check Issues", () => _ = CheckIssuesAsync());
        _previewItem = Item("Preview", TogglePreview);

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

        menu.Opening += (_, _) => RefreshContextMenu();
        FileEditor.ContextFlyout = menu;

        // While previewing, the only thing to do is go back to the text.
        var back = new MenuFlyout();
        back.Items.Add(Item("Edit Markdown", TogglePreview));
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
        _session.Log(AskLogKind.Info, $"{ViewModel.FileName} · line {FileEditor.CursorPosition.LineNumber + 1}");
        (string Outcome, bool IsError) result = ("Cancelled", false);
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
            AskLogTitle.Text = result.IsError ? "Failed" : "Done";
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
            AskPanel.Visibility = Visibility.Collapsed;
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

        AskContextIcon.Glyph = hasSelection ? "" : "";
        AskContextText.Inlines.Clear();
        AskContextText.Inlines.Add(new Microsoft.UI.Xaml.Documents.Run { Text = ViewModel.FileName, FontWeight = FontWeights.SemiBold });
        AskContextText.Inlines.Add(new Microsoft.UI.Xaml.Documents.Run
        {
            Text = "  ·  " + where,
            Foreground = (Brush)Application.Current.Resources["TextFillColorSecondaryBrush"],
        });

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
        AskLogRows.Children.Clear();
        AskLogArea.Visibility = Visibility.Collapsed;

        OpenAskPanel(composer: true);
        AskInput.Focus(FocusState.Programmatic);

        var send = _askSend = new TaskCompletionSource<string?>();
        var instruction = await send.Task;
        return string.IsNullOrWhiteSpace(instruction) ? null : instruction.Trim();
    }

    private void OpenAskPanel(bool composer)
    {
        AskPanel.Visibility = Visibility.Visible;
        AskComposer.Visibility = composer ? Visibility.Visible : Visibility.Collapsed;
        AskStopButton.Visibility = !composer && _session.IsRunning ? Visibility.Visible : Visibility.Collapsed;
    }

    /// <summary>Closes the panel; a job still running is cancelled, a composer still waiting is dismissed.</summary>
    private void CloseAskPanel()
    {
        _session.CancelCommand.Execute(null);
        var send = _askSend;
        _askSend = null;
        send?.TrySetResult(null);
        AskPanel.Visibility = Visibility.Collapsed;
        FileEditor.Focus(FocusState.Programmatic);
    }

    private void OnAskCloseClick(object sender, RoutedEventArgs e) => CloseAskPanel();

    private void OnAskInputTextChanged(object sender, TextChangedEventArgs e) => RefreshAskSendButton();

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
            AskLogRows.Children.Add(BuildLogRow(entry));
        }

        AskLogScroll.UpdateLayout();
        AskLogScroll.ChangeView(null, AskLogScroll.ScrollableHeight, null, disableAnimation: true);
    }

    private static UIElement BuildLogRow(AskLogEntry entry)
    {
        var (glyph, brushKey) = entry.Kind switch
        {
            AskLogKind.Search => ("", "TextFillColorSecondaryBrush"),
            AskLogKind.Done => ("", "SystemFillColorSuccessBrush"),
            AskLogKind.Error => ("", "SystemFillColorCriticalBrush"),
            _ => ("", "TextFillColorTertiaryBrush"),
        };

        var row = new Grid { ColumnSpacing = 8 };
        row.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
        row.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
        row.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });

        var icon = new FontIcon
        {
            Glyph = glyph,
            FontSize = 10,
            Margin = new Thickness(0, 3, 0, 0),
            VerticalAlignment = VerticalAlignment.Top,
            Foreground = (Brush)Application.Current.Resources[brushKey],
        };
        var time = new TextBlock
        {
            Text = entry.Time,
            FontSize = 11,
            FontFamily = new FontFamily("Cascadia Mono, Consolas"),
            Foreground = (Brush)Application.Current.Resources["TextFillColorTertiaryBrush"],
        };
        var body = new TextBlock
        {
            Text = entry.Text,
            FontSize = 12,
            TextWrapping = TextWrapping.Wrap,
            IsTextSelectionEnabled = true,
            Foreground = (Brush)Application.Current.Resources[entry.Kind == AskLogKind.Error ? "SystemFillColorCriticalBrush" : "TextFillColorPrimaryBrush"],
        };
        Grid.SetColumn(time, 1);
        Grid.SetColumn(body, 2);
        row.Children.Add(icon);
        row.Children.Add(time);
        row.Children.Add(body);
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
                AskInput.IsReadOnly = running;
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
