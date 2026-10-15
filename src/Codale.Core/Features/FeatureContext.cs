using System.Text;

using Codale.Core.Helper;

namespace Codale.Core.Features;

/// <summary>
/// What a fresh session starts a feature from: the summary, the progress made since it, the
/// files the agent wrote and the stretches of them most relevant to the message. It rides on
/// the first message (see <see cref="Agents.SessionBriefing.ContextBlock"/>), so no earlier
/// message is ever loaded into the chat.
/// </summary>
public static class FeatureContext
{
    /// <summary>What the retrieved code may take up.</summary>
    public const int CodeBudgetChars = 8_000;

    private const int MaxFiles = 30;

    public static string Build(
        FeatureInfo feature,
        IReadOnlyList<HandoffEntry> progressSinceSummary,
        IReadOnlyList<string> files,
        IReadOnlyList<FileHit> relevantCode)
    {
        var text = new StringBuilder();
        text.AppendLine($"# Feature: {feature.Name}");

        if (feature.Summary.Length > 0)
        {
            text.AppendLine();
            text.AppendLine(feature.Summary.Trim());
        }
        else if (feature.Description.Length > 0)
        {
            text.AppendLine();
            text.AppendLine(feature.Description);
        }

        if (progressSinceSummary.Count > 0)
        {
            text.AppendLine();
            text.AppendLine("## Progress since that summary");
            text.AppendLine(SessionHandoff.Condense(progressSinceSummary));
        }

        if (files.Count > 0)
        {
            text.AppendLine();
            text.AppendLine("## Files written for this feature");
            foreach (var file in files.Take(MaxFiles))
            {
                text.AppendLine($"- `{file}`");
            }
        }

        var used = 0;
        var code = new StringBuilder();
        foreach (var hit in relevantCode)
        {
            var block = $"### {hit.Path}:{hit.StartLine}\n```\n{hit.Text}\n```\n";
            if (used + block.Length > CodeBudgetChars)
            {
                break;
            }

            used += block.Length;
            code.AppendLine(block);
        }

        if (code.Length > 0)
        {
            text.AppendLine();
            text.AppendLine("## Code from earlier work on this feature (may be out of date: check the files)");
            text.Append(code);
        }

        return text.ToString().TrimEnd();
    }
}