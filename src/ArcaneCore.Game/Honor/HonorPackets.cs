using ArcaneCore.Game.Entities;
using ArcaneCore.Protocol;

namespace ArcaneCore.Game.Honor;

/// <summary>
/// Build-5875 honor wire layouts: gtker/wow_messages smsg_pvp_credit.wowm and msg_inspect_honor_stats_server.wowm
/// (1.12), cross-checked against vmangos Server/Packets/Misc.cpp:350-401 and Handlers/MiscHandler.cpp:1000-1036.
/// </summary>
public static class HonorPackets
{
    /// <summary>SMSG_PVP_CREDIT: i32 honor (negative for a dishonorable kill), u64 victim guid (0 for none), u32 victim rank.</summary>
    public static byte[] PvpCredit(int honor, ulong victimGuid, uint victimRank)
    {
        var writer = new PacketWriter(16);
        writer.WriteInt32(honor);
        writer.WriteUInt64(victimGuid);
        writer.WriteUInt32(victimRank);
        return writer.ToArray();
    }

    /// <summary>
    /// MSG_INSPECT_HONOR_STATS (server): 50 bytes read from the target's honor update fields. The three
    /// "unknown" u16 after the kill counts are always 0 (vmangos unknownOld1-3).
    /// </summary>
    public static byte[] InspectHonorStats(Player target)
    {
        ArgumentNullException.ThrowIfNull(target);
        var writer = new PacketWriter(50);
        writer.WriteUInt64(target.Guid.Value);
        writer.WriteByte(target.GetByte(UpdateFields.PlayerFieldBytes, 3));
        writer.WriteUInt32(target.GetUInt32(UpdateFields.PlayerFieldSessionKills));
        writer.WriteUInt16(target.GetUInt16(UpdateFields.PlayerFieldYesterdayKills, 0));
        writer.WriteUInt16(0);
        writer.WriteUInt16(target.GetUInt16(UpdateFields.PlayerFieldLastWeekKills, 0));
        writer.WriteUInt16(0);
        writer.WriteUInt16(target.GetUInt16(UpdateFields.PlayerFieldThisWeekKills, 0));
        writer.WriteUInt16(0);
        writer.WriteUInt32(target.GetUInt32(UpdateFields.PlayerFieldLifetimeHonorbaleKills));
        writer.WriteUInt32(target.GetUInt32(UpdateFields.PlayerFieldLifetimeDishonorbaleKills));
        writer.WriteUInt32(target.GetUInt32(UpdateFields.PlayerFieldYesterdayContribution));
        writer.WriteUInt32(target.GetUInt32(UpdateFields.PlayerFieldLastWeekContribution));
        writer.WriteUInt32(target.GetUInt32(UpdateFields.PlayerFieldThisWeekContribution));
        writer.WriteUInt32(target.GetUInt32(UpdateFields.PlayerFieldLastWeekRank));
        writer.WriteByte(target.GetByte(UpdateFields.PlayerFieldBytes2, 0));
        return writer.ToArray();
    }
}
