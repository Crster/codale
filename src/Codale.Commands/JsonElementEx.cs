using System.Text.Json;

namespace Codale.Commands;

/// <summary>Tolerant accessors over config files Codale does not control (see JsonEx's rationale).</summary>
internal static class JsonElementEx
{
    public static JsonElement Prop(this JsonElement e, string name) =>
        e.ValueKind == JsonValueKind.Object && e.TryGetProperty(name, out var v) ? v : default;

    public static string? Str(this JsonElement e, params string[] names)
    {
        foreach (var name in names)
        {
            if (e.Prop(name) is { ValueKind: JsonValueKind.String } v && v.GetString() is { } s)
            {
                return s;
            }
        }

        return null;
    }

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

    public static IEnumerable<JsonElement> Items(this JsonElement e, params string[] names)
    {
        foreach (var name in names)
        {
            if (e.Prop(name) is { ValueKind: JsonValueKind.Array } arr)
            {
                return arr.EnumerateArray();
            }
        }

        return [];
    }
}