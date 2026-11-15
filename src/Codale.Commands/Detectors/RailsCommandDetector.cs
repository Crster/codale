using System.Text.RegularExpressions;

using static Codale.Commands.CommandParsing;

namespace Codale.Commands.Detectors;

internal sealed class RailsCommandDetector : IProjectCommandDetector
{
    public string Id => "Rails";
    public IReadOnlyList<ProjectCommand> Discover(string root)
    {
        var binRails = Path.Combine(root, "bin", "rails");
        var gemfile = Path.Combine(root, "Gemfile");
        var hasRails = File.Exists(binRails)
            || (File.Exists(gemfile) && Regex.IsMatch(ReadTextSafe(gemfile), @"^\s*gem\s+['""]rails['""]", RegexOptions.Multiline));
        if (!hasRails)
        {
            return [];
        }

        var sourcePath = File.Exists(binRails) ? binRails : gemfile;
        return PresetCommands(CommandSource.Rails, sourcePath,
            ("server", "bundle exec rails server"),
            ("db:migrate", "bundle exec rails db:migrate"),
            ("test", "bundle exec rails test"),
            ("routes", "bundle exec rails routes"));
    }

}