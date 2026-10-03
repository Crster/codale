using System.Collections.Specialized;

using Codale.App.ViewModels;

using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Media;

namespace Codale.App.Views;

/// <summary>
/// A subagent's work as a quiet transcript instead of a terminal log: the prompt in a
/// card, its prose as markdown, and each run of tool calls folded into one summary row
/// ("Ran 5 commands, read 7 files") that opens to the calls themselves.
/// </summary>
public sealed class SubagentTranscript : StackPanel
{
    private RunningTaskItem? _task;

    public SubagentTranscript()
    {
        Spacing = 14;
        Padding = new Thickness(16, 14, 16, 16);
    }

    public event EventHandler? Changed;

    public void Attach(RunningTaskItem task)
    {
        Detach();
        _task = task;
        task.Entries.CollectionChanged += OnEntriesChanged;
        Rebuild();
    }

    public void Detach()
    {
        if (_task is not null)
        {
            _task.Entries.CollectionChanged -= OnEntriesChanged;
        }

        _task = null;
        Children.Clear();
    }

    private void OnEntriesChanged(object? sender, NotifyCollectionChangedEventArgs e)
    {
        if (e.Action == NotifyCollectionChangedAction.Add && e.NewItems is not null)
        {
            foreach (TaskEntry entry in e.NewItems)
            {
                Children.Add(Build(entry));
            }
        }
        else
        {
            Rebuild();
        }

        Changed?.Invoke(this, EventArgs.Empty);
    }

    private void Rebuild()
    {
        Children.Clear();
        if (_task is null)
        {
            return;
        }

        if (_task.Prompt.Length > 0)
        {
            Children.Add(PromptCard(_task.Prompt));
        }

        foreach (var entry in _task.Entries)
        {
            Children.Add(Build(entry));
        }
    }

    private static Brush Themed(string key) => (Brush)Application.Current.Resources[key];

    private static UIElement PromptCard(string prompt) => new Border
    {
        Padding = new Thickness(12, 10, 12, 10),
        CornerRadius = new CornerRadius(8),
        Background = Themed("LayerFillColorDefaultBrush"),
        Child = new MarkdownView { BaseFontSize = 12.5, Markdown = prompt },
    };

    private static UIElement Build(TaskEntry entry) =>
        entry.IsText ? new MarkdownView { BaseFontSize = 13, Markdown = entry.Text } : ToolRun(entry);

    private static UIElement ToolRun(TaskEntry entry)
    {
        var label = new TextBlock
        {
            FontSize = 12,
            Foreground = Themed("TextFillColorTertiaryBrush"),
            TextTrimming = TextTrimming.CharacterEllipsis,
            VerticalAlignment = VerticalAlignment.Center,
        };
        var chevron = new FontIcon
        {
            Glyph = "",
            FontSize = 9,
            Foreground = Themed("TextFillColorTertiaryBrush"),
            VerticalAlignment = VerticalAlignment.Center,
        };
        var header = new Button
        {
            Padding = new Thickness(0, 2, 0, 2),
            Background = new SolidColorBrush(Microsoft.UI.Colors.Transparent),
            BorderThickness = new Thickness(0),
            HorizontalAlignment = HorizontalAlignment.Left,
            Content = new StackPanel { Orientation = Orientation.Horizontal, Spacing = 6, Children = { label, chevron } },
        };

        var calls = new StackPanel { Spacing = 6, Margin = new Thickness(0, 4, 0, 0), Visibility = Visibility.Collapsed };
        var shown = 0;

        void Sync()
        {
            label.Text = entry.Summary;
            for (; shown < entry.Calls.Count; shown++)
            {
                calls.Children.Add(CallRow(entry.Calls[shown]));
            }

            // A result that arrives later updates a row that already exists.
            for (var i = 0; i < shown && i < calls.Children.Count; i++)
            {
                if (calls.Children[i] is StackPanel row)
                {
                    FillRow(row, entry.Calls[i]);
                }
            }
        }

        entry.PropertyChanged += (_, e) =>
        {
            if (e.PropertyName is nameof(TaskEntry.Summary) or nameof(TaskEntry.Calls))
            {
                Sync();
            }
        };
        Sync();

        header.Click += (_, _) =>
        {
            var open = calls.Visibility == Visibility.Collapsed;
            calls.Visibility = open ? Visibility.Visible : Visibility.Collapsed;
            chevron.Glyph = open ? "" : "";
        };

        return new StackPanel { Children = { header, calls } };
    }

    private static UIElement CallRow(TaskCall call)
    {
        var row = new StackPanel { Spacing = 2 };
        FillRow(row, call);
        return row;
    }

    private static void FillRow(StackPanel row, TaskCall call)
    {
        row.Children.Clear();
        row.Children.Add(new TextBlock
        {
            FontSize = 12,
            Text = call.Line,
            Foreground = call.Failed ? Themed("SystemFillColorCriticalBrush") : Themed("TextFillColorSecondaryBrush"),
            TextTrimming = TextTrimming.CharacterEllipsis,
        });

        if (call.Result.Length > 0)
        {
            row.Children.Add(new TextBlock
            {
                FontFamily = new FontFamily("Cascadia Mono, Consolas"),
                FontSize = 11,
                Text = call.Result.Length > 400 ? call.Result[..400] + "…" : call.Result,
                Foreground = Themed("TextFillColorTertiaryBrush"),
                MaxLines = 6,
                TextTrimming = TextTrimming.CharacterEllipsis,
                IsTextSelectionEnabled = true,
            });
        }
    }
}
