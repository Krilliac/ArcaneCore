using ArcaneCore.Data.Npc;
using ArcaneCore.Game.Combat;
using ArcaneCore.Game.Creatures;
using ArcaneCore.Game.Maps;
using ArcaneCore.Kernel.Npc;
using ArcaneCore.World.Features;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;

namespace ArcaneCore.World.Combat;

/// <summary>
/// Registers the faction-aware <see cref="FactionCombatHooks"/> for the world (an
/// <see cref="IWorldFeature"/>, discovered). The catalog is a registered
/// <see cref="FactionTemplateCatalog"/>, else <c>Creatures:FactionTemplateDbcPath</c> (the same
/// source the creature feature uses). With neither, or an empty catalog, nothing is registered and
/// combat keeps the permissive <see cref="CombatHooks.Default"/> (players may attack any non-player
/// unit); this is logged once. Registration uses <see cref="CombatHooks.TryRegister"/>, so it never
/// overrides hooks another feature registered first.
/// </summary>
public sealed class WorldCombatHooksFeature(IServiceProvider services, ILogger<WorldCombatHooksFeature> logger) : IWorldFeature
{
    public void Attach(WorldRuntime world)
    {
        var options = new CreatureOptions();
        services.GetService<IConfiguration>()?.GetSection(CreatureOptions.SectionName).Bind(options);
        FactionTemplateCatalog? catalog = services.GetService<FactionTemplateCatalog>()
            ?? (string.IsNullOrWhiteSpace(options.FactionTemplateDbcPath) ? null : FactionTemplateDbcReader.Load(options.FactionTemplateDbcPath));
        if (catalog is null || catalog.Count == 0)
        {
            logger.LogWarning("No faction templates loaded: combat keeps the permissive default hooks (players can attack any non-player unit)");
            return;
        }

        if (!CombatHooks.TryRegister(world, new FactionCombatHooks(catalog)))
        {
            logger.LogWarning("Combat hooks were already registered by another feature: faction-aware hooks not installed");
        }
    }
}
