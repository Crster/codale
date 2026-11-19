using System.Text.RegularExpressions;

namespace Codale.Workspace;

public sealed record MemoryCandidate(string Title, string Text, string Category, string Evidence, string? ReplacesId = null);

/// <summary>Conservative offline fallback: only explicit user instructions, never inferred agent proposals.</summary>
public static class MemoryCapture
{
    public static bool ContainsSecret(string text) => Regex.IsMatch(text,
        @"(?i)(\b(?:api[ _-]?key|password|passwd|secret|access[ _-]?token|connection[ _-]?string)\b\s*(?:is\s+|[:=]\s*)\S+|\bbearer\s+[a-z0-9_.-]{12,}|\bsk-[a-z0-9_-]{12,}|-----BEGIN .*PRIVATE KEY|://[^\s/:]+:[^\s/@]+@)");

    public static IReadOnlyList<MemoryCandidate> Local(string userText)
    {
        if (ContainsSecret(userText)) return [];
        return Regex.Split(userText, @"(?<=[.!?])\s+|[\r\n]+")
            .Select(s => s.Trim()).Where(s => s.Length is > 8 and <= 1000 && Regex.IsMatch(s,
                @"(?i)^(?:please\s+)?(?:remember (?:that |to )?|i prefer |i like (?:it when|to have) |from now on[,:]? |going forward[,:]? |in (?:this|the) (?:repo|project)[,:]? |our (?:convention|rule|standard) is |we (?:always|never|use) |always use |never use |always |never |don'?t (?:ever |ever again )?(?:use|add|commit|run) |do not (?:use|add|commit|run) |make sure (?:you |to )?always )") &&
                !Regex.IsMatch(s, @"(?i)\b(fix|implement|commit|finish|today|tomorrow|this task|step [0-9])\b"))
            .Take(3).Select(s => new MemoryCandidate(TitleOf(s), s, "preference", s)).ToArray();
    }

    /// <summary>A readable title from the instruction itself: its first words, without the "remember that" lead-in.</summary>
    private static string TitleOf(string sentence)
    {
        var core = Regex.Replace(sentence, @"(?i)^(?:please\s+)?(?:remember (?:that |to )?|from now on[,:]? |going forward[,:]? )", "").Trim().TrimEnd('.', '!');
        if (core.Length == 0) core = sentence;
        var cut = core.Length <= 60 ? core : core[..core.LastIndexOf(' ', 60)];
        return char.ToUpperInvariant(cut[0]) + cut[1..];
    }

    public static bool Eligible(MemoryCandidate candidate, string userText, string? acceptedPlan, string? verifiedEvidence)
    {
        if (candidate is null || string.IsNullOrWhiteSpace(candidate.Text) || string.IsNullOrWhiteSpace(candidate.Title) ||
            candidate.Text.Length is < 3 or > 1500 || candidate.Title.Length > 160 ||
            string.IsNullOrWhiteSpace(candidate.Evidence) || ContainsSecret(candidate.Text) || ContainsSecret(candidate.Evidence)) return false;
        return candidate.Category switch
        {
            "preference" => userText.Contains(candidate.Evidence, StringComparison.OrdinalIgnoreCase),
            "decision" => acceptedPlan?.Contains(candidate.Evidence, StringComparison.OrdinalIgnoreCase) == true ||
                userText.Contains(candidate.Evidence, StringComparison.OrdinalIgnoreCase),
            "project-fact" => candidate.Evidence.Length >= 12 && verifiedEvidence?.Contains(candidate.Evidence, StringComparison.OrdinalIgnoreCase) == true,
            _ => false,
        };
    }
}
