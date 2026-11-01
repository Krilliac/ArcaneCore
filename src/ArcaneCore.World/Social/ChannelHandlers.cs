using ArcaneCore.Game;
using ArcaneCore.Game.Channels;
using ArcaneCore.Game.Entities;
using ArcaneCore.Protocol;
using ArcaneCore.World.Characters;
using ArcaneCore.World.Chat;
using ArcaneCore.World.Cluster.Social;
using ArcaneCore.World.Handlers;
using ArcaneCore.World.Net;
using Microsoft.Extensions.DependencyInjection;

namespace ArcaneCore.World.Social;

/// <summary>Chat channel requests (vmangos ChannelHandler.cpp; payloads per gtker cmsg_*channel*).</summary>
public sealed class ChannelHandlers : IOpcodeHandlerGroup
{
    public void Register(OpcodeTable table)
    {
        table.OnWorld(WorldOpcode.CmsgJoinChannel, HandleJoin);
        table.OnWorld(WorldOpcode.CmsgLeaveChannel, HandleLeave);
        table.OnWorld(WorldOpcode.CmsgChannelList, (s, p, d) => Channels(s).List(p, Text(d)));
        table.OnWorld(WorldOpcode.CmsgChannelPassword, HandlePassword);
        table.OnWorld(WorldOpcode.CmsgChannelSetOwner, HandleSetOwner);
        table.OnWorld(WorldOpcode.CmsgChannelOwner, (s, p, d) => Channels(s).SendOwner(p, Text(d)));
        table.OnWorld(WorldOpcode.CmsgChannelModerator, (s, p, d) => HandleMode(s, p, d, moderator: true, set: true));
        table.OnWorld(WorldOpcode.CmsgChannelUnmoderator, (s, p, d) => HandleMode(s, p, d, moderator: true, set: false));
        table.OnWorld(WorldOpcode.CmsgChannelMute, (s, p, d) => HandleMode(s, p, d, moderator: false, set: true));
        table.OnWorld(WorldOpcode.CmsgChannelUnmute, (s, p, d) => HandleMode(s, p, d, moderator: false, set: false));
        table.OnWorld(WorldOpcode.CmsgChannelInvite, (s, p, d) => WithTarget(d, (c, t) => Channels(s).Invite(p, c, t)));
        table.OnWorld(WorldOpcode.CmsgChannelKick, (s, p, d) => HandleKickOrBan(s, p, d, ban: false));
        table.OnWorld(WorldOpcode.CmsgChannelBan, (s, p, d) => HandleKickOrBan(s, p, d, ban: true));
        table.OnWorld(WorldOpcode.CmsgChannelUnban, HandleUnban);
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
        if (ClusterOf(session) is { } cluster
            && Channels(session).Find(player.Team, name) is { } channel && channel.IsOn(player.Guid))
        {
            cluster.JoinChannel(SenderOf(player), channel.Name);
            if (!channel.IsConstant && channel.Owner == player.Guid)
            {
                // The first joiner of a custom channel owns it locally; the holder grants the claim only while no other worker's member owns it already.
                cluster.ModerateChannel(channel.Name, ChannelModerationAction.ClaimOwner, player.Guid.Value, player.Guid.Value);
            }
        }
    }

    private static ClusterChatRouting? ClusterOf(WorldSession session) => session.Services.GetService<ChatFeature>()?.Cluster;

    private static ClusterChatSender SenderOf(Player player) => new(player.Guid.Value, player.Name, player.AccountId);

    /// <summary>
    /// After a moderation change that can move ownership (a leave, kick or ban of the owner hands it on locally), the new local owner claims it at the holder, which keeps
    /// one owner for the whole realm and refuses the claim while another member holds it.
    /// </summary>
    private static void ClaimLocalOwner(ClusterChatRouting cluster, Channel channel)
    {
        if (!channel.IsConstant && !channel.Owner.IsEmpty)
        {
            cluster.ModerateChannel(channel.Name, ChannelModerationAction.ClaimOwner, channel.Owner.Value, channel.Owner.Value);
        }
    }

    /// <summary>CMSG_CHANNEL_SET_OWNER: a new local owner is mirrored at the holder, which checks the actor's right there.</summary>
    private static void HandleSetOwner(WorldSession session, Player player, byte[] payload) => WithTarget(payload, (name, target) =>
    {
        Channel? channel = Channels(session).Find(player.Team, name);
        ObjectGuid before = channel?.Owner ?? ObjectGuid.Empty;
        Channels(session).SetOwner(player, name, target);
        if (channel is not null && channel.Owner != before && !channel.Owner.IsEmpty && ClusterOf(session) is { } cluster)
        {
            cluster.ModerateChannel(channel.Name, ChannelModerationAction.SetOwner, player.Guid.Value, channel.Owner.Value);
        }
    });

    /// <summary>CMSG_CHANNEL_MODERATOR / UNMODERATOR / MUTE / UNMUTE: a change that took effect locally is mirrored at the holder.</summary>
    private static void HandleMode(WorldSession session, Player player, byte[] payload, bool moderator, bool set) => WithTarget(payload, (name, targetName) =>
    {
        Channels(session).SetMode(player, name, targetName, moderator, set);
        if (ClusterOf(session) is not { } cluster
            || Channels(session).Find(player.Team, name) is not { } channel
            || session.World.FindOnlinePlayer(targetName) is not { } target
            || !channel.IsOn(target.Guid))
        {
            return;
        }

        ChannelMemberFlags flag = moderator ? ChannelMemberFlags.Moderator : ChannelMemberFlags.Muted;
        if (((channel.FlagsOf(target.Guid) & flag) != 0) != set)
        {
            return; // refused locally: nothing to mirror
        }

        ChannelModerationAction action = (moderator, set) switch
        {
            (true, true) => ChannelModerationAction.AddModerator,
            (true, false) => ChannelModerationAction.RemoveModerator,
            (false, true) => ChannelModerationAction.Mute,
            _ => ChannelModerationAction.Unmute,
        };
        cluster.ModerateChannel(channel.Name, action, player.Guid.Value, target.Guid.Value);
    });

    /// <summary>
    /// CMSG_CHANNEL_KICK / CMSG_CHANNEL_BAN: a target removed from the local channel is kicked or banned at the holder when the actor holds the right there (a ban also makes it refuse the
    /// target's join from any worker and its lines), and ownership handed on by the removal is claimed.
    /// </summary>
    private static void HandleKickOrBan(WorldSession session, Player player, byte[] payload, bool ban) => WithTarget(payload, (name, targetName) =>
    {
        Channel? channel = Channels(session).Find(player.Team, name);
        Player? target = channel is null ? null : session.World.FindOnlinePlayer(targetName);
        bool wasMember = channel is not null && target is not null && channel.IsOn(target.Guid);
        Channels(session).KickOrBan(player, name, targetName, ban);
        if (!wasMember || channel is null || target is null || channel.IsOn(target.Guid) || ClusterOf(session) is not { } cluster)
        {
            return;
        }

        // The holder decides: a local owner or moderator that holds no right there removes the target from this worker's channel object only.
        cluster.ModerateChannel(channel.Name, ban ? ChannelModerationAction.Ban : ChannelModerationAction.Kick, player.Guid.Value, target.Guid.Value);

        ClaimLocalOwner(cluster, channel);
    });

    /// <summary>CMSG_CHANNEL_UNBAN: a ban lifted locally is lifted at the holder.</summary>
    private static void HandleUnban(WorldSession session, Player player, byte[] payload) => WithTarget(payload, (name, targetName) =>
    {
        Channel? channel = Channels(session).Find(player.Team, name);
        Player? target = channel is null ? null : session.World.FindOnlinePlayer(targetName);
        bool wasBanned = channel is not null && target is not null && channel.IsBanned(target.Guid);
        Channels(session).Unban(player, name, targetName);
        if (wasBanned && channel is not null && target is not null && !channel.IsBanned(target.Guid) && ClusterOf(session) is { } cluster)
        {
            cluster.ModerateChannel(channel.Name, ChannelModerationAction.Unban, player.Guid.Value, target.Guid.Value);
        }
    });

    /// <summary>
    /// CMSG_LEAVE_CHANNEL: CString name. With cross-worker chat the member is also unsubscribed at the router once the local channel no longer lists it
    /// (a channel that does not exist or never listed the player changes nothing).
    /// </summary>
    private static void HandleLeave(WorldSession session, Player player, byte[] payload)
    {
        string name = Text(payload);
        Channel? channel = name.Length == 0 ? null : Channels(session).Find(player.Team, name);
        bool wasMember = channel is not null && channel.IsOn(player.Guid);
        Channels(session).Leave(player, name);
        if (wasMember && channel is not null && !channel.IsOn(player.Guid) && ClusterOf(session) is { } cluster)
        {
            cluster.LeaveChannel(SenderOf(player), channel.Name);
            ClaimLocalOwner(cluster, channel);
        }
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
