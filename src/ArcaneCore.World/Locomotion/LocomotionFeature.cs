using ArcaneCore.Game.Entities;
using ArcaneCore.Game.Locomotion;
using ArcaneCore.Game.Maps;
using ArcaneCore.World.Features;
using ArcaneCore.World.Teleport;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;

namespace ArcaneCore.World.Locomotion;

/// <summary>
/// Binds the <c>Locomotion</c> configuration section into <see cref="LocomotionOptions"/> (a negative
/// <c>RateDamageFall</c> falls back to 1, like vmangos setConfigPos) and registers the per-world
/// <see cref="LocomotionEnvironment"/> with the teleport probe the pending-ack timeout needs. The rules themselves
/// run in the map tick (<c>MapLocomotion</c>) and in the movement handlers (docs/areas/locomotion-foundation.md).
/// </summary>
public sealed class LocomotionFeature(IServiceProvider services, ILogger<LocomotionFeature> logger) : IWorldFeature
{
    public void Attach(WorldRuntime world)
    {
        var options = new LocomotionOptions();
        services.GetService<IConfiguration>()?.GetSection(LocomotionOptions.SectionName).Bind(options);
        foreach (string name in options.Normalize())
        {
            logger.LogError("Locomotion:{Option} is out of range and was corrected", name);
        }

        // The teleport feature is resolved on first use: it may attach after this one.
        TeleportFeature? teleports = null;
        LocomotionEnvironment.Register(world, new LocomotionEnvironment(options, player =>
        {
            teleports ??= services.GetRequiredService<TeleportFeature>();
            return teleports.Teleports.IsBeingTeleported(player);
        }));
        logger.LogInformation("locomotion: pending-ack timeout {AckMs} ms, fall damage rate {Fall}", options.PendingAckResponseTimeMs, options.RateDamageFall);
    }
}
