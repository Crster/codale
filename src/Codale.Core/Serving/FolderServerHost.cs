using Codale.Core.Tasks;

namespace Codale.Core.Serving;

/// <summary>One folder being served on localhost.</summary>
/// <param name="StartedBy">"agent" for the MCP tool, "you" for the Agent tools button.</param>
public sealed record ServedFolder(string Id, string Folder, string Url, int Port, string StartedBy, DateTimeOffset StartedAt);

/// <summary>
/// The folders a workspace window serves, whoever started them. Servers live until stopped
/// or the window goes; serving a folder that already is reuses its server and URL.
/// </summary>
public sealed class FolderServerHost : IDisposable
{
    private readonly Lock _gate = new();
    private readonly List<(ServedFolder Info, StaticFileServer Server)> _servers = [];
    private int _nextId;

    /// <summary>Raised (on the calling thread) after a server starts or stops.</summary>
    public event EventHandler? Changed;

    public ServedFolder Serve(string folder, int? port = null, bool spa = false, string startedBy = "agent")
    {
        string full;
        try
        {
            full = Path.TrimEndingDirectorySeparator(Path.GetFullPath(folder));
        }
        catch (Exception e) when (e is ArgumentException or NotSupportedException or PathTooLongException)
        {
            throw new TaskServiceException($"Not a valid folder path: {folder}");
        }

        if (!Directory.Exists(full))
        {
            throw new TaskServiceException(File.Exists(full)
                ? $"{full} is a file; pass the folder that contains it."
                : $"Folder not found: {full}");
        }

        if (port is < 1 or > 65535)
        {
            throw new TaskServiceException($"Port {port} is out of range.");
        }

        ServedFolder info;
        lock (_gate)
        {
            var existing = _servers.FindIndex(s => s.Info.Folder.Equals(full, StringComparison.OrdinalIgnoreCase));
            if (existing >= 0 && (port is null || _servers[existing].Info.Port == port))
            {
                return _servers[existing].Info;
            }

            if (existing >= 0)
            {
                // The same folder on another port: move it rather than serve it twice.
                _servers[existing].Server.Dispose();
                _servers.RemoveAt(existing);
            }

            StaticFileServer server;
            try
            {
                server = new StaticFileServer(full, port, spa);
            }
            catch (IOException e)
            {
                throw new TaskServiceException(e.Message);
            }

            info = new ServedFolder("s" + ++_nextId, full, server.Url, server.Port, startedBy, DateTimeOffset.Now);
            _servers.Add((info, server));
        }

        Changed?.Invoke(this, EventArgs.Empty);
        return info;
    }

    public IReadOnlyList<ServedFolder> List()
    {
        lock (_gate)
        {
            return [.. _servers.Select(s => s.Info)];
        }
    }

    /// <summary>Stops a server by id (or by its folder path or URL); returns what was stopped.</summary>
    public ServedFolder Stop(string idOrFolder)
    {
        ServedFolder info;
        lock (_gate)
        {
            var index = _servers.FindIndex(s =>
                s.Info.Id.Equals(idOrFolder, StringComparison.OrdinalIgnoreCase)
                || s.Info.Url.Equals(idOrFolder, StringComparison.OrdinalIgnoreCase)
                || s.Info.Folder.Equals(Path.TrimEndingDirectorySeparator(idOrFolder), StringComparison.OrdinalIgnoreCase));
            if (index < 0)
            {
                throw new TaskServiceException($"No served folder '{idOrFolder}'. Use list_served to see them.");
            }

            info = _servers[index].Info;
            _servers[index].Server.Dispose();
            _servers.RemoveAt(index);
        }

        Changed?.Invoke(this, EventArgs.Empty);
        return info;
    }

    public void StopAll()
    {
        lock (_gate)
        {
            if (_servers.Count == 0)
            {
                return;
            }

            foreach (var (_, server) in _servers)
            {
                server.Dispose();
            }

            _servers.Clear();
        }

        Changed?.Invoke(this, EventArgs.Empty);
    }

    public void Dispose() => StopAll();
}
