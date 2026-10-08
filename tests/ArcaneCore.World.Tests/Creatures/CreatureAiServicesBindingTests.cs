using ArcaneCore.Game;
using ArcaneCore.Game.Creatures;
using ArcaneCore.Game.Entities;
using ArcaneCore.Game.Spells;
using ArcaneCore.Kernel.Npc;
using ArcaneCore.Kernel.Reputation;
using ArcaneCore.Kernel.WorldData.Creatures;
using ArcaneCore.World.Creatures;
using Microsoft.Extensions.DependencyInjection;
using Xunit;

namespace ArcaneCore.World.Tests.Creatures;

/// <summary>
/// The creature AI services are bound from the container by reflection over the properties of
/// <see cref="CreatureAiServices"/>, so a later AI slice adds a seam without editing the creature feature.
/// </summary>
public sealed class CreatureAiServicesBindingTests
{
    private sealed class RecordingCaster : ICreatureSpellCaster
    {
        public event Action<Unit, Unit, SpellInfo>? SpellHit
        {
            add { }
            remove { }
        }

        public CreatureCastResult Cast(Creature caster, uint spellId, Unit? target, bool triggered) => CreatureCastResult.Ok;

        public bool IsCasting(Creature caster) => false;

        public bool HasAura(Unit unit, uint spellId) => false;

        public void Interrupt(Creature caster)
        {
        }

        public void OnCreatureRemoved(Creature creature)
        {
        }
    }

    private sealed class FixedHostility : ICreatureHostility
    {
        public bool IsHostile(Creature creature, Unit target) => true;

        public bool CanAssist(Creature helper, Creature caller) => true;
    }

    private static IServiceProvider Container(Action<ServiceCollection>? configure = null)
    {
        var collection = new ServiceCollection();
        configure?.Invoke(collection);
        return collection.BuildServiceProvider();
    }

    [Fact]
    public void WithNothingRegistered_TheDefaultsApply()
    {
        CreatureAiServices services = CreatureAiServicesBinder.Build(Container(), new CreatureOptions());

        Assert.IsType<FactionCreatureHostility>(services.Hostility);
        Assert.Null(services.Spells);
        Assert.NotNull(services.Factory);
    }

    /// <summary>
    /// vmangos Unit::GetTeam (Unit.cpp:4960-4973), used by GuardMgr::GetTeam: the faction template's Faction.dbc row and its team
    /// (m_parentFactionID) field, 469 Alliance or 67 Horde; anything else, or a missing row, is no team.
    /// </summary>
    [Fact]
    public void TheCreatureTeam_ComesFromFactionDbcsTeamField()
    {
        static FactionRecord Faction(uint id, uint parent) => new(id, -1, [0, 0, 0, 0], [0, 0, 0, 0], [0, 0, 0, 0], [0, 0, 0, 0], parent);
        var templates = new FactionTemplateCatalog([
            new FactionTemplateRecord(12, 72, 0, 2, 2, 12),   // Stormwind
            new FactionTemplateRecord(29, 76, 0, 4, 4, 10),   // Orgrimmar
            new FactionTemplateRecord(14, 16, 0, 8, 0, 1),    // Monster
            new FactionTemplateRecord(35, 999, 0, 0, 0, 0),   // a faction Faction.dbc lacks
        ]);
        var factions = new FactionCatalog([Faction(72, 469), Faction(76, 67), Faction(16, 0)]);
        CreatureAiServices services = CreatureAiServicesBinder.Build(
            Container(c => c.AddSingleton(templates).AddSingleton(factions)), new CreatureOptions());

        Func<Creature, Team?> teamOf = Assert.IsType<Func<Creature, Team?>>(services.TeamOf);
        Assert.Equal(Team.Alliance, teamOf(Creature(12)));
        Assert.Equal(Team.Horde, teamOf(Creature(29)));
        Assert.Null(teamOf(Creature(14)));
        Assert.Null(teamOf(Creature(35)));
        Assert.Null(teamOf(Creature(9999)));
    }

    private static Creature Creature(uint factionTemplate)
    {
        var template = new CreatureTemplate { Entry = 1, Name = "townsman", Faction = factionTemplate, MinLevelHealth = 20, MaxLevelHealth = 20 };
        return new Creature(1, template, spawn: null, new CreatureContent([template], [], [], [], []), new Random(1));
    }

    [Fact]
    public void ARegisteredSpellCaster_IsBound_EvenWithoutTheSpellFeature()
    {
        // Only a SpellFeature adapter used to be possible; any registered ICreatureSpellCaster is now bound.
        var caster = new RecordingCaster();
        CreatureAiServices services = CreatureAiServicesBinder.Build(Container(c => c.AddSingleton<ICreatureSpellCaster>(caster)), new CreatureOptions());

        Assert.Same(caster, services.Spells);
    }

    [Fact]
    public void RegisteredHostilityAndFactory_ReplaceTheDefaults()
    {
        var hostility = new FixedHostility();
        var factory = new CreatureAiFactory();
        CreatureAiServices services = CreatureAiServicesBinder.Build(
            Container(c => c.AddSingleton<ICreatureHostility>(hostility).AddSingleton(factory)), new CreatureOptions());

        Assert.Same(hostility, services.Hostility);
        Assert.Same(factory, services.Factory);
    }

    [Fact]
    public void ARegisteredUnitSpellQueries_IsBound_ThroughTheSameReflectionPath()
    {
        var queries = new FixedQueries();
        CreatureAiServices services = CreatureAiServicesBinder.Build(Container(c => c.AddSingleton<IUnitSpellQueries>(queries)), new CreatureOptions());

        Assert.Same(queries, services.UnitSpells);
    }

    private sealed class FixedQueries : IUnitSpellQueries
    {
        public int GetAuraStacks(Unit unit, uint spellId) => 1;

        public bool IsCasting(Unit unit) => false;
    }

    [Fact]
    public void ARegisteredServiceOfAnUnrelatedType_ChangesNothing()
    {
        CreatureAiServices services = CreatureAiServicesBinder.Build(Container(c => c.AddSingleton("unrelated")), new CreatureOptions());

        Assert.IsType<FactionCreatureHostility>(services.Hostility);
        Assert.Null(services.Spells);
    }
}
