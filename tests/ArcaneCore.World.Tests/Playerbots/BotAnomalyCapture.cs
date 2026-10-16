using ArcaneCore.Game.Entities;
using ArcaneCore.Kernel.Diagnostics;
using ArcaneCore.World.Playerbots;

namespace ArcaneCore.World.Tests.Playerbots;

/// <summary>
/// Reads one bot's reports from the process-wide <see cref="AnomalyLedger.Shared"/> without hearing other tests. Every test world has a
/// bot with the same name and guid (<see cref="PlayerbotMovementControlTests.EnterAsync"/>), so the subject alone is not unique across
/// test classes that run in parallel. Reports made through <see cref="PlayerbotAnomalies.Report"/> carry the world the bot is in, so
/// <see cref="PlayerbotAnomalies.CaptureFor"/> keeps only this world's.
/// </summary>
internal static class BotAnomalyCapture
{
    /// <summary>Reports about <paramref name="player"/> in its own world (<paramref name="host"/> is kept for the callers' signature).</summary>
    public static Task<AnomalyCapture> OnWorldThreadAsync(WorldTestHost host, Player player)
    {
        ArgumentNullException.ThrowIfNull(host);
        return Task.FromResult(PlayerbotAnomalies.CaptureFor(player));
    }

    /// <summary>Reports made on the calling thread only (synchronous code under test).</summary>
    public static AnomalyCapture OnThisThread(Func<AnomalyNotice, bool>? also = null)
    {
        int thread = Environment.CurrentManagedThreadId;
        return AnomalyLedger.Shared.Capture(n => Environment.CurrentManagedThreadId == thread && (also is null || also(n)));
    }
}
