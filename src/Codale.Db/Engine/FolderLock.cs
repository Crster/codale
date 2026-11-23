using System.Diagnostics;

namespace Codale.Db.Engine;

internal static class FolderLock
{
    public static FileStream Acquire(string directory, TimeSpan timeout)
    {
        var path = Path.Combine(directory, "LOCK");
        var start = Stopwatch.GetTimestamp();
        while (true)
        {
            try
            {
                return new FileStream(path, FileMode.OpenOrCreate, FileAccess.ReadWrite, FileShare.None);
            }
            catch (IOException) when (Stopwatch.GetElapsedTime(start) < timeout)
            {
                Thread.Sleep(15);
            }
        }
    }
}
