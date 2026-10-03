using ArcaneCore.Protocol;

namespace ArcaneCore.Game.Death.Resurrection;

/// <summary>
/// SMSG_RESURRECT_REQUEST and CMSG_RESURRECT_RESPONSE. The request follows vmangos (Server/Packets/Spell.cpp:641-648) and
/// mangos-classic (Player.cpp:18883-18921): u64 caster GUID, u32 name length including the terminator, the name (empty when the
/// caster is a player: the client knows the name), u8 sickness (the caster is a spirit healer: the resurrection comes with
/// sickness) and u8 delayed (the timer: spells with SPELL_ATTR_EX3_NO_RES_TIMER, such as Rebirth, resurrect at once). gtker's
/// wow_messages lists only one trailing <c>Bool</c> for 1.12 (<c>smsg_resurrect_request.wowm</c>); the two servers agree with each
/// other and have run real clients, so they are followed, and the two bytes are written by this one function.
/// </summary>
public static class ResurrectionPackets
{
    /// <summary>The SMSG_RESURRECT_REQUEST body.</summary>
    public static byte[] BuildRequest(ObjectGuid caster, string casterName, bool sickness, bool delayed)
    {
        ArgumentNullException.ThrowIfNull(casterName);
        var writer = new PacketWriter(32 + casterName.Length);
        writer.WriteUInt64(caster.Value);
        writer.WriteUInt32((uint)(System.Text.Encoding.UTF8.GetByteCount(casterName) + 1));
        writer.WriteCString(casterName);
        writer.WriteByte(sickness ? (byte)1 : (byte)0);
        writer.WriteByte(delayed ? (byte)1 : (byte)0);
        return writer.ToArray();
    }

    /// <summary>
    /// The CMSG_RESURRECT_RESPONSE body (vmangos <c>ResurrectResponse::ReadFromWorldPacket</c>): u64 resurrector GUID, u8 status
    /// (0 declines; anything else accepts, like vmangos' <c>bool</c>). Null for a body that is too short.
    /// </summary>
    public static (ObjectGuid Resurrector, bool Accept)? ReadResponse(byte[] payload)
    {
        ArgumentNullException.ThrowIfNull(payload);
        if (payload.Length < 9)
        {
            return null;
        }

        var reader = new PacketReader(payload);
        var guid = new ObjectGuid(reader.ReadUInt64());
        return (guid, reader.ReadByte() != 0);
    }
}
