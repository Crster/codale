using Codale.App.ViewModels;

using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Input;
using Microsoft.UI.Xaml.Media;
using Microsoft.UI.Xaml.Shapes;

namespace Codale.App.Views;

/// <summary>
/// A plan the agent proposed, read as a document in the centre area: title, status and
/// the plan rendered as markdown. One tab serves every plan - opening another replaces
/// it - and it follows the artifact, so a revised plan updates in place.
/// </summary>
public sealed partial class ArtifactTab : UserControl
{
    private readonly TextBlock _title = new()
    {
        FontSize = 20,
        FontWeight = Microsoft.UI.Text.FontWeights.SemiBold,
        TextWrapping = TextWrapping.Wrap,
    };

    private readonly TextBlock _status = new() { FontSize = 11, FontWeight = Microsoft.UI.Text.FontWeights.SemiBold };

    private readonly Border _statusPill = new() { Padding = new Thickness(8, 2, 8, 2), CornerRadius = new CornerRadius(9) };

    private readonly TextBlock _subtitle = new()
    {
        FontSize = 12,
        VerticalAlignment = VerticalAlignment.Center,
    };

    private readonly MarkdownView _markdown = new();

    private readonly Image _image = new() { Stretch = Stretch.Uniform, HorizontalAlignment = HorizontalAlignment.Left, Visibility = Visibility.Collapsed };

    private readonly List<(string Quote, string Note)> _annotations = [];

    private readonly StackPanel _annotationList = new() { Spacing = 6 };

    private readonly Border _annotationBar = new()
    {
        Visibility = Visibility.Collapsed,
        Padding = new Thickness(32, 10, 32, 12),
        BorderThickness = new Thickness(0, 1, 0, 0),
    };

    /// <summary>The user asked for the plan to be revised; the text is the annotations as one message.</summary>
    public event Action<SessionArtifact, string>? RevisionRequested;

    public ArtifactTab()
    {
        _statusPill.Child = _status;
        _subtitle.Foreground = (Brush)Application.Current.Resources["TextFillColorTertiaryBrush"];

        var meta = new StackPanel { Orientation = Orientation.Horizontal, Spacing = 10 };
        meta.Children.Add(_statusPill);
        meta.Children.Add(_subtitle);

        var page = new StackPanel { MaxWidth = 860, Spacing = 14, Padding = new Thickness(32, 24, 32, 40) };
        page.Children.Add(_title);
        page.Children.Add(meta);
        page.Children.Add(new Rectangle
        {
            Height = 1,
            Fill = (Brush)Application.Current.Resources["TimelineRailBrush"],
        });
        page.Children.Add(_image);
        page.Children.Add(_markdown);

        var send = new Button { Content = "Send to agent", Style = (Style)Application.Current.Resources["AccentButtonStyle"] };
        send.Click += (_, _) => SendAnnotations();
        var clear = new Button { Content = "Clear" };
        clear.Click += (_, _) => ClearAnnotations();
        var actions = new StackPanel { Orientation = Orientation.Horizontal, Spacing = 8 };
        actions.Children.Add(send);
        actions.Children.Add(clear);

        var bar = new StackPanel { Spacing = 8, MaxWidth = 860 };
        bar.Children.Add(new TextBlock
        {
            Text = "Annotations",
            FontSize = 12,
            FontWeight = Microsoft.UI.Text.FontWeights.SemiBold,
            Foreground = (Brush)Application.Current.Resources["TextFillColorSecondaryBrush"],
        });
        bar.Children.Add(_annotationList);
        bar.Children.Add(actions);
        _annotationBar.Child = bar;
        _annotationBar.BorderBrush = (Brush)Application.Current.Resources["TimelineRailBrush"];
        _annotationBar.Background = (Brush)Application.Current.Resources["LayerFillColorDefaultBrush"];

        var scroller = new ScrollViewer { Content = page, VerticalScrollBarVisibility = ScrollBarVisibility.Auto };
        var root = new Grid();
        root.RowDefinitions.Add(new RowDefinition { Height = new GridLength(1, GridUnitType.Star) });
        root.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
        Grid.SetRow(_annotationBar, 1);
        root.Children.Add(scroller);
        root.Children.Add(_annotationBar);
        Content = root;

        // Selectable text handles right-click itself, so listen even for handled events.
        _markdown.AddHandler(RightTappedEvent, new RightTappedEventHandler(OnMarkdownRightTapped), true);
    }

    private void OnMarkdownRightTapped(object sender, RightTappedRoutedEventArgs e)
    {
        if (Artifact is null || Artifact.IsImage)
        {
            return;
        }

        var point = e.GetPosition(this);
        var block = VisualTreeHelper
            .FindElementsInHostCoordinates(e.GetPosition(null), _markdown)
            .OfType<TextBlock>()
            .FirstOrDefault();
        var quote = "";
        if (block is not null)
        {
            quote = block.SelectedText is { Length: > 0 } selected
                ? selected
                : MarkdownView.PlainText(block);
        }

        quote = quote.Trim();

        var add = new MenuFlyoutItem { Text = "Add annotation…", Icon = new FontIcon { Glyph = "" } };
        add.Click += (_, _) => PromptAnnotation(quote, point);
        var menu = new MenuFlyout();
        menu.Items.Add(add);
        if (quote.Length > 0)
        {
            var copy = new MenuFlyoutItem { Text = "Copy", Icon = new FontIcon { Glyph = "" } };
            copy.Click += (_, _) =>
            {
                var data = new Windows.ApplicationModel.DataTransfer.DataPackage();
                data.SetText(quote);
                Windows.ApplicationModel.DataTransfer.Clipboard.SetContent(data);
            };
            menu.Items.Add(copy);
        }

        menu.ShowAt(this, point);
        e.Handled = true;
    }

    private void PromptAnnotation(string quote, Windows.Foundation.Point at)
    {
        var box = new TextBox
        {
            PlaceholderText = "What should change?",
            AcceptsReturn = true,
            TextWrapping = TextWrapping.Wrap,
            Width = 320,
            MinHeight = 72,
        };
        var flyout = new Flyout();
        var confirm = new Button { Content = "Add", Style = (Style)Application.Current.Resources["AccentButtonStyle"] };
        confirm.Click += (_, _) =>
        {
            if (box.Text.Trim() is { Length: > 0 } note)
            {
                _annotations.Add((quote, note));
                RenderAnnotations();
            }

            flyout.Hide();
        };

        var panel = new StackPanel { Spacing = 8 };
        if (quote.Length > 0)
        {
            panel.Children.Add(new TextBlock
            {
                Text = quote.Length > 120 ? quote[..120] + "…" : quote,
                FontSize = 12,
                MaxWidth = 320,
                TextWrapping = TextWrapping.Wrap,
                Foreground = (Brush)Application.Current.Resources["TextFillColorTertiaryBrush"],
            });
        }

        panel.Children.Add(box);
        panel.Children.Add(confirm);
        flyout.Content = panel;
        flyout.Opened += (_, _) => box.Focus(FocusState.Programmatic);
        flyout.ShowAt(this, new Microsoft.UI.Xaml.Controls.Primitives.FlyoutShowOptions { Position = at });
    }

    private void RenderAnnotations()
    {
        _markdown.SetHighlights(_annotations.Select(a => a.Quote).Where(q => q.Length > 0).ToList());
        _annotationList.Children.Clear();
        _annotationBar.Visibility = _annotations.Count > 0 ? Visibility.Visible : Visibility.Collapsed;

        foreach (var entry in _annotations.ToList())
        {
            var row = new Grid { ColumnSpacing = 8 };
            row.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
            row.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });

            var text = new StackPanel { Spacing = 2 };
            if (entry.Quote.Length > 0)
            {
                text.Children.Add(new TextBlock
                {
                    Text = "“" + (entry.Quote.Length > 100 ? entry.Quote[..100] + "…" : entry.Quote) + "”",
                    FontSize = 11,
                    TextWrapping = TextWrapping.Wrap,
                    Foreground = (Brush)Application.Current.Resources["TextFillColorTertiaryBrush"],
                });
            }

            text.Children.Add(new TextBlock { Text = entry.Note, FontSize = 12.5, TextWrapping = TextWrapping.Wrap });

            var remove = new Button
            {
                Content = new FontIcon { Glyph = "", FontSize = 11 },
                Padding = new Thickness(6),
                Background = new SolidColorBrush(Microsoft.UI.Colors.Transparent),
                BorderThickness = new Thickness(0),
            };
            remove.Click += (_, _) =>
            {
                _annotations.Remove(entry);
                RenderAnnotations();
            };

            Grid.SetColumn(remove, 1);
            row.Children.Add(text);
            row.Children.Add(remove);
            _annotationList.Children.Add(row);
        }
    }

    private void ClearAnnotations()
    {
        _annotations.Clear();
        RenderAnnotations();
    }

    private void SendAnnotations()
    {
        if (Artifact is not { } artifact || _annotations.Count == 0)
        {
            return;
        }

        var lines = _annotations.Select((a, i) =>
            a.Quote.Length > 0
                ? $"{i + 1}. On \"{(a.Quote.Length > 200 ? a.Quote[..200] + "…" : a.Quote)}\": {a.Note}"
                : $"{i + 1}. {a.Note}");
        var message = $"Please revise the plan \"{artifact.Title}\" with these annotations:\n\n{string.Join("\n", lines)}";
        ClearAnnotations();
        RevisionRequested?.Invoke(artifact, message);
    }

    public SessionArtifact? Artifact { get; private set; }

    public void Show(SessionArtifact artifact)
    {
        if (Artifact is not null)
        {
            Artifact.PropertyChanged -= OnArtifactChanged;
        }

        if (!ReferenceEquals(Artifact, artifact))
        {
            _annotations.Clear();
            RenderAnnotations();
        }

        Artifact = artifact;
        artifact.PropertyChanged += OnArtifactChanged;
        Refresh();
    }

    private void OnArtifactChanged(object? sender, System.ComponentModel.PropertyChangedEventArgs e) =>
        DispatcherQueue.TryEnqueue(Refresh);

    private void Refresh()
    {
        if (Artifact is not { } artifact)
        {
            return;
        }

        _title.Text = artifact.IsFile && artifact.SourcePath is { } file ? System.IO.Path.GetFileNameWithoutExtension(file) : artifact.Title;
        _subtitle.Text = artifact.Subtitle;
        _status.Text = artifact.StatusText.ToUpperInvariant();

        var brush = (Brush)new ArtifactStatusBrushConverter().Convert(artifact.Status, typeof(Brush), null!, "");
        _status.Foreground = brush;
        _statusPill.BorderBrush = brush;
        _statusPill.BorderThickness = new Thickness(1);

        var showImage = artifact.IsImage && artifact.SourcePath is { } path && System.IO.File.Exists(path);
        _image.Visibility = showImage ? Visibility.Visible : Visibility.Collapsed;
        _markdown.Visibility = showImage ? Visibility.Collapsed : Visibility.Visible;
        if (showImage)
        {
            _image.Source = new Microsoft.UI.Xaml.Media.Imaging.BitmapImage(new Uri(artifact.SourcePath!));
        }
        else
        {
            // A missing image must not leave the previous artifact's picture behind.
            _image.Source = null;
            _markdown.BasePath = artifact.SourcePath is { } source ? System.IO.Path.GetDirectoryName(source) : null;
            _markdown.Markdown = artifact.IsImage ? "_The image file is no longer available._" : ReadMarkdown(artifact);
        }
    }

    /// <summary>A plan document is read from disk each time it is shown, so an edit by the agent is not stale.</summary>
    private static string ReadMarkdown(SessionArtifact artifact)
    {
        if (artifact.IsFile && artifact.SourcePath is { } path && System.IO.File.Exists(path))
        {
            try
            {
                return System.IO.File.ReadAllText(path);
            }
            catch (System.IO.IOException)
            {
                // Locked mid-write: fall back to what the conversation captured.
            }
        }

        return artifact.Markdown;
    }
}
