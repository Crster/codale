using System.ComponentModel;
using Microsoft.UI;
using Microsoft.UI.Xaml.Media;
using Windows.UI;

namespace Codale.Browser;

/// <summary>A row the list views bind to. <see cref="Changed"/> tells the bindings to read the row again.</summary>
public abstract class DiagnosticsRow : INotifyPropertyChanged
{
    public event PropertyChangedEventHandler? PropertyChanged;

    public void Changed() => PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(string.Empty));

    protected static SolidColorBrush Solid(uint rgb) => new(Color.FromArgb(255, (byte)(rgb >> 16), (byte)(rgb >> 8), (byte)rgb));

    protected static SolidColorBrush Tint(uint rgb, byte alpha) => new(Color.FromArgb(alpha, (byte)(rgb >> 16), (byte)(rgb >> 8), (byte)rgb));
}

/// <summary>One request on the Requests list. Fed from the DevTools Network domain.</summary>
public sealed class NetworkRow : DiagnosticsRow
{
    public NetworkRow(string id) => Id = id;

    public string Id { get; }
    public string Url { get; set; } = "";
    /// <summary>The URL shown in the list: the path alone when it is on the page's own origin.</summary>
    public string PathText { get; set; } = "";
    public string Method { get; set; } = "";
    public string Type { get; set; } = "";
    public string Mime { get; set; } = "";
    public string Status { get; set; } = "";
    public string Error { get; set; } = "";
    public string RequestHeaders { get; set; } = "";
    public string ResponseHeaders { get; set; } = "";
    public string RequestBody { get; set; } = "";
    public string ResponseBody { get; set; } = "";
    public double Start { get; set; }
    public double End { get; set; }
    public long? Size { get; set; }
    public bool Finished { get; set; }
    public bool BodyLoaded { get; set; }
    public bool IsRedirect { get; set; }

    public string StatusText => Error.Length > 0 ? "Failed" : Status.Length > 0 ? Status : "Pending";
    public Brush StatusBrush => Solid(StatusHue);
    public Brush StatusTint => Tint(StatusHue, 0x30);
    public string TypeText => Mime.Length > 0 ? Mime : Type;
    public Brush TypeBrush => Solid(ResourceHue);
    public string TimeText => Finished && End >= Start ? $"{(End - Start) * 1000:0} ms" : "…";
    public string SizeText => Size is { } bytes ? FormatSize(bytes) : "—";
    public Brush PathBrush => Error.Length > 0 ? Solid(0xF43F5E) : Solid(0x9CA3AF);
    public string SearchText => $"{Status} {Error} {Method} {Url} {Mime} {Type}";

    /// <summary>The category the Requests chips group by.</summary>
    public string Kind => Type switch
    {
        "Document" => "Doc",
        "Script" => "JS",
        "Stylesheet" => "CSS",
        "Image" => "Img",
        "Font" => "Font",
        "XHR" or "Fetch" => "XHR",
        _ => "Other",
    };

    public bool Failed => Error.Length > 0 || (int.TryParse(Status, out var code) && code >= 400);

    private uint StatusHue => Error.Length > 0 ? 0xF43F5Eu
        : !int.TryParse(Status, out var code) ? 0x0EA5E9u
        : code >= 400 ? 0xF43F5Eu
        : code >= 300 ? 0xF59E0Bu
        : 0x10B981u;

    private uint ResourceHue
    {
        get
        {
            var text = $"{Type} {Mime}".ToLowerInvariant();
            if (text.Contains("javascript") || text.Contains("script")) return 0xF59E0B;
            if (text.Contains("css") || text.Contains("stylesheet")) return 0xA78BFA;
            if (text.Contains("image")) return 0x10B981;
            if (text.Contains("json") || text.Contains("fetch") || text.Contains("xhr")) return 0x0EA5E9;
            if (text.Contains("font")) return 0xFB923C;
            if (text.Contains("html") || text.Contains("document")) return 0x818CF8;
            return 0x94A3B8;
        }
    }

    private static string FormatSize(long bytes) => bytes < 1024 ? $"{bytes} B" : bytes < 1024 * 1024 ? $"{bytes / 1024d:0.0} KB" : $"{bytes / 1048576d:0.0} MB";
}

/// <summary>One console message (or uncaught exception) from the page.</summary>
public sealed class ConsoleRow : DiagnosticsRow
{
    public ConsoleRow(string level, string message, DateTime time)
    {
        Level = level;
        Message = message;
        Time = time;
    }

    public string Level { get; }
    public string Message { get; }
    public DateTime Time { get; }

    public string TimeText => Time.ToString("HH:mm:ss.fff");
    public string LevelText => Level switch
    {
        "warning" => "Warn",
        "assert" => "Assert",
        "" => "Log",
        _ => char.ToUpperInvariant(Level[0]) + Level[1..],
    };
    public string FirstLine => Message.ReplaceLineEndings(" ⏎ ");
    /// <summary>Where the message was logged from (url:line:column), or empty when the page did not say.</summary>
    public string Source { get; init; } = "";
    public string DetailText => $"{LevelText} · {TimeText}" + (Source.Length > 0 ? Environment.NewLine + Source : "") + Environment.NewLine + Environment.NewLine + Message;
    public string SearchText => $"{Level} {Message}";

    public Brush LevelBrush => Solid(LevelHue);
    public Brush RowTint => Level switch
    {
        "error" or "assert" => Tint(0xF43F5E, 0x14),
        "warning" => Tint(0xF59E0B, 0x14),
        _ => Tint(0x000000, 0x00),
    };

    private uint LevelHue => Level switch
    {
        "error" or "assert" => 0xF43F5E,
        "warning" => 0xF59E0B,
        "info" => 0x0EA5E9,
        _ => 0x94A3B8,
    };
}
