using Codale.App.Themes;

using Microsoft.UI.Input;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Media;

using Windows.System;
using Windows.UI.Core;

namespace Codale.App.Views;

/// <summary>Selection actions shared by rendered replies and sent prompts.</summary>
internal static class ChatSelectionAnnotation
{
    public static void AddTo(MenuFlyout menu, FrameworkElement anchor, Func<string> selectedText)
    {
        var action = new MenuFlyoutItem { Text = "Reply", Icon = new FontIcon { Glyph = "\uE70F" } };
        string quote = "";
        ChatTab? chat = null;
        menu.Items.Add(action);
        menu.Opening += (_, _) =>
        {
            quote = selectedText().Trim();
            chat = FindChat(anchor);
            action.Visibility = chat is null ? Visibility.Collapsed : Visibility.Visible;
            action.IsEnabled = quote.Length > 0;
        };
        action.Click += (_, _) =>
        {
            if (chat is null || quote.Length == 0) return;
            var owner = chat;
            var reference = quote;
            // Defer until the selection menu closes before opening the annotation editor.
            anchor.DispatcherQueue.TryEnqueue(() => Prompt(anchor, owner, reference));
        };
    }

    private static ChatTab? FindChat(DependencyObject element)
    {
        for (DependencyObject? parent = element; parent is not null; parent = VisualTreeHelper.GetParent(parent))
            if (parent is ChatTab chat) return chat;
        return null;
    }

    /// <summary>A quiet quote of the selection over one growing input row; Enter appends, Shift+Enter breaks the line.</summary>
    private static void Prompt(FrameworkElement anchor, ChatTab chat, string quote)
    {
        var note = new TextBox
        {
            PlaceholderText = "Reply to selection…",
            AcceptsReturn = true,
            TextWrapping = TextWrapping.Wrap,
            MaxHeight = 120,
            VerticalAlignment = VerticalAlignment.Center,
        };
        ScrollViewer.SetVerticalScrollBarVisibility(note, ScrollBarVisibility.Auto);
        var append = new Button
        {
            Content = new FontIcon { Glyph = "", FontSize = 14 },
            IsEnabled = false,
            VerticalAlignment = VerticalAlignment.Bottom,
            Style = (Style)Application.Current.Resources["ShellIconButton"],
        };
        ToolTipService.SetToolTip(append, "Append to prompt");

        var flyout = new Flyout
        {
            FlyoutPresenterStyle = new Style(typeof(FlyoutPresenter))
            {
                Setters =
                {
                    new Setter(Control.PaddingProperty, new Thickness(8)),
                    new Setter(FrameworkElement.MinWidthProperty, 0d),
                    new Setter(FrameworkElement.MinHeightProperty, 0d),
                },
            },
        };

        void Submit()
        {
            if (string.IsNullOrWhiteSpace(note.Text)) return;
            chat.AppendSelectionAnnotation(quote, note.Text.Trim());
            flyout.Hide();
            chat.DispatcherQueue.TryEnqueue(chat.FocusDraft);
        }

        note.TextChanged += (_, _) => append.IsEnabled = !string.IsNullOrWhiteSpace(note.Text);
        note.PreviewKeyDown += (_, e) =>
        {
            if (e.Key != VirtualKey.Enter ||
                InputKeyboardSource.GetKeyStateForCurrentThread(VirtualKey.Shift).HasFlag(CoreVirtualKeyStates.Down))
            {
                return;
            }

            e.Handled = true;
            Submit();
        };
        append.Click += (_, _) => Submit();

        var input = new Grid { ColumnSpacing = 4 };
        input.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
        input.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
        Grid.SetColumn(append, 1);
        input.Children.Add(note);
        input.Children.Add(append);

        var preview = new Border
        {
            BorderBrush = ChatPaletteTheme.Brush("TextFillColorTertiaryBrush"),
            BorderThickness = new Thickness(2, 0, 0, 0),
            Padding = new Thickness(8, 0, 0, 0),
            Child = new TextBlock
            {
                Text = quote,
                FontSize = 12,
                MaxLines = 2,
                TextWrapping = TextWrapping.Wrap,
                TextTrimming = TextTrimming.CharacterEllipsis,
                Foreground = ChatPaletteTheme.Brush("TextFillColorSecondaryBrush"),
            },
        };

        flyout.Content = new StackPanel { Spacing = 6, Width = 340, Children = { preview, input } };
        flyout.Opened += (_, _) => note.Focus(FocusState.Programmatic);
        flyout.ShowAt(anchor);
    }
}
