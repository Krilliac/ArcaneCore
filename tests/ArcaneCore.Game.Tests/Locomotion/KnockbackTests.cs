using ArcaneCore.Game.Entities;
using ArcaneCore.Game.Locomotion;
using ArcaneCore.Game.Spells;
using ArcaneCore.Game.Tests.Spells;
using ArcaneCore.Protocol;
using Xunit;
using static ArcaneCore.Game.Tests.Spells.SpellTestKit;

namespace ArcaneCore.Game.Tests.Locomotion;

/// <summary>SPELL_EFFECT_KNOCK_BACK / PLAYER_PULL (vmangos EffectKnockBack, EffectPlayerPull, SpellEffects.cpp:5474-5505; Unit::KnockBackFrom, Unit.cpp:9932-9960).</summary>
public sealed class KnockbackTests
{
    private const uint WarStomp = 970001;    // knock back, misc 75 (7.5 yd/s), value 57
    private const uint ZeroPush = 970002;    // knock back with no horizontal speed (nine classic spells are like this)
    private const uint Pull = 970003;        // player pull, misc 40, value 10
    private const uint DreamFog = 24778;     // the sleep aura vmangos removes first

    private static SpellTestKit Kit()
    {
        SpellInfo Perm(uint id, params SpellEffectInfo[] effects) => Spell(id, effects) with
        {
            Duration = new SpellDuration(-1, 0, -1),
            SpellVisual = 1,
        };

        return new SpellTestKit(
            Spell(WarStomp, Effect(SpellEffectName.KnockBack, 57, SpellImplicitTarget.UnitEnemy, misc: 75)) with { RangeIndex = 4, Range = new SpellRange(0, 30) },
            Spell(ZeroPush, Effect(SpellEffectName.KnockBack, 57, SpellImplicitTarget.UnitEnemy, misc: 0)) with { RangeIndex = 4, Range = new SpellRange(0, 30) },
            Spell(Pull, Effect(SpellEffectName.PlayerPull, 10, SpellImplicitTarget.UnitEnemy, misc: 40)) with { RangeIndex = 4, Range = new SpellRange(0, 30) },
            Perm(DreamFog, Effect(SpellEffectName.ApplyAura, 0, aura: AuraType.Dummy)));
    }

    private static (Player Caster, Player Target, FakeSession TargetSession, FakeSession WatcherSession, Player Watcher) Setup(SpellTestKit kit, float targetX = 5, float targetY = 0)
    {
        (Player caster, _) = kit.AddPlayer(1, 0, 0);
        (Player target, FakeSession targetSession) = kit.AddPlayer(2, targetX, targetY);
        (Player watcher, FakeSession watcherSession) = kit.AddPlayer(3, targetX + 1, targetY);
        kit.World.RunTick(50);
        targetSession.Clear();
        watcherSession.Clear();
        return (caster, target, targetSession, watcherSession, watcher);
    }

    private static byte[][] Of(FakeSession session, WorldOpcode opcode) => [.. session.Sent.Where(p => p.Opcode == opcode).Select(p => p.Payload)];

    [Fact]
    public void ModuleIsDiscovered_AndBothEffectsHaveHandlers()
    {
        using var kit = Kit();
        Assert.Contains(typeof(KnockbackEffects), kit.System.Modules);
        Assert.True(kit.System.HasEffectHandler(SpellEffectName.KnockBack));
        Assert.True(kit.System.HasEffectHandler(SpellEffectName.PlayerPull));
    }

    [Fact]
    public void AKnockBack_OrdersTheTargetsClient_WithTheNegatedVerticalSpeed_AndTellsNobodyElseYet()
    {
        using var kit = Kit();
        (Player caster, Player target, FakeSession targetSession, FakeSession watcherSession, _) = Setup(kit);

        kit.System.CastSpell(caster, WarStomp, SpellCastTargets.ForUnit(target.Guid), triggered: true);

        byte[] order = Assert.Single(Of(targetSession, WorldOpcode.SmsgMoveKnockBack));
        // The caster stands at the origin and the target 5 yards east: the direction is angle 0, so cos 1 and sin 0.
        // misc 75 / 10 = 7.5 horizontal; value 57 / 10 = 5 vertical (integer division, as vmangos), sent as -5.
        Assert.Equal(KnockbackPackets.BuildOrder(target.Guid.Value, 0, new KnockbackInfo(1f, 0f, 7.5f, -5f)), order);
        Assert.True(target.Locomotion.Pending.HasPendingOfType(MovementChangeType.KnockBack));
        Assert.Empty(Of(watcherSession, WorldOpcode.MsgMoveKnockBack)); // observers hear of it after the ack
        Assert.Equal(5f, target.X);                                      // the unit is not moved by the server
    }

    [Fact]
    public void TheDirection_IsFromTheCasterToTheTarget()
    {
        using var kit = Kit();
        (Player caster, Player target, FakeSession targetSession, _, _) = Setup(kit, targetX: 0, targetY: 4);

        kit.System.CastSpell(caster, WarStomp, SpellCastTargets.ForUnit(target.Guid), triggered: true);

        byte[] order = Assert.Single(Of(targetSession, WorldOpcode.SmsgMoveKnockBack));
        var reader = new PacketReader(order);
        reader.ReadPackedGuid();
        reader.ReadUInt32();
        Assert.Equal(0f, reader.ReadSingle(), 4); // cos of a quarter turn
        Assert.Equal(1f, reader.ReadSingle(), 4); // sin of a quarter turn
    }

    [Fact]
    public void AZeroHorizontalKnockBack_StillSendsAPacket()
    {
        using var kit = Kit();
        (Player caster, Player target, FakeSession targetSession, _, _) = Setup(kit);

        kit.System.CastSpell(caster, ZeroPush, SpellCastTargets.ForUnit(target.Guid), triggered: true);

        Assert.Equal(KnockbackPackets.BuildOrder(target.Guid.Value, 0, new KnockbackInfo(1f, 0f, 0f, -5f)), Assert.Single(Of(targetSession, WorldOpcode.SmsgMoveKnockBack)));
    }

    [Fact]
    public void ACasterThatKnocksItselfBack_UsesItsOrientationPlusPi()
    {
        using var kit = Kit();
        (Player caster, _, _, _, _) = Setup(kit);
        FakeSession session = (FakeSession)caster.Session;
        caster.Relocate(0, 0, 83.5f, 0f, 1);
        session.Clear();

        KnockbackService.KnockBackFrom(kit.System, caster, caster, 10f, 3f);

        byte[] order = Assert.Single(Of(session, WorldOpcode.SmsgMoveKnockBack));
        var reader = new PacketReader(order);
        reader.ReadPackedGuid();
        reader.ReadUInt32();
        Assert.Equal(-1f, reader.ReadSingle(), 4); // facing east, flung west
    }

    [Fact]
    public void AStunnedOrRootedTarget_IsNotKnockedBack()
    {
        using var kit = Kit();
        (Player caster, Player target, FakeSession targetSession, _, _) = Setup(kit);
        target.UnitFlags |= UnitFlags.Stunned;
        kit.System.CastSpell(caster, WarStomp, SpellCastTargets.ForUnit(target.Guid), triggered: true);
        Assert.DoesNotContain(targetSession.Sent, p => p.Opcode == WorldOpcode.SmsgMoveKnockBack);

        target.UnitFlags &= ~UnitFlags.Stunned;
        target.SetRooted(true);
        targetSession.Clear();
        kit.System.CastSpell(caster, WarStomp, SpellCastTargets.ForUnit(target.Guid), triggered: true);
        Assert.DoesNotContain(targetSession.Sent, p => p.Opcode == WorldOpcode.SmsgMoveKnockBack);
    }

    [Fact]
    public void ATaxiPassenger_IsSkipped()
    {
        using var kit = Kit();
        (Player caster, Player target, FakeSession targetSession, _, _) = Setup(kit);
        target.UnitFlags |= UnitFlags.TaxiFlight;

        kit.System.CastSpell(caster, WarStomp, SpellCastTargets.ForUnit(target.Guid), triggered: true);

        Assert.DoesNotContain(targetSession.Sent, p => p.Opcode == WorldOpcode.SmsgMoveKnockBack);
    }

    [Fact]
    public void TheDreamFogSleep_IsRemovedBeforeTheKnockBack()
    {
        using var kit = Kit();
        (Player caster, Player target, FakeSession targetSession, _, _) = Setup(kit);
        kit.System.CastSpell(target, DreamFog, SpellCastTargets.ForSelf(), triggered: true);
        Assert.True(kit.System.HasAura(target, DreamFog));

        kit.System.CastSpell(caster, WarStomp, SpellCastTargets.ForUnit(target.Guid), triggered: true);

        Assert.False(kit.System.HasAura(target, DreamFog));
        Assert.Single(Of(targetSession, WorldOpcode.SmsgMoveKnockBack));
    }

    [Fact]
    public void PlayerPull_PullsTowardsTheCaster_ByTheDistanceCappedAtTheValue()
    {
        using var kit = Kit();
        (Player caster, Player target, FakeSession targetSession, _, _) = Setup(kit, targetX: 20, targetY: 0);

        kit.System.CastSpell(caster, Pull, SpellCastTargets.ForUnit(target.Guid), triggered: true);

        // 20 yards away, capped at the value 10: horizontal speed -10, vertical speed misc / 10 = 4 (sent as -4).
        Assert.Equal(KnockbackPackets.BuildOrder(target.Guid.Value, 0, new KnockbackInfo(1f, 0f, -10f, -4f)), Assert.Single(Of(targetSession, WorldOpcode.SmsgMoveKnockBack)));
    }

    [Fact]
    public void PlayerPull_UsesTheDistanceWhenItIsShorter()
    {
        using var kit = Kit();
        (Player caster, Player target, FakeSession targetSession, _, _) = Setup(kit, targetX: 6, targetY: 0);

        kit.System.CastSpell(caster, Pull, SpellCastTargets.ForUnit(target.Guid), triggered: true);

        Assert.Equal(KnockbackPackets.BuildOrder(target.Guid.Value, 0, new KnockbackInfo(1f, 0f, -6f, -4f)), Assert.Single(Of(targetSession, WorldOpcode.SmsgMoveKnockBack)));
    }

    [Fact]
    public void AnUnackedKnockBack_IsDroppedAfterTheTimeout_NotEnforced()
    {
        using var kit = Kit();
        (Player caster, Player target, _, FakeSession watcherSession, _) = Setup(kit);
        kit.System.CastSpell(caster, WarStomp, SpellCastTargets.ForUnit(target.Guid), triggered: true);
        float x = target.X;

        kit.World.RunTick(4001);

        Assert.False(target.Locomotion.Pending.HasPending);
        Assert.Equal(1, target.Locomotion.FailedAckCount); // OnFailedToAckChange
        Assert.Equal(x, target.X);
        Assert.Empty(Of(watcherSession, WorldOpcode.MsgMoveKnockBack));
        Assert.DoesNotContain(watcherSession.Sent, p => p.Opcode is >= WorldOpcode.SmsgSplineMoveUnroot and <= WorldOpcode.SmsgSplineMoveLandWalk);
    }

    [Fact]
    public void AKnockBack_InterruptsTheTargetsCurrentCast()
    {
        using var kit = Kit();
        (Player caster, Player target, _, _, _) = Setup(kit);
        kit.Spellbook.Teach(target, CastBolt);
        SpellSystem.SetPower(target, PowerType.Rage, 100);
        Assert.Equal(SpellCastResult.CastOk, kit.System.HandleCastRequest(target, CastBolt, SpellCastTargets.ForUnit(caster.Guid)));
        Assert.NotNull(kit.System.GetState(target.Guid)!.CurrentCast);

        kit.System.CastSpell(caster, WarStomp, SpellCastTargets.ForUnit(target.Guid), triggered: true);

        Assert.Null(kit.System.GetState(target.Guid)!.CurrentCast);
    }

    // --- the acknowledgement ledger --------------------------------------------------------

    [Fact]
    public void TheAck_MustRepeatTheOrderWithinOneHundredth()
    {
        using var kit = Kit();
        (Player caster, Player target, _, _, _) = Setup(kit);
        kit.System.CastSpell(caster, WarStomp, SpellCastTargets.ForUnit(target.Guid), triggered: true);

        MovementInfo Block(float cos, float sin, float xy, float z) => new()
        {
            Flags = MovementFlags.Jumping, JumpCosAngle = cos, JumpSinAngle = sin, JumpXySpeed = xy, JumpZSpeed = z,
        };

        Assert.Null(KnockbackService.Acknowledge(target, 5, Block(1, 0, 7.5f, -5)));      // counter
        Assert.Null(KnockbackService.Acknowledge(target, 0, Block(1, 0, 7.52f, -5)));     // 0.02 off
        Assert.Null(KnockbackService.Acknowledge(target, 0, Block(1, 0.02f, 7.5f, -5)));
        Assert.Null(KnockbackService.Acknowledge(target, 0, Block(1, 0, 7.5f, 5)));       // sign of the vertical speed
        Assert.Equal(4, target.Locomotion.WrongAckCount);
        Assert.True(target.Locomotion.Pending.HasPending);

        KnockbackInfo? matched = KnockbackService.Acknowledge(target, 0, Block(1, 0.005f, 7.505f, -5.005f));
        Assert.Equal(new KnockbackInfo(1f, 0f, 7.5f, -5f), matched);
        Assert.False(target.Locomotion.Pending.HasPending);
        Assert.Null(KnockbackService.Acknowledge(target, 0, Block(1, 0, 7.5f, -5)));      // a replay
    }

    [Fact]
    public void ObserverAndOrderPackets_HaveTheGtkerLayouts()
    {
        // smsg_move_knock_back.wowm: PackedGuid, u32 counter, f32 vcos, f32 vsin, f32 horizontal, f32 vertical.
        byte[] order = KnockbackPackets.BuildOrder(6, 2, new KnockbackInfo(1f, 0f, 7.5f, -5f));
        Assert.Equal(2 + 4 + (4 * 4), order.Length);
        Assert.Equal(new byte[] { 0x01, 0x06, 0x02, 0, 0, 0 }, order[..6]);

        var info = new MovementInfo { Flags = MovementFlags.Jumping, Time = 3, X = 1, Y = 2, Z = 3 };
        byte[] relay = KnockbackPackets.BuildObserver(6, info, new KnockbackInfo(1f, 0f, 7.5f, -5f));
        var reader = new PacketReader(relay);
        Assert.Equal(6ul, reader.ReadPackedGuid());
        MovementInfo read = MovementInfo.Read(ref reader);
        Assert.Equal((1f, 0f, 7.5f, -5f), (reader.ReadSingle(), reader.ReadSingle(), reader.ReadSingle(), reader.ReadSingle()));
        Assert.Equal(0, reader.Remaining);
        Assert.Equal(3u, read.Time);
    }
}
