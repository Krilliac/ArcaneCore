using ArcaneCore.Game.Maps;
using ArcaneCore.Game.WorldState;
using ArcaneCore.Game.WorldState.Exploration;
using ArcaneCore.Kernel.WorldData.WorldState;
using ArcaneCore.World.Features;
using ArcaneCore.World.Progression;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;

namespace ArcaneCore.World.WorldState;

/// <summary>
/// The world daemon's exploration feature (docs/areas/world-state.md): binds <c>World:Exploration</c>,
/// loads <c>exploration_basexp</c> (a startup warning, once, when it is empty: exploration then
/// gives 0 XP but still reveals the map), and installs the <see cref="ExplorationService"/> the zone
/// tracker calls when a player moves. Experience goes through the progression feature's
/// <see cref="ArcaneCore.Game.Npc.IPlayerExperience"/>.
/// </summary>
public sealed class ExplorationFeature(IServiceProvider services, ILogger<ExplorationFeature> logger) : IWorldFeature
{
    private WorldStateHooks? _hooks;

    public void Attach(WorldRuntime world)
    {
        ArgumentNullException.ThrowIfNull(world);
        _hooks = WorldStateHooks.For(world);
        services.GetService<IConfiguration>()?.GetSection(ExplorationOptions.SectionName).Bind(_hooks.ExplorationSettings);

        using (IServiceScope scope = services.CreateScope())
        {
            IWorldStateDataStore? store = scope.ServiceProvider.GetService<IWorldStateDataStore>();
            WorldStateContent content = store is null ? WorldStateContent.Empty : store.LoadAsync().GetAwaiter().GetResult();
            ReplaceBaseXp(content.BaseXp);
        }

        if (_hooks.ExplorationBaseXp.Count == 0)
        {
            logger.LogWarning("exploration_basexp is empty: discovering an area reveals it but gives no experience (import the table, docs/areas/world-state.md)");
        }
        else
        {
            logger.LogInformation("loaded {Count} exploration base XP rows", _hooks.ExplorationBaseXp.Count);
        }

        ProgressionFeature? progression = services.GetService<ProgressionFeature>();
        _hooks.Explorer = new ExplorationService(
            _hooks,
            () => progression?.Progression,
            () => progression?.Progression.Options.MaxPlayerLevel ?? ExplorationXp.DefaultMaxPlayerLevel,
            logger);
    }

    /// <summary>Swap the base-XP table (startup, and the reload coordinator).</summary>
    public void ReplaceBaseXp(IEnumerable<ExplorationBaseXpRecord> rows)
    {
        WorldStateHooks hooks = _hooks ?? throw new InvalidOperationException("the exploration feature is not attached");
        hooks.ExplorationBaseXp = new ExplorationBaseXpTable(rows.Select(r => new KeyValuePair<uint, uint>(r.Level, r.BaseXp)));
    }
}
