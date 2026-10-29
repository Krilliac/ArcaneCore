using ArcaneCore.Data.Talents;
using ArcaneCore.Game.Combat;
using ArcaneCore.Game.Maps;
using ArcaneCore.Game.Spells.Procs;
using ArcaneCore.Game.Talents;
using ArcaneCore.Kernel.Skills;
using ArcaneCore.Kernel.Talents;
using ArcaneCore.Kernel.WorldData.Procs;
using ArcaneCore.World.Features;
using ArcaneCore.World.Skills;
using ArcaneCore.World.Talents;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;

namespace ArcaneCore.World.Spells.Procs;

/// <summary>
/// Binds the proc engine to the world (docs/areas/procs.md): loads <c>spell_proc_event</c> (vmangos SpellMgr::LoadSpellProcEvents,
/// Spells/SpellMgr.cpp:316-365) into <see cref="Table"/> when a <see cref="ISpellProcEventDataStore"/> is registered and hands it to the spell
/// system, and forwards every map's kills to the killer's KILL procs (vmangos Unit::Kill, Unit.cpp:1102-1104). White swings and the weapon hit
/// reach the engine through map combat and the item proc feature; spell hits, misses, reflects and ticks inside the spell system. Without a
/// store the table is empty and every proc aura runs on its Spell.dbc procFlags and procChance. Features attach in full-name order, so this one
/// follows <see cref="SpellFeature"/>.
/// </summary>
public sealed class SpellProcFeature : IWorldFeature
{
    private readonly IServiceProvider _services;
    private readonly ILogger<SpellProcFeature> _logger;
    private readonly HashSet<MapCombat> _subscribed = new(ReferenceEqualityComparer.Instance);

    public SpellProcFeature(IServiceProvider services, ILogger<SpellProcFeature> logger)
    {
        _services = services;
        _logger = logger;
        Table = new SpellProcEventTable(
            ranks: ResolveRanks,
            spellExists: SpellExists,
            report: message => _logger.LogWarning("{Message}", message));
    }

    /// <summary>The live spell_proc_event table (empty until the rows are loaded).</summary>
    public SpellProcEventTable Table { get; }

    public void Attach(WorldRuntime world)
    {
        ArgumentNullException.ThrowIfNull(world);
        SpellFeature spells = _services.GetRequiredService<SpellFeature>();
        spells.System.ProcEvents = Table;
        world.MapCreated += Subscribe;
        world.MapUnloading += Unsubscribe;
        foreach (Map map in world.Maps)
        {
            Subscribe(map);
        }

        using IServiceScope scope = _services.CreateScope();
        ISpellProcEventDataStore? store = scope.ServiceProvider.GetService<ISpellProcEventDataStore>();
        if (store is null)
        {
            _logger.LogInformation("no spell_proc_event store registered; proc auras use their Spell.dbc procFlags and procChance only");
            return;
        }

        SpellProcEventContent content = store.LoadAsync().GetAwaiter().GetResult();
        Table.Replace(content);
        _logger.LogInformation("loaded {Count} spell proc event conditions", content.Count);
    }

    /// <summary>
    /// Follow a map's kills once: a map reached through both <see cref="WorldRuntime.MapCreated"/> and the attach-time map walk is not
    /// subscribed twice (a second subscription would fire every KILL proc twice).
    /// </summary>
    private void Subscribe(Map map)
    {
        if (map.FindUpdater<MapCombat>() is { } combat && _subscribed.Add(combat))
        {
            SpellFeature spells = _services.GetRequiredService<SpellFeature>();
            combat.UnitKilled += spells.System.OnUnitKilled;
        }
    }

    /// <summary>An unloading map's combat is released, so this feature does not keep the dead map's combat followed (as <c>SpellFeature.UnsubscribeCombat</c>).</summary>
    private void Unsubscribe(Map map)
    {
        if (map.FindUpdater<MapCombat>() is { } combat && _subscribed.Remove(combat))
        {
            SpellFeature spells = _services.GetRequiredService<SpellFeature>();
            combat.UnitKilled -= spells.System.OnUnitKilled;
        }
    }

    // With no spell content loaded every listed spell is assumed to exist (vmangos would drop the unknown ones).
    private bool SpellExists(uint spellId)
        => _services.GetService<SpellFeature>()?.System.Store is not { Count: > 0 } store || store.Get(spellId) is not null;

    private SpellRankChains? ResolveRanks()
    {
        SkillCatalog? catalog = _services.GetService<SkillCatalog>() ?? _services.GetService<SkillsFeature>()?.Catalog;
        if (catalog is { Ranks: { Count: > 0, HasTalents: true } ranks })
        {
            return ranks;
        }

        IEnumerable<TalentRecord>? talents = _services.GetService<TalentCatalog>()?.Talents
            ?? _services.GetService<TalentFeature>()?.Catalog?.Talents;
        if (talents is null
            && _services.GetService<IConfiguration>()?[$"{TalentOptions.Section}:{nameof(TalentOptions.TalentDbcPath)}"] is { Length: > 0 } talentPath
            && File.Exists(talentPath))
        {
            talents = TalentDbcReaders.LoadTalents(talentPath);
        }

        if (talents is null)
        {
            return catalog?.Ranks;
        }

        return new SpellRankChains(catalog?.Abilities ?? [], SpellExists, talents);
    }
}
