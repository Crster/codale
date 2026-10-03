using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Media.Animation;

namespace Codale.App.Views;

/// <summary>
/// Attached property that loops a gentle opacity pulse on an element - the "still
/// working" signal on a tool call's icon. A data template cannot start a storyboard
/// itself (there is no way to address the realised element by name from the page),
/// so the animation is built in code against whatever the property lands on, and
/// torn down when the value goes false or the element unloads.
/// </summary>
public static class Pulse
{
    private static readonly DependencyProperty StoryboardProperty = DependencyProperty.RegisterAttached(
        "StoryboardHolder",
        typeof(Storyboard),
        typeof(Pulse),
        new PropertyMetadata(null));

    public static readonly DependencyProperty IsActiveProperty = DependencyProperty.RegisterAttached(
        "IsActive",
        typeof(bool),
        typeof(Pulse),
        new PropertyMetadata(false, OnIsActiveChanged));

    public static bool GetIsActive(FrameworkElement element) => (bool)element.GetValue(IsActiveProperty);

    public static void SetIsActive(FrameworkElement element, bool value) => element.SetValue(IsActiveProperty, value);

    /// <summary>Marks an element whose Loaded/Unloaded hooks are already attached.</summary>
    private static readonly DependencyProperty HookedProperty = DependencyProperty.RegisterAttached(
        "PulseHooked",
        typeof(bool),
        typeof(Pulse),
        new PropertyMetadata(false));

    private static void OnIsActiveChanged(DependencyObject sender, DependencyPropertyChangedEventArgs e)
    {
        if (sender is not FrameworkElement element)
        {
            return;
        }

        // A forever storyboard keeps ticking after its element leaves the tree (a
        // recycled row, a closed tab), costing the UI thread every frame for nothing:
        // it runs only while the element is loaded.
        if (element.GetValue(HookedProperty) is not true)
        {
            element.SetValue(HookedProperty, true);
            element.Loaded += (_, _) => Apply(element);
            element.Unloaded += (_, _) => Stop(element);
        }

        Apply(element);
    }

    private static void Apply(FrameworkElement element)
    {
        Stop(element);

        if (!GetIsActive(element) || !element.IsLoaded)
        {
            return;
        }

        var fade = new DoubleAnimation
        {
            From = 1.0,
            To = 0.35,
            Duration = new Duration(TimeSpan.FromMilliseconds(700)),
            AutoReverse = true,
            RepeatBehavior = RepeatBehavior.Forever,
        };
        Storyboard.SetTarget(fade, element);
        Storyboard.SetTargetProperty(fade, "Opacity");

        var storyboard = new Storyboard();
        storyboard.Children.Add(fade);
        element.SetValue(StoryboardProperty, storyboard);
        storyboard.Begin();
    }

    private static void Stop(FrameworkElement element)
    {
        if (element.GetValue(StoryboardProperty) is Storyboard running)
        {
            running.Stop();
            element.ClearValue(StoryboardProperty);
        }

        element.Opacity = 1.0;
    }
}
