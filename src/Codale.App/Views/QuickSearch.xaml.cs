using System.Collections.ObjectModel;

using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Input;

using Windows.System;

namespace Codale.App.Views;

/// <summary>One row of the quick search: what it is, where it came from, and what choosing it does.</summary>
public sealed class QuickSearchItem
{
    /// <summary>Where the row comes from ("Files", "Open tabs", "History"...): rows are listed by group.</summary>
    public required string Group { get; init; }

    public required string Glyph { get; init; }

    public required string Title { get; init; }

    public string Detail { get; init; } = "";

    /// <summary>The detail as shown after the title, spaced off it.</summary>
    public string DetailText => Detail.Length == 0 ? "" : "    " + Detail;

    /// <summary>A short word on the right: what Enter does ("Run", "Open", "Ask").</summary>
    public string Hint { get; init; } = "";

    public required Action Invoke { get; init; }

    /// <summary>Set by the palette on the first row of each group, which carries the group's label.</summary>
    public bool ShowGroup { get; set; }

    public Visibility GroupVisibility => ShowGroup ? Visibility.Visible : Visibility.Collapsed;
}

/// <summary>Whatever the window has open answers the palette's queries: the workspace page.</summary>
public interface IQuickSearchSource
{
    /// <param name="query">What was typed; empty when the palette just opened.</param>
    /// <param name="fileScope">The open file's name when the search is scoped to it (Ctrl+F in an editor).</param>
    Task<IReadOnlyList<QuickSearchItem>> SearchAsync(string query, string? fileScope, CancellationToken ct);
}

/// <summary>
/// The floating palette behind the title bar's search field. It never searches on its own:
/// every keystroke (debounced) asks the <see cref="Source"/>, and the rows it returns run
/// their own action, so the palette stays a view over the workspace's data.
/// </summary>
public sealed partial class QuickSearch : UserControl
{
    private readonly ObservableCollection<QuickSearchItem> _results = [];
    private CancellationTokenSource? _cts;
    private Microsoft.UI.Dispatching.DispatcherQueueTimer? _debounce;
    private string? _fileScope;

    public QuickSearch()
    {
        InitializeComponent();
        ResultList.ItemsSource = _results;
        SizeChanged += (_, _) => Card.Width = Math.Min(640, Math.Max(320, ActualWidth - 32));
    }

    public IQuickSearchSource? Source { get; set; }

    public bool IsOpen => Visibility == Visibility.Visible;

    /// <summary>Raised after the palette closes, so the launcher can show the idle state again.</summary>
    public event EventHandler? Closed;

    /// <summary>Opens the palette; with <paramref name="fileScope"/> the query is scoped to that open file.</summary>
    public void Open(string? fileScope = null)
    {
        _fileScope = fileScope;
        ScopeChip.Visibility = fileScope is null ? Visibility.Collapsed : Visibility.Visible;
        ScopeText.Text = fileScope is null ? "" : $"in {fileScope}";
        QueryBox.PlaceholderText = fileScope is null
            ? "Search files, chats, commands and actions…"
            : "Find in this file · Backspace for the whole project";

        if (!IsOpen)
        {
            QueryBox.Text = "";
        }

        Visibility = Visibility.Visible;
        QueryBox.Focus(FocusState.Programmatic);
        QueryBox.SelectAll();
        Refresh(0);
    }

    public void Close()
    {
        if (!IsOpen)
        {
            return;
        }

        _cts?.Cancel();
        _debounce?.Stop();
        Visibility = Visibility.Collapsed;
        _results.Clear();
        Closed?.Invoke(this, EventArgs.Empty);
    }

    private void OnBackdropTapped(object sender, TappedRoutedEventArgs e) => Close();

    private void OnQueryTextChanged(object sender, TextChangedEventArgs e) => Refresh(110);

    private void Refresh(int delayMs)
    {
        if (_debounce is null)
        {
            _debounce = DispatcherQueue.CreateTimer();
            _debounce.IsRepeating = false;
            _debounce.Tick += (_, _) => _ = RunAsync();
        }

        _debounce.Stop();
        _debounce.Interval = TimeSpan.FromMilliseconds(Math.Max(1, delayMs));
        _debounce.Start();
    }

    private async Task RunAsync()
    {
        if (Source is not { } source || !IsOpen)
        {
            return;
        }

        _cts?.Cancel();
        var cts = _cts = new CancellationTokenSource();
        var query = QueryBox.Text.Trim();

        IReadOnlyList<QuickSearchItem> found;
        try
        {
            found = await source.SearchAsync(query, _fileScope, cts.Token);
        }
        catch (OperationCanceledException)
        {
            return;
        }
        catch (Exception)
        {
            found = [];
        }

        if (cts.IsCancellationRequested || !IsOpen)
        {
            return;
        }

        string? group = null;
        _results.Clear();
        foreach (var item in found)
        {
            item.ShowGroup = item.Group != group;
            group = item.Group;
            _results.Add(item);
        }

        if (_results.Count > 0)
        {
            ResultList.SelectedIndex = 0;
        }

        StatusText.Text = _results.Count == 0 ? "Nothing matches" : "";
    }

    private void OnQueryPreviewKeyDown(object sender, KeyRoutedEventArgs e)
    {
        switch (e.Key)
        {
            case VirtualKey.Down:
                Move(1);
                e.Handled = true;
                break;
            case VirtualKey.Up:
                Move(-1);
                e.Handled = true;
                break;
            case VirtualKey.PageDown:
                Move(8);
                e.Handled = true;
                break;
            case VirtualKey.PageUp:
                Move(-8);
                e.Handled = true;
                break;
            case VirtualKey.Enter:
                e.Handled = true;
                if (ResultList.SelectedItem is QuickSearchItem item)
                {
                    Choose(item);
                }

                break;
            case VirtualKey.Escape:
                e.Handled = true;
                Close();
                break;
            case VirtualKey.Back when _fileScope is not null && QueryBox.Text.Length == 0:
                // Backspace on an empty scoped query widens it to the whole project.
                e.Handled = true;
                Open(null);
                break;
        }
    }

    private void Move(int by)
    {
        if (_results.Count == 0)
        {
            return;
        }

        var index = Math.Clamp(ResultList.SelectedIndex + by, 0, _results.Count - 1);
        ResultList.SelectedIndex = index;
        ResultList.ScrollIntoView(_results[index]);
    }

    private void OnResultClick(object sender, ItemClickEventArgs e)
    {
        if (e.ClickedItem is QuickSearchItem item)
        {
            Choose(item);
        }
    }

    private void Choose(QuickSearchItem item)
    {
        Close();
        try
        {
            item.Invoke();
        }
        catch (Exception)
        {
            // A row's action failing must not take the window with it.
        }
    }
}
