using ArcaneCore.Game.Entities;
using ArcaneCore.Kernel.Diagnostics;
using ArcaneCore.World.Playerbots;

namespace ArcaneCore.World.Tests.Playerbots;

/// <summary>
/// Reads one bot's reports from the process-wide <see cref="AnomalyLedger.Shared"/> without hearing other tests. Every test world has a
/// bot with the same name and guid (<see cref="PlayerbotMovementControlTests.EnterAsync"/>), so the subject alone is not unique across
/// test classes that run in parallel. A report is made synchronously on the thread that finds the failure, and each world runs on its own
/// dedicated thread, so the capture also keeps only the reports made on this world's thread.
/// </summary>
internal static class BotAnomalyCapture
{
    /// <summary>Reports about <paramref name="player"/> made on <paramref name="host"/>'s world thread.</summary>
    public static async Task<AnomalyCapture> OnWorldThreadAsync(WorldTestHost host, Player player)
    {
        int worldThread = await host.OnWorldAsync(() => Environment.CurrentManagedThreadId);
        string subject = await host.OnWorldAsync(() => PlayerbotAnomalies.SubjectOf(player));
        return AnomalyLedger.Shared.Capture(n => Environment.CurrentManagedThreadId == worldThread
            && string.Equals(n.Subject, subject, StringComparison.Ordinal));
    }

    /// <summary>Reports made on the calling thread only (synchronous code under test).</summary>
    public static AnomalyCapture OnThisThread(Func<AnomalyNotice, bool>? also = null)
    {
        int thread = Environment.CurrentManagedThreadId;
        return AnomalyLedger.Shared.Capture(n => Environment.CurrentManagedThreadId == thread && (also is null || also(n)));
    }
}
