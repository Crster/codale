using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Media;

using Windows.Foundation;

namespace Codale.App.Views;

/// <summary>Aligns text baselines across independently clickable controls in a single-line header.</summary>
public static class TextBaseline
{
    public static readonly DependencyProperty IsAlignedProperty = DependencyProperty.RegisterAttached(
        "IsAligned", typeof(bool), typeof(TextBaseline), new PropertyMetadata(false, OnChanged));

    private static readonly DependencyProperty StateProperty = DependencyProperty.RegisterAttached(
        "State", typeof(State), typeof(TextBaseline), new PropertyMetadata(null));

    public static bool GetIsAligned(FrameworkElement element) => (bool)element.GetValue(IsAlignedProperty);
    public static void SetIsAligned(FrameworkElement element, bool value) => element.SetValue(IsAlignedProperty, value);

    private static void OnChanged(DependencyObject sender, DependencyPropertyChangedEventArgs args)
    {
        if (sender is not FrameworkElement root) return;
        if (root.GetValue(StateProperty) is State previous) previous.Detach();
        root.ClearValue(StateProperty);
        if (args.NewValue is true) root.SetValue(StateProperty, new State(root));
    }

    private sealed class State
    {
        private readonly FrameworkElement _root;
        private readonly Dictionary<TextBlock, TranslateTransform> _text = [];
        private bool _queued;

        public State(FrameworkElement root)
        {
            _root = root;
            root.Loaded += OnLoaded;
            root.Unloaded += OnUnloaded;
            root.SizeChanged += OnSizeChanged;
            Queue();
        }

        public void Detach()
        {
            _root.Loaded -= OnLoaded;
            _root.Unloaded -= OnUnloaded;
            _root.SizeChanged -= OnSizeChanged;
            Clear();
        }

        private void OnLoaded(object sender, RoutedEventArgs args) => Queue();
        private void OnUnloaded(object sender, RoutedEventArgs args) => Clear();
        private void OnSizeChanged(object sender, SizeChangedEventArgs args) => Queue();

        private void Clear()
        {
            foreach (var (block, shift) in _text)
            {
                block.SizeChanged -= OnSizeChanged;
                if (ReferenceEquals(block.RenderTransform, shift)) block.ClearValue(UIElement.RenderTransformProperty);
            }
            _text.Clear();
        }

        private void Queue()
        {
            if (_queued || !_root.IsLoaded) return;
            _queued = _root.DispatcherQueue.TryEnqueue(() =>
            {
                _queued = false;
                if (_root.IsLoaded && GetIsAligned(_root)) Align();
            });
        }

        private void Align()
        {
            var blocks = Descendants(_root).OfType<TextBlock>().ToHashSet();
            foreach (var block in _text.Keys.Where(block => !blocks.Contains(block)).ToList())
            {
                block.SizeChanged -= OnSizeChanged;
                if (ReferenceEquals(block.RenderTransform, _text[block])) block.ClearValue(UIElement.RenderTransformProperty);
                _text.Remove(block);
            }
            foreach (var block in blocks)
            {
                // Header text does not otherwise have a transform; leave custom transforms alone.
                if (_text.ContainsKey(block) || block.ReadLocalValue(UIElement.RenderTransformProperty) != DependencyProperty.UnsetValue) continue;
                var shift = new TranslateTransform();
                _text.Add(block, shift);
                block.RenderTransform = shift;
                block.SizeChanged += OnSizeChanged;
            }

            var visible = _text.Where(pair => pair.Key.ActualHeight > 0 && IsVisible(pair.Key))
                .Select(pair => (Block: pair.Key, Shift: pair.Value,
                    Baseline: pair.Key.TransformToVisual(_root).TransformPoint(new Point(0, pair.Key.BaselineOffset)).Y - pair.Value.Y))
                .ToList();
            if (visible.Count == 0) return;
            var baseline = visible.Max(item => item.Baseline);
            foreach (var item in visible) item.Shift.Y = baseline - item.Baseline;
        }

        private bool IsVisible(DependencyObject element)
        {
            for (var node = element; node is not null && !ReferenceEquals(node, _root); node = VisualTreeHelper.GetParent(node))
                if (node is UIElement { Visibility: Visibility.Collapsed }) return false;
            return true;
        }

        private static IEnumerable<DependencyObject> Descendants(DependencyObject parent)
        {
            for (var i = 0; i < VisualTreeHelper.GetChildrenCount(parent); i++)
            {
                var child = VisualTreeHelper.GetChild(parent, i);
                if (child is IconElement) continue;
                yield return child;
                foreach (var descendant in Descendants(child)) yield return descendant;
            }
        }
    }
}
