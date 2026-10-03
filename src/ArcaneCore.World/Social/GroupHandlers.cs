using ArcaneCore.Game;
using ArcaneCore.Game.Entities;
using ArcaneCore.Game.Groups;
using ArcaneCore.Protocol;
using ArcaneCore.World.Characters;
using ArcaneCore.World.Handlers;
using ArcaneCore.World.Net;

namespace ArcaneCore.World.Social;

/// <summary>Party and raid requests (vmangos GroupHandler.cpp; payloads per gtker cmsg_group_* / msg_*).</summary>
public sealed class GroupHandlers : IOpcodeHandlerGroup
{
    public void Register(OpcodeTable table)
    {
        table.OnWorld(WorldOpcode.CmsgGroupInvite, (s, p, d) => Groups(s).Invite(p, Name(d)));
        table.OnWorld(WorldOpcode.CmsgGroupAccept, (s, p, _) => Groups(s).Accept(p));
        table.OnWorld(WorldOpcode.CmsgGroupDecline, (s, p, _) => Groups(s).Decline(p));
        table.OnWorld(WorldOpcode.CmsgGroupUninvite, (s, p, d) => Groups(s).UninviteByName(p, Name(d)));
        table.OnWorld(WorldOpcode.CmsgGroupUninviteGuid, (s, p, d) => Groups(s).UninviteByGuid(p, Guid(d)));
        table.OnWorld(WorldOpcode.CmsgGroupSetLeader, (s, p, d) => Groups(s).SetLeader(p, Guid(d)));
        table.OnWorld(WorldOpcode.CmsgGroupDisband, (s, p, _) => Groups(s).Leave(p));
        table.OnWorld(WorldOpcode.CmsgLootMethod, HandleLootMethod);
        table.OnWorld(WorldOpcode.CmsgGroupRaidConvert, (s, p, _) => Groups(s).ConvertToRaid(p));
        table.OnWorld(WorldOpcode.CmsgGroupChangeSubGroup, HandleChangeSubGroup);
        table.OnWorld(WorldOpcode.CmsgGroupSwapSubGroup, HandleSwapSubGroup);
        table.OnWorld(WorldOpcode.CmsgGroupAssistantLeader, HandleAssistantLeader);
        table.OnWorld(WorldOpcode.MsgRaidReadyCheck, (s, p, d) => Groups(s).ReadyCheck(p, d.Length == 0 ? null : d[0]));
        table.OnWorld(WorldOpcode.MsgRaidTargetUpdate, HandleTargetUpdate);
        table.OnWorld(WorldOpcode.MsgMinimapPing, HandleMinimapPing);
        table.OnWorld(WorldOpcode.MsgRandomRoll, HandleRandomRoll);
        table.OnWorld(WorldOpcode.CmsgRequestPartyMemberStats, (s, p, d) => Groups(s).RequestMemberStats(p, Guid(d)));
    }

    private static GroupManager Groups(WorldSession session) => SocialHandlers.Social(session).Groups;

    private static string Name(byte[] payload)
    {
        var reader = new PacketReader(payload);
        return CharacterNames.Normalize(reader.ReadCString());
    }

    private static ObjectGuid Guid(byte[] payload)
    {
        var reader = new PacketReader(payload);
        return new ObjectGuid(reader.ReadUInt64());
    }

    /// <summary>CMSG_LOOT_METHOD: u32 method, u64 master looter, u32 threshold.</summary>
    private static void HandleLootMethod(WorldSession session, Player player, byte[] payload)
    {
        var reader = new PacketReader(payload);
        uint method = reader.ReadUInt32();
        var master = new ObjectGuid(reader.ReadUInt64());
        uint threshold = reader.ReadUInt32();
        Groups(session).SetLootMethod(player, method, master, threshold);
    }

    /// <summary>CMSG_GROUP_CHANGE_SUB_GROUP: CString name, u8 subgroup.</summary>
    private static void HandleChangeSubGroup(WorldSession session, Player player, byte[] payload)
    {
        var reader = new PacketReader(payload);
        string name = CharacterNames.Normalize(reader.ReadCString());
        byte subGroup = reader.ReadByte();
        Groups(session).ChangeSubGroup(player, name, subGroup);
    }

    /// <summary>CMSG_GROUP_SWAP_SUB_GROUP: CString name, CString other name.</summary>
    private static void HandleSwapSubGroup(WorldSession session, Player player, byte[] payload)
    {
        var reader = new PacketReader(payload);
        string name = CharacterNames.Normalize(reader.ReadCString());
        string other = CharacterNames.Normalize(reader.ReadCString());
        Groups(session).SwapSubGroup(player, name, other);
    }

    /// <summary>CMSG_GROUP_ASSISTANT_LEADER: u64 guid, u8 set.</summary>
    private static void HandleAssistantLeader(WorldSession session, Player player, byte[] payload)
    {
        var reader = new PacketReader(payload);
        var guid = new ObjectGuid(reader.ReadUInt64());
        bool set = reader.ReadByte() != 0;
        Groups(session).SetAssistant(player, guid, set);
    }

    /// <summary>MSG_RAID_TARGET_UPDATE: u8 icon; 0xFF asks for the list, otherwise a u64 target follows.</summary>
    private static void HandleTargetUpdate(WorldSession session, Player player, byte[] payload)
    {
        var reader = new PacketReader(payload);
        byte icon = reader.ReadByte();
        ObjectGuid target = icon == 0xFF ? ObjectGuid.Empty : new ObjectGuid(reader.ReadUInt64());
        Groups(session).TargetIcon(player, icon, target);
    }

    /// <summary>MSG_MINIMAP_PING: f32 x, f32 y.</summary>
    private static void HandleMinimapPing(WorldSession session, Player player, byte[] payload)
    {
        var reader = new PacketReader(payload);
        float x = reader.ReadSingle();
        float y = reader.ReadSingle();
        Groups(session).MinimapPing(player, x, y);
    }

    /// <summary>MSG_RANDOM_ROLL: u32 minimum, u32 maximum.</summary>
    private static void HandleRandomRoll(WorldSession session, Player player, byte[] payload)
    {
        var reader = new PacketReader(payload);
        uint min = reader.ReadUInt32();
        uint max = reader.ReadUInt32();
        Groups(session).RandomRoll(player, min, max);
    }
}
