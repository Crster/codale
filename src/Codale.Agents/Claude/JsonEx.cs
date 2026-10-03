using System.Globalization;
using System.Text.Json;

namespace Codale.Agents.Claude;

/// <summary>
/// Tolerant accessors for a wire format we do not control. Every field is treated
/// as optional: an undocumented CLI will add, rename and drop properties between
/// versions, and a missing field must never take the chat panel down.
/// </summary>
internal static class JsonEx
{
    public static JsonElement? Prop(this JsonElement e, string name) =>
        e.ValueKind == JsonValueKind.Object && e.TryGetProperty(name, out var v) ? v : null;

    public static string? Str(this JsonElement e, string name) =>
        e.Prop(name) is { ValueKind: JsonValueKind.String } v ? v.GetString() : null;

    public static int? Int(this JsonElement e, string name) =>
        e.Prop(name) is { ValueKind: JsonValueKind.Number } v && v.TryGetInt32(out var i) ? i : null;

    public static double? Dbl(this JsonElement e, string name) =>
        e.Prop(name) is { ValueKind: JsonValueKind.Number } v && v.TryGetDouble(out var d) ? d : null;

    public static decimal? Dec(this JsonElement e, string name) =>
        e.Prop(name) is { ValueKind: JsonValueKind.Number } v && v.TryGetDecimal(out var d) ? d : null;

    public static bool Bool(this JsonElement e, string name) =>
        e.Prop(name) is { ValueKind: JsonValueKind.True };

    public static IReadOnlyList<string> StrArray(this JsonElement e, string name)
    {
        if (e.Prop(name) is not { ValueKind: JsonValueKind.Array } arr)
        {
            return [];
        }

        var list = new List<string>(arr.GetArrayLength());
        foreach (var item in arr.EnumerateArray())
        {
            if (item.ValueKind == JsonValueKind.String && item.GetString() is { } s)
            {
                list.Add(s);
            }
        }

        return list;
    }

    public static IEnumerable<JsonElement> Items(this JsonElement e, string name) =>
        e.Prop(name) is { ValueKind: JsonValueKind.Array } arr ? arr.EnumerateArray() : [];

    /// <summary>Epoch seconds as used by <c>rate_limit_event</c>.</summary>
    public static DateTimeOffset? EpochSeconds(this JsonElement e, string name) =>
        e.Prop(name) is { ValueKind: JsonValueKind.Number } v && v.TryGetInt64(out var secs)
            ? DateTimeOffset.FromUnixTimeSeconds(secs)
            : null;

    public static DateTimeOffset? Timestamp(this JsonElement e, string name) =>
        e.Str(name) is { } s && DateTimeOffset.TryParse(s, CultureInfo.InvariantCulture, DateTimeStyles.None, out var ts) ? ts : null;
}
