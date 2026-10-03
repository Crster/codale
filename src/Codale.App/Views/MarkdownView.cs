using Codale.App.Services;
using Codale.Core.Markdown;

using Microsoft.UI.Dispatching;
using Microsoft.UI.Text;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Documents;
using Microsoft.UI.Xaml.Media;
using Microsoft.UI.Xaml.Media.Imaging;
using Microsoft.UI.Xaml.Shapes;

using Windows.Storage.Streams;
using Windows.UI;

namespace Codale.App.Views;

/// <summary>
/// Renders markdown text as real blocks - headings, paragraphs, lists, fenced code,
/// quotes, rules - instead of the raw source. A StackPanel subclass because block
/// layout (a code fence's background, a quote's bar, hanging-indent list rows) cannot
/// live in one TextBlock's inlines; the parser lives in Codale.Core and is tested
/// there. Style colours resolve from the theme resources with fixed fallbacks, the
/// same "picked medium for both themes" trade CodeText's palette makes, and the view
/// rebuilds on theme change.
/// </summary>
public sealed class MarkdownView : StackPanel
{
    private static readonly FontFamily CodeFont = new("Cascadia Mono, Consolas");

    /// <summary>Largest accepted data: URI payload, base64 characters; a screenshot fits,
    /// a paste that would wedge the stream does not.</summary>
    private const int MaxDataImageBase64 = 12 * 1024 * 1024;

    /// <summary>While a reply streams, deltas land many times a second; this debounces
    /// the rebuild so each frame parses at most once.</summary>
    private readonly DispatcherQueueTimer _debounce;

    public MarkdownView()
    {
        Spacing = 0;
        _debounce = DispatcherQueue.GetForCurrentThread().CreateTimer();
        _debounce.Interval = TimeSpan.FromMilliseconds(60);
        _debounce.Tick += (_, _) =>
        {
            _debounce.Stop();
            Rebuild();
        };
        ActualThemeChanged += (_, _) =>
        {
            _markdownStale = true;
            Rebuild();
        };

        // A non-virtualized transcript accumulates hundreds of these over a session;
        // an offscreen or unloaded view must not keep a timer ticking (or rebuild at all).
        Loaded += (_, _) =>
        {
            Services.SyntaxSelection.Start();
            Core.Syntax.SyntaxService.Store.Changed += OnGrammarsChanged;
            if (_markdownStale)
            {
                Rebuild();
            }
        };
        Unloaded += (_, _) =>
        {
            _debounce.Stop();
            Core.Syntax.SyntaxService.Store.Changed -= OnGrammarsChanged;
        };
    }

    /// <summary>A grammar was installed or replaced: fences may now colour differently.</summary>
    private void OnGrammarsChanged() => DispatcherQueue.TryEnqueue(() =>
    {
        _markdownStale = true;
        if (IsLoaded)
        {
            Rebuild();
        }
    });

    public static readonly DependencyProperty MarkdownProperty = DependencyProperty.Register(
        nameof(Markdown),
        typeof(string),
        typeof(MarkdownView),
        new PropertyMetadata(null, OnMarkdownChanged));

    /// <summary>The markdown source to render; null or empty renders nothing.</summary>
    public string? Markdown
    {
        get => (string?)GetValue(MarkdownProperty);
        set => SetValue(MarkdownProperty, value);
    }

    public static readonly DependencyProperty BaseFontSizeProperty = DependencyProperty.Register(
        nameof(BaseFontSize),
        typeof(double),
        typeof(MarkdownView),
        new PropertyMetadata(12.5, OnMarkdownChanged));

    /// <summary>Body text size; headings scale off this.</summary>
    public double BaseFontSize
    {
        get => (double)GetValue(BaseFontSizeProperty);
        set => SetValue(BaseFontSizeProperty, value);
    }

    public static readonly DependencyProperty BasePathProperty = DependencyProperty.Register(
        nameof(BasePath),
        typeof(string),
        typeof(MarkdownView),
        new PropertyMetadata(null, OnMarkdownChanged));

    /// <summary>The folder relative image paths resolve against; null leaves them unresolved.</summary>
    public string? BasePath
    {
        get => (string?)GetValue(BasePathProperty);
        set => SetValue(BasePathProperty, value);
    }

    private static void OnMarkdownChanged(DependencyObject sender, DependencyPropertyChangedEventArgs e)
    {
        if (sender is MarkdownView view)
        {
            // Restart rather than render: deltas arriving in a burst pay for one parse.
            view._markdownStale = true;
            view._debounce.Stop();

            if (view.IsLoaded)
            {
                view._debounce.Start();
            }
        }
    }

    /// <summary>Text or theme changed while the view was not loaded; rebuilt on Loaded.</summary>
    private bool _markdownStale;

    private void Rebuild()
    {
        _markdownStale = false;
        Children.Clear();

        if (string.IsNullOrEmpty(Markdown))
        {
            return;
        }

        RenderBlocks(Children, MarkdownParser.Parse(Markdown), BaseFontSize, topBlock: true);
    }

    private void RenderBlocks(UIElementCollection into, IReadOnlyList<MarkdownBlock> blocks, double size, bool topBlock)
    {
        foreach (var block in blocks)
        {
            switch (block)
            {
                case MarkdownBlock.Heading heading:
                    into.Add(Heading(heading, size, topBlock));
                    break;

                case MarkdownBlock.Paragraph paragraph:
                    AddWithImages(into, paragraph.Content, size, content => Prose(content, size, (0, 0, 0, 2)));
                    break;

                case MarkdownBlock.ListItem item:
                    into.Add(ListRow(item, size));
                    break;

                case MarkdownBlock.CodeBlock code:
                    into.Add(CodeFence(code, size));
                    break;

                case MarkdownBlock.Quote quote:
                    into.Add(Quoted(quote, size));
                    break;

                case MarkdownBlock.Table table:
                    into.Add(TableGrid(table, size));
                    break;

                case MarkdownBlock.ThematicBreak:
                    into.Add(new Rectangle
                    {
                        Height = 1,
                        Margin = new Thickness(2, 3, 2, 3),
                        Fill = ThemeBrush("DividerStrokeColorDefaultBrush", 0x50, 0x55, 0x60),
                    });
                    break;
            }

            topBlock = false;
        }
    }

    private TextBlock Heading(MarkdownBlock.Heading heading, double size, bool topBlock)
    {
        var text = Prose(heading.Content, HeadingSize(heading.Level, size), (0, topBlock ? 0 : 8, 0, 2));
        text.FontWeight = FontWeights.Bold;
        text.Foreground = heading.Level switch
        {
            1 => ThemeBrush("MdHeading1Brush", 0x9D, 0xB8, 0xFF),
            2 => ThemeBrush("MdHeading2Brush", 0x7C, 0xC4, 0xFA),
            _ => ThemeBrush("MdHeading3Brush", 0x6E, 0xDD, 0xC8),
        };
        return text;
    }

    private double HeadingSize(int level, double size) => level switch
    {
        1 => size + 5,
        2 => size + 3,
        3 => size + 1.5,
        _ => size + 0.5,
    };

    private TextBlock Prose(IReadOnlyList<MarkdownInline> content, double size, (int L, int T, int R, int B) margin)
    {
        var text = new TextBlock
        {
            FontSize = size,
            TextWrapping = TextWrapping.Wrap,
            IsTextSelectionEnabled = true,
            Foreground = ThemeBrush("MdBodyBrush", 0xD4, 0xD7, 0xDF),
            Margin = new Thickness(margin.L, margin.T, margin.R, margin.B),
        };
        FillInlines(text.Inlines, content, size);
        Highlight(text);
        return text;
    }

    private IReadOnlyList<string> _highlights = [];

    /// <summary>Passages to mark, e.g. annotated plan text; matched against each block's plain text.</summary>
    public void SetHighlights(IReadOnlyList<string> passages)
    {
        _highlights = passages;
        _debounce.Stop();
        if (IsLoaded)
        {
            Rebuild();
        }
        else
        {
            _markdownStale = true;
        }
    }

    /// <summary>A block's text as the reader sees it, which is what a quote is taken from.</summary>
    internal static string PlainText(TextBlock block) => string.Concat(block.Inlines.Select(PlainText));

    private static string PlainText(Inline inline) => inline switch
    {
        Run run => run.Text,
        Span span => string.Concat(span.Inlines.Select(PlainText)),
        _ => "",
    };

    private void Highlight(TextBlock block)
    {
        if (_highlights.Count == 0)
        {
            return;
        }

        var plain = PlainText(block);
        TextHighlighter? highlighter = null;

        foreach (var passage in _highlights)
        {
            var at = passage.Length == 0 ? -1 : plain.IndexOf(passage, StringComparison.Ordinal);
            if (at < 0)
            {
                continue;
            }

            highlighter ??= new TextHighlighter { Background = new SolidColorBrush(Color.FromArgb(0x66, 0xF2, 0xC0, 0x30)) };
            highlighter.Ranges.Add(new TextRange { StartIndex = at, Length = passage.Length });
        }

        if (highlighter is not null)
        {
            block.TextHighlighters.Add(highlighter);
        }
    }

    private Grid ListRow(MarkdownBlock.ListItem item, double size)
    {
        var numbered = item.Number > 0;
        var marker = numbered ? $"{item.Number}." : DepthMarker(item.Depth);

        // The star column gives wrapped lines a hanging indent: they return under the
        // text, not under the marker. Numbers right-align so 9. and 10. stay flush.
        var row = new Grid { Margin = new Thickness(item.Depth * 14, 0, 0, 2), ColumnSpacing = 6 };
        row.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
        row.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });

        var bullet = new TextBlock
        {
            Text = marker,
            FontSize = size,
            MinWidth = numbered ? 16 : 10,
            TextAlignment = numbered ? TextAlignment.Right : TextAlignment.Left,
            Foreground = ThemeBrush("MdBulletBrush", 0x8B, 0x97, 0xF8),
            FontWeight = FontWeights.SemiBold,
        };
        Grid.SetColumn(bullet, 0);

        var content = Prose(item.Content, size, (0, 0, 0, 0));
        Grid.SetColumn(content, 1);

        row.Children.Add(bullet);
        row.Children.Add(content);
        return row;
    }

    /// <summary>• at the outer level, then alternating dash and wedge for the nesting
    /// the parser flattens to depths.</summary>
    private static string DepthMarker(int depth) => depth switch
    {
        1 => "\u2013",  // en dash
        2 => "\u25AA",  // small square
        _ => "\u2022",  // bullet
    };

    private Border CodeFence(MarkdownBlock.CodeBlock code, double size)
    {
        var text = new TextBlock
        {
            FontFamily = CodeFont,
            // Code runs a hair under the prose size, the same relation inline code has.
            FontSize = size - 0.5,
            TextWrapping = TextWrapping.Wrap,
            IsTextSelectionEnabled = true,
        };

        // A fence naming a language the catalog knows is coloured by its grammar; a shell fence with
        // no usable grammar keeps the command-row colours, and everything else stays plain so prose
        // fences ("text", "diff output") stay calm.
        Services.SyntaxSelection.Start();
        var language = Core.Syntax.SyntaxService.Catalog.ByName(code.Language);
        if (language is not null && Core.Syntax.SyntaxService.Tokenizer.Supports(language.Id))
        {
            text.SetValue(CodeText.CodeProperty, code.Text);
            text.SetValue(CodeText.SyntaxProperty, CodeText.ForLanguage(language.Id));
        }
        else if (code.Language?.ToLowerInvariant() is "shell" or "sh" or "bash" or "powershell" or "pwsh" or "cmd" or "zsh")
        {
            text.SetValue(CodeText.CodeProperty, code.Text);
            text.SetValue(CodeText.SyntaxProperty, "shell");
        }
        else
        {
            text.Text = code.Text;
        }

        return new Border
        {
            Background = ThemeBrush("SubtleFillColorSecondaryBrush", 0x2B, 0x2F, 0x38),
            CornerRadius = new CornerRadius(4),
            Padding = new Thickness(8, 6, 8, 6),
            Margin = new Thickness(0, 2, 0, 2),
            Child = text,
        };
    }

    /// <summary>A grid of bordered cells; the header row is bold on a subtle fill.
    /// Columns size to content, the last one takes the remaining width and wraps.</summary>
    private Border TableGrid(MarkdownBlock.Table table, double size)
    {
        var line = ThemeBrush("DividerStrokeColorDefaultBrush", 0x50, 0x55, 0x60);
        var grid = new Grid();
        var columns = table.Alignments.Count;

        for (var c = 0; c < columns; c++)
        {
            grid.ColumnDefinitions.Add(new ColumnDefinition
            {
                Width = c == columns - 1 ? new GridLength(1, GridUnitType.Star) : GridLength.Auto,
            });
        }

        void AddRow(IReadOnlyList<IReadOnlyList<MarkdownInline>> cells, int row, bool header)
        {
            grid.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });

            for (var c = 0; c < columns; c++)
            {
                var text = Prose(cells[c], size, (0, 0, 0, 0));
                text.TextAlignment = table.Alignments[c] switch
                {
                    TableAlignment.Center => TextAlignment.Center,
                    TableAlignment.Right => TextAlignment.Right,
                    _ => TextAlignment.Left,
                };

                if (header)
                {
                    text.FontWeight = FontWeights.SemiBold;
                }

                var cell = new Border
                {
                    BorderBrush = line,
                    BorderThickness = new Thickness(c == 0 ? 0 : 1, row == 0 ? 0 : 1, 0, 0),
                    Padding = new Thickness(8, 4, 8, 4),
                    Background = header ? ThemeBrush("SubtleFillColorSecondaryBrush", 0x2B, 0x2F, 0x38) : null,
                    MaxWidth = c == columns - 1 ? double.PositiveInfinity : 320,
                    Child = text,
                };
                Grid.SetRow(cell, row);
                Grid.SetColumn(cell, c);
                grid.Children.Add(cell);
            }
        }

        AddRow(table.Header, 0, header: true);

        for (var r = 0; r < table.Rows.Count; r++)
        {
            AddRow(table.Rows[r], r + 1, header: false);
        }

        return new Border
        {
            BorderBrush = line,
            BorderThickness = new Thickness(1),
            CornerRadius = new CornerRadius(4),
            Margin = new Thickness(0, 2, 0, 4),
            HorizontalAlignment = HorizontalAlignment.Left,
            Child = grid,
        };
    }

    private Border Quoted(MarkdownBlock.Quote quote, double size)
    {
        var inner = new StackPanel { Opacity = 0.85 };
        RenderBlocks(inner.Children, quote.Content, size, topBlock: true);
        return new Border
        {
            BorderThickness = new Thickness(2, 0, 0, 0),
            BorderBrush = ThemeBrush("AccentFillColorDefaultBrush", 0x61, 0xAF, 0xEF),
            Padding = new Thickness(8, 1, 0, 1),
            Margin = new Thickness(0, 2, 0, 2),
            Child = inner,
        };
    }

    private void FillInlines(InlineCollection into, IReadOnlyList<MarkdownInline> content, double size, bool strike = false, bool linkify = true)
    {
        foreach (var inline in content)
        {
            switch (inline)
            {
                case MarkdownInline.Text text:
                {
                    var decorations = strike ? Windows.UI.Text.TextDecorations.Strikethrough : Windows.UI.Text.TextDecorations.None;
                    var at = 0;

                    // Bare URLs and paths the agent wrote outside backticks are clickable too.
                    foreach (var match in linkify ? LinkFinder.Find(text.Value) : [])
                    {
                        if (match.Index > at)
                        {
                            into.Add(new Run { Text = text.Value[at..match.Index], TextDecorations = decorations });
                        }

                        into.Add(MakeLink(match, match.Text, null));
                        at = match.Index + match.Length;
                    }

                    if (at < text.Value.Length)
                    {
                        into.Add(new Run { Text = text.Value[at..], TextDecorations = decorations });
                    }

                    break;
                }

                case MarkdownInline.Emphasis emphasis:
                {
                    var span = new Span();
                    if (emphasis.Strong)
                    {
                        span.FontWeight = FontWeights.Bold;
                        span.Foreground = ThemeBrush("MdStrongBrush", 0xFF, 0xFF, 0xFF);
                    }
                    else
                    {
                        span.FontStyle = Windows.UI.Text.FontStyle.Italic;
                    }

                    FillInlines(span.Inlines, emphasis.Content, size, strike, linkify);
                    into.Add(span);
                    break;
                }

                case MarkdownInline.Strike struck:
                    FillInlines(into, struck.Content, size, strike: true, linkify);
                    break;

                case MarkdownInline.CodeSpan code:
                {
                    var run = new Run
                    {
                        Text = code.Value,
                        FontFamily = CodeFont,
                        FontSize = size - 0.5,
                        TextDecorations = strike ? Windows.UI.Text.TextDecorations.Strikethrough : Windows.UI.Text.TextDecorations.None,
                    };

                    // `src/Foo.cs:42` is how agents name a place; make it a way there.
                    if (linkify && LinkFinder.AsFileReference(code.Value) is { } reference)
                    {
                        into.Add(MakeLink(reference, null, run));
                    }
                    else
                    {
                        run.Foreground = ThemeBrush("MdCodeBrush", 0xF2, 0xC0, 0x78);
                        into.Add(run);
                    }

                    break;
                }

                case MarkdownInline.Link link:
                {
                    var hyperlink = new Hyperlink();
                    FillInlines(hyperlink.Inlines, link.Content, size, strike, linkify: false);

                    if (Uri.TryCreate(link.Url, UriKind.Absolute, out var uri) && uri.Scheme is "http" or "https")
                    {
                        hyperlink.NavigateUri = uri;
                    }
                    else if (FileTarget(link.Url) is { } target)
                    {
                        hyperlink.Click += (_, _) => FileLinkClicked?.Invoke(this, target);
                    }

                    into.Add(hyperlink);
                    break;
                }

                case MarkdownInline.Image image:
                    // A table cell, list row or heading cannot host the Image control; the
                    // alt text stays as the link it came from instead of vanishing.
                    into.Add(ImageFallbackLink(image, size));
                    break;
            }
        }
    }

    /// <summary>
    /// Prose is a TextBlock, which cannot host the Image control an image inline needs,
    /// so a paragraph that mixes words and pictures splits at every picture. One that must
    /// not load (a remote URL) falls back into the prose as linked alt text instead.
    /// </summary>
    private void AddWithImages(UIElementCollection into, IReadOnlyList<MarkdownInline> content, double size, Func<IReadOnlyList<MarkdownInline>, TextBlock> prose)
    {
        var pending = new List<MarkdownInline>();

        void Flush()
        {
            if (pending.Count > 0)
            {
                into.Add(prose(pending));
                pending.Clear();
            }
        }

        foreach (var inline in content)
        {
            if (inline is MarkdownInline.Image image && ImageFrame(image, size, into) is { } frame)
            {
                Flush();
                into.Add(frame);
            }
            else
            {
                pending.Add(inline);
            }
        }

        Flush();
    }

    /// <summary>
    /// The Image for an ![alt](url) inline, or null when the URL should not load: remote
    /// images stay links, because agent-written markdown must not be able to make the
    /// transcript phone home. An image that fails to decode swaps to linked alt text.
    /// </summary>
    private Microsoft.UI.Xaml.Controls.Image? ImageFrame(MarkdownInline.Image image, double size, UIElementCollection into)
    {
        if (LocalImageSource(image.Url) is not { } source)
        {
            return null;
        }

        var frame = new Microsoft.UI.Xaml.Controls.Image
        {
            Stretch = Stretch.Uniform,
            HorizontalAlignment = HorizontalAlignment.Left,
            MaxHeight = 320,
            Margin = new Thickness(0, 2, 0, 4),
        };
        Microsoft.UI.Xaml.Automation.AutomationProperties.SetName(frame, InlineText(image.Content));

        if (source.Bytes is { } bytes)
        {
            _ = LoadDataImageAsync(frame, bytes, into, image, size);
        }
        else
        {
            frame.ImageFailed += (_, _) => ReplaceImage(into, frame, AltTextBlock(ImageFallbackLink(image, size), size));
            frame.Source = new BitmapImage(new Uri(source.Path));
        }

        return frame;
    }

    /// <summary>
    /// Where an image URL lands on disk — an absolute or document-relative path, a file:
    /// URI, or a data: URI's bytes — or null when it should not render as a picture.
    /// </summary>
    private (string Path, byte[]? Bytes)? LocalImageSource(string url)
    {
        if (url.StartsWith("data:", StringComparison.OrdinalIgnoreCase))
        {
            return DataImageBytes(url) is { } bytes ? (url, bytes) : null;
        }

        if (url.StartsWith("http:", StringComparison.OrdinalIgnoreCase) || url.StartsWith("https:", StringComparison.OrdinalIgnoreCase))
        {
            return null;
        }

        string path;
        if (url.StartsWith("file:", StringComparison.OrdinalIgnoreCase)
            && Uri.TryCreate(url, UriKind.Absolute, out var file))
        {
            path = file.LocalPath; // already unescaped
        }
        else
        {
            path = Uri.UnescapeDataString(url);
        }

        try
        {
            if (!System.IO.Path.IsPathRooted(path))
            {
                if (BasePath is not { } dir)
                {
                    return null;
                }

                path = System.IO.Path.GetFullPath(System.IO.Path.Combine(dir, path));
            }
        }
        catch (Exception)
        {
            return null;
        }

        return System.IO.File.Exists(path) ? (path, null) : null;
    }

    /// <summary>The bytes of a data:image/...;base64 URI, or null when malformed or oversized.</summary>
    private static byte[]? DataImageBytes(string url)
    {
        var comma = url.IndexOf(',');
        if (comma < 0
            || !url[..comma].EndsWith(";base64", StringComparison.OrdinalIgnoreCase)
            || url.Length - comma - 1 > MaxDataImageBase64)
        {
            return null;
        }

        try
        {
            return Convert.FromBase64String(url[(comma + 1)..]);
        }
        catch (FormatException)
        {
            return null;
        }
    }

    /// <summary>A data URI decodes from bytes rather than a file, so the bitmap fills from a
    /// stream once the frame is placed; a corrupt payload swaps in linked alt text.</summary>
    private async Task LoadDataImageAsync(Microsoft.UI.Xaml.Controls.Image frame, byte[] bytes, UIElementCollection into, MarkdownInline.Image image, double size)
    {
        try
        {
            var stream = new InMemoryRandomAccessStream();
            using (var writer = new DataWriter(stream))
            {
                writer.WriteBytes(bytes);
                await writer.StoreAsync();
                await writer.FlushAsync();
                writer.DetachStream();
            }

            stream.Seek(0);
            var bitmap = new BitmapImage();
            await bitmap.SetSourceAsync(stream);
            frame.Source = bitmap;
        }
        catch (Exception)
        {
            ReplaceImage(into, frame, AltTextBlock(ImageFallbackLink(image, size), size));
        }
    }

    /// <summary>A frame that could not draw gives its spot back — the view may have rebuilt
    /// while the bitmap decoded, in which case the stale frame is gone already.</summary>
    private static void ReplaceImage(UIElementCollection into, FrameworkElement frame, FrameworkElement fallback)
    {
        var at = into.IndexOf(frame);
        if (at < 0)
        {
            return;
        }

        into.RemoveAt(at);
        into.Insert(at, fallback);
    }

    /// <summary>Where an image cannot draw — a remote URL, a nested cell, a load failure —
    /// its alt text stays on screen as the link it came from.</summary>
    private Hyperlink ImageFallbackLink(MarkdownInline.Image image, double size)
    {
        var hyperlink = new Hyperlink();
        FillInlines(hyperlink.Inlines, image.Content, size, linkify: false);

        if (Uri.TryCreate(image.Url, UriKind.Absolute, out var uri) && uri.Scheme is "http" or "https")
        {
            hyperlink.NavigateUri = uri;
        }
        else if (FileTarget(image.Url) is { } target)
        {
            hyperlink.Click += (_, _) => FileLinkClicked?.Invoke(this, target);
        }

        return hyperlink;
    }

    /// <summary>A failed or unloaded image's alt line as prose, still carrying the link.</summary>
    private TextBlock AltTextBlock(Hyperlink link, double size)
    {
        var text = new TextBlock
        {
            FontSize = size,
            TextWrapping = TextWrapping.Wrap,
            IsTextSelectionEnabled = true,
            Foreground = ThemeBrush("MdBodyBrush", 0xD4, 0xD7, 0xDF),
        };
        text.Inlines.Add(link);
        return text;
    }

    /// <summary>An inline list as plain text — an image's alt line, for tools that read it.</summary>
    private static string InlineText(IReadOnlyList<MarkdownInline> content) => string.Concat(content.Select(TextOf));

    private static string TextOf(MarkdownInline inline) => inline switch
    {
        MarkdownInline.Text text => text.Value,
        MarkdownInline.CodeSpan code => code.Value,
        MarkdownInline.Emphasis emphasis => InlineText(emphasis.Content),
        MarkdownInline.Strike struck => InlineText(struck.Content),
        MarkdownInline.Link link => InlineText(link.Content),
        MarkdownInline.Image image => InlineText(image.Content),
        _ => "",
    };

    /// <summary>
    /// A file path or line in the transcript was clicked. Static because the view is
    /// built by a data template, far from the tab that knows how to open files; the
    /// tab filters by whether the sender sits inside it.
    /// </summary>
    public static event Action<MarkdownView, LinkMatch>? FileLinkClicked;

    /// <summary>A link for a match: a web URL navigates, a file reference asks the host to open it.</summary>
    private Hyperlink MakeLink(LinkMatch match, string? text, Run? styled)
    {
        var hyperlink = new Hyperlink();
        hyperlink.Inlines.Add(styled ?? new Run { Text = text ?? match.Text });

        if (match.Url is { } url && Uri.TryCreate(url, UriKind.Absolute, out var uri))
        {
            hyperlink.NavigateUri = uri;
        }
        else if (match.Path is not null)
        {
            hyperlink.Click += (_, _) => FileLinkClicked?.Invoke(this, match);
        }

        return hyperlink;
    }

    /// <summary>The file a markdown link points at: a relative or absolute path, a file: URI, or either with #L42.</summary>
    private static LinkMatch? FileTarget(string url)
    {
        var line = 0;
        var hash = url.LastIndexOf("#L", StringComparison.Ordinal);
        if (hash >= 0 && int.TryParse(url[(hash + 2)..].Split('-', 'C')[0], out line))
        {
            url = url[..hash];
        }

        if (url.StartsWith("file:", StringComparison.OrdinalIgnoreCase) && Uri.TryCreate(url, UriKind.Absolute, out var file))
        {
            url = file.LocalPath;
        }

        var reference = LinkFinder.AsFileReference(Uri.UnescapeDataString(url));
        return reference is { Path: not null }
            ? reference with { Line = reference.Line > 0 ? reference.Line : line }
            : null;
    }

    /// <summary>Theme brushes by name where the lookup reaches them, fixed colours
    /// where it does not - the fallbacks read on dark and light alike.</summary>
    private static Brush ThemeBrush(string key, byte r, byte g, byte b)
    {
        if (Application.Current.Resources.TryGetValue(key, out var value) && value is Brush brush)
        {
            return brush;
        }

        return new SolidColorBrush(Color.FromArgb(255, r, g, b));
    }
}
