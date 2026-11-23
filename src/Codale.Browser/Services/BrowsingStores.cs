using System.Collections.ObjectModel;

namespace Codale.Browser;

/// <summary>A saved page, shown in the sidebar's Bookmarks list.</summary>
public sealed record Bookmark(string Url, string Title, DateTimeOffset Added)
{
    public string Site => Uri.TryCreate(Url, UriKind.Absolute, out var uri) ? (uri.IsFile ? "Local file" : uri.Host) : Url;
    public string Label => Title.Length > 0 ? Title : Site;
}

/// <summary>One visit in the sidebar's History list.</summary>
public sealed record HistoryEntry(string Url, string Title, DateTimeOffset Visited)
{
    public string Site => Uri.TryCreate(Url, UriKind.Absolute, out var uri) ? (uri.IsFile ? "Local file" : uri.Host) : Url;
    public string Label => Title.Length > 0 ? Title : Site;
    public string VisitedText => Visited.ToLocalTime().ToString("d MMM, HH:mm");
    public string Detail => Site + " · " + VisitedText;
}

/// <summary>The bookmarks the user saved. Bound directly to the sidebar's list.</summary>
public sealed class BookmarkStore
{
    private sealed class Store
    {
        public List<Bookmark> Items { get; set; } = [];
    }

    private readonly string _path;

    public BookmarkStore(string folder)
    {
        _path = Path.Combine(folder, "bookmarks.json");
        Items = new ObservableCollection<Bookmark>(JsonFile.Load<Store>(_path).Items);
    }

    public ObservableCollection<Bookmark> Items { get; }

    public bool Contains(string url) => Items.Any(b => b.Url == url);

    /// <summary>Saves the page, or removes its bookmark. Returns true when the page is now bookmarked.</summary>
    public bool Toggle(string url, string title)
    {
        var existing = Items.FirstOrDefault(b => b.Url == url);
        if (existing is not null) Items.Remove(existing);
        else Items.Insert(0, new Bookmark(url, title, DateTimeOffset.UtcNow));
        Save();
        return existing is null;
    }

    public void Remove(string url)
    {
        foreach (var bookmark in Items.Where(b => b.Url == url).ToList()) Items.Remove(bookmark);
        Save();
    }

    private void Save() => JsonFile.Save(_path, new Store { Items = Items.ToList() });
}

/// <summary>Pages the user has visited, newest first, capped so the file stays small.</summary>
public sealed class HistoryStore
{
    private const int Limit = 2000;

    private sealed class Store
    {
        public List<HistoryEntry> Items { get; set; } = [];
    }

    private readonly string _path;
    private readonly List<HistoryEntry> _items;

    public HistoryStore(string folder)
    {
        _path = Path.Combine(folder, "history.json");
        _items = JsonFile.Load<Store>(_path).Items;
    }

    /// <summary>Records a visit. A reload of the page just visited refreshes its entry instead of adding another.</summary>
    public void Add(string url, string title)
    {
        var entry = new HistoryEntry(url, title, DateTimeOffset.UtcNow);
        if (_items.Count > 0 && _items[0].Url == url) _items[0] = entry;
        else _items.Insert(0, entry);
        if (_items.Count > Limit) _items.RemoveRange(Limit, _items.Count - Limit);
        Save();
    }

    public IReadOnlyList<HistoryEntry> Search(string query) =>
        _items.Where(h => query.Length == 0 || h.Url.Contains(query, StringComparison.OrdinalIgnoreCase) || h.Title.Contains(query, StringComparison.OrdinalIgnoreCase)).ToList();

    public void Clear()
    {
        _items.Clear();
        Save();
    }

    private void Save() => JsonFile.Save(_path, new Store { Items = _items });
}

/// <summary>A download in the sidebar's Downloads list, kept up to date from the WebView2 download operation.</summary>
public sealed class DownloadRow : DiagnosticsRow
{
    public DownloadRow(string url, string name)
    {
        Url = url;
        Name = name;
    }

    public string Url { get; }
    public string Name { get; set; }
    public string Path { get; set; } = "";
    public string State { get; set; } = "In progress";
    public long Received { get; set; }
    public long Total { get; set; }

    public string StatusText => State == "In progress" && Total > 0
        ? $"{State} · {Format(Received)} of {Format(Total)}"
        : State;

    public bool Finished => State is "Completed" or "Failed";

    /// <summary>0 to 100, for the progress bar; a finished download reads 100.</summary>
    public double Progress => State == "Completed" ? 100 : Total > 0 ? Received * 100.0 / Total : 0;

    public static string Format(long bytes) => bytes < 1024 ? $"{bytes} B"
        : bytes < 1024 * 1024 ? $"{bytes / 1024d:0.0} KB"
        : $"{bytes / 1048576d:0.0} MB";
}
