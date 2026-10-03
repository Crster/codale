using Codale.App.Services;

using CommunityToolkit.Mvvm.ComponentModel;

namespace Codale.App.ViewModels;

/// <summary>
/// The workspace editor: opens the file a tree click or search result points at in the
/// native code editor, tracks unsaved changes, and writes them back.
/// </summary>
/// <remarks>
/// The view model deliberately knows only text and paths; highlighting is the control's
/// own (its built-in language set), chosen in the view by extension. Files beyond the
/// size guard and binary files are shown as a message rather than loaded.
/// </remarks>
public sealed partial class EditorViewModel : ObservableObject
{
    /// <summary>Anything larger is not loaded; editing it would stall the UI.</summary>
    private const int MaxEditBytes = 8 * 1024 * 1024;

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(TabState))]
    public partial string? FilePath { get; set; }

    [ObservableProperty]
    public partial string? FileName { get; set; }

    [ObservableProperty]
    public partial string? Message { get; set; }

    [ObservableProperty]
    public partial bool HasFile { get; set; }

    /// <summary>False for oversized and binary files: shown, but not editable.</summary>
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(ShowsEditorStatus))]
    public partial bool CanEdit { get; set; }

    /// <summary>Set when the file is an image the view can draw; the editor is hidden then.</summary>
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(IsBinaryPlaceholder))]
    public partial bool IsImage { get; set; }

    /// <summary>Set for non-image binary files: shown as a placeholder with an "open" action.</summary>
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(IsBinaryPlaceholder))]
    public partial bool IsBinary { get; set; }

    /// <summary>True when the centre shows neither the editor nor an image, only the file message.</summary>
    public bool IsBinaryPlaceholder => IsBinary && !IsImage;

    private static readonly HashSet<string> ImageExtensions = new(StringComparer.OrdinalIgnoreCase)
    {
        ".png", ".jpg", ".jpeg", ".gif", ".bmp", ".ico", ".webp", ".tif", ".tiff", ".svg",
    };

    /// <summary>Extensions that are never text; skipped without reading the file.</summary>
    private static readonly HashSet<string> BinaryExtensions = new(StringComparer.OrdinalIgnoreCase)
    {
        ".exe", ".dll", ".pdb", ".zip", ".7z", ".rar", ".gz", ".tar", ".pdf", ".docx", ".xlsx", ".pptx",
        ".mp3", ".wav", ".flac", ".mp4", ".mkv", ".avi", ".mov", ".webm", ".ttf", ".otf", ".woff", ".woff2",
        ".msix", ".nupkg", ".bin", ".dat", ".db", ".sqlite", ".pfx", ".snk", ".obj", ".lib", ".pri",
    };

    public static bool IsImagePath(string path) => ImageExtensions.Contains(Path.GetExtension(path));

    /// <summary>
    /// Whether this tab is still the replaceable preview. The workspace clears it the
    /// moment the file is claimed by an edit, a save, or a double-click on the header.
    /// </summary>
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(TabState))]
    public partial bool IsPreview { get; set; } = true;

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(TabState))]
    public partial bool IsDirty { get; set; }

    /// <summary>Which tab icon fits: preview, untitled new file, edited, or saved.</summary>
    public string TabState => IsPreview ? "preview" : FilePath is null ? "new" : IsDirty ? "edited" : "saved";

    /// <summary>The text the view loaded into the editor; updated by the view on change.</summary>
    public string? LoadedText { get; private set; }

    /// <summary>
    /// Reads the editor's current content. When set, a save takes its text from here, so a
    /// save that the view did not trigger itself (close-time "Save all") never writes a
    /// stale <see cref="LoadedText"/>.
    /// </summary>
    public Func<string>? CurrentText { get; set; }

    /// <summary>How the file on disk is encoded and ended, restored on save so opening and saving does not rewrite it.</summary>
    private TextFileFormat _format = TextFileFormat.Default;

    /// <summary>The file's write time and size when it was loaded or last saved; a different pair on disk means someone else changed it.</summary>
    private (DateTime WriteUtc, long Length)? _diskStamp;

    /// <summary>Set after a save was refused for an on-disk change; the next save is the user confirming the overwrite.</summary>
    private bool _overwriteArmed;

    /// <summary>The encoding for the status bar, e.g. "UTF-8", "UTF-8 with BOM", "UTF-16 LE".</summary>
    public string EncodingLabel => _format.Encoding.CodePage switch
    {
        65001 => _format.HasBom ? "UTF-8 with BOM" : "UTF-8",
        1200 => "UTF-16 LE",
        1201 => "UTF-16 BE",
        12000 => "UTF-32 LE",
        12001 => "UTF-32 BE",
        _ => _format.Encoding.EncodingName,
    };

    /// <summary>The line ending for the status bar.</summary>
    public string LineEndingLabel => _format.LineEnding switch
    {
        "\r\n" => "CRLF",
        "\n" => "LF",
        _ => "CR",
    };

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(SelectionSummary))]
    [NotifyPropertyChangedFor(nameof(EditorStatusText))]
    [NotifyPropertyChangedFor(nameof(ShowsEditorStatus))]
    public partial int CursorLine { get; set; } = 1;

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(SelectionSummary))]
    [NotifyPropertyChangedFor(nameof(EditorStatusText))]
    public partial int CursorColumn { get; set; } = 1;

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(SelectionSummary))]
    [NotifyPropertyChangedFor(nameof(EditorStatusText))]
    public partial int SelectionLength { get; set; }

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(WordCountText))]
    [NotifyPropertyChangedFor(nameof(EditorStatusText))]
    [NotifyPropertyChangedFor(nameof(ShowsEditorStatus))]
    public partial int WordCount { get; set; }

    /// <summary>False while another centre tab is in front, so the bar reflects the live tab.</summary>
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(ShowsEditorStatus))]
    public partial bool IsTabActive { get; set; }

    /// <summary>True only while the editor tab is in front and showing an editable file.</summary>
    public bool ShowsEditorStatus => HasFile && CanEdit && IsTabActive;

    /// <summary>Cursor position, with the selection length appended while text is selected.</summary>
    public string SelectionSummary => SelectionLength > 0
        ? $"Ln {CursorLine}, Col {CursorColumn} ({SelectionLength} selected)"
        : $"Ln {CursorLine}, Col {CursorColumn}";

    public string WordCountText => $"{WordCount:N0} words";

    /// <summary>Position and word count as one string, so no separator is ever drawn alone.</summary>
    public string EditorStatusText => $"{SelectionSummary} · {WordCountText}";

    /// <summary>The view calls this on every editor selection or cursor move.</summary>
    public void UpdateCursorPosition(int line, int column, int selectionLength)
    {
        CursorLine = line;
        CursorColumn = column;
        SelectionLength = selectionLength;
    }

    public void SetWordCount(int count) => WordCount = count;

    /// <summary>Clears per-file state on load, so the bar never shows the previous file's numbers.</summary>
    private void ResetEditorStatus(string? textForWordCount)
    {
        CursorLine = 1;
        CursorColumn = 1;
        SelectionLength = 0;
        WordCount = CountWords(textForWordCount);
    }

    /// <summary>Closes the buffer: called when the editor tab is closed.</summary>
    public void Close()
    {
        FilePath = null;
        FileName = null;
        Message = null;
        IsDirty = false;
        IsPreview = true;
        HasFile = false;
        CanEdit = false;
        IsImage = false;
        IsBinary = false;
        LoadedText = null;
        IsTabActive = false;
        CurrentText = null;
        ResetFileState();
        ResetEditorStatus(null);
    }

    private void ResetFileState()
    {
        _format = TextFileFormat.Default;
        _diskStamp = null;
        _overwriteArmed = false;
        OnPropertyChanged(nameof(EncodingLabel));
        OnPropertyChanged(nameof(LineEndingLabel));
    }

    /// <summary>Whitespace transitions, no allocations: cheap enough to run debounced per keystroke.</summary>
    public static int CountWords(string? text)
    {
        if (string.IsNullOrEmpty(text))
        {
            return 0;
        }

        var count = 0;
        var inWord = false;

        for (var i = 0; i < text.Length; i++)
        {
            if (char.IsWhiteSpace(text[i]))
            {
                inWord = false;
            }
            else if (!inWord)
            {
                inWord = true;
                count++;
            }
        }

        return count;
    }

    public async Task LoadAsync(string path)
    {
        FilePath = path;
        FileName = Path.GetFileName(path);
        Message = null;
        IsDirty = false;
        LoadedText = null;
        IsImage = false;
        IsBinary = false;
        ResetFileState();
        ResetEditorStatus(null);

        try
        {
            var info = new FileInfo(path);

            if (!info.Exists)
            {
                Message = "File not found.";
                HasFile = false;
                return;
            }

            // Images are drawn by the view whatever their size; the edit guard is for text.
            if (IsImagePath(path))
            {
                HasFile = true;
                CanEdit = false;
                IsImage = true;
                IsBinary = true;
                return;
            }

            if (BinaryExtensions.Contains(Path.GetExtension(path)))
            {
                Message = $"{FormatSize(info.Length)} · binary file; not shown.";
                HasFile = true;
                CanEdit = false;
                IsBinary = true;
                return;
            }

            if (info.Length > MaxEditBytes)
            {
                Message = $"File is {info.Length / 1024 / 1024} MB; too large to edit.";
                HasFile = true;
                CanEdit = false;
                return;
            }

            var sw = System.Diagnostics.Stopwatch.StartNew();

            // Async read: a multi-MB file (or a cold, AV-scanned one) must not freeze
            // the UI thread on every tree or search-result click. The stamp is taken
            // before the read, so a change that lands mid-read is caught on the next save.
            var stamp = (info.LastWriteTimeUtc, info.Length);
            var bytes = await File.ReadAllBytesAsync(path);
            var (text, format) = await Task.Run(() => TextFileCodec.Decode(bytes));

            if (LooksBinary(text))
            {
                Message = $"{FormatSize(info.Length)} · binary file; not shown.";
                HasFile = true;
                CanEdit = false;
                IsBinary = true;
                return;
            }

            LoadedText = text;
            _format = format;
            _diskStamp = stamp;
            OnPropertyChanged(nameof(EncodingLabel));
            OnPropertyChanged(nameof(LineEndingLabel));
            CrashLog.Info("editor", $"loaded {path} ({info.Length} bytes, {EncodingLabel}, {LineEndingLabel}) in {sw.ElapsedMilliseconds} ms");
            HasFile = true;
            CanEdit = true;
            WordCount = CountWords(text);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            CrashLog.Error("editor", $"load failed: {path}", ex);
            Message = ex.Message;
            HasFile = false;
        }
    }

    /// <summary>The view calls this on every editor keystroke.</summary>
    public void MarkDirty() => IsDirty = true;

    /// <summary>The view calls this with the current editor content.</summary>
    public void SetText(string text)
    {
        LoadedText = text;
        IsDirty = true;
    }

    /// <summary>
    /// Writes the buffer back in the encoding and line ending it was read in. Refuses once
    /// when the file changed on disk since it was opened (an agent or another editor wrote
    /// to it): the message says so, and saving again overwrites. True when the file was
    /// written.
    /// </summary>
    public event EventHandler? Saved;

    public async Task<bool> SaveAsync()
    {
        if (CurrentText is { } provider && FilePath is not null && CanEdit)
        {
            LoadedText = provider();
        }

        if (FilePath is not { } path || LoadedText is not { } text || !CanEdit)
        {
            return false;
        }

        try
        {
            if (!_overwriteArmed && ChangedOnDisk(path))
            {
                _overwriteArmed = true;
                Message = "This file changed on disk since you opened it. Save again to overwrite it with your version.";
                CrashLog.Info("editor", $"save held back, changed on disk: {path}");
                return false;
            }

            var format = _format;
            var bytes = TextFileCodec.Encode(text, ref format);
            _format = format;

            await Task.Run(() => WriteAtomically(path, bytes));
            var written = new FileInfo(path);
            _diskStamp = (written.LastWriteTimeUtc, written.Length);
            _overwriteArmed = false;

            CrashLog.Info("editor", $"saved {path} ({text.Length} chars, {EncodingLabel}, {LineEndingLabel})");
            IsDirty = false;
            OnPropertyChanged(nameof(EncodingLabel));
            Saved?.Invoke(this, EventArgs.Empty);

            // Stays silent on success: the tab glyph already shows saved state, and a
            // banner over the text was noise after every save. Only failures speak.
            Message = null;
            return true;
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            CrashLog.Error("editor", $"save failed: {path}", ex);
            Message = $"Save failed: {ex.Message}";
            return false;
        }
    }

    private bool ChangedOnDisk(string path)
    {
        if (_diskStamp is not { } stamp)
        {
            return false;
        }

        var now = new FileInfo(path);
        return now.Exists && (now.LastWriteTimeUtc != stamp.WriteUtc || now.Length != stamp.Length);
    }

    /// <summary>
    /// Temp file plus replace, so a crash or a full disk mid-write leaves the old file
    /// intact instead of a truncated one. A symlinked file, or one another process holds
    /// open without share-delete, is written in place instead.
    /// </summary>
    private static void WriteAtomically(string path, byte[] bytes)
    {
        var existing = new FileInfo(path);
        if (existing.Exists && existing.LinkTarget is not null)
        {
            File.WriteAllBytes(path, bytes);
            return;
        }

        var temp = Path.Combine(Path.GetDirectoryName(path)!, $".{Path.GetFileName(path)}.{Guid.NewGuid():N}.tmp");
        try
        {
            File.WriteAllBytes(temp, bytes);
            if (existing.Exists)
            {
                File.Replace(temp, path, destinationBackupFileName: null);
            }
            else
            {
                File.Move(temp, path);
            }
        }
        catch (IOException) when (existing.Exists)
        {
            TryDelete(temp);
            File.WriteAllBytes(path, bytes);
        }
        catch
        {
            TryDelete(temp);
            throw;
        }
    }

    private static void TryDelete(string path)
    {
        try
        {
            File.Delete(path);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            // A stray temp file is harmless.
        }
    }

    private static string FormatSize(long bytes) => bytes switch
    {
        < 1024 => $"{bytes} B",
        < 1024 * 1024 => $"{bytes / 1024.0:0.#} KB",
        _ => $"{bytes / 1024.0 / 1024.0:0.#} MB",
    };

    /// <summary>A NUL byte in the first chunk is the usual heuristic for "not text".</summary>
    private static bool LooksBinary(string text)
    {
        var limit = Math.Min(text.Length, 8000);

        for (var i = 0; i < limit; i++)
        {
            if (text[i] == '\0')
            {
                return true;
            }
        }

        return false;
    }
}
