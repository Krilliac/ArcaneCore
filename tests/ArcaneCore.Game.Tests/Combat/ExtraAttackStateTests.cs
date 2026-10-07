using ArcaneCore.Game.Combat;
using ArcaneCore.Game.Entities;
using ArcaneCore.Game.Maps;
using ArcaneCore.Game.Spells;
using ArcaneCore.Game.Tests.Spells;
using Xunit;

namespace ArcaneCore.Game.Tests.ExtraAttacks;

public sealed class ExtraAttackStateTests
{
    [Fact]
    public void AddExtraAttacksSpellEffect_QueuesTargetBatch()
    {
        const uint spellId = 49701;
        using var kit = new SpellTestKit(SpellTestKit.Spell(spellId,
            SpellTestKit.Effect(SpellEffectName.AddExtraAttacks, 3)));
        (Player player, _) = kit.AddPlayer(1);

        Assert.Equal(SpellCastResult.CastOk, kit.System.CastSpell(player, spellId, SpellCastTargets.ForSelf(), triggered: true));
        Assert.Equal((uint)3, player.Combat.ExtraAttacks);
    }

    [Fact]
    public void MeleeScheduler_DrainsQueuedAttacksThroughNormalSwingPath()
    {
        (WorldRuntime world, Map map, _, _) = CombatTestKit.CreateWorld();
        Player player = CombatTestKit.AddPlayer(world, 3, 0, 0, new FakeSession(3));
        var victim = new CombatTestUnit();
        victim.Spawn(map, 1, 0);
        Assert.True(map.Combat.Attack(player, victim));
        int swings = 0;
        map.Combat.MeleeSwingResolved += _ => swings++;
        player.Combat.QueueExtraAttacks(2);

        player.Combat.SetAttackTimer(WeaponAttackType.BaseAttack, 500);
        map.Combat.Update(0);
        Assert.True(swings > 0);
        Assert.Equal(0u, player.Combat.ExtraAttacks);
        Assert.Equal(2, swings); // queued base swings; ordinary timer remains deferred
        Assert.True(player.Combat.GetAttackTimer(WeaponAttackType.BaseAttack) > 0);
    }

    [Fact]
    public void QueueExtraAttacks_IsBoundedAndConsumesOneAtATime()
    {
        Player player = TestWorld.CreatePlayer(1, 0, 0, new FakeSession(1));

        Assert.True(player.Combat.QueueExtraAttacks(101));
        Assert.Equal((uint)100, player.Combat.ExtraAttacks);
        Assert.True(player.Combat.HasPendingExtraAttacks);
        Assert.True(player.Combat.ConsumeExtraAttack());
        Assert.Equal((uint)99, player.Combat.ExtraAttacks);
    }

    [Fact]
    public void QueueExtraAttacks_RejectsNestedQueueWhileLockedOrPending()
    {
        Player player = TestWorld.CreatePlayer(2, 0, 0, new FakeSession(2));

        Assert.True(player.Combat.QueueExtraAttacks(2));
        Assert.False(player.Combat.QueueExtraAttacks(3));
        Assert.True(player.Combat.ExtraAttacksLocked == false);
    }

    [Fact]
    public void ResetExtraAttacks_ClearsCountWithoutUnlockingActiveDrain()
    {
        Player player = TestWorld.CreatePlayer(4, 0, 0, new FakeSession(4));
        Assert.True(player.Combat.QueueExtraAttacks(2));
        player.Combat.LockExtraAttacks();

        player.Combat.ResetExtraAttacks();

        Assert.Equal(0u, player.Combat.ExtraAttacks);
        Assert.True(player.Combat.ExtraAttacksLocked);
        player.Combat.UnlockExtraAttacks();
    }

    [Fact]
    public void GeneratedSwingLethalTarget_DrainsRemainingBatch()
    {
        (WorldRuntime world, Map map, _, _) = CombatTestKit.CreateWorld();
        Player player = CombatTestKit.AddPlayer(world, 5, 0, 0, new FakeSession(5));
        var victim = new CombatTestUnit(health: 1);
        victim.Spawn(map, 1, 0);
        Assert.True(map.Combat.Attack(player, victim));
        player.Combat.QueueExtraAttacks(3);
        map.Combat.MeleeSwingResolved += _ => victim.Health = 0;

        map.Combat.Update(0);
        Assert.Equal(0u, player.Combat.ExtraAttacks);
    }

    [Fact]
    public void QueueCreatedDuringSwing_WaitsForTheNextUnitUpdate()
    {
        (WorldRuntime world, Map map, _, _) = CombatTestKit.CreateWorld();
        Player player = CombatTestKit.AddPlayer(world, 6, 0, 0, new FakeSession(6));
        var victim = new CombatTestUnit();
        victim.Spawn(map, 1, 0);
        Assert.True(map.Combat.Attack(player, victim));
        int swings = 0;
        map.Combat.MeleeSwingResolved += _ =>
        {
            if (++swings == 1)
                Assert.True(player.Combat.QueueExtraAttacks(2));
        };

        map.Combat.Update(0);
        Assert.Equal(1, swings);
        Assert.Equal(2u, player.Combat.ExtraAttacks);
        Assert.False(map.Combat.UpdateMeleeAttackingState(player));
        Assert.Equal(1, swings);

        map.Combat.Update(0);
        Assert.Equal(3, swings);
        Assert.Equal(0u, player.Combat.ExtraAttacks);
    }
}
