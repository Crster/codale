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

    /// <summary>Re-colours the palette brushes for the given resolved theme. Cheap; safe to call repeatedly.</summary>
    public static void Apply(bool light)
    {
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
        ["KindReadBrush"] = C("#5AA9F8"),
        ["KindReadTintBrush"] = C("#245AA9F8"),
        ["KindSearchBrush"] = C("#2DD4BF"),
        ["KindSearchTintBrush"] = C("#242DD4BF"),
        ["KindEditBrush"] = C("#F5B94A"),
        ["KindEditTintBrush"] = C("#24F5B94A"),
        ["KindWriteBrush"] = C("#4CC38A"),
        ["KindWriteTintBrush"] = C("#244CC38A"),
        ["KindShellBrush"] = C("#A78BFA"),
        ["KindShellTintBrush"] = C("#24A78BFA"),
        ["KindWebBrush"] = C("#F472B6"),
        ["KindWebTintBrush"] = C("#24F472B6"),
        ["KindAgentBrush"] = C("#FB923C"),
        ["KindAgentTintBrush"] = C("#24FB923C"),
        ["KindAskBrush"] = C("#818CF8"),
        ["KindAskTintBrush"] = C("#24818CF8"),
        ["KindPlanBrush"] = C("#818CF8"),
        ["KindPlanTintBrush"] = C("#24818CF8"),
        ["KindTodoBrush"] = C("#94A3B8"),
        ["KindTodoTintBrush"] = C("#1F94A3B8"),
        ["KindOtherBrush"] = C("#A1A1AA"),
        ["KindOtherTintBrush"] = C("#1FA1A1AA"),
        ["StatusRunningBrush"] = C("#60A5FA"),
        ["StatusSuccessBrush"] = C("#4CC38A"),
        ["StatusFailedBrush"] = C("#F87171"),
        ["StatusWarnBrush"] = C("#F5B94A"),
        ["StatusMutedBrush"] = C("#8B8B93"),
        ["DiffAddedBackgroundBrush"] = C("#1F3FB950"),
        ["DiffRemovedBackgroundBrush"] = C("#24F85149"),
        ["DiffAddedForegroundBrush"] = C("#7EE2A8"),
        ["DiffRemovedForegroundBrush"] = C("#FF8A80"),
        ["DiffContextForegroundBrush"] = C("#B4FFFFFF"),
        ["DiffGutterBrush"] = C("#5CFFFFFF"),
        ["DiffHunkHeaderBrush"] = C("#8C818CF8"),
        ["TimelineRailBrush"] = C("#1FFFFFFF"),
        ["StepHoverBrush"] = C("#0DFFFFFF"),
        ["DetailSurfaceBrush"] = C("#0AFFFFFF"),
        ["DetailStrokeBrush"] = C("#14FFFFFF"),
        ["CodeSurfaceBrush"] = C("#33000000"),
        ["UserBubbleBrush"] = C("#2E6D7CF7"),
        ["UserBubbleStrokeBrush"] = C("#406D7CF7"),
        ["PillBackgroundBrush"] = C("#0FFFFFFF"),
        ["TodoTextBrush"] = C("#E6FFFFFF"),
        ["TodoDoneTextBrush"] = C("#73FFFFFF"),
        ["MdBodyBrush"] = C("#D4D7DF"),
        ["MdStrongBrush"] = C("#FFFFFF"),
        ["MdHeading1Brush"] = C("#9DB8FF"),
        ["MdHeading2Brush"] = C("#7CC4FA"),
        ["MdHeading3Brush"] = C("#6EDDC8"),
        ["MdCodeBrush"] = C("#F2C078"),
        ["MdBulletBrush"] = C("#8B97F8"),
        ["AskSurfaceBrush"] = C("#14818CF8"),
        ["AskStrokeBrush"] = C("#59818CF8"),
        ["OptionSelectedBrush"] = C("#29818CF8"),
        ["OptionHoverBrush"] = C("#0FFFFFFF"),
        ["ApprovalSurfaceBrush"] = C("#12F5B94A"),
        ["ApprovalStrokeBrush"] = C("#59F5B94A"),
    };

    private static readonly Dictionary<string, Windows.UI.Color> LightColors = new()
    {
        // Kinds: the dark hues are too pale for white text runs, so the foregrounds
        // darken; the ~14% tint washes stay as they are - over light they read pastel.
        ["KindReadBrush"] = C("#2563EB"),
        ["KindReadTintBrush"] = C("#245AA9F8"),
        ["KindSearchBrush"] = C("#0D9488"),
        ["KindSearchTintBrush"] = C("#242DD4BF"),
        ["KindEditBrush"] = C("#B45309"),
        ["KindEditTintBrush"] = C("#24F5B94A"),
        ["KindWriteBrush"] = C("#15803D"),
        ["KindWriteTintBrush"] = C("#244CC38A"),
        ["KindShellBrush"] = C("#7C3AED"),
        ["KindShellTintBrush"] = C("#24A78BFA"),
        ["KindWebBrush"] = C("#DB2777"),
        ["KindWebTintBrush"] = C("#24F472B6"),
        ["KindAgentBrush"] = C("#EA580C"),
        ["KindAgentTintBrush"] = C("#24FB923C"),
        ["KindAskBrush"] = C("#4F46E5"),
        ["KindAskTintBrush"] = C("#24818CF8"),
        ["KindPlanBrush"] = C("#4F46E5"),
        ["KindPlanTintBrush"] = C("#24818CF8"),
        ["KindTodoBrush"] = C("#475569"),
        ["KindTodoTintBrush"] = C("#1F94A3B8"),
        ["KindOtherBrush"] = C("#52525B"),
        ["KindOtherTintBrush"] = C("#1FA1A1AA"),
        ["StatusRunningBrush"] = C("#2563EB"),
        ["StatusSuccessBrush"] = C("#15803D"),
        ["StatusFailedBrush"] = C("#DC2626"),
        ["StatusWarnBrush"] = C("#B45309"),
        ["StatusMutedBrush"] = C("#6B7280"),
        ["DiffAddedBackgroundBrush"] = C("#2E3FB950"),
        ["DiffRemovedBackgroundBrush"] = C("#2EF85149"),
        ["DiffAddedForegroundBrush"] = C("#15803D"),
        ["DiffRemovedForegroundBrush"] = C("#DC2626"),
        // White-alpha surfaces flip to black-alpha; white text flips to slate.
        ["DiffContextForegroundBrush"] = C("#B4334155"),
        ["DiffGutterBrush"] = C("#5C334155"),
        ["DiffHunkHeaderBrush"] = C("#8C4F46E5"),
        ["TimelineRailBrush"] = C("#1F000000"),
        ["StepHoverBrush"] = C("#0D000000"),
        ["DetailSurfaceBrush"] = C("#0A000000"),
        ["DetailStrokeBrush"] = C("#14000000"),
        ["CodeSurfaceBrush"] = C("#14000000"),
        ["UserBubbleBrush"] = C("#2E6D7CF7"),
        ["UserBubbleStrokeBrush"] = C("#406D7CF7"),
        ["PillBackgroundBrush"] = C("#0F000000"),
        ["TodoTextBrush"] = C("#E61E293B"),
        ["TodoDoneTextBrush"] = C("#73334155"),
        ["MdBodyBrush"] = C("#1E293B"),
        ["MdStrongBrush"] = C("#0B1220"),
        ["MdHeading1Brush"] = C("#1D4ED8"),
        ["MdHeading2Brush"] = C("#0369A1"),
        ["MdHeading3Brush"] = C("#0F766E"),
        ["MdCodeBrush"] = C("#B45309"),
        ["MdBulletBrush"] = C("#4F46E5"),
        ["AskSurfaceBrush"] = C("#14818CF8"),
        ["AskStrokeBrush"] = C("#59818CF8"),
        ["OptionSelectedBrush"] = C("#29818CF8"),
        ["OptionHoverBrush"] = C("#0F000000"),
        ["ApprovalSurfaceBrush"] = C("#12F5B94A"),
        ["ApprovalStrokeBrush"] = C("#59F5B94A"),
    };
}
