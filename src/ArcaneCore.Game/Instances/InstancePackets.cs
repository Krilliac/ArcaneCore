using ArcaneCore.Protocol;

namespace ArcaneCore.Game.Instances;

/// <summary>Why CMSG_RESET_INSTANCES failed for a map (vmangos/cmangos <c>InstanceResetFailReason</c>; gtker <c>InstanceResetFailedReason</c>).</summary>
public enum InstanceResetFailedReason : uint
{
    /// <summary>At least one player is inside the instance.</summary>
    General = 0,

    /// <summary>At least one group member is offline.</summary>
    Offline = 1,

    /// <summary>At least one player is zoning into the instance.</summary>
    Zoning = 2,
}

/// <summary>SMSG_RAID_INSTANCE_MESSAGE types for 1.12 (vmangos <c>RaidInstanceMessage</c>; gtker 1.12/2.4.3 enum).</summary>
public enum RaidInstanceMessageType : uint
{
    /// <summary>"WARNING! %s is scheduled to reset in %d hour(s)."</summary>
    WarningHours = 1,

    /// <summary>"WARNING! %s is scheduled to reset in %d minute(s)!"</summary>
    WarningMinutes = 2,

    /// <summary>"... Please exit the zone or you will be returned to your bind location!"</summary>
    WarningMinutesSoon = 3,

    /// <summary>"Welcome to %s. This raid instance is scheduled to reset in %s."</summary>
    Welcome = 4,
}

/// <summary>SMSG_RAID_GROUP_ONLY errors (vmangos <c>RaidGroupError</c>; gtker <c>RaidGroupError</c>).</summary>
public enum RaidGroupError : uint
{
    /// <summary>ERR_RAID_GROUP_REQUIRED.</summary>
    Required = 1,

    /// <summary>ERR_RAID_GROUP_FULL.</summary>
    Full = 2,
}

/// <summary>
/// Instance packets, 1.12.1 layouts. References: vmangos Player.cpp (<c>SendRaidInfo</c>,
/// <c>SendInstanceResetWarning</c>, <c>SendResetInstanceSuccess/Failed</c>,
/// <c>SendRaidGroupOnlyError</c>), Map.cpp (<c>SMSG_INSTANCE_SAVE_CREATED</c>) and the MIT
/// gtker/wow_messages definitions; every field is a little-endian u32.
/// </summary>
public static class InstancePackets
{
    /// <summary>One SMSG_RAID_INSTANCE_INFO entry: map, seconds until reset, instance id (no index field in 1.12).</summary>
    public readonly record struct RaidInfo(uint MapId, uint ResetSeconds, uint InstanceId);

    /// <summary>SMSG_RAID_INSTANCE_INFO: u32 count, then per entry u32 map, u32 reset time left (s), u32 instance id.</summary>
    public static byte[] BuildRaidInstanceInfo(IReadOnlyList<RaidInfo> entries)
    {
        var packet = new PacketWriter(4 + (entries.Count * 12));
        packet.WriteUInt32((uint)entries.Count);
        foreach (RaidInfo entry in entries)
        {
            packet.WriteUInt32(entry.MapId);
            packet.WriteUInt32(entry.ResetSeconds);
            packet.WriteUInt32(entry.InstanceId);
        }

        return packet.ToArray();
    }

    /// <summary>SMSG_INSTANCE_RESET: u32 map.</summary>
    public static byte[] BuildInstanceReset(uint mapId) => U32(mapId);

    /// <summary>SMSG_INSTANCE_RESET_FAILED: u32 reason, u32 map.</summary>
    public static byte[] BuildInstanceResetFailed(InstanceResetFailedReason reason, uint mapId)
    {
        var packet = new PacketWriter(8);
        packet.WriteUInt32((uint)reason);
        packet.WriteUInt32(mapId);
        return packet.ToArray();
    }

    /// <summary>SMSG_RAID_INSTANCE_MESSAGE: u32 type, u32 map, u32 time left (s).</summary>
    public static byte[] BuildRaidInstanceMessage(RaidInstanceMessageType type, uint mapId, uint timeLeftSeconds)
    {
        var packet = new PacketWriter(12);
        packet.WriteUInt32((uint)type);
        packet.WriteUInt32(mapId);
        packet.WriteUInt32(timeLeftSeconds);
        return packet.ToArray();
    }

    /// <summary>
    /// The SMSG_RAID_INSTANCE_MESSAGE type for a time left (vmangos <c>Player::SendInstanceResetWarning</c>):
    /// more than an hour welcome, more than 15 minutes hours, more than 5 minutes minutes, else soon.
    /// </summary>
    public static RaidInstanceMessageType MessageTypeFor(uint timeLeftSeconds) => timeLeftSeconds switch
    {
        > 3600 => RaidInstanceMessageType.Welcome,
        > 900 => RaidInstanceMessageType.WarningHours,
        > 300 => RaidInstanceMessageType.WarningMinutes,
        _ => RaidInstanceMessageType.WarningMinutesSoon,
    };

    /// <summary>SMSG_RAID_GROUP_ONLY: u32 homebind timer (ms; 0 hides the reminder), u32 error.</summary>
    public static byte[] BuildRaidGroupOnly(uint timerMs, RaidGroupError error)
    {
        var packet = new PacketWriter(8);
        packet.WriteUInt32(timerMs);
        packet.WriteUInt32((uint)error);
        return packet.ToArray();
    }

    /// <summary>SMSG_INSTANCE_SAVE_CREATED: u32 0 (every emulator sends 0).</summary>
    public static byte[] BuildInstanceSaveCreated() => U32(0);

    /// <summary>SMSG_UPDATE_INSTANCE_OWNERSHIP (0x032B): Bool32 player_is_saved_to_a_raid (wow_messages raid/smsg_update_instance_ownership.wowm).</summary>
    public static byte[] BuildUpdateInstanceOwnership(bool savedToARaid) => U32(savedToARaid ? 1u : 0u);

    /// <summary>SMSG_UPDATE_LAST_INSTANCE (0x0320): u32 map (wow_messages raid/smsg_update_last_instance.wowm).</summary>
    public static byte[] BuildUpdateLastInstance(uint mapId) => U32(mapId);

    private static byte[] U32(uint value)
    {
        var packet = new PacketWriter(4);
        packet.WriteUInt32(value);
        return packet.ToArray();
    }
}
