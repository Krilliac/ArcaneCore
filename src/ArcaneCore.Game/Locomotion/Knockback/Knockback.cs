using ArcaneCore.Game.Entities;
using ArcaneCore.Game.Spells;
using ArcaneCore.Protocol;

namespace ArcaneCore.Game.Locomotion;

/// <summary>An acknowledgement of a knock back: u64 GUID, u32 counter, movement block whose jump block repeats the order.</summary>
public readonly record struct KnockbackAck(ulong Guid, uint Counter, MovementInfo Movement);

/// <summary>
/// Wire layouts of the knock back packets (vmangos MovementPacketSender.cpp:241-304; gtker smsg_move_knock_back.wowm,
/// cmsg_move_knock_back_ack.wowm).
/// </summary>
public static class KnockbackPackets
{
    /// <summary>SMSG_MOVE_KNOCK_BACK to the knocked unit's client: packed GUID, u32 counter, f32 vcos, f32 vsin, f32 horizontal speed, f32 vertical speed (already negated).</summary>
    public static byte[] BuildOrder(ulong guid, uint counter, in KnockbackInfo info)
    {
        var writer = new PacketWriter(29);
        writer.WritePackedGuid(guid);
        writer.WriteUInt32(counter);
        writer.WriteSingle(info.VCos);
        writer.WriteSingle(info.VSin);
        writer.WriteSingle(info.SpeedXY);
        writer.WriteSingle(info.SpeedZ);
        return writer.ToArray();
    }

    /// <summary>MSG_MOVE_KNOCK_BACK to the observers after the ack: packed GUID, movement block, f32 vcos, vsin, xy speed, z speed.</summary>
    public static byte[] BuildObserver(ulong guid, in MovementInfo movement, in KnockbackInfo info)
    {
        var writer = new PacketWriter(80);
        writer.WritePackedGuid(guid);
        movement.Write(writer);
        writer.WriteSingle(info.VCos);
        writer.WriteSingle(info.VSin);
        writer.WriteSingle(info.SpeedXY);
        writer.WriteSingle(info.SpeedZ);
        return writer.ToArray();
    }

    public static KnockbackAck ReadAck(ReadOnlySpan<byte> payload)
    {
        var reader = new PacketReader(payload);
        ulong guid = reader.ReadUInt64();
        uint counter = reader.ReadUInt32();
        MovementInfo movement = MovementInfo.Read(ref reader);
        return new KnockbackAck(guid, counter, movement);
    }
}

/// <summary>
/// Knocking a unit back (vmangos Unit::KnockBackFrom and Unit::KnockBack, Unit.cpp:9932-9960). Like every pre-WotLK core, only
/// a player is actually moved (mangos-classic Unit.cpp:10816: "Effect properly implemented only for players"): its client is
/// ordered with SMSG_MOVE_KNOCK_BACK (pending change), the unit is moved when the ack arrives. A stunned or rooted unit is not
/// knocked back; the unit's current non-melee spell is interrupted first.
/// </summary>
public static class KnockbackService
{
    /// <summary>
    /// vmangos KnockBackFrom: the direction is away from <paramref name="source"/> (the caster's orientation plus pi when it is
    /// the unit itself). A negative horizontal speed pulls instead (EffectPlayerPull). Returns true when an order was sent.
    /// </summary>
    public static bool KnockBackFrom(SpellSystem system, Unit unit, Unit source, float horizontalSpeed, float verticalSpeed)
    {
        ArgumentNullException.ThrowIfNull(system);
        ArgumentNullException.ThrowIfNull(unit);
        ArgumentNullException.ThrowIfNull(source);
        if (IsStunnedOrRooted(unit))
        {
            return false;
        }

        float angle = ReferenceEquals(unit, source) ? unit.Orientation + MathF.PI : AngleFromTo(source, unit);
        return KnockBack(system, unit, angle, horizontalSpeed, verticalSpeed);
    }

    /// <summary>vmangos Unit::KnockBack.</summary>
    public static bool KnockBack(SpellSystem system, Unit unit, float angle, float horizontalSpeed, float verticalSpeed)
    {
        if (IsStunnedOrRooted(unit))
        {
            return false;
        }

        system.InterruptNonMeleeSpells(unit);

        // "Effect properly implemented only for players": a player controlled by its client, in a map.
        if (unit is not Player player || player.Map is null)
        {
            return false;
        }

        float vsin = MathF.Sin(angle);
        float vcos = MathF.Cos(angle);
        var info = new KnockbackInfo(vcos, vsin, horizontalSpeed, -verticalSpeed); // "notice the - sign in front of speedZ"
        uint counter = player.NextMovementCounter();
        player.Locomotion.Pending.Push(counter, MovementChangeType.KnockBack, apply: true, knockback: info);
        player.Session.Send(WorldOpcode.SmsgMoveKnockBack, KnockbackPackets.BuildOrder(player.Guid.Value, counter, info));
        return true;
    }

    /// <summary>
    /// Acknowledge a knock back (vmangos HandleMoveKnockBackAck, MovementHandler.cpp:747-802): the ack must match a pending order
    /// (counter and the jump block within 0.01). Returns the pending order's numbers on a match, null (and a counted wrong ack)
    /// otherwise; a matching ack ends the fall in progress (SetFallInformation(0), :796).
    /// </summary>
    public static KnockbackInfo? Acknowledge(Player player, uint counter, in MovementInfo block)
    {
        ArgumentNullException.ThrowIfNull(player);
        LocomotionState state = player.Locomotion;
        KnockbackInfo? sent = state.Pending.Changes.FirstOrDefault(c => c.Counter == counter && c.Type == MovementChangeType.KnockBack)?.Knockback;
        if (sent is null || !state.Pending.TryAcknowledgeKnockBack(counter, in block))
        {
            state.NoteWrongAck();
            return null;
        }

        return sent;
    }

    /// <summary>vmangos HasUnitState(UNIT_STATE_STUNNED | UNIT_STATE_ROOT).</summary>
    private static bool IsStunnedOrRooted(Unit unit)
        => (unit.UnitFlags & UnitFlags.Stunned) != 0 || unit is Player { IsRooted: true } || unit.Movement.HasFlag(MovementFlags.Root);

    /// <summary>vmangos WorldObject::GetAngle(target) as seen from <paramref name="from"/>: atan2 of the offset, in [0, 2 pi).</summary>
    private static float AngleFromTo(Unit from, Unit to)
    {
        float angle = MathF.Atan2(to.Y - from.Y, to.X - from.X);
        if (angle < 0)
        {
            angle += 2 * MathF.PI;
        }

        return angle;
    }
}
