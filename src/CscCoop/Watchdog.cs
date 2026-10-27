using System.Threading;

namespace CscCoop;

// Finds where a frozen game is stuck (world2 suite: the host's game froze for 75-79 s after a shard was
// collected, with the mod's own Update at 3 ms). The mod leaves breadcrumbs at the start and end of its
// patches and per-frame work; a background thread reports the last one when frames stop for 5 s.
static class Watchdog
{
    public static volatile string Where = "start";
    static long lastFrame = System.Diagnostics.Stopwatch.GetTimestamp();
    static Thread thread;

    public static void Frame() => Interlocked.Exchange(ref lastFrame, System.Diagnostics.Stopwatch.GetTimestamp());

    public static void Start()
    {
        if (thread != null) return;
        thread = new Thread(() =>
        {
            bool reported = false;
            while (true)
            {
                Thread.Sleep(1000);
                double s = (System.Diagnostics.Stopwatch.GetTimestamp() - Interlocked.Read(ref lastFrame)) / (double)System.Diagnostics.Stopwatch.Frequency;
                if (s > 5 && !reported) { reported = true; Plugin.L.LogWarning($"COOP main thread stuck {s:F0} s, last mod step: {Where}"); }
                if (s < 1 && reported) { reported = false; Plugin.L.LogWarning($"COOP main thread running again, last mod step: {Where}"); }
            }
        }) { IsBackground = true, Name = "csccoop-watchdog" };
        thread.Start();
    }
}
