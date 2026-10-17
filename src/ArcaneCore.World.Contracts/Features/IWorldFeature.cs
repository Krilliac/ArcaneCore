using ArcaneCore.Game.Maps;

namespace ArcaneCore.World.Features;

/// <summary>
/// A world-daemon feature service (channels, groups, social lists, teleports, …). Every
/// non-abstract implementation in the world assembly is registered as a singleton and attached to
/// the world before the world thread starts; <see cref="Attach"/> is where it subscribes to
/// <see cref="WorldRuntime.PlayerLoggedIn"/> / <see cref="WorldRuntime.PlayerLoggingOut"/>.
/// <para>
/// Discovery (instead of a hand-maintained list) lets features built in parallel land without
/// editing a shared registration file (docs/integration/seams.md). Handlers reach a
/// feature through <c>session.Services.GetRequiredService&lt;TFeature&gt;()</c>.
/// </para>
/// <para>
/// Lives in the contracts assembly (namespace unchanged) so a pack can implement it; a feature that does not also
/// implement <see cref="IDetachableWorldFeature"/> is pinned: it is attached once and never detached.
/// </para>
/// </summary>
public interface IWorldFeature
{
    /// <summary>Called once, before the world thread starts (no world-thread work may run here).</summary>
    void Attach(WorldRuntime world);

    /// <summary>After the world stops producing updates, drain feature-owned saves before shutdown completes.</summary>
    Task StopAsync() => Task.CompletedTask;
}
