using ArcaneCore.Game.Instances.Scripts;
using ArcaneCore.Game.Instances.Scripts.ZulFarrak;
using ArcaneCore.Game.GameObjects;
using ArcaneCore.Game.Creatures;
using ArcaneCore.Game.Entities;
using ArcaneCore.Game.Spells;
using ArcaneCore.Game.Tests.Pets;
using ArcaneCore.Game.Tests.Spells;
using ArcaneCore.Kernel.WorldData.Creatures;
using Xunit;
using static ArcaneCore.Game.Tests.Spells.SpellTestKit;

namespace ArcaneCore.Game.Tests.Instances;

public sealed class ZulFarrakScriptTests
{
    [Fact]
    public void NekrumAndSezzzizDeaths_CompletePyramid_AndOpenEndDoor()
    {
        using DungeonScriptHarness run = new(map => new ZulFarrakInstance(map),
            [7271, 7796, 7275], [7271, 7796, 7275], (ZulFarrakInstance.EndDoor, GameObjectType.Door));
        var data = Assert.IsType<ZulFarrakInstance>(run.Data);
        Assert.IsType<ZumrahAi>(run.Creature(7271).AI);
        Assert.True(data.StartPyramid());
        Assert.Equal(20_000u, data.PyramidTimerMs);
        run.Kill(7796);
        Assert.Equal(EncounterState.InProgress, data.GetData(ZulFarrakInstance.TypePyramid));
        run.Kill(7275);
        Assert.Equal(EncounterState.Done, data.GetData(ZulFarrakInstance.TypePyramid));
        Assert.Equal(0u, data.PyramidTimerMs);
        Assert.Equal(GameObjectState.Active, run.Object(ZulFarrakInstance.EndDoor).State);
    }

    [Fact]
    public void Zumrah_BecomesHostileAndStartsEncounterWhenAPlayerComesNear()
    {
        using DungeonScriptHarness run = new(map => new ZulFarrakInstance(map), [7271], [7271]);
        var data = Assert.IsType<ZulFarrakInstance>(run.Data);
        var zumrah = run.Creature(7271);
        Assert.IsType<ZumrahAi>(zumrah.AI).MoveInLineOfSight(run.Player);
        Assert.Equal(14u, zumrah.FactionTemplate);
        Assert.Equal(EncounterState.InProgress, data.GetData(ZulFarrakInstance.TypeZumrah));
    }

    [Fact]
    public void Gong_StartsTheImportedEventRelay_AndSummonsGahzrillaOnce()
    {
        var relay = new RelayScriptCatalog(
            [new RelayScriptStep(RelayScriptCatalog.EventRelayId(ZulFarrakInstance.GongEvent), 2000, 0, 10, 7273, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0,
                -10f, -380f, 61.78f, 0, 0, 0)], []);
        using DungeonScriptHarness run = new(map => new ZulFarrakInstance(map), [7273], [], relay,
            (ZulFarrakInstance.GahzrillaGong, GameObjectType.Goober));
        var data = Assert.IsType<ZulFarrakInstance>(run.Data);
        Assert.True(data.TriggerGahzrillaGong(run.Player, run.Object(ZulFarrakInstance.GahzrillaGong)));
        Assert.False(data.TriggerGahzrillaGong(run.Player, run.Object(ZulFarrakInstance.GahzrillaGong)));
        Assert.Equal(EncounterState.InProgress, data.GetData(ZulFarrakInstance.TypeGahzrilla));
        run.Tick(2000);
        Assert.Single(run.Creatures.Creatures, c => c.Template.Entry == 7273);
    }

    /// <summary>
    /// Unlocking (10738) sends event 2609 (Spell.dbc 5875: effect 61 SEND_EVENT, misc value 2609). The importer keeps 2609 twice: as the
    /// dbscripts_on_event script and as its relay copy (CreatureDumpImporter). ProcessEventId_event_spell_unlocking (zulfarrak.cpp:61-75)
    /// lets one wave start and returns true for every later cast, so each Sandfury summon row runs once in all.
    /// </summary>
    [Fact]
    public void UnlockingCast_AgainstTheImportedEvent2609Rows_SpawnsASingleWave()
    {
        const uint Zealot = 8877;
        static RelayScriptStep Summon(uint id) => new(id, 1000, 0, 10, Zealot, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, -10f, -380f, 61.78f, 0, 0, 0);
        var relays = new RelayScriptCatalog([Summon(RelayScriptCatalog.EventRelayId(ZulFarrakInstance.PyramidEvent))], []);
        var events = new DbScriptCatalog([(DbScriptKind.Event, Summon(ZulFarrakInstance.PyramidEvent))]);
        using DungeonScriptHarness run = new(map => new ZulFarrakInstance(map), [Zealot], [], relays, events);
        var data = Assert.IsType<ZulFarrakInstance>(run.Data);
        using var kit = new SpellTestKit(Spell(DungeonScriptHooks.UnlockingSpell,
            Effect(SpellEffectName.SendEvent, 1, misc: (int)ZulFarrakInstance.PyramidEvent)));
        var spells = new SpellSystem(kit.Store, () => kit.Now, spellbook: kit.Spellbook, random: new Random(1));

        Assert.Equal(SpellCastResult.CastOk, spells.CastSpell(run.Player, DungeonScriptHooks.UnlockingSpell, SpellCastTargets.ForSelf(), triggered: true));
        Assert.Equal(EncounterState.InProgress, data.GetData(ZulFarrakInstance.TypePyramid));
        run.Tick(1000);
        Assert.Single(run.Creatures.Creatures, c => c.Template.Entry == Zealot);

        Assert.Equal(SpellCastResult.CastOk, spells.CastSpell(run.Player, DungeonScriptHooks.UnlockingSpell, SpellCastTargets.ForSelf(), triggered: true));
        run.Tick(1000);
        Assert.Single(run.Creatures.Creatures, c => c.Template.Entry == Zealot);
        Assert.Equal(0, run.Creatures.PendingDbScriptSteps);
        Assert.Equal(0, run.Creatures.PendingRelaySteps);
    }

    [Fact]
    public void Zumrah_CastsTheRealSummonSpellAtTheGrave_AndConsumesItAfterCast()
    {
        var caster = new DestinationCaster();
        using DungeonScriptHarness run = new(map => new ZulFarrakInstance(map), [7271], [7271], null,
            new CreatureAiServices { Spells = caster }, (ZulFarrakInstance.ShallowGrave, GameObjectType.Chest));
        var zumrah = run.Creature(7271);
        Assert.True(run.Creatures.AttackStart(zumrah, run.Player));
        run.Tick(1001);
        var grave = run.Object(ZulFarrakInstance.ShallowGrave);
        Assert.Equal((ZumrahAi.SummonZombiesSpell, grave.X, grave.Y, grave.Z), caster.LastCast!.Value);
        Assert.False(run.Object(ZulFarrakInstance.ShallowGrave).IsSpawned);
    }

    [Fact]
    public void DestinationCreatureCaster_UsesTheSpellSystemsSummonWildEffect()
    {
        using var kit = new PetTestKit(
            [Spell(ZumrahAi.SummonZombiesSpell, Effect(SpellEffectName.SummonWild, 1, misc: (int)PetTestKit.WildEntry))],
            creatureSpells: true);
        Creature creature = kit.Creatures.SpawnTemporary(kit.Content.FindTemplate(PetTestKit.NpcCasterEntry)!, 0, 0, 83.5f, 0);
        var caster = new SpellSystemCreatureCaster(kit.Spells.System);
        Assert.Equal(CreatureCastResult.Ok,
            caster.CastAtDestination(creature, ZumrahAi.SummonZombiesSpell, 30, 40, 83.5f, triggered: true));
        Creature summoned = Assert.Single(kit.Creatures.Creatures, c => c.Template.Entry == PetTestKit.WildEntry);
        Assert.Equal((30f, 40f, 83.5f), (summoned.X, summoned.Y, summoned.Z));
    }

    private sealed class DestinationCaster : ICreatureSpellCaster
    {
        public (uint Spell, float X, float Y, float Z)? LastCast { get; private set; }
        public event Action<Unit, Unit, SpellInfo>? SpellHit { add { } remove { } }
        public CreatureCastResult Cast(Creature caster, uint spellId, Unit? target, bool triggered) => CreatureCastResult.Failed;
        public CreatureCastResult CastAtDestination(Creature caster, uint spellId, float x, float y, float z, bool triggered)
        {
            LastCast = (spellId, x, y, z);
            return CreatureCastResult.Ok;
        }
        public bool IsCasting(Creature caster) => false;
        public bool HasAura(Unit unit, uint spellId) => false;
        public void Interrupt(Creature caster) { }
        public void OnCreatureRemoved(Creature creature) { }
    }
}
