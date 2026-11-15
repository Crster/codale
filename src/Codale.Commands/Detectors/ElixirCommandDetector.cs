using static Codale.Commands.CommandParsing;

namespace Codale.Commands.Detectors;

internal sealed class ElixirCommandDetector : IProjectCommandDetector
{
    public string Id => "Elixir";
    public IReadOnlyList<ProjectCommand> Discover(string root)
    {
        var path = Path.Combine(root, "mix.exs");
        if (!File.Exists(path))
        {
            return [];
        }

        var text = ReadTextSafe(path);
        var commands = new List<ProjectCommand>();
        if (text.Contains(":phoenix", StringComparison.Ordinal))
        {
            commands.Add(new ProjectCommand
            {
                Name = "phx.server",
                Command = "mix phx.server",
                Source = CommandSource.Elixir,
                SourcePath = path,
            });
        }
        else
        {
            commands.Add(new ProjectCommand
            {
                Name = "run",
                Command = "mix run",
                Source = CommandSource.Elixir,
                SourcePath = path,
            });
        }

        commands.AddRange(PresetCommands(CommandSource.Elixir, path,
            ("test", "mix test"),
            ("deps.get", "mix deps.get"),
            ("compile", "mix compile")));

        return commands;
    }

}