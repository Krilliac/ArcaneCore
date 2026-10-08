using ArcaneCore.Game.Instances.Scripts;
using ArcaneCore.Game.Instances.Scripts.ZulFarrak;
using ArcaneCore.Game.GameObjects;
using ArcaneCore.Game.Creatures;
using ArcaneCore.Game.Entities;
using ArcaneCore.Game.Spells;
using ArcaneCore.Game.Tests.Pets;
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
            [new RelayScriptStep(1_002_488, 2000, 0, 10, 7273, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0,
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
