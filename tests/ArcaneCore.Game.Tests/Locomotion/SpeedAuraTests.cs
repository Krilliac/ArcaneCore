using ArcaneCore.Game.Entities;
using ArcaneCore.Game.Locomotion;
using ArcaneCore.Game.Spells;
using ArcaneCore.Game.Tests.Spells;
using ArcaneCore.Protocol;
using Xunit;
using static ArcaneCore.Game.Tests.Spells.SpellTestKit;

namespace ArcaneCore.Game.Tests.Locomotion;

/// <summary>The speed auras and the order/ack handshake (vmangos HandleAuraModIncreaseSpeed etc., SpellAuras.cpp:3972-4040).</summary>
public sealed class SpeedAuraTests
{
    private const uint GhostWolf = 950001;     // aura 31, +40 (classic Ghost Wolf 2645)
    private const uint Frostbolt = 950002;     // aura 33, -50 (a typical snare)
    private const uint Sprint = 950003;        // aura 31, +60
    private const uint Mount = 950004;         // aura 32, +100
    private const uint Dolphin = 950005;       // aura 58, +50
    private const uint Normalize = 950006;     // aura 191, 7
    private const uint Boots = 950007;         // aura 171, +15

    private static SpellTestKit Kit()
    {
        SpellInfo Perm(uint id, params SpellEffectInfo[] effects) => Spell(id, effects) with
        {
            Duration = new SpellDuration(-1, 0, -1),
            SpellVisual = 1,
        };

        return new SpellTestKit(
            Perm(GhostWolf, Effect(SpellEffectName.ApplyAura, 40, aura: AuraType.ModIncreaseSpeed)),
            Perm(Frostbolt, Effect(SpellEffectName.ApplyAura, -50, aura: AuraType.ModDecreaseSpeed)),
            Perm(Sprint, Effect(SpellEffectName.ApplyAura, 60, aura: AuraType.ModIncreaseSpeed)),
            Perm(Mount, Effect(SpellEffectName.ApplyAura, 100, aura: AuraType.ModIncreaseMountedSpeed)),
            Perm(Dolphin, Effect(SpellEffectName.ApplyAura, 50, aura: AuraType.ModIncreaseSwimSpeed)),
            Perm(Normalize, Effect(SpellEffectName.ApplyAura, 7, aura: AuraType.UseNormalMovementSpeed)),
            Perm(Boots, Effect(SpellEffectName.ApplyAura, 15, aura: AuraType.ModSpeedNotStack)));
    }

    private static byte[][] Orders(FakeSession session, WorldOpcode opcode)
        => [.. session.Sent.Where(p => p.Opcode == opcode).Select(p => p.Payload)];

    [Fact]
    public void TheModuleIsDiscovered_AndTheNineAuraTypesHaveHandlers()
    {
        using var kit = Kit();
        Assert.Contains(typeof(SpeedAuras), kit.System.Modules);
        foreach (AuraType type in new[]
        {
            AuraType.ModIncreaseSpeed, AuraType.ModIncreaseMountedSpeed, AuraType.ModDecreaseSpeed, AuraType.ModIncreaseSwimSpeed,
            AuraType.ModSpeedAlways, AuraType.ModMountedSpeedAlways, AuraType.ModSpeedNotStack, AuraType.ModMountedSpeedNotStack,
            AuraType.UseNormalMovementSpeed,
        })
        {
            Assert.True(kit.System.HasAuraHandler(type), type.ToString());
        }
    }

    [Fact]
    public void GhostWolf_OrdersOneRunSpeedChange_AndLeavesTheSpeedUntilTheAck()
    {
        using var kit = Kit();
        (Player player, FakeSession session) = kit.AddPlayer(1);

        kit.System.CastSpell(player, GhostWolf, SpellCastTargets.ForSelf(), triggered: true);

        byte[] order = Assert.Single(Orders(session, WorldOpcode.SmsgForceRunSpeedChange));
        float expected = 7.0f * ((100.0f + 40) / 100.0f);
        Assert.Equal(SpeedPackets.BuildForceChange(player.Guid.Value, 0, expected), order);
        Assert.Equal(7.0f, player.RunSpeed);                                   // the ack decides
        Assert.DoesNotContain(session.Sent, p => p.Opcode == WorldOpcode.MsgMoveSetRunSpeed);
        Assert.DoesNotContain(session.Sent, p => p.Opcode is WorldOpcode.SmsgForceSwimSpeedChange or WorldOpcode.SmsgForceRunBackSpeedChange or WorldOpcode.SmsgForceWalkSpeedChange);

        // the ack
        Assert.True(MovementControl.AcknowledgeSpeed(player, MoveType.Run, 0, expected));
        UnitSpeed.SetReal(player, MoveType.Run, expected);
        Assert.Equal(9.8f, player.RunSpeed, 4);
    }

    [Fact]
    public void ARemovedAura_OrdersTheSpeedBack()
    {
        using var kit = Kit();
        (Player player, FakeSession session) = kit.AddPlayer(1);
        kit.System.CastSpell(player, GhostWolf, SpellCastTargets.ForSelf(), triggered: true);
        MovementControl.AcknowledgeSpeed(player, MoveType.Run, 0, 7.0f * 1.4f);
        UnitSpeed.SetReal(player, MoveType.Run, 7.0f * 1.4f);
        session.Clear();

        kit.System.RemoveAuras(player, GhostWolf);

        byte[] order = Assert.Single(Orders(session, WorldOpcode.SmsgForceRunSpeedChange));
        Assert.Equal(SpeedPackets.BuildForceChange(player.Guid.Value, 1, 7.0f), order); // counter 1, back to the base speed
    }

    [Fact]
    public void ASnare_UpdatesRunRunBackAndSwim_ThreeOrders()
    {
        using var kit = Kit();
        (Player player, FakeSession session) = kit.AddPlayer(1);

        kit.System.CastSpell(player, Frostbolt, SpellCastTargets.ForSelf(), triggered: true);

        Assert.Equal(SpeedPackets.BuildForceChange(player.Guid.Value, 0, 3.5f), Assert.Single(Orders(session, WorldOpcode.SmsgForceRunSpeedChange)));
        Assert.Equal(SpeedPackets.BuildForceChange(player.Guid.Value, 1, 2.25f), Assert.Single(Orders(session, WorldOpcode.SmsgForceRunBackSpeedChange)));
        Assert.Equal(SpeedPackets.BuildForceChange(player.Guid.Value, 2, 4.722222f * 0.5f), Assert.Single(Orders(session, WorldOpcode.SmsgForceSwimSpeedChange)));
        Assert.Empty(Orders(session, WorldOpcode.SmsgForceWalkSpeedChange));
    }

    [Fact]
    public void SwimIncrease_OnlyUpdatesSwim()
    {
        using var kit = Kit();
        (Player player, FakeSession session) = kit.AddPlayer(1);

        kit.System.CastSpell(player, Dolphin, SpellCastTargets.ForSelf(), triggered: true);

        Assert.Single(Orders(session, WorldOpcode.SmsgForceSwimSpeedChange));
        Assert.DoesNotContain(session.Sent, p => p.Opcode == WorldOpcode.SmsgForceRunSpeedChange);
    }

    [Fact]
    public void AnAuraThatChangesNothing_SendsNothing()
    {
        using var kit = Kit();
        (Player player, FakeSession session) = kit.AddPlayer(1);
        player.SetUInt32(UpdateFields.UnitFieldMountdisplayid, 1); // mounted: a +40 on-foot aura does not apply

        kit.System.CastSpell(player, GhostWolf, SpellCastTargets.ForSelf(), triggered: true);

        Assert.DoesNotContain(session.Sent, p => p.Opcode == WorldOpcode.SmsgForceRunSpeedChange);
        Assert.Equal(7.0f, player.RunSpeed);
    }

    [Fact]
    public void AMount_RunsAtTheMountedSpeed_AndTheOnFootAuraWaits()
    {
        using var kit = Kit();
        (Player player, FakeSession session) = kit.AddPlayer(1);
        player.SetUInt32(UpdateFields.UnitFieldMountdisplayid, 14337);

        kit.System.CastSpell(player, Mount, SpellCastTargets.ForSelf(), triggered: true);

        Assert.Equal(SpeedPackets.BuildForceChange(player.Guid.Value, 0, 14.0f), Assert.Single(Orders(session, WorldOpcode.SmsgForceRunSpeedChange)));
    }

    [Fact]
    public void RestoredAurasBeforeTheClientIsInTheWorld_SetTheSpeedDirectly_WithoutAPacket()
    {
        using var kit = Kit();
        var session = new FakeSession(2);
        Player player = TestWorld.CreatePlayer(2, 0, 0, session); // not in a map: a login restore

        kit.System.CastSpell(player, Sprint, SpellCastTargets.ForSelf(), triggered: true);
        kit.System.CastSpell(player, Frostbolt, SpellCastTargets.ForSelf(), triggered: true);

        Assert.Equal(7.0f * ((100.0f + 60) / 100.0f) * 0.5f, player.RunSpeed, 4);
        Assert.DoesNotContain(session.Sent, p => p.Opcode is WorldOpcode.SmsgForceRunSpeedChange or WorldOpcode.SmsgForceRunBackSpeedChange or WorldOpcode.SmsgForceSwimSpeedChange);
        Assert.False(player.Locomotion.Pending.HasPending);
    }

    [Fact]
    public void AnUnackedSpeedChange_IsEnforcedAfterTheTimeout_AndEveryoneIsToldWithASplinePacket()
    {
        using var kit = Kit();
        (Player player, FakeSession session) = kit.AddPlayer(1);
        (_, FakeSession watcherSession) = kit.AddPlayer(2, 1, 0);
        kit.World.RunTick(50);
        kit.System.CastSpell(player, GhostWolf, SpellCastTargets.ForSelf(), triggered: true);
        session.Clear();
        watcherSession.Clear();

        kit.World.RunTick(4001);

        Assert.Equal(9.8f, player.RunSpeed, 4);
        Assert.Single(Orders(session, WorldOpcode.SmsgSplineSetRunSpeed));
        Assert.Single(Orders(watcherSession, WorldOpcode.SmsgSplineSetRunSpeed));
        Assert.Equal(1, player.Locomotion.FailedAckCount);
    }

    [Fact]
    public void ASupersededSpeedChange_IsDroppedByTheTimeout_NotEnforced()
    {
        using var kit = Kit();
        (Player player, _) = kit.AddPlayer(1);
        kit.System.CastSpell(player, GhostWolf, SpellCastTargets.ForSelf(), triggered: true);  // order 0: 9.8
        kit.System.RemoveAuras(player, GhostWolf);                                              // order 1: back to 7
        Assert.Equal(2, player.Locomotion.Pending.Count);

        kit.World.RunTick(4001);

        // order 0 is older than order 1 of the same type: it is dropped, and order 1 enforces the base speed.
        Assert.Equal(7.0f, player.RunSpeed);
        Assert.Equal(0, player.Locomotion.FailedAckCount);
        kit.World.RunTick(1);
        Assert.Equal(1, player.Locomotion.FailedAckCount);
        Assert.False(player.Locomotion.Pending.HasPending);
    }

    [Fact]
    public void StackedAuras_FollowTheFormula_AcrossApplyAndRemove()
    {
        using var kit = Kit();
        (Player player, _) = kit.AddPlayer(1);
        void Settle()
        {
            foreach (PendingMovementChange change in player.Locomotion.Pending.Changes.ToArray())
            {
                if (UnitSpeed.IsSpeedChange(change.Type))
                {
                    player.Locomotion.Pending.TryAcknowledgeSpeed(change.Counter, change.Type, change.NewValue);
                    UnitSpeed.SetReal(player, UnitSpeed.MoveTypeOf(change.Type), change.NewValue);
                }
            }
        }

        kit.System.CastSpell(player, Sprint, SpellCastTargets.ForSelf(), triggered: true);
        Settle();
        Assert.Equal(11.2f, player.RunSpeed, 4);

        kit.System.CastSpell(player, Boots, SpellCastTargets.ForSelf(), triggered: true);   // not-stack 15 < main 60: bonus 1.15 * 1.6
        Settle();
        Assert.Equal(7.0f * (1.15f * 160.0f / 100.0f), player.RunSpeed, 3);

        kit.System.CastSpell(player, Frostbolt, SpellCastTargets.ForSelf(), triggered: true);
        Settle();
        Assert.Equal(7.0f * (1.15f * 160.0f / 100.0f) * 0.5f, player.RunSpeed, 3);

        kit.System.RemoveAuras(player, Sprint);
        kit.System.RemoveAuras(player, Boots);
        kit.System.RemoveAuras(player, Frostbolt);
        Settle();
        Assert.Equal(7.0f, player.RunSpeed, 4);
        Assert.Equal(4.5f, player.RunBackSpeed, 4);
    }
}
