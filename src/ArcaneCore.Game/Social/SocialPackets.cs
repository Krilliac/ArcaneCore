using ArcaneCore.Protocol;

namespace ArcaneCore.Game.Social;

/// <summary>Friend and ignore list packets (vmangos Server/Packets/Social.cpp, gtker social/*.wowm).</summary>
public static class SocialPackets
{
    /// <summary>
    /// SMSG_FRIEND_LIST: u8 count, then per friend u64 guid, u8 status and — when online —
    /// u32 area, u32 level, u32 class (vmangos FriendList::AppendBodyTo; gtker smsg_friend_list).
    /// </summary>
    public static byte[] BuildFriendList(IReadOnlyList<(ObjectGuid Guid, FriendStatus Status, uint Area, uint Level, uint Class)> friends)
    {
        var writer = new PacketWriter(1 + (friends.Count * 21));
        writer.WriteByte((byte)friends.Count);
        foreach ((ObjectGuid guid, FriendStatus status, uint area, uint level, uint cls) in friends)
        {
            writer.WriteUInt64(guid.Value);
            writer.WriteByte((byte)status);
            if (status != FriendStatus.Offline)
            {
                writer.WriteUInt32(area);
                writer.WriteUInt32(level);
                writer.WriteUInt32(cls);
            }
        }

        return writer.ToArray();
    }

    /// <summary>SMSG_IGNORE_LIST: u8 count + u64 guids (vmangos IgnoreList; gtker smsg_ignore_list).</summary>
    public static byte[] BuildIgnoreList(IReadOnlyList<ObjectGuid> ignored)
    {
        var writer = new PacketWriter(1 + (ignored.Count * 8));
        writer.WriteByte((byte)ignored.Count);
        foreach (ObjectGuid guid in ignored)
        {
            writer.WriteUInt64(guid.Value);
        }

        return writer.ToArray();
    }

    /// <summary>
    /// SMSG_FRIEND_STATUS: u8 result, u64 guid; FRIEND_ADDED_ONLINE and FRIEND_ONLINE add u8
    /// status, u32 area, u32 level, u32 class (vmangos FriendStatus::AppendBodyTo for builds
    /// after 1.8.4; cmangos-classic SocialMgr::SendFriendStatus). gtker smsg_friend_status lists
    /// only result + guid; the servers win (docs/areas/social.md).
    /// </summary>
    public static byte[] BuildFriendStatus(FriendsResult result, ObjectGuid friend, FriendStatus status, uint area, uint level, uint cls)
    {
        var writer = new PacketWriter(22);
        writer.WriteByte((byte)result);
        writer.WriteUInt64(friend.Value);
        if (result is FriendsResult.AddedOnline or FriendsResult.Online)
        {
            writer.WriteByte((byte)status);
            writer.WriteUInt32(area);
            writer.WriteUInt32(level);
            writer.WriteUInt32(cls);
        }

        return writer.ToArray();
    }
}
