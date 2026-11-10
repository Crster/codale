using System.Text;
using System.Text.RegularExpressions;

namespace Codale.Smart;

/// <summary>
/// The task brief the local model writes for the agent CLI: what the request is, where the change belongs and the facts
/// it must respect. A small model can invent a path or line, so nothing survives unless its citation resolves to real
/// source: lines that cite nothing, or something that does not exist, are dropped, and a brief with no verified
/// location is discarded for the deterministic context.
/// </summary>
public static partial class SmartBrief
{
    public const int MaxTaskChars = 220;
    public const int MaxLineChars = 260;

    public const string Instructions =
        "Write a brief for a coding agent, from the provided material only. Output exactly this layout and nothing else:\n" +
        "Task: <one sentence: what the request needs, as a change, an answer or a plan>\n" +
        "Where:\n- <path>:<firstLine>-<lastLine> — <what is there and what must change>\n" +
        "Facts:\n- <signature, caller, convention or constraint the work must respect> (<path>:<line>)\n" +
        "Rules: at most 4 Where lines and 4 Facts lines. Every line cites a path and line numbers that appear in the material. " +
        "Never invent a path or a line. Do not restate code; do not do the work. Do not call tools unless a location you need is missing.";

    [GeneratedRegex(@"(?<path>[\w@.\-/\\]+\.\w+):(?<start>\d+)(?:-(?<end>\d+))?")]
    private static partial Regex Citation();

    /// <summary>The brief with only verified lines, or null when no <c>Where</c> line survives.</summary>
    public static string? Validate(string? text, string worktree)
    {
        if (string.IsNullOrWhiteSpace(text)) return null;
        var root = Path.GetFullPath(worktree);
        var task = "";
        var where = new List<string>();
        var facts = new List<string>();
        var section = "";
        var lineCounts = new Dictionary<string, int>(StringComparer.OrdinalIgnoreCase);
        foreach (var raw in text.ReplaceLineEndings("\n").Split('\n'))
        {
            var line = raw.Trim();
            if (line.Length == 0) continue;
            if (line.StartsWith("Task:", StringComparison.OrdinalIgnoreCase)) { task = Clip(line[5..].Trim(), MaxTaskChars); section = ""; continue; }
            if (line.Equals("Where:", StringComparison.OrdinalIgnoreCase)) { section = "where"; continue; }
            if (line.Equals("Facts:", StringComparison.OrdinalIgnoreCase)) { section = "facts"; continue; }
            if (section.Length == 0 || !line.StartsWith("- ", StringComparison.Ordinal)) continue;
            var target = section == "where" ? where : facts;
            if (target.Count >= 4 || !Resolves(line, root, lineCounts)) continue;
            target.Add(Clip(line, MaxLineChars));
        }
        if (where.Count == 0 || task.Length == 0) return null;
        var result = new StringBuilder("Task: " + task + "\nWhere:\n" + string.Join("\n", where));
        if (facts.Count > 0) result.Append("\nFacts:\n" + string.Join("\n", facts));
        return result.ToString();
    }

    /// <summary>True when the line cites at least one location and every citation is a real file inside the project with the lines in range.</summary>
    private static bool Resolves(string line, string root, Dictionary<string, int> lineCounts)
    {
        var any = false;
        foreach (Match m in Citation().Matches(line))
        {
            any = true;
            string full;
            try { full = Path.GetFullPath(m.Groups["path"].Value.Replace('/', Path.DirectorySeparatorChar), root); }
            catch (ArgumentException) { return false; }
            if (!full.StartsWith(root + Path.DirectorySeparatorChar, StringComparison.OrdinalIgnoreCase)) return false;
            if (!lineCounts.TryGetValue(full, out var count))
            {
                if (!File.Exists(full)) return false;
                try { count = File.ReadLines(full).Count(); }
                catch (IOException) { return false; }
                catch (UnauthorizedAccessException) { return false; }
                lineCounts[full] = count;
            }
            if (!int.TryParse(m.Groups["start"].Value, out var start) || start < 1 || start > count) return false;
            if (m.Groups["end"].Success && (!int.TryParse(m.Groups["end"].Value, out var end) || end < start || end > count)) return false;
        }
        return any;
    }

    private static string Clip(string value, int max) => value.Length <= max ? value : value[..max].TrimEnd() + "…";
}
