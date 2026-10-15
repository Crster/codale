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
/// A bare image peek: the picture centred over a dimmed backdrop, no controls.
/// Scrolling the wheel over the image zooms it; a click outside the image or Esc dismisses.
/// </summary>
public sealed class ImageLightbox
{
    private const double MinZoom = 0.25;
    private const double MaxZoom = 8;
    private const double ZoomStep = 1.15;

    /// <summary>Share of the window the image may fill before any zoom.</summary>
    private const double FitShare = 0.8;

    private readonly XamlRoot _root;
    private readonly Popup _popup;
    private readonly Grid _overlay;
    private readonly Image _image;
    private readonly ScaleTransform _scale = new() { ScaleX = 1, ScaleY = 1 };
    private bool _closing;

    private ImageLightbox(XamlRoot root, string path)
    {
        _root = root;

        _image = new Image
        {
            Source = new BitmapImage(new Uri(path)),
            Stretch = Stretch.Uniform,
            RenderTransform = _scale,
            RenderTransformOrigin = new Windows.Foundation.Point(0.5, 0.5),
        };

        // Zoom is a render transform around the centre; clip nothing so it can outgrow the window.
        var frame = new Border
        {
            Child = _image,
            CornerRadius = new CornerRadius(8),
            HorizontalAlignment = HorizontalAlignment.Center,
            VerticalAlignment = VerticalAlignment.Center,
            Shadow = new ThemeShadow(),
            Translation = new System.Numerics.Vector3(0, 0, 32),
        };

        _overlay = new Grid
        {
            Background = new SolidColorBrush(ColorHelper.FromArgb(0xC8, 0x0A, 0x0A, 0x0C)),
            Opacity = 0,
            IsTabStop = true,
            Children = { frame },
        };
        _overlay.Tapped += OnOverlayTapped;
        _overlay.KeyDown += OnKeyDown;
        _overlay.PointerWheelChanged += OnWheel;

        _popup = new Popup { XamlRoot = root, Child = _overlay };
        _popup.Closed += (_, _) => _root.Changed -= OnRootChanged;
    }

    /// <summary>Shows the peek; returns once it is on screen, not when it closes.</summary>
    public static void Show(XamlRoot root, string path)
    {
        new ImageLightbox(root, path).Open();
    }

    private void Open()
    {
        Resize();
        _root.Changed += OnRootChanged;
        _popup.IsOpen = true;
        Animate(_overlay, 1, 140, () => _overlay.Focus(FocusState.Programmatic));
    }

    private void Close()
    {
        if (_closing)
        {
            return;
        }

        _closing = true;
        Animate(_overlay, 0, 100, () => _popup.IsOpen = false);
    }

    private void OnRootChanged(XamlRoot sender, XamlRootChangedEventArgs args) => Resize();

    private void Resize()
    {
        _overlay.Width = _root.Size.Width;
        _overlay.Height = _root.Size.Height;

        // Large images shrink to fit; small ones keep their own size.
        _image.MaxWidth = _root.Size.Width * FitShare;
        _image.MaxHeight = _root.Size.Height * FitShare;
    }

    private void OnWheel(object sender, PointerRoutedEventArgs e)
    {
        var delta = e.GetCurrentPoint(_overlay).Properties.MouseWheelDelta;
        if (delta == 0)
        {
            return;
        }

        var factor = delta > 0 ? ZoomStep : 1 / ZoomStep;
        var zoom = Math.Clamp(_scale.ScaleX * factor, MinZoom, MaxZoom);
        _scale.ScaleX = zoom;
        _scale.ScaleY = zoom;
        e.Handled = true;
    }

    private void OnOverlayTapped(object sender, TappedRoutedEventArgs e)
    {
        // A click on the image itself keeps the peek open; anywhere else dismisses.
        if (e.OriginalSource is not Image)
        {
            Close();
        }
    }

    private void OnKeyDown(object sender, KeyRoutedEventArgs e)
    {
        if (e.Key == VirtualKey.Escape)
        {
            Close();
            e.Handled = true;
        }
    }

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
}