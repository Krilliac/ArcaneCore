using ArcaneCore.Game.Combat;
using ArcaneCore.Game.Creatures;
using ArcaneCore.Game.Entities;
using ArcaneCore.Game.Instances.Scripts;
using ArcaneCore.Game.Instances.Scripts.TempleOfAhnQiraj;
using ArcaneCore.Game.Maps;
using ArcaneCore.Game.Spells;
using ArcaneCore.Game.Tests.CreatureAi;
using ArcaneCore.Game.Tests.Spells;
using ArcaneCore.Kernel.WorldData.Creatures;
using Xunit;
using static ArcaneCore.Game.Tests.CreatureTestSupport;
using static ArcaneCore.Game.Tests.Spells.SpellTestKit;

namespace ArcaneCore.Game.Tests.Instances;

public sealed class HuhuranTests
{
    [Fact]
    public void HuhuranStartsCombatWhenAnAttackablePlayerEntersHerScriptedSightRange()
    {
        using WorldRuntime world = TestWorld.CreateRuntime();
        Map map = world.GetMap(531);
        var raid = new TempleOfAhnQirajInstance(map);
        map.AddUpdater(raid);
        var content = new CreatureContent([Template(15509)], [], [], [], []);
        var creatures = new CreatureMapSystem(map, content, random: new Random(1),
            aiServices: new CreatureAiServices { Spells = new FakeCaster(), Hostility = new AlwaysHostile() });
        map.AddUpdater(creatures);
        Player player = TestWorld.CreatePlayer(1, 81, 0, new FakeSession(), 531);
        player.Relocate(81, 0, 450, 0, 0);
        world.AddPlayer(player);
        world.RunTick(0);
        Creature boss = creatures.SpawnTemporary(content.FindTemplate(15509)!, 0, 0, 450, 0);
        var ai = Assert.IsType<HuhuranAI>(boss.AI);

        ai.MoveInLineOfSight(player);
        Assert.Null(boss.Combat.Victim);
        player.Relocate(70, 0, 450, 0, 0);
        ai.MoveInLineOfSight(player);
        Assert.Same(player, boss.Combat.Victim);
        Assert.Equal(EncounterState.InProgress, raid.GetData(TempleOfAhnQirajInstance.Huhuran));
    }

    [Fact]
    public void PullTimersBerserkWipeAndDeathDriveTheEncounter()
    {
        using WorldRuntime world = TestWorld.CreateRuntime();
        Map map = world.GetMap(531);
        var raid = new TempleOfAhnQirajInstance(map);
        map.AddUpdater(raid);
        var content = new CreatureContent([Template(15509, b => b.MinLevelHealth = b.MaxLevelHealth = 10000)], [], [], [], []);
        var casts = new FakeCaster();
        var creatures = new CreatureMapSystem(map, content, random: new Random(1),
            aiServices: new CreatureAiServices { Spells = casts, Hostility = new AlwaysHostile() });
        map.AddUpdater(creatures);
        Player tank = TestWorld.CreatePlayer(1, 1, 0, new FakeSession(), 531);
        tank.Relocate(1, 0, 450, 0, 0);
        world.AddPlayer(tank);
        world.RunTick(0);
        Creature boss = creatures.SpawnTemporary(content.FindTemplate(15509)!, 0, 0, 450, 0);
        var ai = Assert.IsType<HuhuranAI>(boss.AI);
        Assert.True(ai.AttackStart(tank));
        Assert.Equal(EncounterState.InProgress, raid.GetData(TempleOfAhnQirajInstance.Huhuran));

        ai.OnUpdate(8_000);
        Assert.Contains(casts.Casts, c => c.Spell == 26050 && ReferenceEquals(c.Target, tank));
        ai.OnUpdate(30_000);
        Assert.Contains(casts.Casts, c => c.Spell == 26051);
        Assert.Contains(casts.Casts, c => c.Spell == 26180);
        Assert.Contains(casts.Casts, c => c.Spell == 26053);
        boss.Health = 3000;
        ai.OnUpdate(1);
        ai.OnUpdate(1);
        Assert.Contains(casts.Casts, c => c.Spell == 26068);

        ai.OnEvade();
        ai.OnReachedHome();
        Assert.Equal(EncounterState.Fail, raid.GetData(TempleOfAhnQirajInstance.Huhuran));
        boss.Health = boss.MaxHealth;
        Assert.True(ai.AttackStart(tank));
        casts.Casts.Clear();
        ai.OnUpdate(300_001);
        ai.OnUpdate(1);
        Assert.Contains(casts.Casts, c => c.Spell == 26068); // five-minute fallback, without low health
        map.Combat.Kill(tank, boss);
        Assert.Equal(EncounterState.Done, raid.GetData(TempleOfAhnQirajInstance.Huhuran));
    }

    [Fact]
    public void PoisonVolleyAndWyvernStingPreferTheClosestAreaTargets()
    {
        static SpellInfo Area(uint id, SpellEffectName effect, AuraType aura, uint cap) => Spell(id,
            Effect(effect, 10, SpellImplicitTarget.EnumUnitsEnemyAoeAtSrcLoc, aura) with { Radius = 80 }) with
        {
            MaxAffectedTargets = cap, Duration = new SpellDuration(10_000, 0, 10_000),
            StartRecoveryCategory = 0, StartRecoveryTime = 0,
        };
        using var kit = new SpellTestKit(
            Area(26052, SpellEffectName.SchoolDamage, AuraType.None, 2),
            Area(26180, SpellEffectName.ApplyAura, AuraType.ModStun, 2));
        var relations = new FakeRelations();
        kit.System.Relations = relations;
        (Player caster, _) = kit.AddPlayer(1);
        (Player near, _) = kit.AddPlayer(2, 5);
        (Player middle, _) = kit.AddPlayer(3, 10);
        (Player far, _) = kit.AddPlayer(4, 20);
        foreach (Player player in new[] { near, middle, far }) relations.Hostile.Add(player.Guid);
        kit.World.RunTick(0);

        Assert.Equal(SpellCastResult.CastOk, kit.System.CastSpell(caster, 26052, SpellCastTargets.ForSelf(), triggered: true));
        Assert.True(near.Health < near.MaxHealth);
        Assert.True(middle.Health < middle.MaxHealth);
        Assert.Equal(far.MaxHealth, far.Health);
        Assert.Equal(SpellCastResult.CastOk, kit.System.CastSpell(caster, 26180, SpellCastTargets.ForSelf(), triggered: true));
        Assert.True(kit.System.HasAura(near, 26180));
        Assert.True(kit.System.HasAura(middle, 26180));
        Assert.False(kit.System.HasAura(far, 26180));
    }

    [Fact]
    public void FrenzyInTheLiveSpellSystemTriggersPoisonVolleyOnTheClosestPlayers()
    {
        using var kit = new SpellTestKit(
            Spell(26051, Effect(SpellEffectName.ApplyAura, 0, aura: AuraType.PeriodicTriggerSpell,
                amplitude: 1000, trigger: 26052)) with { Duration = new SpellDuration(4000, 0, 4000) },
            Spell(26052, Effect(SpellEffectName.SchoolDamage, 10,
                SpellImplicitTarget.EnumUnitsEnemyAoeAtSrcLoc) with { Radius = 80 }) with { MaxAffectedTargets = 2 });
        Map map = kit.World.GetMap(531);
        var raid = new TempleOfAhnQirajInstance(map);
        map.AddUpdater(raid);
        map.Combat.SpellMitigation = kit.System;
        var relations = new FakeRelations();
        kit.System.Relations = relations;
        var content = new CreatureContent([Template(15509, b => b.MinLevelHealth = b.MaxLevelHealth = 10000)], [], [], [], []);
        var creatures = new CreatureMapSystem(map, content, random: new Random(1),
            aiServices: new CreatureAiServices { Spells = new SpellSystemCreatureCaster(kit.System), Hostility = new AlwaysHostile() });
        map.AddUpdater(creatures);
        Player[] targets =
        [
            TestWorld.CreatePlayer(1, 5, 0, new FakeSession(), 531),
            TestWorld.CreatePlayer(2, 10, 0, new FakeSession(), 531),
            TestWorld.CreatePlayer(3, 20, 0, new FakeSession(), 531),
        ];
        foreach (Player player in targets)
        {
            player.Relocate(player.X, 0, 450, 0, kit.Now);
            kit.World.AddPlayer(player);
            relations.Hostile.Add(player.Guid);
        }
        kit.World.RunTick(0);
        Creature boss = creatures.SpawnTemporary(content.FindTemplate(15509)!, 0, 0, 450, 0);
        var ai = Assert.IsType<HuhuranAI>(boss.AI);
        Assert.True(ai.AttackStart(targets[0]));

        ai.OnUpdate(40_000);
        Assert.True(kit.System.HasAura(boss, 26051));
        kit.Advance(1000);

        Assert.True(targets[0].Health < targets[0].MaxHealth);
        Assert.True(targets[1].Health < targets[1].MaxHealth);
        Assert.Equal(targets[2].MaxHealth, targets[2].Health);
    }

    [Theory]
    [InlineData(AuraRemoveMode.Expire, 500u)]
    [InlineData(AuraRemoveMode.Dispel, 3000u)]
    public void WyvernStingRemovalDealsTheCorrectDamage(AuraRemoveMode mode, uint damage)
    {
        using var kit = new SpellTestKit(
            Spell(26180, Effect(SpellEffectName.ApplyAura, 0, SpellImplicitTarget.UnitEnemy, AuraType.ModStun)) with
            { Duration = new SpellDuration(10_000, 0, 10_000), RangeIndex = 4, Range = new SpellRange(0, 80) },
            Spell(26233, Effect(SpellEffectName.SchoolDamage, 1, SpellImplicitTarget.UnitEnemy)) with
            { RangeIndex = 4, Range = new SpellRange(0, 80) });
        var relations = new FakeRelations();
        kit.System.Relations = relations;
        (Player caster, _) = kit.AddPlayer(1);
        (Player target, _) = kit.AddPlayer(2, 5);
        relations.Hostile.Add(target.Guid);
        target.MaxHealth = 10_000;
        target.Health = 10_000;

        Assert.Equal(SpellCastResult.CastOk, kit.System.CastSpell(caster, 26180,
            SpellCastTargets.ForUnit(target.Guid), triggered: true));
        kit.System.RemoveAuras(target, 26180, mode);

        Assert.Equal(10_000u - damage, target.Health);
    }
}
