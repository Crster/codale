using static Codale.Commands.CommandParsing;

namespace Codale.Commands.Detectors;

internal sealed class LaravelCommandDetector : IProjectCommandDetector
{
    public string Id => "Laravel";
    public IReadOnlyList<ProjectCommand> Discover(string root)
    {
        var path = Path.Combine(root, "artisan");
        return !File.Exists(path)
            ? []
            : (IReadOnlyList<ProjectCommand>)PresetCommands(CommandSource.Laravel, path,
            ("serve", "php artisan serve"),
            ("migrate", "php artisan migrate"),
            ("test", "php artisan test"),
            ("queue:work", "php artisan queue:work"),
            ("optimize:clear", "php artisan optimize:clear"));
    }

}