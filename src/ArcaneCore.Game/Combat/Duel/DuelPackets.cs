using ArcaneCore.Protocol;

namespace ArcaneCore.Game.Combat;

/// <summary>
/// Duel packet bodies for build 5875 (vmangos src/game/Server/Packets/Duel.cpp:20-65, Spells/SpellEffects.cpp:4732-4736,
/// Handlers/DuelHandler.cpp:30-71; gtker wow_messages world/duel/*.wowm). wow_messages names SMSG_DUEL_REQUESTED's fields
/// initiator/target and SMSG_DUEL_WINNER's reason/opponent_name/initiator_name; vmangos sends the flag object guid then the
/// challenger guid and (fled, winner name, loser name): the same bytes under different names.
/// </summary>
public static class DuelPackets
{
    /// <summary>The countdown the client shows, in milliseconds (vmangos DuelHandler.cpp:45; wow_messages types the field as seconds but both servers send 3000).</summary>
    public const uint CountdownMilliseconds = 3000;

    /// <summary>SMSG_DUEL_REQUESTED: u64 flag (arbiter) guid, u64 challenger guid.</summary>
    public static byte[] Requested(ulong arbiter, ulong challenger)
    {
        var w = new PacketWriter(16);
        w.WriteUInt64(arbiter);
        w.WriteUInt64(challenger);
        return w.ToArray();
    }

    /// <summary>SMSG_DUEL_COMPLETE: u8 started (0 when the duel was interrupted before it began; 1 otherwise).</summary>
    public static byte[] Complete(bool started) => [started ? (byte)1 : (byte)0];

    /// <summary>SMSG_DUEL_WINNER: u8 fled (0 = won, 1 = fled), CString winner, CString loser.</summary>
    public static byte[] Winner(bool fled, string winnerName, string loserName)
    {
        ArgumentNullException.ThrowIfNull(winnerName);
        ArgumentNullException.ThrowIfNull(loserName);
        var w = new PacketWriter(2 + winnerName.Length + loserName.Length + 2);
        w.WriteByte(fled ? (byte)1 : (byte)0);
        w.WriteCString(winnerName);
        w.WriteCString(loserName);
        return w.ToArray();
    }

    /// <summary>SMSG_DUEL_COUNTDOWN: u32 milliseconds.</summary>
    public static byte[] Countdown(uint milliseconds)
    {
        var w = new PacketWriter(4);
        w.WriteUInt32(milliseconds);
        return w.ToArray();
    }

    /// <summary>SMSG_DUEL_OUTOFBOUNDS: empty body.</summary>
    public static byte[] OutOfBounds() => [];

    /// <summary>SMSG_DUEL_INBOUNDS: empty body.</summary>
    public static byte[] InBounds() => [];

    /// <summary>
    /// The u64 guid of CMSG_DUEL_ACCEPTED / CMSG_DUEL_CANCELLED. Both references read and ignore it; a short payload yields null
    /// (the handler then drops the packet instead of throwing).
    /// </summary>
    public static ulong? ParseArbiterGuid(ReadOnlySpan<byte> payload)
    {
        if (payload.Length < 8)
        {
            return null;
        }

        var r = new PacketReader(payload);
        return r.ReadUInt64();
    }
}
