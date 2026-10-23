using ArcaneCore.Game;
using ArcaneCore.Game.Entities;
using ArcaneCore.Game.Social;
using ArcaneCore.Kernel.Accounts;
using ArcaneCore.Kernel.Characters;
using ArcaneCore.Kernel.Social;
using ArcaneCore.Protocol;
using ArcaneCore.World.Playerbots.BotSocial;

namespace ArcaneCore.World.Playerbots.BotSocial;

/// <summary>What a bot does to one entry of its friend or ignore list.</summary>
public enum PlayerbotListChange { None, Ignore, Unignore, Unfriend, Friend }

/// <summary>
/// Real friend and ignore lists (docs/areas/playbots-social.md, Lists): a bot puts a character it has come to like on its friend list
/// (CMSG_ADD_FRIEND), takes one it stopped liking off (CMSG_DEL_FRIEND), ignores one it detests, or a rival who earned a chat strike
/// (CMSG_ADD_IGNORE), and lifts the ignore when the feeling has faded (CMSG_DEL_IGNORE). The packets go through the bot's own session, so
/// the server's rules (list limits, same team, no self) apply as to a player. At most one change per bot per
/// <see cref="PlayerbotSocialListsOptions.ChangeSpacingSeconds"/>; a game master is never ignored. Every affinity read goes through
/// <c>EffectiveAffinity</c> and <c>IsRival</c>. World thread only.
/// </summary>
internal sealed class PlayerbotSocialLists
{
    /// <summary>The bots looked over in one pass (a cursor rotates through the rest).</summary>
    internal const int BotsPerPass = 8;

    private readonly Dictionary<Guid, long> _lastChange = [];
    private int _cursor;

    /// <summary>Changes made so far (tests, status).</summary>
    internal int Changes { get; private set; }

    /// <summary>
    /// The one change a bot makes for a relation, by priority: ignore, un-ignore, un-friend, friend. Ignoring needs affinity at or below
    /// <see cref="PlayerbotSocialListsOptions.IgnoreAffinity"/>, or a rival with a chat strike, and never a game master; an ignore is lifted
    /// above <see cref="PlayerbotSocialListsOptions.UnignoreAffinity"/> unless it is still a rival with a strike; a friend goes below
    /// <see cref="PlayerbotSocialListsOptions.UnfriendAffinity"/>; a friend is made at <see cref="PlayerbotSocialListsOptions.FriendAffinity"/>
    /// with <see cref="PlayerbotSocialListsOptions.FriendMinFamiliarity"/> encounters, not a rival, not ignored.
    /// </summary>
    internal static PlayerbotListChange Decide(PlayerbotSocialListsOptions options, int affinity, int familiarity, bool rival, bool chatStrike,
        bool gameMaster, bool isFriend, bool isIgnored)
    {
        ArgumentNullException.ThrowIfNull(options);
        bool detested = affinity <= options.IgnoreAffinity || (rival && chatStrike);
        if (!isIgnored && detested && !gameMaster) return PlayerbotListChange.Ignore;
        if (isIgnored && !detested && affinity > options.UnignoreAffinity) return PlayerbotListChange.Unignore;
        if (isFriend && affinity < options.UnfriendAffinity) return PlayerbotListChange.Unfriend;
        if (!isFriend && !isIgnored && !rival && affinity >= options.FriendAffinity && familiarity >= options.FriendMinFamiliarity) return PlayerbotListChange.Friend;
        return PlayerbotListChange.None;
    }

    /// <summary>Look over the next bots whose spacing has passed and make at most one change for each.</summary>
    internal void Run(PlayerbotSocialPass pass)
    {
        PlayerbotSocialListsOptions options = pass.Options.Lists;
        if (!options.Enabled || pass.Recorder is not { Enabled: true } recorder || pass.Bots.Count == 0) return;
        long spacingMs = options.ChangeSpacingSeconds * 1000L;
        int count = pass.Bots.Count;
        int looked = 0;
        for (int i = 0; i < count && looked < BotsPerPass; i++)
        {
            PlayerbotSocialBot bot = pass.Bots[(_cursor + i) % count];
            if (_lastChange.TryGetValue(bot.Id, out long last) && pass.NowMs - last < spacingMs) continue;
            looked++;
            if (ChangeFor(pass, recorder, options, bot)) _lastChange[bot.Id] = pass.NowMs;
        }

        _cursor = (_cursor + 1) % count;
        if (_lastChange.Count > 4 * count + 64)
            foreach (Guid id in _lastChange.Keys.ToArray())
                if (!pass.BotIds.Contains(id)) _lastChange.Remove(id);
    }

    private bool ChangeFor(PlayerbotSocialPass pass, PlayerbotSocialRecorder recorder, PlayerbotSocialListsOptions options, PlayerbotSocialBot bot)
    {
        IReadOnlyList<PlayerbotRelation> relations = recorder.Memory.Relations(bot.Id);
        if (relations.Count == 0) return false;
        PlayerSocial list = pass.Social.Friends.Get(bot.Player);
        PlayerbotListChange best = PlayerbotListChange.None;
        PlayerbotRelation? chosen = null;
        Player? target = null;
        foreach (PlayerbotRelation relation in relations)
        {
            var guid = new ObjectGuid(relation.OtherGuid);
            Player? online = pass.World.FindOnlinePlayer(guid);
            bool gm = online is not null && (online.IsGameMaster || online.Session.Security > AccountSecurity.Player);
            bool strike = online is not null && pass.Chat?.Safety.Player(online.Name) is { Strikes: > 0 };
            uint low = guid.Low;
            PlayerbotListChange change = Decide(options, recorder.Memory.EffectiveAffinity(relation), relation.Familiarity, recorder.Memory.IsRival(relation), strike, gm,
                list.Has(low, SocialFlags.Friend), list.Has(low, SocialFlags.Ignored));
            // Adding needs the name, so the other must be online; lifting an entry needs only the guid.
            if (change is PlayerbotListChange.Ignore or PlayerbotListChange.Friend && online is null) continue;
            if (change != PlayerbotListChange.None && (best == PlayerbotListChange.None || change < best || (change == best && relation.OtherGuid < chosen!.OtherGuid)))
            {
                best = change;
                chosen = relation;
                target = online;
            }
        }

        if (chosen is null) return false;
        var other = new ObjectGuid(chosen.OtherGuid);
        (WorldOpcode opcode, byte[] payload) = best switch
        {
            PlayerbotListChange.Ignore => (WorldOpcode.CmsgAddIgnore, PlayerbotSocialPackets.OneString(target!.Name)),
            PlayerbotListChange.Unignore => (WorldOpcode.CmsgDelIgnore, PlayerbotSocialPackets.GuidOnly(other)),
            PlayerbotListChange.Unfriend => (WorldOpcode.CmsgDelFriend, PlayerbotSocialPackets.GuidOnly(other)),
            _ => (WorldOpcode.CmsgAddFriend, PlayerbotSocialPackets.OneString(target!.Name)),
        };
        PlayerbotSocialPackets.Send(bot.Session, opcode, payload);
        Changes++;
        return true;
    }
}
