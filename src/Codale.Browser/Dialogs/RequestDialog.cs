using System.Globalization;
using System.Runtime.InteropServices;
using System.Runtime.InteropServices.WindowsRuntime;
using System.Text;
using System.Text.Json;

using Microsoft.UI.Text;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Media;
using Microsoft.UI.Xaml.Media.Imaging;
using Microsoft.Web.WebView2.Core;

using Windows.ApplicationModel.DataTransfer;
using Windows.Storage.Streams;

namespace Codale.Browser;

/// <summary>
/// The details of one network request, opened by clicking its row: the general facts and both header sets, the
/// payload (query string and request body), and the response body, fetched from the page's DevTools session on open.
/// </summary>
internal static class RequestDialog
{
    private static readonly FontFamily Mono = new("Cascadia Mono, Consolas");

    public static async Task ShowAsync(XamlRoot root, NetworkRequest request, CoreWebView2? core)
    {
        var width = Math.Clamp(root.Size.Width - 120, 420, 1040);
        var height = Math.Clamp(root.Size.Height - 260, 240, 640);

        // The response and request bodies load while the dialog opens; each view awaits the one it shows.
        var response = LoadResponseAsync(request, core);
        var payload = LoadPostDataAsync(request, core);

        var host = new ContentPresenter { HorizontalContentAlignment = HorizontalAlignment.Stretch, VerticalContentAlignment = VerticalAlignment.Stretch };
        var tabs = new SelectorBar();
        var headersItem = new SelectorBarItem { Text = "Headers", IsSelected = true };
        var payloadItem = new SelectorBarItem { Text = "Payload" };
        var responseItem = new SelectorBarItem { Text = "Response" };
        tabs.Items.Add(headersItem);
        var hasQuery = Uri.TryCreate(request.Url, UriKind.Absolute, out var address) && address.Query.Length > 1;
        if (request.HasPostData || request.PostData is not null || hasQuery) tabs.Items.Add(payloadItem);
        tabs.Items.Add(responseItem);

        var views = new Dictionary<SelectorBarItem, UIElement>();
        void Show(SelectorBarItem item)
        {
            if (!views.TryGetValue(item, out var view))
            {
                view = item == payloadItem ? PayloadView(request, payload)
                    : item == responseItem ? ResponseView(request, response)
                    : HeadersView(request);
                views[item] = view;
            }

            host.Content = view;
        }

        tabs.SelectionChanged += (_, _) =>
        {
            if (tabs.SelectedItem is { } item) Show(item);
        };
        Show(headersItem);

        var content = new Grid { Width = width, Height = height, RowSpacing = 8 };
        content.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
        content.RowDefinitions.Add(new RowDefinition { Height = new GridLength(1, GridUnitType.Star) });
        content.Children.Add(tabs);
        Grid.SetRow(host, 1);
        content.Children.Add(host);

        var dialog = new ContentDialog
        {
            Title = TitleView(request),
            Content = content,
            PrimaryButtonText = "Copy URL",
            SecondaryButtonText = "Copy as cURL",
            CloseButtonText = "Close",
            DefaultButton = ContentDialogButton.Close,
        };
        if (Application.Current.Resources.TryGetValue("DefaultContentDialogStyle", out var style) && style is Style dialogStyle) dialog.Style = dialogStyle;
        dialog.Resources["ContentDialogMaxWidth"] = width + 80;
        dialog.Resources["ContentDialogMaxHeight"] = height + 260;

        // The copy buttons keep the dialog open and say "Copied" for a moment.
        dialog.PrimaryButtonClick += (_, e) =>
        {
            e.Cancel = true;
            Copy(request.Url);
            Flash(dialog, primary: true);
        };
        dialog.SecondaryButtonClick += async (_, e) =>
        {
            e.Cancel = true;
            var deferral = e.GetDeferral();
            try
            {
                Copy(BodyFormat.Curl(request, (await payload).Text));
            }
            finally
            {
                deferral.Complete();
            }

            Flash(dialog, primary: false);
        };

        await Prompts.ShowAsync(dialog, root);
    }

    private static void Flash(ContentDialog dialog, bool primary)
    {
        if (primary) dialog.PrimaryButtonText = "Copied ✓";
        else dialog.SecondaryButtonText = "Copied ✓";
        var timer = dialog.DispatcherQueue.CreateTimer();
        timer.Interval = TimeSpan.FromSeconds(1.4);
        timer.IsRepeating = false;
        timer.Tick += (_, _) =>
        {
            if (primary) dialog.PrimaryButtonText = "Copy URL";
            else dialog.SecondaryButtonText = "Copy as cURL";
        };
        timer.Start();
    }

    // ---- Title -------------------------------------------------------------------------

    private static UIElement TitleView(NetworkRequest request)
    {
        var line = new StackPanel { Orientation = Orientation.Horizontal, Spacing = 10 };
        line.Children.Add(Pill(request.Method, request.MethodBrush));
        line.Children.Add(Pill(request.StatusLabel, request.StatusBrush));
        line.Children.Add(new TextBlock
        {
            Text = request.Name.Length > 0 ? request.Name : request.Url,
            VerticalAlignment = VerticalAlignment.Center,
            TextTrimming = TextTrimming.CharacterEllipsis,
            MaxWidth = 640,
        });

        var panel = new StackPanel { Spacing = 6 };
        panel.Children.Add(line);
        panel.Children.Add(new TextBlock
        {
            Text = request.Url,
            FontSize = 12,
            FontFamily = Mono,
            Opacity = 0.65,
            TextWrapping = TextWrapping.Wrap,
            MaxLines = 2,
            TextTrimming = TextTrimming.CharacterEllipsis,
            IsTextSelectionEnabled = true,
        });
        return panel;
    }

    private static Border Pill(string text, Brush foreground) => new()
    {
        CornerRadius = new CornerRadius(4),
        Padding = new Thickness(7, 1, 7, 2),
        BorderThickness = new Thickness(1),
        BorderBrush = foreground,
        VerticalAlignment = VerticalAlignment.Center,
        Child = new TextBlock { Text = text, FontSize = 13, FontWeight = FontWeights.SemiBold, Foreground = foreground, FontFamily = Mono },
    };

    // ---- Headers -------------------------------------------------------------------

    private static UIElement HeadersView(NetworkRequest request)
    {
        var general = new List<(string, string, Brush?)>
        {
            ("Request URL", request.Url, null),
            ("Request method", request.Method, request.MethodBrush),
            ("Status code", request.Error is { } error ? $"{request.StatusLabel}  {error}" : request.Status is { } code ? $"{code} {request.StatusText}".Trim() : "(pending)", request.StatusBrush),
        };
        void Add(string name, string? value)
        {
            if (!string.IsNullOrEmpty(value)) general.Add((name, value, null));
        }

        Add("Remote address", request.RemoteAddress);
        Add("Type", request.TypeLabel);
        Add("Content type", request.MimeType);
        Add("Protocol", request.Protocol);
        Add("Initiator", request.Initiator);
        Add("Started", request.Started.ToString("yyyy-MM-dd HH:mm:ss.fff", CultureInfo.InvariantCulture));
        Add("Duration", request.DurationMs is { } ms ? NetworkRequest.FormatDuration(ms) : null);
        Add("Transferred", request.TransferSize is { } size ? $"{NetworkRequest.FormatSize(size)} ({size:N0} bytes)" : null);
        Add("Served from", request.CacheSource);

        var stack = new StackPanel { Spacing = 18, Padding = new Thickness(0, 4, 16, 12) };
        stack.Children.Add(Section("General", general));
        stack.Children.Add(Section("Response headers", request.ResponseHeaders.Select(h => (h.Key, h.Value, (Brush?)null)).ToList(),
            request.IsFinished || request.Status is not null ? "No response headers." : "Waiting for the response."));
        stack.Children.Add(Section("Request headers", request.RequestHeaders.Select(h => (h.Key, h.Value, (Brush?)null)).ToList(), "No request headers were recorded."));
        return Scroll(stack);
    }

    /// <summary>A titled list of name/value rows; names in a fixed column, values wrapping and selectable.</summary>
    private static UIElement Section(string title, List<(string Name, string Value, Brush? Foreground)> rows, string empty = "")
    {
        var panel = new StackPanel { Spacing = 6 };
        var header = new StackPanel { Orientation = Orientation.Horizontal, Spacing = 8 };
        header.Children.Add(new TextBlock { Text = title, FontWeight = FontWeights.SemiBold, FontSize = 14 });
        if (title != "General") header.Children.Add(new TextBlock { Text = rows.Count.ToString(CultureInfo.InvariantCulture), Opacity = 0.5, VerticalAlignment = VerticalAlignment.Center, FontSize = 12 });
        panel.Children.Add(header);

        if (rows.Count == 0)
        {
            panel.Children.Add(new TextBlock { Text = empty, Opacity = 0.6, FontSize = 12 });
            return panel;
        }

        var grid = new Grid { ColumnSpacing = 16, RowSpacing = 3 };
        grid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(220) });
        grid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
        for (var i = 0; i < rows.Count; i++)
        {
            grid.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
            var name = new TextBlock
            {
                Text = rows[i].Name,
                FontSize = 12,
                Opacity = 0.7,
                FontFamily = Mono,
                TextWrapping = TextWrapping.Wrap,
                IsTextSelectionEnabled = true,
            };
            var value = new TextBlock
            {
                Text = rows[i].Value,
                FontSize = 12,
                FontFamily = Mono,
                TextWrapping = TextWrapping.Wrap,
                IsTextSelectionEnabled = true,
            };
            if (rows[i].Foreground is { } brush)
            {
                value.Foreground = brush;
                value.FontWeight = FontWeights.SemiBold;
            }

            Grid.SetRow(name, i);
            Grid.SetRow(value, i);
            Grid.SetColumn(value, 1);
            grid.Children.Add(name);
            grid.Children.Add(value);
        }

        panel.Children.Add(grid);
        return panel;
    }

    // ---- Payload -----------------------------------------------------------------------

    private static UIElement PayloadView(NetworkRequest request, Task<Body> payload)
    {
        var stack = new StackPanel { Spacing = 18, Padding = new Thickness(0, 4, 16, 12) };
        if (Uri.TryCreate(request.Url, UriKind.Absolute, out var uri) && uri.Query.Length > 1)
        {
            stack.Children.Add(Section("Query string parameters", BodyFormat.FormPairs(uri.Query).Select(p => (p.Key, p.Value, (Brush?)null)).ToList()));
        }

        var bodyHost = new StackPanel { Spacing = 6 };
        stack.Children.Add(bodyHost);
        if (request.HasPostData || request.PostData is not null)
        {
            bodyHost.Children.Add(Loading("Loading the request body…"));
            _ = FillAsync();
        }

        return Scroll(stack);

        async Task FillAsync()
        {
            var body = await payload;
            bodyHost.Children.Clear();
            var contentType = BodyFormat.Header(request.RequestHeaders, "content-type");
            if (body.Text is not { } text)
            {
                bodyHost.Children.Add(Note(body.Error ?? "The request body is not available."));
                return;
            }

            if (contentType.Contains("x-www-form-urlencoded", StringComparison.OrdinalIgnoreCase))
            {
                bodyHost.Children.Add(Section("Form data", BodyFormat.FormPairs(text).Select(p => (p.Key, p.Value, (Brush?)null)).ToList()));
                return;
            }

            bodyHost.Children.Add(SectionTitle("Request body", contentType, text));
            bodyHost.Children.Add(CodeBlock(text, BodyFormat.IsJsonMime(contentType) || contentType.Length == 0, scroll: false));
        }
    }

    // ---- Response ------------------------------------------------------------------

    private static UIElement ResponseView(NetworkRequest request, Task<Body> response)
    {
        var host = new Grid();
        host.Children.Add(Loading("Loading the response…"));
        _ = FillAsync();
        return host;

        async Task FillAsync()
        {
            var body = await response;
            host.Children.Clear();
            if (body.Error is { } error)
            {
                host.Children.Add(Center(Note(error)));
                return;
            }

            var mime = request.MimeType;
            var layout = new Grid { RowSpacing = 8 };
            layout.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
            layout.RowDefinitions.Add(new RowDefinition { Height = new GridLength(1, GridUnitType.Star) });
            host.Children.Add(layout);

            if (body.Bytes is { } bytes && BodyFormat.IsImageMime(mime))
            {
                var image = new Image { Stretch = Stretch.Uniform, HorizontalAlignment = HorizontalAlignment.Center, VerticalAlignment = VerticalAlignment.Center };
                var caption = Toolbar($"{mime} · {NetworkRequest.FormatSize(bytes.Length)}", null);
                layout.Children.Add(caption);
                var frame = new Border
                {
                    Child = image,
                    Padding = new Thickness(12),
                    CornerRadius = new CornerRadius(6),
                    Background = (Brush)Application.Current.Resources["CardBackgroundFillColorDefaultBrush"],
                };
                Grid.SetRow(frame, 1);
                layout.Children.Add(frame);
                try
                {
                    var bitmap = new BitmapImage();
                    using var stream = new InMemoryRandomAccessStream();
                    await stream.WriteAsync(bytes.AsBuffer());
                    stream.Seek(0);
                    await bitmap.SetSourceAsync(stream);
                    image.Source = bitmap;
                    image.MaxWidth = bitmap.PixelWidth;
                    image.MaxHeight = bitmap.PixelHeight;
                    ((TextBlock)((Grid)caption).Children[0]).Text = $"{bitmap.PixelWidth} × {bitmap.PixelHeight} · {mime} · {NetworkRequest.FormatSize(bytes.Length)}";
                }
                catch (Exception ex) when (ex is COMException or ArgumentException)
                {
                    frame.Child = Note("This image could not be decoded.");
                }

                return;
            }

            var text = body.Text;
            if (text is null && body.Bytes is { } raw)
            {
                if (BodyFormat.IsTextMime(mime)) text = Encoding.UTF8.GetString(raw);
                else
                {
                    layout.Children.Add(Toolbar($"Binary data · {mime} · {NetworkRequest.FormatSize(raw.Length)}", null));
                    var dump = CodeBlock(HexDump(raw, 4096), json: false, scroll: true);
                    Grid.SetRow(dump, 1);
                    layout.Children.Add(dump);
                    return;
                }
            }

            text ??= "";
            if (text.Length == 0)
            {
                host.Children.Clear();
                host.Children.Add(Center(Note("This response has no body.")));
                return;
            }

            var json = BodyFormat.IsJsonMime(mime) || mime.Length == 0 || mime.StartsWith("text/plain", StringComparison.OrdinalIgnoreCase);
            layout.Children.Add(Toolbar($"{(mime.Length > 0 ? mime : "unknown type")} · {text.Length:N0} characters", text));
            var code = CodeBlock(text, json, scroll: true);
            Grid.SetRow(code, 1);
            layout.Children.Add(code);
        }
    }

    // ---- Shared pieces -------------------------------------------------------------

    private static Grid Toolbar(string caption, string? copyText)
    {
        var bar = new Grid { ColumnSpacing = 8 };
        bar.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
        bar.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
        bar.Children.Add(new TextBlock { Text = caption, Opacity = 0.65, FontSize = 12, VerticalAlignment = VerticalAlignment.Center, TextTrimming = TextTrimming.CharacterEllipsis });
        if (copyText is not null)
        {
            var copy = CopyButton("Copy response", copyText);
            Grid.SetColumn(copy, 1);
            bar.Children.Add(copy);
        }

        return bar;
    }

    private static UIElement SectionTitle(string title, string detail, string copyText)
    {
        var bar = new Grid { ColumnSpacing = 8 };
        bar.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
        bar.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
        bar.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
        bar.Children.Add(new TextBlock { Text = title, FontWeight = FontWeights.SemiBold, FontSize = 14, VerticalAlignment = VerticalAlignment.Center });
        var info = new TextBlock { Text = detail, Opacity = 0.5, FontSize = 12, VerticalAlignment = VerticalAlignment.Center, TextTrimming = TextTrimming.CharacterEllipsis };
        Grid.SetColumn(info, 1);
        bar.Children.Add(info);
        var copy = CopyButton("Copy", copyText);
        Grid.SetColumn(copy, 2);
        bar.Children.Add(copy);
        return bar;
    }

    private static Button CopyButton(string label, string text)
    {
        var content = new StackPanel { Orientation = Orientation.Horizontal, Spacing = 6 };
        content.Children.Add(new FontIcon { Glyph = "", FontSize = 12 });
        var caption = new TextBlock { Text = label, FontSize = 12 };
        content.Children.Add(caption);
        var button = new Button { Content = content, Padding = new Thickness(10, 3, 10, 4), MinHeight = 0 };
        button.Click += (_, _) =>
        {
            Copy(text);
            caption.Text = "Copied ✓";
            var timer = button.DispatcherQueue.CreateTimer();
            timer.Interval = TimeSpan.FromSeconds(1.4);
            timer.IsRepeating = false;
            timer.Tick += (_, _) => caption.Text = label;
            timer.Start();
        };
        return button;
    }

    /// <summary>A body as monospaced text: JSON pretty-printed and coloured when it parses, the rest as sent. Very long text is cut.</summary>
    private static FrameworkElement CodeBlock(string text, bool json, bool scroll)
    {
        var block = new TextBlock
        {
            FontFamily = Mono,
            FontSize = 12,
            TextWrapping = TextWrapping.Wrap,
            IsTextSelectionEnabled = true,
        };

        var pretty = json ? BodyFormat.Json(text) : null;
        if (pretty is not null)
        {
            var length = pretty.Sum(s => s.Text.Length);
            // Colouring makes a run per token; past ~120k characters that gets slow, so large JSON is shown pretty but plain.
            if (length <= 120_000) RichText.Fill(block, pretty);
            else block.Text = Truncate(ConsoleFormat.PlainText(pretty));
        }
        else
        {
            block.Text = Truncate(text);
        }

        var frame = new Border
        {
            Child = block,
            Padding = new Thickness(12, 10, 12, 10),
            CornerRadius = new CornerRadius(6),
            Background = (Brush)Application.Current.Resources["CardBackgroundFillColorDefaultBrush"],
            BorderBrush = (Brush)Application.Current.Resources["CardStrokeColorDefaultBrush"],
            BorderThickness = new Thickness(1),
        };
        return scroll ? Scroll(frame) : frame;
    }

    private static string Truncate(string text) =>
        text.Length <= BodyFormat.DisplayLimit
            ? text
            : text[..BodyFormat.DisplayLimit] + $"\n\n… {text.Length - BodyFormat.DisplayLimit:N0} more characters (Copy takes the whole body)";

    private static string HexDump(byte[] bytes, int limit)
    {
        var output = new StringBuilder();
        var count = Math.Min(bytes.Length, limit);
        for (var offset = 0; offset < count; offset += 16)
        {
            output.Append(offset.ToString("x8", CultureInfo.InvariantCulture)).Append("  ");
            for (var i = 0; i < 16; i++)
            {
                output.Append(offset + i < count ? bytes[offset + i].ToString("x2", CultureInfo.InvariantCulture) + " " : "   ");
                if (i == 7) output.Append(' ');
            }

            output.Append(' ');
            for (var i = 0; i < 16 && offset + i < count; i++)
            {
                var b = bytes[offset + i];
                output.Append(b is >= 32 and < 127 ? (char)b : '.');
            }

            output.Append('\n');
        }

        if (bytes.Length > limit) output.Append($"… {bytes.Length - limit:N0} more bytes");
        return output.ToString();
    }

    private static ScrollViewer Scroll(UIElement content) => new()
    {
        Content = content,
        HorizontalScrollBarVisibility = ScrollBarVisibility.Disabled,
        VerticalScrollBarVisibility = ScrollBarVisibility.Auto,
    };

    private static UIElement Loading(string text)
    {
        var panel = new StackPanel { Orientation = Orientation.Horizontal, Spacing = 10, Margin = new Thickness(0, 8, 0, 0) };
        panel.Children.Add(new ProgressRing { IsActive = true, Width = 16, Height = 16 });
        panel.Children.Add(new TextBlock { Text = text, Opacity = 0.7, VerticalAlignment = VerticalAlignment.Center });
        return panel;
    }

    private static TextBlock Note(string text) => new() { Text = text, Opacity = 0.7, TextWrapping = TextWrapping.Wrap };

    private static UIElement Center(UIElement child)
    {
        if (child is FrameworkElement element)
        {
            element.HorizontalAlignment = HorizontalAlignment.Center;
            element.VerticalAlignment = VerticalAlignment.Center;
        }

        return child;
    }

    private static void Copy(string text)
    {
        var data = new DataPackage();
        data.SetText(text);
        Clipboard.SetContent(data);
        Clipboard.Flush();
    }

    // ---- Loading bodies over the DevTools protocol ---------------------------------

    private sealed record Body(string? Text, byte[]? Bytes, string? Error);

    private static async Task<Body> LoadResponseAsync(NetworkRequest request, CoreWebView2? core)
    {
        if (request.Type == "WebSocket") return new Body(null, null, "WebSocket messages are not recorded.");
        if (request.IsRedirect) return new Body(null, null, $"This response redirected the request ({request.Status}); a redirect has no body to show.");
        if (request.IsFailed && request.Status is null) return new Body(null, null, $"The request failed ({request.Error}), so there is no response.");
        if (!request.IsFinished) return new Body(null, null, "The response has not finished loading. Close this and open the row again once it has.");
        if (core is null) return new Body(null, null, "The tab this request belongs to is closed.");

        try
        {
            var result = await core.CallDevToolsProtocolMethodAsync("Network.getResponseBody", JsonSerializer.Serialize(new { requestId = request.Id }));
            using var document = JsonDocument.Parse(result);
            var body = ConsoleFormat.Str(document.RootElement, "body");
            var encoded = document.RootElement.TryGetProperty("base64Encoded", out var flag) && flag.ValueKind == JsonValueKind.True;
            return encoded ? new Body(null, Convert.FromBase64String(body), null) : new Body(body, null, null);
        }
        catch (Exception ex) when (ex is COMException or JsonException or FormatException or InvalidOperationException or ArgumentException)
        {
            return new Body(null, null, "The response body is no longer available. The browser drops bodies after a navigation or when its buffer fills; reload the page and open the request again.");
        }
    }

    private static async Task<Body> LoadPostDataAsync(NetworkRequest request, CoreWebView2? core)
    {
        if (request.PostData is { } known) return new Body(known, null, null);
        if (!request.HasPostData) return new Body(null, null, null);
        if (core is null) return new Body(null, null, "The tab this request belongs to is closed.");
        try
        {
            var result = await core.CallDevToolsProtocolMethodAsync("Network.getRequestPostData", JsonSerializer.Serialize(new { requestId = request.Id }));
            using var document = JsonDocument.Parse(result);
            var text = ConsoleFormat.Str(document.RootElement, "postData");
            request.PostData = text;
            return new Body(text, null, null);
        }
        catch (Exception ex) when (ex is COMException or JsonException or InvalidOperationException or ArgumentException)
        {
            return new Body(null, null, "The request body is not available (it may be a file upload or a stream).");
        }
    }
}
