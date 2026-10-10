using ArcaneCore.Game.Combat;
using ArcaneCore.Game.Creatures;
using ArcaneCore.Game.Entities;
using ArcaneCore.Game.Instances.Scripts;
using ArcaneCore.Game.Instances.Scripts.TempleOfAhnQiraj;
using ArcaneCore.Game.Maps;
using ArcaneCore.Game.Pets.Control;
using ArcaneCore.Game.Spells;
using ArcaneCore.Game.Spells.Rules;
using ArcaneCore.Game.Spells.Rules.Immunity;
using ArcaneCore.Game.Tests.CreatureAi;
using ArcaneCore.Kernel.WorldData.Creatures;
using Xunit;
using static ArcaneCore.Game.Tests.CreatureTestSupport;
using static ArcaneCore.Game.Tests.Spells.SpellTestKit;

namespace ArcaneCore.Game.Tests.Instances;

public sealed class TwinEmperorTests
{
    private sealed class Arena : IDisposable
    {
        public WorldRuntime World { get; } = TestWorld.CreateRuntime();
        public Map Map { get; }
        public TempleOfAhnQirajInstance Raid { get; }
        public CreatureMapSystem Creatures { get; }
        public FakeCaster Casts { get; } = new();
        public Player Tank { get; }
        public Creature Nilash { get; }
        public Creature Lor { get; }
        public VeknilashAI NilashAi => Assert.IsType<VeknilashAI>(Nilash.AI);
        public VeklorAI LorAi => Assert.IsType<VeklorAI>(Lor.AI);

        public Arena()
        {
            Map = World.GetMap(531);
            Raid = new TempleOfAhnQirajInstance(Map);
            Map.AddUpdater(Raid);
            var content = new CreatureContent(
                [Template(15275, b => b.MinLevelHealth = b.MaxLevelHealth = 10_000),
                 Template(15276, b => b.MinLevelHealth = b.MaxLevelHealth = 8_000), Template(15316), Template(15317)],
                [], [], [], []);
            Creatures = new CreatureMapSystem(Map, content, random: new Random(1),
                aiServices: new CreatureAiServices { Spells = Casts, Hostility = new AlwaysHostile() });
            Map.AddUpdater(Creatures);
            Tank = TestWorld.CreatePlayer(1, -9020, 1176, new FakeSession(), 531);
            Tank.Relocate(-9020, 1176, -104, 0, 0);
            Tank.MaxHealth = Tank.Health = 100_000;
            World.AddPlayer(Tank);
            World.RunTick(0);
            Nilash = Creatures.SpawnTemporary(content.FindTemplate(15275)!, -9023, 1176, -104, 0);
            Lor = Creatures.SpawnTemporary(content.FindTemplate(15276)!, -8868, 1205, -104, 0);
            Casts.Casts.Clear();
        }

        public void Pull()
        {
            Assert.True(NilashAi.AttackStart(Tank));
            Assert.Equal(EncounterState.InProgress, Raid.GetData(TempleOfAhnQirajInstance.Twins));
            Assert.Same(Tank, Lor.Combat.Victim);
        }

        public void Dispose() => World.Dispose();
    }

    [Fact]
    public void LinkedPullMirrorsDamageByPercentage_AndOneDeathCompletesBoth()
    {
        using var arena = new Arena();
        arena.Pull();
        arena.Map.Combat.DealDamage(arena.Tank, arena.Nilash, 1000);
        Assert.Equal(9000u, arena.Nilash.Health);
        Assert.Equal(7200u, arena.Lor.Health);
        arena.Map.Combat.Kill(arena.Tank, arena.Nilash);
        Assert.False(arena.Lor.IsAlive);
        Assert.Equal(EncounterState.Done, arena.Raid.GetData(TempleOfAhnQirajInstance.Twins));
    }

    [Fact]
    public void TheOpeningHitMirrorsBeforeThePullStateIsRecorded()
    {
        using var arena = new Arena();
        Assert.Equal(EncounterState.NotStarted, arena.Raid.GetData(TempleOfAhnQirajInstance.Twins));
        arena.Map.Combat.DealDamage(arena.Tank, arena.Nilash, 1000);
        Assert.Equal(9000u, arena.Nilash.Health);
        Assert.Equal(7200u, arena.Lor.Health);
    }

    [Fact]
    public void VeklorTeleportsBothBosses_ThenTheClosestPlayerReceivesThreat()
    {
        using var arena = new Arena();
        arena.Pull();
        arena.LorAi.OnUpdate(40_001);
        Assert.InRange(arena.Nilash.X, -8869, -8867);
        Assert.InRange(arena.Lor.X, -9024, -9022);
        Assert.Contains(arena.Casts.Casts, c => c.Spell == 800);
        Assert.Contains(arena.Casts.Casts, c => c.Spell == 26638);
        Assert.Null(arena.Nilash.Combat.Victim);
        arena.LorAi.OnUpdate(2000);
        arena.NilashAi.OnUpdate(2000);
        Assert.Same(arena.Tank, arena.Lor.Combat.Victim);
        Assert.Same(arena.Tank, arena.Nilash.Combat.Victim);
    }

    [Fact]
    public void EncounterSchoolImmunitiesCoverSpellDamageAndWhiteMelee()
    {
        using var kit = new ArcaneCore.Game.Tests.Spells.SpellTestKit(
            Spell(900101, Effect(SpellEffectName.SchoolDamage, 100, SpellImplicitTarget.UnitEnemy)) with
            { School = SpellSchool.Frost, RangeIndex = 4, Range = new SpellRange(0, 80) },
            Spell(900102, Effect(SpellEffectName.SchoolDamage, 100, SpellImplicitTarget.UnitEnemy)) with
            { School = SpellSchool.Normal, RangeIndex = 4, Range = new SpellRange(0, 80) });
        Map map = kit.World.GetMap(531);
        map.AddUpdater(new TempleOfAhnQirajInstance(map));
        map.Combat.SpellMitigation = kit.System;
        var content = new CreatureContent([Template(15275), Template(15276)], [], [], [], []);
        var creatures = new CreatureMapSystem(map, content, random: new Random(1),
            aiServices: new CreatureAiServices { Spells = new SpellSystemCreatureCaster(kit.System), Hostility = new AlwaysHostile() });
        map.AddUpdater(creatures);
        Player caster = TestWorld.CreatePlayer(1, 1, 0, new FakeSession(), 531);
        caster.Relocate(1, 0, -104, 0, kit.Now);
        kit.World.AddPlayer(caster);
        kit.World.RunTick(0);
        Creature nilash = creatures.SpawnTemporary(content.FindTemplate(15275)!, 3, 0, -104, 0);
        Creature lor = creatures.SpawnTemporary(content.FindTemplate(15276)!, 5, 0, -104, 0);
        SpellInfo frost = kit.System.Store.Get(900101)!;
        SpellInfo normal = kit.System.Store.Get(900102)!;
        Assert.True(ImmunityRules.IsImmuneToDamage(kit.System, nilash, SpellSchoolMasks.Of(SpellSchool.Frost), frost));
        Assert.False(ImmunityRules.IsImmuneToDamage(kit.System, nilash, SpellSchoolMasks.Of(SpellSchool.Normal), normal));
        Assert.True(ImmunityRules.IsImmuneToDamage(kit.System, lor, SpellSchoolMasks.Of(SpellSchool.Normal), normal));
        Assert.False(ImmunityRules.IsImmuneToDamage(kit.System, lor, SpellSchoolMasks.Of(SpellSchool.Frost), frost));
        Assert.Equal(100u, kit.System.AbsorbDamage(caster, lor, SpellSchoolMasks.Of(SpellSchool.Normal), 100, null));
    }

    [Fact]
    public void HomeArrivalFailsTheFight()
    {
        using var arena = new Arena();
        arena.Pull();
        arena.NilashAi.OnReachedHome();
        Assert.Equal(EncounterState.Fail, arena.Raid.GetData(TempleOfAhnQirajInstance.Twins));
    }
}
