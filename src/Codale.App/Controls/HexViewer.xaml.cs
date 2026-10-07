using System.Buffers.Binary;
using System.Globalization;
using System.Text;

using Codale.App.Services;

using Microsoft.UI.Input;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Input;

using Windows.ApplicationModel.DataTransfer;
using Windows.System;
using Windows.UI.Core;

namespace Codale.App.Controls;

/// <summary>
/// Read-only hex viewer for binary files: a virtualized offset / hex / text dump with byte and
/// range selection (click, shift-click, drag, keyboard), go-to-offset, hex / text / UTF-16 search in
/// both directions, a data inspector that reads the selection as numbers and times, and several
/// copy formats. Nothing is loaded up front, so file size does not matter.
/// </summary>
public sealed partial class HexViewer : UserControl
{
    private const double RowHeight = 20;
    private const int MaxCopyBytes = 16 * 1024 * 1024;

    private HexSource? _source;
    private HexRowList? _rows;
    private string? _path;
    private int _perRow = 16;

    /// <summary>The fixed end of the selection and the end that moves; -1 when nothing is selected.</summary>
    private long _anchor = -1;
    private long _caret = -1;

    private CancellationTokenSource? _findCts;
    private bool _dragging;
    private HexRow? _dragRow;

    public HexViewer()
    {
        InitializeComponent();
        MeasureCharWidth();
        BuildInspectorRows();
    }

    private long SelStart => Math.Min(_anchor, _caret);

    private long SelEnd => Math.Max(_anchor, _caret);

    private bool HasSelection => _anchor >= 0;

    /// <summary>Shows <paramref name="path"/>; the previous file's handle and selection are dropped.</summary>
    public void Open(string path)
    {
        Close();

        try
        {
            _source = new HexSource(path);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            CrashLog.Error("hex", $"open failed: {path}", ex);
            SizeText.Text = ex.Message;
            return;
        }

        _path = path;
        SizeText.Text = FormatSize(_source.Length);
        TypeText.Text = HexSignatures.Detect(_source.Read(0, 16)) is { } kind ? kind : "";
        FindStatus.Text = "";
        Rebuild();

        if (_source.Length > 0)
        {
            Select(0, 0, scroll: false);
        }
        else
        {
            RefreshSelectionUi();
        }
    }

    /// <summary>Releases the file; called when the tab shows something else or closes.</summary>
    public void Close()
    {
        _findCts?.Cancel();
        _findCts = null;
        Rows.ItemsSource = null;
        _rows = null;
        _source?.Dispose();
        _source = null;
        _path = null;
        _anchor = _caret = -1;
        _dragging = false;
        SizeText.Text = "";
        SelectionText.Text = "";
        TypeText.Text = "";
        FindStatus.Text = "";
        FindProgress.IsActive = false;
        RefreshInspector();
    }

    private void Rebuild()
    {
        if (_source is null)
        {
            return;
        }

        _rows = new HexRowList(_source, _perRow);
        if (HasSelection)
        {
            _rows.SetSelection(SelStart, SelEnd);
        }

        Rows.ItemsSource = _rows;
        RulerHex.Text = BuildRuler();
    }

    private string BuildRuler()
    {
        var sb = new StringBuilder();
        for (var i = 0; i < _perRow; i++)
        {
            sb.Append(i.ToString("X2")).Append(' ');
            if (i % HexLayout.GroupSize == HexLayout.GroupSize - 1 && i < _perRow - 1)
            {
                sb.Append(' ');
            }
        }

        return sb.ToString();
    }

    /// <summary>All geometry is character arithmetic, so the font's real width is measured once.</summary>
    private void MeasureCharWidth()
    {
        var probe = new TextBlock
        {
            FontFamily = new Microsoft.UI.Xaml.Media.FontFamily("Cascadia Mono, Consolas"),
            FontSize = 13,
            Text = new string('0', 40),
        };
        probe.Measure(new Windows.Foundation.Size(double.PositiveInfinity, double.PositiveInfinity));
        HexLayout.CharWidth = probe.DesiredSize.Width / 40;
    }

    // ---- selection ----------------------------------------------------------------------

    private void Select(long anchor, long caret, bool scroll)
    {
        if (_source is null || _source.Length == 0)
        {
            return;
        }

        var max = _source.Length - 1;
        _anchor = Math.Clamp(anchor, 0, max);
        _caret = Math.Clamp(caret, 0, max);
        _rows?.SetSelection(SelStart, SelEnd);
        RefreshSelectionUi();

        if (scroll)
        {
            ScrollTo(_caret, center: false);
        }
    }

    private void ScrollTo(long offset, bool center)
    {
        if (_rows is null)
        {
            return;
        }

        var index = (int)(offset / _perRow);
        if (center)
        {
            Rows.ScrollIntoView(_rows[Math.Max(0, index - 4)], ScrollIntoViewAlignment.Leading);
        }

        Rows.ScrollIntoView(_rows[index]);
    }

    private void RefreshSelectionUi()
    {
        if (!HasSelection)
        {
            SelectionText.Text = "";
        }
        else
        {
            var length = SelEnd - SelStart + 1;
            SelectionText.Text = length == 1
                ? $"Offset 0x{SelStart:X} ({SelStart:N0})"
                : $"0x{SelStart:X} – 0x{SelEnd:X} · {length:N0} bytes";
        }

        RefreshInspector();
    }

    // ---- pointer ------------------------------------------------------------------------

    private void OnHexPointerPressed(object sender, PointerRoutedEventArgs e) => BeginPointerSelection(sender, e, ascii: false);

    private void OnAsciiPointerPressed(object sender, PointerRoutedEventArgs e) => BeginPointerSelection(sender, e, ascii: true);

    private void OnHexPointerMoved(object sender, PointerRoutedEventArgs e) => ContinuePointerSelection(sender, e, ascii: false);

    private void OnAsciiPointerMoved(object sender, PointerRoutedEventArgs e) => ContinuePointerSelection(sender, e, ascii: true);

    private void OnPointerCaptureLost(object sender, PointerRoutedEventArgs e) => _dragging = false;

    private void OnPointerReleased(object sender, PointerRoutedEventArgs e)
    {
        _dragging = false;
        ((UIElement)sender).ReleasePointerCapture(e.Pointer);
    }

    private void BeginPointerSelection(object sender, PointerRoutedEventArgs e, bool ascii)
    {
        var cell = (FrameworkElement)sender;
        if (cell.DataContext is not HexRow row || !e.GetCurrentPoint(cell).Properties.IsLeftButtonPressed)
        {
            return;
        }

        Focus(FocusState.Pointer);
        var offset = row.Offset + ByteInRow(e.GetCurrentPoint(cell).Position.X, ascii, row);

        if (IsKeyDown(VirtualKey.Shift) && HasSelection)
        {
            Select(_anchor, offset, scroll: false);
        }
        else
        {
            Select(offset, offset, scroll: false);
        }

        // Capture, so a drag keeps reporting to this cell even after the pointer leaves the row.
        _dragging = true;
        _dragRow = row;
        cell.CapturePointer(e.Pointer);
        e.Handled = true;
    }

    private void ContinuePointerSelection(object sender, PointerRoutedEventArgs e, bool ascii)
    {
        if (!_dragging || _dragRow is not { } start || _source is null)
        {
            return;
        }

        var position = e.GetCurrentPoint((FrameworkElement)sender).Position;
        var rowDelta = (long)Math.Floor(position.Y / RowHeight);
        var rowOffset = start.Offset + rowDelta * _perRow;
        var column = ByteInRow(position.X, ascii, start);
        var offset = Math.Clamp(rowOffset + column, 0, _source.Length - 1);

        if (offset != _caret)
        {
            Select(_anchor, offset, scroll: true);
        }
    }

    /// <summary>The byte column under an x coordinate inside the hex or text cell.</summary>
    private int ByteInRow(double x, bool ascii, HexRow row)
    {
        var column = (int)Math.Floor(Math.Max(0, x) / HexLayout.CharWidth);
        return ascii
            ? Math.Clamp(column, 0, _perRow - 1)
            : HexLayout.ByteAtHexColumn(column, _perRow);
    }

    // ---- keyboard -----------------------------------------------------------------------

    private static bool IsKeyDown(VirtualKey key) =>
        InputKeyboardSource.GetKeyStateForCurrentThread(key).HasFlag(CoreVirtualKeyStates.Down);

    private void OnViewerKeyDown(object sender, KeyRoutedEventArgs e)
    {
        if (_source is null || e.OriginalSource is TextBox or ComboBox or ComboBoxItem)
        {
            return;
        }

        var ctrl = IsKeyDown(VirtualKey.Control);
        var shift = IsKeyDown(VirtualKey.Shift);
        var page = _perRow * Math.Max(1, (int)(Rows.ActualHeight / RowHeight) - 1);
        long? target = e.Key switch
        {
            VirtualKey.Left => _caret - 1,
            VirtualKey.Right => _caret + 1,
            VirtualKey.Up => _caret - _perRow,
            VirtualKey.Down => _caret + _perRow,
            VirtualKey.PageUp => _caret - page,
            VirtualKey.PageDown => _caret + page,
            VirtualKey.Home => ctrl ? 0 : _caret - _caret % _perRow,
            VirtualKey.End => ctrl ? _source.Length - 1 : _caret - _caret % _perRow + _perRow - 1,
            _ => null,
        };

        if (target is { } next && HasSelection)
        {
            Select(shift ? _anchor : next, next, scroll: true);
            e.Handled = true;
            return;
        }

        switch (e.Key)
        {
            case VirtualKey.A when ctrl && _source.Length > 0:
                Select(0, _source.Length - 1, scroll: false);
                break;
            case VirtualKey.C when ctrl:
                CopySelection(CopyFormat.Hex);
                break;
            case VirtualKey.G when ctrl:
                GoToBox.Focus(FocusState.Keyboard);
                GoToBox.SelectAll();
                break;
            case VirtualKey.F when ctrl:
                FindBox.Focus(FocusState.Keyboard);
                FindBox.SelectAll();
                break;
            case VirtualKey.F3:
                _ = FindAsync(forward: !shift);
                break;
            default:
                return;
        }

        e.Handled = true;
    }

    // ---- go to ---------------------------------------------------------------------------

    private void OnGoToClick(object sender, RoutedEventArgs e) => GoTo();

    private void OnGoToKeyDown(object sender, KeyRoutedEventArgs e)
    {
        if (e.Key == VirtualKey.Enter)
        {
            GoTo();
            e.Handled = true;
        }
    }

    private void GoTo()
    {
        if (_source is null || _source.Length == 0)
        {
            return;
        }

        if (!TryParseOffset(GoToBox.Text, out var offset, out var relative))
        {
            FindStatus.Text = "Not an offset. Use 0x1F0, 1F0h or 496; prefix + or - to move from the selection.";
            return;
        }

        var target = relative ? (HasSelection ? SelStart : 0) + offset : offset;
        target = Math.Clamp(target, 0, _source.Length - 1);
        FindStatus.Text = "";
        Select(target, target, scroll: false);
        ScrollTo(target, center: true);
        Focus(FocusState.Programmatic);
    }

    private static bool TryParseOffset(string text, out long value, out bool relative)
    {
        value = 0;
        text = text.Trim().Replace("_", "").Replace(",", "");
        relative = text.StartsWith('+') || text.StartsWith('-');
        var sign = 1L;

        if (relative)
        {
            sign = text[0] == '-' ? -1 : 1;
            text = text[1..].Trim();
        }

        var hex = text.StartsWith("0x", StringComparison.OrdinalIgnoreCase);
        if (hex)
        {
            text = text[2..];
        }
        else if (text.EndsWith('h') || text.EndsWith('H'))
        {
            hex = true;
            text = text[..^1];
        }
        else if (text.Any(char.IsAsciiHexDigitLower) || text.Any(char.IsAsciiHexDigitUpper))
        {
            hex = text.All(char.IsAsciiHexDigit); // "1F0" with no prefix: only letters can mean hex
        }

        var ok = long.TryParse(text, hex ? NumberStyles.AllowHexSpecifier : NumberStyles.None, CultureInfo.InvariantCulture, out var parsed);
        value = parsed * sign;
        return ok;
    }

    // ---- find ----------------------------------------------------------------------------

    private void OnFindKeyDown(object sender, KeyRoutedEventArgs e)
    {
        if (e.Key == VirtualKey.Enter)
        {
            _ = FindAsync(forward: !IsKeyDown(VirtualKey.Shift));
            e.Handled = true;
        }
    }

    private void OnFindNextClick(object sender, RoutedEventArgs e) => _ = FindAsync(forward: true);

    private void OnFindPreviousClick(object sender, RoutedEventArgs e) => _ = FindAsync(forward: false);

    private void OnFindModeChanged(object sender, SelectionChangedEventArgs e)
    {
        if (MatchCase is null)
        {
            return; // fired while the XAML is still loading
        }

        MatchCase.IsEnabled = FindMode.SelectedIndex != 0;
        FindBox.PlaceholderText = FindMode.SelectedIndex == 0 ? "Find  DE AD BE EF" : "Find text";

        // A new mode invalidates the old status; re-run the search so the switch visibly does something.
        FindStatus.Text = "";
        if (_source is not null && FindBox.Text.Length > 0)
        {
            _ = FindAsync(forward: true, includeSelection: true);
        }
    }

    private byte[]? BuildPattern()
    {
        var text = FindBox.Text;
        if (text.Length == 0)
        {
            return null;
        }

        switch (FindMode.SelectedIndex)
        {
            case 0:
                var bytes = HexSearch.ParseHex(text);
                if (bytes is null)
                {
                    FindStatus.Text = "Enter whole hex bytes, e.g. DE AD BE EF.";
                }

                return bytes;
            case 1:
                return Encoding.UTF8.GetBytes(text);
            default:
                return Encoding.Unicode.GetBytes(text);
        }
    }

    private async Task FindAsync(bool forward, bool includeSelection = false)
    {
        if (_source is not { } source || BuildPattern() is not { } pattern)
        {
            return;
        }

        _findCts?.Cancel();
        var cts = _findCts = new CancellationTokenSource();
        var ignoreCase = FindMode.SelectedIndex != 0 && MatchCase.IsChecked != true;
        var from = HasSelection ? SelStart - (includeSelection ? 1 : 0) : (forward ? -1 : source.Length);

        FindProgress.IsActive = true;
        FindProgress.Visibility = Visibility.Visible;
        FindStatus.Text = "Searching…";

        try
        {
            var hit = await Task.Run(() => HexSearch.Find(source, pattern, from, forward, ignoreCase, cts.Token), cts.Token);
            if (cts.IsCancellationRequested || source != _source)
            {
                return;
            }

            if (hit < 0)
            {
                FindStatus.Text = "No match.";
                return;
            }

            Select(hit, hit + pattern.Length - 1, scroll: false);
            ScrollTo(hit, center: true);
            FindStatus.Text = $"Match at 0x{hit:X}";
        }
        catch (OperationCanceledException)
        {
            // A newer search or a new file superseded this one.
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            FindStatus.Text = ex.Message;
        }
        finally
        {
            if (_findCts == cts)
            {
                FindProgress.IsActive = false;
                FindProgress.Visibility = Visibility.Collapsed;
            }
        }
    }

    // ---- layout toolbar -------------------------------------------------------------------

    private void OnRowWidthChanged(object sender, SelectionChangedEventArgs e)
    {
        if (RowWidth.SelectedItem is not ComboBoxItem { Tag: string tag } || !int.TryParse(tag, out var width) || width == _perRow)
        {
            return;
        }

        _perRow = width;
        Rebuild();

        if (HasSelection)
        {
            ScrollTo(_caret, center: true);
        }
    }

    private void OnInspectorToggled(object sender, RoutedEventArgs e)
    {
        if (InspectorPanel is null)
        {
            return;
        }

        InspectorPanel.Visibility = InspectorToggle.IsChecked == true ? Visibility.Visible : Visibility.Collapsed;
    }

    // ---- inspector -------------------------------------------------------------------------

    private readonly List<(string Label, TextBlock Value)> _inspector = [];

    private static readonly string[] InspectorLabels =
    [
        "Binary", "Int8", "UInt8",
        "Int16 LE", "Int16 BE", "UInt16 LE", "UInt16 BE",
        "Int32 LE", "Int32 BE", "UInt32 LE", "UInt32 BE",
        "Int64 LE", "Int64 BE", "UInt64 LE", "UInt64 BE",
        "Float32 LE", "Float32 BE", "Float64 LE", "Float64 BE",
        "Unix time (32)", "FILETIME (64)", "ASCII", "UTF-8",
    ];

    private void BuildInspectorRows()
    {
        for (var i = 0; i < InspectorLabels.Length; i++)
        {
            InspectorGrid.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });

            var label = new TextBlock
            {
                Text = InspectorLabels[i],
                FontSize = 12,
                Foreground = (Microsoft.UI.Xaml.Media.Brush)Application.Current.Resources["TextFillColorSecondaryBrush"],
            };
            var value = new TextBlock
            {
                FontFamily = new Microsoft.UI.Xaml.Media.FontFamily("Cascadia Mono, Consolas"),
                FontSize = 12,
                IsTextSelectionEnabled = true,
                TextWrapping = TextWrapping.WrapWholeWords,
            };

            Grid.SetRow(label, i);
            Grid.SetRow(value, i);
            Grid.SetColumn(value, 1);
            InspectorGrid.Children.Add(label);
            InspectorGrid.Children.Add(value);
            _inspector.Add((InspectorLabels[i], value));
        }

        InspectorGrid.Visibility = Visibility.Collapsed;
    }

    private void RefreshInspector()
    {
        if (_source is null || !HasSelection)
        {
            InspectorGrid.Visibility = Visibility.Collapsed;
            InspectorHint.Visibility = Visibility.Visible;
            return;
        }

        InspectorHint.Visibility = Visibility.Collapsed;
        InspectorGrid.Visibility = Visibility.Visible;

        var data = _source.Read(SelStart, 64);
        if (data.Length == 0)
        {
            return; // the file shrank under the selection
        }

        var set = (string label, string? text) =>
        {
            var row = _inspector.First(r => r.Label == label).Value;
            row.Text = text ?? "–";
        };

        string? Need(int n, Func<ReadOnlySpan<byte>, string?> read) => data.Length >= n ? read(data) : null;

        set("Binary", Convert.ToString(data[0], 2).PadLeft(8, '0'));
        set("Int8", ((sbyte)data[0]).ToString());
        set("UInt8", data[0].ToString());
        set("Int16 LE", Need(2, d => BinaryPrimitives.ReadInt16LittleEndian(d).ToString("N0")));
        set("Int16 BE", Need(2, d => BinaryPrimitives.ReadInt16BigEndian(d).ToString("N0")));
        set("UInt16 LE", Need(2, d => BinaryPrimitives.ReadUInt16LittleEndian(d).ToString("N0")));
        set("UInt16 BE", Need(2, d => BinaryPrimitives.ReadUInt16BigEndian(d).ToString("N0")));
        set("Int32 LE", Need(4, d => BinaryPrimitives.ReadInt32LittleEndian(d).ToString("N0")));
        set("Int32 BE", Need(4, d => BinaryPrimitives.ReadInt32BigEndian(d).ToString("N0")));
        set("UInt32 LE", Need(4, d => BinaryPrimitives.ReadUInt32LittleEndian(d).ToString("N0")));
        set("UInt32 BE", Need(4, d => BinaryPrimitives.ReadUInt32BigEndian(d).ToString("N0")));
        set("Int64 LE", Need(8, d => BinaryPrimitives.ReadInt64LittleEndian(d).ToString("N0")));
        set("Int64 BE", Need(8, d => BinaryPrimitives.ReadInt64BigEndian(d).ToString("N0")));
        set("UInt64 LE", Need(8, d => BinaryPrimitives.ReadUInt64LittleEndian(d).ToString("N0")));
        set("UInt64 BE", Need(8, d => BinaryPrimitives.ReadUInt64BigEndian(d).ToString("N0")));
        set("Float32 LE", Need(4, d => BinaryPrimitives.ReadSingleLittleEndian(d).ToString("G9", CultureInfo.InvariantCulture)));
        set("Float32 BE", Need(4, d => BinaryPrimitives.ReadSingleBigEndian(d).ToString("G9", CultureInfo.InvariantCulture)));
        set("Float64 LE", Need(8, d => BinaryPrimitives.ReadDoubleLittleEndian(d).ToString("G17", CultureInfo.InvariantCulture)));
        set("Float64 BE", Need(8, d => BinaryPrimitives.ReadDoubleBigEndian(d).ToString("G17", CultureInfo.InvariantCulture)));
        set("Unix time (32)", Need(4, d => FormatUnix(BinaryPrimitives.ReadUInt32LittleEndian(d))));
        set("FILETIME (64)", Need(8, d => FormatFileTime(BinaryPrimitives.ReadInt64LittleEndian(d))));
        set("ASCII", data[0] is >= 0x20 and < 0x7F ? $"'{(char)data[0]}'" : "(non-printable)");

        var utf8Length = (int)Math.Min(data.Length, Math.Min(32, SelEnd - SelStart + 1));
        var decoded = Encoding.UTF8.GetString(data, 0, utf8Length);
        set("UTF-8", string.Concat(decoded.Select(c => char.IsControl(c) ? '·' : c)));
    }

    private static string? FormatUnix(uint seconds) =>
        seconds == 0 ? null : DateTimeOffset.FromUnixTimeSeconds(seconds).ToString("yyyy-MM-dd HH:mm:ss 'UTC'", CultureInfo.InvariantCulture);

    private static string? FormatFileTime(long ticks) =>
        ticks is <= 0 or > 2650467743999999999
            ? null
            : DateTime.FromFileTimeUtc(ticks).ToString("yyyy-MM-dd HH:mm:ss 'UTC'", CultureInfo.InvariantCulture);

    // ---- copy -------------------------------------------------------------------------------

    private enum CopyFormat
    {
        Hex,
        Ascii,
        CArray,
        Base64,
    }

    private void OnCopyHexClick(object sender, RoutedEventArgs e) => CopySelection(CopyFormat.Hex);

    private void OnCopyAsciiClick(object sender, RoutedEventArgs e) => CopySelection(CopyFormat.Ascii);

    private void OnCopyCArrayClick(object sender, RoutedEventArgs e) => CopySelection(CopyFormat.CArray);

    private void OnCopyBase64Click(object sender, RoutedEventArgs e) => CopySelection(CopyFormat.Base64);

    private void OnCopyOffsetClick(object sender, RoutedEventArgs e)
    {
        if (HasSelection)
        {
            SetClipboard($"0x{SelStart:X}");
        }
    }

    private void CopySelection(CopyFormat format)
    {
        if (_source is null || !HasSelection)
        {
            return;
        }

        var length = SelEnd - SelStart + 1;
        if (length > MaxCopyBytes)
        {
            FindStatus.Text = $"Selection is too large to copy ({length / 1024 / 1024} MB); limit is {MaxCopyBytes / 1024 / 1024} MB.";
            return;
        }

        var data = _source.Read(SelStart, (int)length);
        var text = format switch
        {
            CopyFormat.Hex => string.Join(' ', data.Select(b => b.ToString("X2"))),
            CopyFormat.Ascii => new string(data.Select(b => b is >= 0x20 and < 0x7F ? (char)b : '.').ToArray()),
            CopyFormat.Base64 => Convert.ToBase64String(data),
            _ => ToCArray(data),
        };

        SetClipboard(text);
        FindStatus.Text = $"Copied {data.Length:N0} byte{(data.Length == 1 ? "" : "s")}.";
    }

    private static string ToCArray(byte[] data)
    {
        var sb = new StringBuilder("unsigned char data[] = {");
        for (var i = 0; i < data.Length; i++)
        {
            sb.Append(i % 12 == 0 ? "\n    " : " ");
            sb.Append("0x").Append(data[i].ToString("X2"));
            if (i < data.Length - 1)
            {
                sb.Append(',');
            }
        }

        return sb.Append("\n};").ToString();
    }

    private static void SetClipboard(string text)
    {
        var package = new DataPackage();
        package.SetText(text);
        Clipboard.SetContent(package);
    }

    // ---- misc ---------------------------------------------------------------------------------

    private void OnOpenExternallyClick(object sender, RoutedEventArgs e)
    {
        if (_path is null)
        {
            return;
        }

        try
        {
            System.Diagnostics.Process.Start(new System.Diagnostics.ProcessStartInfo(_path) { UseShellExecute = true });
        }
        catch (Exception ex) when (ex is System.ComponentModel.Win32Exception or IOException)
        {
            FindStatus.Text = ex.Message;
        }
    }

    private static string FormatSize(long bytes) => bytes switch
    {
        < 1024 => $"{bytes:N0} bytes",
        < 1024 * 1024 => $"{bytes / 1024.0:0.#} KB ({bytes:N0} bytes)",
        < 1024L * 1024 * 1024 => $"{bytes / 1024.0 / 1024.0:0.#} MB ({bytes:N0} bytes)",
        _ => $"{bytes / 1024.0 / 1024.0 / 1024.0:0.##} GB ({bytes:N0} bytes)",
    };
}
