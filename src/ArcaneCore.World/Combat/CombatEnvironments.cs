using ArcaneCore.Game.Combat;
using ArcaneCore.Game.Maps;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;

namespace ArcaneCore.World.Combat;

/// <summary>
/// One <see cref="CombatEnvironment"/> per world, shared by the combat features: the first feature to attach binds the
/// <c>Combat</c> configuration section into <see cref="CombatOptions"/> (negative <c>Rate.Mana</c> / <c>Rate.Rage.Loss</c>
/// fall back to 1, like vmangos <c>setConfigPos</c>) and registers it; the others add their links to it.
/// </summary>
internal static class CombatEnvironments
{
    public static CombatEnvironment GetOrCreate(IServiceProvider services, WorldRuntime world, ILogger logger)
    {
        ArgumentNullException.ThrowIfNull(services);
        ArgumentNullException.ThrowIfNull(world);
        return CombatEnvironment.GetOrCreate(world, () =>
        {
            var options = new CombatOptions();
            services.GetService<IConfiguration>()?.GetSection(CombatOptions.SectionName).Bind(options);
            foreach (string name in options.Normalize())
            {
                logger.LogError("{Rate} can't be negative. Using 1 instead.", name);
            }

            return options;
        });
    }
}
