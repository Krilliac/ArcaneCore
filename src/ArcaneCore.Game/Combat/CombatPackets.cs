using ArcaneCore.Game.Entities;
using ArcaneCore.Protocol;

namespace ArcaneCore.Game.Combat;

/// <summary>One damage component of an SMSG_ATTACKERSTATEUPDATE (vmangos DamageStructs.h SubDamageInfo).</summary>
public readonly record struct SubDamage(uint School, uint Damage, uint Absorb, int Resist);

/// <summary>
/// Combat packet bodies for build 5875. Layouts follow vmangos
/// src/game/Server/Packets/Combat.cpp (servers win; differences from gtker/wow_messages are
/// listed in docs/areas/combat.md).
/// </summary>
public static class CombatPackets
{
    /// <summary>
    /// SMSG_ATTACKSTART: u64 attacker, u64 victim — full GUIDs (vmangos AttackStart::AppendBodyTo,
    /// cmangos-classic Unit::SendMeleeAttackStart, gtker smsg_attackstart.wowm).
    /// </summary>
    public static byte[] AttackStart(ObjectGuid attacker, ObjectGuid victim)
    {
        var w = new PacketWriter(16);
        w.WriteUInt64(attacker.Value);
        w.WriteUInt64(victim.Value);
        return w.ToArray();
    }

    /// <summary>
    /// SMSG_ATTACKSTOP: packed attacker, packed victim (0 when none), u32 "is dead" — "is 32bit
    /// on client" (vmangos AttackStop::AppendBodyTo). What the flag means differs between
    /// senders; see the callers.
    /// </summary>
    public static byte[] AttackStop(ObjectGuid attacker, ObjectGuid victim, bool isDead)
    {
        var w = new PacketWriter(22);
        w.WritePackedGuid(attacker.Value);
        w.WritePackedGuid(victim.Value);
        w.WriteUInt32(isDead ? 1u : 0u);
        return w.ToArray();
    }

    /// <summary>
    /// SMSG_ATTACKERSTATEUPDATE (vmangos MeleeAttackingStateUpdate::AppendBodyTo for builds
    /// &gt; 1.5.1): u32 hit info, packed attacker, packed victim, i32 total damage, u8 count, then
    /// per sub-damage i32 first school index, f32 damage, i32 damage, i32 absorb, i32 resist;
    /// then u32 victim state, u32 attacker state (0), u32 melee spell id (0), i32 blocked.
    /// </summary>
    public static byte[] AttackerStateUpdate(
        HitInfo hitInfo, ObjectGuid attacker, ObjectGuid victim, uint totalDamage,
        ReadOnlySpan<SubDamage> subDamage, VictimState victimState, uint blocked)
    {
        var w = new PacketWriter(64);
        w.WriteUInt32((uint)hitInfo);
        w.WritePackedGuid(attacker.Value);
        w.WritePackedGuid(victim.Value);
        w.WriteUInt32(totalDamage);
        w.WriteByte((byte)subDamage.Length);
        foreach (SubDamage sub in subDamage)
        {
            w.WriteUInt32(sub.School);
            w.WriteSingle(sub.Damage);
            w.WriteUInt32(sub.Damage);
            w.WriteUInt32(sub.Absorb);
            w.WriteInt32(sub.Resist);
        }

        w.WriteUInt32((uint)victimState);
        w.WriteUInt32(0); // attacker state
        w.WriteUInt32(0); // melee spell id (white swing)
        w.WriteUInt32(blocked);
        return w.ToArray();
    }

    /// <summary>SMSG_PARTYKILLLOG: u64 killer, u64 victim (vmangos PartyKillLog; gtker smsg_partykilllog.wowm).</summary>
    public static byte[] PartyKillLog(ObjectGuid killer, ObjectGuid victim) => AttackStart(killer, victim);

    /// <summary>SMSG_CORPSE_RECLAIM_DELAY: u32 delay in milliseconds (vmangos Player::SendCorpseReclaimDelay).</summary>
    public static byte[] CorpseReclaimDelay(uint delayMs)
    {
        var w = new PacketWriter(4);
        w.WriteUInt32(delayMs);
        return w.ToArray();
    }

    /// <summary>MSG_CORPSE_QUERY reply without a corpse: u8 0 (vmangos HandleCorpseQueryOpcode).</summary>
    public static byte[] CorpseQueryNotFound() => [0];

    /// <summary>
    /// MSG_CORPSE_QUERY reply: u8 1, i32 map (the ghost-entrance map when the corpse is in a
    /// dungeon and the player is elsewhere), f32 x, y, z, u32 corpse map
    /// (vmangos HandleCorpseQueryOpcode; gtker msg_corpse_query_server.wowm).
    /// </summary>
    public static byte[] CorpseQueryFound(int mapId, float x, float y, float z, uint corpseMapId)
    {
        var w = new PacketWriter(21);
        w.WriteByte(1);
        w.WriteInt32(mapId);
        w.WriteSingle(x);
        w.WriteSingle(y);
        w.WriteSingle(z);
        w.WriteUInt32(corpseMapId);
        return w.ToArray();
    }

    /// <summary>
    /// SMSG_MOVE_WATER_WALK / SMSG_MOVE_LAND_WALK: packed GUID + u32 movement counter
    /// (vmangos MovementPacketSender::SendMovementFlagChangeToController, builds &gt; 1.9.4).
    /// </summary>
    public static byte[] MovementFlagChange(ObjectGuid guid, uint counter)
    {
        var w = new PacketWriter(13);
        w.WritePackedGuid(guid.Value);
        w.WriteUInt32(counter);
        return w.ToArray();
    }

    /// <summary>
    /// Send to the unit's own client (if a player) and to every player whose client has it —
    /// vmangos WorldObject::SendObjectMessageToSet / SendMessageToSet(self = true).
    /// </summary>
    public static void SendToSet(Unit source, WorldOpcode opcode, ReadOnlySpan<byte> payload)
    {
        if (source is Player self)
        {
            self.Session.Send(opcode, payload);
        }

        source.Map?.BroadcastToObservers(source, opcode, payload);
    }
}
