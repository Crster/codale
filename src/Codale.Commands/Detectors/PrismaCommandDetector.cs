using System.Text.Json;
using System.Text.Json.Nodes;
using System.Text.RegularExpressions;
using System.Xml.Linq;
using static Codale.Commands.CommandParsing;

namespace Codale.Commands;

internal sealed class PrismaCommandDetector : IProjectCommandDetector
{
    public string Id => "Prisma";
    public IReadOnlyList<ProjectCommand> Discover(string root)
    {
        var path = FirstExisting(root, Path.Combine("prisma", "schema.prisma"), "schema.prisma");
        if (path is null)
        {
            return [];
        }

        return PresetCommands(CommandSource.Prisma, path,
            ("generate", "npx prisma generate"),
            ("migrate dev", "npx prisma migrate dev"),
            ("db push", "npx prisma db push"),
            ("studio", "npx prisma studio"));
    }

}
