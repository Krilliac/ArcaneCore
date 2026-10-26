namespace ArcaneCore.World.Persistence;

/// <summary>
/// The shutdown stop-drain lanes of the world host (a DI singleton). <see cref="Features"/> is shared by every feature-owned write queue (honor, reputation, taxi,
/// rest, explored zones, social, instance, GM audit, groups, account address, creature respawn), so the stall bound is global across them, not per queue.
/// <see cref="Saves"/> belongs to the character save queue alone: whatever the feature queues burned, player saves start with an untouched budget.
/// <c>WorldHost.StopCoreAsync</c> begins each lane just before the queues it covers stop.
/// </summary>
public sealed class ShutdownDrain
{
    /// <summary>The lane shared by all feature write queues (settable at construction, so a host or test can choose the stall budget).</summary>
    public StopDrainBudget Features { get; init; } = new() { Name = "feature write queues' stop drain" };

    /// <summary>The character save queue's reserved lane (settable at construction).</summary>
    public StopDrainBudget Saves { get; init; } = new() { Name = "character save queue's stop drain" };
}
