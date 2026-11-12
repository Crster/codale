using Codale.Core.Agents;
using Codale.Core.Text;

using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Documents;
using Microsoft.UI.Xaml.Media;
using Microsoft.UI.Xaml.Media.Imaging;

namespace Codale.App.Views;

/// <summary>
/// A sent prompt, with its attachments drawn inline as the same pills the composer showed.
/// Attachments the text no longer mentions lead the message, so none is lost. Clicking a pill opens the file.
/// </summary>
public sealed partial class PromptText : ContentControl
{
    public static readonly DependencyProperty TextProperty = DependencyProperty.Register(
        nameof(Text), typeof(string), typeof(PromptText), new PropertyMetadata("", (d, _) => ((PromptText)d).Build()));

    public static readonly DependencyProperty AttachmentsProperty = DependencyProperty.Register(
        nameof(Attachments), typeof(object), typeof(PromptText), new PropertyMetadata(null, (d, _) => ((PromptText)d).Build()));

    private readonly RichTextBlock _block = new()
    {
        TextWrapping = TextWrapping.Wrap,
        IsTextSelectionEnabled = true,
    };

    public PromptText()
    {
        HorizontalContentAlignment = HorizontalAlignment.Stretch;
        IsTabStop = false;
        Content = _block;
        ActualThemeChanged += (_, _) => Build();
    }

    public string Text
    {
        get => (string)GetValue(TextProperty);
        set => SetValue(TextProperty, value);
    }

    public IReadOnlyList<TurnAttachment>? Attachments
    {
        get => (IReadOnlyList<TurnAttachment>?)GetValue(AttachmentsProperty);
        set => SetValue(AttachmentsProperty, value);
    }

    private void Build()
    {
        var text = (Text ?? "").Replace("\r\n", "\n").Replace('\r', '\n');
        var attachments = Attachments ?? [];
        var pills = PromptPills.Find(text);

        // Each attachment pairs with the first mention of its name; the rest lead the message.
        var unclaimed = attachments.ToList();
        var paired = new Dictionary<PromptPill, TurnAttachment>();
        foreach (var pill in pills)
        {
            if (unclaimed.FirstOrDefault(a => Mentions(pill, a)) is { } attachment)
            {
                paired[pill] = attachment;
                unclaimed.Remove(attachment);
            }
        }

        var paragraph = new Paragraph();
        foreach (var attachment in unclaimed)
        {
            paragraph.Inlines.Add(PillInline(KindOf(attachment.Kind), attachment.Name, attachment.Path));
            paragraph.Inlines.Add(new Run { Text = " " });
        }

        var at = 0;
        foreach (var (pill, attachment) in paired.OrderBy(p => p.Key.Start))
        {
            // Only a real attachment is a pill; a mention typed by hand stays text.
            AddText(paragraph, text[at..pill.Start]);
            paragraph.Inlines.Add(PillInline(pill.Kind, attachment.Name, attachment.Path));
            at = pill.End;
        }

        AddText(paragraph, text[at..]);

        _block.Blocks.Clear();
        _block.Blocks.Add(paragraph);
    }

    private static PromptPillKind KindOf(TurnAttachmentKind kind) => kind switch
    {
        TurnAttachmentKind.Image => PromptPillKind.Image,
        TurnAttachmentKind.Pdf => PromptPillKind.Pdf,
        _ => PromptPillKind.Text,
    };

    private static bool Mentions(PromptPill pill, TurnAttachment attachment) =>
        string.Equals(pill.Target, attachment.Name, StringComparison.OrdinalIgnoreCase) ||
        string.Equals(pill.Target, attachment.Path, StringComparison.OrdinalIgnoreCase);

    private static void AddText(Paragraph paragraph, string text)
    {
        var lines = text.Split('\n');
        for (var i = 0; i < lines.Length; i++)
        {
            if (i > 0)
            {
                paragraph.Inlines.Add(new LineBreak());
            }

            if (lines[i].Length > 0)
            {
                paragraph.Inlines.Add(new Run { Text = lines[i] });
            }
        }
    }

    private InlineUIContainer PillInline(PromptPillKind kind, string title, string path)
    {
        var theme = ActualTheme;
        var content = new StackPanel { Orientation = Orientation.Horizontal, Spacing = 4 };

        if (kind == PromptPillKind.Image && File.Exists(path))
        {
            content.Children.Add(new Border
            {
                Width = 14,
                Height = 14,
                CornerRadius = new CornerRadius(3),
                VerticalAlignment = VerticalAlignment.Center,
                Child = new Image
                {
                    Stretch = Stretch.UniformToFill,
                    Source = new BitmapImage(new Uri(path)) { DecodePixelHeight = 32 },
                },
            });
        }
        else
        {
            content.Children.Add(new FontIcon
            {
                Glyph = PromptPillVisuals.Glyph(kind),
                FontSize = PromptPillVisuals.GlyphSize,
                Foreground = new SolidColorBrush(PromptPillVisuals.GlyphColor(kind, theme)),
                VerticalAlignment = VerticalAlignment.Center,
            });
        }

        content.Children.Add(new TextBlock
        {
            Text = title,
            FontSize = PromptPillVisuals.TitleSize,
            MaxWidth = PromptPillVisuals.MaxTitleWidth,
            TextTrimming = TextTrimming.CharacterEllipsis,
            Foreground = new SolidColorBrush(PromptPillVisuals.Foreground(theme)),
            VerticalAlignment = VerticalAlignment.Center,
        });

        var chip = new Border
        {
            Height = PromptPillVisuals.Height,
            Padding = new Thickness(6, 0, 7, 0),
            CornerRadius = new CornerRadius(5),
            BorderThickness = new Thickness(1),
            Background = new SolidColorBrush(PromptPillVisuals.Fill(theme)),
            BorderBrush = new SolidColorBrush(PromptPillVisuals.Stroke(theme)),
            Child = content,

            // An inline element sits on the baseline; this lowers the pill to centre on the text.
            RenderTransform = new TranslateTransform { Y = 5 },
        };

        // The pill opens a preview: it reads as a link - hand cursor, a lift on hover.
        var host = new HandCursorHost { Children = { chip }, RenderTransform = chip.RenderTransform };
        chip.RenderTransform = null;
        var rest = chip.Background;
        var hover = new SolidColorBrush(PromptPillVisuals.Stroke(theme));
        host.PointerEntered += (_, _) => chip.Background = hover;
        host.PointerExited += (_, _) => chip.Background = rest;
        host.PointerCanceled += (_, _) => chip.Background = rest;

        ToolTipService.SetToolTip(host, "Preview");
        host.Tapped += async (_, e) =>
        {
            e.Handled = true;
            await PromptPillVisuals.OpenAsync(XamlRoot, kind, title, path);
        };

        return new InlineUIContainer { Child = host };
    }
}
