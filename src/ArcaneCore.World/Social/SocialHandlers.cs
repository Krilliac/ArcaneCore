using ArcaneCore.Game;
using ArcaneCore.Game.Entities;
using ArcaneCore.Game.Social;
using ArcaneCore.Protocol;
using ArcaneCore.World.Characters;
using ArcaneCore.World.Handlers;
using ArcaneCore.World.Net;
using ArcaneCore.World.Packets;
using Microsoft.Extensions.DependencyInjection;

namespace ArcaneCore.World.Social;

/// <summary>Friend and ignore lists (vmangos MiscHandler.cpp HandleFriendListOpcode … HandleDelIgnoreOpcode, HandleChatIgnoredOpcode).</summary>
public sealed class SocialHandlers : IOpcodeHandlerGroup
{
    public void Register(OpcodeTable table)
    {
        table.OnWorld(WorldOpcode.CmsgFriendList, HandleFriendList);
        table.OnWorld(WorldOpcode.CmsgAddFriend, HandleAddFriend);
        table.OnWorld(WorldOpcode.CmsgDelFriend, HandleDelFriend);
        table.OnWorld(WorldOpcode.CmsgAddIgnore, HandleAddIgnore);
        table.OnWorld(WorldOpcode.CmsgDelIgnore, HandleDelIgnore);
        table.OnWorld(WorldOpcode.CmsgChatIgnored, HandleChatIgnored);
    }

    internal static SocialContext Social(WorldSession session) => session.Services.GetRequiredService<SocialFeature>().Context;

    /// <summary>CMSG_FRIEND_LIST (empty): the friend and ignore lists (vmangos HandleFriendListOpcode → SendSocialList).</summary>
    private static void HandleFriendList(WorldSession session, Player player, byte[] payload)
    {
        SocialContext social = Social(session);
        if (social.Friends.IsLoaded(player))
        {
            social.Friends.SendFriendList(player);
            social.Friends.SendIgnoreList(player);
        }
    }

    /// <summary>CMSG_ADD_FRIEND: CString name (vmangos HandleAddFriendOpcode).</summary>
    private static void HandleAddFriend(WorldSession session, Player player, byte[] payload)
    {
        var reader = new PacketReader(payload);
        Social(session).Friends.AddFriend(player, CharacterNames.Normalize(reader.ReadCString()));
    }

    /// <summary>CMSG_DEL_FRIEND: u64 guid (vmangos HandleDelFriendOpcode).</summary>
    private static void HandleDelFriend(WorldSession session, Player player, byte[] payload)
    {
        var reader = new PacketReader(payload);
        Social(session).Friends.RemoveFriend(player, new ObjectGuid(reader.ReadUInt64()));
    }

    /// <summary>CMSG_ADD_IGNORE: CString name (vmangos HandleAddIgnoreOpcode).</summary>
    private static void HandleAddIgnore(WorldSession session, Player player, byte[] payload)
    {
        var reader = new PacketReader(payload);
        Social(session).Friends.AddIgnore(player, CharacterNames.Normalize(reader.ReadCString()));
    }

    /// <summary>CMSG_DEL_IGNORE: u64 guid (vmangos HandleDelIgnoreOpcode).</summary>
    private static void HandleDelIgnore(WorldSession session, Player player, byte[] payload)
    {
        var reader = new PacketReader(payload);
        Social(session).Friends.RemoveIgnore(player, new ObjectGuid(reader.ReadUInt64()));
    }

    /// <summary>
    /// CMSG_CHAT_IGNORED: u64 guid of a player whose message the client dropped because it is
    /// ignored; that player gets CHAT_MSG_IGNORED with the ignorer's name (vmangos
    /// HandleChatIgnoredOpcode). The client filters ignored players' chat itself.
    /// </summary>
    private static void HandleChatIgnored(WorldSession session, Player player, byte[] payload)
    {
        var reader = new PacketReader(payload);
        if (session.World.FindOnlinePlayer(new ObjectGuid(reader.ReadUInt64())) is { } other)
        {
            other.Session.Send(WorldOpcode.SmsgMessagechat,
                ChatPackets.BuildMessage(ChatType.Ignored, Language.Universal, player.Guid, player.Name, ChatTag.None));
        }
    }
}
