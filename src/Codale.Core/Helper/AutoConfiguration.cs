using System.Text.RegularExpressions;

namespace Codale.Core.Helper;

/// <summary>Only unambiguous, whole-message Codale tier commands are applied by the host.</summary>
public static class AutoConfiguration
{
    public static string? Tier(string text)
    {
        var match = Regex.Match(text.Trim(),
            @"^(?:please\s+)?(?:(?:set|switch|change)\s+(?:(?:this chat|codale|my|the)(?:'s)?\s+)?(?:model\s+)?tier\s+(?:to\s+)?(?<tier>lite|flash|pro|plus|cli)|use\s+(?<tier>lite|flash|pro|plus|cli)\s+(?:tier|model)(?:\s+for this chat)?)\s*[.!]?$",
            RegexOptions.IgnoreCase);
        return match.Success ? match.Groups["tier"].Value.ToLowerInvariant() : null;
    }
}
