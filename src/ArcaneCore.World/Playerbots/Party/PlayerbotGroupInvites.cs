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
    /// <see cref="PlayerbotInvitePolicy.None"/> nobody, <see cref="PlayerbotInvitePolicy.GuildOrFriends"/> a guild mate or a friend
    /// (either side's friend list), <see cref="PlayerbotInvitePolicy.Anyone"/> everybody.
    /// </summary>
    internal static bool Allows(PlayerbotInvitePolicy policy, IReadOnlyCollection<string> allowlist, string inviter,
        bool sameGuild, bool friends)
    {
        ArgumentNullException.ThrowIfNull(allowlist);
        if (string.IsNullOrEmpty(inviter)) return false;
        if (allowlist.Any(name => string.Equals(name, inviter, StringComparison.OrdinalIgnoreCase))) return true;
        return policy switch
        {
            PlayerbotInvitePolicy.Anyone => true,
            PlayerbotInvitePolicy.GuildOrFriends => sameGuild || friends,
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

    /// <summary>One of the two has the other on its friend list.</summary>
    internal static bool Friends(SocialContext social, Player bot, Player inviter)
        => social.Friends.Get(bot).Has(inviter.Guid.Low, SocialFlags.Friend)
            || social.Friends.Get(inviter).Has(bot.Guid.Low, SocialFlags.Friend);
}
