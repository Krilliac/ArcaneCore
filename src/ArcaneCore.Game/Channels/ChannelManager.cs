using ArcaneCore.Game.Entities;
using ArcaneCore.Game.Social;
using ArcaneCore.Protocol;

namespace ArcaneCore.Game.Channels;

/// <summary>
/// The realm's chat channels: one set per faction, or a single shared set with
/// AllowTwoSide.Interaction.Channel (vmangos ChannelMgr.cpp channelMgr(team)). Request routing
/// follows vmangos ChannelHandler.cpp. World thread.
/// </summary>
public sealed class ChannelManager(SocialContext context)
{
    private readonly Dictionary<Team, Dictionary<string, Channel>> _byTeam = [];
    private readonly Dictionary<ObjectGuid, HashSet<Channel>> _joined = [];

    /// <summary>The channel called <paramref name="name"/> for <paramref name="team"/>, if it exists (case-insensitive).</summary>
    public Channel? Find(Team team, string name) => Channels(team).GetValueOrDefault(name.ToLowerInvariant());

    /// <summary>The channels <paramref name="player"/> is in.</summary>
    public IReadOnlyCollection<Channel> JoinedBy(Player player) => _joined.TryGetValue(player.Guid, out var set) ? set : [];

    /// <summary>
    /// CMSG_JOIN_CHANNEL (vmangos HandleJoinChannelOpcode): an empty name or one starting with
    /// a non-letter ASCII character gets INVALID_NAME; otherwise the channel is created on
    /// demand (ChannelMgr::GetJoinChannel) and joined.
    /// </summary>
    public void Join(Player player, string name, string password)
    {
        if (name.Length == 0 || (name[0] <= 127 && !char.IsAsciiLetter(name[0])))
        {
            player.Session.Send(WorldOpcode.SmsgChannelNotify, ChannelPackets.BuildNotify(ChatNotify.InvalidName, name));
            return;
        }

        Dictionary<string, Channel> channels = Channels(player.Team);
        string key = name.ToLowerInvariant();
        if (!channels.TryGetValue(key, out Channel? channel))
        {
            channel = new Channel(context, name);
            channels[key] = channel;
        }

        if (channel.Join(player, password))
        {
            Joined(player).Add(channel);
        }
        else
        {
            RemoveIfEmpty(player.Team, channel);
        }
    }

    /// <summary>CMSG_LEAVE_CHANNEL (vmangos HandleLeaveChannelOpcode): empty names are ignored; empty custom channels are deleted.</summary>
    public void Leave(Player player, string name)
    {
        if (name.Length == 0 || Get(player, name) is not { } channel)
        {
            return;
        }

        channel.Leave(player.Guid, send: true);
        Joined(player).Remove(channel);
        RemoveIfEmpty(player.Team, channel);
    }

    /// <summary>
    /// Leave every channel without telling the player (vmangos Player::CleanupChannels on
    /// logout: Channel::Leave(guid, false) then ChannelMgr::LeftChannel).
    /// </summary>
    public void LeaveAll(Player player)
    {
        if (!_joined.Remove(player.Guid, out HashSet<Channel>? channels))
        {
            return;
        }

        foreach (Channel channel in channels)
        {
            channel.Leave(player.Guid, send: false);
            RemoveIfEmpty(player.Team, channel);
        }
    }

    /// <summary>CMSG_CHANNEL_LIST.</summary>
    public void List(Player player, string name) => Get(player, name)?.List(player);

    /// <summary>CMSG_CHANNEL_PASSWORD.</summary>
    public void SetPassword(Player player, string name, string password) => Get(player, name)?.SetPassword(player, password);

    /// <summary>CMSG_CHANNEL_SET_OWNER.</summary>
    public void SetOwner(Player player, string name, string target) => Get(player, name)?.SetOwner(player, target);

    /// <summary>CMSG_CHANNEL_OWNER.</summary>
    public void SendOwner(Player player, string name) => Get(player, name)?.SendWhoOwner(player);

    /// <summary>CMSG_CHANNEL_MODERATOR / UNMODERATOR / MUTE / UNMUTE.</summary>
    public void SetMode(Player player, string name, string target, bool moderator, bool set) => Get(player, name)?.SetMode(player, target, moderator, set);

    /// <summary>CMSG_CHANNEL_INVITE.</summary>
    public void Invite(Player player, string name, string target) => Get(player, name)?.Invite(player, target);

    /// <summary>CMSG_CHANNEL_KICK / CMSG_CHANNEL_BAN.</summary>
    public void KickOrBan(Player player, string name, string target, bool ban)
    {
        if (Get(player, name) is { } channel && channel.KickOrBan(player, target, ban) is { } removed)
        {
            Joined(removed).Remove(channel);
        }
    }

    /// <summary>CMSG_CHANNEL_UNBAN.</summary>
    public void Unban(Player player, string name, string target) => Get(player, name)?.Unban(player, target);

    /// <summary>CMSG_CHANNEL_ANNOUNCEMENTS.</summary>
    public void ToggleAnnounce(Player player, string name) => Get(player, name)?.ToggleAnnounce(player);

    /// <summary>CMSG_CHANNEL_MODERATE.</summary>
    public void ToggleModerate(Player player, string name) => Get(player, name)?.ToggleModerate(player);

    /// <summary>CHAT_MSG_CHANNEL (vmangos HandleMessagechatOpcode → Channel::Say).</summary>
    public void Say(Player player, string name, string text, Language language) => Get(player, name)?.Say(player, text, language);

    /// <summary>vmangos ChannelMgr::GetChannel(name, player, pkt = true): NOT_MEMBER with the requested name when it does not exist.</summary>
    private Channel? Get(Player player, string name)
    {
        if (Find(player.Team, name) is { } channel)
        {
            return channel;
        }

        player.Session.Send(WorldOpcode.SmsgChannelNotify, ChannelPackets.BuildNotify(ChatNotify.NotMember, name));
        return null;
    }

    private Dictionary<string, Channel> Channels(Team team)
    {
        Team key = context.Options.AllowTwoSideChannel ? Team.Alliance : team;
        if (!_byTeam.TryGetValue(key, out var channels))
        {
            channels = new Dictionary<string, Channel>(StringComparer.Ordinal);
            _byTeam[key] = channels;
        }

        return channels;
    }

    private HashSet<Channel> Joined(Player player)
    {
        if (!_joined.TryGetValue(player.Guid, out var set))
        {
            set = [];
            _joined[player.Guid] = set;
        }

        return set;
    }

    /// <summary>vmangos ChannelMgr::LeftChannel: empty non-constant channels are deleted.</summary>
    private void RemoveIfEmpty(Team team, Channel channel)
    {
        if (channel.MemberCount != 0 || channel.IsConstant)
        {
            return;
        }

        Dictionary<string, Channel> channels = Channels(team);
        foreach ((string key, Channel value) in channels)
        {
            if (ReferenceEquals(value, channel))
            {
                channels.Remove(key);
                return;
            }
        }
    }
}
