using ArcaneCore.Game.Maps;
using ArcaneCore.World.Features;
using ArcaneCore.World.Net;
using Microsoft.Extensions.DependencyInjection;

namespace ArcaneCore.World.Gm.Server;

/// <summary>
/// Player counts for <c>.server info</c>: the active count comes from the session registry (the
/// players in the world when none is registered) and the maximum is sampled whenever a player
/// enters the world. ArcaneCore has no login queue, so the queued counts are always 0.
/// </summary>
public sealed class ServerStats(IServiceProvider services) : IWorldFeature
{
    private int _maxActive;

    public int MaxActive => Volatile.Read(ref _maxActive);

    public void Attach(WorldRuntime world) => world.PlayerLoggedIn += _ => Sample(world);

    public int Active(WorldRuntime world) => Math.Max(services.GetService<SessionRegistry>()?.Count ?? 0, world.OnlinePlayerCount);

    private void Sample(WorldRuntime world)
    {
        int active = Active(world);
        int seen;
        do
        {
            seen = Volatile.Read(ref _maxActive);
        }
        while (active > seen && Interlocked.CompareExchange(ref _maxActive, active, seen) != seen);
    }
}
