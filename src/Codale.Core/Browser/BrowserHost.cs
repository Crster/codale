using System.Diagnostics;
using System.IO.Pipes;
using System.Text.Json;

using Codale.Core.Projects;

namespace Codale.Core.Browser;

/// <summary>Launches and sends requests to the per-user Codale Browser window.</summary>
public static class BrowserHost
{
    public static async Task<JsonElement?> RequestAsync(object request, TimeSpan timeout, TimeSpan? connectTimeout = null)
    {
        using var cancellation = new CancellationTokenSource(connectTimeout ?? timeout);
        try
        {
            await using var pipe = new NamedPipeClientStream(".", "Codale.Browser.Control." + Environment.UserName,
                PipeDirection.InOut, PipeOptions.Asynchronous | PipeOptions.CurrentUserOnly);
            await pipe.ConnectAsync(cancellation.Token).ConfigureAwait(false);
            if (connectTimeout is not null) cancellation.CancelAfter(timeout);
            using var reader = new StreamReader(pipe, leaveOpen: true);
            using var writer = new StreamWriter(pipe, leaveOpen: true) { AutoFlush = true };
            await writer.WriteLineAsync(JsonSerializer.Serialize(request).AsMemory(), cancellation.Token).ConfigureAwait(false);
            if (await reader.ReadLineAsync(cancellation.Token).ConfigureAwait(false) is not { } line) return null;
            using var answer = JsonDocument.Parse(line);
            return answer.RootElement.Clone();
        }
        catch (Exception ex) when (ex is IOException or OperationCanceledException or TimeoutException or JsonException or UnauthorizedAccessException)
        {
            return null;
        }
    }

    public static void Launch(string executable)
    {
        // Explorer launches the unpackaged browser without inheriting the caller's package identity.
        // The handoff file carries the data root that the shell cannot pass in the environment.
        var directory = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.UserProfile), ".codale");
        Directory.CreateDirectory(directory);
        var handoff = Path.Combine(directory, "browser-launch.json");
        var pending = Path.Combine(directory, $"browser-launch-{Guid.NewGuid():N}.tmp");
        try
        {
            File.WriteAllText(pending, JsonSerializer.Serialize(new { dataRoot = CodalePaths.DataRoot }));
            File.Move(pending, handoff, overwrite: true);
        }
        finally
        {
            File.Delete(pending);
        }

        using var process = Process.Start(new ProcessStartInfo("explorer.exe")
        {
            Arguments = $"\"{executable}\"",
            UseShellExecute = false,
        });
    }
}
