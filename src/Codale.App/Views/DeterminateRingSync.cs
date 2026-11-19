using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Media;

namespace Codale.App.Views;

/// <summary>Keeps the native quota ring's Lottie position aligned with its unchanged Value.</summary>
internal static class DeterminateRingSync
{
    // WinUI can lose the determinate visual position while retaining Value, e.g. on resume:
    // https://github.com/microsoft/microsoft-ui-xaml/issues/10625
    // Reassigning the same Value is a no-op. Seek the existing player instead; this keeps
    // the original ProgressRing, template, brushes, bindings and accessibility range intact.
    public static void Refresh(DependencyObject root)
    {
        if (root is ProgressRing ring)
        {
            var range = ring.Maximum - ring.Minimum;
            if (ring.IsActive && !ring.IsIndeterminate && double.IsFinite(ring.Value) && range > 0
                && FindPlayer(ring) is { IsAnimatedVisualLoaded: true, IsPlaying: false } player)
            {
                player.SetProgress(Math.Clamp((ring.Value - ring.Minimum) / range, 0, 1));
            }
            return;
        }

        for (var i = 0; i < VisualTreeHelper.GetChildrenCount(root); i++)
            Refresh(VisualTreeHelper.GetChild(root, i));
    }

    private static AnimatedVisualPlayer? FindPlayer(DependencyObject root)
    {
        for (var i = 0; i < VisualTreeHelper.GetChildrenCount(root); i++)
        {
            var child = VisualTreeHelper.GetChild(root, i);
            if (child is AnimatedVisualPlayer { Name: "LottiePlayer" } player)
                return player;
            if (FindPlayer(child) is { } nested)
                return nested;
        }
        return null;
    }
}
