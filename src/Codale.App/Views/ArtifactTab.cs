using Codale.App.ViewModels;

using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
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

    private readonly MarkdownView _markdown = new() { HostOwnsTextMenu = true };

    private readonly Image _image = new() { Stretch = Stretch.Uniform, HorizontalAlignment = HorizontalAlignment.Left, Visibility = Visibility.Collapsed };

    private readonly PlanAnnotationBar _annotationBar = new()
    {
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

        _annotationBar.BorderBrush = (Brush)Application.Current.Resources["TimelineRailBrush"];
        _annotationBar.Background = (Brush)Application.Current.Resources["LayerFillColorDefaultBrush"];
        _annotationBar.SendRequested += (_, message) =>
        {
            if (Artifact is { } plan)
            {
                RevisionRequested?.Invoke(plan, message);
            }
        };

        var scroller = new ScrollViewer { Content = page, VerticalScrollBarVisibility = ScrollBarVisibility.Auto };
        var root = new Grid();
        root.RowDefinitions.Add(new RowDefinition { Height = new GridLength(1, GridUnitType.Star) });
        root.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
        Grid.SetRow(_annotationBar, 1);
        root.Children.Add(scroller);
        root.Children.Add(_annotationBar);
        Content = root;
    }

    public SessionArtifact? Artifact { get; private set; }

    public void Show(SessionArtifact artifact)
    {
        if (Artifact is not null)
        {
            Artifact.PropertyChanged -= OnArtifactChanged;
        }

        // Annotations live on the plan, so switching plans keeps each one's notes.
        Artifact = artifact;
        PlanAnnotator.For(_markdown).Plan = artifact;
        _annotationBar.Plan = artifact;
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
