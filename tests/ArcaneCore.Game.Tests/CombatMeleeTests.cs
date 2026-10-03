using System.Buffers.Binary;
using ArcaneCore.Game.Combat;
using ArcaneCore.Game.Entities;
using ArcaneCore.Game.Maps;
using ArcaneCore.Protocol;
using Xunit;

namespace ArcaneCore.Game.Tests;

/// <summary>Auto attack: start/stop packets, the swing loop, swing errors, damage and its side effects.</summary>
public sealed class CombatMeleeTests
{
    private static (WorldRuntime World, Map Map, ScriptedRandom Random, TestCombatHooks Hooks, Player A, FakeSession SA, Player V, FakeSession SV) TwoPlayers(float vx = 2f)
    {
        (WorldRuntime world, Map map, ScriptedRandom random, TestCombatHooks hooks) = CombatTestKit.CreateWorld();
        var sa = new FakeSession(1);
        var sv = new FakeSession(2);
        Player a = CombatTestKit.AddPlayer(world, 1, 0, 0, sa);
        Player v = CombatTestKit.AddPlayer(world, 2, vx, 0, sv, Race.Orc);
        a.SetFloat(UpdateFields.UnitFieldMindamage, 50);
        a.SetFloat(UpdateFields.UnitFieldMaxdamage, 50);
        world.RunTick(1);
        sa.Clear();
        sv.Clear();
        return (world, map, random, hooks, a, sa, v, sv);
    }

    private static List<byte[]> Payloads(FakeSession session, WorldOpcode opcode)
        => CombatTestKit.Drain(session).Where(p => p.Opcode == opcode).Select(p => p.Payload).ToList();

    [Fact]
    public void Attack_SetsTheTarget_AndSendsAttackStartToSelfAndObservers()
    {
        var t = TwoPlayers();
        using WorldRuntime world = t.World;

        Assert.True(t.Map.Combat.Attack(t.A, t.V));

        byte[] expected = [.. BitConverter.GetBytes(t.A.Guid.Value), .. BitConverter.GetBytes(t.V.Guid.Value)];
        Assert.Equal(expected, Assert.Single(Payloads(t.SA, WorldOpcode.SmsgAttackstart)));
        Assert.Equal(expected, Assert.Single(Payloads(t.SV, WorldOpcode.SmsgAttackstart)));
        Assert.Equal(t.V.Guid, t.A.Target);
        Assert.Same(t.V, t.A.Combat.Victim);
        Assert.Contains(t.A, t.V.Combat.Attackers);

        // attacking the same victim again changes nothing
        Assert.False(t.Map.Combat.Attack(t.A, t.V));
    }

    [Fact]
    public void Swing_DealsDamage_SendsAttackerStateUpdate_AndRestartsTheTimer()
    {
        var t = TwoPlayers();
        using WorldRuntime world = t.World;
        t.Map.Combat.Attack(t.A, t.V);
        t.SA.Clear();
        t.SV.Clear();

        world.RunTick(50);

        // The victim faces away (both face east), so it cannot dodge/parry/block: a plain hit.
        var w = new PacketWriter(64);
        w.WriteUInt32((uint)(HitInfo.AffectsVictim | HitInfo.Pvp));
        w.WritePackedGuid(t.A.Guid.Value);
        w.WritePackedGuid(t.V.Guid.Value);
        w.WriteUInt32(50);
        w.WriteByte(1);
        w.WriteUInt32(0);
        w.WriteSingle(50);
        w.WriteUInt32(50);
        w.WriteUInt32(0);
        w.WriteInt32(0);
        w.WriteUInt32((uint)VictimState.Normal);
        w.WriteUInt32(0);
        w.WriteUInt32(0);
        w.WriteUInt32(0);
        Assert.Equal(w.ToArray(), Assert.Single(Payloads(t.SA, WorldOpcode.SmsgAttackerstateupdate)));
        Assert.Equal(950u, t.V.Health);
        Assert.True(t.A.Combat.IsInCombat);
        Assert.True(t.V.Combat.IsInCombat);
        Assert.Equal(2000u, t.A.Combat.GetAttackTimer(WeaponAttackType.BaseAttack));

        // Rage (vmangos Player::RewardRage at level 60, conversion ≈ 230.6): 16 dealt, 5 taken.
        Assert.Equal(16u, t.A.GetUInt32(UpdateFields.UnitFieldPower1 + 1));
        Assert.Equal(5u, t.V.GetUInt32(UpdateFields.UnitFieldPower1 + 1));

        world.RunTick(1999);
        Assert.Empty(Payloads(t.SA, WorldOpcode.SmsgAttackerstateupdate));
        world.RunTick(1);
        Assert.Single(Payloads(t.SA, WorldOpcode.SmsgAttackerstateupdate));
        Assert.Equal(900u, t.V.Health);
    }

    [Fact]
    public void OutOfRange_SendsNotInRangeOnce_ThenSwingsWhenClose()
    {
        var t = TwoPlayers(vx: 20f);
        using WorldRuntime world = t.World;
        t.Map.Combat.Attack(t.A, t.V);
        t.SA.Clear();

        world.RunTick(50);
        world.RunTick(100);
        world.RunTick(100);

        List<(WorldOpcode Opcode, byte[] Payload)> sent = [.. CombatTestKit.Drain(t.SA)];
        Assert.Single(sent, p => p.Opcode == WorldOpcode.SmsgAttackswingNotinrange);
        Assert.DoesNotContain(sent, p => p.Opcode == WorldOpcode.SmsgAttackerstateupdate);
        Assert.Equal(AttackCheckResult.NotInRange, t.A.Combat.LastSwingError);

        t.V.Relocate(4.9f, 0, 83.5f, 0, 0); // reach: 1.5 + 1.5 + 4/3 < 5 → 5 yd
        world.RunTick(100);
        Assert.Single(Payloads(t.SA, WorldOpcode.SmsgAttackerstateupdate));
        Assert.Equal(AttackCheckResult.Ok, t.A.Combat.LastSwingError);
    }

    [Fact]
    public void MeleeReach_IsFiveYards_PlusLeewayWhenBothRun()
    {
        var t = TwoPlayers(vx: 6f);
        using WorldRuntime world = t.World;
        Assert.False(MapCombat.CanReachWithMeleeAutoAttack(t.A, t.V));

        var running = new MovementInfo { Flags = MovementFlags.Forward, X = 0, Y = 0, Z = 83.5f };
        t.A.ApplyMovement(running, 0);
        t.V.ApplyMovement(running with { X = 6f }, 0);
        Assert.True(MapCombat.CanReachWithMeleeAutoAttack(t.A, t.V)); // 5 + 2.66

        t.V.ApplyMovement(running with { X = 6f, Flags = MovementFlags.Forward | MovementFlags.WalkMode }, 0);
        Assert.False(MapCombat.CanReachWithMeleeAutoAttack(t.A, t.V));
    }

    [Fact]
    public void FacingAway_SendsBadFacing()
    {
        var t = TwoPlayers(vx: 3f);
        using WorldRuntime world = t.World;
        t.A.Relocate(0, 0, 83.5f, MathF.PI, 0); // facing west, victim to the east
        t.Map.Combat.Attack(t.A, t.V);
        t.SA.Clear();

        world.RunTick(50);

        Assert.Single(Payloads(t.SA, WorldOpcode.SmsgAttackswingBadfacing));
        Assert.Equal(1000u, t.V.Health);
    }

    [Fact]
    public void AttackStop_ClearsTheTarget_AndSendsPackedGuids()
    {
        var t = TwoPlayers();
        using WorldRuntime world = t.World;
        t.Map.Combat.Attack(t.A, t.V);
        t.SA.Clear();
        t.SV.Clear();

        Assert.True(t.Map.Combat.AttackStop(t.A));

        byte[] expected = [.. CombatTestKit.Packed(t.A.Guid.Value), .. CombatTestKit.Packed(t.V.Guid.Value), 0, 0, 0, 0];
        Assert.Equal(expected, Assert.Single(Payloads(t.SA, WorldOpcode.SmsgAttackstop)));
        Assert.Equal(expected, Assert.Single(Payloads(t.SV, WorldOpcode.SmsgAttackstop)));
        Assert.Equal(default, t.A.Target);
        Assert.Empty(t.V.Combat.Attackers);
        Assert.False(t.Map.Combat.AttackStop(t.A));
    }

    [Fact]
    public void PvpCombat_LingersFivePointFiveSeconds_RoundedToTheCheck()
    {
        var t = TwoPlayers();
        using WorldRuntime world = t.World;
        t.Map.Combat.Attack(t.A, t.V);
        world.RunTick(50);
        t.Map.Combat.AttackStop(t.A);
        Assert.True(t.V.Combat.IsInCombat);

        for (int i = 0; i < 55; i++)
        {
            world.RunTick(100);
        }

        Assert.True(t.V.Combat.IsInCombat); // 5.5 s batched up to 6 s
        for (int i = 0; i < 20; i++)
        {
            world.RunTick(100);
        }

        Assert.False(t.V.Combat.IsInCombat);
        Assert.False(t.A.Combat.IsInCombat);
        Assert.Equal(0u, (uint)(t.V.UnitFlags & UnitFlags.InCombat));
    }

    [Fact]
    public void SittingVictim_StandsUpWhenHit()
    {
        var t = TwoPlayers();
        using WorldRuntime world = t.World;
        t.V.SetStandState(StandState.Sit);
        t.SV.Clear();

        t.Map.Combat.DealDamage(t.A, t.V, 10);

        Assert.Equal(StandState.Stand, t.V.StandState);
        Assert.Contains(CombatTestKit.Drain(t.SV), p => p.Opcode == WorldOpcode.SmsgStandstateUpdate);
    }

    [Fact]
    public void Creature_AutoAttacksAPlayer_AndThePlayerGetsRage()
    {
        (WorldRuntime world, Map map, _, _) = CombatTestKit.CreateWorld();
        using WorldRuntime w = world;
        var session = new FakeSession(1);
        Player player = CombatTestKit.AddPlayer(world, 1, 0, 0, session);
        var mob = new CombatTestUnit();
        mob.Spawn(map, 2, 0, orientation: MathF.PI);

        Assert.True(map.Combat.Attack(mob, player));
        world.RunTick(50);

        Assert.Equal(990u, player.Health);
        Assert.Equal(1u, player.GetUInt32(UpdateFields.UnitFieldPower1 + 1)); // 10 / 230.6 · 2.5 · 10
        Assert.True(player.Combat.IsInCombat);
        Assert.True(mob.Combat.IsInCombat);
    }

    [Fact]
    public void PlayerHittingAMob_AddsThreat_AndTheMobReacts()
    {
        (WorldRuntime world, Map map, ScriptedRandom random, _) = CombatTestKit.CreateWorld();
        using WorldRuntime w = world;
        Player player = CombatTestKit.AddPlayer(world, 1, 0, 0, new FakeSession(1));
        player.SetFloat(UpdateFields.UnitFieldMindamage, 50);
        player.SetFloat(UpdateFields.UnitFieldMaxdamage, 50);
        var mob = new CombatTestUnit();
        mob.Spawn(map, 2, 0, orientation: MathF.PI);

        map.Combat.Attack(player, mob);
        world.RunTick(50);

        Assert.Equal(950u, mob.Health);
        Assert.Equal(50f, mob.Combat.Threat.GetThreat(player));
        Assert.Contains(mob, player.Combat.ThreatenedBy);
        Assert.Equal([player], mob.AttackedBy);

        // While the mob holds the player on its threat list, the player stays in combat.
        map.Combat.AttackStop(player);
        for (int i = 0; i < 100; i++)
        {
            world.RunTick(100);
        }

        Assert.True(player.Combat.IsInCombat);
    }

    [Fact]
    public void Parry_HastesTheVictimsNextSwing_AndGivesTheAttackerRage()
    {
        (WorldRuntime world, Map map, ScriptedRandom random, _) = CombatTestKit.CreateWorld();
        using WorldRuntime w = world;
        Player player = CombatTestKit.AddPlayer(world, 1, 0, 0, new FakeSession(1));
        player.SetFloat(UpdateFields.UnitFieldMindamage, 50);
        player.SetFloat(UpdateFields.UnitFieldMaxdamage, 50);
        var mob = new CombatTestUnit();
        mob.Spawn(map, 2, 0, orientation: MathF.PI); // facing the player
        mob.Combat.SetAttackTimer(WeaponAttackType.BaseAttack, 1001);

        map.Combat.Attack(player, mob);
        random.Ints.Enqueue(1200); // equal-level parry range is 1000..1499
        world.RunTick(1);

        Assert.Equal(1000u, mob.Health);
        Assert.Equal(399u, mob.Combat.GetAttackTimer(WeaponAttackType.BaseAttack)); // 1001 in (400, 1200] → 400, then its own 1 ms tick
        Assert.Equal(12u, player.GetUInt32(UpdateFields.UnitFieldPower1 + 1));     // 50 · 0.75 → 37 → 12 rage·10
    }

    [Fact]
    public void CalculateMeleeDamage_AppliesGlancingBlockAndCrit()
    {
        (WorldRuntime world, Map map, ScriptedRandom random, _) = CombatTestKit.CreateWorld();
        using WorldRuntime w = world;
        Player player = CombatTestKit.AddPlayer(world, 1, 0, 0, new FakeSession(1));
        player.SetFloat(UpdateFields.UnitFieldMindamage, 50);
        player.SetFloat(UpdateFields.UnitFieldMaxdamage, 50);
        var mob = new CombatTestUnit();
        mob.Spawn(map, 2, 0, orientation: MathF.PI);

        random.Ints.Enqueue(1600); // glancing: frand(0.91, 0.99) → 0.91 with the default fraction
        MeleeDamageInfo glance = map.Combat.CalculateMeleeDamage(player, mob, WeaponAttackType.BaseAttack);
        Assert.Equal(MeleeHitOutcome.Glancing, glance.Outcome);
        Assert.Equal(45u, glance.TotalDamage);
        Assert.True(glance.HitInfo.HasFlag(HitInfo.Glancing | HitInfo.AffectsVictim));

        random.Ints.Enqueue(2600); // block: creature block value level/2 + str/20 = 30
        MeleeDamageInfo block = map.Combat.CalculateMeleeDamage(player, mob, WeaponAttackType.BaseAttack);
        Assert.Equal(MeleeHitOutcome.Block, block.Outcome);
        Assert.Equal(30u, block.Blocked);
        Assert.Equal(20u, block.TotalDamage);
        Assert.Equal(VictimState.Normal, block.TargetState);
        Assert.True(block.HitInfo.HasFlag(HitInfo.Block | HitInfo.RolledBlock));

        player.SetFloat(UpdateFields.PlayerCritPercentage, 10f);
        random.Ints.Enqueue(3500); // crit 3000..3999
        MeleeDamageInfo crit = map.Combat.CalculateMeleeDamage(player, mob, WeaponAttackType.BaseAttack);
        Assert.Equal(MeleeHitOutcome.Crit, crit.Outcome);
        Assert.Equal(100u, crit.TotalDamage);

        mob.SetUInt32(UpdateFields.UnitFieldResistances, 3000); // armor
        MeleeDamageInfo armored = map.Combat.CalculateMeleeDamage(player, mob, WeaponAttackType.BaseAttack);
        Assert.Equal(32u, armored.TotalDamage); // 50 · (1 − 0.353)
        Assert.Equal(18u, armored.CleanDamage);
    }

    [Fact]
    public void DualWield_SwingsTheOffHandTwoHundredMillisecondsApart()
    {
        var t = TwoPlayers();
        using WorldRuntime world = t.World;
        t.Hooks.DualWield = true;
        t.A.SetUInt32(UpdateFields.UnitFieldBaseattacktime + 1, 2000);
        t.Map.Combat.Attack(t.A, t.V);
        t.A.Combat.SetAttackTimer(WeaponAttackType.OffAttack, 0);
        t.SA.Clear();

        world.RunTick(1);

        List<byte[]> swings = Payloads(t.SA, WorldOpcode.SmsgAttackerstateupdate);
        Assert.Single(swings); // main hand only; the off hand was pushed to 200 ms
        Assert.Equal(200u, t.A.Combat.GetAttackTimer(WeaponAttackType.OffAttack));

        world.RunTick(200);
        byte[] off = Assert.Single(Payloads(t.SA, WorldOpcode.SmsgAttackerstateupdate));
        Assert.True(((HitInfo)BinaryPrimitives.ReadUInt32LittleEndian(off)).HasFlag(HitInfo.LeftSwing));
    }

    [Fact]
    public void ThreatList_KeepsTheVictimUntil110PercentInMeleeOr130PercentAtRange()
    {
        var owner = new CombatTestUnit();
        var tank = new CombatTestUnit();
        var melee = new CombatTestUnit();
        var ranged = new CombatTestUnit();
        ThreatList threat = owner.Combat.Threat;
        threat.AddThreat(tank, 100);
        Func<Unit, bool> valid = _ => true;
        Func<Unit, bool> inMelee = u => !ReferenceEquals(u, ranged);
        Assert.Same(tank, threat.SelectVictim(valid, inMelee));

        threat.AddThreat(melee, 110);
        threat.AddThreat(ranged, 129);
        Assert.Same(tank, threat.SelectVictim(valid, inMelee));

        threat.AddThreat(melee, 1); // 111 > 110
        Assert.Same(melee, threat.SelectVictim(valid, inMelee));

        threat.AddThreat(ranged, 16); // 145 > 111 · 1.3 = 144.3
        Assert.Same(ranged, threat.SelectVictim(valid, inMelee));

        threat.Remove(ranged);
        Assert.DoesNotContain(owner, ranged.Combat.ThreatenedBy);
        threat.Clear();
        Assert.Empty(tank.Combat.ThreatenedBy);
    }
}
