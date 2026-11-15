using System.Text.Json;
using System.Text.Json.Nodes;
using System.Text.RegularExpressions;
using System.Xml.Linq;
using static Codale.Commands.CommandParsing;

namespace Codale.Commands;

internal sealed class LaravelCommandDetector : IProjectCommandDetector
{
    public string Id => "Laravel";
    public IReadOnlyList<ProjectCommand> Discover(string root)
    {
        var path = Path.Combine(root, "artisan");
        if (!File.Exists(path))
        {
            return [];
        }

        return PresetCommands(CommandSource.Laravel, path,
            ("serve", "php artisan serve"),
            ("migrate", "php artisan migrate"),
            ("test", "php artisan test"),
            ("queue:work", "php artisan queue:work"),
            ("optimize:clear", "php artisan optimize:clear"));
    }

}
