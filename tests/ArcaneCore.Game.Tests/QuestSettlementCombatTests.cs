using ArcaneCore.Game.Combat;
using ArcaneCore.Game.Entities;
using ArcaneCore.Game.Maps;
using ArcaneCore.Game.Maps.Templates;
using ArcaneCore.Game.Spells;
using ArcaneCore.Game.Teleport;
using ArcaneCore.Game.Tests.Spells;
using ArcaneCore.Kernel.WorldData;
using ArcaneCore.Protocol;
using Xunit;
using static ArcaneCore.Game.Tests.Spells.SpellTestKit;

namespace ArcaneCore.Game.Tests;

public sealed class QuestSettlementCombatTests
{
    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public void PendingAttackerOrVictim_BlocksAllDamageAndDeathEntryPoints(bool freezeAttacker)
    {
        (WorldRuntime world, Map map, _, TestCombatHooks hooks) = CombatTestKit.CreateWorld();
        using (world)
        {
            var attackerSession = new FakeSession(1);
            Player attacker = CombatTestKit.AddPlayer(world, 1, 0, 0, attackerSession);
            Player victim = CombatTestKit.AddPlayer(world, 2, 2, 0, new FakeSession(2), Race.Orc);
            Player frozen = freezeAttacker ? attacker : victim;
            Guid operation = Guid.NewGuid();
            Assert.True(frozen.BeginQuestSettlement(operation));

            Assert.False(map.Combat.Attack(attacker, victim));
            Assert.Equal(AttackCheckResult.CantAttack, map.Combat.CanAutoAttackTarget(attacker, victim));
            Assert.Null(map.Combat.AttackerStateUpdate(attacker, victim, WeaponAttackType.BaseAttack));
            Assert.Equal(0u, map.Combat.DealDamage(attacker, victim, victim.Health));
            map.Combat.Kill(attacker, victim);
            map.Combat.Kill(null, frozen);
            Assert.Equal(1000u, attacker.Health);
            Assert.Equal(1000u, victim.Health);
            Assert.Equal(DeathState.Alive, victim.Combat.DeathState);
            Assert.Empty(hooks.Kills);
            Assert.Null(attacker.Combat.Victim);
            Assert.DoesNotContain(attackerSession.Sent, p => p.Opcode == WorldOpcode.SmsgAttackerstateupdate);

            frozen.EndQuestSettlement(operation);
            Assert.True(map.Combat.Attack(attacker, victim));
            Assert.Equal(10u, map.Combat.DealDamage(attacker, victim, 10));
        }
    }

    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public void ExistingAttack_IsPausedWhenEitherActorIsPending_AndLogoutStillDetachesIt(bool freezeAttacker)
    {
        (WorldRuntime world, Map map, _, _) = CombatTestKit.CreateWorld();
        using (world)
        {
            var attackerSession = new FakeSession(1);
            Player attacker = CombatTestKit.AddPlayer(world, 1, 0, 0, attackerSession);
            Player victim = CombatTestKit.AddPlayer(world, 2, 2, 0, new FakeSession(2), Race.Orc);
            Assert.True(map.Combat.Attack(attacker, victim));
            Player frozen = freezeAttacker ? attacker : victim;
            Assert.True(frozen.BeginQuestSettlement(Guid.NewGuid()));
            attackerSession.Clear();

            world.RunTick(5000);

            Assert.Equal(1000u, victim.Health);
            Assert.False(map.Combat.UpdateMeleeAttackingState(attacker));
            Assert.DoesNotContain(attackerSession.Sent, p => p.Opcode == WorldOpcode.SmsgAttackerstateupdate);
            world.RemovePlayer(frozen);
            Assert.Null(attacker.Combat.Victim);
            Assert.Empty(victim.Combat.Attackers);
        }
    }

    [Fact]
    public void PendingCaster_PausesExistingCastAndRejectsTriggeredCasts()
    {
        using var kit = new SpellTestKit();
        (Player caster, _) = kit.AddPlayer(1);
        (Player target, _) = kit.AddPlayer(2, 2);
        kit.Spellbook.Teach(caster, CastBolt);
        SpellSystem.SetPower(caster, PowerType.Rage, 100);
        Assert.Equal(SpellCastResult.CastOk, kit.System.HandleCastRequest(caster, CastBolt, SpellCastTargets.ForUnit(target.Guid)));
        SpellCast cast = kit.System.GetState(caster.Guid)!.CurrentCast!;
        Guid operation = Guid.NewGuid();
        Assert.True(caster.BeginQuestSettlement(operation));

        kit.Advance(5000);

        Assert.Equal(2000, cast.Timer);
        Assert.Equal(60u, target.Health);
        Assert.Equal(100u, SpellSystem.GetPower(caster, PowerType.Rage));
        Assert.Equal(SpellCastResult.NotReady, kit.System.CastSpell(caster, InstantHeal, SpellCastTargets.ForSelf(), triggered: true));
        caster.EndQuestSettlement(operation);
        kit.Advance(2000);
        Assert.Equal(45u, target.Health);
        Assert.Equal(50u, SpellSystem.GetPower(caster, PowerType.Rage));
    }

    [Fact]
    public void TargetBecomingPendingBeforeCastLands_RejectsEffectsWithoutSpendingPower()
    {
        using var kit = new SpellTestKit();
        (Player caster, _) = kit.AddPlayer(1);
        (Player target, _) = kit.AddPlayer(2, 2);
        kit.Spellbook.Teach(caster, CastBolt);
        SpellSystem.SetPower(caster, PowerType.Rage, 100);
        Assert.Equal(SpellCastResult.CastOk, kit.System.HandleCastRequest(caster, CastBolt, SpellCastTargets.ForUnit(target.Guid)));
        Assert.True(target.BeginQuestSettlement(Guid.NewGuid()));

        kit.Advance(2000);

        Assert.Equal(60u, target.Health);
        Assert.Equal(100u, SpellSystem.GetPower(caster, PowerType.Rage));
        Assert.Null(kit.System.GetState(caster.Guid)?.CurrentCast);
        Assert.Equal(SpellCastResult.BadTargets, kit.System.CastSpell(caster, CastBolt, SpellCastTargets.ForUnit(target.Guid), triggered: true));
    }

    [Theory]
    [InlineData(AuraType.PeriodicDamage, true)]
    [InlineData(AuraType.PeriodicDamage, false)]
    [InlineData(AuraType.PeriodicHeal, true)]
    [InlineData(AuraType.PeriodicHeal, false)]
    [InlineData(AuraType.PeriodicEnergize, true)]
    [InlineData(AuraType.PeriodicEnergize, false)]
    [InlineData(AuraType.PeriodicTriggerSpell, true)]
    [InlineData(AuraType.PeriodicTriggerSpell, false)]
    public void PeriodicEffects_PauseForPendingCasterOrTarget_WithoutCatchingUp(AuraType type, bool freezeCaster)
    {
        const uint auraId = 900100;
        const uint triggeredId = 900101;
        SpellInfo auraSpell = Spell(auraId, Effect(SpellEffectName.ApplyAura, 5, SpellImplicitTarget.Unit, type,
            amplitude: 1000, misc: (int)PowerType.Rage, trigger: triggeredId)) with
        {
            Duration = new SpellDuration(10000, 0, 10000),
            RangeIndex = 4,
            Range = new SpellRange(0, 30),
        };
        SpellInfo triggered = Spell(triggeredId, Effect(SpellEffectName.SchoolDamage, 5, SpellImplicitTarget.Unit));
        using var kit = new SpellTestKit(auraSpell, triggered);
        (Player caster, _) = kit.AddPlayer(1);
        (Player target, FakeSession targetSession) = kit.AddPlayer(2, 2);
        target.Health = 30;
        Assert.Equal(SpellCastResult.CastOk, kit.System.CastSpell(caster, auraId, SpellCastTargets.ForUnit(target.Guid), triggered: true));
        SpellAuraHolder holder = Assert.Single(kit.System.GetAuras(target));
        SpellAura aura = Assert.Single(holder.Auras.OfType<SpellAura>());
        Player frozen = freezeCaster ? caster : target;
        Guid operation = Guid.NewGuid();
        Assert.True(frozen.BeginQuestSettlement(operation));
        targetSession.Clear();

        kit.Advance(5000);

        Assert.Equal(30u, target.Health);
        Assert.Equal(0u, SpellSystem.GetPower(target, PowerType.Rage));
        Assert.Equal(10000, holder.Duration);
        Assert.Equal(0, aura.TickCount);
        Assert.DoesNotContain(targetSession.Sent, p => p.Opcode == WorldOpcode.SmsgPeriodicauralog);
        frozen.EndQuestSettlement(operation);
        kit.Advance(1000);
        Assert.Equal(1, aura.TickCount);
        Assert.Equal(9000, holder.Duration);
        Assert.Equal(type switch
        {
            AuraType.PeriodicDamage or AuraType.PeriodicTriggerSpell => 25u,
            AuraType.PeriodicHeal => 35u,
            _ => 30u,
        }, target.Health);
        Assert.Equal(type == AuraType.PeriodicEnergize ? 5u : 0u, SpellSystem.GetPower(target, PowerType.Rage));
    }

    [Fact]
    public void PendingPlayer_RejectsNearAndFarTeleports()
    {
        using WorldRuntime world = TestWorld.CreateRuntime();
        LoadTeleportMaps(world);
        Player player = TestWorld.CreatePlayer(1, 0, 0, new FakeSession(1));
        world.AddPlayer(player);
        var teleports = new TeleportService(world, _ => { }, _ => { });
        Assert.True(player.BeginQuestSettlement(Guid.NewGuid()));

        Assert.False(teleports.TeleportTo(player, 0, 10, 20, 83.5f, 0));
        Assert.False(teleports.TeleportTo(player, 1, 10, 20, 83.5f, 0));
        Assert.Equal(0, teleports.PendingCount);
        Assert.Equal((0f, 0f, 0u), (player.X, player.Y, player.MapId));
    }

    [Fact]
    public void DeferredFarTeleport_DoesNotDepartAfterPlayerBecomesPending()
    {
        using WorldRuntime world = TestWorld.CreateRuntime();
        LoadTeleportMaps(world);
        var session = new FakeSession(1);
        Player player = TestWorld.CreatePlayer(1, 0, 0, session);
        world.AddPlayer(player);
        var teleports = new TeleportService(world, _ => { }, _ => { });
        Assert.True(teleports.TeleportTo(player, 1, 10, 20, 83.5f, 0));
        Assert.True(player.BeginQuestSettlement(Guid.NewGuid()));
        session.Clear();

        world.RunTick(50);

        Assert.Same(world.GetMap(0), player.Map);
        Assert.Equal((0f, 0f, 0u), (player.X, player.Y, player.MapId));
        Assert.DoesNotContain(session.Sent, p => p.Opcode is WorldOpcode.SmsgTransferPending or WorldOpcode.SmsgNewWorld);
        teleports.Forget(player);
        Assert.Equal(0, teleports.PendingCount);
    }

    private static void LoadTeleportMaps(WorldRuntime world)
        => WorldMaps.Of(world).Load(new MapContent(
        [
            new MapTemplate(0, 0, MapType.Common, 0, 0, 0, -1, 0, 0, "Eastern Kingdoms", ""),
            new MapTemplate(1, 0, MapType.Common, 0, 0, 0, -1, 0, 0, "Kalimdor", ""),
        ], [], [], [], []));
}
