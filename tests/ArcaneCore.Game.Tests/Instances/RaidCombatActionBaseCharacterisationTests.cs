using ArcaneCore.Game.Creatures;
using ArcaneCore.Game.Entities;
using ArcaneCore.Game.Instances;
using ArcaneCore.Game.Instances.Scripts;
using ArcaneCore.Game.Instances.Scripts.MoltenCore;
using ArcaneCore.Game.Maps;
using ArcaneCore.Game.Tests.CreatureAi;
using Xunit;
using static ArcaneCore.Game.Tests.CreatureTestSupport;

namespace ArcaneCore.Game.Tests.Instances;

/// <summary>
/// Characterisation of the behaviours that differ between the three SD2 combat-action bases (RaidBossAI, RaidCreatureAI,
/// ScriptDevBossAI) and must survive their shared-helper extraction: the Cast default target, Below (at or below) next to
/// HealthBelowPct (strictly below), the Fail rule on evade, random-target picks and the timer-draw order.
/// </summary>
public sealed class RaidCombatActionBaseCharacterisationTests
{
    private sealed class MinimumRandom : Random
    {
        public override long NextInt64(long minValue, long maxValue) => minValue;
    }

    /// <summary>A RaidBossAI raid (Blackwing Lair 469, Zul'Gurub 309) with a tank engaged to the boss.</summary>
    private sealed class BossRaid : IDisposable
    {
        public WorldRuntime World { get; } = TestWorld.CreateRuntime();
        public FakeCaster Caster { get; } = new();
        public Creature Boss { get; }
        public Player Tank { get; }

        public BossRaid(uint map, uint entry, Random? random = null)
        {
            Map mapInstance = World.GetMap(map);
            var data = Assert.IsAssignableFrom<InstanceData>(InstanceScriptRegistry.Default.Create(mapInstance));
            data.Initialize();
            mapInstance.AddUpdater(data);
            var creatures = new CreatureMapSystem(mapInstance, Content([Template(entry)], []),
                new CreatureOptions { AggroRate = 0, RespawnPacifyMs = 0 }, random: random ?? new Random(1),
                aiServices: new CreatureAiServices { Spells = Caster, Hostility = new AlwaysHostile() });
            mapInstance.AddUpdater(creatures);
            Tank = TestWorld.CreatePlayer(1, 0, 0, new FakeSession(), map);
            float z = map == 309 ? 50 : 450;
            Tank.Relocate(0, 0, z, 0, 0);
            World.AddPlayer(Tank);
            World.RunTick(0);
            Boss = creatures.SpawnTemporary(Template(entry), 1, 0, z, 0);
            Boss.AI!.AttackStart(Tank);
            Caster.Casts.Clear();
        }

        public void Dispose() => World.Dispose();
    }

    /// <summary>A Molten Core (RaidCreatureAI) arena.</summary>
    private sealed class CoreArena : IDisposable
    {
        public WorldRuntime World { get; } = TestWorld.CreateRuntime();
        public ScriptedInstance Raid { get; }
        public CreatureMapSystem Creatures { get; }
        public FakeCaster Spells { get; } = new();
        public Player Player { get; }

        public CoreArena(Random? random = null)
        {
            Map map = World.GetMap(0);
            Raid = new MoltenCoreInstance(map);
            map.AddUpdater(Raid);
            var content = new ArcaneCore.Kernel.WorldData.Creatures.CreatureContent(
                MoltenCoreInstance.BossEntries.Select(e => Template(e, b => { b.MinLevelHealth = b.MaxLevelHealth = 10000; })), [], [], [], []);
            Creatures = new CreatureMapSystem(map, content, random: random ?? new Random(1),
                aiServices: new CreatureAiServices { Spells = Spells });
            map.AddUpdater(Creatures);
            (Player, _) = CreatureAiTestSupport.AddPlayer(World, 1, 0, 0);
            Player.MaxHealth = Player.Health = 100000;
        }

        public Creature Engage(uint entry)
        {
            Creature boss = Creatures.SpawnTemporary(Creatures.Content.FindTemplate(entry)!, 5, 0, 83.5f, 0);
            Assert.True(boss.AI!.AttackStart(Player));
            Spells.Casts.Clear();
            return boss;
        }

        public void Dispose() => World.Dispose();
    }

    [Fact]
    public void RaidBossAi_CastWithoutTarget_PassesANullTargetToTheCaster()
    {
        using var raid = new BossRaid(469, 12017);
        raid.Boss.AI!.OnUpdate(20000);
        (uint Spell, Unit? Target, bool Triggered) cast = Assert.Single(raid.Caster.Casts, c => c.Spell == 23331);
        Assert.Null(cast.Target);
    }

    [Fact]
    public void RaidCreatureAi_CastWithoutTarget_TargetsTheCreatureItself()
    {
        using var a = new CoreArena();
        Creature boss = a.Engage(11982);
        boss.AI!.OnRespawn();
        (uint Spell, Unit? Target, bool Triggered) cast = Assert.Single(a.Spells.Casts, c => c.Spell == 19449);
        Assert.Same(boss, cast.Target);
        Assert.True(cast.Triggered);
    }

    [Fact]
    public void RaidBossAi_Below_IsAtOrBelow_AndHealthBelowPct_IsStrictlyBelow()
    {
        using var raid = new BossRaid(309, 14507);
        raid.Boss.MaxHealth = 1000;

        // Below(50): 500 of 1000 is at the threshold and casts the snake transformation, 501 does not.
        raid.Boss.Health = 501;
        raid.Boss.AI!.OnUpdate(1);
        Assert.DoesNotContain(raid.Caster.Casts, c => c.Spell == 23849);
        raid.Boss.Health = 500;
        raid.Boss.AI.OnUpdate(1);
        Assert.Single(raid.Caster.Casts, c => c.Spell == 23849);

        // HealthBelowPct(20): 200 of 1000 is exactly 20 percent and does not frenzy, 199 does.
        raid.Boss.Health = 200;
        raid.Boss.AI.OnUpdate(1);
        Assert.DoesNotContain(raid.Caster.Casts, c => c.Spell == 23537);
        raid.Boss.Health = 199;
        raid.Boss.AI.OnUpdate(1);
        Assert.Single(raid.Caster.Casts, c => c.Spell == 23537);
    }

    [Fact]
    public void RaidBossAi_RandomTarget_PicksTheOnlyThreatTarget()
    {
        using var raid = new BossRaid(309, 14517);
        raid.Boss.AI!.OnUpdate(10000);
        (uint Spell, Unit? Target, bool Triggered) cast = Assert.Single(raid.Caster.Casts, c => c.Spell == 24408);
        Assert.Same(raid.Tank, cast.Target);
    }

    [Fact]
    public void RaidCreatureAi_RandomTarget_PicksTheOnlyThreatTarget()
    {
        using var a = new CoreArena();
        Creature boss = a.Engage(12118);
        boss.AI!.OnUpdate(6000);
        (uint Spell, Unit? Target, bool Triggered) cast = Assert.Single(a.Spells.Casts, c => c.Spell == 19460);
        Assert.Same(a.Player, cast.Target);
    }

    [Fact]
    public void RaidBossAi_RepeatDelayIsDrawnFromTheRandomSourceAtCastTime()
    {
        // With every draw at its minimum, Broodlord's Blast Wave (23331) repeats 8000 ms after the cast.
        using var raid = new BossRaid(469, 12017, new MinimumRandom());
        raid.Boss.AI!.OnUpdate(20000);
        Assert.Single(raid.Caster.Casts, c => c.Spell == 23331);
        raid.Boss.AI.OnUpdate(7999);
        Assert.Single(raid.Caster.Casts, c => c.Spell == 23331);
        raid.Boss.AI.OnUpdate(1);
        Assert.Equal(2, raid.Caster.Casts.Count(c => c.Spell == 23331));
    }

    [Fact]
    public void RaidCreatureAi_InitialAndRepeatDelaysAreDrawnFromTheRandomSource()
    {
        // Magmadar's Frenzy (19408): initial Random(6000, 10000) and repeat Random(30000, 35000), both at the minimum here.
        using var a = new CoreArena(new MinimumRandom());
        Creature boss = a.Engage(11982);
        boss.AI!.OnUpdate(5999);
        Assert.DoesNotContain(a.Spells.Casts, c => c.Spell == 19408);
        boss.AI.OnUpdate(1);
        Assert.Single(a.Spells.Casts, c => c.Spell == 19408);
        boss.AI.OnUpdate(29999);
        Assert.Single(a.Spells.Casts, c => c.Spell == 19408);
        boss.AI.OnUpdate(1);
        Assert.Equal(2, a.Spells.Casts.Count(c => c.Spell == 19408));
    }

    [Fact]
    public void RaidCreatureAi_Evade_FailsAnInProgressEncounter_ButKeepsDoneAndSpecial()
    {
        using var a = new CoreArena();
        Creature boss = a.Engage(12118);
        Assert.Equal(EncounterState.InProgress, a.Raid.GetData(0));
        boss.AI!.OnEvade();
        Assert.Equal(EncounterState.Fail, a.Raid.GetData(0));

        a.Raid.SetData(0, EncounterState.Special);
        boss.AI.OnEvade();
        Assert.Equal(EncounterState.Special, a.Raid.GetData(0));

        a.Raid.SetData(0, EncounterState.Done);
        boss.AI.OnEvade();
        Assert.Equal(EncounterState.Done, a.Raid.GetData(0));
    }

    [Fact]
    public void RaidBossAi_Evade_OnlyResetsTheActions_ItDoesNotTouchTheEncounter()
    {
        using var raid = new BossRaid(469, 14601);
        var data = Assert.IsAssignableFrom<InstanceData>(raid.Boss.Map!.FindUpdater<InstanceData>());
        Assert.Equal(EncounterState.InProgress, data.GetData(4));
        raid.Boss.AI!.OnEvade();
        Assert.Equal(EncounterState.InProgress, data.GetData(4));
        raid.Boss.AI.OnReachedHome();
        Assert.Equal(EncounterState.Fail, data.GetData(4));
    }

    [Fact]
    public void RaidCreatureAi_OnUpdate_RunsNoActionsOutOfCombat()
    {
        using var a = new CoreArena();
        Creature boss = a.Creatures.SpawnTemporary(a.Creatures.Content.FindTemplate(12118)!, 5, 0, 83.5f, 0);
        boss.AI!.OnUpdate(60000);
        Assert.Empty(a.Spells.Casts);
    }
}
