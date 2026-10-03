using ArcaneCore.Game.Channels;
using ArcaneCore.Game.Entities;
using ArcaneCore.Protocol;
using ArcaneCore.World.Characters;
using ArcaneCore.World.Handlers;
using ArcaneCore.World.Net;

namespace ArcaneCore.World.Social;

/// <summary>Chat channel requests (vmangos ChannelHandler.cpp; payloads per gtker cmsg_*channel*).</summary>
public sealed class ChannelHandlers : IOpcodeHandlerGroup
{
    public void Register(OpcodeTable table)
    {
        table.OnWorld(WorldOpcode.CmsgJoinChannel, HandleJoin);
        table.OnWorld(WorldOpcode.CmsgLeaveChannel, (s, p, d) => Channels(s).Leave(p, Text(d)));
        table.OnWorld(WorldOpcode.CmsgChannelList, (s, p, d) => Channels(s).List(p, Text(d)));
        table.OnWorld(WorldOpcode.CmsgChannelPassword, HandlePassword);
        table.OnWorld(WorldOpcode.CmsgChannelSetOwner, (s, p, d) => WithTarget(d, (c, t) => Channels(s).SetOwner(p, c, t)));
        table.OnWorld(WorldOpcode.CmsgChannelOwner, (s, p, d) => Channels(s).SendOwner(p, Text(d)));
        table.OnWorld(WorldOpcode.CmsgChannelModerator, (s, p, d) => WithTarget(d, (c, t) => Channels(s).SetMode(p, c, t, moderator: true, set: true)));
        table.OnWorld(WorldOpcode.CmsgChannelUnmoderator, (s, p, d) => WithTarget(d, (c, t) => Channels(s).SetMode(p, c, t, moderator: true, set: false)));
        table.OnWorld(WorldOpcode.CmsgChannelMute, (s, p, d) => WithTarget(d, (c, t) => Channels(s).SetMode(p, c, t, moderator: false, set: true)));
        table.OnWorld(WorldOpcode.CmsgChannelUnmute, (s, p, d) => WithTarget(d, (c, t) => Channels(s).SetMode(p, c, t, moderator: false, set: false)));
        table.OnWorld(WorldOpcode.CmsgChannelInvite, (s, p, d) => WithTarget(d, (c, t) => Channels(s).Invite(p, c, t)));
        table.OnWorld(WorldOpcode.CmsgChannelKick, (s, p, d) => WithTarget(d, (c, t) => Channels(s).KickOrBan(p, c, t, ban: false)));
        table.OnWorld(WorldOpcode.CmsgChannelBan, (s, p, d) => WithTarget(d, (c, t) => Channels(s).KickOrBan(p, c, t, ban: true)));
        table.OnWorld(WorldOpcode.CmsgChannelUnban, (s, p, d) => WithTarget(d, (c, t) => Channels(s).Unban(p, c, t)));
        table.OnWorld(WorldOpcode.CmsgChannelAnnouncements, (s, p, d) => Channels(s).ToggleAnnounce(p, Text(d)));
        table.OnWorld(WorldOpcode.CmsgChannelModerate, (s, p, d) => Channels(s).ToggleModerate(p, Text(d)));
    }

    private static ChannelManager Channels(WorldSession session) => SocialHandlers.Social(session).Channels;

    private static string Text(byte[] payload) => new PacketReader(payload).ReadCString();

    /// <summary>A channel name followed by a player name (normalized like vmangos normalizePlayerName).</summary>
    private static void WithTarget(byte[] payload, Action<string, string> action)
    {
        var reader = new PacketReader(payload);
        string channel = reader.ReadCString();
        string target = CharacterNames.Normalize(reader.ReadCString());
        action(channel, target);
    }

    /// <summary>CMSG_JOIN_CHANNEL: CString name, CString password.</summary>
    private static void HandleJoin(WorldSession session, Player player, byte[] payload)
    {
        var reader = new PacketReader(payload);
        string name = reader.ReadCString();
        string password = reader.ReadCString();
        Channels(session).Join(player, name, password);
    }

    /// <summary>CMSG_CHANNEL_PASSWORD: CString name, CString password.</summary>
    private static void HandlePassword(WorldSession session, Player player, byte[] payload)
    {
        var reader = new PacketReader(payload);
        string name = reader.ReadCString();
        string password = reader.ReadCString();
        Channels(session).SetPassword(player, name, password);
    }
}
