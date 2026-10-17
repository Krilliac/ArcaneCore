using ArcaneCore.Game.Teleport;

namespace ArcaneCore.World.Features;

/// <summary>When a <see cref="IWorldLifecycleHooks"/> hook runs.</summary>
public enum WorldLifecycleStage
{
    /// <summary>Once the world thread runs (after every feature attached). Hooks run in registration order.</summary>
    AfterWorldStart,

    /// <summary>Before the world thread stops, while sessions still exist (e.g. log managed bots out). Newest registration first.</summary>
    BeforeWorldStop,
}

/// <summary>
/// Generic startup and shutdown hooks (docs/design/hot-reload-tiers.md, section 4.4 2a): the seam that replaces the host
/// calling a feature by type. Register through <see cref="FeatureSubscriptions.Add"/> so a detach removes the hook.
/// </summary>
public interface IWorldLifecycleHooks
{
    /// <summary>
    /// Run <paramref name="hook"/> at <paramref name="stage"/>. <paramref name="name"/> identifies it in logs and must be
    /// unique per stage. Disposing the result removes the hook (a hook already running finishes).
    /// </summary>
    IDisposable Register(WorldLifecycleStage stage, string name, Func<CancellationToken, Task> hook);
}

/// <summary>
/// Teleport (area trigger) requirement waivers as registrations rather than feature types (docs/design/hot-reload-tiers.md,
/// section 4.1, "teleport waiver lookup"): a detachable feature registers its <see cref="IAreaTriggerWaiver"/> here
/// through <see cref="FeatureSubscriptions.Add"/>, and the teleport handler sees it for exactly as long as it is attached.
/// </summary>
public interface ITeleportWaiverRegistry
{
    /// <summary>Consult <paramref name="waiver"/> on every area trigger teleport until the result is disposed.</summary>
    IDisposable Register(IAreaTriggerWaiver waiver);
}
