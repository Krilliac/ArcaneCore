namespace ArcaneCore.World.Persistence;

/// <summary>
/// The shutdown stop-drain lanes of the world host (a DI singleton). <see cref="Features"/> is shared by every feature-owned write queue (honor, reputation, taxi,
/// rest, explored zones, social, instance, GM audit, groups, account address, creature respawn), so the stall bound is global across them, not per queue.
/// <see cref="Saves"/> belongs to the character save queue alone: whatever the feature queues burned, player saves start with an untouched budget.
/// <c>WorldHost.StopCoreAsync</c> begins each lane just before the queues it covers stop.
/// </summary>
public sealed class ShutdownDrain
{
    /// <summary>The save lane's stall budget: three of the characters database's <c>busy_timeout</c>s (5 s each), so a locked database still gets the three inline attempts per snapshot.</summary>
    public static readonly TimeSpan SavesStallBudget = TimeSpan.FromSeconds(15);

    /// <summary>The lane shared by all feature write queues (settable at construction, so a host or test can choose the stall budget).</summary>
    public StopDrainBudget Features { get; init; } = new() { Name = "feature write queues' stop drain" };

    /// <summary>The character save queue's reserved lane (settable at construction).</summary>
    public StopDrainBudget Saves { get; init; } = new() { Name = "character save queue's stop drain", StallBudget = SavesStallBudget };
}
