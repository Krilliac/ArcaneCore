using ArcaneCore.Game.Combat;
using ArcaneCore.Game.Entities;
using ArcaneCore.Game.Maps;
using ArcaneCore.Protocol;
using Xunit;

namespace ArcaneCore.Game.Tests.CombatMechanics;

/// <summary>
/// vmangos Player::CanAutoAttackTarget (Player.cpp:1103-1109): every swing of a player re-checks IsValidAttackTarget and a target that
/// stopped being one (a charm, a faction change) gives ATTACK_RESULT_FRIENDLY_TARGET: the attack stops and the client hears
/// SMSG_ATTACKSWING_CANT_ATTACK. vmangos Unit::Attack (Unit.cpp:4486-4488): a mounted player cannot start an attack.
/// </summary>
public sealed class AutoAttackTargetValidationTests
{
    private sealed class SwitchableHooks : CombatHooks
    {
        public bool Friendly { get; set; }

        public override bool IsFriendly(Unit a, Unit b) => Friendly || base.IsFriendly(a, b);
    }

    private static (WorldRuntime World, Map Map, SwitchableHooks Hooks, Player Player, FakeSession Session, CombatTestUnit Mob) Scene()
    {
        (WorldRuntime world, Map map, ScriptedRandom _, TestCombatHooks _) = CombatTestKit.CreateWorld();
        var hooks = new SwitchableHooks();
        map.Combat.Hooks = hooks;
        var session = new FakeSession(1);
        Player player = CombatTestKit.AddPlayer(world, 1, 0, 0, session);
        player.SetFloat(UpdateFields.UnitFieldMindamage, 10);
        player.SetFloat(UpdateFields.UnitFieldMaxdamage, 10);
        var mob = new CombatTestUnit();
        mob.Spawn(map, 2, 0);
        return (world, map, hooks, player, session, mob);
    }

    [Fact]
    public void ATargetThatTurnedFriendly_IsAFriendlyTargetResult()
    {
        (WorldRuntime world, Map map, SwitchableHooks hooks, Player player, FakeSession _, CombatTestUnit mob) = Scene();
        using WorldRuntime _ = world;

        Assert.Equal(AttackCheckResult.Ok, map.Combat.CanAutoAttackTarget(player, mob));
        hooks.Friendly = true;
        Assert.Equal(AttackCheckResult.FriendlyTarget, map.Combat.CanAutoAttackTarget(player, mob));
    }

    [Fact]
    public void ASwingAgainstATargetThatTurnedFriendly_StopsTheAttack_AndSendsCantAttack()
    {
        (WorldRuntime world, Map map, SwitchableHooks hooks, Player player, FakeSession session, CombatTestUnit mob) = Scene();
        using WorldRuntime _ = world;
        Assert.True(map.Combat.Attack(player, mob));
        world.RunTick(50);
        uint afterFirstSwing = mob.Health;
        Assert.True(afterFirstSwing < mob.MaxHealth);

        hooks.Friendly = true;
        session.Clear();
        world.RunTick(2000);

        Assert.Equal(afterFirstSwing, mob.Health);
        Assert.Null(player.Combat.Victim);
        Assert.Contains(CombatTestKit.Drain(session), p => p.Opcode == WorldOpcode.SmsgAttackswingCantAttack);
    }

    [Fact]
    public void AMountedPlayer_CannotStartAnAttack()
    {
        (WorldRuntime world, Map map, SwitchableHooks _, Player player, FakeSession _, CombatTestUnit mob) = Scene();
        using WorldRuntime _ = world;
        player.SetUInt32(UpdateFields.UnitFieldMountdisplayid, 14_334);

        Assert.False(map.Combat.Attack(player, mob));
        Assert.Null(player.Combat.Victim);

        player.SetUInt32(UpdateFields.UnitFieldMountdisplayid, 0);
        Assert.True(map.Combat.Attack(player, mob));
    }
}
