using System.Collections.Specialized;
using System.Runtime.CompilerServices;

using Codale.App.ViewModels;

using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Controls.Primitives;
using Microsoft.UI.Xaml.Input;
using Microsoft.UI.Xaml.Media;

namespace Codale.App.Views;

/// <summary>
/// Lets the reader point at a passage of a rendered plan and say what should change:
/// selecting text offers an Annotate chip, right-click offers the same, and annotated
/// passages stay highlighted. The notes go on the plan's artifact, so every view of the
/// plan - the chat card, the plan tab - shows and sends the same list.
/// </summary>
internal sealed class PlanAnnotator
{
    private static readonly ConditionalWeakTable<MarkdownView, PlanAnnotator> Attached = new();

    private readonly MarkdownView _markdown;

    private SessionArtifact? _plan;

    /// <summary>The "Annotate" chip shown beside a fresh selection; one at a time.</summary>
    private Flyout? _chip;

    private IReadOnlyList<string> _shownHighlights = [];

    /// <summary>The annotator of a view, created on first use; the markdown must let the host own its text menu.</summary>
    public static PlanAnnotator For(MarkdownView markdown) => Attached.GetValue(markdown, m => new PlanAnnotator(m));

    private PlanAnnotator(MarkdownView markdown)
    {
        _markdown = markdown;

        // Selectable text handles pointer input itself, so listen even for handled events.
        markdown.AddHandler(UIElement.RightTappedEvent, new RightTappedEventHandler(OnRightTapped), true);
        markdown.AddHandler(UIElement.PointerReleasedEvent, new PointerEventHandler(OnPointerReleased), true);

        // A transcript keeps many cards; only those on screen listen to their plan.
        markdown.Loaded += (_, _) => Subscribe(_plan);
        markdown.Unloaded += (_, _) => Unsubscribe(_plan);
    }

    /// <summary>The plan being read; null turns annotating off (e.g. an image artifact).</summary>
    public SessionArtifact? Plan
    {
        get => _plan;
        set
        {
            if (ReferenceEquals(_plan, value))
            {
                return;
            }

            Unsubscribe(_plan);
            _plan = value;
            if (_markdown.IsLoaded)
            {
                Subscribe(value);
            }

            RefreshHighlights();
        }
    }

    private void Subscribe(SessionArtifact? plan)
    {
        if (plan is not null)
        {
            plan.Annotations.CollectionChanged -= OnAnnotationsChanged;
            plan.Annotations.CollectionChanged += OnAnnotationsChanged;
            RefreshHighlights();
        }
    }

    private void Unsubscribe(SessionArtifact? plan)
    {
        if (plan is not null)
        {
            plan.Annotations.CollectionChanged -= OnAnnotationsChanged;
        }
    }

    private void OnAnnotationsChanged(object? sender, NotifyCollectionChangedEventArgs e) => RefreshHighlights();

    /// <summary>Highlights rebuild the whole view, so they are only pushed when they change.</summary>
    private void RefreshHighlights()
    {
        var passages = _plan?.Annotations.Select(a => a.Quote).Where(q => q.Length > 0).ToList() ?? [];
        if (passages.SequenceEqual(_shownHighlights))
        {
            return;
        }

        _shownHighlights = passages;
        _markdown.SetHighlights(passages);
    }

    private bool CanAnnotate => _plan is { IsImage: false };

    private void OnPointerReleased(object sender, PointerRoutedEventArgs e)
    {
        if (!CanAnnotate ||
            e.GetCurrentPoint(_markdown).Properties.PointerUpdateKind != Microsoft.UI.Input.PointerUpdateKind.LeftButtonReleased)
        {
            return;
        }

        var point = e.GetCurrentPoint(_markdown).Position;
        var hit = e.GetCurrentPoint(null).Position;

        // The text block settles its selection after the release; read it once it has.
        _markdown.DispatcherQueue.TryEnqueue(() =>
        {
            var quote = VisualTreeHelper
                .FindElementsInHostCoordinates(hit, _markdown)
                .OfType<TextBlock>()
                .Select(b => b.SelectedText)
                .FirstOrDefault(s => !string.IsNullOrWhiteSpace(s))
                ?.Trim();
            if (quote is { Length: > 0 })
            {
                ShowChip(quote, point);
            }
        });
    }

    private void ShowChip(string quote, Windows.Foundation.Point point)
    {
        _chip?.Hide();
        var annotate = new Button
        {
            Content = new StackPanel
            {
                Orientation = Orientation.Horizontal,
                Spacing = 6,
                Children =
                {
                    new FontIcon { Glyph = "", FontSize = 12 },
                    new TextBlock { Text = "Annotate", FontSize = 12 },
                },
            },
            Style = (Style)Application.Current.Resources["AccentButtonStyle"],
        };
        var chip = new Flyout
        {
            Content = annotate,
            ShouldConstrainToRootBounds = true,
            FlyoutPresenterStyle = new Style(typeof(FlyoutPresenter))
            {
                Setters =
                {
                    new Setter(Control.PaddingProperty, new Thickness(4)),
                    new Setter(FrameworkElement.MinWidthProperty, 0d),
                    new Setter(FrameworkElement.MinHeightProperty, 0d),
                },
            },
        };
        annotate.Click += (_, _) =>
        {
            chip.Hide();
            Prompt(quote, point);
        };
        chip.Closed += (_, _) =>
        {
            if (ReferenceEquals(_chip, chip))
            {
                _chip = null;
            }
        };
        _chip = chip;
        chip.ShowAt(_markdown, new FlyoutShowOptions
        {
            Position = new Windows.Foundation.Point(point.X, point.Y + 14),
            ShowMode = FlyoutShowMode.Transient,
        });
    }

    private void OnRightTapped(object sender, RightTappedRoutedEventArgs e)
    {
        if (!CanAnnotate)
        {
            return;
        }

        var point = e.GetPosition(_markdown);
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
        add.Click += (_, _) => Prompt(quote, point);
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

        menu.ShowAt(_markdown, point);
        e.Handled = true;
    }

    private void Prompt(string quote, Windows.Foundation.Point at)
    {
        var plan = _plan;
        if (plan is null)
        {
            return;
        }

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
                plan.Annotations.Add(new PlanAnnotation(quote, note));
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
        flyout.ShowAt(_markdown, new FlyoutShowOptions { Position = at });
    }
}

/// <summary>
/// A plan's pending annotations with Send and Clear; collapsed while there are none.
/// Hosts style the frame (padding, border, background) to suit where it sits.
/// </summary>
public sealed partial class PlanAnnotationBar : Grid
{
    private readonly StackPanel _list = new() { Spacing = 6 };

    private SessionArtifact? _plan;

    /// <summary>The reader sent the annotations (already cleared); the text is them as one revision request.</summary>
    public event EventHandler<string>? SendRequested;

    public PlanAnnotationBar()
    {
        Visibility = Visibility.Collapsed;

        var send = new Button { Content = "Send to agent", Style = (Style)Application.Current.Resources["AccentButtonStyle"] };
        send.Click += (_, _) => Send();
        var clear = new Button { Content = "Clear" };
        clear.Click += (_, _) => _plan?.Annotations.Clear();
        var actions = new StackPanel { Orientation = Orientation.Horizontal, Spacing = 8 };
        actions.Children.Add(send);
        actions.Children.Add(clear);

        var bar = new StackPanel { Spacing = 8, MaxWidth = 860, HorizontalAlignment = HorizontalAlignment.Left };
        bar.Children.Add(new TextBlock
        {
            Text = "Annotations",
            FontSize = 12,
            FontWeight = Microsoft.UI.Text.FontWeights.SemiBold,
            Foreground = (Brush)Application.Current.Resources["TextFillColorSecondaryBrush"],
        });
        bar.Children.Add(_list);
        bar.Children.Add(actions);
        Children.Add(bar);

        Loaded += (_, _) => Subscribe();
        Unloaded += (_, _) => Unsubscribe();
    }

    /// <summary>Internal: XAML type info must not reach SessionArtifact, whose members are required.</summary>
    internal SessionArtifact? Plan
    {
        get => _plan;
        set
        {
            if (ReferenceEquals(_plan, value))
            {
                return;
            }

            Unsubscribe();
            _plan = value;
            if (IsLoaded)
            {
                Subscribe();
            }

            Render();
        }
    }

    private void Subscribe()
    {
        if (_plan is not null)
        {
            _plan.Annotations.CollectionChanged -= OnAnnotationsChanged;
            _plan.Annotations.CollectionChanged += OnAnnotationsChanged;
        }

        Render();
    }

    private void Unsubscribe()
    {
        if (_plan is not null)
        {
            _plan.Annotations.CollectionChanged -= OnAnnotationsChanged;
        }
    }

    private void OnAnnotationsChanged(object? sender, NotifyCollectionChangedEventArgs e) => Render();

    private void Render()
    {
        _list.Children.Clear();
        var annotations = _plan?.Annotations.ToList() ?? [];
        Visibility = annotations.Count > 0 ? Visibility.Visible : Visibility.Collapsed;

        foreach (var entry in annotations)
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
            ToolTipService.SetToolTip(remove, "Remove annotation");
            remove.Click += (_, _) => _plan?.Annotations.Remove(entry);

            Grid.SetColumn(remove, 1);
            row.Children.Add(text);
            row.Children.Add(remove);
            _list.Children.Add(row);
        }
    }

    private void Send()
    {
        if (_plan is not { Annotations.Count: > 0 } plan)
        {
            return;
        }

        var message = plan.RevisionMessage();
        plan.Annotations.Clear();
        SendRequested?.Invoke(this, message);
    }
}
