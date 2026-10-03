using Microsoft.UI;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Controls.Primitives;
using Microsoft.UI.Xaml.Input;
using Microsoft.UI.Xaml.Media;
using Microsoft.UI.Xaml.Media.Animation;
using Microsoft.UI.Xaml.Media.Imaging;

using Windows.System;

namespace Codale.App.Views;

/// <summary>
/// Full-window image preview: a dimmed backdrop, the image centred and zoomable, and a
/// floating toolbar with the file's details and actions. Stands in for a ContentDialog,
/// whose title bar and button row eat the room a screenshot needs.
/// </summary>
/// <remarks>
/// Esc, the close button or a click on the backdrop dismisses it; Ctrl+wheel, pinch,
/// +/- and 0 (fit) zoom. The window's caption buttons draw over the top edge, so the
/// chrome lives at the bottom.
/// </remarks>
public sealed class ImageLightbox
{
    private const double MinZoom = 0.1;
    private const double MaxZoom = 8;

    private readonly XamlRoot _root;
    private readonly string _path;
    private readonly Popup _popup;
    private readonly Grid _overlay;
    private readonly ScrollViewer _scroller;
    private readonly Image _image;
    private readonly TextBlock _meta;
    private readonly Button _zoomLabel;
    private readonly Button _closeButton;
    private bool _closing;

    private ImageLightbox(XamlRoot root, string name, string path)
    {
        _root = root;
        _path = path;

        _image = new Image
        {
            Source = new BitmapImage(new Uri(path)),
            Stretch = Stretch.Uniform,
        };
        _image.ImageOpened += OnImageOpened;

        // The frame keeps a hairline edge so a white screenshot does not bleed into a
        // light backdrop, and clips the image to its rounded corners.
        var frame = new Border
        {
            Child = _image,
            CornerRadius = new CornerRadius(8),
            BorderThickness = new Thickness(1),
            BorderBrush = new SolidColorBrush(ColorHelper.FromArgb(0x33, 0xFF, 0xFF, 0xFF)),
            HorizontalAlignment = HorizontalAlignment.Center,
            VerticalAlignment = VerticalAlignment.Center,
        };

        _scroller = new ScrollViewer
        {
            Content = frame,
            ZoomMode = ZoomMode.Enabled,
            MinZoomFactor = (float)MinZoom,
            MaxZoomFactor = (float)MaxZoom,
            HorizontalScrollBarVisibility = ScrollBarVisibility.Auto,
            VerticalScrollBarVisibility = ScrollBarVisibility.Auto,
            HorizontalScrollMode = ScrollMode.Auto,
            VerticalScrollMode = ScrollMode.Auto,
            HorizontalContentAlignment = HorizontalAlignment.Center,
            VerticalContentAlignment = VerticalAlignment.Center,
            // Room for the toolbar underneath and the caption buttons above.
            Padding = new Thickness(32, 48, 32, 96),
        };
        _scroller.ViewChanged += (_, _) => UpdateZoomLabel();

        _meta = new TextBlock
        {
            Style = Resource<Style>("CaptionTextBlockStyle"),
            Foreground = Resource<Brush>("TextFillColorTertiaryBrush"),
            VerticalAlignment = VerticalAlignment.Center,
        };

        _zoomLabel = ToolbarButton(null, "Fit to window (0)", (_, _) => ToggleFit());
        _zoomLabel.MinWidth = 56;
        _zoomLabel.Content = new TextBlock { Text = "100%", Style = Resource<Style>("CaptionTextBlockStyle") };

        _closeButton = ToolbarButton("", "Close (Esc)", (_, _) => Close());

        var toolbar = new Border
        {
            Background = Resource<Brush>("AcrylicInAppFillColorDefaultBrush"),
            BorderBrush = Resource<Brush>("SurfaceStrokeColorFlyoutBrush"),
            BorderThickness = new Thickness(1),
            CornerRadius = new CornerRadius(12),
            Padding = new Thickness(6),
            Margin = new Thickness(16, 0, 16, 24),
            HorizontalAlignment = HorizontalAlignment.Center,
            VerticalAlignment = VerticalAlignment.Bottom,
            Shadow = new ThemeShadow(),
            Translation = new System.Numerics.Vector3(0, 0, 32),
            Child = new StackPanel
            {
                Orientation = Orientation.Horizontal,
                Spacing = 2,
                Children =
                {
                    new FontIcon
                    {
                        Glyph = "",
                        FontSize = 16,
                        Margin = new Thickness(10, 0, 8, 0),
                        Foreground = Resource<Brush>("AccentTextFillColorPrimaryBrush"),
                        VerticalAlignment = VerticalAlignment.Center,
                    },
                    new StackPanel
                    {
                        VerticalAlignment = VerticalAlignment.Center,
                        Margin = new Thickness(0, 0, 12, 0),
                        MaxWidth = 320,
                        Children =
                        {
                            new TextBlock
                            {
                                Text = name,
                                Style = Resource<Style>("BodyStrongTextBlockStyle"),
                                TextTrimming = TextTrimming.CharacterEllipsis,
                                TextWrapping = TextWrapping.NoWrap,
                            },
                            _meta,
                        },
                    },
                    Separator(),
                    ToolbarButton("", "Zoom out (-)", (_, _) => ZoomBy(1 / 1.25)),
                    _zoomLabel,
                    ToolbarButton("", "Zoom in (+)", (_, _) => ZoomBy(1.25)),
                    Separator(),
                    ToolbarButton("", "Copy image", async (_, _) => await CopyAsync()),
                    ToolbarButton("", "Show in folder", async (_, _) => await RevealAsync()),
                    ToolbarButton("", "Open externally", async (_, _) => await OpenExternallyAsync()),
                    Separator(),
                    _closeButton,
                },
            },
        };

        _overlay = new Grid
        {
            Background = new SolidColorBrush(ColorHelper.FromArgb(0xE0, 0x0A, 0x0A, 0x0C)),
            Opacity = 0,
            Children = { _scroller, toolbar },
        };
        _overlay.Tapped += OnOverlayTapped;
        _overlay.KeyDown += OnKeyDown;

        _popup = new Popup { XamlRoot = root, Child = _overlay };
        _popup.Closed += (_, _) => _root.Changed -= OnRootChanged;
    }

    /// <summary>Shows the preview; returns once it is on screen, not when it closes.</summary>
    public static void Show(XamlRoot root, string name, string path)
    {
        new ImageLightbox(root, name, path).Open();
    }

    private void Open()
    {
        Resize();
        _root.Changed += OnRootChanged;
        _popup.IsOpen = true;

        _meta.Text = FileSizeText();
        Animate(_overlay, 1, 160, () => _closeButton.Focus(FocusState.Programmatic));
    }

    private void Close()
    {
        if (_closing)
        {
            return;
        }

        _closing = true;
        Animate(_overlay, 0, 120, () => _popup.IsOpen = false);
    }

    private void OnRootChanged(XamlRoot sender, XamlRootChangedEventArgs args) => Resize();

    private void Resize()
    {
        _overlay.Width = _root.Size.Width;
        _overlay.Height = _root.Size.Height;
    }

    private void OnImageOpened(object sender, RoutedEventArgs e)
    {
        if (_image.Source is BitmapImage { PixelWidth: > 0 } bitmap)
        {
            // Lay the image out at its own pixel size so 100% means 100%, then zoom
            // out to fit; a small image stays at 100% rather than being blown up.
            _image.Width = bitmap.PixelWidth;
            _image.Height = bitmap.PixelHeight;
            _meta.Text = $"{bitmap.PixelWidth} × {bitmap.PixelHeight}  ·  {FileSizeText()}";

            // The ScrollViewer has to measure the new size before a zoom lands.
            _scroller.UpdateLayout();
            _scroller.ChangeView(null, null, (float)Math.Min(1, FitZoom()), disableAnimation: true);
        }
    }

    private double FitZoom()
    {
        if (_image.Width is not > 0 || _image.Height is not > 0)
        {
            return 1;
        }

        var padding = _scroller.Padding;
        var width = _scroller.ActualWidth - padding.Left - padding.Right - 2;
        var height = _scroller.ActualHeight - padding.Top - padding.Bottom - 2;
        return Math.Clamp(Math.Min(width / _image.Width, height / _image.Height), MinZoom, MaxZoom);
    }

    private void ZoomBy(double factor)
    {
        var target = Math.Clamp(_scroller.ZoomFactor * factor, MinZoom, MaxZoom);
        _scroller.ChangeView(null, null, (float)target);
    }

    /// <summary>The zoom readout doubles as a toggle between fit and actual size.</summary>
    private void ToggleFit()
    {
        var fit = Math.Min(1, FitZoom());
        var atFit = Math.Abs(_scroller.ZoomFactor - fit) < 0.01;
        _scroller.ChangeView(null, null, (float)(atFit ? 1 : fit));
    }

    private void UpdateZoomLabel()
    {
        if (_zoomLabel.Content is TextBlock text)
        {
            text.Text = $"{Math.Round(_scroller.ZoomFactor * 100)}%";
        }
    }

    private void OnOverlayTapped(object sender, TappedRoutedEventArgs e)
    {
        // Only the empty space around the image dismisses; the image and toolbar do not.
        if (e.OriginalSource is FrameworkElement source
            && (ReferenceEquals(source, _overlay) || source is ScrollContentPresenter || ReferenceEquals(source, _scroller)))
        {
            Close();
        }
    }

    private void OnKeyDown(object sender, KeyRoutedEventArgs e)
    {
        switch (e.Key)
        {
            case VirtualKey.Escape:
                Close();
                break;
            case VirtualKey.Add or (VirtualKey)187:
                ZoomBy(1.25);
                break;
            case VirtualKey.Subtract or (VirtualKey)189:
                ZoomBy(1 / 1.25);
                break;
            case VirtualKey.Number0 or VirtualKey.NumberPad0:
                ToggleFit();
                break;
            default:
                return;
        }

        e.Handled = true;
    }

    private async Task CopyAsync()
    {
        try
        {
            var file = await Windows.Storage.StorageFile.GetFileFromPathAsync(_path);
            var package = new Windows.ApplicationModel.DataTransfer.DataPackage();
            package.SetBitmap(Windows.Storage.Streams.RandomAccessStreamReference.CreateFromFile(file));
            Windows.ApplicationModel.DataTransfer.Clipboard.SetContent(package);
        }
        catch (Exception ex) when (ex is System.Runtime.InteropServices.COMException or IOException or UnauthorizedAccessException)
        {
            // Clipboard busy or the file went away; nothing useful to report here.
        }
    }

    private async Task RevealAsync()
    {
        if (Path.GetDirectoryName(_path) is not { } folder)
        {
            return;
        }

        var options = new FolderLauncherOptions();
        options.ItemsToSelect.Add(await Windows.Storage.StorageFile.GetFileFromPathAsync(_path));
        await Launcher.LaunchFolderPathAsync(folder, options);
    }

    private async Task OpenExternallyAsync()
    {
        await Launcher.LaunchFileAsync(await Windows.Storage.StorageFile.GetFileFromPathAsync(_path));
        Close();
    }

    private string FileSizeText()
    {
        long bytes;
        try
        {
            bytes = new FileInfo(_path).Length;
        }
        catch (IOException)
        {
            return "";
        }

        return bytes switch
        {
            < 1024 => $"{bytes} B",
            < 1024 * 1024 => $"{bytes / 1024.0:0.#} KB",
            _ => $"{bytes / (1024.0 * 1024):0.#} MB",
        };
    }

    private static Button ToolbarButton(string? glyph, string tooltip, RoutedEventHandler click)
    {
        var button = new Button
        {
            MinWidth = 36,
            Height = 36,
            Padding = new Thickness(8, 0, 8, 0),
            Background = new SolidColorBrush(Colors.Transparent),
            BorderThickness = new Thickness(0),
            CornerRadius = new CornerRadius(8),
            VerticalAlignment = VerticalAlignment.Center,
            Content = glyph is null ? null : new FontIcon { Glyph = glyph, FontSize = 14 },
        };
        ToolTipService.SetToolTip(button, tooltip);
        Microsoft.UI.Xaml.Automation.AutomationProperties.SetName(button, tooltip);
        button.Click += click;
        return button;
    }

    private static Border Separator() => new()
    {
        Width = 1,
        Height = 20,
        Margin = new Thickness(4, 0, 4, 0),
        VerticalAlignment = VerticalAlignment.Center,
        Background = Resource<Brush>("DividerStrokeColorDefaultBrush"),
    };

    private static void Animate(UIElement target, double to, int milliseconds, Action completed)
    {
        var animation = new DoubleAnimation
        {
            To = to,
            Duration = TimeSpan.FromMilliseconds(milliseconds),
            EasingFunction = new CubicEase { EasingMode = EasingMode.EaseOut },
        };
        Storyboard.SetTarget(animation, target);
        Storyboard.SetTargetProperty(animation, nameof(UIElement.Opacity));

        var storyboard = new Storyboard { Children = { animation } };
        storyboard.Completed += (_, _) => completed();
        storyboard.Begin();
    }

    private static T Resource<T>(string key) where T : class => (T)Application.Current.Resources[key];
}
