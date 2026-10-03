using ArcaneCore.Game.Creatures;
using ArcaneCore.Game.Entities;
using ArcaneCore.Game.Locomotion;
using ArcaneCore.Game.Maps;
using ArcaneCore.Game.Spells;
using ArcaneCore.Game.Tests.Spells;
using ArcaneCore.Kernel.WorldData.Creatures;
using ArcaneCore.Protocol;
using Xunit;
using static ArcaneCore.Game.Tests.CreatureTestSupport;

namespace ArcaneCore.Game.Tests.Locomotion;

/// <summary>Creature speeds (vmangos Unit::UpdateSpeed creature part, Unit.cpp:7063-7094; CreatureDefines.h:140,225-230).</summary>
public sealed class CreatureSpeedTests
{
    private static (WorldRuntime World, Creature Creature, FakeSession Watcher) Scene(CreatureTemplate? template = null)
    {
        template ??= Template();
        CreatureContent content = Content([template], [Spawn(1, template.Entry, 30, 0)]);
        (WorldRuntime world, _, CreatureMapSystem system) = CreateSystem(content);
        var session = new FakeSession();
        Player player = TestWorld.CreatePlayer(1, 0, 0, session);
        world.AddPlayer(player);
        world.RunTick(50);
        Creature creature = Assert.Single(system.Creatures);
        session.Clear();
        return (world, creature, session);
    }

    private static void Slow(Creature creature, int amount)
    {
        creature.Locomotion.Auras.Add(new SpellAura(0, AuraType.ModDecreaseSpeed, amount, 0, 0));
        UnitSpeed.UpdateSpeed(creature, MoveType.Run);
    }

    [Fact]
    public void ACreatureSpawnsWithItsTemplateSpeeds()
    {
        (_, Creature wolf, _) = Scene();
        Assert.Equal(2.5f, wolf.WalkSpeed);
        Assert.Equal(7.0f * 1.14286f, wolf.RunSpeed, 4); // the default speed_run

        (_, Creature fast, _) = Scene(Template(5, _ => { }) with { SpeedRun = 1.5f, SpeedWalk = 1.2f });
        Assert.Equal(7.0f * 1.5f, fast.RunSpeed, 4);
        Assert.Equal(2.5f * 1.2f, fast.WalkSpeed, 4);
    }

    [Fact]
    public void ASlow_ChangesTheRunSpeedAtOnce_AndEveryoneNearbyIsToldWithoutAnAckHandshake()
    {
        (_, Creature wolf, FakeSession watcher) = Scene();
        float before = wolf.RunSpeed;

        Slow(wolf, -50);

        float expected = 7.0f * (0.5f * 1.14286f);
        Assert.Equal(expected, wolf.RunSpeed, 4);
        Assert.Equal(before * 0.5f, wolf.RunSpeed, 3);
        byte[] packet = Assert.Single(watcher.Sent, p => p.Opcode == WorldOpcode.SmsgSplineSetRunSpeed).Payload;
        Assert.Equal(SpeedPackets.BuildSpline(wolf.Guid.Value, wolf.RunSpeed), packet);
        Assert.False(wolf.Locomotion.Pending.HasPending); // server-moved: nobody to ack
        Assert.DoesNotContain(watcher.Sent, p => p.Opcode == WorldOpcode.SmsgForceRunSpeedChange);
    }

    [Fact]
    public void ARemovedSlow_RestoresTheTemplateSpeed()
    {
        (_, Creature wolf, FakeSession watcher) = Scene();
        var aura = new SpellAura(0, AuraType.ModDecreaseSpeed, -50, 0, 0);
        wolf.Locomotion.Auras.Add(aura);
        UnitSpeed.UpdateSpeed(wolf, MoveType.Run);
        watcher.Clear();

        wolf.Locomotion.Auras.Remove(aura);
        UnitSpeed.UpdateSpeed(wolf, MoveType.Run);

        Assert.Equal(7.0f * 1.14286f, wolf.RunSpeed, 4);
        Assert.Single(watcher.Sent, p => p.Opcode == WorldOpcode.SmsgSplineSetRunSpeed);
    }

    [Fact]
    public void TheNextSplineUsesTheLiveSpeed()
    {
        (_, Creature wolf, _) = Scene();
        CreatureSpline fast = wolf.StartSpline(wolf.X + 14f, wolf.Y, wolf.Z, run: true, finalOrientation: null, splineId: 1, clockMs: 0);
        Assert.Equal(1750u, fast.DurationMs); // 14 yards at 8.0 yards per second

        Slow(wolf, -50);
        CreatureSpline slow = wolf.StartSpline(wolf.X + 14f, wolf.Y, wolf.Z, run: true, finalOrientation: null, splineId: 2, clockMs: 0);
        Assert.Equal(3500u, slow.DurationMs);
    }

    [Theory]
    [InlineData(100, 1.0f)]  // 100%
    [InlineData(16, 1.0f)]   // 16.0% is not under 16%
    [InlineData(15, 0.7f)]   // HEALTHLESS_15
    [InlineData(11, 0.7f)]
    [InlineData(10, 0.6f)]   // HEALTHLESS_10
    [InlineData(6, 0.6f)]
    [InlineData(5, 0.5f)]    // HEALTHLESS_5
    [InlineData(1, 0.5f)]
    public void TheWoundedSlowdown_FollowsTheHealthPercent(uint healthOfHundred, float factor)
    {
        (_, Creature wolf, _) = Scene(Template(7, t => { t.MinLevelHealth = 100; t.MaxLevelHealth = 100; }));
        wolf.Health = healthOfHundred;

        UnitSpeed.UpdateSpeed(wolf, MoveType.Run);

        Assert.Equal(7.0f * 1.14286f * factor, wolf.RunSpeed, 3);
        Assert.Equal(2.5f, wolf.WalkSpeed); // run only
    }

    [Fact]
    public void WorldBossesAndTheNoWoundedSlowdownFlag_AreNotSlowedByInjury()
    {
        CreatureTemplate boss = Template(8, t => { t.MinLevelHealth = 100; t.MaxLevelHealth = 100; t.Rank = (uint)CreatureRank.WorldBoss; });
        (_, Creature bossCreature, _) = Scene(boss);
        bossCreature.Health = 3;
        UnitSpeed.UpdateSpeed(bossCreature, MoveType.Run);
        Assert.Equal(7.0f * 1.14286f, bossCreature.RunSpeed, 3);

        CreatureTemplate steady = Template(9, t => { t.MinLevelHealth = 100; t.MaxLevelHealth = 100; }) with { StaticFlags2 = 0x40 };
        (_, Creature steadyCreature, _) = Scene(steady);
        steadyCreature.Health = 3;
        UnitSpeed.UpdateSpeed(steadyCreature, MoveType.Run);
        Assert.Equal(7.0f * 1.14286f, steadyCreature.RunSpeed, 3);
    }

    [Fact]
    public void TheSnareAndTheWoundedSlowdown_Multiply()
    {
        (_, Creature wolf, _) = Scene(Template(10, t => { t.MinLevelHealth = 100; t.MaxLevelHealth = 100; }));
        wolf.Health = 10;

        Slow(wolf, -50);

        Assert.Equal(7.0f * (0.5f * 1.14286f * 0.6f), wolf.RunSpeed, 3);
    }

    [Fact]
    public void ACreature_WithASpeedAuraFromTheSpellSystem_IsRecomputed()
    {
        // The aura module acts on creatures too: a +50% run aura on a wolf (template 1.14286) gives 1.5 * 1.14286.
        using var kit = new SpellTestKit(SpellTestKit.Spell(950020,
            SpellTestKit.Effect(SpellEffectName.ApplyAura, 50, aura: AuraType.ModIncreaseSpeed)) with { Duration = new SpellDuration(-1, 0, -1), SpellVisual = 1 });
        CreatureTemplate template = Template();
        CreatureContent content = Content([template], [Spawn(1, template.Entry, 30, 0)]);
        var system = new CreatureMapSystem(kit.World.GetMap(0), content, null, random: new Random(1));
        kit.World.GetMap(0).AddUpdater(system);
        (Player player, _) = kit.AddPlayer(1);
        kit.World.RunTick(50);
        Creature wolf = Assert.Single(system.Creatures);

        kit.System.CastSpell(wolf, 950020, SpellCastTargets.ForSelf(), triggered: true);

        Assert.Equal(7.0f * (1.14286f * 1.5f), wolf.RunSpeed, 3);
        Assert.NotNull(player);
    }
}
