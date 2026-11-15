using System.Text.Json;
using System.Text.Json.Nodes;
using System.Text.RegularExpressions;
using System.Xml.Linq;
using static Codale.Commands.CommandParsing;

namespace Codale.Commands;

internal sealed class FlutterCommandDetector : IProjectCommandDetector
{
    public string Id => "Flutter";
    public IReadOnlyList<ProjectCommand> Discover(string root)
    {
        var path = Path.Combine(root, "pubspec.yaml");
        if (!File.Exists(path))
        {
            return [];
        }

        var text = ReadTextSafe(path);
        var isFlutter = Regex.IsMatch(text, @"^\s*flutter\s*:", RegexOptions.Multiline);
        if (isFlutter)
        {
            return PresetCommands(CommandSource.Flutter, path,
                ("run", "flutter run"),
                ("test", "flutter test"),
                ("pub get", "flutter pub get"),
                ("build", "flutter build"));
        }

        return PresetCommands(CommandSource.Flutter, path,
            ("run", "dart run"),
            ("test", "dart test"),
            ("pub get", "dart pub get"));
    }

}
