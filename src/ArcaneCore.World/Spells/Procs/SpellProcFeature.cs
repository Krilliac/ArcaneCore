using ArcaneCore.Game.Combat;
using ArcaneCore.Game.Maps;
using ArcaneCore.Game.Spells.Procs;
using ArcaneCore.Kernel.WorldData.Procs;
using ArcaneCore.World.Features;
using ArcaneCore.World.Skills;
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

    public SpellProcFeature(IServiceProvider services, ILogger<SpellProcFeature> logger)
    {
        _services = services;
        _logger = logger;
        Table = new SpellProcEventTable(
            ranks: () => _services.GetService<SkillsFeature>()?.Catalog.Ranks,
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

    private void Subscribe(Map map)
    {
        if (map.FindUpdater<MapCombat>() is { } combat)
        {
            SpellFeature spells = _services.GetRequiredService<SpellFeature>();
            combat.UnitKilled += spells.System.OnUnitKilled;
        }
    }

    // With no spell content loaded every listed spell is assumed to exist (vmangos would drop the unknown ones).
    private bool SpellExists(uint spellId)
        => _services.GetService<SpellFeature>()?.System.Store is not { Count: > 0 } store || store.Get(spellId) is not null;
}
