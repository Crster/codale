using System.Text;

namespace Codale.Smart;

public sealed record SmartContext(string Text, IReadOnlyList<string> Keys, IReadOnlyList<string> Files, int Snippets, int Findings)
{
    public const int MaxBytes = 6 * 1024;

    public static async Task<SmartContext> BuildAsync(IEnumerable<SmartEvidence> evidence, IReadOnlySet<string> delivered,
        string basic, IReadOnlyList<string> basicKeys, CancellationToken ct = default)
    {
        var text = new StringBuilder(); var keys = new List<string>(); var files = new List<string>(); int snippets = 0, findings = 0;
        foreach (var item in evidence.DistinctBy(e => e.Key))
        {
            if (delivered.Contains(item.Key) || !await item.IsFreshAsync(ct).ConfigureAwait(false)) continue;
            var heading = item.Kind == "file" ? $"{item.Path}:{item.StartLine}-{item.EndLine}"
                : $"Historical command observation at {item.ObservedAt:O}; cwd: {item.Path}; exit: {item.ExitCode}\nCommand: {item.Command}";
            var block = heading + "\n" + item.Text + "\n\n";
            int left = MaxBytes - Encoding.UTF8.GetByteCount(text.ToString());
            if (left < Encoding.UTF8.GetByteCount(heading) + 80) break;
            text.Append(SmartConversation.Clip(block, left)); keys.Add(item.Key);
            if (item.Kind == "file") { files.Add(item.Path); snippets++; } else findings++;
        }
        if (!string.IsNullOrWhiteSpace(basic) && Encoding.UTF8.GetByteCount(text.ToString()) + Encoding.UTF8.GetByteCount(basic) <= MaxBytes)
        { text.Append(basic); keys.AddRange(basicKeys); }
        return new(text.ToString().TrimEnd(), keys.Distinct().ToArray(), files.Distinct().ToArray(), snippets, findings);
    }
}
