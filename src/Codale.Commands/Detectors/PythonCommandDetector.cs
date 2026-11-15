using System.Text.Json;
using System.Text.Json.Nodes;
using System.Text.RegularExpressions;
using System.Xml.Linq;
using static Codale.Commands.CommandParsing;

namespace Codale.Commands;

internal sealed class PythonCommandDetector : IProjectCommandDetector
{
    public string Id => "Python";
    public IReadOnlyList<ProjectCommand> Discover(string root)
    {
        var commands = new List<ProjectCommand>();
        var seenNames = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

        void AddPython(string name, string command, string sourcePath)
        {
            if (seenNames.Add(name))
            {
                commands.Add(new ProjectCommand
                {
                    Name = name,
                    Command = command,
                    Source = CommandSource.Python,
                    SourcePath = sourcePath,
                });
            }
        }

        var managePy = Path.Combine(root, "manage.py");
        if (File.Exists(managePy))
        {
            AddPython("django: runserver", "python manage.py runserver", managePy);
            AddPython("django: migrate", "python manage.py migrate", managePy);
            AddPython("django: makemigrations", "python manage.py makemigrations", managePy);
            AddPython("django: test", "python manage.py test", managePy);
        }

        var pyproject = Path.Combine(root, "pyproject.toml");
        var pyprojectText = File.Exists(pyproject) ? ReadTextSafe(pyproject) : "";
        if (pyprojectText.Length > 0)
        {
            var runner = File.Exists(Path.Combine(root, "uv.lock")) ? "uv run"
                : File.Exists(Path.Combine(root, "poetry.lock")) || pyprojectText.Contains("[tool.poetry", StringComparison.Ordinal) ? "poetry run"
                : File.Exists(Path.Combine(root, "pdm.lock")) || pyprojectText.Contains("[tool.pdm", StringComparison.Ordinal) ? "pdm run"
                : null;

            foreach (var section in new[] { "project.scripts", "tool.poetry.scripts", "tool.pdm.scripts", "tool.hatch.envs.default.scripts" })
            {
                foreach (var (key, _) in ParseTomlSectionKeys(pyprojectText, section))
                {
                    if (!IsSafeName(key))
                    {
                        continue;
                    }

                    var cmd = section == "tool.hatch.envs.default.scripts"
                        ? $"hatch run {key}"
                        : runner is not null ? $"{runner} {key}" : key;
                    AddPython(key, cmd, pyproject);
                }
            }

            foreach (var (key, _) in ParseTomlSectionKeys(pyprojectText, "tool.poe.tasks"))
            {
                if (IsSafeName(key))
                {
                    AddPython(key, $"poe {key}", pyproject);
                }
            }
        }

        var pipfile = Path.Combine(root, "Pipfile");
        if (File.Exists(pipfile))
        {
            foreach (var (key, _) in ParseTomlSectionKeys(ReadTextSafe(pipfile), "scripts"))
            {
                if (IsSafeName(key))
                {
                    AddPython(key, $"pipenv run {key}", pipfile);
                }
            }
        }

        var primaryManifest = FirstExisting(root, "pyproject.toml", "Pipfile", "requirements.txt", "setup.py", "manage.py");
        if (primaryManifest is not null)
        {
            foreach (var entry in new[] { "main.py", "app.py" })
            {
                var entryPath = Path.Combine(root, entry);
                if (File.Exists(entryPath))
                {
                    AddPython($"run {entry}", $"python {entry}", entryPath);
                }
            }

            if (pyprojectText.Contains("pytest", StringComparison.OrdinalIgnoreCase) ||
                File.Exists(Path.Combine(root, "pytest.ini")) ||
                Directory.Exists(Path.Combine(root, "tests")))
            {
                AddPython("pytest", "pytest", primaryManifest);
            }

            if (pyprojectText.Contains("[tool.ruff", StringComparison.OrdinalIgnoreCase) ||
                File.Exists(Path.Combine(root, "ruff.toml")))
            {
                AddPython("ruff check", "ruff check .", primaryManifest);
            }
        }

        return commands;
    }

}
