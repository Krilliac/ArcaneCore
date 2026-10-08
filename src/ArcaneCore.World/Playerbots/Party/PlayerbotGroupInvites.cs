using ArcaneCore.Game.Entities;
using ArcaneCore.Game.Social;
using ArcaneCore.Kernel.Social;
using ArcaneCore.Protocol;

namespace ArcaneCore.World.Playerbots.Party;

/// <summary>
/// A bot's answer to SMSG_GROUP_INVITE (mangoszero AcceptInvitationAction.h: accept through the ordinary CMSG_GROUP_ACCEPT, or
/// decline with CMSG_GROUP_DECLINE, which tells the inviter). Who may invite is <see cref="PlayerbotPartyOptions.InvitePolicy"/>
/// plus the <see cref="PlayerbotPartyOptions.Allowlist"/>.
/// </summary>
internal static class PlayerbotGroupInvites
{
    /// <summary>
    /// Whether a bot accepts <paramref name="inviter"/>'s invitation: an allowlisted name always; otherwise
    /// <see cref="PlayerbotInvitePolicy.None"/> nobody, <see cref="PlayerbotInvitePolicy.GuildOrFriends"/> a guild mate or a player on the
    /// BOT's friend list (<paramref name="onBotsFriendList"/>; the inviter's own list does not count: anyone can befriend any bot with
    /// CMSG_ADD_FRIEND, so it shows no consent), <see cref="PlayerbotInvitePolicy.Anyone"/> everybody.
    /// </summary>
    internal static bool Allows(PlayerbotInvitePolicy policy, IReadOnlyCollection<string> allowlist, string inviter,
        bool sameGuild, bool onBotsFriendList)
    {
        ArgumentNullException.ThrowIfNull(allowlist);
        if (string.IsNullOrEmpty(inviter)) return false;
        if (allowlist.Any(name => string.Equals(name, inviter, StringComparison.OrdinalIgnoreCase))) return true;
        return policy switch
        {
            PlayerbotInvitePolicy.Anyone => true,
            PlayerbotInvitePolicy.GuildOrFriends => sameGuild || onBotsFriendList,
            _ => false,
        };
    }

    /// <summary>SMSG_GROUP_INVITE: the inviter's name (CString; GroupPackets.BuildName). Null for an empty body.</summary>
    internal static string? ReadInviter(byte[] payload)
    {
        if (payload.Length == 0) return null;
        var reader = new PacketReader(payload);
        string name = reader.ReadCString();
        return name.Length == 0 ? null : name;
    }

    /// <summary>The bot and <paramref name="inviter"/> are in the same guild.</summary>
    internal static bool SameGuild(SocialContext social, Player bot, Player inviter)
        => social.Guilds.GetGuildOf(bot) is { } guild && ReferenceEquals(social.Guilds.GetGuildOf(inviter), guild);

    /// <summary>
    /// The inviter is on the bot's own friend list. Only that list counts: whoever is on it was put there for the bot, while any player
    /// can put any bot on their own list with one CMSG_ADD_FRIEND, so that list grants nothing.
    /// </summary>
    internal static bool OnBotsFriendList(SocialContext social, Player bot, Player inviter)
        => social.Friends.Get(bot).Has(inviter.Guid.Low, SocialFlags.Friend);
}
