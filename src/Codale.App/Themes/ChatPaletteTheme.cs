using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Media;

namespace Codale.App;

/// <summary>
/// The ChatPalette brushes are shared SolidColorBrush instances in Application.Resources,
/// resolved at parse time and by converters that cannot use ThemeResource (they read the
/// app-level dictionary, which does not follow a window's theme override). Switching the
/// app theme therefore re-colours those instances in place: every consumer - StaticResource,
/// ThemeResource or converter - sees the new values live without recreating anything.
/// </summary>
/// <remarks>
/// The XAML palette ships the dark values, which is also the app default; this class only
/// flips between the two fixed sets, so the maps must stay key-complete mirror images.
/// Tint and surface brushes that are hue-alpha over whatever background do not need a
/// light variant; only white-alpha surfaces, text and light foreground hues do.
/// </remarks>
public static class ChatPaletteTheme
{
    private static bool _isLight;

    /// <summary>True while the resolved app theme is Light.</summary>
    public static bool IsLight => _isLight;

    /// <summary>
    /// A resource for code that cannot use ThemeResource. Application.Resources[key] hands
    /// back the app-level theme's half of the Fluent theme dictionaries (dark, white text)
    /// even when a window is overridden to Light, so look in the matching half first.
    /// </summary>
    public static object Resource(string key)
    {
        var resources = Application.Current.Resources;
        var themeKey = _isLight ? "Light" : "Default";
        if (FindThemed(resources, themeKey, key) is { } themed)
        {
            return themed;
        }

        return resources[key];
    }

    /// <summary><see cref="Resource"/> for brushes.</summary>
    public static Brush Brush(string key) => (Brush)Resource(key);

    /// <summary><see cref="Resource"/> that reports a missing key instead of throwing.</summary>
    public static bool TryGetBrush(string key, out Brush brush)
    {
        try
        {
            if (Resource(key) is Brush found)
            {
                brush = found;
                return true;
            }
        }
        catch (Exception)
        {
            // Not defined anywhere; the caller falls back.
        }

        brush = null!;
        return false;
    }

    private static object? FindThemed(ResourceDictionary dictionary, string themeKey, string key)
    {
        if (dictionary.ThemeDictionaries.TryGetValue(themeKey, out var theme)
            && theme is ResourceDictionary themed
            && themed.TryGetValue(key, out var value))
        {
            return value;
        }

        foreach (var merged in dictionary.MergedDictionaries)
        {
            if (FindThemed(merged, themeKey, key) is { } found)
            {
                return found;
            }
        }

        return null;
    }

    /// <summary>Re-colours the palette brushes for the given resolved theme. Cheap; safe to call repeatedly.</summary>
    public static void Apply(bool light)
    {
        Controls.CodePalette.Light = light;
        if (_isLight == light)
        {
            return;
        }

        _isLight = light;
        try
        {
            var resources = Application.Current.Resources;
            var map = light ? LightColors : DarkColors;
            foreach (var (key, color) in map)
            {
                if (resources[key] is SolidColorBrush brush)
                {
                    brush.Color = color;
                }
            }
        }
        catch (Exception ex)
        {
            // A palette problem must never take the launch down with it.
            CrashLog.Write("Theme", "palette apply failed", ex);
        }
    }

    /// <summary>Parses "#RRGGBB" (the XAML palette's RGB form, opaque) and "#AARRGGBB" alike.</summary>
    private static Windows.UI.Color C(string hex) => hex.Length == 9
        ? Windows.UI.Color.FromArgb(
            Convert.ToByte(hex[1..3], 16),
            Convert.ToByte(hex[3..5], 16),
            Convert.ToByte(hex[5..7], 16),
            Convert.ToByte(hex[7..9], 16))
        : Windows.UI.Color.FromArgb(
            0xFF,
            Convert.ToByte(hex[1..3], 16),
            Convert.ToByte(hex[3..5], 16),
            Convert.ToByte(hex[5..7], 16));

    private static readonly Dictionary<string, Windows.UI.Color> DarkColors = new()
    {
        ["KindReadBrush"] = C("#38BDF8"),
        ["KindReadTintBrush"] = C("#2638BDF8"),
        ["KindSearchBrush"] = C("#2DD4BF"),
        ["KindSearchTintBrush"] = C("#262DD4BF"),
        ["KindEditBrush"] = C("#FBBF24"),
        ["KindEditTintBrush"] = C("#26FBBF24"),
        ["KindWriteBrush"] = C("#34D399"),
        ["KindWriteTintBrush"] = C("#2634D399"),
        ["KindShellBrush"] = C("#A78BFA"),
        ["KindShellTintBrush"] = C("#26A78BFA"),
        ["KindWebBrush"] = C("#F472B6"),
        ["KindWebTintBrush"] = C("#26F472B6"),
        ["KindAgentBrush"] = C("#FB923C"),
        ["KindAgentTintBrush"] = C("#26FB923C"),
        ["KindAskBrush"] = C("#818CF8"),
        ["KindAskTintBrush"] = C("#26818CF8"),
        ["KindPlanBrush"] = C("#A78BFA"),
        ["KindPlanTintBrush"] = C("#26A78BFA"),
        ["KindTodoBrush"] = C("#38BDF8"),
        ["KindTodoTintBrush"] = C("#2638BDF8"),
        ["KindOtherBrush"] = C("#94A3B8"),
        ["KindOtherTintBrush"] = C("#1E94A3B8"),
        ["StatusRunningBrush"] = C("#38BDF8"),
        ["StatusRunningTintBrush"] = C("#2438BDF8"),
        ["StatusSuccessBrush"] = C("#34D399"),
        ["StatusSuccessTintBrush"] = C("#2434D399"),
        ["StatusFailedBrush"] = C("#FB7185"),
        ["StatusFailedTintBrush"] = C("#24FB7185"),
        ["StatusWarnBrush"] = C("#FBBF24"),
        ["StatusWarnTintBrush"] = C("#24FBBF24"),
        ["StatusMutedBrush"] = C("#94A3B8"),
        ["DiffAddedBackgroundBrush"] = C("#2410B981"),
        ["DiffRemovedBackgroundBrush"] = C("#28F43F5E"),
        ["DiffAddedForegroundBrush"] = C("#6EE7B7"),
        ["DiffRemovedForegroundBrush"] = C("#FDA4AF"),
        ["DiffContextForegroundBrush"] = C("#C8FFFFFF"),
        ["DiffGutterBrush"] = C("#60FFFFFF"),
        ["DiffHunkHeaderBrush"] = C("#99818CF8"),
        ["TimelineRailBrush"] = C("#22FFFFFF"),
        ["StepHoverBrush"] = C("#12FFFFFF"),
        ["DetailSurfaceBrush"] = C("#0DFFFFFF"),
        ["DetailStrokeBrush"] = C("#1AFFFFFF"),
        ["CodeSurfaceBrush"] = C("#3D000000"),
        ["CodeHeaderSurfaceBrush"] = C("#55000000"),
        ["UserBubbleBrush"] = C("#2B6366F1"),
        ["UserBubbleStrokeBrush"] = C("#4D6366F1"),
        ["AssistantCardBrush"] = C("#06FFFFFF"),
        ["AssistantCardStrokeBrush"] = C("#12FFFFFF"),
        ["StarterCardBrush"] = C("#0DFFFFFF"),
        ["StarterCardStrokeBrush"] = C("#1AFFFFFF"),
        ["PillBackgroundBrush"] = C("#14FFFFFF"),
        ["TodoTextBrush"] = C("#F1F5F9"),
        ["TodoDoneTextBrush"] = C("#64748B"),
        ["MdBodyBrush"] = C("#E2E8F0"),
        ["MdStrongBrush"] = C("#FFFFFF"),
        ["MdHeading1Brush"] = C("#A5B4FC"),
        ["MdHeading2Brush"] = C("#93C5FD"),
        ["MdHeading3Brush"] = C("#6EE7B7"),
        ["MdCodeBrush"] = C("#FCD34D"),
        ["MdBulletBrush"] = C("#A5B4FC"),
        ["MdChangeAddedBrush"] = C("#6EE7B7"),
        ["MdChangeEditedBrush"] = C("#FCD34D"),
        ["MdChangeDroppedBrush"] = C("#FDA4AF"),
        ["AskSurfaceBrush"] = C("#18818CF8"),
        ["AskStrokeBrush"] = C("#66818CF8"),
        ["OptionSelectedBrush"] = C("#33818CF8"),
        ["OptionHoverBrush"] = C("#14FFFFFF"),
        ["ApprovalSurfaceBrush"] = C("#18FBBF24"),
        ["ApprovalStrokeBrush"] = C("#66FBBF24"),
    };

    private static readonly Dictionary<string, Windows.UI.Color> LightColors = new()
    {
        // Kinds: the dark hues are too pale for white text runs, so the foregrounds
        // darken; the ~14% tint washes stay as they are - over light they read pastel.
        ["KindReadBrush"] = C("#0284C7"),
        ["KindReadTintBrush"] = C("#2638BDF8"),
        ["KindSearchBrush"] = C("#0D9488"),
        ["KindSearchTintBrush"] = C("#262DD4BF"),
        ["KindEditBrush"] = C("#D97706"),
        ["KindEditTintBrush"] = C("#26FBBF24"),
        ["KindWriteBrush"] = C("#059669"),
        ["KindWriteTintBrush"] = C("#2634D399"),
        ["KindShellBrush"] = C("#7C3AED"),
        ["KindShellTintBrush"] = C("#26A78BFA"),
        ["KindWebBrush"] = C("#DB2777"),
        ["KindWebTintBrush"] = C("#26F472B6"),
        ["KindAgentBrush"] = C("#EA580C"),
        ["KindAgentTintBrush"] = C("#26FB923C"),
        ["KindAskBrush"] = C("#4F46E5"),
        ["KindAskTintBrush"] = C("#26818CF8"),
        ["KindPlanBrush"] = C("#6D28D9"),
        ["KindPlanTintBrush"] = C("#26A78BFA"),
        ["KindTodoBrush"] = C("#0284C7"),
        ["KindTodoTintBrush"] = C("#2638BDF8"),
        ["KindOtherBrush"] = C("#64748B"),
        ["KindOtherTintBrush"] = C("#1E94A3B8"),
        ["StatusRunningBrush"] = C("#0284C7"),
        ["StatusRunningTintBrush"] = C("#2438BDF8"),
        ["StatusSuccessBrush"] = C("#059669"),
        ["StatusSuccessTintBrush"] = C("#2434D399"),
        ["StatusFailedBrush"] = C("#E11D48"),
        ["StatusFailedTintBrush"] = C("#24FB7185"),
        ["StatusWarnBrush"] = C("#D97706"),
        ["StatusWarnTintBrush"] = C("#24FBBF24"),
        ["StatusMutedBrush"] = C("#64748B"),
        ["DiffAddedBackgroundBrush"] = C("#2E10B981"),
        ["DiffRemovedBackgroundBrush"] = C("#2EF43F5E"),
        ["DiffAddedForegroundBrush"] = C("#059669"),
        ["DiffRemovedForegroundBrush"] = C("#E11D48"),
        // White-alpha surfaces flip to black-alpha; white text flips to slate.
        ["DiffContextForegroundBrush"] = C("#B4334155"),
        ["DiffGutterBrush"] = C("#5C334155"),
        ["DiffHunkHeaderBrush"] = C("#8C4F46E5"),
        ["TimelineRailBrush"] = C("#1F000000"),
        ["StepHoverBrush"] = C("#0D000000"),
        ["DetailSurfaceBrush"] = C("#0A000000"),
        ["DetailStrokeBrush"] = C("#14000000"),
        ["CodeSurfaceBrush"] = C("#14000000"),
        ["CodeHeaderSurfaceBrush"] = C("#20000000"),
        ["UserBubbleBrush"] = C("#246366F1"),
        ["UserBubbleStrokeBrush"] = C("#446366F1"),
        ["AssistantCardBrush"] = C("#06000000"),
        ["AssistantCardStrokeBrush"] = C("#12000000"),
        ["StarterCardBrush"] = C("#0A000000"),
        ["StarterCardStrokeBrush"] = C("#16000000"),
        ["PillBackgroundBrush"] = C("#0F000000"),
        ["TodoTextBrush"] = C("#1E293B"),
        ["TodoDoneTextBrush"] = C("#64748B"),
        ["MdBodyBrush"] = C("#1E293B"),
        ["MdStrongBrush"] = C("#0B1220"),
        ["MdHeading1Brush"] = C("#1D4ED8"),
        ["MdHeading2Brush"] = C("#0369A1"),
        ["MdHeading3Brush"] = C("#0F766E"),
        ["MdCodeBrush"] = C("#B45309"),
        ["MdBulletBrush"] = C("#4F46E5"),
        ["MdChangeAddedBrush"] = C("#0F766E"),
        ["MdChangeEditedBrush"] = C("#B45309"),
        ["MdChangeDroppedBrush"] = C("#DC2626"),
        ["AskSurfaceBrush"] = C("#14818CF8"),
        ["AskStrokeBrush"] = C("#59818CF8"),
        ["OptionSelectedBrush"] = C("#29818CF8"),
        ["OptionHoverBrush"] = C("#0F000000"),
        ["ApprovalSurfaceBrush"] = C("#12F5B94A"),
        ["ApprovalStrokeBrush"] = C("#59F5B94A"),
    };
}