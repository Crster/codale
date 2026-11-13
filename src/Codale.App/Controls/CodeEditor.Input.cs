using System.Diagnostics;

using Codale.Editor;

using Microsoft.UI.Input;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Input;

using Windows.ApplicationModel.DataTransfer;
using Windows.System;

namespace Codale.App.Controls;

// Keyboard and pointer input, mapped onto EditorModel commands.
//
// Keys (beyond the usual caret movement and editing):
//   Alt+Up/Down            move the lines under the selections
//   Shift+Alt+Up/Down      duplicate those lines below/above      (also Ctrl+Shift+D)
//   Ctrl+Alt+Up/Down       add a caret on the row above/below
//   Ctrl+D                 select the word, then add the next copy as another selection
//   Ctrl+Shift+L           a selection on every copy
//   Ctrl+Shift+K           delete lines            Ctrl+L  select line
//   Ctrl+Enter / +Shift    new line below / above   Tab / Shift+Tab indent / outdent
//   Ctrl+F2                toggle bookmark   F2 / Shift+F2 next / previous   Ctrl+Shift+F2 clear
//   Alt+H                  pin a highlight on the word   Alt+Shift+H clear pinned highlights
//   Escape                 back to one caret
// Pointer: Alt+click adds/removes a caret, Alt+Shift+drag or middle-drag selects a column,
// double/triple click select word/line, clicks in the gutter select lines, the bookmark strip
// toggles bookmarks.
public sealed partial class CodeEditor
{
    private enum DragMode
    {
        None,
        Characters,
        Words,
        Lines,
        Column,
    }

    private DragMode _dragMode;
    private Selection _dragOrigin;
    private double _columnAnchorX;
    private int _columnAnchorRow;
    private Windows.Foundation.Point _dragPoint;
    private readonly Stopwatch _clickClock = new();
    private Windows.Foundation.Point _lastClickPoint;
    private int _clickCount;
    private Microsoft.UI.Dispatching.DispatcherQueueTimer? _autoScrollTimer;
    private InputSystemCursorShape? _cursorShape;
    private string? _lineCopyText;

    // ------------------------------------------------------------------ keyboard

    private void OnEditorKeyDown(object sender, KeyRoutedEventArgs e)
    {
        var ctrl = IsCtrlDown();
        var shift = IsShiftDown();
        var alt = IsAltDown();

        if (e.Key == VirtualKey.Tab && !CompletionOpen && !shift && AcceptGhost())
        {
            e.Handled = true;
            return;
        }

        if (e.Key == VirtualKey.Space && ctrl && InlineCompleter is not null)
        {
            CloseCompletion();
            RequestGhost();
            e.Handled = true;
            return;
        }

        if (e.Key == VirtualKey.Escape && GhostVisible && !CompletionOpen)
        {
            ClearGhost();
            e.Handled = true;
            return;
        }

        if (e.Key is not (VirtualKey.Shift or VirtualKey.Control or VirtualKey.Menu or VirtualKey.LeftShift or VirtualKey.RightShift
            or VirtualKey.LeftControl or VirtualKey.RightControl or VirtualKey.LeftMenu or VirtualKey.RightMenu))
        {
            ClearGhost();
        }

        if (HandleCompletionKey(e))
        {
            e.Handled = true;
            return;
        }

        e.Handled = HandleKey(e.Key, ctrl, shift, alt);
    }

    private bool HandleKey(VirtualKey key, bool ctrl, bool shift, bool alt)
    {
        switch (key)
        {
            case VirtualKey.Left: _model.MoveHorizontal(back: true, word: ctrl, extend: shift); break;
            case VirtualKey.Right: _model.MoveHorizontal(back: false, word: ctrl, extend: shift); break;

            case VirtualKey.Up or VirtualKey.Down:
            {
                var up = key == VirtualKey.Up;
                if (alt && shift && !ctrl)
                {
                    _model.DuplicateLines(down: !up);
                }
                else if (alt && !ctrl)
                {
                    _model.MoveLines(up);
                }
                else if (ctrl && alt)
                {
                    AddCaretVertically(up ? -1 : 1);
                }
                else if (ctrl)
                {
                    ScrollTo(null, _scrollY + (up ? -_lineHeight : _lineHeight), animate: true);
                    return true;
                }
                else
                {
                    MoveVertically(up ? -1 : 1, shift);
                }

                break;
            }

            case VirtualKey.PageUp or VirtualKey.PageDown:
            {
                var rows = Math.Max(1, (int)(ViewportHeight / _lineHeight) - 1) * (key == VirtualKey.PageUp ? -1 : 1);
                ScrollTo(null, _scrollY + rows * _lineHeight, animate: false);
                MoveVertically(rows, shift);
                break;
            }

            case VirtualKey.Home:
                if (ctrl)
                {
                    _model.MoveDocumentStart(shift);
                }
                else
                {
                    _model.MoveHome(shift);
                }

                break;
            case VirtualKey.End:
                if (ctrl)
                {
                    _model.MoveDocumentEnd(shift);
                }
                else
                {
                    _model.MoveEnd(shift);
                }

                break;

            case VirtualKey.Back: _model.Backspace(word: ctrl); break;
            case VirtualKey.Delete when shift && !ctrl: CutSelection(); break;
            case VirtualKey.Delete: _model.Delete(word: ctrl); break;
            case VirtualKey.Enter when ctrl: _model.InsertLine(above: shift); break;
            case VirtualKey.Enter: _model.InsertNewLine(); break;
            case VirtualKey.Tab when shift: _model.OutdentLines(); break;
            case VirtualKey.Tab: _model.Tab(); break;
            case VirtualKey.Escape: return _model.CollapseToPrimary();

            case VirtualKey.A when ctrl: _model.SelectAll(); break;
            case VirtualKey.C when ctrl:
            case VirtualKey.Insert when ctrl: CopySelection(); break;
            case VirtualKey.X when ctrl: CutSelection(); break;
            case VirtualKey.V when ctrl:
            case VirtualKey.Insert when shift: _ = PasteAsync(); break;
            case VirtualKey.Z when ctrl && shift: _model.Redo(); break;
            case VirtualKey.Z when ctrl: _model.Undo(); break;
            case VirtualKey.Y when ctrl: _model.Redo(); break;
            case VirtualKey.D when ctrl && shift: _model.DuplicateLines(down: true); break;
            case VirtualKey.D when ctrl: _model.AddNextOccurrence(); RevealCaret(); break;
            case VirtualKey.L when ctrl && shift: _model.SelectAllOccurrences(); break;
            case VirtualKey.L when ctrl: SelectLines(); break;
            case VirtualKey.K when ctrl && shift: _model.DeleteLines(); break;

            case VirtualKey.F2 when ctrl && shift: _bookmarks.Clear(); break;
            case VirtualKey.F2 when ctrl: _bookmarks.Toggle(_model.Primary.Caret.Line); break;
            case VirtualKey.F2: GoToBookmark(previous: shift); break;
            case VirtualKey.H when alt && shift && !ctrl: ClearPinnedHighlights(); break;
            case VirtualKey.H when alt && !ctrl: TogglePinnedHighlight(); break;

            default: return false;
        }

        RestartCaretBlink();
        if (key is not (VirtualKey.Escape or VirtualKey.F2))
        {
            RevealCaret();
        }

        return true;
    }

    private void OnEditorCharacterReceived(UIElement sender, CharacterReceivedRoutedEventArgs args)
    {
        // Control combos arrive here as control characters and were handled in KeyDown.
        if (IsCtrlDown() && !IsAltDown() || IsAltDown() && !IsCtrlDown())
        {
            return;
        }

        var ch = args.Character;
        if (ch is '\r' or '\n' or '\t' || char.IsControl(ch))
        {
            return;
        }

        args.Handled = true;

        // A character outside the BMP arrives as two calls, one surrogate each; insert the pair whole.
        if (char.IsHighSurrogate(ch))
        {
            _pendingHighSurrogate = ch;
            return;
        }

        var text = char.IsLowSurrogate(ch) && _pendingHighSurrogate is { } high
            ? string.Concat(high, ch)
            : ch.ToString();
        _pendingHighSurrogate = null;
        _model.Type(text);
        OnCharacterTyped(ch);
        ScheduleGhost();
    }

    private char? _pendingHighSurrogate;

    /// <summary>Up/Down (and Page): one visual row at a time, keeping the pixel column it started from.</summary>
    private void MoveVertically(int rows, bool extend)
    {
        EnsureMetrics();
        var total = TotalRows;
        var list = new List<Selection>(_model.Selections.Count);
        foreach (var selection in _model.Selections)
        {
            var (row, x) = Locate(selection.Caret);
            var preferred = double.IsNaN(selection.PreferredX) ? x : selection.PreferredX;
            var target = row + rows;
            DocumentPosition caret;
            if (target < 0)
            {
                caret = default;
            }
            else if (target >= total)
            {
                caret = _model.Buffer.End;
            }
            else
            {
                var (line, rowInLine) = LineOfRow(target);
                caret = new DocumentPosition(line, ColumnAt(line, rowInLine, preferred));
            }

            list.Add(new Selection(extend ? selection.Anchor : caret, caret, preferred));
        }

        _model.SetSelections(list, _model.PrimaryIndex);
    }

    /// <summary>Ctrl+Alt+Up/Down: a new caret one row beyond the outermost one, at the same pixel column.</summary>
    private void AddCaretVertically(int direction)
    {
        EnsureMetrics();
        var edge = direction < 0 ? _model.Selections[0] : _model.Selections[^1];
        var (row, x) = Locate(edge.Caret);
        var preferred = double.IsNaN(edge.PreferredX) ? x : edge.PreferredX;
        var target = row + direction;
        if (target < 0 || target >= TotalRows)
        {
            return;
        }

        var (line, rowInLine) = LineOfRow(target);
        var caret = new DocumentPosition(line, ColumnAt(line, rowInLine, preferred));
        _model.AddSelection(new Selection(caret, caret, preferred));
    }

    /// <summary>Ctrl+L: select the caret lines; again extends by the next line.</summary>
    private void SelectLines()
    {
        var list = _model.Selections.Select(s =>
        {
            // Already whole lines (ending at a line start): take in the next line too.
            var last = s.End.Line;
            return new Selection(new DocumentPosition(s.Start.Line, 0), _model.LineAt(last).End);
        }).ToList();
        _model.SetSelections(list, _model.PrimaryIndex);
    }

    private void GoToBookmark(bool previous)
    {
        var line = _model.Primary.Caret.Line;
        var target = previous ? _bookmarks.Previous(line) : _bookmarks.Next(line);
        if (target < 0)
        {
            return;
        }

        _model.SetCaret(new DocumentPosition(target, 0));
        ScrollLineToCenter(target);
    }

    // ------------------------------------------------------------------ clipboard

    private void CopySelection()
    {
        string text;
        if (_model.HasSelection)
        {
            text = _model.GetSelectedText();
            _lineCopyText = null;
        }
        else
        {
            text = _model.GetCaretLinesText();
            _lineCopyText = text;
        }

        var package = new DataPackage { RequestedOperation = DataPackageOperation.Copy };
        package.SetText(text);
        Clipboard.SetContent(package);
    }

    private void CutSelection()
    {
        CopySelection();
        if (_model.HasSelection)
        {
            _model.Delete();
        }
        else
        {
            _model.DeleteLines();
        }
    }

    private async Task PasteAsync()
    {
        var content = Clipboard.GetContent();
        if (!content.Contains(StandardDataFormats.Text))
        {
            return;
        }

        var text = await content.GetTextAsync();
        if (_closed)
        {
            return;
        }

        if (!_model.HasSelection && _lineCopyText is not null && text == _lineCopyText && !_model.HasMultipleSelections)
        {
            // Whole lines copied with nothing selected paste as whole lines above the caret's line.
            var caret = _model.Primary.Caret;
            _model.ReplaceRange(new DocumentPosition(caret.Line, 0), new DocumentPosition(caret.Line, 0), text);
            _model.SetCaret(_model.Buffer.Clamp(new DocumentPosition(caret.Line + text.Count(c => c == '\n'), caret.Col)));
            return;
        }

        _model.Paste(text);
    }

    // ------------------------------------------------------------------ pointer

    private void OnPointerPressed(object sender, PointerRoutedEventArgs e)
    {
        Focus(FocusState.Pointer);
        CloseCompletion();
        var point = e.GetCurrentPoint(RootGrid);
        var position = point.Position;
        var properties = point.Properties;

        if (properties.IsLeftButtonPressed && TryPressScrollBar(position, e))
        {
            e.Handled = true;
            return;
        }

        if (properties.IsRightButtonPressed)
        {
            // The context menu: move the caret there unless the click is inside a selection.
            var clicked = PositionAt(position);
            if (!_model.Selections.Any(s => s.Contains(clicked)))
            {
                _model.SetCaret(clicked);
            }

            return;
        }

        var shift = IsShiftDown();
        var alt = IsAltDown();
        _dragPoint = position;

        if (properties.IsMiddleButtonPressed || (alt && shift && properties.IsLeftButtonPressed))
        {
            StartColumnDrag(position);
            RootGrid.CapturePointer(e.Pointer);
            e.Handled = true;
            return;
        }

        if (!properties.IsLeftButtonPressed)
        {
            return;
        }

        // Clicks in the gutter: the bookmark strip toggles, the numbers select whole lines.
        if (position.X < GutterWidth)
        {
            var row = Math.Clamp((int)Math.Floor((position.Y + _scrollY) / _lineHeight), 0, TotalRows - 1);
            var (line, _) = LineOfRow(row);
            if (position.X < BookmarkMargin)
            {
                _bookmarks.Toggle(line);
            }
            else if (_hoveredMarker is null)
            {
                _dragOrigin = _model.LineAt(line);
                _dragMode = DragMode.Lines;
                if (shift)
                {
                    ExtendDrag(position);
                }
                else
                {
                    _model.SetSelection(_dragOrigin);
                }

                RootGrid.CapturePointer(e.Pointer);
            }

            e.Handled = true;
            return;
        }

        _clickClock.Stop();
        var repeat = _clickClock.ElapsedMilliseconds < 450
            && Math.Abs(position.X - _lastClickPoint.X) < 4
            && Math.Abs(position.Y - _lastClickPoint.Y) < 4;
        _clickCount = repeat ? _clickCount % 3 + 1 : 1;
        _clickClock.Restart();
        _lastClickPoint = position;

        var at = PositionAt(position);
        if (alt && _clickCount == 1)
        {
            _model.ToggleCaret(at);
            _dragMode = DragMode.None;
        }
        else if (_clickCount == 2)
        {
            _dragOrigin = _model.RunAt(at);
            _dragMode = DragMode.Words;
            _model.SetSelection(_dragOrigin);
        }
        else if (_clickCount == 3)
        {
            _dragOrigin = _model.LineAt(at.Line);
            _dragMode = DragMode.Lines;
            _model.SetSelection(_dragOrigin);
        }
        else if (shift)
        {
            _model.SetCaret(at, extend: true);
            _dragOrigin = new Selection(_model.Primary.Anchor);
            _dragMode = DragMode.Characters;
        }
        else
        {
            _model.SetCaret(at);
            _dragOrigin = new Selection(at);
            _dragMode = DragMode.Characters;
        }

        if (_dragMode != DragMode.None)
        {
            RootGrid.CapturePointer(e.Pointer);
        }

        e.Handled = true;
    }

    private void OnPointerMoved(object sender, PointerRoutedEventArgs e)
    {
        var position = e.GetCurrentPoint(RootGrid).Position;
        if (_barDrag != BarDrag.None)
        {
            DragScrollBar(position);
            e.Handled = true;
            return;
        }

        if (_dragMode != DragMode.None)
        {
            _dragPoint = position;
            ExtendDrag(position);
            UpdateAutoScroll();
            e.Handled = true;
            return;
        }

        UpdateHover(position);
    }

    private void OnPointerReleased(object sender, PointerRoutedEventArgs e)
    {
        EndDrags();
        RootGrid.ReleasePointerCapture(e.Pointer);
        UpdateHover(e.GetCurrentPoint(RootGrid).Position);
        e.Handled = true;
    }

    private void OnPointerCaptureLost(object sender, PointerRoutedEventArgs e) => EndDrags();

    private void OnPointerEntered(object sender, PointerRoutedEventArgs e) => UpdateHover(e.GetCurrentPoint(RootGrid).Position);

    private void OnPointerExited(object sender, PointerRoutedEventArgs e)
    {
        if (_barDrag != BarDrag.None || _dragMode != DragMode.None)
        {
            return;
        }

        _vHover = _hHover = _hNear = false;
        HideDiffPreview();
        SetCursor(null);
        StartRenderLoop();
    }

    private void EndDrags()
    {
        _autoScrollTimer?.Stop();
        if (_barDrag != BarDrag.None)
        {
            _barDrag = BarDrag.None;
            StartRenderLoop();
        }

        _dragMode = DragMode.None;
    }

    private void UpdateHover(Windows.Foundation.Point position)
    {
        var width = ViewportWidth;
        var height = ViewportHeight;
        var overV = HasVBar && position.X >= width - BarHitSize && position.X <= width && position.Y >= 0;
        var overH = !overV && HasHBar && position.Y >= height - BarHitSize && position.Y <= height && position.X >= GutterWidth;
        var nearH = HasHBar && position.Y >= height - BarHitSize * 3 && position.X >= GutterWidth && position.Y <= height;
        if (overV != _vHover || overH != _hHover || nearH != _hNear)
        {
            _vHover = overV;
            _hHover = overH;
            _hNear = nearH;
            StartRenderLoop();
        }

        if (overV)
        {
            HideDiffPreview();
            SetCursor(MarkAt(position.Y) is not null && !OnVThumb(position.Y) ? InputSystemCursorShape.Hand : InputSystemCursorShape.Arrow);
            return;
        }

        if (overH)
        {
            HideDiffPreview();
            SetCursor(InputSystemCursorShape.Arrow);
            return;
        }

        if (UpdateDiffHover(position))
        {
            SetCursor(InputSystemCursorShape.Hand);
            return;
        }

        SetCursor(position.X < GutterWidth ? InputSystemCursorShape.Arrow : InputSystemCursorShape.IBeam);
    }

    private void SetCursor(InputSystemCursorShape? shape)
    {
        if (_cursorShape == shape)
        {
            return;
        }

        _cursorShape = shape;
        ProtectedCursor = shape is { } s ? InputSystemCursor.Create(s) : null;
    }

    private void StartColumnDrag(Windows.Foundation.Point position)
    {
        EnsureMetrics();
        _dragMode = DragMode.Column;
        _columnAnchorX = position.X - TextLeft + _scrollX;
        _columnAnchorRow = Math.Clamp((int)Math.Floor((position.Y + _scrollY) / _lineHeight), 0, TotalRows - 1);
        ExtendDrag(position);
    }

    private void ExtendDrag(Windows.Foundation.Point position)
    {
        var at = PositionAt(position);
        switch (_dragMode)
        {
            case DragMode.Characters:
                _model.SetSelection(new Selection(_dragOrigin.Anchor, at));
                break;

            case DragMode.Words:
            {
                var word = _model.RunAt(at);
                _model.SetSelection(at < _dragOrigin.Start
                    ? new Selection(_dragOrigin.End, word.Start)
                    : new Selection(_dragOrigin.Start, DocumentPosition.Max(word.End, _dragOrigin.End)));
                break;
            }

            case DragMode.Lines:
            {
                var line = _model.LineAt(at.Line);
                _model.SetSelection(at < _dragOrigin.Start
                    ? new Selection(_dragOrigin.End, line.Start)
                    : new Selection(_dragOrigin.Start, DocumentPosition.Max(line.End, _dragOrigin.End)));
                break;
            }

            case DragMode.Column:
            {
                // A box: the same pixel span on every row between the anchor and the pointer.
                var row = Math.Clamp((int)Math.Floor((position.Y + _scrollY) / _lineHeight), 0, TotalRows - 1);
                var x = Math.Max(0, position.X - TextLeft + _scrollX);
                var list = new List<Selection>();
                var step = row >= _columnAnchorRow ? 1 : -1;
                for (var r = _columnAnchorRow; ; r += step)
                {
                    var (line, rowInLine) = LineOfRow(r);
                    var anchor = new DocumentPosition(line, ColumnAt(line, rowInLine, Math.Max(0, _columnAnchorX)));
                    var caret = new DocumentPosition(line, ColumnAt(line, rowInLine, x));
                    list.Add(new Selection(anchor, caret, x));
                    if (r == row)
                    {
                        break;
                    }
                }

                _model.SetSelections(list, list.Count - 1);
                break;
            }
        }
    }

    // Dragging a selection past an edge keeps scrolling that way, faster the further out the pointer is.
    private void UpdateAutoScroll()
    {
        var outside = _dragPoint.Y < 0 || _dragPoint.Y > ViewportHeight
            || (_wrapCols == NoWrap && (_dragPoint.X < GutterWidth || _dragPoint.X > ViewportWidth));
        if (!outside)
        {
            _autoScrollTimer?.Stop();
            return;
        }

        if (_autoScrollTimer is null)
        {
            _autoScrollTimer = Microsoft.UI.Dispatching.DispatcherQueue.GetForCurrentThread().CreateTimer();
            _autoScrollTimer.Interval = TimeSpan.FromMilliseconds(16);
            _autoScrollTimer.Tick += (_, _) => OnAutoScrollTick();
        }

        if (!_autoScrollTimer.IsRunning)
        {
            _autoScrollTimer.Start();
        }
    }

    private void OnAutoScrollTick()
    {
        if (_dragMode == DragMode.None)
        {
            _autoScrollTimer?.Stop();
            return;
        }

        double Step(double over) => Math.Clamp(over / 3, 2, _lineHeight * 4) * Math.Sign(over);
        double? y = null;
        double? x = null;
        if (_dragPoint.Y < 0)
        {
            y = _scrollY + Step(_dragPoint.Y);
        }
        else if (_dragPoint.Y > ViewportHeight)
        {
            y = _scrollY + Step(_dragPoint.Y - ViewportHeight);
        }

        if (_wrapCols == NoWrap)
        {
            if (_dragPoint.X < GutterWidth)
            {
                x = _scrollX + Step(_dragPoint.X - GutterWidth);
            }
            else if (_dragPoint.X > ViewportWidth)
            {
                x = _scrollX + Step(_dragPoint.X - ViewportWidth);
            }
        }

        if (x is null && y is null)
        {
            _autoScrollTimer?.Stop();
            return;
        }

        ScrollTo(x, y, animate: false);
        ExtendDrag(_dragPoint);
    }

    private void OnPointerWheelChanged(object sender, PointerRoutedEventArgs e)
    {
        var point = e.GetCurrentPoint(RootGrid);
        var delta = point.Properties.MouseWheelDelta;
        e.Handled = true;

        if (IsCtrlDown())
        {
            var size = (float)(FontSize is double.NaN or <= 0 ? DefaultFontSize : FontSize) + Math.Sign(delta);
            if (size >= MinFontSize && size <= MaxFontSize && size != FontSize)
            {
                FontSize = size;
            }

            return;
        }

        EnsureMetrics();
        if (point.Properties.IsHorizontalMouseWheel || IsShiftDown())
        {
            var dx = point.Properties.IsHorizontalMouseWheel ? delta : -delta;
            ScrollTo(_scrollX + dx * _charWidth * 3 / 120 * (point.Properties.IsHorizontalMouseWheel ? 1 : 2), null, animate: false);
            return;
        }

        // A mouse notch (a multiple of 120) eases three lines over a few frames; a precision
        // touchpad sends small deltas many times a second and follows the fingers directly.
        var notch = delta % 120 == 0;
        var distance = -delta * 3 * _lineHeight / 120;
        if (notch)
        {
            var from = double.IsNaN(_targetY) ? _scrollY : _targetY;
            ScrollTo(null, from + distance, animate: true);
        }
        else
        {
            _targetY = double.NaN;
            ScrollTo(null, _scrollY + distance, animate: false);
        }
    }

    private void OnEditorLosingFocus(UIElement sender, LosingFocusEventArgs args)
    {
        if (args.NewFocusedElement is null || ReferenceEquals(args.NewFocusedElement, RootGrid) || ReferenceEquals(args.NewFocusedElement, Surface))
        {
            args.TryCancel();
        }
    }

    // ------------------------------------------------------------------ scrollbar pointer

    private bool OnVThumb(double y)
    {
        var (start, length) = VThumb();
        return y >= start && y <= start + length;
    }

    private bool TryPressScrollBar(Windows.Foundation.Point position, PointerRoutedEventArgs e)
    {
        var width = ViewportWidth;
        var height = ViewportHeight;
        if (HasVBar && position.X >= width - BarHitSize)
        {
            var (start, length) = VThumb();
            if (!OnVThumb(position.Y) && MarkAt(position.Y) is { } mark)
            {
                _model.SetCaret(new DocumentPosition(mark.Line, mark.Col));
                ScrollLineToCenter(mark.Line);
                return true;
            }

            _barDrag = BarDrag.Vertical;
            _barGrab = OnVThumb(position.Y) ? position.Y - start : length / 2;
            RootGrid.CapturePointer(e.Pointer);
            DragScrollBar(position);
            StartRenderLoop();
            return true;
        }

        if (HasHBar && _hReveal > 0.2 && position.Y >= height - BarHitSize && position.X >= GutterWidth)
        {
            var (start, length) = HThumb();
            _barDrag = BarDrag.Horizontal;
            _barGrab = position.X >= start && position.X <= start + length ? position.X - start : length / 2;
            RootGrid.CapturePointer(e.Pointer);
            DragScrollBar(position);
            StartRenderLoop();
            return true;
        }

        return false;
    }

    private void DragScrollBar(Windows.Foundation.Point position)
    {
        if (_barDrag == BarDrag.Vertical)
        {
            var (_, length) = VThumb();
            var travel = ViewportHeight - length;
            if (travel > 0)
            {
                ScrollTo(null, Math.Clamp((position.Y - _barGrab) / travel, 0, 1) * MaxScrollY, animate: false);
            }
        }
        else if (_barDrag == BarDrag.Horizontal)
        {
            var (_, length) = HThumb();
            var left = GutterWidth;
            var travel = ViewportWidth - left - (HasVBar ? VBarHoverWidth : 0) - length;
            if (travel > 0)
            {
                ScrollTo(Math.Clamp((position.X - left - _barGrab) / travel, 0, 1) * MaxScrollX, null, animate: false);
            }
        }
    }
}
