using ArcaneCore.Game;
using ArcaneCore.Protocol;

namespace ArcaneCore.World.Packets;

/// <summary>One line of a /who result.</summary>
public sealed record WhoEntry(string Name, string Guild, uint Level, uint Class, uint Race, uint Zone);

/// <summary>Logout, account data, /who and the small status replies the client polls after login.</summary>
public static class MiscPackets
{
    /// <summary>vmangos GMTicketMgr.h GMTICKET_STATUS_DEFAULT: the player has no open ticket.</summary>
    public const uint GmTicketStatusDefault = 0x0A;

    /// <summary>SMSG_LOGOUT_RESPONSE: u32 reason, u8 instant (vmangos Misc::LogoutResponse).</summary>
    public static byte[] BuildLogoutResponse(LogoutResult result, bool instant)
    {
        var writer = new PacketWriter(5);
        writer.WriteUInt32((uint)result);
        writer.WriteByte(instant ? (byte)1 : (byte)0);
        return writer.ToArray();
    }

    /// <summary>
    /// SMSG_UPDATE_ACCOUNT_DATA for 1.12: u32 type, u32 decompressed size, zlib data; an empty
    /// blob is just the type and a zero size (vmangos HandleRequestAccountData; cmangos-classic
    /// writes the same type/size/data layout).
    /// </summary>
    public static byte[] BuildUpdateAccountData(uint type, ReadOnlySpan<byte> data)
    {
        byte[] compressed = data.IsEmpty ? [] : AccountDataCompression.Compress(data);
        var writer = new PacketWriter(8 + compressed.Length);
        writer.WriteUInt32(type);
        writer.WriteUInt32((uint)data.Length);
        writer.WriteBytes(compressed);
        return writer.ToArray();
    }

    /// <summary>SMSG_GMTICKET_GETTICKET with no ticket: u32 status only (vmangos TicketMgr::SendTicket(nullptr)).</summary>
    public static byte[] BuildNoGmTicket()
    {
        var writer = new PacketWriter(4);
        writer.WriteUInt32(GmTicketStatusDefault);
        return writer.ToArray();
    }

    /// <summary>
    /// MSG_QUERY_NEXT_MAIL_TIME reply: f32 — 0 with unread mail, otherwise −86400 (one day;
    /// vmangos HandleQueryNextMailTime). There is no mail yet, so always −86400.
    /// </summary>
    public static byte[] BuildNoNextMail()
    {
        var writer = new PacketWriter(4);
        writer.WriteSingle(-86400.0f);
        return writer.ToArray();
    }

    /// <summary>SMSG_RAID_INSTANCE_INFO with no saved raids: u32 count (vmangos Player::SendRaidInfo).</summary>
    public static byte[] BuildNoRaidInstances() => [0, 0, 0, 0];

    /// <summary>
    /// SMSG_WHO: u32 listed, u32 online, then per player name, guild, u32 level, class, race,
    /// zone (vmangos WhoListClientQueryTask for builds &gt; 1.8.4; gtker smsg_who).
    /// </summary>
    public static byte[] BuildWho(IReadOnlyList<WhoEntry> entries, uint onlineCount)
    {
        var writer = new PacketWriter(8 + (entries.Count * 40));
        writer.WriteUInt32((uint)entries.Count);
        writer.WriteUInt32(onlineCount);
        foreach (WhoEntry entry in entries)
        {
            writer.WriteCString(entry.Name);
            writer.WriteCString(entry.Guild);
            writer.WriteUInt32(entry.Level);
            writer.WriteUInt32(entry.Class);
            writer.WriteUInt32(entry.Race);
            writer.WriteUInt32(entry.Zone);
        }

        return writer.ToArray();
    }

    /// <summary>MSG_MOVE_TIME_SKIPPED relay: packed mover GUID, u32 skipped milliseconds (vmangos HandleMoveTimeSkippedOpcode).</summary>
    public static byte[] BuildMoveTimeSkipped(ObjectGuid mover, uint lagMs)
    {
        var writer = new PacketWriter(13);
        writer.WritePackedGuid(mover.Value);
        writer.WriteUInt32(lagMs);
        return writer.ToArray();
    }
}
