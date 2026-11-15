namespace Codale.Commands;

/// <summary>
/// The rules every way of running a saved command shares: which command a name means, where it runs, and whether a
/// live run already covers it. The app's commands menu and the agent's tools both use these.
/// </summary>
public static class ProjectCommandRun
{
    /// <summary>
    /// The command a name refers to: an exact name (case-insensitive) first, then a unique substring.
    /// </summary>
    /// <exception cref="InvalidOperationException">No command or several match; the message names the candidates.</exception>
    public static ProjectCommand Resolve(IReadOnlyList<ProjectCommand> all, string name)
    {
        name = name.Trim();
        if (all.FirstOrDefault(c => string.Equals(c.Name, name, StringComparison.OrdinalIgnoreCase)) is { } exact)
        {
            return exact;
        }

        var partial = all.Where(c => c.Name.Contains(name, StringComparison.OrdinalIgnoreCase)).ToList();
        if (partial.Count == 1)
        {
            return partial[0];
        }

        var available = string.Join(", ", (partial.Count > 1 ? partial : all).Select(c => $"\"{c.Name}\""));
        throw new InvalidOperationException(partial.Count > 1
            ? $"\"{name}\" matches several commands: {available}."
            : all.Count == 0
                ? "No commands saved. Use add_command to save one."
                : $"No command named \"{name}\". Available: {available}.");
    }

    /// <summary>
    /// The absolute directory a command runs in: its working directory under <paramref name="root"/>, or the root when it
    /// names none or the path is not valid.
    /// </summary>
    public static string WorkingDirectoryFor(ProjectCommand command, string root)
    {
        if (string.IsNullOrWhiteSpace(command.WorkingDirectory))
        {
            return root;
        }

        try
        {
            return Path.GetFullPath(Path.Combine(root, command.WorkingDirectory));
        }
        catch (ArgumentException)
        {
            return root;
        }
    }

    /// <summary>
    /// Whether a live run already covers this command: the same name, or the same command line. A second dev server
    /// would only fight over the port, so the caller shows the live run instead.
    /// </summary>
    public static bool IsCoveredBy(ProjectCommand command, string? liveName, string? liveCommand)
    {
        var name = command.Name.Trim();
        var line = command.Command.Trim();
        return string.Equals(liveName?.Trim(), name, StringComparison.OrdinalIgnoreCase)
            || string.Equals(liveCommand?.Trim(), line, StringComparison.Ordinal);
    }
}
