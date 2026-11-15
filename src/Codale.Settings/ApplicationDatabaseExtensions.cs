using Codale.Core.Projects;
using Codale.Db;

namespace Codale.Settings;

public static class ApplicationDatabaseExtensions
{
    /// <summary>The root of a project's own entries, keyed by its path.</summary>
    public static DbNode Project(this CodaleDb db, string projectPath) =>
        db.Open("project").Open(ProjectPaths.InstanceKey(projectPath));

    /// <summary>A machine-wide setting.</summary>
    public static DbNode Setting(this CodaleDb db, string name) => db.Open("setting").Open(name);

}
