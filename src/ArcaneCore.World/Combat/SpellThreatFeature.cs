using ArcaneCore.Game.Combat.Threat;
using ArcaneCore.Game.Maps;
using ArcaneCore.Kernel.WorldData.Threat;
using ArcaneCore.World.Features;
using ArcaneCore.World.Skills;
using ArcaneCore.World.Spells;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;

namespace ArcaneCore.World.Combat;

/// <summary>
/// Loads the <c>spell_threat</c> table (vmangos SpellMgr::LoadSpellThreats, Spells/SpellMgr.cpp:834-875) when a
/// <see cref="ISpellThreatDataStore"/> is registered and keeps it in the <see cref="Table"/> the threat feature binds to every map
/// (<see cref="ThreatFeature"/>); <c>.reload spell_threats</c> swaps the rows. Without a store the table is empty: spell multipliers are 1 and
/// spells add no flat threat. The higher ranks of a listed spell come from the skill content's rank chains and the spells that exist from the
/// spell store, both read when the table is first used, so the load order of the features does not matter.
/// </summary>
public sealed class SpellThreatFeature : IWorldFeature
{
    private readonly IServiceProvider _services;
    private readonly ILogger<SpellThreatFeature> _logger;

    public SpellThreatFeature(IServiceProvider services, ILogger<SpellThreatFeature> logger)
    {
        _services = services;
        _logger = logger;
        Table = new SpellThreatTable(
            ranks: () => _services.GetService<SkillsFeature>()?.Catalog.Ranks,
            spellExists: SpellExists,
            report: message => _logger.LogWarning("{Message}", message));
    }

    /// <summary>The live table (empty until the rows are loaded).</summary>
    public SpellThreatTable Table { get; }

    public void Attach(WorldRuntime world)
    {
        ArgumentNullException.ThrowIfNull(world);
        using IServiceScope scope = _services.CreateScope();
        ISpellThreatDataStore? store = scope.ServiceProvider.GetService<ISpellThreatDataStore>();
        if (store is null)
        {
            _logger.LogInformation("no spell_threat store registered; spells keep a threat multiplier of 1 and add no flat threat");
            return;
        }

        SpellThreatContent content = store.LoadAsync().GetAwaiter().GetResult();
        Table.Replace(content);
        _logger.LogInformation("loaded {Count} spell threat entries", content.Count);
    }

    // With no spell content loaded every listed spell is assumed to exist (vmangos would drop the unknown ones).
    private bool SpellExists(uint spellId)
        => _services.GetService<SpellFeature>()?.System.Store is not { Count: > 0 } store || store.Get(spellId) is not null;
}
