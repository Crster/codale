using static Codale.Commands.CommandParsing;

namespace Codale.Commands.Detectors;

internal sealed class PrismaCommandDetector : IProjectCommandDetector
{
    public string Id => "Prisma";
    public IReadOnlyList<ProjectCommand> Discover(string root)
    {
        var path = FirstExisting(root, Path.Combine("prisma", "schema.prisma"), "schema.prisma");
        return path is null
            ? []
            : (IReadOnlyList<ProjectCommand>)PresetCommands(CommandSource.Prisma, path,
            ("generate", "npx prisma generate"),
            ("migrate dev", "npx prisma migrate dev"),
            ("db push", "npx prisma db push"),
            ("studio", "npx prisma studio"));
    }

}