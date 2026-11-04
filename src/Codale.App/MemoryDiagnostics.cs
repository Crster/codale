using System.Diagnostics;
using System.Globalization;

namespace Codale.App;

/// <summary>Low-frequency, process-wide observations. Never forces a GC or retains UI objects.</summary>
internal static class MemoryDiagnostics
{
    private static readonly object Gate = new();
    private static readonly Stopwatch Uptime = Stopwatch.StartNew();
    private static Timer? _timer;
    private static Timer? _releaseTimer;
    private static long? _lastPrivate;
    private static long? _lastManaged;
    private static long _peakPrivate;
    private static readonly List<(WeakReference<object> Owner, double ClosedAt)> Closed = [];
    private static long _collectedClosed;
    private static long _droppedClosed;
    private static long _releasedMarkdownViews;
    private static long _releasedMarkdownBlocks;
    private static long _releasedImageReferences;
    private static long _preparedRows;
    private static long _recycledRows;

    public static void MarkdownReleased(int blocks, int images)
    {
        Interlocked.Increment(ref _releasedMarkdownViews);
        Interlocked.Add(ref _releasedMarkdownBlocks, blocks);
        Interlocked.Add(ref _releasedImageReferences, images);
    }

    public static void RowPrepared() => Interlocked.Increment(ref _preparedRows);
    public static void RowRecycled() => Interlocked.Increment(ref _recycledRows);

    public static void Start()
    {
        lock (Gate)
        {
            _timer ??= new Timer(_ => Sample("periodic"), null, TimeSpan.FromSeconds(30), TimeSpan.FromSeconds(30));
            _releaseTimer ??= new Timer(_ => Sample("cleanup.settled"), null, Timeout.Infinite, Timeout.Infinite);
        }
        Sample("startup");
    }

    public static void Stop()
    {
        lock (Gate)
        {
            _timer?.Dispose();
            _releaseTimer?.Dispose();
            _timer = _releaseTimer = null;
        }
        Sample("shutdown");
    }

    /// <summary>A release is an explicit disposal, not a claim that GC or the driver reclaimed bytes.</summary>
    public static void Released(string reason, string details = "", object? owner = null)
    {
        lock (Gate)
        {
            if (owner is not null)
            {
                if (Closed.Count == 128)
                {
                    Closed.RemoveAt(0);
                    _droppedClosed++;
                }
                Closed.Add((new WeakReference<object>(owner), Uptime.Elapsed.TotalSeconds));
            }
            // Coalesce bursts of recycled rows/closed tabs into one follow-up.
            _releaseTimer?.Change(TimeSpan.FromSeconds(5), Timeout.InfiniteTimeSpan);
        }
        Sample(reason, details);
    }

    public static void Sample(string reason, string details = "")
    {
        try
        {
            lock (Gate)
            {
                using var process = Process.GetCurrentProcess();
                var privateBytes = process.PrivateMemorySize64;
                var managed = GC.GetTotalMemory(forceFullCollection: false);
                var gc = GC.GetGCMemoryInfo();
                _collectedClosed += Closed.RemoveAll(entry => !entry.Owner.TryGetTarget(out _));
                var oldestClosedSec = Closed.Count == 0 ? 0 : Uptime.Elapsed.TotalSeconds - Closed[0].ClosedAt;
                _peakPrivate = Math.Max(_peakPrivate, privateBytes);
                var text = $"pid={Environment.ProcessId} event={reason} uptimeSec={Uptime.Elapsed.TotalSeconds:0} " +
                    $"workingMB={Mb(process.WorkingSet64)} privateMB={Mb(privateBytes)} managedMB={Mb(managed)} " +
                    $"deltaPrivateMB={Delta(privateBytes, _lastPrivate)} deltaManagedMB={Delta(managed, _lastManaged)} " +
                    $"peakPrivateMB={Mb(_peakPrivate)} gcHeapMB={Mb(gc.HeapSizeBytes)} " +
                    $"gcCommittedMB={Mb(gc.TotalCommittedBytes)} gcFragmentedMB={Mb(gc.FragmentedBytes)} " +
                    $"allocatedMB={Mb(GC.GetTotalAllocatedBytes(precise: false))} " +
                    $"gc0={GC.CollectionCount(0)} gc1={GC.CollectionCount(1)} gc2={GC.CollectionCount(2)} " +
                    $"handles={process.HandleCount} threads={process.Threads.Count} " +
                    $"closedStillAlive={Closed.Count} closedCollected={_collectedClosed} oldestClosedSec={oldestClosedSec:0} closedTrackingDropped={_droppedClosed} " +
                    $"markdownUnloaded={Interlocked.Read(ref _releasedMarkdownViews)} markdownBlocksReleased={Interlocked.Read(ref _releasedMarkdownBlocks)} " +
                    $"imageReferencesReleased={Interlocked.Read(ref _releasedImageReferences)} " +
                    $"rowsPrepared={Interlocked.Read(ref _preparedRows)} rowsRecycled={Interlocked.Read(ref _recycledRows)} {details}";
                if (_lastPrivate is { } previous && privateBytes - previous >= 128L * 1024 * 1024)
                    CrashLog.Warn("memory", text);
                else
                    CrashLog.Info("memory", text);
                _lastPrivate = privateBytes;
                _lastManaged = managed;
            }
        }
        catch (Exception ex)
        {
            // Diagnostics cannot interfere with cleanup or terminate a timer callback.
            CrashLog.Debug("memory", $"sample unavailable: {ex.GetType().Name}");
        }
    }

    private static string Mb(long bytes) => (bytes / (1024d * 1024)).ToString("0.0", CultureInfo.InvariantCulture);
    private static string Delta(long bytes, long? previous) => previous is { } value ? Mb(bytes - value) : "n/a";
}
